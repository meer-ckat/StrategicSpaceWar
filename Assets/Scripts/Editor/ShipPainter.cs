#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Ship > Ship Painter. 칸을 찍어서 함선을 그리고 JSON으로 뽑는다.
///
/// **런타임을 하나도 안 건드린다.** 여기가 하는 일은 <c>placements</c> 배열을 만드는 것뿐이고,
/// 배를 짓는 것은 여전히 <see cref="ShipBuilder.Spawn"/>이다. 그래서 이 창이 틀려도
/// 시뮬레이션은 안 틀린다 - 틀린 JSON이 나올 뿐이고, 그건 로드할 때 걸린다.
///
/// 좌표 규약은 격자와 같다: col은 오른쪽(뱃머리), row는 **아래로** 증가한다. 화면에 그릴 때
/// row가 곧 y라 손으로 보는 것과 맵이 같은 방향이다.
///
/// v1은 1x1 판만 찍는다. 경사판(rot/size/offset)은 JSON에서 손으로 넣어야 한다 - 마우스로
/// 각도를 고르게 만들면 창이 두 배가 되는데, 경사는 배 한 척에 스무 장 남짓이라 값이 안 맞는다.
/// </summary>
public sealed class ShipPainter : EditorWindow
{
    [System.Serializable]
    private class NameOnly { public string defName; }

    /// <summary>
    /// 한 칸에 놓인 것. **def 이름만으로는 부족하다** - 배치는 회전과 콜라이더 덮어쓰기도
    /// 들고 있고, 그것을 안 들면 불러왔다 저장하는 순간 사라진다.
    ///
    /// 실제로 사라지던 양이 적지 않다: mirror 776장, lance 56장, destroyer 30장이 size를
    /// 지정하고 있고 offset도 그만큼이다. 증상이 "배는 멀쩡히 지어지는데 모양만 이상하다"라
    /// 아무 검사에도 안 걸린다.
    ///
    /// size가 0이면 def 값을 쓴다는 규약은 <see cref="Placement.size"/>와 같다.
    /// </summary>
    private readonly struct Placed
    {
        public readonly string def;
        public readonly float rot;
        public readonly Vector2 size;     // 0이면 def 값
        public readonly Vector2 offset;   // 칸 좌표계. ThingDef.Spawn이 -rot으로 돌려 넣는다

        /// <summary>실물 모양. 칸 로컬(-0.5~0.5). null이면 콜라이더 사각형 전체.</summary>
        public readonly Vector2[] shape;

        public Placed(string def, float rot = 0f, Vector2 size = default, Vector2 offset = default,
                      Vector2[] shape = null)
        {
            this.def = def;
            this.rot = rot;
            this.size = size;
            this.offset = offset;
            this.shape = shape;
        }
    }

    /// <summary>
    /// 선택한 칸이 판이면 _plates, 모듈이면 _modules. 각도·크기·offset 편집(팔레트 필드,
    /// 방향키)이 둘을 같은 코드로 다룬다 - 모듈도 반 칸 밀어 벽에서 떼어야 할 때가 있다.
    /// </summary>
    private Dictionary<Vector2Int, Placed> SelectedStore =>
        _selected == null ? null
        : _plates.ContainsKey(_selected.Value) ? _plates
        : _modules.ContainsKey(_selected.Value) ? _modules
        : null;

    /// <summary>모듈은 격자를 안 차지하므로 판과 따로 든다. 한 칸에 하나씩.</summary>
    private readonly Dictionary<Vector2Int, Placed> _plates = new();
    private readonly Dictionary<Vector2Int, Placed> _modules = new();

    private readonly List<string> _plateDefs = new();
    private readonly List<string> _moduleDefs = new();

    private string _brush = "Armor mk5";
    private bool _brushIsModule;

    /// <summary>
    /// 브러시의 각도와 크기. **배 좌표계다** - 화면에 보이는 기울기와 부호가 반대일 수
    /// 있다(<see cref="DrawPlate"/>). 기준점은 CLAUDE.md에 있다: 오른쪽으로 갈수록
    /// 올라가는 선이 rot 음수다.
    ///
    /// 크기 0은 "def 값을 쓴다"이고 그게 기본이다. 여기를 1로 초기화하면 판 전부가
    /// 자기 크기를 JSON에 적게 돼서, 진짜로 특별한 자리가 어디인지 안 보인다.
    /// </summary>
    private float _brushRot;
    private Vector2 _brushSize;

    /// <summary>
    /// 지금 손보고 있는 판. 없으면 (숫자 필드도 키 입력도) 브러시를 건드린다.
    ///
    /// 칠하면 그 칸이 선택된다. 찍고 바로 각도를 다듬는 것이 실제 작업 순서라서다.
    /// </summary>
    private Vector2Int? _selected;

    /// <summary>화면에서 한 번에 미는 거리(m)와 도는 각도. 값은 m·도지 픽셀이 아니다.</summary>
    private const float NudgeStep = 0.05f;
    private const float TurnStep = 7.5f;

    /// <summary>모양 점의 눈금. 칸이 1 m라 0.05면 칸을 20등분이다.</summary>
    private const float ShapeStep = 0.05f;

    /// <summary>이 칸 수 안의 판에만 시작점이 달라붙는다.</summary>
    private const float AnchorSnapRange = 4f;

    /// <summary>Ctrl을 누르고 있을 때의 눈금. 자석도 같이 꺼진다.</summary>
    private const float FineStep = 0.01f;

    /// <summary>이 거리 안이면 칸 꼭짓점에 달라붙는다.</summary>
    private const float MagnetRange = 0.1f;

    /// <summary>반칸 격자(변 중점·칸 중심)의 자석. 간격이 0.5라 정수 자석보다 좁아야
    /// 자유 배치로 갈 길이 남는다.</summary>
    private const float HalfMagnetRange = 0.08f;

    /// <summary>
    /// 브러시가 들고 다니는 offset. **마지막으로 만진 판에서 따라온다** - 뱃머리를
    /// 두를 때 옆 칸이 같은 자리에 서야 이음매가 안 벌어진다.
    /// </summary>
    private Vector2 _brushOffset;

    /// <summary>
    /// 가로축 대칭으로 같이 찍는다. **축은 손으로 넣는다** - 배 경계의 중앙으로
    /// 자동 계산하면 그리는 도중에 경계가 계속 변해서 축이 같이 흔들리고, 앞서 찍은
    /// 판들의 짝이 엉뚱한 칸에 가 있게 된다.
    ///
    /// 축은 소수를 받는다. 높이가 짝수인 배는 축이 칸 **사이**에 서기 때문이다
    /// (row 8과 9가 짝이면 축은 8.5).
    /// </summary>
    private bool _mirror;
    private float _mirrorAxis;

    /// <summary>
    /// 모양 찍기. 켜면 클릭이 칠하기가 아니라 **칸 고르기 + 점 찍기**가 된다.
    ///
    /// 첫 클릭이 도장을 찍을 칸을 정하고, 그 뒤 클릭이 점을 쌓는다. 점은 격자 좌표로
    /// 모이고 칸을 넘어가도 된다 - 이음매를 가리는 모양은 한 칸을 반드시 넘는다.
    /// 콜라이더 크기와 자리는 닫을 때 바운딩 박스에서 나온다.
    ///
    /// 칸을 사람이 먼저 못 박는 이유: 무게중심으로 정하면 모양을 조금 옮길 때마다
    /// 도장이 이웃 칸으로 건너뛰어서 멀쩡히 있던 판을 덮어쓴다.
    ///
    /// 회전과 섞지 않는다. 폴리곤이 이미 모양을 다 말하므로 rot 0으로 둔다 - 둘을
    /// 같이 쓰면 "이 판이 왜 이 모양인가"의 답이 두 군데로 갈린다.
    /// </summary>
    private bool _shapeMode;
    private Vector2Int? _shapeCell;
    private readonly List<Vector2> _shapePoints = new();

    /// <summary>
    /// 점 편집. 모양 모드에서 폴리곤 판을 클릭하면 그 판의 점들이 여기로 열린다.
    ///
    /// **격자 좌표로 연다.** 저장은 콜라이더 중심 기준이지만 그 중심이 편집 도중에
    /// 계속 움직이므로(bbox가 점을 따라간다), 편집 중에는 안 움직이는 공간이 필요하다.
    /// 닫을 때 <see cref="FromGridPolygon"/>이 한 번에 되돌린다.
    /// </summary>
    private Vector2Int? _editCell;
    private readonly List<Vector2> _editPoints = new();
    private int _dragVertex = -1;

    /// <summary>
    /// 직전에 그은 선의 끝과 그 선의 모양. 다음 선이 **바로 여기서** 시작하면 그 칸이
    /// 이음매이고, 두 선의 끝면을 잇는 사다리꼴로 바뀐다. 다른 칸에서 시작하면 잊는다.
    /// </summary>
    private Vector2Int? _jointCell;
    private Vector2 _jointDir;
    private float _jointSpan;
    private float _jointWidth;

    /// <summary>
    /// 이어 긋는 동안 쌓인 칸들과, 그 사슬이 시작한 자리.
    ///
    /// **두 가지를 위해 있다.** 첫째, 마지막 선이 처음 자리로 돌아오면 그 칸도 이음매다 -
    /// _jointCell은 직전 선의 끝만 알아서 닫히는 자리를 못 본다. 둘째, 예각으로 꺾으면
    /// 두 줄이 이음매 근처 칸을 여러 개 공유하는데, 한 칸에 판이 하나라 뒤엣것이
    /// 앞엣것을 지운다. 사슬이 쓴 칸이면 지우는 대신 두 판을 합친다.
    ///
    /// 사슬이 아닌 판과는 안 합친다 - 아무 판이나 합치면 선을 그을 때마다 옆 판이
    /// 조용히 자란다.
    /// </summary>
    private readonly HashSet<Vector2Int> _chainCells = new();
    private Vector2Int? _chainStart;
    private Vector2 _chainStartDir;
    private float _chainStartSpan;
    private float _chainStartWidth;

    /// <summary>판을 채우지 않고 칸 경계선만 본다. 경사판이 칸을 가려서 어디가 어딘지 안 보일 때.</summary>
    private bool _gridOnly;

    /// <summary>
    /// 전력망 모드. 켜면 팔레트가 전기 기기와 전선뿐이고, 일반 모드에서는 전기가 안 놓인다 -
    /// 두 층을 한 화면에 섞으면 판 밑의 전선이 안 보이고 클릭이 어느 층으로 가는지 안 갈린다.
    /// </summary>
    private bool _powerMode;
    private bool _brushIsWire;

    /// <summary>전선. 점은 격자 좌표(칸 번호 정수, y 아래). 저장은 칸 중심 기준(-0.5)이다.</summary>
    private readonly List<List<Vector2>> _wires = new();
    private readonly List<Vector2> _wireInProgress = new();

    /// <summary>전선 점의 눈금. 서브셀 하나(1/6 m) - 런타임 노드 키와 같은 눈금이다.</summary>
    private const float WireStep = 1f / 6f;

    /// <summary>
    /// 실행취소. **판과 모듈 사전을 통째로 복사해 쌓는다.**
    ///
    /// EditorWindow의 평범한 필드는 Unity의 Undo가 못 본다(ScriptableObject가 아니다).
    /// 조작마다 되돌리는 코드를 따로 쓰면 새 조작을 넣을 때마다 짝을 하나씩 잊는다 -
    /// 통째로 찍어 두면 그 실수가 존재할 자리가 없다. 배 한 척이 판 수백 장이라
    /// 복사가 아깝지 않다: 사람이 키를 누르는 속도로만 일어난다.
    /// </summary>
    private class Snapshot
    {
        public Dictionary<Vector2Int, Placed> plates, modules;
        public List<List<Vector2>> wires;
    }

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    private const int MaxUndo = 64;

    private string _shipName = "newship";
    private Vector2 _pan = new(40f, 40f);
    private float _zoom = 18f;
    private Vector2 _paletteScroll;
    private string _status = "";

    private const int Margin = 6;

    [MenuItem("Tools/Ship/Ship Painter")]
    private static void Open() => GetWindow<ShipPainter>("Ship Painter").minSize = new Vector2(760, 520);

    private void OnEnable() => BuildPalette();

    /// <summary>
    /// Defs 폴더를 훑어 붓 목록을 만든다. **파일 이름이 아니라 defName을 쓴다** -
    /// `ArmorMk5.json`의 이름은 `Armor mk5`라 둘이 다르다.
    /// </summary>
    private void BuildPalette()
    {
        _plateDefs.Clear();
        _moduleDefs.Clear();

        if (!Directory.Exists(DefDatabase.DefDirectory))
            return;

        foreach (string path in Directory.GetFiles(DefDatabase.DefDirectory, "*.json", SearchOption.AllDirectories))
        {
            NameOnly head = JsonUtility.FromJson<NameOnly>(File.ReadAllText(path));

            if (head == null || string.IsNullOrEmpty(head.defName))
                continue;

            ThingDef def = DefDatabase.Get(head.defName);

            if (def?.MainType == null)
                continue;

            if (typeof(Armor).IsAssignableFrom(def.MainType))
                _plateDefs.Add(head.defName);
            else if (typeof(Gun).IsAssignableFrom(def.MainType)
                  || typeof(Engine).IsAssignableFrom(def.MainType)
                  || typeof(CriticalModule).IsAssignableFrom(def.MainType)
                  || typeof(Tank).IsAssignableFrom(def.MainType))
                _moduleDefs.Add(head.defName);
        }

        _plateDefs.Sort();
        _moduleDefs.Sort();
    }

    private void OnGUI()
    {
        wantsMouseMove = true;

        if (Event.current.type == EventType.MouseMove)
            Repaint();

        DrawToolbar();

        Rect side = new(0f, 22f, 190f, position.height - 22f);
        Rect canvas = new(190f, 22f, position.width - 190f, position.height - 22f);

        DrawPalette(side);
        DrawCanvas(canvas);

        if (!string.IsNullOrEmpty(_status))
            EditorGUI.LabelField(new Rect(196f, position.height - 20f, canvas.width - 12f, 18f), _status);
    }

    private void DrawToolbar()
    {
        using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _shipName = EditorGUILayout.TextField(_shipName, EditorStyles.toolbarTextField, GUILayout.Width(160f));

            if (GUILayout.Button("불러오기", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                Load();

            if (GUILayout.Button("저장", EditorStyles.toolbarButton, GUILayout.Width(50f)))
                Save();

            if (GUILayout.Button("템플릿 뽑기", EditorStyles.toolbarButton, GUILayout.Width(90f)))
                ExportTemplate();

            if (GUILayout.Button("비우기", EditorStyles.toolbarButton, GUILayout.Width(60f)))
            {
                Push();
                _plates.Clear();
                _modules.Clear();
                _wires.Clear();
                _wireInProgress.Clear();
                _selected = null;
                _status = "";
            }

            _gridOnly = GUILayout.Toggle(_gridOnly, "격자만", EditorStyles.toolbarButton, GUILayout.Width(60f));
            _mirror = GUILayout.Toggle(_mirror, "대칭", EditorStyles.toolbarButton, GUILayout.Width(48f));

            bool wasShape = _shapeMode;
            _shapeMode = GUILayout.Toggle(_shapeMode, "모양", EditorStyles.toolbarButton, GUILayout.Width(48f));

            if (wasShape && !_shapeMode)
            {
                CancelShape();
                CancelEdit();
            }

            bool wasPower = _powerMode;
            _powerMode = GUILayout.Toggle(_powerMode, "전력망", EditorStyles.toolbarButton, GUILayout.Width(56f));

            if (wasPower != _powerMode)
            {
                _wireInProgress.Clear();
                _brushIsWire = _powerMode;

                if (_powerMode)
                {
                    CancelShape();
                    CancelEdit();
                    _shapeMode = false;
                }
                else if (_brushIsModule && IsElectrical(_brush))
                {
                    _brush = _plateDefs.Count > 0 ? _plateDefs[0] : "";
                    _brushIsModule = false;
                }
            }

            using (new EditorGUI.DisabledScope(!_mirror))
            {
                GUILayout.Label("축", EditorStyles.miniLabel, GUILayout.Width(16f));
                _mirrorAxis = EditorGUILayout.FloatField(_mirrorAxis, EditorStyles.toolbarTextField, GUILayout.Width(44f));
            }

            GUILayout.Space(12f);
            GUILayout.Label(
                $"판 {_plates.Count}  모듈 {_modules.Count}  전선 {_wires.Count}"
                + (Mathf.Approximately(_brushRot, 0f) ? "" : $"   브러시 {_brushRot:0.#}도")
                + (_shapeCell != null ? $"   모양 {_shapeCell.Value.x},{_shapeCell.Value.y} 점 {_shapePoints.Count}" : ""),
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                "좌클릭 칠하기 / Shift+클릭 사선 잇기 / Ctrl+클릭 시작점 / 우클릭 지우기 / Alt+클릭 스포이드 / 가운데 끌기 이동 / 휠 확대"
                + (_powerMode
                    ? "   |   전선: 클릭=점 / 기기 클릭=끝 / Enter 끝 / Backspace 점 빼기 / Esc 버리기 / 우클릭 전선 지우기"
                    : _shapeMode
                    ? "   |   모양: 판 클릭=편집(점 끌기/변 클릭 끼우기/Delete 빼기) / 빈칸 클릭=새 모양 / Ctrl 정밀(0.01, 자석끔) / Enter 닫기 / Backspace 취소 / Esc 버리기"
                    : "   |   방향키 offset / Shift+방향키 크기 / Alt+좌우([ ]) 회전 / Ctrl+Z 되돌리기"),
                EditorStyles.miniLabel);
        }
    }

    private void DrawPalette(Rect area)
    {
        GUILayout.BeginArea(area);
        _paletteScroll = GUILayout.BeginScrollView(_paletteScroll);

        if (_powerMode)
        {
            GUILayout.Label("전력망", EditorStyles.boldLabel);

            if (GUILayout.Toggle(_brushIsWire, "전선", EditorStyles.miniButton))
            {
                _brushIsWire = true;
                _brushIsModule = false;
            }

            foreach (string def in _moduleDefs)
            {
                if (!IsElectrical(def))
                    continue;

                if (GUILayout.Toggle(_brushIsModule && _brush == def, def, EditorStyles.miniButton))
                {
                    _brush = def;
                    _brushIsModule = true;
                    _brushIsWire = false;
                }
            }
        }
        else
        {
            GUILayout.Label("판", EditorStyles.boldLabel);

            foreach (string def in _plateDefs)
            {
                if (GUILayout.Toggle(!_brushIsModule && _brush == def, def, EditorStyles.miniButton))
                {
                    _brush = def;
                    _brushIsModule = false;
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("모듈", EditorStyles.boldLabel);

            foreach (string def in _moduleDefs)
            {
                if (IsElectrical(def))
                    continue;

                if (GUILayout.Toggle(_brushIsModule && _brush == def, def, EditorStyles.miniButton))
                {
                    _brush = def;
                    _brushIsModule = true;
                }
            }
        }

        GUILayout.Space(10f);

        bool editing = SelectedStore != null;

        GUILayout.Label(
            editing ? $"선택한 판 {_selected.Value.x},{_selected.Value.y}" : "브러시",
            EditorStyles.boldLabel);

        float rot = _brushRot;
        Vector2 size = _brushSize;
        Vector2 offset = _brushOffset;
        Placed sel = default;

        if (editing)
        {
            sel = SelectedStore[_selected.Value];
            rot = sel.rot;
            size = sel.size;
            offset = sel.offset;
        }

        // 같은 필드가 선택 여부에 따라 다른 것을 편집한다. 필드를 두 벌 두면 어느 쪽이
        // 지금 유효한지가 화면에 안 나오고, 브러시를 고친 줄 알았는데 판이 바뀐다.
        rot = EditorGUILayout.FloatField("각도", rot);
        size = EditorGUILayout.Vector2Field("크기 (0=def)", size);

        // 브러시에도 offset이 있다. 짝수 크기 모듈은 자동으로 반 칸 밀지만(ModuleBrush),
        // 1.3·1.9 같은 크기는 손으로 밀어야 벽에서 떨어진다.
        offset = EditorGUILayout.Vector2Field("offset (칸)", offset);

        if (editing)
        {
            if (rot != sel.rot || size != sel.size || offset != sel.offset)
            {
                Push();

                // **여기서는 눈금에 안 맞춘다.** Tidy는 키로 0.05씩 쌓을 때의 누적
                // 오차를 막는 것이고, 직접 친 값은 애초에 안 쌓인다. 여기에 걸면
                // 필드를 드래그해도 7.5도를 다 넘기기 전까지 같은 값으로 되돌아와서
                // "각도가 안 먹는다"가 된다.
                // **shape를 같이 넘긴다.** 빠뜨리면 폴리곤 판의 숫자 하나만 고쳐도
                // 모양이 통째로 사라지고, 남는 것은 사각형 콜라이더뿐이다 - 배는
                // 그대로 지어지고 경고도 없다.
                var next = new Placed(sel.def, rot, size, offset, sel.shape);
                Dictionary<Vector2Int, Placed> store = SelectedStore;
                store[_selected.Value] = next;

                if (_mirror)
                {
                    Vector2Int other = Across(_selected.Value);

                    if (other != _selected.Value && store.ContainsKey(other))
                        store[other] = Mirrored(next);
                }

                _brushRot = next.rot;
                _brushSize = next.size;
                _brushOffset = next.offset;
            }
        }
        else
        {
            _brushRot = rot;
            _brushSize = size;
            _brushOffset = offset;
        }

        using (new GUILayout.HorizontalScope())
        {
            // 손으로 제일 많이 쓰는 값들. 자동 추론은 안 한다 - 두 칸 찍으면 사선을
            // 이어주는 기능은 배 한 척을 다 그려보고 뭐가 반복되는지 본 다음에 만든다.
            if (GUILayout.Button("0", EditorStyles.miniButtonLeft)) _brushRot = 0f;
            if (GUILayout.Button("+45", EditorStyles.miniButtonMid)) _brushRot = 45f;
            if (GUILayout.Button("-45", EditorStyles.miniButtonMid)) _brushRot = -45f;

            if (GUILayout.Button("기본크기", EditorStyles.miniButtonMid))
                _brushSize = Vector2.zero;
            if (GUILayout.Button("offset 0", EditorStyles.miniButtonRight))
                _brushOffset = Vector2.zero;
        }

        if (_shapeMode)
        {
            GUILayout.Space(10f);
            bool shaping = _editCell != null;

            GUILayout.Label(
                shaping ? $"편집 {_editCell.Value.x},{_editCell.Value.y}  점 {_editPoints.Count}개"
                : _shapeCell == null ? "칸을 고르거나 폴리곤 판을 클릭"
                : $"칸 {_shapeCell.Value.x},{_shapeCell.Value.y}  점 {_shapePoints.Count}개",
                EditorStyles.boldLabel);

            using (new GUILayout.HorizontalScope())
            {
                // 키에만 기대지 않는다. 포커스가 어디 가 있든 버튼은 언제나 눌린다.
                using (new EditorGUI.DisabledScope((shaping ? _editPoints.Count : _shapePoints.Count) < 3))
                {
                    if (GUILayout.Button("닫기", EditorStyles.miniButtonLeft))
                    {
                        if (shaping) CommitEdit(); else CommitShape();
                    }
                }

                if (GUILayout.Button("버리기", EditorStyles.miniButtonRight))
                {
                    CancelShape();
                    CancelEdit();
                }
            }
        }

        GUILayout.Space(10f);

        if (GUILayout.Button("검사"))
            _status = Validate();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // =========================================================
    // 캔버스
    // =========================================================

    private void DrawCanvas(Rect area)
    {
        GUI.BeginClip(area);
        Rect local = new(0f, 0f, area.width, area.height);
        EditorGUI.DrawRect(local, new Color(0.11f, 0.12f, 0.14f));

        HandleInput(local);
        HandleKeys();

        HashSet<Vector2Int> exterior = RefreshFitCaches();

        if (!_gridOnly)
        {
            foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
            {
                // 편집 중인 판은 저장된 모양 대신 편집 버퍼를 그린다. 둘을 겹쳐 그리면
                // 어느 쪽이 진짜인지 안 보인다.
                if (_editCell != pair.Key)
                    DrawPlate(pair.Key, pair.Value, local);
            }
        }
        else
        {
            // **격자만 모드는 위상을 보는 자리다.** 콜라이더는 안 그린다 - 각도도 크기도
            // offset도 방 구획에 아무 영향이 없고(격자는 판의 위치 하나만 읽는다),
            // 그것들을 같이 그리면 "어느 칸이 막혔나"가 안 보인다. 여기서 답해야 하는
            // 질문은 하나뿐이다: 방이 생기려면 어느 칸에 판을 더 놓아야 하나.
            foreach (Vector2Int cell in _plates.Keys)
                DrawCell(cell, Muted(PlateColour(_plates[cell].def)), local);
        }

        // **채우기 뒤에 그린다.** 앞에 그리면 방 오버레이가 선을 덮어서 격자가 안 보인다.
        if (_gridOnly)
            DrawGrid(local);

        if (_mirror)
        {
            // 축은 칸 경계가 아니라 칸 중심을 지난다 - row 8과 9의 짝인 8.5는 두 칸
            // 사이에 서지만, row 8 자기 자신이 짝인 8은 그 칸 한가운데를 지난다.
            float y = _pan.y + (_mirrorAxis + 0.5f) * _zoom;
            EditorGUI.DrawRect(new Rect(0f, y - 1f, local.width, 2f), new Color(0.4f, 0.9f, 1f, 0.55f));
        }

        DrawSelection(local);
        DrawShapeInProgress();
        DrawEdit();

        // 실내는 판 위에 안 겹치므로 뒤에 그려도 된다. 판이 없는 칸만 칠한다.
        foreach (Vector2Int cell in Interior(exterior))
            DrawCell(cell, Muted(new Color(0.16f, 0.34f, 0.5f, 0.55f)), local);

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
            DrawModule(pair.Key, pair.Value, local);

        DrawWires(local);

        // 모듈 브러시의 미리보기. 놓기 전에 크기·방향·벽 안 여부가 보인다.
        if (_brushIsModule && !string.IsNullOrEmpty(_brush) && local.Contains(Event.current.mousePosition))
        {
            Vector2Int under = CellAt(Event.current.mousePosition);

            if (!_modules.ContainsKey(under))
                DrawModule(under, ModuleBrush(), local, ghost: true);
        }

        DrawModuleHover(local);

        GUI.EndClip();
    }

    private static GUIStyle _moduleLabel;
    private static GUIStyle _hoverLabel;

    /// <summary>
    /// 모듈을 실제 콜라이더 상자(def 크기 + 배치 덮어쓰기 + offset, rot)로 그린다. 예전에는
    /// 칸 한가운데 작은 사각형 하나라 3 m 포탑과 원자로가 같은 점으로 보였고, 벽 안에
    /// 박힌 것도 안 보였다. 벽 안이면 붉게. 원점(회전축)은 작은 점, 이름은 칸 위에.
    /// </summary>
    private void DrawModule(Vector2Int cell, Placed placed, Rect clip, bool ghost = false)
    {
        Rect r = CellRect(cell);

        ShipBuilder.ModuleBox(placed.def, placed.rot, placed.size, placed.offset, out Vector2 size, out Vector2 offset);

        // 칸 중심 + 배 좌표 offset(y 뒤집어서). DrawPlate와 같은 규칙.
        var centre = new Vector2(r.center.x + offset.x * _zoom, r.center.y - offset.y * _zoom);

        var box = new Rect(
            centre.x - size.x * _zoom * 0.5f,
            centre.y - size.y * _zoom * 0.5f,
            size.x * _zoom,
            size.y * _zoom);

        var reach = new Rect(
            centre.x - size.magnitude * _zoom, centre.y - size.magnitude * _zoom,
            size.magnitude * _zoom * 2f, size.magnitude * _zoom * 2f);

        if (!reach.Overlaps(clip))
            return;

        Color c = ModuleColour(placed.def);
        ModulePlacement.Result fit = FitOf(cell, placed);
        bool buried = !fit.Allowed;
        bool warn = fit.verdict == ModulePlacement.Verdict.Buried;

        Color fill = buried ? new Color(1f, 0.25f, 0.2f, 0.45f)
            : warn ? new Color(1f, 0.7f, 0.2f, 0.35f)
            : new Color(c.r, c.g, c.b, 0.32f);
        Color line = buried ? new Color(1f, 0.35f, 0.3f) : warn ? new Color(1f, 0.75f, 0.3f) : c;

        if (_powerMode && !IsDevice(placed.def))
        {
            fill = Muted(fill);
            line = Muted(line);
        }

        if (ghost)
        {
            fill.a *= 0.5f;
            line.a = 0.6f;
        }

        Matrix4x4 saved = GUI.matrix;
        GUIUtility.RotateAroundPivot(-placed.rot, centre);

        EditorGUI.DrawRect(box, fill);
        EditorGUI.DrawRect(new Rect(box.x, box.y, box.width, 1f), line);
        EditorGUI.DrawRect(new Rect(box.x, box.yMax - 1f, box.width, 1f), line);
        EditorGUI.DrawRect(new Rect(box.x, box.y, 1f, box.height), line);
        EditorGUI.DrawRect(new Rect(box.xMax - 1f, box.y, 1f, box.height), line);

        GUI.matrix = saved;

        // 원점 = 회전축 = 배치 칸. 상자가 offset으로 밀려 있어도 여기가 그 모듈의 자리다.
        EditorGUI.DrawRect(
            new Rect(r.x + r.width * 0.36f, r.y + r.height * 0.36f, r.width * 0.28f, r.height * 0.28f), c);

        if (_zoom < 14f || ghost)
            return;

        _moduleLabel ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white },
        };

        GUI.Label(new Rect(r.center.x - 60f, r.y - 15f, 120f, 14f), placed.def, _moduleLabel);
    }

    /// <summary>마우스 아래 모듈의 정보 한 줄. 이름·자리·크기·각도·마운트·벽 안 칸 수.</summary>
    private void DrawModuleHover(Rect clip)
    {
        Vector2 mouse = Event.current.mousePosition;

        if (!clip.Contains(mouse))
            return;

        Vector2Int cell = CellAt(mouse);

        if (!_modules.TryGetValue(cell, out Placed placed))
            return;

        ShipBuilder.ModuleBox(placed.def, placed.rot, placed.size, placed.offset, out Vector2 size, out _);
        ModulePlacement.Result fit = FitOf(cell, placed);
        string mount = fit.hasMount ? $"({fit.mount.x},{fit.mount.y})" : "없음";

        string text =
            $"{placed.def}  원점 ({cell.x},{cell.y})  자리 ({fit.cell.x},{fit.cell.y})  {size.x:0.#}×{size.y:0.#} m  rot {placed.rot:0}°  마운트 {mount}" +
            (fit.verdict == ModulePlacement.Verdict.Ok ? "" : $"  <!> {fit.detail}");

        _hoverLabel ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white } };

        Vector2 sz = _hoverLabel.CalcSize(new GUIContent(text));
        var bg = new Rect(
            Mathf.Min(mouse.x + 14f, clip.width - sz.x - 8f),
            Mathf.Max(mouse.y - sz.y - 10f, 0f),
            sz.x + 8f, sz.y + 4f);

        EditorGUI.DrawRect(bg, new Color(0.05f, 0.05f, 0.07f, 0.92f));
        GUI.Label(new Rect(bg.x + 4f, bg.y + 2f, sz.x, sz.y), text, _hoverLabel);
    }

    private void DrawCell(Vector2Int cell, Color colour, Rect clip)
    {
        Rect r = CellRect(cell);

        if (!r.Overlaps(clip))
            return;

        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width - 1f, r.height - 1f), colour);
    }

    /// <summary>
    /// 판 하나를 회전·크기·offset까지 반영해서 그린다.
    ///
    /// **부호가 여기서 문다.** 배치의 rot과 offset은 배 좌표계인데(y가 위로), 이 창의
    /// 격자는 row가 아래로 증가하고 화면 y도 아래로 간다. 둘은 x축 대칭이라, 같은 도형을
    /// 같은 방향으로 보이게 하려면 각도의 부호를 뒤집고 offset의 y도 뒤집어야 한다.
    /// 수식으로는 거울 M = diag(1,-1)에 대해 M·R(θ)·M⁻¹ = R(-θ)다.
    ///
    /// 안 뒤집으면 창에서는 멀쩡한데 게임에서 경사면이 **거울상**으로 나온다. 배는
    /// 여전히 지어지고 방도 정상이라 아무 검사도 안 걸리고, 틀린 것은 눈에 보이는
    /// 기울기뿐이다. 기준점은 CLAUDE.md에 있다 - 오른쪽으로 갈수록 올라가는 선이
    /// rot 음수다.
    /// </summary>
    private void DrawPlate(Vector2Int cell, Placed placed, Rect clip)
    {
        Rect r = CellRect(cell);

        ThingDef def = DefDatabase.Get(placed.def);
        Vector2 size = placed.size != Vector2.zero
            ? placed.size
            : (def != null ? def.collider.size : Vector2.one);

        Color colour = Muted(PlateColour(placed.def));

        // 모양이 있으면 그것이 진짜 실루엣이다. 회전·크기는 모양이 있을 때 0으로
        // 두기로 했으므로 여기서 갈라도 두 그림이 겹칠 일이 없다.
        if (placed.shape != null && placed.shape.Length >= 3)
        {
            // 점은 콜라이더 중심 기준이라 offset을 더해야 칸 중심 기준이 된다.
            // 칸을 넘어가는 모양이 흔하므로 클립 판정은 콜라이더 크기로 넉넉히 본다.
            var shapeCentre = new Vector2(
                r.center.x + placed.offset.x * _zoom,
                r.center.y - placed.offset.y * _zoom);

            var shapeReach = new Rect(
                shapeCentre.x - size.x * _zoom, shapeCentre.y - size.y * _zoom,
                size.x * _zoom * 2f, size.y * _zoom * 2f);

            if (shapeReach.Overlaps(clip))
                DrawPolygon(shapeCentre, placed.shape, colour);

            return;
        }

        // 회전도 크기 지정도 없으면 예전 경로 그대로. 판 대부분이 여기로 빠지므로
        // GUI.matrix를 건드리는 비용이 안 든다.
        if (Mathf.Approximately(placed.rot, 0f)
            && Mathf.Approximately(size.x, 1f) && Mathf.Approximately(size.y, 1f)
            && placed.offset == Vector2.zero)
        {
            if (r.Overlaps(clip))
                EditorGUI.DrawRect(new Rect(r.x, r.y, r.width - 1f, r.height - 1f), colour);

            return;
        }

        // 칸 중심 + 배치 offset(y 뒤집어서) 자리에 size 크기로 놓는다.
        var centre = new Vector2(
            r.center.x + placed.offset.x * _zoom,
            r.center.y - placed.offset.y * _zoom);

        var box = new Rect(
            centre.x - size.x * _zoom * 0.5f,
            centre.y - size.y * _zoom * 0.5f,
            size.x * _zoom,
            size.y * _zoom);

        // 회전하면 AABB가 커지므로 클립 판정도 넉넉히 본다. 안 그러면 화면 가장자리에서
        // 기울어진 판이 사라진다.
        var reach = new Rect(
            centre.x - size.magnitude * _zoom, centre.y - size.magnitude * _zoom,
            size.magnitude * _zoom * 2f, size.magnitude * _zoom * 2f);

        if (!reach.Overlaps(clip))
            return;

        Matrix4x4 saved = GUI.matrix;

        GUIUtility.RotateAroundPivot(-placed.rot, centre);
        EditorGUI.DrawRect(box, colour);

        GUI.matrix = saved;
    }

    /// <summary>
    /// 칸 로컬 폴리곤을 화면에 채운다. **부채꼴로 쪼갠다** - 오목한 모양이면 이 방법이
    /// 틀리지만, 여기는 저작 중에 눈으로 보는 그림이고 진짜 판정은 Armor가 넓이
    /// 클리핑으로 한다. 미리보기가 조금 틀리는 것과 시뮬레이션이 틀리는 것은 다른 일이다.
    /// </summary>
    private void DrawPolygon(Vector2 centre, Vector2[] shape, Color colour)
    {
        Vector2 mid = Vector2.zero;

        foreach (Vector2 v in shape)
            mid += v;

        mid /= shape.Length;

        for (int i = 0; i < shape.Length; i++)
        {
            Vector2 a = ToScreen(centre, shape[i]);
            Vector2 b = ToScreen(centre, shape[(i + 1) % shape.Length]);
            Vector2 c = ToScreen(centre, mid);

            FillTriangle(a, b, c, colour);
        }
    }

    /// <summary>격자 좌표(칸 번호가 정수) -> 화면. 여기는 y가 안 뒤집힌다 - 둘 다 아래로 간다.</summary>
    private Vector2 GridToScreen(Vector2 grid)
        => new(_pan.x + grid.x * _zoom, _pan.y + grid.y * _zoom);

    /// <summary>얇은 선 하나. EditorGUI에 선이 없어서 삼각형 채우기로 대신한다.</summary>
    private static void Line(Vector2 a, Vector2 b, Color colour)
    {
        Vector2 n = (b - a).normalized;
        var side = new Vector2(-n.y, n.x);

        FillTriangle(a + side, b + side, b - side, colour);
        FillTriangle(a + side, b - side, a - side, colour);
    }

    /// <summary>칸 로컬 -> 화면. y가 뒤집힌다.</summary>
    private Vector2 ToScreen(Vector2 centre, Vector2 local)
        => new(centre.x + local.x * _zoom, centre.y - local.y * _zoom);

    /// <summary>
    /// 삼각형 하나를 가로줄로 채운다. EditorGUI에는 사각형밖에 없어서 직접 훑는다.
    /// 줌이 커도 칸 하나만큼이라 줄 수가 얼마 안 된다.
    /// </summary>
    private static void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Color colour)
    {
        float top = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
        float bottom = Mathf.Max(a.y, Mathf.Max(b.y, c.y));

        for (float y = Mathf.Floor(top); y <= bottom; y += 1f)
        {
            float lo = float.MaxValue;
            float hi = float.MinValue;

            Span(a, b, y, ref lo, ref hi);
            Span(b, c, y, ref lo, ref hi);
            Span(c, a, y, ref lo, ref hi);

            if (hi > lo)
                EditorGUI.DrawRect(new Rect(lo, y, hi - lo, 1f), colour);
        }
    }

    private static void Span(Vector2 p, Vector2 q, float y, ref float lo, ref float hi)
    {
        if (p.y > y == q.y > y)
            return;

        float x = p.x + (q.x - p.x) * (y - p.y) / (q.y - p.y);

        lo = Mathf.Min(lo, x);
        hi = Mathf.Max(hi, x);
    }

    /// <summary>편집 중인 폴리곤. 채운 모양 + 점 손잡이.</summary>
    private void DrawEdit()
    {
        if (_editCell == null || _editPoints.Count < 3)
            return;

        var screen = new Vector2[_editPoints.Count];

        for (int i = 0; i < _editPoints.Count; i++)
            screen[i] = GridToScreen(_editPoints[i]);

        Color fill = PlateColour(_plates.TryGetValue(_editCell.Value, out Placed p) ? p.def : _brush);
        fill.a = 0.75f;

        // 부채꼴 채우기라 오목하면 조금 틀리게 보인다. 저작 중 눈으로 보는 그림이고
        // 진짜 판정은 Armor가 넓이 클리핑으로 한다.
        Vector2 mid = Vector2.zero;

        foreach (Vector2 v in screen)
            mid += v;

        mid /= screen.Length;

        for (int i = 0; i < screen.Length; i++)
            FillTriangle(screen[i], screen[(i + 1) % screen.Length], mid, fill);

        var handle = new Color(1f, 0.85f, 0.2f);
        var active = new Color(0.4f, 0.9f, 1f);

        for (int i = 0; i < screen.Length; i++)
        {
            Line(screen[i], screen[(i + 1) % screen.Length], handle);

            float r = i == _dragVertex ? 4f : 3f;
            EditorGUI.DrawRect(
                new Rect(screen[i].x - r, screen[i].y - r, r * 2f, r * 2f),
                i == _dragVertex ? active : handle);
        }

        // 도장을 찍을 칸. 점이 어디로 뻗든 차지하는 것은 여기 하나뿐이다.
        Rect cellRect = CellRect(_editCell.Value);
        var mark = new Color(0.4f, 0.9f, 1f, 0.8f);

        EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y, cellRect.width, 2f), mark);
        EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.yMax - 2f, cellRect.width, 2f), mark);
        EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y, 2f, cellRect.height), mark);
        EditorGUI.DrawRect(new Rect(cellRect.xMax - 2f, cellRect.y, 2f, cellRect.height), mark);
    }

    /// <summary>찍는 중인 점들. 아직 판이 아니라서 따로 그린다.</summary>
    private void DrawShapeInProgress()
    {
        if (_shapeCell == null)
            return;

        var mark = new Color(1f, 0.85f, 0.2f);

        // 도장을 찍을 칸. 점이 칸을 넘어가도 차지하는 것은 여기 하나뿐이라 보여야 한다.
        Rect r = CellRect(_shapeCell.Value);
        var cellMark = new Color(0.4f, 0.9f, 1f, 0.85f);

        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 2f), cellMark);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 2f, r.width, 2f), cellMark);
        EditorGUI.DrawRect(new Rect(r.x, r.y, 2f, r.height), cellMark);
        EditorGUI.DrawRect(new Rect(r.xMax - 2f, r.y, 2f, r.height), cellMark);

        for (int i = 0; i < _shapePoints.Count; i++)
        {
            Vector2 pt = GridToScreen(_shapePoints[i]);
            EditorGUI.DrawRect(new Rect(pt.x - 2f, pt.y - 2f, 5f, 5f), mark);

            // 순서가 보여야 한다 - 점을 엉뚱한 차례로 찍으면 폴리곤이 나비 모양이 된다.
            if (i > 0)
                Line(GridToScreen(_shapePoints[i - 1]), pt, mark);
        }

        // 닫으면 어떤 모양이 되는지 미리 보여준다. 마지막 점과 첫 점 사이.
        if (_shapePoints.Count >= 3)
            Line(GridToScreen(_shapePoints[_shapePoints.Count - 1]), GridToScreen(_shapePoints[0]), mark);
    }

    /// <summary>
    /// 1 m 칸 경계선.
    ///
    /// **이 모드가 답하는 질문은 "방이 생기려면 어느 칸에 판을 더 놓아야 하나"다.**
    /// 경사판을 기울이고 offset으로 밀기 시작하면 그림과 칸이 눈에 띄게 어긋나는데,
    /// 격자가 읽는 것은 여전히 판의 **위치 하나뿐**이라 그 어긋남이 정상이다. 그래서
    /// 밀폐를 볼 때는 콜라이더를 아예 안 그리는 편이 낫다 - 기울어진 그림이 칸 경계를
    /// 가리면 뚫린 칸이 막혀 보인다.
    ///
    /// 확대가 너무 작으면 선이 화면을 덮으므로 그때는 안 그린다.
    /// </summary>
    private void DrawGrid(Rect clip)
    {
        if (_zoom < 8f)
            return;

        var line = new Color(1f, 1f, 1f, 0.35f);

        // **C#의 %는 음수를 그대로 돌려준다.** _pan이 왼쪽으로 넘어가는 순간 첫 선이
        // 화면 밖으로 나가고 격자가 한 칸 어긋난다. 칸 경계는 _pan + k*_zoom이므로
        // 나머지를 양수로 접어야 그 자리에 선다.
        float x0 = (_pan.x % _zoom + _zoom) % _zoom;
        float y0 = (_pan.y % _zoom + _zoom) % _zoom;

        for (float x = x0; x < clip.width; x += _zoom)
            EditorGUI.DrawRect(new Rect(x, 0f, 1f, clip.height), line);

        for (float y = y0; y < clip.height; y += _zoom)
            EditorGUI.DrawRect(new Rect(0f, y, clip.width, 1f), line);
    }

    /// <summary>
    /// 선택한 칸에 테두리. 한도를 넘었으면 빨갛다.
    ///
    /// **저장을 막지는 않는다.** 드래그하듯 값을 밀다 보면 잠깐 넘었다가 돌아오는 것이
    /// 정상이고, 그때마다 막으면 손이 묶인다. 최종 판정은 검사 버튼과 저장 직후의
    /// 왕복 검증이 한다.
    ///
    /// 초과 판정은 선택한 것 하나만 본다. 매 프레임 판 776장(mirror)에 삼각함수를
    /// 돌릴 이유가 없다 - 지금 손대는 판만 실시간이면 된다.
    /// </summary>
    private void DrawSelection(Rect clip)
    {
        if (SelectedStore == null)
            return;

        Rect r = CellRect(_selected.Value);

        if (!r.Overlaps(clip))
            return;

        bool over = _plates.ContainsKey(_selected.Value) && IsOverhanging(_plates[_selected.Value]);
        Color c = over ? new Color(1f, 0.3f, 0.25f) : new Color(1f, 0.85f, 0.2f);

        const float t = 2f;
        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
        EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
        EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
    }

    private Rect CellRect(Vector2Int cell) =>
        new(_pan.x + cell.x * _zoom, _pan.y + cell.y * _zoom, _zoom, _zoom);

    private Vector2Int CellAt(Vector2 point) =>
        new(Mathf.FloorToInt((point.x - _pan.x) / _zoom), Mathf.FloorToInt((point.y - _pan.y) / _zoom));

    private void HandleInput(Rect local)
    {
        Event e = Event.current;
        Vector2 point = e.mousePosition;

        if (!local.Contains(point))
            return;

        if (e.type == EventType.ScrollWheel)
        {
            float before = _zoom;
            // 확대 상한이 넉넉해야 한다 - 모양 점을 0.05 눈금으로 찍으려면 칸 하나가
            // 화면에서 충분히 커야 하고, 48이면 눈금 하나가 2픽셀이라 못 겨눈다.
            // 축소도 같이 열어 둔다: 큰 배를 통째로 보려면 3 정도가 필요하다.
            _zoom = Mathf.Clamp(_zoom - e.delta.y * (_zoom * 0.06f), 3f, 240f);

            // 커서 아래 칸이 제자리에 남도록 pan을 보정한다. 안 하면 확대할 때마다
            // 보던 곳이 화면 밖으로 달아난다.
            _pan += (point - _pan) * (1f - _zoom / before);
            e.Use();
            Repaint();
            return;
        }

        bool paint = e.button == 0 && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag);
        bool erase = e.button == 1 && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag);
        bool drag = e.button == 2 && e.type == EventType.MouseDrag;

        if (drag)
        {
            _pan += e.delta;
            e.Use();
            Repaint();
            return;
        }

        if (!paint && !erase)
            return;

        Vector2Int cell = CellAt(point);

        // 전선 브러시. 왼쪽 버튼 이벤트를 통째로 삼킨다 - 모양 모드와 같은 이유(드래그가 칠하기로 샌다).
        if (_powerMode && _brushIsWire)
        {
            if (e.type == EventType.MouseDown)
            {
                if (e.button == 0) WireClick(point);
                else if (e.button == 1) EraseWireAt(point);
            }

            e.Use();
            Repaint();
            return;
        }

        // 전력망 모드에서는 전기 기기만 지운다. 판은 일반 모드의 것이다.
        if (_powerMode && erase && !(_modules.TryGetValue(cell, out Placed under) && IsElectrical(under.def)))
        {
            e.Use();
            return;
        }

        // **Ctrl+클릭은 선의 시작점만 옮긴다.** 칠하지도 지우지도 않는다.
        //
        // 없으면 선 도구가 한붓 그리기가 된다 - _selected가 늘 마지막 선의 끝이라
        // **거기서만** 이어 그을 수 있고, 다른 자리에서 가지를 치려면 그 칸을 한 번
        // 칠해서(= 원치 않는 판을 놓아서) 선택을 옮겨야 했다. Alt 스포이드로도 옮겨지는데
        // 그건 판이 이미 있어야 하고 브러시까지 통째로 바꾼다.
        //
        // 시작점을 손으로 정했으니 _jointCell도 여기로 옮긴다. 안 옮기면 직전 선의 끝이
        // 다리 후보로 남아, 엉뚱한 칸에서 이음매가 굽는다.
        if (paint && !_shapeMode && (e.control || e.command) && e.type == EventType.MouseDown)
        {
            _selected = cell;
            // **직전 선의 끝이 아니면 다리를 포기한다.** _jointDir/_jointSpan은 그 선의
            // 값이라, 몇 줄 전에 끝난 칸을 시작점으로 고르면 엉뚱한 방향으로 이음매가
            // 굽는다. 다리를 못 놓는 것은 안 보이고, 틀린 다리는 보인다.
            _jointCell = _jointCell == cell ? cell : null;
            _chainStart = null;

            _status = _plates.ContainsKey(cell)
                ? $"시작점 {cell.x},{cell.y}. Shift+클릭으로 여기서 이어라."
                : $"시작점 {cell.x},{cell.y} (빈 칸). Shift+클릭하면 근처 판에 붙는다.";

            GUI.FocusControl(null);
            e.Use();
            Repaint();
            return;
        }

        // Ctrl이 눌린 왼쪽 버튼은 위에서 끝났다. 삼키지 않으면 MouseDrag가 아래 칠하기로
        // 흘러가서 시작점을 찍자마자 그 자리에 판이 생긴다 - Shift와 같은 함정이다.
        if (!_shapeMode && (e.control || e.command) && e.button == 0)
        {
            e.Use();
            return;
        }

        // Alt+클릭은 스포이드다. 안 칠하고, 그 판의 값을 브러시로 빨아들이고 선택한다.
        // destroyer처럼 판마다 offset이 다른 배는 "옆 판과 비슷하게"가 작업의 대부분이라,
        // 이 한 가지가 숫자 다시 치는 일을 거의 다 없앤다.
        if (paint && e.alt)
        {
            if (_plates.TryGetValue(cell, out Placed picked))
            {
                _brush = picked.def;
                _brushIsModule = false;
                _brushRot = picked.rot;
                _brushSize = picked.size;
                _brushOffset = picked.offset;
                _selected = cell;
                GUI.FocusControl(null);
            }

            e.Use();
            Repaint();
            return;
        }

        // 모양 모드에서는 좌클릭이 점 찍기다. 드래그는 안 받는다 - 점을 하나씩 놓는
        // 일이라 끌면 수십 개가 쏟아진다.
        // **왼쪽 버튼 이벤트를 통째로 삼킨다.** MouseDown만 잡으면 클릭하며 마우스가
        // 1픽셀만 움직여도 MouseDrag가 뒤로 흘러가서 그 칸이 칠해진다 - 증상은
        // "점 찍었더니 판이 생긴다"다. 점은 MouseDown에서만 는다.
        // Shift+클릭은 모양 모드에서도 선 도구로 간다. 안 그러면 모양 모드를 켜둔 채
        // 선을 그으려다 "가끔 Shift+클릭이 안 먹는다"가 된다 - 켜둔 줄 모르면 원인이
        // 절대 안 보인다.
        if (_shapeMode && e.button == 0 && !e.shift)
        {
            if (e.type == EventType.MouseDown)
                ShapeClick(cell, point);
            else if (e.type == EventType.MouseDrag && _dragVertex >= 0)
                DragVertex(point);
            else if (e.type == EventType.MouseUp)
                _dragVertex = -1;

            if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag || e.type == EventType.MouseUp)
            {
                e.Use();
                Repaint();
            }

            return;
        }

        // Shift+클릭은 마지막으로 만진 칸에서 여기까지 잇는다. 포토샵의 선 도구와 같다 -
        // 새 상태를 안 만든다. 끝점은 이미 _selected가 들고 있다.
        if (paint && e.shift && e.type == EventType.MouseDown && !_brushIsModule)
        {
            // **시작점을 가까운 판에 붙인다.** 선을 긋다 잠깐 다른 일을 하면 _selected가
            // 옮겨가 있고, 그때 이어 긋기가 제일 불편하다. 클릭한 자리에서 가까운 판을
            // 찾아 거기서 시작하면 "마지막 지점"을 손으로 다시 찾을 일이 없다.
            Vector2Int? start = NearestPlate(_selected, cell) ?? _selected;

            // 시작이 클릭한 칸 자신이면 길이 0이라 아무 일도 안 일어난다. 조용히
            // 넘어가면 "Shift+클릭이 안 먹는다"로 보인다 - 말을 해야 한다.
            if (start == null || start.Value == cell)
            {
                _status = "이을 판이 없다. 다른 칸을 먼저 칠하거나 거기서 시작해라.";
                e.Use();
                return;
            }

            Push();
            Line(start.Value, cell);
            GUI.FocusControl(null);
            e.Use();
            Repaint();
            return;
        }

        // **Shift가 눌린 왼쪽 버튼은 선 도구 전용이다.** 여기서 삼키지 않으면 클릭하며
        // 마우스가 1픽셀만 움직여도 MouseDrag가 아래 칠하기로 흘러가는데, Line이
        // 브러시에 각도와 크기를 남겨두므로 그 스침이 선 조각처럼 생긴 판을 찍고
        // _selected까지 옮긴다 - "목표지점에서 새 직선이 나온다"와 "Shift+클릭이
        // 가끔 안 먹는다"가 전부 이 한 줄기다.
        if (e.shift && e.button == 0)
        {
            e.Use();
            return;
        }

        // 드래그 한 번이 조작 하나다. 매 MouseDrag마다 쌓으면 Ctrl+Z 한 번이 칸 하나만
        // 되돌리고, 붓질 한 번을 물리려면 스무 번을 눌러야 한다.
        if (e.type == EventType.MouseDown)
            Push();

        if (erase)
        {
            // 누른 자리에 모듈이 있으면 이 드래그는 모듈만 지운다. 안 그러면 드래그가 같은
            // 칸을 두 번 지나며 모듈 다음에 판까지 지운다.
            if (e.type == EventType.MouseDown)
                _eraseModulesOnly = _modules.ContainsKey(cell);

            Erase(cell);

            if (_mirror)
                Erase(Across(cell));
        }
        else if (_brushIsModule)
        {
            // 판에 파묻히거나, 다른 모듈과 겹치거나, 우주에 뜨면 못 놓는다. 런타임은 아직 경고만.
            ModulePlacement.Result fit = FitOf(cell, ModuleBrush());

            if (!fit.Allowed)
            {
                _status = $"({cell.x},{cell.y})에 {_brush} 못 놓는다 - {fit.detail}";
                return;
            }

            if (fit.verdict == ModulePlacement.Verdict.Buried)
                _status = $"({cell.x},{cell.y}) {_brush}: {fit.detail}";

            _modules[cell] = Tidy(ModuleBrush());

            if (_mirror)
                _modules[Across(cell)] = Tidy(Mirrored(ModuleBrush()));
        }
        else
        {
            var put = Tidy(new Placed(_brush, _brushRot, _brushSize, _brushOffset));

            Paint(cell, put);

            if (_mirror)
            {
                Vector2Int other = Across(cell);

                // 축 위의 칸은 자기 자신이 짝이다. 그냥 두면 뒤엣것이 앞엣것을 덮어서
                // 축에 놓은 판만 각도가 뒤집힌다.
                if (other != cell)
                    Paint(other, Mirrored(put));
            }

            _selected = cell;

            // 숫자 필드가 포커스를 쥐고 있으면 화살표키를 그쪽이 먹는다.
            GUI.FocusControl(null);
        }

        e.Use();
        Repaint();
    }

    /// <summary>
    /// **화면에서 본 것을 저장할 값으로 바꾸는 자리는 여기 하나여야 한다.**
    ///
    /// 창의 격자와 화면은 y가 아래로 가고, 배치의 rot·offset은 배 좌표계(y가 위로)다.
    /// 둘은 x축 대칭이라 각도의 부호와 offset의 y가 같이 뒤집힌다. 조작마다 이 변환을
    /// 따로 쓰면 그중 하나만 안 뒤집히고, 증상은 "위로 밀었는데 게임에서 아래로 간다"에
    /// 그친다 - 배는 멀쩡히 지어지고 방도 정상이라 아무 검사도 안 걸린다.
    ///
    /// 크기는 여기 안 온다. size는 판의 **로컬 축** 값이라 화면이 어느 쪽이든 상관없다.
    /// </summary>
    /// <summary>
    /// 가로축 건너편 칸. row는 아래로 증가하지만 대칭은 축에서 잰 거리라 방향과 무관하다.
    /// </summary>
    private Vector2Int Across(Vector2Int cell) =>
        new(cell.x, Mathf.RoundToInt(2f * _mirrorAxis - cell.y));

    /// <summary>
    /// 가로축에 비친 판.
    ///
    /// **각도와 offset.y가 같이 부호를 바꾼다.** 배 좌표계에서 가로축 대칭은 거울
    /// M = diag(1,-1)이고, M·R(θ)·M⁻¹ = R(-θ)다 - 그림을 그릴 때 쓴 것과 정확히 같은
    /// 식이다. 크기는 안 바뀐다: size는 판의 로컬 축 값이라 거울에 비쳐도 그대로다.
    /// </summary>
    private static Placed Mirrored(Placed p)
    {
        Vector2[] shape = null;

        if (p.shape != null)
        {
            // 점도 같이 비친다. **감기 방향이 뒤집히지만 상관없다** - 넓이도 점 포함
            // 판정도 감기와 무관하게 짜여 있다.
            shape = new Vector2[p.shape.Length];

            for (int i = 0; i < p.shape.Length; i++)
                shape[i] = new Vector2(p.shape[i].x, -p.shape[i].y);
        }

        Vector2 defOffset = DefDatabase.Get(p.def)?.collider?.offset ?? Vector2.zero;

        return new Placed(p.def, -p.rot, p.size, ModulePlacement.MirrorOffset(defOffset, p.rot, p.offset), shape);
    }

    private static Vector2 ScreenNudgeToShip(Vector2 screen) => new(screen.x, -screen.y);

    private static float ScreenTurnToShip(float screenClockwise) => -screenClockwise;

    /// <summary>
    /// 눈금에 맞춘다. **0.05를 스무 번 더하면 1이 아니라 0.99999994다** - float의
    /// 누적 오차라 화면에는 1로 보이고 JSON에는 0.9999999가 적힌다. 다음에 열었을 때
    /// 그 값이 다시 눈금 밖이라 조금씩 어긋나기 시작한다.
    ///
    /// 매번 반올림하면 오차가 쌓일 자리가 없다. 값이 이미 눈금 위면 아무 일도 안 한다.
    /// </summary>
    private static float Snap(float v, float step) => Mathf.Round(v / step) * step;

    private static Vector2 Snap(Vector2 v, float step) => new(Snap(v.x, step), Snap(v.y, step));

    /// <summary>
    /// **각도는 안 건드린다.** 7.5는 이진수로 정확해서 몇 번을 더해도 안 새고, 여기서
    /// 반올림하면 자유각이 죽는다 - 2:1 기울기 사선은 26.565도인데 22.5도로 뭉개지고,
    /// 스포이드로 그런 판을 집어서 찍어도 같은 일이 난다. 새는 것은 0.05뿐이다.
    /// </summary>
    private static Placed Tidy(Placed p, float step = NudgeStep) => new(
        p.def,
        p.rot,
        p.size == Vector2.zero ? Vector2.zero : Snap(p.size, step),
        Snap(p.offset, step),
        p.shape);

    /// <summary>지금 상태를 실행취소 더미에 올린다. **바꾸기 전에** 부른다.</summary>
    private Snapshot Snap() => new() { plates = Copy(_plates), modules = Copy(_modules), wires = CopyWires(_wires) };

    private static List<List<Vector2>> CopyWires(List<List<Vector2>> src)
    {
        var copy = new List<List<Vector2>>(src.Count);
        foreach (List<Vector2> w in src) copy.Add(new List<Vector2>(w));
        return copy;
    }

    private void Push()
    {
        _undo.Add(Snap());

        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);

        // 새 조작이 들어오면 앞으로 갈 길은 사라진다. 안 지우면 되돌린 뒤 다른 것을
        // 하고 나서 Ctrl+Y를 눌렀을 때 없던 역사가 되살아난다.
        _redo.Clear();
    }

    /// <summary>
    /// 되돌리기용 사본. **shape 배열까지 새로 만든다.**
    ///
    /// Placed는 값 타입이지만 shape는 참조다. 사전만 복사하면 스냅샷과 현재가 같은
    /// 배열을 가리키고, 점 편집이 그 배열을 제자리에서 고치는 순간 **과거가 같이
    /// 바뀐다** - Ctrl+Z를 눌러도 아무 일이 안 일어난다. 폴리곤이 생기기 전에는
    /// 배열을 늘 새로 만들어서 이 함정이 잠들어 있었다.
    /// </summary>
    private static Dictionary<Vector2Int, Placed> Copy(Dictionary<Vector2Int, Placed> src)
    {
        var copy = new Dictionary<Vector2Int, Placed>(src.Count);

        foreach (KeyValuePair<Vector2Int, Placed> pair in src)
        {
            Placed p = pair.Value;
            copy[pair.Key] = p.shape == null
                ? p
                : new Placed(p.def, p.rot, p.size, p.offset, (Vector2[])p.shape.Clone());
        }

        return copy;
    }

    private void Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0)
            return;

        to.Add(Snap());

        var snap = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);

        _plates.Clear();
        foreach (KeyValuePair<Vector2Int, Placed> pair in snap.plates)
            _plates[pair.Key] = pair.Value;

        _modules.Clear();
        foreach (KeyValuePair<Vector2Int, Placed> pair in snap.modules)
            _modules[pair.Key] = pair.Value;

        _wires.Clear();
        _wires.AddRange(snap.wires);
        _wireInProgress.Clear();

        if (_selected != null && !_plates.ContainsKey(_selected.Value))
            _selected = null;

        Repaint();
    }

    /// <summary>
    /// 선택한 판을 키로 다듬는다. 핸들 드래그를 안 만든 이유가 여기 있다 - 값이 0.05 m와
    /// 7.5도로 양자화돼 있어서 마우스의 미세 조작이 아무 이득을 안 준다. 회전한 사각형의
    /// 귀퉁이를 화면 방향으로 끌면 로컬 폭과 높이가 **둘 다** 변하는데, 거기에 스냅이
    /// 걸리면 핸들이 커서를 안 따라온다. 구현을 잘 해도 안 없어지는 종류의 조작감이다.
    /// </summary>
    private void HandleKeys()
    {
        Event e = Event.current;

        if (e.type != EventType.KeyDown)
            return;

        if (_shapeMode && _editCell != null)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    CommitEdit();
                    e.Use();
                    Repaint();
                    return;

                case KeyCode.Delete:
                case KeyCode.Backspace:
                    // 마지막에 집었던 점을 뺀다. 셋 미만으로는 안 내려간다 - 거기서
                    // 더 빼면 모양이 아니게 되고, 그 상태를 저장할 수가 없다.
                    if (_editPoints.Count > 3 && _dragVertex >= 0 && _dragVertex < _editPoints.Count)
                    {
                        _editPoints.RemoveAt(_dragVertex);
                        _dragVertex = -1;
                    }

                    e.Use();
                    Repaint();
                    return;

                case KeyCode.Escape:
                    CancelEdit();
                    e.Use();
                    Repaint();
                    return;
            }
        }

        if (_shapeMode && _shapeCell != null)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    CommitShape();
                    e.Use();
                    Repaint();
                    return;

                case KeyCode.Backspace:
                    if (_shapePoints.Count > 0)
                        _shapePoints.RemoveAt(_shapePoints.Count - 1);

                    e.Use();
                    Repaint();
                    return;

                case KeyCode.Escape:
                    CancelShape();
                    e.Use();
                    Repaint();
                    return;
            }
        }

        if (_powerMode && _wireInProgress.Count > 0)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    CommitWire();
                    e.Use();
                    Repaint();
                    return;

                case KeyCode.Backspace:
                    _wireInProgress.RemoveAt(_wireInProgress.Count - 1);
                    e.Use();
                    Repaint();
                    return;

                case KeyCode.Escape:
                    _wireInProgress.Clear();
                    e.Use();
                    Repaint();
                    return;
            }
        }

        // Ctrl+Z / Ctrl+Y. 선택이 없어도 돌아가야 하므로 아래 검사보다 위에 둔다.
        // **Z/Y가 아니면 흘려보낸다** - 통째로 return하면 Ctrl+방향키(정밀 이동)가
        // 여기서 죽는다.
        if (e.control || e.command)
        {
            if (e.keyCode == KeyCode.Z) { Step(_undo, _redo); e.Use(); return; }
            if (e.keyCode == KeyCode.Y) { Step(_redo, _undo); e.Use(); return; }
        }

        // Q/E: 브러시 90도 회전(Shift면 45). 모듈은 배치 rot이 곧 마운트 방향이라
        // (0 앞, 90 위, -90 아래) 놓기 전에 돌려야 한다. 판 브러시에도 같은 키.
        if (e.keyCode == KeyCode.Q || e.keyCode == KeyCode.E)
        {
            float degrees = e.shift ? 45f : 90f;
            float next = _brushRot + (e.keyCode == KeyCode.Q ? degrees : -degrees);
            _brushRot = Mathf.Repeat(next + 180f, 360f) - 180f;
            _status = $"브러시 {_brushRot:0.#}도  (Q 반시계 / E 시계, Shift = 45도)";
            e.Use();
            Repaint();
            return;
        }

        if (_selected == null)
            return;

        Dictionary<Vector2Int, Placed> store = SelectedStore;

        if (store == null || !store.TryGetValue(_selected.Value, out Placed p))
            return;

        bool size = e.shift;

        // Ctrl은 모양 점과 같은 규칙이다: 0.01. 눈금 반올림도 같은 눈금으로 해야
        // 한다 - 0.05로 되돌리면 0.01씩 네 번 밀어도 제자리다.
        bool fine = e.control || e.command;
        float step = fine ? FineStep : NudgeStep;

        Vector2 screen = Vector2.zero;
        float turn = 0f;

        // **화면 델타다.** Unity의 Vector2.up은 (0,1)인데 화면에서 y는 아래로 가므로
        // 위로 미는 것은 (0,-1)이다. Vector2.up을 그대로 쓰면 여기서 한 번,
        // ScreenNudgeToShip에서 또 한 번 뒤집혀 위아래가 서로 바뀐다.
        //
        // Alt+좌우가 회전인 것은 한글 입력 상태 때문이다. IME가 켜져 있으면 Unity가
        // 대괄호 키의 keyCode를 None으로 주고 문자만 넘긴다 - 방향키는 IME를 안 타서
        // 언제나 온다. 대괄호도 올 때는 받는다.
        switch (e.keyCode)
        {
            case KeyCode.LeftArrow:
                if (e.alt) turn = -TurnStep; else screen = new Vector2(-1f, 0f);
                break;
            case KeyCode.RightArrow:
                if (e.alt) turn = TurnStep; else screen = new Vector2(1f, 0f);
                break;
            case KeyCode.UpArrow:    screen = new Vector2(0f, -1f); break;
            case KeyCode.DownArrow:  screen = new Vector2(0f, 1f);  break;
            case KeyCode.LeftBracket:  turn = -TurnStep; break;
            case KeyCode.RightBracket: turn = TurnStep;  break;

            default:
                if (e.character == '[') turn = -TurnStep;
                else if (e.character == ']') turn = TurnStep;
                else return;
                break;
        }

        Push();

        if (size)
        {
            // size 0은 "def 값을 쓴다"라, 0에서 더하면 def 크기를 잃는다. 처음 만질 때
            // def 값을 꺼내 와서 거기서 시작한다.
            ThingDef def = DefDatabase.Get(p.def);
            Vector2 now = p.size != Vector2.zero
                ? p.size
                : (def != null ? def.collider.size : Vector2.one);

            // 크기에는 방향이 없다. 화면에서 위/오른쪽이 늘리는 쪽이고, 화면 위는
            // screen.y가 음수라 부호를 뒤집어 읽는다.
            now = new Vector2(
                Mathf.Max(step, now.x + screen.x * step),
                Mathf.Max(step, now.y - screen.y * step));

            p = new Placed(p.def, p.rot, now, p.offset, p.shape);
        }
        else if (turn != 0f)
        {
            p = new Placed(p.def, p.rot + ScreenTurnToShip(turn), p.size, p.offset, p.shape);
        }
        else
        {
            p = new Placed(p.def, p.rot, p.size, p.offset + ScreenNudgeToShip(screen) * step, p.shape);
        }

        p = Tidy(p, step);
        store[_selected.Value] = p;

        // 짝도 같이 움직인다. 안 그러면 한쪽만 다듬고 반대쪽이 옛날 각도로 남는데,
        // 대칭인 배는 그게 눈에 잘 안 띈다.
        if (_mirror)
        {
            Vector2Int other = Across(_selected.Value);

            if (other != _selected.Value && store.ContainsKey(other))
                store[other] = Mirrored(p);
        }

        // 브러시도 따라간다. 같은 각도·같은 자리로 옆 칸을 이어 찍는 것이 실제 작업
        // 순서고, offset이 안 따라오면 이음매가 판마다 벌어진다.
        _brushRot = p.rot;
        _brushSize = p.size;
        _brushOffset = p.offset;

        e.Use();
        Repaint();
    }

    /// <summary>
    /// 선택한 칸에서 여기까지 한 줄로 잇는다. Shift+클릭.
    ///
    /// **칸마다 정확히 한 장이다.** 한 칸에 두 장을 두면 <see cref="HullStructure"/>의
    /// 생존 장부(칸을 열쇠로 하는 집합)가 먼저 죽는 판에 칸을 통째로 비워서, 멀쩡한
    /// 판이 서 있는데 격자에 구멍이 뚫린다. 긴 판 한 장으로 여러 칸을 가로지르는 것도
    /// 같은 이유로 안 된다 - 격자에는 한 칸만 찍히고 나머지는 빈 칸이라 방이 샌다.
    ///
    /// 그래도 눈에는 연속된 직선으로 보인다. 각도가 같은 판이 이어지면 이음매가 안
    /// 보인다 - cruiser의 뱃머리가 이미 45도 판 11장이다.
    /// </summary>
    /// <summary>
    /// 칸 좌표계 방향 하나를 판의 rot으로.
    ///
    /// rot은 판의 긴 축 방향이 아니라 거기서 90도 뺀 값이다. 긴 축은 로컬 y라 회전
    /// 전에 이미 +90도를 보고 있기 때문이다. 기준점은 mirror다: 그 배 판 775장의
    /// rot이 정확히 반지름 각도이고 긴 축은 접선(반지름+90)이다.
    ///
    /// 선각은 배 좌표계로 잰다 - row가 아래로 증가하므로 y를 뒤집어 넣는다.
    /// </summary>
    private static float RotAlong(Vector2 cellDir)
    {
        float rot = Mathf.Atan2(-cellDir.y, cellDir.x) * Mathf.Rad2Deg - 90f;

        // 상자는 180도 대칭이라 어느 쪽으로 적어도 같은 도형이다. 사람이 읽을 값으로
        // 접어 둔다 - -135도와 45도가 같은 것인 줄 모르면 JSON을 훑을 때 계속 헷갈린다.
        while (rot <= -90f) rot += 180f;
        while (rot > 90f) rot -= 180f;

        return rot;
    }

    private void Line(Vector2Int from, Vector2Int to)
    {
        int dx = to.x - from.x;
        int dy = to.y - from.y;
        int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

        if (steps == 0)
            return;

        // **rot은 판의 긴 축 방향이 아니라 거기서 90도 뺀 값이다.** 긴 축은 로컬 y라
        // 회전 전에 이미 +90도를 보고 있다. 이걸 빼먹으면 45도에서만 맞는다 - 45와
        // -45는 상자에 대해 같은 방향이라 눈에 안 띈다. 기준점은 mirror의 판 775장이다.
        float rot = RotAlong(new Vector2(dx, dy));

        // 칸 하나가 먹는 선의 길이. 45도면 √2, 2:1이면 √5/2다. 이걸 안 맞추면 판
        // 사이가 벌어지거나 겹친다.
        float span = new Vector2(dx, dy).magnitude / steps;

        ThingDef def = DefDatabase.Get(_brush);
        float width = _brushSize.x > 0f ? _brushSize.x : (def != null ? def.collider.size.x : 1f);
        var size = new Vector2(width, span);

        Vector2 u = new Vector2(dx, dy).normalized;

        // **사슬 판정은 칸을 놓기 전에 해야 한다.** 루프 뒤에서 지우면 방금 넣은 칸이
        // 통째로 사라져서, 다음 선이 그 칸을 모르고 덮어쓴다 - 예각이 잘려나가던
        // 원인이 이 순서 하나였다.
        if (_jointCell != from || _chainStart == null)
        {
            _chainCells.Clear();
            _chainStart = from;
            _chainStartDir = u;
            _chainStartSpan = span;
            _chainStartWidth = width;
        }

        for (int i = 0; i <= steps; i++)
        {
            // 선 위의 **균등한** 지점. 반올림한 것이 도장을 찍을 칸이고, 반올림하면서
            // 버린 나머지가 그대로 offset이 된다.
            //
            // 칸 중심을 선에 **투영**하면 안 된다 - 판이 선 위에 오기는 하는데 간격이
            // 안 고르다. 3:1 선의 칸들은 선상 위치가 0, 0.949, 2.213, 3.162라 간격이
            // 0.949와 1.264를 오가는데 판 길이는 1.054 하나뿐이라 붙었다 벌어졌다 한다.
            float t = (float)i / steps;
            var exact = new Vector2(dx * t, dy * t);

            var cell = new Vector2Int(
                from.x + Mathf.RoundToInt(exact.x),
                from.y + Mathf.RoundToInt(exact.y));

            Vector2 shift = exact - new Vector2(cell.x - from.x, cell.y - from.y);

            // 칸 좌표계의 변위를 배 좌표계로. y만 부호가 바뀐다.
            var put = new Placed(_brush, rot, size, new Vector2(shift.x, -shift.y));

            // 사슬이 이미 쓴 칸이면 덮어쓰지 않고 합친다. 예각으로 꺾을 때 두 줄이
            // 겹치는 칸이 여기로 온다 - 덮어쓰면 앞선 줄이 이음매 근처에서 잘려나간다.
            if (_chainCells.Contains(cell))
                put = MergeInto(cell, put);

            Paint(cell, put);
            _chainCells.Add(cell);

            if (_mirror)
            {
                Vector2Int other = Across(cell);

                if (other != cell)
                    Paint(other, Mirrored(put));
            }
        }

        // 직전 선이 바로 이 칸에서 끝났으면 그 칸을 두 선의 다리로 바꾼다.
        // **선을 다 그린 뒤에 한다** - 먼저 놓으면 이 선의 첫 판이 덮어쓴다.
        if (_jointCell == from)
            Bridge(from, _jointDir, _jointSpan, _jointWidth, u, span, width);

        // 사슬이 닫혔다. 시작 칸에서는 이 선이 들어오고 첫 선이 나간다.
        if (_chainStart == to && _chainCells.Count > 1)
            Bridge(to, u, span, width, _chainStartDir, _chainStartSpan, _chainStartWidth);

        _jointCell = to;
        _jointDir = u;
        _jointSpan = span;
        _jointWidth = width;

        _brushRot = rot;
        _brushSize = size;
        _selected = to;
    }

    /// <summary>
    /// 이미 사슬이 쓴 칸에 새 판을 얹을 때, 둘의 볼록 껍질로 합친다.
    ///
    /// 예각에서는 두 줄이 몇 칸을 공유한다. 한 칸에 판이 하나라 합집합을 그대로 담을 수
    /// 없지만, 그 자리의 합집합은 쐐기 모양이라 껍질이 거의 같다. 덮어써서 앞선 줄이
    /// 통째로 사라지는 것보다 훨씬 낫다.
    /// </summary>
    private Placed MergeInto(Vector2Int cell, Placed put)
    {
        if (!_plates.TryGetValue(cell, out Placed had) || had.def != put.def)
            return put;

        Vector2[] a = GridPoints(cell, had);
        Vector2[] b = GridPoints(cell, put);

        var all = new Vector2[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);

        int hull = Ballistics.ConvexHull(all, all.Length, all);

        return hull >= 3 ? FromGridPolygon(all, hull, cell) : put;
    }

    private static Vector2[] SubArray(Vector2[] src, int count)
    {
        var view = new Vector2[count];

        for (int i = 0; i < count; i++)
            view[i] = src[i];

        return view;
    }

    /// <summary>판의 꼭짓점을 격자 좌표로. 모양이 없으면 회전한 콜라이더의 네 귀퉁이.</summary>
    private static Vector2[] GridPoints(Vector2Int cell, Placed p)
    {
        if (p.shape != null && p.shape.Length >= 3)
        {
            var pts = new Vector2[p.shape.Length];

            for (int i = 0; i < pts.Length; i++)
                pts[i] = ToGrid(cell, p.offset + p.shape[i]);

            return pts;
        }

        // size 0은 "def 값을 쓴다"다. 1x1로 때우면 0.7071짜리 경사판 같은 def의
        // 귀퉁이가 엉뚱한 자리에 잡혀서 스냅이 안 붙는 것처럼 보인다.
        ThingDef def = DefDatabase.Get(p.def);
        Vector2 half = (p.size != Vector2.zero
            ? p.size
            : (def != null ? def.collider.size : Vector2.one)) * 0.5f;

        return new[]
        {
            ToGrid(cell, p.offset + Ballistics.Rotate(new Vector2(-half.x, -half.y), p.rot)),
            ToGrid(cell, p.offset + Ballistics.Rotate(new Vector2(half.x, -half.y), p.rot)),
            ToGrid(cell, p.offset + Ballistics.Rotate(new Vector2(half.x, half.y), p.rot)),
            ToGrid(cell, p.offset + Ballistics.Rotate(new Vector2(-half.x, half.y), p.rot)),
        };
    }

    /// <summary>
    /// 두 선이 만나는 칸을 다리로 바꾼다. 사각형이 아니라 **사다리꼴**이다.
    ///
    /// 직선 구간은 회전한 직사각형으로 충분하다 - 각도와 길이만 맞으면 칸마다 정확히
    /// 맞물린다. 안 되는 것은 꺾이는 칸 하나뿐이고, 거기서만 폴리곤이 필요하다.
    ///
    /// 사각형으로 다리를 놓으면 끝면이 자기 축에 수직이라 이웃한 줄의 축과 각을 이루고,
    /// 그 모서리가 바깥으로 삐져나온다(가시). 사다리꼴은 **들어온 줄의 끝면 두 점과
    /// 나가는 줄의 시작면 두 점을 그대로 잇는다** - 두 줄이 무엇이든 정확히 맞는다.
    ///
    /// 곧게 이어지면 그 사다리꼴이 정확히 직사각형이 된다. 특수 경우를 안 만들어도
    /// 되는 이유이고, 식이 맞는지 보는 제일 빠른 확인이기도 하다.
    /// </summary>
    private void Bridge(Vector2Int cell, Vector2 inDir, float inSpan, float inWidth,
                        Vector2 outDir, float outSpan, float outWidth)
    {
        // 선의 양 끝점은 offset이 0이다 - t가 0과 1일 때 exact가 정수라 버릴 나머지가
        // 없다. 그래서 꼭짓점은 정확히 칸 중심이다.
        var mid = new Vector2(cell.x + 0.5f, cell.y + 0.5f);

        Vector2 endA = mid - inDir * inSpan * 0.5f;
        Vector2 endB = mid + outDir * outSpan * 0.5f;

        var nA = new Vector2(-inDir.y, inDir.x) * (inWidth * 0.5f);
        var nB = new Vector2(-outDir.y, outDir.x) * (outWidth * 0.5f);

        // 감기 순서가 중요하다. A의 양쪽 -> B의 양쪽으로 돌아야 나비 모양이 안 된다.
        var quad = new[] { endA + nA, endB + nB, endB - nB, endA - nA };

        // **예각에서는 그 순서로도 꼬인다.** 두 방향이 거의 반대면 변이 서로 교차해서
        // 나비가 되고, 넓이가 상쇄돼 거의 0이 된다 - 증상은 다리 자리에 실오라기만
        // 남는 것이다. 꼬였으면 볼록 껍질로 바꾼다. 예각의 합집합은 쐐기라 껍질이
        // 거의 같다.
        float straight = Ballistics.PolygonArea(quad);
        int hull = Ballistics.ConvexHull(quad, quad.Length, quad);

        Placed put = hull >= 3 && Ballistics.PolygonArea(SubArray(quad, hull)) > straight * 1.01f
            ? FromGridPolygon(quad, hull, cell)
            : FromGridPolygon(new[] { endA + nA, endB + nB, endB - nB, endA - nA }, 4, cell);

        _plates[cell] = put;

        if (_mirror)
        {
            Vector2Int other = Across(cell);

            if (other != cell)
                _plates[other] = Mirrored(put);
        }
    }

    /// <summary>
    /// 시작점으로 쓸 판을 고른다. 선택된 칸이 아직 판이면 그대로, 아니면 클릭 지점
    /// 둘레에서 제일 가까운 판.
    ///
    /// **클릭한 칸 자신은 제외한다** - 거기서 시작하면 길이 0짜리 선이 된다.
    /// </summary>
    private Vector2Int? NearestPlate(Vector2Int? selected, Vector2Int clicked)
    {
        if (selected != null && selected.Value != clicked && _plates.ContainsKey(selected.Value))
            return selected;

        Vector2Int? best = null;
        float bestDist = float.MaxValue;

        foreach (Vector2Int cell in _plates.Keys)
        {
            if (cell == clicked)
                continue;

            float d = (cell - clicked).sqrMagnitude;

            if (d < bestDist)
            {
                bestDist = d;
                best = cell;
            }
        }

        // 너무 멀면 안 붙인다. 화면 반대편 판에 붙으면 배를 가로지르는 선이 그어진다.
        return bestDist <= AnchorSnapRange * AnchorSnapRange ? best : null;
    }

    /// <summary>
    /// 격자 좌표 폴리곤 -> 그 칸에 놓을 판. 바운딩 박스가 콜라이더가 되고 점은
    /// 그 중심 기준으로 다시 적힌다. <see cref="CommitShape"/>와 같은 규칙이다.
    /// </summary>
    private Placed FromGridPolygon(Vector2[] grid, int count, Vector2Int cell)
    {
        var pts = new Vector2[count];
        Vector2 lo = Vector2.positiveInfinity;
        Vector2 hi = Vector2.negativeInfinity;

        for (int i = 0; i < count; i++)
        {
            // 격자 -> 칸 좌표계. row가 아래로 증가하므로 y만 부호가 바뀐다.
            pts[i] = new Vector2(
                grid[i].x - (cell.x + 0.5f),
                (cell.y + 0.5f) - grid[i].y);

            lo = Vector2.Min(lo, pts[i]);
            hi = Vector2.Max(hi, pts[i]);
        }

        Vector2 size = Vector2.Max(hi - lo, new Vector2(ShapeStep, ShapeStep));
        Vector2 centre = (lo + hi) * 0.5f;

        for (int i = 0; i < count; i++)
            pts[i] -= centre;

        return new Placed(_brush, 0f, size, centre, pts);
    }

    /// <summary>
    /// 점 하나 추가. 첫 점이 칸을 정하고, 그 뒤로는 **같은 칸 안에서만** 받는다.
    ///
    /// 칸을 넘어가는 점을 받으면 폴리곤이 이웃 칸까지 뻗는데, 그림은 콜라이더 밖을
    /// 안 그리고 잠김 비율도 콜라이더 안에서만 재므로 그 부분은 조용히 잘린다.
    /// 안 보이는 데이터를 만드는 것보다 못 찍게 하는 편이 낫다.
    /// </summary>
    private void AddShapePoint(Vector2 screen)
    {
        // **격자 좌표로 쌓는다** - 칸 번호가 곧 정수인 공간. 이음매를 가리는 모양은
        // 한 칸을 반드시 넘으므로 칸 안으로 가둘 수 없다. 콜라이더 크기와 자리는
        // 닫을 때 점들의 바운딩 박스에서 나온다.
        // Ctrl은 눈금을 0.01로 낮추고 자석도 끈다. 자석이 살아 있으면 칸 모서리
        // 근처에서 세밀하게 찍는 것이 아예 불가능하다 - 그 근처가 정확히 세밀함이
        // 필요한 자리다.
        bool fine = Event.current != null && (Event.current.control || Event.current.command);

        _shapePoints.Add(SnapPoint(new Vector2(
            (screen.x - _pan.x) / _zoom,
            (screen.y - _pan.y) / _zoom), fine));
    }

    /// <summary>
    /// 모양 모드의 왼쪽 클릭 하나. 상태에 따라 세 가지 중 하나다.
    ///
    /// 편집 중이면 점을 집거나 변에 점을 끼우고, 새 모양을 찍는 중이면 점을 쌓고,
    /// 아무것도 아니면 클릭한 칸을 보고 **정한다** - 폴리곤 판이면 열어서 고치고,
    /// 아니면 그 칸을 새 모양의 자리로 잡는다. 더블클릭 같은 것을 안 만든 이유는
    /// 클릭 한 번으로 갈리는 것이 이미 명확하기 때문이다.
    /// </summary>
    private void ShapeClick(Vector2Int cell, Vector2 screen)
    {
        // **포커스를 뺏는다.** 툴바의 배 이름 필드가 쥐고 있으면 GUI가 Return을 먼저
        // 삼켜서 Enter가 영영 안 온다 - 증상은 "점은 찍히는데 안 닫힌다"다.
        GUI.FocusControl(null);

        if (_editCell != null)
        {
            GrabVertex(screen);
            return;
        }

        if (_shapeCell != null)
        {
            AddShapePoint(screen);
            return;
        }

        if (_plates.TryGetValue(cell, out Placed had) && had.shape != null && had.shape.Length >= 3)
        {
            OpenForEdit(cell, had);
            return;
        }

        // **첫 클릭은 칸을 고르는 것이고 점을 안 찍는다.** 무게중심으로 칸을 정하면
        // 모양을 조금 옮길 때마다 도장이 이웃 칸으로 건너뛰어서, 멀쩡히 있던 판을
        // 덮어쓴다. 어느 칸을 차지할지는 사람이 먼저 못 박아야 한다.
        _shapeCell = cell;
        _shapePoints.Clear();
        _status = $"({cell.x},{cell.y}) 칸에 얹을 모양을 찍어라. 점은 칸을 넘어가도 된다.";
    }

    /// <summary>저장된 모양을 격자 좌표로 펼친다.</summary>
    private void OpenForEdit(Vector2Int cell, Placed placed)
    {
        _editCell = cell;
        _editPoints.Clear();

        foreach (Vector2 pt in placed.shape)
            _editPoints.Add(ToGrid(cell, placed.offset + pt));

        _status = $"({cell.x},{cell.y}) 모양 편집. 점을 끌고, 변을 클릭해 끼우고, Delete로 뺀다.";
    }

    /// <summary>칸 좌표계(칸 중심 기준, y 위) -> 격자 좌표(칸 번호가 정수, y 아래).</summary>
    private static Vector2 ToGrid(Vector2Int cell, Vector2 local)
        => new(cell.x + 0.5f + local.x, cell.y + 0.5f - local.y);

    /// <summary>
    /// 점을 집는다. 점 근처가 아니면 제일 가까운 변에 새 점을 끼우고 그것을 집는다.
    /// 변에서도 멀면 아무 일도 안 한다 - 편집 중에 빈 곳을 눌렀다고 모양이 바뀌면
    /// 되돌리기를 계속 눌러야 한다.
    /// </summary>
    private void GrabVertex(Vector2 screen)
    {
        const float grabPixels = 8f;

        for (int i = 0; i < _editPoints.Count; i++)
        {
            if ((GridToScreen(_editPoints[i]) - screen).sqrMagnitude <= grabPixels * grabPixels)
            {
                _dragVertex = i;
                return;
            }
        }

        int best = -1;
        float bestDist = grabPixels * grabPixels;
        Vector2 bestPoint = Vector2.zero;

        for (int i = 0; i < _editPoints.Count; i++)
        {
            Vector2 a = GridToScreen(_editPoints[i]);
            Vector2 b = GridToScreen(_editPoints[(i + 1) % _editPoints.Count]);

            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(screen - a, ab) / ab.sqrMagnitude) : 0f;
            Vector2 on = a + ab * t;
            float d = (on - screen).sqrMagnitude;

            if (d < bestDist)
            {
                bestDist = d;
                best = i;
                bestPoint = on;
            }
        }

        if (best < 0)
            return;

        var grid = new Vector2(
            (bestPoint.x - _pan.x) / _zoom,
            (bestPoint.y - _pan.y) / _zoom);

        _editPoints.Insert(best + 1, grid);
        _dragVertex = best + 1;
    }

    private void DragVertex(Vector2 screen)
    {
        if (_dragVertex < 0 || _dragVertex >= _editPoints.Count)
            return;

        Event e = Event.current;
        bool fine = e != null && (e.control || e.command);

        var raw = new Vector2(
            (screen.x - _pan.x) / _zoom,
            (screen.y - _pan.y) / _zoom);

        _editPoints[_dragVertex] = SnapPoint(raw, fine);
    }

    /// <summary>
    /// 점을 붙일 자리. 우선순위가 곧 규칙이다:
    ///
    ///   1) 다른 판의 꼭짓점 (0.1) - 사각형 판의 귀퉁이 포함
    ///   2) 다른 판의 변 중점 (0.1, 꼭짓점에 밀린다)
    ///   3) 정수 칸 꼭짓점 (0.1)
    ///   4) 반칸 격자 - 변 중점과 칸 중심 (0.08)
    ///   5) 0.05 눈금
    ///
    /// 판이 먼저인 이유: 이음매가 안 보이려면 두 판이 **정확히 같은 좌표**를 공유해야
    /// 하는데, 눈금만으로는 한 눈금 어긋나고 그 한 눈금이 곧 보이는 틈이다. 변 중점이
    /// 꼭짓점에 밀리는 이유: 짧은 변에서는 둘이 0.25 안에 같이 들어오는데, 중점을
    /// 잡으려던 사람은 드물고 꼭짓점을 잡으려던 사람이 흔하다.
    ///
    /// 반칸 자석이 정수 자석보다 좁은 이유(0.12): 반칸 격자는 간격이 0.5라 자석을
    /// 0.2로 주면 화면 대부분이 자석 범위가 되어 자유 배치(0.05)로 갈 길이 없어진다.
    /// </summary>
    private Vector2 SnapPoint(Vector2 grid, bool fine)
    {
        if (fine)
            return Snap(grid, FineStep);

        const float plateRange = 0.1f * 0.1f;

        // 1) 꼭짓점, 2) 변 중점. 두 패스로 돌아 꼭짓점이 이긴다.
        for (int pass = 0; pass < 2; pass++)
        {
            Vector2 best = Vector2.zero;
            float bestDist = plateRange;
            bool found = false;

            foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
            {
                if (pair.Key == _editCell)
                    continue;

                Vector2[] pts = GridPoints(pair.Key, pair.Value);

                for (int i = 0; i < pts.Length; i++)
                {
                    Vector2 g = pass == 0 ? pts[i] : (pts[i] + pts[(i + 1) % pts.Length]) * 0.5f;
                    float d = (g - grid).sqrMagnitude;

                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = g;
                        found = true;
                    }
                }
            }

            if (found)
                return best;
        }

        // 3) 정수 칸 꼭짓점
        var corner = new Vector2(Mathf.Round(grid.x), Mathf.Round(grid.y));

        if ((corner - grid).sqrMagnitude <= MagnetRange * MagnetRange)
            return corner;

        // 4) 반칸 격자 - 칸 변의 중점과 칸 중심이 전부 여기에 있다
        var half = new Vector2(Mathf.Round(grid.x * 2f) * 0.5f, Mathf.Round(grid.y * 2f) * 0.5f);

        if ((half - grid).sqrMagnitude <= HalfMagnetRange * HalfMagnetRange)
            return half;

        // 5) 눈금
        return Snap(grid, ShapeStep);
    }

    /// <summary>
    /// 편집을 닫는다. bbox에서 콜라이더 크기와 자리를 다시 뽑는다.
    /// </summary>
    private void CommitEdit()
    {
        if (_editCell == null)
            return;

        if (_editPoints.Count < 3)
        {
            _status = "점이 셋은 있어야 모양이 된다. Delete를 너무 눌렀다.";
            return;
        }

        Push();

        Vector2Int cell = _editCell.Value;
        Placed put = FromGridPolygon(_editPoints.ToArray(), _editPoints.Count, cell);

        _plates[cell] = put;

        // **CommitShape와 같은 규칙이다.** 새로 그리기에는 있는데 모양 수정에만 없어서,
        // 대칭을 켜고 점을 옮기면 반대쪽 판이 옛 모양 그대로 남았다. 판을 놓는 자리는
        // 전부 이 세 줄을 갖는다(Paint, 선 긋기, CommitShape).
        if (_mirror)
        {
            Vector2Int other = Across(cell);

            if (other != cell)
                _plates[other] = Mirrored(put);
        }

        _selected = cell;
        _status = $"({cell.x},{cell.y}) 모양 저장. 점 {_editPoints.Count}개.";

        CancelEdit();
    }

    private void CancelEdit()
    {
        _editCell = null;
        _editPoints.Clear();
        _dragVertex = -1;
    }

    /// <summary>찍은 점들을 그 칸의 판에 얹는다.</summary>
    private void CommitShape()
    {
        if (_shapeCell == null)
            return;

        if (_shapePoints.Count < 3)
        {
            _status = "점이 셋은 있어야 모양이 된다.";
            return;
        }

        // 도장은 **처음에 고른 칸**에 찍는다. 점이 어디로 뻗든 이 칸 하나만 차지한다.
        Vector2Int cell = _shapeCell.Value;

        // 격자 좌표 -> 칸 좌표계(칸 중심 기준, y는 위로). row가 아래로 증가하므로
        // y만 부호가 바뀐다.
        var pts = new Vector2[_shapePoints.Count];
        Vector2 lo = Vector2.positiveInfinity;
        Vector2 hi = Vector2.negativeInfinity;

        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = new Vector2(
                _shapePoints[i].x - (cell.x + 0.5f),
                (cell.y + 0.5f) - _shapePoints[i].y);

            lo = Vector2.Min(lo, pts[i]);
            hi = Vector2.Max(hi, pts[i]);
        }

        // **바운딩 박스가 곧 콜라이더다.** 폴리곤이 칸을 넘어가도 콜라이더가 그만큼
        // 커지므로 그림도 잠김 비율도 잘리지 않는다 - Armor는 콜라이더 안에서만
        // 모양을 읽는다. 격자는 여전히 이 판을 칸 하나로만 본다(위치만 읽으므로).
        Vector2 size = Vector2.Max(hi - lo, new Vector2(ShapeStep, ShapeStep));
        Vector2 centre = (lo + hi) * 0.5f;

        // 점은 콜라이더 중심 기준으로 다시 적는다. Armor.BakeShape가 그 공간을 읽는다.
        for (int i = 0; i < pts.Length; i++)
            pts[i] -= centre;

        Push();

        // 회전은 0이다. 폴리곤이 모양을 다 말하므로 같이 쓰면 "이 판이 왜 이 모양인가"의
        // 답이 두 군데로 갈린다.
        var put = new Placed(_brush, 0f, size, centre, pts);

        _plates[cell] = put;

        if (_mirror)
        {
            Vector2Int other = Across(cell);

            if (other != cell)
                _plates[other] = Mirrored(put);
        }

        _selected = cell;
        _status = $"({cell.x},{cell.y})에 점 {_shapePoints.Count}개짜리 모양을 얹었다.";

        CancelShape();
    }

    private void CancelShape()
    {
        _shapeCell = null;
        _shapePoints.Clear();
    }

    /// <summary>
    /// 판 한 장 놓기. **덮어쓰는 것이면 말한다.**
    ///
    /// 한 칸에는 판이 한 장이다 - <see cref="HullStructure"/>의 생존 장부가 칸을 열쇠로
    /// 하는 집합이라, 한 칸에 둘을 두면 먼저 죽는 쪽이 칸을 통째로 비워서 멀쩡한 판이
    /// 서 있는데도 격자에 구멍이 뚫린다. 그래서 도구가 뒤엣것만 남기는 것은 맞다.
    ///
    /// 틀린 것은 **조용한 것**이었다. 겹쳐 찍은 줄 모르면 앞서 그린 판이 사라진 것을
    /// 한참 뒤에 발견하고, 그때는 무엇이 있었는지 기억이 안 난다.
    /// </summary>
    private void Paint(Vector2Int cell, Placed put)
    {
        if (_plates.TryGetValue(cell, out Placed had) && !Same(had, put))
        {
            _status = $"({cell.x},{cell.y})에 있던 {had.def}를 덮어썼다."
                    + "  한 칸에 판 한 장이다 - 되돌리려면 Ctrl+Z.";
        }

        _plates[cell] = put;
    }

    /// <summary>
    /// 모듈이 있으면 모듈만 지운다. 판까지 같이 지우면 포탑 하나 빼려다 갑판에 구멍이 난다 -
    /// 판을 지우려면 한 번 더 누른다.
    /// </summary>
    private bool _eraseModulesOnly;

    private void Erase(Vector2Int cell)
    {
        _modules.Remove(cell);

        if (_eraseModulesOnly)
            return;

        _plates.Remove(cell);

        if (_selected == cell)
            _selected = null;
    }

    private static bool Same(Placed a, Placed b)
        => a.def == b.def && a.rot == b.rot && a.size == b.size && a.offset == b.offset;

    /// <summary>전력망 모드에서 전기와 무관한 것은 채도를 빼고 어둡게 - 전선과 기기만 눈에 남는다.</summary>
    private Color Muted(Color c)
    {
        if (!_powerMode)
            return c;

        float grey = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        return new Color(grey * 0.45f, grey * 0.45f, grey * 0.45f, c.a);
    }

    private static Color PlateColour(string def) => def switch
    {
        "Ballistic Door" => new Color(0.85f, 0.72f, 0.30f),
        "Glass" => new Color(0.45f, 0.78f, 0.85f),
        "Lance Armor" => new Color(0.62f, 0.71f, 0.86f),
        "Armor mk3" => new Color(0.88f, 0.89f, 0.90f),
        "Armor mk4" => new Color(0.80f, 0.81f, 0.80f),
        "Armor mk6" => new Color(0.55f, 0.60f, 0.68f),
        _ => new Color(0.70f, 0.71f, 0.72f),
    };

    // 이름이 아니라 종류로 - "Reactor Mini"·"Mini Engine"이 이름 표에서 빠져 있었다.
    private static bool IsReactor(string def)
    {
        ThingDef thing = DefDatabase.Get(def);
        return thing?.MainType != null
            && typeof(CriticalModule).IsAssignableFrom(thing.MainType)
            && (JsonUtility.FromJson<PowerNums>(thing.raw)?.providesPower ?? false);
    }

    private static bool IsEngine(string def)
    {
        System.Type t = DefDatabase.Get(def)?.MainType;
        return t != null && typeof(Engine).IsAssignableFrom(t);
    }

    private static bool IsGun(string def)
    {
        System.Type t = DefDatabase.Get(def)?.MainType;
        return t != null && typeof(Gun).IsAssignableFrom(t);
    }

    /// <summary>전력망 모드에서만 놓이는 것. 릴레이·퓨즈가 오면 여기 늘어난다.</summary>
    private static bool IsElectrical(string def) => IsReactor(def);

    /// <summary>전선이 붙는 것. 전기를 내거나 먹는 기기.</summary>
    private static bool IsDevice(string def) => IsElectrical(def) || IsGun(def);

    // =========================================================
    // 전선
    // =========================================================

    private void DrawWires(Rect clip)
    {
        var on = new Color(1f, 0.78f, 0.35f, _powerMode ? 1f : 0.35f);

        foreach (List<Vector2> w in _wires)
            DrawWire(w, on);

        if (_wireInProgress.Count == 0)
            return;

        var mark = new Color(0.4f, 0.9f, 1f);
        DrawWire(_wireInProgress, mark);

        // 마지막 점에서 커서까지 미리보기.
        if (_brushIsWire && clip.Contains(Event.current.mousePosition))
            Line(GridToScreen(_wireInProgress[_wireInProgress.Count - 1]),
                 GridToScreen(WireSnap(Event.current.mousePosition)), new Color(0.4f, 0.9f, 1f, 0.5f));
    }

    private void DrawWire(List<Vector2> w, Color c)
    {
        for (int i = 0; i < w.Count; i++)
        {
            Vector2 p = GridToScreen(w[i]);
            EditorGUI.DrawRect(new Rect(p.x - 2f, p.y - 2f, 5f, 5f), c);

            if (i > 0)
                Line(GridToScreen(w[i - 1]), p, c);
        }
    }

    /// <summary>화면 → 격자, 1/6 m 눈금. 기기 칸 위면 그 칸 중심 - 런타임이 기기를 칸으로 찾는다.</summary>
    private Vector2 WireSnap(Vector2 screen)
    {
        Vector2Int cell = CellAt(screen);

        if (_modules.TryGetValue(cell, out Placed m) && IsDevice(m.def))
            return new Vector2(cell.x + 0.5f, cell.y + 0.5f);

        return Snap(new Vector2((screen.x - _pan.x) / _zoom, (screen.y - _pan.y) / _zoom), WireStep);
    }

    private void WireClick(Vector2 screen)
    {
        GUI.FocusControl(null);

        Vector2 p = WireSnap(screen);
        Vector2Int cell = CellAt(screen);
        bool onDevice = _modules.TryGetValue(cell, out Placed m) && IsDevice(m.def);

        if (_wireInProgress.Count > 0 && p == _wireInProgress[_wireInProgress.Count - 1])
            return;

        _wireInProgress.Add(p);

        if (onDevice && _wireInProgress.Count >= 2)
            CommitWire();
        else
            _status = onDevice
                ? $"({cell.x},{cell.y})에서 전선 시작."
                : $"전선 점 {_wireInProgress.Count}. 기기를 클릭하거나 Enter로 끝낸다.";
    }

    private void CommitWire()
    {
        if (_wireInProgress.Count < 2)
        {
            _wireInProgress.Clear();
            return;
        }

        Push();
        _wires.Add(new List<Vector2>(_wireInProgress));
        _wireInProgress.Clear();
        _status = $"전선 {_wires.Count}개.";
    }

    private void EraseWireAt(Vector2 screen)
    {
        var g = new Vector2((screen.x - _pan.x) / _zoom, (screen.y - _pan.y) / _zoom);
        float best = 0.25f * 0.25f;   // 1/4 칸 안
        int hit = -1;

        for (int i = 0; i < _wires.Count; i++)
            for (int j = 1; j < _wires[i].Count; j++)
            {
                float d = DistToSegment(g, _wires[i][j - 1], _wires[i][j]);
                if (d < best) { best = d; hit = i; }
            }

        if (hit < 0)
            return;

        Push();
        _wires.RemoveAt(hit);
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return (p - (a + ab * t)).sqrMagnitude;
    }

    /// <summary>
    /// 끝점이 기기 칸 **중심**이거나 다른 전선의 꼭짓점과 같은 자리인가. 런타임(Ship.Power)이 같은
    /// 규칙이다 - 칸 위 아무 데나로 하면 경계에 걸친 점이 반올림 방향에 따라 붙었다 안 붙었다 한다.
    /// 같은 자리는 허용오차로 본다 - 저장이 소수 4자리라 다시 읽은 점과 방금 찍은 점이 비트로는 다르다.
    /// </summary>
    private bool WireEndAttached(Vector2 p, List<Vector2> self)
    {
        const float eps = 1e-3f;
        var cell = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
        var centre = new Vector2(cell.x + 0.5f, cell.y + 0.5f);

        if (_modules.TryGetValue(cell, out Placed m) && IsDevice(m.def) && (p - centre).sqrMagnitude <= eps * eps)
            return true;

        foreach (List<Vector2> w in _wires)
        {
            if (w == self) continue;
            foreach (Vector2 q in w)
                if ((q - p).sqrMagnitude <= eps * eps)
                    return true;
        }

        return false;
    }

    private string WiresJson()
    {
        var sb = new System.Text.StringBuilder("[");

        for (int i = 0; i < _wires.Count; i++)
        {
            sb.Append(i == 0 ? "\n    { \"points\": [" : ",\n    { \"points\": [");

            for (int j = 0; j < _wires[i].Count; j++)
                sb.Append(j == 0 ? "" : ", ").Append(Json(_wires[i][j] - new Vector2(0.5f, 0.5f)));

            sb.Append("] }");
        }

        sb.Append(_wires.Count == 0 ? "]" : "\n  ]");
        return sb.ToString();
    }

    [System.Serializable] private class PowerNums { public bool providesPower; }

    private static Color ModuleColour(string def)
    {
        System.Type t = DefDatabase.Get(def)?.MainType;
        if (IsReactor(def)) return new Color(0.35f, 0.85f, 0.45f);
        if (t != null && typeof(CriticalModule).IsAssignableFrom(t)) return new Color(0.90f, 0.35f, 0.30f);
        if (IsEngine(def)) return new Color(0.95f, 0.60f, 0.25f);
        if (t != null && typeof(Tank).IsAssignableFrom(t)) return new Color(0.85f, 0.80f, 0.30f);
        return new Color(0.55f, 0.75f, 0.95f);
    }

    // =========================================================
    // 밀폐 판정
    // =========================================================

    private bool Bounds(out Vector2Int min, out Vector2Int max)
    {
        min = max = default;

        if (_plates.Count == 0)
            return false;

        int c0 = int.MaxValue, c1 = int.MinValue, r0 = int.MaxValue, r1 = int.MinValue;

        foreach (Vector2Int cell in _plates.Keys)
        {
            c0 = Mathf.Min(c0, cell.x); c1 = Mathf.Max(c1, cell.x);
            r0 = Mathf.Min(r0, cell.y); r1 = Mathf.Max(r1, cell.y);
        }

        // 판 바깥으로 한 겹. 그래야 테두리가 반드시 빈 칸이라 flood가 씨앗을 얻는다.
        min = new Vector2Int(c0 - 1, r0 - 1);
        max = new Vector2Int(c1 + 1, r1 + 1);
        return true;
    }

    /// <summary>
    /// 지금 찍힌 판으로 진짜 격자를 한 장 만든다.
    ///
    /// **밀폐 규칙을 여기서 다시 짜지 않는다.** 예전에는 이 창이 flood를 자기 손으로 돌렸는데,
    /// 그러면 규칙이 두 곳에 산다 - 게임 쪽이 바뀌는 날 창만 옛 규칙으로 남아서 "창은
    /// 밀폐됐다는데 게임은 샌다"가 된다. 실물 칸만 찍어 넘기면 <see cref="ShipGrid.MarkExterior"/>가
    /// 나머지를 정한다.
    ///
    /// 문(Door)도 실물이라 공기를 막는다. 그래서 판이든 문이든 <see cref="ShipGrid.Cell.Wall"/>로
    /// 찍는다 - 창이 답해야 하는 질문은 "여기가 막혀 있나"뿐이고, 문의 여닫힘은 런타임의 일이다.
    /// </summary>
    private ShipGrid.Map BuildMap(out Vector2Int min)
    {
        min = default;

        if (!Bounds(out min, out Vector2Int max))
            return null;

        var map = new ShipGrid.Map(
            max.x - min.x + 1, max.y - min.y + 1, Vector2.zero);

        foreach (Vector2Int cell in _plates.Keys)
            map.cells[cell.x - min.x, cell.y - min.y] = ShipGrid.Cell.Wall;

        ShipGrid.MarkExterior(map);
        return map;
    }

    private HashSet<Vector2Int> Exterior()
    {
        var outside = new HashSet<Vector2Int>();
        ShipGrid.Map map = BuildMap(out Vector2Int min);

        if (map == null)
            return outside;

        for (int col = 0; col < map.width; col++)
        for (int row = 0; row < map.height; row++)
        {
            if (map.cells[col, row] == ShipGrid.Cell.Exterior)
                outside.Add(new Vector2Int(col + min.x, row + min.y));
        }

        return outside;
    }

    private List<Vector2Int> Interior(HashSet<Vector2Int> exterior)
    {
        var inside = new List<Vector2Int>();

        if (!Bounds(out Vector2Int min, out Vector2Int max))
            return inside;

        for (int col = min.x; col <= max.x; col++)
        for (int row = min.y; row <= max.y; row++)
        {
            var cell = new Vector2Int(col, row);

            if (!_plates.ContainsKey(cell) && !exterior.Contains(cell))
                inside.Add(cell);
        }

        return inside;
    }

    /// <summary>
    /// 모듈이 얹힐 판. 자기 칸에 판이 있으면 그것, 없으면 4방향 이웃 중 첫 판이다.
    /// 없으면 false - 그런 모듈은 배를 지을 때 조용히 안 생긴다.
    /// </summary>
    /// <summary>마지막 캔버스 그리기 때의 실내 칸. 그리기가 한 번 돌아야 채워진다 - 한 프레임 늦는 것은 무해하다.</summary>
    private readonly HashSet<Vector2Int> _interiorCache = new();

    /// <summary>지금 브러시로 놓을 모듈. offset은 자유값이다 - 격자에 맞추는 것은 없다.</summary>
    private Placed ModuleBrush() => new(_brush, _brushRot, _brushSize, _brushOffset);

    private readonly List<ModulePlacement.Plate> _platePolys = new();
    private readonly List<ModulePlacement.Other> _modulePolys = new();

    /// <summary>
    /// FitOf가 읽는 실내 칸·판 폴리곤·모듈 폴리곤을 지금 상태로. 캔버스 그리기와 Validate
    /// 둘 다 여기서 시작한다 - 불러온 직후 Validate가 지난 배의 캐시로 판정해서 페인터는
    /// 조용한데 런타임만 경고하던 것이 그 때문이다.
    /// </summary>
    private HashSet<Vector2Int> RefreshFitCaches()
    {
        HashSet<Vector2Int> exterior = Exterior();

        _interiorCache.Clear();
        _interiorCache.UnionWith(Interior(exterior));

        _platePolys.Clear();

        foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
        {
            Placed p = pair.Value;
            Vector2[] poly = ModulePlacement.PlatePolygon(pair.Key, p.def, p.rot, p.size, p.offset, p.shape);
            _platePolys.Add(new ModulePlacement.Plate { cell = pair.Key, poly = poly, area = Mathf.Abs(Ballistics.PolygonArea(poly)) });
        }

        _modulePolys.Clear();

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
        {
            Placed p = pair.Value;
            _modulePolys.Add(new ModulePlacement.Other
            {
                origin = pair.Key,
                poly = ModulePlacement.ModulePolygon(pair.Key, p.def, p.rot, p.size, p.offset, out _, out _),
            });
        }

        return exterior;
    }

    /// <summary>이 모듈이 이 원점에 설 수 있나. 판정은 ModulePlacement.Evaluate 하나다 - 런타임과 같다.</summary>
    private ModulePlacement.Result FitOf(Vector2Int origin, Placed placed)
        => ModulePlacement.Evaluate(
            placed.def, origin, placed.rot, placed.size, placed.offset,
            c => _plates.ContainsKey(c) || _interiorCache.Contains(c),
            _platePolys, _modulePolys);

    /// <summary>
    /// 모듈이 얹힐 판. 자기 칸에 판이 있으면 그것, 모듈이면 덮인 넓이가 제일 큰 판(FitOf),
    /// 둘 다 아니면 4방향 이웃 중 첫 판(모양 도구가 쓴다).
    /// </summary>
    private bool MountFor(Vector2Int cell, out Vector2Int mount)
    {
        if (_plates.ContainsKey(cell))
        {
            mount = cell;
            return true;
        }

        if (_modules.TryGetValue(cell, out Placed under))
        {
            ModulePlacement.Result fit = FitOf(cell, under);
            mount = fit.mount;
            return fit.hasMount;
        }

        foreach (Vector2Int dir in new[] { Vector2Int.down, Vector2Int.up, Vector2Int.right, Vector2Int.left })
        {
            if (_plates.ContainsKey(cell + dir))
            {
                mount = cell + dir;
                return true;
            }
        }

        mount = default;
        return false;
    }

    private string Validate()
    {
        if (_plates.Count == 0)
            return "판이 하나도 없다.";

        HashSet<Vector2Int> exterior = RefreshFitCaches();
        int inside = Interior(exterior).Count;

        var floating = new List<Vector2Int>();
        bool reactor = false, engine = false;

        int buried = 0;

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
        {
            ModulePlacement.Result fit = FitOf(pair.Key, pair.Value);

            if (!fit.Allowed)
                floating.Add(pair.Key);
            else if (fit.verdict == ModulePlacement.Verdict.Buried)
                buried++;

            if (IsReactor(pair.Value.def)) reactor = true;
            if (IsEngine(pair.Value.def)) engine = true;
        }

        Bounds(out Vector2Int min, out Vector2Int max);

        var notes = new List<string>
        {
            $"{max.x - min.x - 1}x{max.y - min.y - 1}칸",
            $"판 {_plates.Count}",
            $"실내 {inside}",
        };

        int loose = 0;
        foreach (List<Vector2> w in _wires)
            if (!WireEndAttached(w[0], w) || !WireEndAttached(w[w.Count - 1], w))
                loose++;

        notes.Add($"전선 {_wires.Count}");

        if (loose > 0)
            notes.Add($"<!> 끝이 기기에 안 닿은 전선 {loose}개");

        List<Vector2Int> spill = Overhanging();

        if (spill.Count > 0)
            notes.Add($"<!> 칸을 1칸 넘게 벗어난 판 {spill.Count}개: {Cells(spill)}");

        if (inside == 0) notes.Add("<!> 밀폐 안 됨 - 공기가 없다");
        if (floating.Count > 0) notes.Add($"<!> 자리가 안 맞는 모듈 {floating.Count}개: {Cells(floating)}");
        if (buried > 0) notes.Add($"배 안에 묻힌 포·엔진 {buried}개(관통 전엔 안 맞음)");
        if (!reactor) notes.Add("<!> 원자로 없음 - 조타·조준이 멈춘다");
        if (!engine) notes.Add("<!> 엔진 없음");

        return string.Join("   ", notes);
    }

    /// <summary>
    /// 콜라이더가 자기 칸과 8방향 이웃을 벗어나는 판. 칸 중심 기준 각 축 ±1.5 m가 한도다.
    ///
    /// **넘기는 것 자체는 허용한다.** 막으면 각도 표현이 막힌다 - 45도 판을 칸 안에
    /// 완전히 넣으려면 판이 작아져서 이웃과 사이에 틈이 생긴다.
    ///
    /// 한도가 1칸인 것은 취향이 아니라 디버깅 가능성의 경계다. CLAUDE.md가 감수하기로
    /// 적어 둔 비용이 있다 - "2x2 판이 덮은 이웃 칸이 격자상 비어 있으면 방은 그리로
    /// 이어진다. 버그가 아니라 이 결정." 1칸이면 그 영향이 인접 칸에 갇혀서 눈으로
    /// 좇을 수 있다. 2칸부터는 어느 판이 어느 방을 이어붙였는지 알 수 없어지고, 기압이
    /// 새야 할 곳에서 안 새는 일이 판 스무 장 떨어진 곳에서 원인을 갖는다.
    ///
    /// **자동으로 안 고친다.** 어느 칸인지만 말한다 - 고치는 방법이 각도를 줄이는 것일
    /// 수도 판을 옮기는 것일 수도 있어서, 도구가 고르면 반드시 틀린 쪽을 고른다.
    /// </summary>
    private List<Vector2Int> Overhanging()
    {
        var over = new List<Vector2Int>();

        foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
        {
            if (IsOverhanging(pair.Value))
                over.Add(pair.Key);
        }

        return over;
    }

    /// <summary>
    /// 판 하나가 한도를 넘었나. **목록에서 쪼개 둔 이유는 그림이 매 프레임 부르기
    /// 때문이다** - 선택한 판 하나만 실시간으로 보면 되는데, 목록을 부르면 mirror의
    /// 판 776장에 매 프레임 삼각함수가 돈다.
    /// </summary>
    private static bool IsOverhanging(Placed placed)
    {
        ThingDef def = DefDatabase.Get(placed.def);

        if (def == null)
            return false;

        Vector2 size = placed.size != Vector2.zero ? placed.size : def.collider.size;

        if (size.x <= 0f || size.y <= 0f)
            return false;

        float r = placed.rot * Mathf.Deg2Rad;
        float c = Mathf.Abs(Mathf.Cos(r));
        float sn = Mathf.Abs(Mathf.Sin(r));

        // 회전한 사각형의 AABB. size 그대로 재면 45도 1x1 판의 반폭을 0.5로 보는데
        // 실제로는 0.707이라, 한도를 넘은 판을 통과시킨다.
        var half = new Vector2(
            (size.x * c + size.y * sn) * 0.5f,
            (size.x * sn + size.y * c) * 0.5f);

        // 칸 중심에서 콜라이더 중심까지. ThingDef.Spawn이 box.offset에
        // (def.offset + Rotate(placement.offset, -rot))을 넣으므로, 그것을 다시
        // +rot으로 돌리면 def 몫만 회전하고 배치 몫은 그대로 남는다.
        //
        // 여기 값들은 배 좌표계다(CLAUDE.md: rot도 offset도 배 좌표계로 적는다).
        // 이 검사에는 상관없다 - 한도가 ±1.5로 대칭이라 y 부호가 뒤집혀도 결과가
        // 같다. **부호가 실제로 무는 곳은 그림이다** (DrawPlate 참고).
        Vector2 centre = Ballistics.Rotate(def.collider.offset, placed.rot) + placed.offset;

        return Mathf.Abs(centre.x) + half.x > 1.5f || Mathf.Abs(centre.y) + half.y > 1.5f;
    }

    /// <summary>목록이 길면 앞의 몇 개만. 콘솔이 아니라 한 줄짜리 상태 표시라서.</summary>
    private static string Cells(List<Vector2Int> cells)
    {
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < cells.Count && i < 6; i++)
            sb.Append(i > 0 ? ", " : "").Append('(').Append(cells[i].x).Append(',').Append(cells[i].y).Append(')');

        if (cells.Count > 6)
            sb.Append(" 외 ").Append(cells.Count - 6).Append("개");

        return sb.ToString();
    }

    // =========================================================
    // 입출력
    // =========================================================

    private void Load()
    {
        string path = ShipDef.PathOf(_shipName);

        if (!File.Exists(path))
        {
            _status = $"없는 파일: {path}";
            return;
        }

        // 그림 규격 검사는 끄고 연다. 안 맞는 건 상태줄로만 - 여기서 판을 고쳐 맞추는 것이니까.
        ShipDef def = ShipDef.Load(_shipName, checkSkin: false);

        if (def == null)
        {
            _status = "읽기 실패. 콘솔을 봐라.";
            return;
        }

        string skinNote = ShipDef.SkinProblem(def) is string bad ? $"  <!> 그림 '{def.hullSkin}': {bad}" : "";

        _plates.Clear();
        _modules.Clear();

        foreach (Placement p in def.placements)
        {
            ThingDef thing = DefDatabase.Get(p.def);
            var cell = new Vector2Int(p.col, p.row);

            // **배치가 들고 있던 것을 전부 물려받는다.** def 이름만 집으면 여기서 잃고
            // 저장할 때 0으로 덮어쓴다 - 그 사이에 아무 경고도 안 난다.
            var placed = new Placed(p.def, p.rot, p.size, p.offset, p.shape);

            if (thing?.MainType != null && typeof(Armor).IsAssignableFrom(thing.MainType))
                _plates[cell] = placed;
            else
                _modules[cell] = placed;
        }

        _wires.Clear();
        _wireInProgress.Clear();

        if (def.wires != null)
            foreach (Wire w in def.wires)
                if (w?.points != null && w.points.Count >= 2)
                    _wires.Add(w.points.ConvertAll(p => p + new Vector2(0.5f, 0.5f)));

        _status = Validate() + skinNote;
        Repaint();
    }

    /// <summary>
    /// **배 수치는 안 건드린다.** 파일이 이미 있으면 `placements` 값만 갈아끼운다 -
    /// drag나 angleAccel은 손으로 튜닝한 숫자라 통째로 다시 쓰면 조용히 사라진다.
    /// <see cref="ShipDef.Save"/>가 같은 이유로 같은 짓을 한다.
    /// </summary>
    private void Save()
    {
        if (_plates.Count == 0)
        {
            _status = "판이 없어서 저장 안 한다.";
            return;
        }

        var body = new System.Text.StringBuilder();
        body.Append("[\n");

        bool first = true;

        foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
            Append(body, ref first, pair.Value, pair.Key, new Vector2Int(-1, -1));

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
        {
            // 붙을 판이 없어도 내보낸다(mount -1). 후면 위 모듈은 선체 직속이 정상이다 -
            // 예전에는 여기서 빠져서 방 한가운데 원자로가 JSON에 안 나갔다.
            if (!MountFor(pair.Key, out Vector2Int mount))
                mount = new Vector2Int(-1, -1);

            Append(body, ref first, pair.Value, pair.Key, mount);
        }

        body.Append("\n  ]");

        string path = ShipDef.PathOf(_shipName);
        Directory.CreateDirectory(ShipDef.DirectoryPath);

        string text = File.Exists(path)
            ? DefKeys.ReplaceTopLevelValue(File.ReadAllText(path), "placements", body.ToString())
            : Template(_shipName, body.ToString());

        if (string.IsNullOrEmpty(text))
        {
            _status = "기존 파일에서 placements를 못 찾았다. 파일을 확인해라.";
            return;
        }

        // 전선은 설계도에 없던 키일 수 있어서 끼워 넣는다 - rearLost와 같은 사정.
        text = DefKeys.UpsertTopLevelValue(text, "wires", WiresJson()) ?? text;

        File.WriteAllText(path, text);
        AssetDatabase.Refresh();

        _status = $"{path}에 썼다. {Validate()}"
                + RoundTrip();
    }

    /// <summary>
    /// 방금 쓴 파일을 다시 읽어 화면의 것과 대조한다.
    ///
    /// **이 도구가 조용히 데이터를 지운 적이 있어서 있는 검사다.** 값 타입이 def 이름만
    /// 들던 시절, 배를 열었다 저장만 해도 rot / size / offset이 전부 0이 됐다. 배는
    /// 여전히 지어지고 방도 선체도 정상이라 아무 검사에도 안 걸렸고, 증상은 "언제부터
    /// 모양이 이랬지"였다. mirror 776장, lance 56장이 그렇게 날아갈 뻔했다.
    ///
    /// 격자만 비교하면 이걸 못 잡는다 - 각도와 콜라이더는 칸에 도장을 안 찍는다.
    /// 그래서 배치를 값으로 대조한다.
    ///
    /// 저장 직후에 부르는 것이 요점이다. 사람이 따로 눌러야 하는 검사는 하필 급할 때
    /// 안 눌린다.
    /// </summary>
    private string RoundTrip()
    {
        ShipDef back = ShipDef.Load(_shipName);

        if (back == null)
            return "   <!> 왕복 실패: 방금 쓴 파일을 다시 못 읽는다";

        var seen = new Dictionary<Vector2Int, Placement>();

        foreach (Placement p in back.placements)
        {
            if (!p.IsMounted)
                seen[new Vector2Int(p.col, p.row)] = p;
        }

        int lost = 0;

        foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
        {
            if (!seen.TryGetValue(pair.Key, out Placement p) || !Same(p, pair.Value))
                lost++;
        }

        return lost > 0
            ? $"   <!> 왕복 실패: 판 {lost}개가 달라졌다"
            : "   왕복 통과";
    }

    /// <summary>
    /// 같은 배치인가. 각도는 도 단위라 0.01도면 충분하고, 크기·offset은 m 단위다.
    /// float 그대로 비교하면 "0.1을 썼는데 0.099999가 돌아왔다"로 영원히 실패한다.
    /// </summary>
    private static bool Same(Placement p, Placed placed)
        => p.def == placed.def
        && Mathf.Abs(Mathf.DeltaAngle(p.rot, placed.rot)) < 0.01f
        && (p.size - placed.size).sqrMagnitude < 1e-6f
        && (p.offset - placed.offset).sqrMagnitude < 1e-6f
        && SameShape(p.shape, placed.shape);

    /// <summary>
    /// 점 개수와 순서까지 같아야 같은 모양이다. **점 하나가 조용히 빠지는 것이 제일
    /// 무섭다** - 사각형이 삼각형이 돼도 배는 그대로 지어지고 방도 정상이다.
    /// </summary>
    private static bool SameShape(Vector2[] a, Vector2[] b)
    {
        int na = a?.Length ?? 0;
        int nb = b?.Length ?? 0;

        // 점이 셋 미만이면 둘 다 "모양 없음"이다 - Armor가 그렇게 읽는다.
        if (na < 3 && nb < 3)
            return true;

        if (na != nb)
            return false;

        for (int i = 0; i < na; i++)
        {
            if ((a[i] - b[i]).sqrMagnitude > 1e-6f)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 배치 한 줄. **size와 offset은 0이면 아예 안 쓴다.**
    ///
    /// 미관이 아니라 읽힘 때문이다. 판 241장이 전부 자기 크기를 적고 있으면 진짜로 특별한
    /// 자리가 어디인지 안 보이고, 기존 파일과 diff가 통째로 나서 무엇이 실제로 바뀌었는지
    /// 검토할 수가 없다. ShipExporter.CaptureCollider가 같은 규칙을 쓴다.
    ///
    /// rot은 0이어도 쓴다. 기존 파일이 전부 그 형식이라 여기만 빼면 diff가 난다.
    /// </summary>
    private static void Append(
        System.Text.StringBuilder sb, ref bool first, Placed placed, Vector2Int cell, Vector2Int mount)
    {
        if (!first)
            sb.Append(",\n");

        first = false;

        sb.Append("    { \"def\": \"").Append(placed.def)
          .Append("\", \"col\": ").Append(cell.x)
          .Append(", \"row\": ").Append(cell.y)
          .Append(", \"rot\": ").Append(Num(placed.rot));

        if (placed.size != Vector2.zero)
            sb.Append(", \"size\": ").Append(Json(placed.size));

        if (placed.offset != Vector2.zero)
            sb.Append(", \"offset\": ").Append(Json(placed.offset));

        if (placed.shape != null && placed.shape.Length >= 3)
        {
            sb.Append(", \"shape\": [");

            for (int i = 0; i < placed.shape.Length; i++)
                sb.Append(i > 0 ? ", " : "").Append(Json(placed.shape[i]));

            sb.Append(']');
        }

        sb.Append(", \"mountCol\": ").Append(mount.x)
          .Append(", \"mountRow\": ").Append(mount.y)
          .Append(" }");
    }

    /// <summary>문화권 소수점(1,5)이 섞이면 JSON이 통째로 깨진다. 이 창은 로케일을 안 탄다.</summary>
    private static string Num(float v)
        => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Json(Vector2 v) => $"{{ \"x\": {Num(v.x)}, \"y\": {Num(v.y)} }}";

    // =========================================================
    // 스킨 템플릿
    // =========================================================

    /// <summary>텍스처 한 변의 상한. 거울(241칸)을 뽑으려다 에디터가 죽는 걸 막는다.</summary>
    private const int MaxTemplateSide = 8192;

    /// <summary>
    /// 그림 그릴 밑판을 PNG로 뽑는다. 격자선과 판 실루엣이 깔린 빈 캔버스다.
    ///
    /// **크기 계산을 여기서 다시 하지 않는다.** <see cref="ShipDef.Bbox"/>와
    /// <see cref="ShipDef.PPU"/>를 그대로 쓴다 - 검증하는 쪽(<c>SkinIsValid</c>)과 같은
    /// 두 값이라야 "템플릿대로 그렸는데 배가 안 실린다"가 안 생긴다.
    ///
    /// bbox가 판이 아니라 **배치 전체**라는 점이 중요하다. 판보다 바깥에 얹힌 모듈(다리 밑
    /// 부스터 같은 것)이 캔버스를 한 칸 넓힌다.
    ///
    /// 파일 이름에 `_template`을 붙이는 것도 의도다. `_hull.png`로 뽑으면 hullSkin이 그걸
    /// 가리키고 있어서 **격자선이 그대로 함선 그림이 된다.**
    /// </summary>
    private void ExportTemplate()
    {
        ShipDef def = ShipDef.Load(_shipName);

        if (def == null)
        {
            _status = "설계도를 못 읽었다. 콘솔을 봐라.";
            return;
        }

        RectInt box = def.Bbox();
        int ppu = ShipDef.PPU;
        int width = box.width * ppu;
        int height = box.height * ppu;

        if (width > MaxTemplateSide || height > MaxTemplateSide)
        {
            _status = $"{width}x{height}는 너무 크다. {MaxTemplateSide} 넘는 건 안 뽑는다.";
            return;
        }

        // 칸 하나가 48x48이라 판마다 SetPixel을 부르면 136만 번이 된다. 배열 하나 채우고
        // 마지막에 한 번만 올린다.
        var pixels = new Color32[width * height];

        var plateFill = new Color32(255, 255, 255, 26);
        var moduleFill = new Color32(255, 150, 60, 90);
        var gridLine = new Color32(255, 255, 255, 64);

        foreach (Placement p in def.placements)
        {
            ThingDef thing = DefDatabase.Get(p.def);

            bool isPlate = thing?.MainType != null
                        && typeof(Armor).IsAssignableFrom(thing.MainType);

            if (isPlate)
            {
                // **실물 발자국대로 찍는다.** 칸 단위로 채우면 경사판도 폴리곤 판도
                // 꽉 찬 정사각형이 되고, 그 위에 그린 그림이 실제 실루엣과 어긋난다.
                // 템플릿의 존재 이유가 실루엣인데 그게 틀리면 안 된다.
                Vector2[] grid = GridPoints(
                    new Vector2Int(p.col, p.row),
                    new Placed(p.def, p.rot, p.size, p.offset, p.shape));

                RasterizePolygon(pixels, width, height, box, ppu, grid, plateFill);
            }
            else
            {
                // 모듈은 칸 채움 그대로. 템플릿은 선체 실루엣용이라 모듈은 자리만
                // 보이면 된다.
                Fill(pixels, width, height, box, p.col, p.row, ppu, moduleFill);
            }
        }

        // 격자선은 마지막에. 판 채움 위에 그어져야 칸 경계가 보인다.
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            // 텍스처 y는 아래에서 위로, 칸 row는 위에서 아래로. 위에서 센 줄로 되돌린다.
            int fromTop = height - 1 - y;

            if (x % ppu >= 2 && fromTop % ppu >= 2)
                continue;

            pixels[y * width + x] = gridLine;
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        texture.Apply(false);

        string dir = Path.Combine(Application.dataPath, "Art", "Templates~", "Ships");   // ~ = Unity가 임포트 안 함
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{_shipName}_template.png");
        File.WriteAllBytes(path, texture.EncodeToPNG());
        DestroyImmediate(texture);
        AssetDatabase.Refresh();

        _status = $"{width}x{height} 템플릿을 {path}에 썼다. "
                + $"({box.width}x{box.height}칸 x {ppu})";
    }

    /// <summary>
    /// 격자 좌표 폴리곤을 픽셀로 채운다. 픽셀 중심의 점 포함 판정 - 느리지만(판마다
    /// 바운딩 박스 픽셀 전수) 버튼 한 번짜리 에디터 일이라 상관없다.
    /// </summary>
    private static void RasterizePolygon(
        Color32[] pixels, int width, int height, RectInt box, int ppu,
        Vector2[] grid, Color32 colour)
    {
        if (grid == null || grid.Length < 3)
            return;

        float gx0 = float.MaxValue, gy0 = float.MaxValue, gx1 = float.MinValue, gy1 = float.MinValue;

        foreach (Vector2 g in grid)
        {
            gx0 = Mathf.Min(gx0, g.x); gy0 = Mathf.Min(gy0, g.y);
            gx1 = Mathf.Max(gx1, g.x); gy1 = Mathf.Max(gy1, g.y);
        }

        int x0 = Mathf.Max(0, Mathf.FloorToInt((gx0 - box.xMin) * ppu));
        int x1 = Mathf.Min(width - 1, Mathf.CeilToInt((gx1 - box.xMin) * ppu));
        int t0 = Mathf.Max(0, Mathf.FloorToInt((gy0 - box.yMin) * ppu));
        int t1 = Mathf.Min(height - 1, Mathf.CeilToInt((gy1 - box.yMin) * ppu));

        for (int fromTop = t0; fromTop <= t1; fromTop++)
        {
            // row는 아래로 증가하고 텍스처 y는 위로 증가한다. Fill과 같은 뒤집기다.
            int y = height - 1 - fromTop;
            int rowStart = y * width;
            float gy = box.yMin + (fromTop + 0.5f) / ppu;

            for (int x = x0; x <= x1; x++)
            {
                var g = new Vector2(box.xMin + (x + 0.5f) / ppu, gy);

                if (Ballistics.PolygonContains(grid, g))
                    pixels[rowStart + x] = colour;
            }
        }
    }

    /// <summary>
    /// 칸 하나가 차지하는 48x48 픽셀 블록을 칠한다.
    ///
    /// **row는 아래로 증가하고 텍스처 y는 위로 증가한다.** 안 뒤집으면 템플릿이 위아래로
    /// 뒤집혀 나오고, 그 위에 그린 그림이 통째로 뒤집힌 채 배에 붙는다.
    /// </summary>
    private static void Fill(
        Color32[] pixels, int width, int height, RectInt box,
        int col, int row, int ppu, Color32 colour)
    {
        int x0 = (col - box.xMin) * ppu;
        int yTop = (row - box.yMin) * ppu;          // 위에서 센 픽셀
        int y0 = height - yTop - ppu;               // 아래에서 센 픽셀

        for (int y = y0; y < y0 + ppu; y++)
        {
            if (y < 0 || y >= height)
                continue;

            int rowStart = y * width;

            for (int x = x0; x < x0 + ppu; x++)
            {
                if (x < 0 || x >= width)
                    continue;

                pixels[rowStart + x] = colour;
            }
        }
    }

    /// <summary>
    /// 새 배의 기본 수치. 프리깃급을 베껴 두고 JSON에서 손으로 맞추게 한다 -
    /// 창에서 고르게 만들면 이 창이 함선 편집기가 되어 버린다.
    /// </summary>
    private static string Template(string name, string placements) =>
$@"{{
  ""defName"": ""{name}"",
  ""basedOn"": ""{name}"",
  ""massPerPlate"": 350,
  ""drag"": 0.3,
  ""angleAccel"": 24,
  ""angleDrag"": 0.5,
  ""angleBrake"": 10,
  ""leakRate"": 2,
  ""doorRate"": 1,
  ""crews"": 3,
  ""FightDistance"": 180,
  ""DetectionDistance"": 300,
  ""breakawaySpeed"": 2,
  ""placements"": {placements}
}}";
}
#endif
