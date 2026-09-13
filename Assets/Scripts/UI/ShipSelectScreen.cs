using System;
using System.Collections.Generic;
using System.IO;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// 함선 선택 화면. 첫 씬 로드에 열리고, 고르면 씬을 다시 열어 그 배로 시작한다.
///
/// **씬을 나누지 않는다** - 선택은 씬이 아니라 상태다. 여는 동안 TickManager.Paused와
/// Time.timeScale = 0으로 세계를 통째로 세워두고, 확정하면 검게 덮은 뒤 씬을 리로드한다.
/// 리로드 직후의 완전한 검정 1초(GameManager.BootFade)가 곧 "잠시 암전"이라 전환 연출이
/// 공짜다. 고른 defName은 static으로 살아남아 Ship.Awake가 읽는다 - 죽어서 재시작해도
/// 같은 배로 간다.
///
/// 목록은 StreamingAssets/Ships의 def 전부에서 crews > 0인 것만 - 운석·잔해·거울은
/// 승무원이 없어서 저절로 빠진다.
/// </summary>
public sealed class ShipSelectScreen : MonoBehaviour
{
    /// <summary>고른 배. null이면 아직 안 골랐다 = 선택 화면이 열린다.</summary>
    public static string Chosen { get; private set; }

    /// <summary>
    /// 선택을 잊는다. **런이 죽었을 때 부른다** - 다음 함장은 배부터 다시 고른다.
    ///
    /// Chosen이 static이라 씬 리로드에 살아남는 것이 원래 설계다(구역 사이 리로드에서
    /// 이 화면이 다시 열리면 안 되니까). 그런데 죽음 리로드도 같은 문을 지나가서,
    /// 안 지우면 **죽어도 같은 배로 바로 태어난다** - 고를 기회 자체가 없었다.
    /// </summary>
    public static void Forget() => Chosen = null;

    /// <summary>지금 선택 화면이 떠 있는가. Campaign.Start가 이걸 보고 런 시작을 미룬다.</summary>
    public static bool IsOpen { get; private set; }

    private sealed class Option
    {
        public string defName;
        public ShipDef def;
        public Header header;
        public string weapons;   // "M12 x4, rail x2"
        public float mass;       // kg
        public float topSpeed;   // m/s, 종단속도
        public float tankImpulse; // kN·s
        public Texture2D schematic;

        /// <summary>이 배를 연구하는 데 드는 점수. 배치 수 그대로 - 큰 배가 비싸다.</summary>
        public int researchCost;
    }

    // def의 raw JSON에서 배 수치만 읽는 그릇. ShipDef가 수치를 Ship에 바로 붓는 설계라
    // (Apply), 배를 안 만들고 수치를 보려면 같은 원문을 한 번 더 읽는 수밖에 없다 -
    // ShipDef 헤더가 쓰는 "같은 원문 두 번 읽기"와 같은 트릭이다.
    [Serializable]
    private sealed class Header
    {
        public float drag = 0.02f;
        public int crews;
        public float massPerPlate = 420f;
        public float FightDistance;
    }

    [Serializable] private sealed class EngineNums { public float MaxPower; }
    [Serializable] private sealed class TankNums { public float impulse; }

    // ------------------------------------------------------------
    // 연구 언락. 점수(RunState.Research)와 언락 둘 다 PlayerPrefs다 - 메타 진행이라
    // 런과 함께 죽지 않는다. 처음에 점수를 progress 파일에 뒀다가 "소비처는 죽어야
    // 열리는데 죽는 순간 지갑이 지워지는" 자기모순을 겪고 옮겼다.
    // ------------------------------------------------------------

    private const string UnlockKey = "research.ships";

    private static bool IsUnlocked(string defName)
    {
        string list = PlayerPrefs.GetString(UnlockKey, "");
        return ("," + list + ",").Contains("," + defName + ",");
    }

    private static void Unlock(string defName)
    {
        if (IsUnlocked(defName))
            return;

        string list = PlayerPrefs.GetString(UnlockKey, "");
        PlayerPrefs.SetString(UnlockKey, string.IsNullOrEmpty(list) ? defName : list + "," + defName);
        PlayerPrefs.Save();
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Run/연구 언락 초기화")]
    private static void ResetUnlocks()
    {
        PlayerPrefs.DeleteKey(UnlockKey);
        Debug.Log("[연구] 언락 목록을 지웠다. 기본 지급 배만 남는다.");
    }
#endif

    private readonly List<Option> _options = new();
    private int _selected;
    private bool _open;
    private float _scroll;
    private float _confirmedAt = -1f;   // unscaled. 0 이상이면 암전 중

    private const float FadeSeconds = 0.5f;
    private const int ScreenLayer = UiLayer.ShipSelect;    // 다른 ImGui 위에. 암전은 그 위.
    private const float RowHeight = 44f;

    private static GUIStyle _title, _rowStyle, _rowSelected, _statLabel, _statValue, _dim, _black, _backdrop;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindFirstObjectByType<ShipSelectScreen>() != null)
            return;

        var go = new GameObject("Ship Select");
        DontDestroyOnLoad(go);
        go.AddComponent<ShipSelectScreen>();
    }

    private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // Start가 아니라 Awake다 - 같은 프레임에 TickManager.Update가 먼저 돌면 로딩
    // 프레임의 큰 deltaTime이 한꺼번에 틱으로 터진 뒤에야 멈춘다.
    private void Awake() => Decide();

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Decide();

    /// <summary>아직 안 골랐으면 세계를 세우고 연다. 골랐으면 세워둔 것을 푼다.</summary>
    private void Decide()
    {
        if (Chosen == null)
            Open();
        else
            Close();
    }

    private void Open()
    {
        _open = true;
        IsOpen = true;
        _confirmedAt = -1f;
        Core.TickManager.Paused = true;
        Time.timeScale = 0f;

        if (_options.Count == 0)
            BuildOptions();

        // **열릴 때마다 다시 읽는다.** BuildOptions에서 한 번만 채웠더니, 이 오브젝트가
        // DontDestroyOnLoad라 죽음 뒤에도 지난 런의 배가 남았다 - RunState.Clear로 저장은
        // 지워졌는데 캐시가 "그 배 몰던 중"이라고 우겨서, 죽은 런의 배가 계속 해금으로
        // 보였다. 저장이 진실이고 이 값은 그 사본이다 - 사본은 볼 때마다 새로 뜬다.
        ShipDef saved = RunState.Load();

        _sailing = saved == null ? null
            : string.IsNullOrEmpty(saved.basedOn) ? saved.defName : saved.basedOn;
    }

    private void Close()
    {
        _open = false;
        IsOpen = false;
        Core.TickManager.Paused = false;
        Time.timeScale = 1f;
    }

    private void BuildOptions()
    {
        foreach (string path in Directory.GetFiles(ShipDef.DirectoryPath, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            ShipDef def = ShipDef.Load(name);

            if (def == null)
                continue;

            var header = JsonUtility.FromJson<Header>(def.raw);

            if (header == null || header.crews <= 0)
                continue;   // 운석·잔해·시설 - 사람이 몰 배가 아니다

            _options.Add(Describe(name, def, header));
        }

        // 이름순. 파일 시스템 순서는 기계마다 다르다.
        _options.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));

        // **첫 배는 공짜다.** 전부 잠겨 있으면 연구점수를 벌 배가 없어서 게임이 안 열린다.
        //
        // "제일 싼 배"로 계산했더니 lines(테스트용 낙서 배, crews만 3인)가 기본 지급이
        // 됐다 - 시작 배는 min()이 아니라 게임의 결정이다. 이름으로 박고, 그 배가 없는
        // 폴더(모드질 등)에서만 최저가로 물러난다.
        if (!IsUnlocked(DefaultShip))
        {
            bool exists = false;

            foreach (Option option in _options)
                if (option.defName == DefaultShip)
                    exists = true;

            if (exists)
                Unlock(DefaultShip);
            else if (_options.Count > 0)
            {
                Option cheapest = _options[0];

                foreach (Option option in _options)
                    if (option.researchCost < cheapest.researchCost)
                        cheapest = option;

                Unlock(cheapest.defName);
            }
        }
    }

    /// <summary>시작 배. 언락 없이 처음부터 몰 수 있는 유일한 배다.</summary>
    private const string DefaultShip = "scout";

    /// <summary>저장된 런이 몰던 배. 언락 없이도 출격할 수 있다 - 소급 몰수 방지.</summary>
    private string _sailing;

    private bool CanSail(string defName) => IsUnlocked(defName) || defName == _sailing;

    /// <summary>배 한 척의 카탈로그 줄. 배를 만들지 않고 def만 읽는다.</summary>
    private static Option Describe(string name, ShipDef def, Header header)
    {
        float mass = Mathf.Max(1f, def.placements.Count * header.massPerPlate);
        float thrust = 0f;      // kN
        float tank = 0f;        // kN·s
        var guns = new Dictionary<string, int>();

        foreach (Placement p in def.placements)
        {
            ThingDef thing = DefDatabase.Get(p.def);

            if (thing == null)
                continue;

            switch (thing.thingClass)
            {
                case "Gun":
                case "Launcher":
                    guns.TryGetValue(p.def, out int n);
                    guns[p.def] = n + 1;
                    break;

                case "Engine":
                    thrust += JsonUtility.FromJson<EngineNums>(thing.raw)?.MaxPower ?? 0f;
                    break;

                case "Tank":
                    tank += JsonUtility.FromJson<TankNums>(thing.raw)?.impulse ?? 0f;
                    break;
            }
        }

        var parts = new List<string>();

        foreach (KeyValuePair<string, int> pair in guns)
            parts.Add($"{pair.Key} x{pair.Value}");

        return new Option
        {
            defName = name,
            def = def,
            header = header,
            weapons = parts.Count > 0 ? string.Join(", ", parts) : "-",
            mass = mass,
            // Drive와 같은 식: 종단속도 = 추력(N) / (질량 x drag). drag 0은 무한이다.
            topSpeed = header.drag > 0f ? thrust * 1000f / (mass * header.drag) : float.PositiveInfinity,
            tankImpulse = tank,
            schematic = BuildSchematic(def),
            researchCost = def.placements.Count,
        };
    }

    /// <summary>
    /// ShipStatusHud의 AIRFRAME과 같은 그림을 def에서 굽는다 - 칸 하나가 픽셀 하나,
    /// 포인트 필터로 키우면 그 계기판 모자이크가 된다. 위젯 수백 개 대신 텍스처 한 장.
    /// </summary>
    private static Texture2D BuildSchematic(ShipDef def)
    {
        ShipGrid.Map map = ShipBuilder.StampFromDef(def);

        if (map == null || map.width <= 0 || map.height <= 0)
            return null;

        var texture = new Texture2D(map.width, map.height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        var pixels = new Color32[map.width * map.height];
        var solid = new Color32(199, 230, 255, 255);   // ShipStatusHud.HudColor

        for (int col = 0; col < map.width; col++)
        {
            for (int row = 0; row < map.height; row++)
            {
                if (!ShipGrid.Solid(map.cells[col, row]))
                    continue;

                // row는 아래로 증가하고 텍스처 y는 위로 증가한다.
                pixels[(map.height - 1 - row) * map.width + col] = solid;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false);
        return texture;
    }

    /// <summary>
    /// 스타일이 준비됐으면 true. **아직이면 이 프레임을 통째로 건너뛴다.**
    ///
    /// GUI.skin은 OnGUI 안에서만 유효해서 GUIManager가 거기서 초기화한다. Update는 첫
    /// 몇 프레임 그보다 먼저 도므로, 그동안 GUIStyleMaker를 부르면 에러 로그만 찍히고
    /// 빈 GUIStyle이 돌아온다 - 증상이 "시작할 때 에러 아홉 줄"이었다.
    ///
    /// 걸쇠는 DialogueManager/ControlHints와 같은 것이다.
    /// </summary>
    private static bool MakeStyles()
    {
        if (_title != null)
            return true;

        if (!GUIStyleMaker.Initialized)
            return false;

        _title = GUIStyleMaker.Label(new Color(0.78f, 0.90f, 1f), 28, TextAnchor.MiddleLeft);
        _rowStyle = GUIStyleMaker.Button(new Color(0.10f, 0.14f, 0.20f, 0.92f), new Color(0.78f, 0.90f, 1f), new Color(0.16f, 0.22f, 0.30f, 0.95f));

        // [미연구] 꼬리표가 색 태그를 쓴다. GUI.skin.button 복사본은 richText가 꺼져 있어서
        // 안 켜면 태그가 글자 그대로 나온다.
        _rowStyle.richText = true;
        _rowSelected = GUIStyleMaker.Button(new Color(0.20f, 0.32f, 0.45f, 0.95f), Color.white, new Color(0.24f, 0.38f, 0.52f, 0.95f));
        _statLabel = GUIStyleMaker.Label(new Color(0.55f, 0.65f, 0.75f), 15);
        _statValue = GUIStyleMaker.Label(new Color(0.78f, 0.90f, 1f), 15, TextAnchor.MiddleRight);
        _dim = GUIStyleMaker.Box(new Color(0.02f, 0.03f, 0.05f, 0.88f));

        // 배경은 **완전 불투명**이다. 0.88을 화면 전체에 깔았더니 12%가 새서 뒤에 이미
        // 소환된 플레이어 배가 비쳤다 - 이 화면은 세계를 세워두고 여는 것이라 세계가
        // 보이면 "이미 시작됐는데 왜 고르라 하지"가 된다. _dim은 패널용으로만 남는다.
        _backdrop = GUIStyleMaker.Box(new Color(0.02f, 0.03f, 0.05f, 1f));
        _black = GUIStyleMaker.Box(Color.black);

        return true;
    }

    private void Update()
    {
        if (!_open)
            return;

        // **Begin보다 먼저 본다.** 스타일 없이 선언하면 위젯이 빈 GUIStyle로 태어나고,
        // 즉시 모드라 한 프레임 안 그리는 것은 그냥 안 그리는 것이다.
        if (!MakeStyles())
            return;

        ImGui.Begin();

        float w = GUIManager.LogicalWidth;
        float h = GUIManager.LogicalHeight;

        // 배경. 장식이라 입력을 안 막는다(Decorative).
        ImGui.BoxLabel("shipsel:dim", new Rect(0, 0, w, h), "", _backdrop).Layer = ScreenLayer;
        ImGui.Label("shipsel:title", new Rect(40, 24, 600, 40), "함선 선택", _title).Layer = ScreenLayer;

        DrawList(w, h);
        DrawDetail(w, h);
        DrawConfirm(w, h);

        Fade(w, h);
    }

    // ------------------------------------------------------------
    // 우측 세로 목록
    // ------------------------------------------------------------

    private void DrawList(float w, float h)
    {
        // 바닥을 h-180에서 끊는다. 아래 100px은 잔고 라벨(h-94)과 확정 버튼(h-68)의
        // 자리다 - h-160으로 내리면 리스트 마지막 행이 그 둘 밑으로 파고든다. 원래도
        // 버튼과 12px 겹쳤는데 버튼이 위에 그려져 안 보였을 뿐이다.
        var area = new Rect(w - 380f, 80f, 340f, h - 260f);

        // 휠 스크롤. 스크롤바 위젯 대신 마스크 그룹 + 오프셋 - 리스트가 잘리고,
        // 위치 표시는 오른쪽의 가는 줄 하나면 충분하다.
        float content = _options.Count * (RowHeight + 6f);
        float maxScroll = Mathf.Max(0f, content - area.height);

        if (Mouse.current != null && area.Contains(GUIManager.MousePos))
            _scroll = Mathf.Clamp(_scroll - Mouse.current.scroll.ReadValue().y * 0.6f, 0f, maxScroll);

        GUIGroup group = ImGui.BeginGroup("shipsel:list", area, null, mask: true);
        group.Layer = ScreenLayer + 1;

        for (int i = 0; i < _options.Count; i++)
        {
            var row = new Rect(
                area.x,
                area.y + i * (RowHeight + 6f) - _scroll,
                area.width - 14f,
                RowHeight);

            // 화면 밖 행은 선언 자체를 건너뛴다 - 마스크는 그리기만 자르지 위젯은 산다.
            if (row.yMax < area.y || row.y > area.yMax)
                continue;

            Option option = _options[i];

            // 잠긴 배도 목록에 보이고 골라진다 - 수치를 보고 "이걸 벌자"가 서야
            // 연구점수에 목적이 생긴다. 못 하는 것은 출격뿐이다.
            string label = CanSail(option.defName)
                ? option.defName
                : $"{option.defName}  <color=#8899aa>[미연구]</color>";

            if (ImGui.Button($"shipsel:row:{option.defName}", row, label,
                    i == _selected ? _rowSelected : _rowStyle))
                _selected = i;
        }

        ImGui.EndGroup();

        // 스크롤 위치 표시줄
        if (maxScroll > 0f)
        {
            float barHeight = area.height * (area.height / content);
            float barY = area.y + (_scroll / maxScroll) * (area.height - barHeight);

            ImGui.BoxLabel("shipsel:scrollbar",
                new Rect(area.xMax - 8f, barY, 6f, barHeight), "", _rowSelected)
                .Layer = ScreenLayer + 1;
        }
    }

    // ------------------------------------------------------------
    // 좌측 모식도 + 수치
    // ------------------------------------------------------------

    private void DrawDetail(float w, float h)
    {
        if (_selected < 0 || _selected >= _options.Count)
            return;

        Option option = _options[_selected];
        var panel = new Rect(40f, 80f, w - 460f, h - 160f);

        ImGui.BoxLabel("shipsel:detail", panel, "", _dim).Layer = ScreenLayer;

        // 모식도. 칸 비율을 지키면서 패널 위쪽 60%에 맞춘다.
        if (option.schematic != null)
        {
            var mapArea = new Rect(
                panel.x + 24f, panel.y + 24f, panel.width - 48f, panel.height * 0.6f - 48f);

            float scale = Mathf.Min(
                mapArea.width / option.schematic.width,
                mapArea.height / option.schematic.height);

            var fit = new Rect(
                mapArea.x + (mapArea.width - option.schematic.width * scale) * 0.5f,
                mapArea.y + (mapArea.height - option.schematic.height * scale) * 0.5f,
                option.schematic.width * scale,
                option.schematic.height * scale);

            ImGui.Image("shipsel:map", fit, option.schematic, ScaleMode.StretchToFill)
                .Layer = ScreenLayer + 1;
        }

        // 수치
        float y = panel.y + panel.height * 0.6f + 8f;

        Stat(panel, ref y, "질량", $"{option.mass / 1000f:0.#} t");
        Stat(panel, ref y, "승무원", $"{option.header.crews}");
        Stat(panel, ref y, "종단속도",
            float.IsInfinity(option.topSpeed) ? "∞" : $"{option.topSpeed:0.#} m/s");
        Stat(panel, ref y, "추진제", option.tankImpulse > 0f ? $"{option.tankImpulse:0} kN·s" : "-");
        Stat(panel, ref y, "무장", option.weapons);
    }

    private void Stat(Rect panel, ref float y, string label, string value)
    {
        var left = new Rect(panel.x + 24f, y, 140f, 24f);
        var right = new Rect(panel.x + 170f, y, panel.width - 200f, 24f);

        ImGui.Label($"shipsel:stat:{label}", left, label, _statLabel).Layer = ScreenLayer + 1;
        ImGui.Label($"shipsel:statv:{label}", right, value, _statValue).Layer = ScreenLayer + 1;

        y += 28f;
    }

    // ------------------------------------------------------------
    // 확정 + 암전
    // ------------------------------------------------------------

    private void DrawConfirm(float w, float h)
    {
        if (_confirmedAt >= 0f || _selected < 0 || _selected >= _options.Count)
            return;

        Option option = _options[_selected];
        var rect = new Rect(w - 380f, h - 68f, 340f, 48f);

        ImGui.Label("shipsel:rsch",
            new Rect(rect.x, rect.y - 26f, rect.width, 22f),
            $"연구점수 {RunState.Research}", _statLabel).Layer = ScreenLayer + 1;

        // 버튼은 그룹에 담는다. 루트에 두면 Reset이 Layer를 0으로 되돌려서 배경(900)
        // 밑으로 가라앉는다 - 목록(DrawList)이 쓰는 방식 그대로다.
        ImGui.BeginGroup("shipsel:confirm", rect, null, mask: false)
            .Layer = ScreenLayer + 1;

        bool sail = CanSail(option.defName);
        int cost = option.researchCost;

        // 자식도 절대 좌표다. 그룹이 그릴 때 자기 원점을 빼서 옮긴다(DrawItemLocal) -
        // 목록의 행들이 area.x를 그대로 쓰는 것과 같은 규칙이다.
        if (sail && ImGui.Button("shipsel:go", rect, "출격", _rowSelected))
            _confirmedAt = Time.unscaledTime;

        if (!sail && RunState.Research >= cost
            && ImGui.Button("shipsel:research", rect, $"연구  (-{cost})", _rowSelected))
        {
            // 언락만 하고 화면에 남는다. 남은 점수로 다른 배도 열 수 있고,
            // 출격은 그 다음 결심이다.
            RunState.Research -= cost;
            Unlock(option.defName);
        }

        if (!sail && RunState.Research < cost)
            ImGui.BoxLabel("shipsel:locked", rect,
                $"연구 부족  {RunState.Research} / {cost}", _dim);

        ImGui.EndGroup();
    }

    private void Fade(float w, float h)
    {
        if (_confirmedAt < 0f)
            return;

        float t = (Time.unscaledTime - _confirmedAt) / FadeSeconds;

        GUIBoxLabel black = ImGui.BoxLabel("shipsel:black", new Rect(0, 0, w, h), "", _black);
        black.Layer = ScreenLayer + 10;
        black.Opacity = Mathf.Clamp01(t);

        if (t < 1f)
            return;

        // 다 덮였다. 여기서 확정하고 씬을 다시 연다 - 리로드 후에는 Chosen이 있어서
        // 이 화면이 안 열리고, Ship.Awake가 이 이름으로 짓는다. 리로드 직후의 검정
        // 1초(GameManager.BootFade)가 이 암전을 이어받는다.
        Chosen = _options[_selected].defName;

        // 몰던 배와 다른 배를 골랐으면 지난 런은 끝난 것이다 - 손상 저장과 진행도
        // (구역·자원)를 같이 지운다. 안 지우면 저장된 런이 선택을 이겨서(RunShipFor)
        // 고른 배가 아예 안 나온다. 같은 배를 다시 고르면 런을 그대로 잇는다.
        ShipDef saved = RunState.Load();

        string sailing = saved == null ? null
            : string.IsNullOrEmpty(saved.basedOn) ? saved.defName : saved.basedOn;

        if (sailing != null && sailing != Chosen)
            RunState.Clear();

        Close();

        // 이 화면의 위젯(검은 덮개 포함)을 씬과 함께 정리한다. 리로드 직후 프레임에는
        // 수확(Begin)이 "지난 프레임에 선언됨"으로 보고 한 번 더 그릴 수 있다 - 남는
        // 대사도 어차피 씬과 함께 죽을 것들이라 통째로 버려도 안전하다.
        ImGui.Clear();

        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
