using System;
using System.Collections.Generic;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

// 정비 노드. 배는 씬에 그대로 보이고(Campaign.EnterRefit이 카메라를 왼쪽에 놓는다) 오른쪽 PanelFraction만 패널이다.
// 고친 판이 배에서 바로 멀쩡해지는 것이 이 화면의 값어치라 화면을 덮지 않는다.
// 초안 - 배치·색·문구는 오너가 고친다. 팔레트·헬퍼는 LogisticsScreen 것을 그대로 쓴다(두 화면이 한 목소리).
public sealed class RefitScreen : MonoBehaviour
{
    public static bool IsOpen;

    /// <summary>패널이 덮는 화면 오른쪽 비율. Campaign이 카메라 오프셋에 같은 값을 쓴다.</summary>
    public const float PanelFraction = 0.4f;

    const int WindowLayer = 880;   // LogisticsScreen과 같다. 둘이 동시에 열리는 일이 없다
    const float Margin = 24f, Padding = 16f, Gap = 8f, RowH = 22f, ButtonH = 40f;
    const float OpenFade = 0.22f;
    const float BarkSeconds = 4f;
    const float BlackIn = 0.3f, BlackOut = 0.4f;   // 출항: 정비 → 암전 → 항로 화면. 없으면 씬에서 전체 화면으로 뚝 끊긴다
    const float RepairFlash = 0.8f;                // 방금 고친 판이 초록으로 남는 시간
    const float DamageAlpha = 0.55f;               // 손상 판 표시의 최대 불투명도(체력 0일 때)

    static RefitScreen instance;

    GUIGroup window, panel, comms;
    GUILabel plateValue, materialValue, commsSpeaker, commsText;
    GUIButton[] repairButtons;
    GUIButton depart, refuel;
    GUILabel propellantValue;
    GUIStyle rowLabel, rowValue;
    GUIBoxLabel black;
    GUIStyle damagedBox, repairedBox;
    readonly Dictionary<Component, float> repairedUntil = new();
    readonly List<Armor> repairing = new();
    GUIGroup lostGroup;
    GUIStyle lostLabel, lostButton;
    Rect lostRect;
    float lostScrollMax;
    List<Ship.LostModule> lost = new();
    bool ready;
    float barkUntil;
    Action<GUIItem, float> counting;
    LogisticsScreen.Comms commsDef;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (FindFirstObjectByType<RefitScreen>() != null)
            return;

        var go = new GameObject("Refit");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<RefitScreen>();
    }

    void Awake() => instance = this;

    /// <summary>false면 화면을 못 연다(설치 안 된 씬). Campaign이 항로 화면으로 대신 간다.</summary>
    public static bool Open()
    {
        if (instance == null || Campaign.current == null)
            return false;

        instance.Build();
        IsOpen = true;
        return true;
    }

    void Build()
    {
        Campaign c = Campaign.current;

        GUIStyle eyebrow = GUIStyleMaker.Label(LogisticsScreen.Dim, 12).Font(12, FontStyle.Bold);
        GUIStyle heading = GUIStyleMaker.Label(LogisticsScreen.White, 20).Font(20, FontStyle.Bold);
        GUIStyle body = GUIStyleMaker.Label(LogisticsScreen.White, 15).Wrap().RichText();
        GUIStyle button = GUIStyleMaker.Button(LogisticsScreen.White, LogisticsScreen.Obsidian, LogisticsScreen.Orange, null, 16);
        GUIStyle panelStyle = GUIStyleMaker.Box(LogisticsScreen.Panel);
        GUIStyle card = GUIStyleMaker.Box(LogisticsScreen.Surface);
        rowLabel = GUIStyleMaker.Label(LogisticsScreen.Dim, 15);
        rowValue = GUIStyleMaker.Label(LogisticsScreen.White, 15, TextAnchor.MiddleRight).RichText();
        damagedBox ??= GUIStyleMaker.Box(LogisticsScreen.Red);
        repairedBox ??= GUIStyleMaker.Box(LogisticsScreen.Green);
        repairedUntil.Clear();

        Rect screen = new Rect(0f, 0f, GUIManager.LogicalWidth, GUIManager.LogicalHeight);
        float panelW = screen.width * PanelFraction - Margin;
        Rect panelRect = new Rect(screen.xMax - Margin - panelW, Margin, panelW, screen.height - Margin * 2f);

        if (window != null)
        {
            LogisticsScreen.KillTree(window);
            GUIManager.Unregister(window);
        }
        counting = null;

        // 창은 화면 전체지만 바닥을 안 칠한다 - 왼쪽은 씬이다. style을 null로 두면 GUIGroup이
        // GUI.skin.box(어두운 상자)를 그려 배와 손상 표시를 덮는다. 투명 상자를 준다.
        window = Widget.SetLayer(Widget.Window("", screen, "Refit", GUIStyleMaker.Box(Color.clear)), WindowLayer);
        window.whenTick += (_, __) => Keys();

        panel = Widget.Window(window, "", panelRect, "RefitPanel", panelStyle);
        Rect inn = LogisticsScreen.Inset(panelRect, Padding);
        float y = inn.y;

        Widget.Label(panel, "정비", new Rect(inn.x, y, inn.width, 16f), eyebrow);
        y += 20f;
        string where = c.Visited.Count > 0 ? c.Visited[c.Visited.Count - 1].name : "정비";
        Widget.Label(panel, where, new Rect(inn.x, y, inn.width, 28f), heading);
        y += 36f;

        plateValue = Row(inn, ref y, "손상 판");
        materialValue = Row(inn, ref y, "보유 자재");
        propellantValue = Row(inn, ref y, "보유 추진제");
        y += Gap;

        float bw = (inn.width - Gap * 2f) / 3f;
        repairButtons = new[]
        {
            Widget.Button(panel, "수리 1", new Rect(inn.x, y, bw, ButtonH), null, button),
            Widget.Button(panel, "수리 10", new Rect(inn.x + bw + Gap, y, bw, ButtonH), null, button),
            Widget.Button(panel, "전량", new Rect(inn.x + (bw + Gap) * 2f, y, bw, ButtonH), null, button),
        };
        repairButtons[0].callBack = () => Repair(1, repairButtons[0]);
        repairButtons[1].callBack = () => Repair(10, repairButtons[1]);
        repairButtons[2].callBack = () => Repair(RunState.Materials, repairButtons[2]);
        foreach (GUIButton b in repairButtons)
            Hoverable(b);
        y += ButtonH + Gap;
        refuel = Widget.Button(panel, "급유", new Rect(inn.x, y, inn.width, ButtonH), Refuel, button);
        Hoverable(refuel);
        y += ButtonH + Gap;
        Widget.Label(panel, "R 수리 1   Shift+R 전량   Enter 출항", new Rect(inn.x, y, inn.width, 16f), eyebrow);
        y += 16f + Gap * 2f;

        depart = Widget.Button(panel, "출항", new Rect(inn.x, inn.yMax - ButtonH, inn.width, ButtonH), Depart, button);
        Hoverable(depart);

        // 잃은 모듈. 수리 버튼과 통신 카드 사이 전부.
        Rect commsRect = new Rect(panelRect.x, panelRect.yMax - ButtonH - Gap - 100f, panelRect.width, 100f);
        Widget.Label(panel, "잃은 모듈  (자재로 다시 산다)", new Rect(inn.x, y, inn.width, 16f), eyebrow);
        y += 20f;
        lostRect = new Rect(inn.x, y, inn.width, Mathf.Max(RowH, commsRect.y - Gap - y));
        lostLabel = GUIStyleMaker.Label(LogisticsScreen.White, 14).RichText();
        lostButton = GUIStyleMaker.Button(LogisticsScreen.White, LogisticsScreen.Surface, LogisticsScreen.Orange, null, 13);
        BuildLost();
        comms = Widget.Window(window, "", commsRect, "Comms", card);
        Rect cmIn = LogisticsScreen.Inset(commsRect, 12f);
        Widget.Label(comms, "COMMUNICATION", new Rect(cmIn.x, cmIn.y, cmIn.width, 14f), eyebrow);
        commsSpeaker = Widget.Label(comms, "", new Rect(cmIn.x, cmIn.y + 16f, cmIn.width, 16f), GUIStyleMaker.Label(LogisticsScreen.White, 12).Font(12, FontStyle.Bold));
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
        commsDef ??= LogisticsScreen.Comms.Load();

        if (black == null)
        {
            black = Widget.SetLayer(Widget.BoxLabel("", screen, GUIStyleMaker.Box(Color.black)), WindowLayer + 10);
            black.isVisible = false;
            black.isInteractable = false;
        }

        RefreshStatus();

        ready = false;
        window.isInteractable = false;
        panel.FadeIn(OpenFade, 0f, TweenHelper.EaseOutQuad, onComplete: () =>
        {
            ready = true;
            window.isInteractable = true;
            Bark("refit");
        });
    }

    // 목록은 살 때마다 다시 짓는다 - 행이 줄고 값이 바뀐다. 같은 def는 한 줄로 묶고 버튼은 그중 첫 것을 산다.
    void BuildLost()
    {
        if (lostGroup != null)
        {
            LogisticsScreen.KillTree(lostGroup);
            GUIManager.Unregister(lostGroup);
            lostGroup = null;
        }

        Ship p = Campaign.current.Player;
        lost = p != null ? p.LostModules() : new List<Ship.LostModule>();

        lostGroup = Widget.Window(window, "", lostRect, "Lost", GUIStyleMaker.Box(Color.clear));
        lostGroup.Scrollable = true;
        lostGroup.ScrollPosition = Vector2.zero;
        lostGroup.whenTick += (_, __) =>
        {
            if (Mouse.current != null && lostGroup.Rect.Contains(GUIManager.MousePos))
                lostGroup.ScrollPosition.y = Mathf.Clamp(
                    lostGroup.ScrollPosition.y - Mouse.current.scroll.ReadValue().y * 0.6f, 0f, lostScrollMax);
        };

        if (lost.Count == 0)
        {
            Widget.Label(lostGroup, LogisticsScreen.Tint("없음", LogisticsScreen.Dim), new Rect(lostRect.x, lostRect.y, lostRect.width, RowH), lostLabel);
            lostScrollMax = 0f;
            return;
        }

        // def별 묶음. 순서는 첫 등장 순.
        var order = new List<string>();
        var count = new Dictionary<string, int>();
        var first = new Dictionary<string, Ship.LostModule>();
        foreach (Ship.LostModule m in lost)
        {
            if (!count.ContainsKey(m.placement.def))
            {
                order.Add(m.placement.def);
                count[m.placement.def] = 0;
                first[m.placement.def] = m;
            }
            count[m.placement.def]++;
        }

        float rowStep = RowH + 6f;
        float bw = 84f;
        float y = lostRect.y;
        foreach (string def in order)
        {
            Ship.LostModule m = first[def];
            bool can = RunState.Materials >= m.cost;
            string text = def + "  " + LogisticsScreen.Tint("x" + count[def], LogisticsScreen.Dim);
            Widget.Label(lostGroup, text, new Rect(lostRect.x, y, lostRect.width - bw - Gap, RowH), lostLabel);
            GUIButton buy = Widget.Button(lostGroup, m.cost + " 자재", new Rect(lostRect.xMax - bw, y, bw, RowH), null, lostButton);
            Ship.LostModule pick = m;
            buy.callBack = () => Buy(pick, buy);
            buy.isEnabled = buy.isInteractable = can;
            buy.Opacity = can ? 1f : LogisticsScreen.DisabledOpacity;
            y += rowStep;
        }
        lostScrollMax = Mathf.Max(0f, y - lostRect.y - lostRect.height);
    }

    void Buy(Ship.LostModule m, GUIButton pressed)
    {
        Ship p = Campaign.current.Player;
        if (p == null || !ready || !pressed.isInteractable || RunState.Materials < m.cost)
            return;

        Thing bought = p.BuyModule(m);
        if (bought == null)
        {
            BuildLost();
            return;
        }

        int m0 = RunState.Materials;
        RunState.Materials -= m.cost;
        RunState.Save(p);
        repairedUntil[bought] = Time.unscaledTime + RepairFlash;

        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        RefreshStatus();
        BuildLost();
        Tween01(x => RenderStatus(p.DamagedPlateCount(), Mathf.RoundToInt(Mathf.Lerp(m0, RunState.Materials, x))));
    }

    GUILabel Row(Rect area, ref float y, string label)
    {
        Widget.Label(panel, label, new Rect(area.x, y, area.width * 0.5f, RowH), rowLabel);
        GUILabel v = Widget.Label(panel, "", new Rect(area.x + area.width * 0.5f, y, area.width * 0.5f, RowH), rowValue);
        y += RowH;
        return v;
    }

    void RenderStatus(int damaged, int materials)
    {
        plateValue.Content.text = LogisticsScreen.Tint(damaged.ToString(), damaged > 0 ? LogisticsScreen.Red : LogisticsScreen.Green);
        materialValue.Content.text = $"<b>{materials}</b>";
    }

    void RefreshStatus()
    {
        Ship p = Campaign.current.Player;
        int damaged = p != null ? p.DamagedPlateCount() : 0;
        bool can = damaged > 0 && RunState.Materials > 0;

        RenderStatus(damaged, RunState.Materials);
        foreach (GUIButton b in repairButtons)
        {
            b.isEnabled = b.isInteractable = can;
            b.Opacity = can ? 1f : LogisticsScreen.DisabledOpacity;
        }

        propellantValue.Content.text = $"<b>{RunState.Propellant}</b>";
        bool canFuel = p != null && RunState.Propellant > 0 && p.shipTanks.Count > 0;
        refuel.isEnabled = refuel.isInteractable = canFuel;
        refuel.Opacity = canFuel ? 1f : LogisticsScreen.DisabledOpacity;

        // 수리가 자재를 쓰면 살 수 있던 모듈이 못 사는 것이 된다. 값은 버튼 글자 앞 숫자다.
        if (lostGroup != null)
            foreach (GUIItem item in lostGroup.Childrens)
                if (item is GUIButton buy)
                {
                    int.TryParse(buy.Content.text.Split(' ')[0], out int cost);
                    bool ok = RunState.Materials >= cost;
                    buy.isEnabled = buy.isInteractable = ok;
                    buy.Opacity = ok ? 1f : LogisticsScreen.DisabledOpacity;
                }
    }

    // 배가 실제로 쓴 만큼만 뺀다. 수리마다 배를 저장한다 - Battle.End의 저장은 수리 전이라,
    // 여기서 안 쓰면 자재는 빠졌는데(Materials setter는 즉시 쓴다) 수리는 다음 승리까지 파일에 없다.
    void Repair(int want, GUIButton pressed)
    {
        Ship p = Campaign.current.Player;
        if (p == null || !pressed.isInteractable)
            return;

        int d0 = p.DamagedPlateCount(), m0 = RunState.Materials;

        repairing.Clear();
        foreach (Armor a in p.shipArmors)
            if (a != null && a.HealthFraction < Ship.DamagedBelow)
                repairing.Add(a);

        int used = p.RepairPlates(Mathf.Min(want, RunState.Materials));
        RunState.Materials -= used;
        if (used > 0)
            RunState.Save(p);

        // 손상이었다가 지금 멀쩡한 판이 방금 고친 판이다.
        foreach (Armor a in repairing)
            if (a.HealthFraction >= Ship.DamagedBelow)
                repairedUntil[a] = Time.unscaledTime + RepairFlash;
        int d1 = p.DamagedPlateCount(), m1 = RunState.Materials;

        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        LogisticsScreen.Punch(pressed);
        RefreshStatus();
        Tween01(x => RenderStatus(Mathf.RoundToInt(Mathf.Lerp(d0, d1, x)), Mathf.RoundToInt(Mathf.Lerp(m0, m1, x))));
    }

    // 창고의 추진제(kN·s)를 탱크에 붓는다. 빈 자리만큼만 들어가고 나머지는 창고에 남는다.
    void Refuel()
    {
        Ship p = Campaign.current.Player;
        if (p == null || !refuel.isInteractable)
            return;

        int poured = Mathf.RoundToInt(p.Refuel(RunState.Propellant));
        if (poured <= 0)
            return;

        RunState.Propellant -= poured;
        RunState.Save(p);
        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        LogisticsScreen.Punch(refuel);
        RefreshStatus();
    }

    void Tween01(Action<float> onX)
    {
        if (counting != null)
            window.whenTick -= counting;

        float t = 0f;
        counting = (_, dt) =>
        {
            t += dt;
            float x = Mathf.Clamp01(t / LogisticsScreen.CountUp);
            onX(TweenHelper.EaseOutQuad(x));
            if (x < 1f)
                return;
            window.whenTick -= counting;
            counting = null;
        };
        window.whenTick += counting;
    }

    void Bark(string when)
    {
        LogisticsScreen.Comms.Bark b = when != null ? commsDef.Find(when) : null;
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

    void Hoverable(GUIItem item)
    {
        bool over = false;
        item.whenTick += (_, __) =>
        {
            bool now = ready && item.isVisible && item.isInteractable && item.Rect.Contains(GUIManager.MousePos);
            if (now && !over)
                LogisticsScreen.Sfx(LogisticsScreen.SfxHover);
            over = now;
        };
    }

    void Keys()
    {
        Keyboard k = Keyboard.current;
        if (k == null || !ready)
            return;

        if (k.enterKey.wasPressedThisFrame)
            Depart();
        else if (k.rKey.wasPressedThisFrame && k.shiftKey.isPressed)
            Repair(RunState.Materials, repairButtons[2]);
        else if (k.rKey.wasPressedThisFrame)
            Repair(1, repairButtons[0]);
    }

    // 출항. 암전 밑에서 창을 걷고 Campaign이 항로 화면을 열면 다시 걷는다.
    void Depart()
    {
        if (!ready)
            return;
        ready = false;

        LogisticsScreen.Sfx(LogisticsScreen.SfxSelect);
        StartCoroutine(DepartRoutine());
    }

    System.Collections.IEnumerator DepartRoutine()
    {
        black.SetRect(new Rect(0f, 0f, GUIManager.LogicalWidth, GUIManager.LogicalHeight));
        black.Opacity = 0f;
        black.isVisible = true;
        black.FadeTo(1f, BlackIn, ease: TweenHelper.EaseInQuad);
        yield return new WaitForSecondsRealtime(BlackIn + 0.02f);

        LogisticsScreen.KillTree(window);
        GUIManager.Unregister(window);
        window = null;
        IsOpen = false;

        Campaign.current?.DepartRefit();   // 항로 화면이 검정 밑에서 지어진다

        black.FadeTo(0f, BlackOut, ease: TweenHelper.EaseOutQuad, onComplete: () => black.isVisible = false);
    }

    // 손상 판 위에 붉은 상자(체력이 낮을수록 진하다), 방금 고친 판에 초록 상자. 배 그림만으로는 어디가
    // 상했는지 안 읽힌다 - 손상 마스크가 서브셀 단위라 멀리서는 색 차이뿐이다. 선언을 그만두면 사라진다.
    void Update()
    {
        if (!IsOpen || Campaign.current == null)
            return;

        Ship p = Campaign.current.Player;
        Camera cam = Camera.main;
        if (p == null || cam == null)
            return;

        ImGui.Begin();

        float now = Time.unscaledTime;
        for (int i = 0; i < p.shipArmors.Count; i++)
        {
            Armor a = p.shipArmors[i];
            if (a == null || !Ship.StillAboard(a, p))
                continue;

            float hp = a.HealthFraction;
            bool repaired = repairedUntil.TryGetValue(a, out float until) && now < until;
            if (hp >= Ship.DamagedBelow && !repaired)
                continue;

            Mark(cam, a, repaired ? repairedBox : damagedBox,
                repaired ? Mathf.Clamp01((until - now) / RepairFlash) : Mathf.Lerp(0.15f, DamageAlpha, 1f - hp));
        }

        // 방금 산 모듈. 판은 위에서 이미 그렸다.
        foreach (KeyValuePair<Component, float> kv in repairedUntil)
            if (kv.Key != null && kv.Key is not Armor && now < kv.Value)
                Mark(cam, kv.Key, repairedBox, Mathf.Clamp01((kv.Value - now) / RepairFlash));
    }

    void Mark(Camera cam, Component part, GUIStyle style, float opacity)
    {
        if (!part.TryGetComponent(out Collider2D col))
            return;

        Bounds b = col.bounds;
        Vector2 lo = (Vector2)cam.WorldToScreenPoint(b.min) / GUIManager.UiScale;
        Vector2 hi = (Vector2)cam.WorldToScreenPoint(b.max) / GUIManager.UiScale;
        // GUI는 y가 아래로 증가한다.
        Rect r = Rect.MinMaxRect(
            Mathf.Min(lo.x, hi.x), GUIManager.LogicalHeight - Mathf.Max(lo.y, hi.y),
            Mathf.Max(lo.x, hi.x), GUIManager.LogicalHeight - Mathf.Min(lo.y, hi.y));

        GUIBoxLabel box = ImGui.BoxLabel($"refit:mark:{part.GetInstanceID()}", r, "", style);
        box.Layer = WindowLayer - 1;
        box.isInteractable = false;
        box.Opacity = opacity;
    }
}
