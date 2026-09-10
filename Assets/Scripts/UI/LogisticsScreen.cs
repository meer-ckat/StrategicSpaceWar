using System;
using System.Collections.Generic;
using System.IO;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

// 구역 사이의 캠페인 현황. 화면을 가득 채운다. 왼쪽 절반이 항로도(메인), 오른쪽은 위가 고른 구역의 정보, 아래가 함선 상태·정비.
// 열 때마다 통째로 다시 짓는다 - 거쳐온 구역 수가 매번 다르고 라벨 수십 개라 값싸다.
//
// UI가 정본이고 통신(COMMS)은 반응이다. 판단에 필요한 사실(적·정비·자재)은 항로도 배지와 Info에 반드시 있고,
// 통신 카드는 같은 사실을 사람 목소리로 한 번 더 말할 뿐이다. 카드가 안 떠도 정보는 하나도 안 빠진다.
public sealed class LogisticsScreen : MonoBehaviour
{
    public static bool IsOpen;

    const int WindowLayer = 880;
    const float Margin = 24f;    // 화면 가장자리
    const float PanelGap = 12f;  // 패널 사이
    const float Padding = 16f;   // 패널 안쪽
    const float Gap = 8f;        // 패널 안 요소 사이
    const float RowH = 22f;      // 라벨/값 한 줄

    // 팔레트. 역할별로 하나씩이고 값은 여기서만 고친다. 화면의 대부분은 Obsidian·Panel·White고, 색은 상태를 말할 때만 쓴다.
    static readonly Color Obsidian = new(0.06f, 0.065f, 0.075f);   // 바닥
    static readonly Color Panel = new(0.085f, 0.095f, 0.105f);      // 패널
    static readonly Color Ink = new(0.12f, 0.13f, 0.145f);          // 지나온·현재 노드
    static readonly Color Surface = new(0.17f, 0.185f, 0.205f);     // 고를 수 있는 노드, 통신 카드
    static readonly Color White = new(0.96f, 0.97f, 1f);            // 글자
    static readonly Color Dim = new(0.55f, 0.57f, 0.60f);           // 보조 글자
    static readonly Color Blue = new(0.25f, 0.47f, 0.95f);          // 항로: 선택·연결선
    static readonly Color BlueDim = new(0.18f, 0.24f, 0.36f);       // 고르지 않은 가지선
    static readonly Color Green = new(0.30f, 0.80f, 0.45f);         // 가능·획득
    static readonly Color Orange = new(1.00f, 0.55f, 0.15f);        // 주의·hover·표적 시설
    static readonly Color Red = new(0.92f, 0.30f, 0.28f);           // 손상·불가·적

    // 항로도. 노드는 짧은 직사각형, 한 줄에 하나, x는 난수, y는 살짝 흔들림. 배지는 노드 바로 위 한 줄.
    public static readonly Vector2 NodeSize = new(130f, 32f);
    const float NodeStep = 88f;
    const float NodeJitter = 12f;
    const float BadgeH = 14f;
    const float LineWidth = 2f;
    const float ScrollbarWidth = 20f;
    const float DisabledOpacity = 0.35f;

    // 모션. 전체 열림 < 0.5초. 읽는 패널은 안 움직이고 지도만 산다.
    const float OpenFade = 0.22f;
    const float OpenStagger = 0.08f;
    const float NodeWave = 0.18f;
    const float NodeWaveStep = 0.035f;
    const float PunchAmount = 0.12f;
    const float CountUp = 0.3f;
    const float MagnetRadius = 90f;  // 항로점이 마우스에 끌리기 시작하는 거리
    const float MagnetPx = 4f;       // 그 거리 끝에서의 최대 이동
    const float ParallaxPx = 4f;     // 마우스가 화면 끝에서 끝까지 갈 때 항로도가 따라가는 최대 거리
    const float BarkSeconds = 4f;

    // Resources/Sound/<이름>.mp3. 없으면 SoundManager가 이름당 한 번 경고하고 넘어간다.
    const string SfxHover = "UI_Hover", SfxSelect = "UI_Select", SfxClick = "UI_Click", SfxDepart = "UI_Depart";

    static LogisticsScreen instance;

    GUIGroup window, nav, info, comms;
    GUILabel infoTitle, infoKind, plateValue, materialValue, serviceValue, commsSpeaker, commsText;
    readonly List<GUIItem> infoRows = new();
    readonly List<GUIItem> repairUi = new();
    GUIButton[] laneButtons, repairButtons;
    GUIBoxLabel[] laneStubs;
    GUIButton depart;
    GUIStyle candidate, selected, line, lineDim, rowLabel, rowValue;
    GUIBoxLabel black;
    SectorDef[] lanes;
    int lane;
    float scrollMax;
    float barkUntil;
    bool ready;                              // 등장 모션이 끝났다. 그 전엔 입력을 안 받는다
    Action<GUIItem, float> counting;         // 진행 중인 카운트업. 연타하면 교체한다
    Comms commsDef;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindFirstObjectByType<LogisticsScreen>() != null)
            return;

        var go = new GameObject("Logistics");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<LogisticsScreen>();
    }

    void Awake() => instance = this;

    public static void Open()
    {
        if (instance == null || Campaign.current == null)
            return;

        instance.Build();

        IsOpen = true;
        Core.TickManager.Paused = true;
        Time.timeScale = 0f;
    }

    void Build()
    {
        Campaign c = Campaign.current;

        // 글자 위계 셋: 눈썹 12 < 본문 15 < 제목 20
        GUIStyle eyebrow = GUIStyleMaker.Label(Dim, 12).Font(12, FontStyle.Bold);
        GUIStyle heading = GUIStyleMaker.Label(White, 20).Font(20, FontStyle.Bold);
        heading.wordWrap = false;
        GUIStyle body = GUIStyleMaker.Label(White, 15).Wrap().RichText();
        GUIStyle badge = GUIStyleMaker.Label(Dim, 11, TextAnchor.MiddleCenter).Font(11, FontStyle.Bold).RichText();
        GUIStyle button = GUIStyleMaker.Button(White, Obsidian, Orange, null, 16);
        GUIStyle ground = GUIStyleMaker.Box(Obsidian);
        GUIStyle panel = GUIStyleMaker.Box(Panel);
        GUIStyle card = GUIStyleMaker.Box(Surface);
        GUIStyle visitedNode = GUIStyleMaker.Button(Ink, Dim, Surface, null, 13);
        GUIStyle hereNode = GUIStyleMaker.Button(Ink, White, Surface, null, 13);
        rowLabel = GUIStyleMaker.Label(Dim, 15);
        rowValue = GUIStyleMaker.Label(White, 15, TextAnchor.MiddleRight).RichText();
        line = GUIStyleMaker.Box(Blue);
        lineDim = GUIStyleMaker.Box(BlueDim);
        candidate = GUIStyleMaker.Button(Surface, White, Blue, null, 13);
        selected = GUIStyleMaker.Button(Blue, White, Blue, null, 13);

        Rect screen = new Rect(0f, 0f, GUIManager.LogicalWidth, GUIManager.LogicalHeight);
        Rect inner = Inset(screen, Margin);
        float halfW = (inner.width - PanelGap) * 0.5f;
        float halfH = (inner.height - PanelGap) * 0.5f;
        Rect navRect = new Rect(inner.x, inner.y, halfW, inner.height);
        Rect infoRect = new Rect(inner.xMax - halfW, inner.y, halfW, halfH);
        Rect statusRect = new Rect(inner.xMax - halfW, inner.yMax - halfH, halfW, halfH);

        if (window != null)
            GUIManager.Unregister(window);
        counting = null;
        infoRows.Clear();
        repairUi.Clear();
        window = Widget.SetLayer(Widget.Window("", screen, "Logistics", ground), WindowLayer);
        window.whenTick += (_, __) => Keys();

        // 갈래가 없는 장 경계면 다음 장이 단일 갈래다. 그래야 Info가 가리키는 곳이 항로도에도 있다.
        lanes = c.Fork ?? (c.Current != null ? new[] { c.Current } : Array.Empty<SectorDef>());

        // Navigator: 항로도. 맨 윗줄이 갈래(좌우로 벌림), 그 아래로 거쳐온 구역(최근이 위).
        // 스크롤 뷰라 자식 좌표는 view 원점 기준 절대 좌표다. 스크롤바 모양은 GUI.skin.verticalScrollbar.
        nav = Widget.Window(window, "", navRect, "Navigator", panel);
        nav.Scrollable = true;
        GUILayoutManager.SetPadding(nav, Padding);
        Rect view = GUILayoutManager.GetContentRect(nav);
        float contentW = view.width - ScrollbarWidth;

        int laneRows = lanes.Length > 0 ? 1 : 0;
        int visited = c.Visited.Count;
        float contentH = (visited + laneRows) * NodeStep + NodeJitter;
        nav.ContentSize = new Vector2(contentW, Mathf.Max(contentH, view.height));
        scrollMax = Mathf.Max(0f, contentH - view.height);

        // 항로는 바닥에서 시작해 위로 자란다. 내용이 짧으면 빈 자리를 위에 둔다 - 통신 카드가 거기 뜬다.
        Rect laid = new Rect(view.x, view.y + Mathf.Max(0f, view.height - contentH), view.width, view.height);

        // 휠은 손으로 돈다. 프로젝트가 Input System 전용이라 IMGUI에 ScrollWheel 이벤트가 안 온다 (ShipSelectScreen과 같은 이유).
        nav.whenTick += (_, __) =>
        {
            if (Mouse.current != null && nav.Rect.Contains(GUIManager.MousePos))
                Scroll(-Mouse.current.scroll.ReadValue().y * 0.6f);
        };

        // 자리부터 다 정한다. 선을 먼저 넣고 노드를 그 위에 얹는다 - 그리는 순서가 자식 순서다.
        int seed = RunState.Seed;
        Vector2[] visitedPos = new Vector2[visited];
        for (int i = 0; i < visited; i++)
            visitedPos[i] = NodePos(seed, laid, contentW, i + laneRows, 0, 1, visited - 1 - i, 0);

        Vector2[] lanePos = new Vector2[lanes.Length];
        for (int k = 0; k < lanes.Length; k++)
            lanePos[k] = NodePos(seed, laid, contentW, 0, k, lanes.Length, visited, k + 1);

        // 선은 그룹 하나에 모아 한 번에 페이드한다. Mask를 끄면 자식이 그룹 Rect에 안 잘린다.
        GUIGroup lines = Widget.Window(nav, "", view, "Lines", GUIStyle.none);
        lines.Mask = false;
        for (int i = 0; i + 1 < visited; i++)
            Elbow(lines, visitedPos[i], visitedPos[i + 1], line);
        laneStubs = visited > 0 ? Branch(lines, lanes, lanePos, visitedPos[0], line) : new GUIBoxLabel[lanes.Length];

        // 갈래 노드 + 배지. 고르기 전에 비교할 수 있어야 한다 - 적·정비·자재가 노드 위 한 줄에 있다.
        var nodes = new List<GUIItem>();   // 위에서 아래 순서
        laneButtons = new GUIButton[lanes.Length];
        for (int k = 0; k < lanes.Length; k++)
        {
            if (lanes[k] == null)
                continue;
            int chosen = k;
            laneButtons[k] = Widget.Button(nav, lanes[k].name, new Rect(lanePos[k], NodeSize), () => Select(chosen, announce: true), candidate);
            GUILabel tag = Widget.Label(nav, Badge(lanes[k]), new Rect(lanePos[k].x, lanePos[k].y - BadgeH - 2f, NodeSize.x, BadgeH), badge);
            Hoverable(laneButtons[k], nav);
            Magnet(laneButtons[k], nav, tag);
            nodes.Add(laneButtons[k]);
        }
        for (int i = 0; i < visited; i++)
        {
            SectorDef s = c.Visited[visited - 1 - i];
            GUIButton b = Widget.Button(nav, s.name, new Rect(visitedPos[i], NodeSize), () => Show(s, departable: false, announce: true), i == 0 ? hereNode : visitedNode);
            Hoverable(b, nav);
            if (i == 0)
                Magnet(b, nav, Widget.Label(nav, "CURRENT", new Rect(visitedPos[i].x, visitedPos[i].y + NodeSize.y + 2f, NodeSize.x, BadgeH), badge));
            else
                Magnet(b, nav);
            nodes.Add(b);
        }

        // Info: 고른 구역. 제목·종류는 고정, 라벨/값 줄은 Show가 다시 짓는다, 출항은 바닥.
        info = Widget.Window(window, "", infoRect, "Info", panel);
        Rect infoIn = Inset(infoRect, Padding);
        Widget.Label(info, "선택 구역", new Rect(infoIn.x, infoIn.y, infoIn.width, 16f), eyebrow);
        infoTitle = Widget.Label(info, "", new Rect(infoIn.x, infoIn.y + 20f, infoIn.width, 28f), heading);
        infoKind = Widget.Label(info, "", new Rect(infoIn.x, infoIn.y + 50f, infoIn.width, 18f), eyebrow);
        depart = Widget.Button(info, "출항", new Rect(infoIn.x, infoIn.yMax - 16f - Gap - 44f, infoIn.width, 44f), Depart, button);
        Widget.Label(info, "←→ 항로   Enter 출항", new Rect(infoIn.x, infoIn.yMax - 16f, infoIn.width, 16f), eyebrow);
        Hoverable(depart);

        // Ship Status: 언제나 쓸모 있는 상태 셋. 정비 조작은 정비 노드에서만 내려온다 - 못 쓰는 버튼을 늘어놓지 않는다.
        GUIGroup status = Widget.Window(window, "", statusRect, "Status", panel);
        Rect stIn = Inset(statusRect, Padding);
        Widget.Label(status, "함선 상태", new Rect(stIn.x, stIn.y, stIn.width, 16f), eyebrow);
        plateValue = Row(status, stIn, 0, "손상 판", "");
        materialValue = Row(status, stIn, 1, "보유 자재", "");
        serviceValue = Row(status, stIn, 2, "정비", "");

        float ry = stIn.y + 20f + RowH * 3 + Gap * 2;
        float bw = (stIn.width - Gap * 2f) / 3f;
        repairButtons = new[]
        {
            Widget.Button(status, "수리 1", new Rect(stIn.x, ry, bw, 40f), null, button),
            Widget.Button(status, "수리 10", new Rect(stIn.x + bw + Gap, ry, bw, 40f), null, button),
            Widget.Button(status, "전량", new Rect(stIn.x + (bw + Gap) * 2f, ry, bw, 40f), null, button),
        };
        repairButtons[0].callBack = () => Repair(1, repairButtons[0]);
        repairButtons[1].callBack = () => Repair(10, repairButtons[1]);
        repairButtons[2].callBack = () => Repair(RunState.Materials, repairButtons[2]);
        foreach (GUIButton b in repairButtons)
        {
            Hoverable(b);
            repairUi.Add(b);
        }
        repairUi.Add(Widget.Label(status, "R 수리 1   Shift+R 전량", new Rect(stIn.x, ry + 40f + Gap, stIn.width, 16f), eyebrow));

        // COMMS: 항로도 좌상단에 잠깐 뜨는 카드. 레이아웃 요소가 아니라 위에 얹히는 것이라 항로가 길어져도 자리를 안 뺏는다.
        // 84였는데 본문 자리가 26px라 두 줄째가 그룹 마스크에 잘렸다. 15px 한글 두 줄 = 38.
        Rect commsRect = new Rect(navRect.x + Padding, navRect.y + Padding, navRect.width * 0.6f, 100f);
        comms = Widget.Window(window, "", commsRect, "Comms", card);
        Rect cmIn = Inset(commsRect, 12f);
        Widget.Label(comms, "COMMUNICATION", new Rect(cmIn.x, cmIn.y, cmIn.width, 14f), eyebrow);
        commsSpeaker = Widget.Label(comms, "", new Rect(cmIn.x, cmIn.y + 16f, cmIn.width, 16f), GUIStyleMaker.Label(White, 12).Font(12, FontStyle.Bold));
        commsText = Widget.Label(comms, "", new Rect(cmIn.x, cmIn.y + 34f, cmIn.width, cmIn.height - 34f), body);
        comms.isVisible = false;
        comms.isInteractable = false;
        foreach (GUIItem item in comms.Childrens)
            item.isInteractable = false;
        window.whenTick += (_, __) =>
        {
            if (comms.isVisible && barkUntil > 0f && Time.unscaledTime > barkUntil)
            {
                barkUntil = 0f;
                comms.FadeOut(0.3f, onComplete: () => comms.isVisible = false);
            }
        };
        if (commsDef == null)
            commsDef = Comms.Load();

        if (black == null)
        {
            black = Widget.SetLayer(Widget.BoxLabel("", screen, GUIStyleMaker.Box(Color.black)), WindowLayer + 10);
            black.isVisible = false;
            black.isInteractable = false;
        }

        lane = -1;
        for (int k = 0; k < lanes.Length && lane < 0; k++)
            if (lanes[k] != null)
                lane = k;

        if (lane >= 0)
            Select(lane, announce: false);
        else
            Show(null, departable: false, announce: false);
        RefreshStatus();

        // 보이는 행만 아래(과거)에서 위(갈래)로 올라온다. 화면 밖 과거는 기다릴 이유가 없다.
        int visibleRows = Mathf.CeilToInt(view.height / NodeStep) + 1;
        if (nodes.Count > visibleRows)
            nodes.RemoveRange(visibleRows, nodes.Count - visibleRows);
        Parallax(nav, 1f);
        Enter(new[] { nav, info, status }, nodes, lines);
    }

    // 열림 모션 전부. 패널은 Fade(자식에 알파가 곱해진다), 항목은 Move(그룹 SetRect는 자식을 안 옮긴다).
    void Enter(GUIGroup[] panels, List<GUIItem> nodes, GUIGroup lines)
    {
        ready = false;
        window.isInteractable = false;

        for (int i = 0; i < panels.Length; i++)
            panels[i].FadeIn(OpenFade, i * OpenStagger, TweenHelper.EaseOutQuad);
        lines.FadeIn(0.2f, 0.25f);

        for (int i = 0; i < nodes.Count; i++)
            nodes[i].FadeIn(NodeWave, (nodes.Count - 1 - i) * NodeWaveStep);
        GUITween.Wave(nodes, new Vector2(0f, 10f), NodeWave, NodeWaveStep, TweenHelper.EaseOutQuad, inward: true, reverse: true,
            onAllComplete: () =>
            {
                ready = true;
                window.isInteractable = true;
                Bark(OpeningBark());
            });
    }

    // 라벨 왼쪽, 값 오른쪽. 값 라벨을 돌려줘서 나중에 글자만 바꾼다.
    GUILabel Row(GUIGroup g, Rect area, int row, string label, string value)
    {
        float y = area.y + 20f + row * RowH;
        Widget.Label(g, label, new Rect(area.x, y, area.width * 0.5f, RowH), rowLabel);
        return Widget.Label(g, value, new Rect(area.x + area.width * 0.5f, y, area.width * 0.5f, RowH), rowValue);
    }

    // row는 위에서 아래, col은 갈래 칸. seed·key·salt가 같으면 언제 열어도 같은 자리다.
    public static Vector2 NodePos(int seed, Rect view, float contentW, int row, int col, int cols, int key, int salt)
    {
        float colW = contentW / cols;
        float x = view.x + colW * col + Hash01(seed, key, salt) * (colW - NodeSize.x);
        float y = view.y + NodeJitter * 0.5f + row * NodeStep + (NodeStep - NodeSize.y) * 0.5f + (Hash01(seed, key, salt + 7) - 0.5f) * NodeJitter;
        return new Vector2(x, y);
    }

    static float Hash01(int seed, int key, int salt) => (Ballistics.Hash(seed, key, salt) & 0xFFFF) / 65535f;

    static Vector2 BottomCenter(Vector2 pos) => new Vector2(pos.x + NodeSize.x * 0.5f, pos.y + NodeSize.y);
    static Vector2 TopCenter(Vector2 pos) => new Vector2(pos.x + NodeSize.x * 0.5f, pos.y);

    static GUIBoxLabel VLine(GUIGroup g, float x, float y0, float y1, GUIStyle s) =>
        Widget.BoxLabel(g, "", new Rect(x - LineWidth * 0.5f, Mathf.Min(y0, y1), LineWidth, Mathf.Abs(y1 - y0)), s);

    static GUIBoxLabel HLine(GUIGroup g, float x0, float x1, float y, GUIStyle s) =>
        Widget.BoxLabel(g, "", new Rect(Mathf.Min(x0, x1) - LineWidth * 0.5f, y - LineWidth * 0.5f, Mathf.Abs(x1 - x0) + LineWidth, LineWidth), s);

    // 위 노드의 아래 가운데에서 아래 노드의 위 가운데로 꺾인 선. 축 정렬 사각형 셋이라 회전이 없다.
    static void Elbow(GUIGroup g, Vector2 upper, Vector2 lower, GUIStyle s)
    {
        Vector2 a = BottomCenter(upper), b = TopCenter(lower);
        float mid = (a.y + b.y) * 0.5f;
        VLine(g, a.x, a.y, mid, s);
        HLine(g, a.x, b.x, mid, s);
        VLine(g, b.x, mid, b.y, s);
    }

    // 갈림길. 현재 노드에서 줄기 하나가 올라가 가로 막대 하나에 닿고, 거기서 갈래마다 가지가 올라간다.
    // 줄기·막대는 공유고 가지만 갈래 것이라 돌려준다 - 선택이 바뀌면 가지 색만 바뀐다.
    public static GUIBoxLabel[] Branch(GUIGroup g, SectorDef[] lanes, Vector2[] lanePos, Vector2 current, GUIStyle s)
    {
        var stubs = new GUIBoxLabel[lanes.Length];
        Vector2 root = TopCenter(current);
        float top = float.MinValue, xMin = root.x, xMax = root.x;
        for (int k = 0; k < lanes.Length; k++)
        {
            if (lanes[k] == null)
                continue;
            Vector2 tip = BottomCenter(lanePos[k]);
            top = Mathf.Max(top, tip.y);
            xMin = Mathf.Min(xMin, tip.x);
            xMax = Mathf.Max(xMax, tip.x);
        }
        if (top == float.MinValue)
            return stubs;

        float mid = (top + root.y) * 0.5f;
        VLine(g, root.x, mid, root.y, s);
        HLine(g, xMin, xMax, mid, s);
        for (int k = 0; k < lanes.Length; k++)
            if (lanes[k] != null)
                stubs[k] = VLine(g, BottomCenter(lanePos[k]).x, BottomCenter(lanePos[k]).y, mid, s);
        return stubs;
    }

    void Select(int k, bool announce)
    {
        lane = k;
        for (int i = 0; i < laneButtons.Length; i++)
        {
            if (laneButtons[i] == null)
                continue;
            laneButtons[i].Style = i == lane ? selected : candidate;
            if (laneStubs[i] != null)
                laneStubs[i].Style = i == lane ? line : lineDim;
        }

        Show(lanes[lane], departable: true, announce);
        if (announce)
        {
            Punch(laneButtons[lane]);
            Sfx(SfxSelect);
            Bark(LaneBark(lanes[lane]));
        }
    }

    // 지나온 구역을 볼 때는 출항 버튼이 없다. 출항은 언제나 lane이 가리키는 곳이다.
    void Show(SectorDef s, bool departable, bool announce)
    {
        depart.isVisible = depart.isInteractable = departable;
        infoTitle.Content.text = s != null ? s.name : "";
        infoKind.Content.text = s != null ? s.kind.ToUpperInvariant() : "";

        foreach (GUIItem item in infoRows)
            GUIManager.Unregister(item);
        infoRows.Clear();
        if (s == null)
            return;

        Rect area = new Rect(info.Rect.x + Padding, info.Rect.y + Padding + 76f, info.Rect.width - Padding * 2f, RowH);
        int row = 0;
        List<(string ship, int n)> ships = Ships(s);
        int facilities = Facilities(s);
        if (ships.Count == 0 && facilities == 0)
            InfoRow(area, row++, "접촉", Tint("없음", Green));
        foreach ((string ship, int n) in ships)
            InfoRow(area, row++, ship, Tint($"×{n}", Red));
        if (facilities > 0)
            InfoRow(area, row++, "표적 시설", Tint(facilities.ToString(), Orange));
        InfoRow(area, row++, "정비", s.refit ? Tint("가능", Green) : Tint("불가", Dim));
        if (s.materials > 0)
            InfoRow(area, row++, "회수 자재", Tint($"+{s.materials}", Green));

        if (announce)
            foreach (GUIItem item in infoRows)
                item.FadeIn(0.12f);
    }

    void InfoRow(Rect area, int row, string label, string value)
    {
        float y = area.y + row * RowH;
        infoRows.Add(Widget.Label(info, label, new Rect(area.x, y, area.width * 0.5f, RowH), rowLabel));
        infoRows.Add(Widget.Label(info, value, new Rect(area.x + area.width * 0.5f, y, area.width * 0.5f, RowH), rowValue));
    }

    static string Tint(string text, Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text}</color>";

    // 적 함선만, 함급(defName)별로 등장 순서대로. 중립·아군·시설은 여기 없다.
    public static List<(string ship, int n)> Ships(SectorDef s)
    {
        var ships = new List<(string ship, int n)>();
        foreach (SpawnDef sp in s.spawns)
        {
            if (sp.Side != Ship.Team.Enemy || sp.hulk)
                continue;
            int i = ships.FindIndex(e => e.ship == sp.ship);
            if (i < 0)
                ships.Add((sp.ship, 1));
            else
                ships[i] = (sp.ship, ships[i].n + 1);
        }
        return ships;
    }

    // 적 시설(hulk). 표적이다. 중립 hulk(표류장·보급 부표)는 배경이라 안 센다.
    public static int Facilities(SectorDef s)
    {
        int n = 0;
        foreach (SpawnDef sp in s.spawns)
            if (sp.hulk && sp.Side == Ship.Team.Enemy)
                n++;
        return n;
    }

    // 노드 위 한 줄. 글자로 말하고 색은 거든다 - 색맹이어도 읽힌다.
    public static string Badge(SectorDef s)
    {
        int hostile = 0;
        foreach ((string _, int n) in Ships(s))
            hostile += n;
        int facilities = Facilities(s);

        var parts = new List<string>();
        if (hostile > 0)
            parts.Add(Tint($"HOSTILE ×{hostile}", Red));
        if (facilities > 0)
            parts.Add(Tint($"TARGET ×{facilities}", Orange));
        if (s.refit)
            parts.Add(Tint("REFIT", Green));
        if (s.materials > 0)
            parts.Add(Tint($"+{s.materials}", Green));
        return parts.Count > 0 ? string.Join("  ", parts) : "CLEAR";
    }

    void RenderStatus(int damaged, int materials)
    {
        plateValue.Content.text = Tint(damaged.ToString(), damaged > 0 ? Red : Green);
        materialValue.Content.text = $"<b>{materials}</b>";
        serviceValue.Content.text = Campaign.current.CanRefit ? Tint("가능", Green) : Tint("시설 없음", Red);
    }

    void RefreshStatus()
    {
        Campaign c = Campaign.current;
        Ship p = c.Player;
        int damaged = p != null ? p.DamagedPlateCount() : 0;
        bool can = damaged > 0 && RunState.Materials > 0;

        RenderStatus(damaged, RunState.Materials);
        foreach (GUIItem item in repairUi)
            item.isVisible = item.isInteractable = c.CanRefit;
        foreach (GUIButton b in repairButtons)
        {
            b.isEnabled = b.isInteractable = c.CanRefit && can;
            b.Opacity = can ? 1f : DisabledOpacity;
        }
    }

    // 배가 실제로 쓴 만큼만 뺀다. RepairPlates가 고칠 판이 모자라면 예산을 덜 쓰고 돌려준다.
    void Repair(int want, GUIButton pressed)
    {
        Ship p = Campaign.current.Player;
        if (p == null || !pressed.isInteractable)
            return;

        int d0 = p.DamagedPlateCount(), m0 = RunState.Materials;
        RunState.Materials -= p.RepairPlates(Mathf.Min(want, RunState.Materials));
        int d1 = p.DamagedPlateCount(), m1 = RunState.Materials;

        Sfx(SfxClick);
        Punch(pressed);
        RefreshStatus();
        Tween01(x => RenderStatus(Mathf.RoundToInt(Mathf.Lerp(d0, d1, x)), Mathf.RoundToInt(Mathf.Lerp(m0, m1, x))));
    }

    // 0→1을 CountUp 동안 흘린다. 라벨을 모르므로 호출자가 문자열을 만든다. 연타하면 앞 것을 버린다.
    void Tween01(Action<float> onX)
    {
        if (counting != null)
            window.whenTick -= counting;

        float t = 0f;
        counting = (_, dt) =>
        {
            t += dt;
            float x = Mathf.Clamp01(t / CountUp);
            onX(TweenHelper.EaseOutQuad(x));
            if (x < 1f)
                return;
            window.whenTick -= counting;
            counting = null;
        };
        window.whenTick += counting;
    }

    // ---- COMMS ------------------------------------------------------------
    // 통신은 반응이지 정보가 아니다. 조건은 UI가 이미 보여주는 사실에서만 나온다.

    [Serializable]
    public class Comms
    {
        [Serializable]
        public class Bark
        {
            public string when;
            public string speaker;
            public string[] lines;
        }

        public List<Bark> barks = new();

        public static string Path => System.IO.Path.Combine(Application.streamingAssetsPath, "Run", "comms.json");

        public static Comms Load()
        {
            if (!File.Exists(Path))
                return new Comms();
            Comms c = JsonUtility.FromJson<Comms>(File.ReadAllText(Path));
            return c ?? new Comms();
        }

        public Bark Find(string when)
        {
            foreach (Bark b in barks)
                if (b.when == when && b.lines != null && b.lines.Length > 0)
                    return b;
            return null;
        }
    }

    // 열릴 때: 고칠 게 있는데 못 고치는 상황이 제일 먼저. 아니면 고른 갈래 이야기.
    string OpeningBark()
    {
        Campaign c = Campaign.current;
        Ship p = c.Player;
        if (p != null && p.DamagedPlateCount() > 0 && !c.CanRefit)
            return "noservice";
        return lane >= 0 ? LaneBark(lanes[lane]) : null;
    }

    static string LaneBark(SectorDef s)
    {
        if (s == null)
            return null;
        if (s.refit)
            return "refit";
        if (Ships(s).Count > 0 || Facilities(s) > 0)
            return "hostile";
        if (s.materials > 0)
            return "salvage";
        return "clear";
    }

    // 한 화자, 한두 줄, 입력 차단 없음, BarkSeconds 뒤 사라짐. 같은 조건의 줄이 여럿이면 런·구역 수로 고정된 하나.
    void Bark(string when)
    {
        Comms.Bark b = when != null ? commsDef.Find(when) : null;
        if (b == null)
            return;

        int pick = (int)(Ballistics.Hash(RunState.Seed, Campaign.current.Visited.Count, when.GetHashCode()) % (uint)b.lines.Length);
        commsSpeaker.Content.text = b.speaker;
        commsText.Content.text = b.lines[pick];
        barkUntil = Time.unscaledTime + BarkSeconds;

        GUITween.Kill(comms);
        comms.isVisible = true;
        comms.FadeIn(0.15f);
    }

    // ---- 상호작용 ----------------------------------------------------------

    // 연타해도 커진 채 안 남는다. PunchScale은 현재 RenderScale을 기준으로 잡아서 그대로 두면 누적된다.
    static void Punch(GUIItem item)
    {
        GUITween.Kill(item);
        item.RenderScale = Vector2.one;
        item.PunchScale(PunchAmount, 0.18f);
    }

    // 진입 순간 한 번. 스크롤 뷰 안의 아이템은 Rect가 스크롤 전 좌표라 ScrollPosition을 더해 비교한다.
    void Hoverable(GUIItem item, GUIGroup scroller = null)
    {
        bool over = false;
        item.whenTick += (_, __) =>
        {
            Vector2 m = GUIManager.MousePos;
            bool now = ready && item.isVisible && item.isInteractable
                && (scroller == null || scroller.Rect.Contains(m))
                && item.Rect.Contains(scroller != null ? m + scroller.ScrollPosition : m);
            if (now && !over)
                Sfx(SfxHover);
            over = now;
        };
    }

    // 항로점이 반경 안의 마우스 쪽으로 끌린다. 거리에 비례해 최대 MagnetPx, 밖이면 제자리로. 배지 같은 딸린 것은 같이 간다.
    // 집은 안 적어둔다 - 부모 패널의 패럴랙스가 노드를 같이 옮기므로 적용한 오프셋의 차분만 더한다.
    void Magnet(GUIItem node, GUIGroup scroller, GUIItem attached = null)
    {
        Vector2 offset = Vector2.zero;
        node.whenTick += (_, dt) =>
        {
            if (!ready || Mouse.current == null)
                return;
            Vector2 m = GUIManager.MousePos;
            Vector2 d = m + scroller.ScrollPosition - (node.Rect.center - offset);
            Vector2 target = scroller.Rect.Contains(m) && d.magnitude < MagnetRadius ? d * (MagnetPx / MagnetRadius) : Vector2.zero;
            Vector2 next = Vector2.Lerp(offset, target, 1f - Mathf.Exp(-12f * dt));
            node.SetPos(node.Pos + next - offset);
            attached?.SetPos(attached.Pos + next - offset);
            offset = next;
        };
    }

    // 항로도가 마우스 쪽으로 살짝 기운다. 그룹 SetPos는 자식을 같이 옮기므로 스크롤 안 노드도 따라간다.
    // 등장 모션이 끝난 뒤에만 - 진행 중인 MoveTo가 잡은 절대 좌표와 싸우지 않는다. 읽는 패널(Info·상태)은 안 움직인다.
    void Parallax(GUIGroup panel, float depth)
    {
        Vector2 home = panel.Pos;
        Vector2 offset = Vector2.zero;
        panel.whenTick += (_, dt) =>
        {
            if (!ready || Mouse.current == null)
                return;
            Vector2 m = GUIManager.MousePos;
            Vector2 n = new Vector2(m.x / GUIManager.LogicalWidth - 0.5f, m.y / GUIManager.LogicalHeight - 0.5f);
            offset = Vector2.Lerp(offset, n * ParallaxPx * depth, 1f - Mathf.Exp(-12f * dt));
            panel.SetPos(home + offset);
        };
    }

    static void Sfx(string name) => SoundManager.AudioShot(name, 0.6f);

    void Scroll(float dy) => nav.ScrollPosition.y = Mathf.Clamp(nav.ScrollPosition.y + dy, 0f, scrollMax);

    void Keys()
    {
        Keyboard k = Keyboard.current;
        if (k == null || !ready)
            return;

        if (k.leftArrowKey.wasPressedThisFrame)
            Step(-1);
        else if (k.rightArrowKey.wasPressedThisFrame)
            Step(1);
        else if (k.enterKey.wasPressedThisFrame && depart.isVisible)
            Depart();
        else if (k.rKey.wasPressedThisFrame && k.shiftKey.isPressed)
            Repair(RunState.Materials, repairButtons[2]);
        else if (k.rKey.wasPressedThisFrame)
            Repair(1, repairButtons[0]);
        else if (k.upArrowKey.wasPressedThisFrame)
            Scroll(-NodeStep);
        else if (k.downArrowKey.wasPressedThisFrame)
            Scroll(NodeStep);
    }

    // 옆 갈래로. null 갈래는 건너뛰고, 한 바퀴 돌아 제자리면 아무 일도 없다.
    void Step(int dir)
    {
        int n = lanes.Length;
        if (n == 0)
            return;
        int k = lane;
        for (int i = 0; i < n; i++)
        {
            k = (k + dir + n) % n;
            if (lanes[k] != null)
                break;
        }
        if (k != lane)
            Select(k, announce: true);
    }

    // 암전 → (항로 확정 + 텔레포트) → 걷힘. 다 덮인 순간에 세계를 푼다.
    void Depart()
    {
        if (!ready)
            return;
        ready = false;

        Campaign c = Campaign.current;
        int chosen = Mathf.Max(lane, 0);

        Sfx(SfxDepart);
        KillTree(window);
        GUIManager.Unregister(window);
        window = null;

        black.isVisible = true;
        black.Opacity = 0f;
        black.FadeTo(1f, 0.45f, ease: TweenHelper.EaseInQuad, onComplete: () =>
        {
            c.Depart(chosen);
            IsOpen = false;
            Core.TickManager.Paused = false;
            Time.timeScale = 1f;
            black.FadeTo(0f, 0.45f, ease: TweenHelper.EaseOutQuad, onComplete: () => black.isVisible = false);
        });
    }

    // Unregister는 트윈을 모른다. 진행 중인 것을 안 끊으면 GUITween의 표에 죽은 아이템이 남는다.
    static void KillTree(GUIItem item)
    {
        GUITween.Kill(item);
        if (item is GUIGroup g)
            foreach (GUIItem child in g.Childrens)
                KillTree(child);
    }

    static Rect Inset(Rect r, float p) => new Rect(r.x + p, r.y + p, r.width - p * 2f, r.height - p * 2f);
}
