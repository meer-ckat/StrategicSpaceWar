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

        public Placed(string def, float rot = 0f, Vector2 size = default, Vector2 offset = default)
        {
            this.def = def;
            this.rot = rot;
            this.size = size;
            this.offset = offset;
        }
    }

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

    /// <summary>
    /// 브러시가 들고 다니는 offset. **마지막으로 만진 판에서 따라온다** - 뱃머리를
    /// 두를 때 옆 칸이 같은 자리에 서야 이음매가 안 벌어진다.
    /// </summary>
    private Vector2 _brushOffset;

    /// <summary>판을 채우지 않고 칸 경계선만 본다. 경사판이 칸을 가려서 어디가 어딘지 안 보일 때.</summary>
    private bool _gridOnly;

    /// <summary>
    /// 실행취소. **판과 모듈 사전을 통째로 복사해 쌓는다.**
    ///
    /// EditorWindow의 평범한 필드는 Unity의 Undo가 못 본다(ScriptableObject가 아니다).
    /// 조작마다 되돌리는 코드를 따로 쓰면 새 조작을 넣을 때마다 짝을 하나씩 잊는다 -
    /// 통째로 찍어 두면 그 실수가 존재할 자리가 없다. 배 한 척이 판 수백 장이라
    /// 복사가 아깝지 않다: 사람이 키를 누르는 속도로만 일어난다.
    /// </summary>
    private readonly List<(Dictionary<Vector2Int, Placed> plates, Dictionary<Vector2Int, Placed> modules)> _undo = new();
    private readonly List<(Dictionary<Vector2Int, Placed> plates, Dictionary<Vector2Int, Placed> modules)> _redo = new();

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

        foreach (string path in Directory.GetFiles(DefDatabase.DefDirectory, "*.json"))
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
                  || typeof(CriticalModule).IsAssignableFrom(def.MainType))
                _moduleDefs.Add(head.defName);
        }

        _plateDefs.Sort();
        _moduleDefs.Sort();
    }

    private void OnGUI()
    {
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
                _selected = null;
                _status = "";
            }

            _gridOnly = GUILayout.Toggle(_gridOnly, "격자만", EditorStyles.toolbarButton, GUILayout.Width(60f));

            GUILayout.Space(12f);
            GUILayout.Label(
                $"판 {_plates.Count}  모듈 {_modules.Count}"
                + (Mathf.Approximately(_brushRot, 0f) ? "" : $"   브러시 {_brushRot:0.#}도"),
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                "좌클릭 칠하기 / 우클릭 지우기 / Alt+클릭 스포이드 / 가운데 끌기 이동 / 휠 확대"
                + "   |   방향키 offset / Shift+방향키 크기 / Alt+좌우([ ]) 회전 / Ctrl+Z 되돌리기",
                EditorStyles.miniLabel);
        }
    }

    private void DrawPalette(Rect area)
    {
        GUILayout.BeginArea(area);
        _paletteScroll = GUILayout.BeginScrollView(_paletteScroll);

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
            if (GUILayout.Toggle(_brushIsModule && _brush == def, def, EditorStyles.miniButton))
            {
                _brush = def;
                _brushIsModule = true;
            }
        }

        GUILayout.Space(10f);

        bool editing = _selected != null && _plates.ContainsKey(_selected.Value);

        GUILayout.Label(
            editing ? $"선택한 판 {_selected.Value.x},{_selected.Value.y}" : "브러시",
            EditorStyles.boldLabel);

        float rot = _brushRot;
        Vector2 size = _brushSize;
        Vector2 offset = Vector2.zero;
        Placed sel = default;

        if (editing)
        {
            sel = _plates[_selected.Value];
            rot = sel.rot;
            size = sel.size;
            offset = sel.offset;
        }

        // 같은 필드가 선택 여부에 따라 다른 것을 편집한다. 필드를 두 벌 두면 어느 쪽이
        // 지금 유효한지가 화면에 안 나오고, 브러시를 고친 줄 알았는데 판이 바뀐다.
        rot = EditorGUILayout.FloatField("각도", rot);
        size = EditorGUILayout.Vector2Field("크기 (0=def)", size);

        using (new EditorGUI.DisabledScope(!editing))
            offset = EditorGUILayout.Vector2Field("offset", offset);

        if (editing)
        {
            if (rot != sel.rot || size != sel.size || offset != sel.offset)
            {
                Push();

                // **여기서는 눈금에 안 맞춘다.** Tidy는 키로 0.05씩 쌓을 때의 누적
                // 오차를 막는 것이고, 직접 친 값은 애초에 안 쌓인다. 여기에 걸면
                // 필드를 드래그해도 7.5도를 다 넘기기 전까지 같은 값으로 되돌아와서
                // "각도가 안 먹는다"가 된다.
                var next = new Placed(sel.def, rot, size, offset);
                _plates[_selected.Value] = next;

                _brushRot = next.rot;
                _brushSize = next.size;
                _brushOffset = next.offset;
            }
        }
        else
        {
            _brushRot = rot;
            _brushSize = size;
        }

        using (new GUILayout.HorizontalScope())
        {
            // 손으로 제일 많이 쓰는 값들. 자동 추론은 안 한다 - 두 칸 찍으면 사선을
            // 이어주는 기능은 배 한 척을 다 그려보고 뭐가 반복되는지 본 다음에 만든다.
            if (GUILayout.Button("0", EditorStyles.miniButtonLeft)) _brushRot = 0f;
            if (GUILayout.Button("+45", EditorStyles.miniButtonMid)) _brushRot = 45f;
            if (GUILayout.Button("-45", EditorStyles.miniButtonMid)) _brushRot = -45f;

            if (GUILayout.Button("기본크기", EditorStyles.miniButtonRight))
                _brushSize = Vector2.zero;
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

        HashSet<Vector2Int> exterior = Exterior();

        if (!_gridOnly)
        {
            foreach (KeyValuePair<Vector2Int, Placed> pair in _plates)
                DrawPlate(pair.Key, pair.Value, local);
        }
        else
        {
            // **격자만 모드는 위상을 보는 자리다.** 콜라이더는 안 그린다 - 각도도 크기도
            // offset도 방 구획에 아무 영향이 없고(격자는 판의 위치 하나만 읽는다),
            // 그것들을 같이 그리면 "어느 칸이 막혔나"가 안 보인다. 여기서 답해야 하는
            // 질문은 하나뿐이다: 방이 생기려면 어느 칸에 판을 더 놓아야 하나.
            foreach (Vector2Int cell in _plates.Keys)
                DrawCell(cell, PlateColour(_plates[cell].def), local);
        }

        // **채우기 뒤에 그린다.** 앞에 그리면 방 오버레이가 선을 덮어서 격자가 안 보인다.
        if (_gridOnly)
            DrawGrid(local);

        DrawSelection(local);

        // 실내는 판 위에 안 겹치므로 뒤에 그려도 된다. 판이 없는 칸만 칠한다.
        foreach (Vector2Int cell in Interior(exterior))
            DrawCell(cell, new Color(0.16f, 0.34f, 0.5f, 0.55f), local);

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
        {
            Rect r = CellRect(pair.Key);
            r = new Rect(r.x + r.width * 0.22f, r.y + r.height * 0.22f, r.width * 0.56f, r.height * 0.56f);

            if (r.Overlaps(local))
                EditorGUI.DrawRect(r, ModuleColour(pair.Value.def));
        }

        GUI.EndClip();
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

        Color colour = PlateColour(placed.def);

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
        if (_selected == null || !_plates.ContainsKey(_selected.Value))
            return;

        Rect r = CellRect(_selected.Value);

        if (!r.Overlaps(clip))
            return;

        bool over = IsOverhanging(_plates[_selected.Value]);
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
            _zoom = Mathf.Clamp(_zoom - e.delta.y, 6f, 48f);

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

        // 드래그 한 번이 조작 하나다. 매 MouseDrag마다 쌓으면 Ctrl+Z 한 번이 칸 하나만
        // 되돌리고, 붓질 한 번을 물리려면 스무 번을 눌러야 한다.
        if (e.type == EventType.MouseDown)
            Push();

        if (erase)
        {
            _modules.Remove(cell);
            _plates.Remove(cell);

            if (_selected == cell)
                _selected = null;
        }
        else if (_brushIsModule)
        {
            _modules[cell] = Tidy(new Placed(_brush, _brushRot, _brushSize, _brushOffset));
        }
        else
        {
            _plates[cell] = Tidy(new Placed(_brush, _brushRot, _brushSize, _brushOffset));
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

    private static Placed Tidy(Placed p) => new(
        p.def,
        Snap(p.rot, TurnStep),
        p.size == Vector2.zero ? Vector2.zero : Snap(p.size, NudgeStep),
        Snap(p.offset, NudgeStep));

    /// <summary>지금 상태를 실행취소 더미에 올린다. **바꾸기 전에** 부른다.</summary>
    private void Push()
    {
        _undo.Add((new Dictionary<Vector2Int, Placed>(_plates), new Dictionary<Vector2Int, Placed>(_modules)));

        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);

        // 새 조작이 들어오면 앞으로 갈 길은 사라진다. 안 지우면 되돌린 뒤 다른 것을
        // 하고 나서 Ctrl+Y를 눌렀을 때 없던 역사가 되살아난다.
        _redo.Clear();
    }

    private void Step(List<(Dictionary<Vector2Int, Placed> plates, Dictionary<Vector2Int, Placed> modules)> from,
                     List<(Dictionary<Vector2Int, Placed> plates, Dictionary<Vector2Int, Placed> modules)> to)
    {
        if (from.Count == 0)
            return;

        to.Add((new Dictionary<Vector2Int, Placed>(_plates), new Dictionary<Vector2Int, Placed>(_modules)));

        var snap = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);

        _plates.Clear();
        foreach (KeyValuePair<Vector2Int, Placed> pair in snap.plates)
            _plates[pair.Key] = pair.Value;

        _modules.Clear();
        foreach (KeyValuePair<Vector2Int, Placed> pair in snap.modules)
            _modules[pair.Key] = pair.Value;

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

        // Ctrl+Z / Ctrl+Y. 선택이 없어도 돌아가야 하므로 아래 검사보다 위에 둔다.
        if (e.control || e.command)
        {
            if (e.keyCode == KeyCode.Z) { Step(_undo, _redo); e.Use(); return; }
            if (e.keyCode == KeyCode.Y) { Step(_redo, _undo); e.Use(); return; }

            return;
        }

        if (_selected == null)
            return;

        if (!_plates.TryGetValue(_selected.Value, out Placed p))
            return;

        bool size = e.shift;
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
                Mathf.Max(NudgeStep, now.x + screen.x * NudgeStep),
                Mathf.Max(NudgeStep, now.y - screen.y * NudgeStep));

            p = new Placed(p.def, p.rot, now, p.offset);
        }
        else if (turn != 0f)
        {
            p = new Placed(p.def, p.rot + ScreenTurnToShip(turn), p.size, p.offset);
        }
        else
        {
            p = new Placed(p.def, p.rot, p.size, p.offset + ScreenNudgeToShip(screen) * NudgeStep);
        }

        p = Tidy(p);
        _plates[_selected.Value] = p;

        // 브러시도 따라간다. 같은 각도·같은 자리로 옆 칸을 이어 찍는 것이 실제 작업
        // 순서고, offset이 안 따라오면 이음매가 판마다 벌어진다.
        _brushRot = p.rot;
        _brushSize = p.size;
        _brushOffset = p.offset;

        e.Use();
        Repaint();
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

    private static Color ModuleColour(string def) => def switch
    {
        "Reactor" => new Color(0.35f, 0.85f, 0.45f),
        "Magazine" => new Color(0.90f, 0.35f, 0.30f),
        "SuperDuper Engine" => new Color(0.95f, 0.60f, 0.25f),
        _ => new Color(0.55f, 0.75f, 0.95f),
    };

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
    private bool MountFor(Vector2Int cell, out Vector2Int mount)
    {
        if (_plates.ContainsKey(cell))
        {
            mount = cell;
            return true;
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

        HashSet<Vector2Int> exterior = Exterior();
        int inside = Interior(exterior).Count;

        int orphan = 0;
        bool reactor = false, engine = false;

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
        {
            if (!MountFor(pair.Key, out _))
                orphan++;

            if (pair.Value.def == "Reactor") reactor = true;
            if (pair.Value.def == "SuperDuper Engine") engine = true;
        }

        Bounds(out Vector2Int min, out Vector2Int max);

        var notes = new List<string>
        {
            $"{max.x - min.x - 1}x{max.y - min.y - 1}칸",
            $"판 {_plates.Count}",
            $"실내 {inside}",
        };

        List<Vector2Int> spill = Overhanging();

        if (spill.Count > 0)
            notes.Add($"<!> 칸을 1칸 넘게 벗어난 판 {spill.Count}개: {Cells(spill)}");

        if (inside == 0) notes.Add("<!> 밀폐 안 됨 - 공기가 없다");
        if (orphan > 0) notes.Add($"<!> 붙을 판이 없는 모듈 {orphan}개");
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

        ShipDef def = ShipDef.Load(_shipName);

        if (def == null)
        {
            _status = "읽기 실패. 콘솔을 봐라.";
            return;
        }

        _plates.Clear();
        _modules.Clear();

        foreach (Placement p in def.placements)
        {
            ThingDef thing = DefDatabase.Get(p.def);
            var cell = new Vector2Int(p.col, p.row);

            // **배치가 들고 있던 것을 전부 물려받는다.** def 이름만 집으면 여기서 잃고
            // 저장할 때 0으로 덮어쓴다 - 그 사이에 아무 경고도 안 난다.
            var placed = new Placed(p.def, p.rot, p.size, p.offset);

            if (thing?.MainType != null && typeof(Armor).IsAssignableFrom(thing.MainType))
                _plates[cell] = placed;
            else
                _modules[cell] = placed;
        }

        _status = Validate();
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

        int orphan = 0;

        foreach (KeyValuePair<Vector2Int, Placed> pair in _modules)
        {
            if (!MountFor(pair.Key, out Vector2Int mount))
            {
                orphan++;
                continue;
            }

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

        File.WriteAllText(path, text);
        AssetDatabase.Refresh();

        _status = $"{path}에 썼다. {Validate()}"
                + (orphan > 0 ? $"  (붙을 판이 없어 뺀 모듈 {orphan}개)" : "")
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
        && (p.offset - placed.offset).sqrMagnitude < 1e-6f;

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

        // 칸 하나가 96x96이라 판마다 SetPixel을 부르면 543만 번이 된다. 배열 하나 채우고
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

            Fill(pixels, width, height, box, p.col, p.row, ppu,
                isPlate ? plateFill : moduleFill);
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

        string path = Path.Combine(ShipDef.DirectoryPath, $"{_shipName}_template.png");
        File.WriteAllBytes(path, texture.EncodeToPNG());
        DestroyImmediate(texture);
        AssetDatabase.Refresh();

        _status = $"{width}x{height} 템플릿을 {path}에 썼다. "
                + $"({box.width}x{box.height}칸 x {ppu})";
    }

    /// <summary>
    /// 칸 하나가 차지하는 96x96 픽셀 블록을 칠한다.
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
