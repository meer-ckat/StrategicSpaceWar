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

    private readonly List<Option> _options = new();
    private int _selected;
    private bool _open;
    private float _scroll;
    private float _confirmedAt = -1f;   // unscaled. 0 이상이면 암전 중

    private const float FadeSeconds = 0.5f;
    private const int ScreenLayer = 900;    // 다른 ImGui 위에. 암전은 그 위.
    private const float RowHeight = 44f;

    private static GUIStyle _title, _rowStyle, _rowSelected, _statLabel, _statValue, _dim, _black;

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
    }

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

    private static void MakeStyles()
    {
        if (_title != null)
            return;

        _title = GUIStyleMaker.Label(new Color(0.78f, 0.90f, 1f), 28, TextAnchor.MiddleLeft);
        _rowStyle = GUIStyleMaker.Button(new Color(0.10f, 0.14f, 0.20f, 0.92f), new Color(0.78f, 0.90f, 1f), new Color(0.16f, 0.22f, 0.30f, 0.95f));
        _rowSelected = GUIStyleMaker.Button(new Color(0.20f, 0.32f, 0.45f, 0.95f), Color.white, new Color(0.24f, 0.38f, 0.52f, 0.95f));
        _statLabel = GUIStyleMaker.Label(new Color(0.55f, 0.65f, 0.75f), 15);
        _statValue = GUIStyleMaker.Label(new Color(0.78f, 0.90f, 1f), 15, TextAnchor.MiddleRight);
        _dim = GUIStyleMaker.Box(new Color(0.02f, 0.03f, 0.05f, 0.88f));
        _black = GUIStyleMaker.Box(Color.black);
    }

    private void Update()
    {
        if (!_open)
            return;

        ImGui.Begin();
        MakeStyles();

        float w = GUIManager.LogicalWidth;
        float h = GUIManager.LogicalHeight;

        // 배경. 장식이라 입력을 안 막는다(Decorative).
        ImGui.BoxLabel("shipsel:dim", new Rect(0, 0, w, h), "", _dim).Layer = ScreenLayer;
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
        var area = new Rect(w - 380f, 80f, 340f, h - 160f);

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

            if (ImGui.Button($"shipsel:row:{option.defName}", row, option.defName,
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
        if (_confirmedAt >= 0f)
            return;

        var rect = new Rect(w - 380f, h - 68f, 340f, 48f);

        if (ImGui.Button("shipsel:go", rect, "출격", _rowSelected)
            && _selected >= 0 && _selected < _options.Count)
        {
            _confirmedAt = Time.unscaledTime;
        }
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
