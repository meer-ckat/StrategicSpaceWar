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
    public const float PanelFraction = 0.5f;   // 0.4는 모듈 행(이름 + 교체 + 후보 가격)이 안 들어갔다

    const int WindowLayer = UiLayer.Screen;   // LogisticsScreen과 같다. 둘이 동시에 열리는 일이 없다
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
    GUIButton depart, refuel, rearm, buyAmmo;

    /// <summary>크레딧 1로 사는 탄약 칸. 격파 급여 한 척(destroyer 160 CR)이 V2 적재의 절반쯤이다 -
    /// 한 판 이기면 반만 채워지므로 수리와 경쟁이 생긴다.</summary>
    const int AmmoPerCredit = 500;
    GUILabel propellantValue, munitionsValue;
    GUIStyle rowLabel, rowValue;
    GUIBoxLabel black;
    GUIStyle damagedBox, repairedBox;
    readonly Dictionary<Component, float> repairedUntil = new();
    readonly List<Armor> repairing = new();
    GUIGroup lostGroup;
    GUIStyle lostLabel, lostButton;
    Rect lostRect;
    float lostScrollMax;
    List<Ship.ModuleSlot> lost = new();

    // 펼친 자리의 설계 배치 인덱스. -1이면 접혀 있다.
    int openSlot = -1;
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

        GUIStyle eyebrow = GUIStyleMaker.Label(Palette.Steel, 12).Font(12, FontStyle.Bold);
        GUIStyle heading = GUIStyleMaker.Label(Palette.Hull, 20).Font(20, FontStyle.Bold);
        GUIStyle body = GUIStyleMaker.Label(Palette.Hull, 15).Wrap().RichText();
        GUIStyle button = GUIStyleMaker.Button(Palette.Hull, Palette.Void, Palette.Radiance, null, 16);
        GUIStyle panelStyle = GUIStyleMaker.Box(Palette.DeepSpace);
        GUIStyle card = GUIStyleMaker.Box(Palette.Bulkhead);
        rowLabel = GUIStyleMaker.Label(Palette.Steel, 15);
        rowValue = GUIStyleMaker.Label(Palette.Hull, 15, TextAnchor.MiddleRight).RichText();
        damagedBox ??= GUIStyleMaker.Box(Palette.Breach);
        repairedBox ??= GUIStyleMaker.Box(Palette.Signal);
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

        // 위쪽은 접는다. 모듈 목록이 이 화면의 본문인데(2026-09-13) 상태 4행·버튼 3단·키 안내가
        // 그 위에서 350 px을 먹고 목록에 한두 줄만 남겼다. 상태는 두 칸씩 한 줄, 급유·재보급은 한 줄,
        // 키 안내는 버튼 글자에 넣는다 - 124 px이 목록으로 간다.
        Row2(inn, ref y, "손상 판", out plateValue, "잔고", out materialValue);
        Row2(inn, ref y, "추진제", out propellantValue, "탄약", out munitionsValue);
        y += Gap;

        float bw = (inn.width - Gap * 2f) / 3f;
        repairButtons = new[]
        {
            Widget.Button(panel, "수리 1  (R)", new Rect(inn.x, y, bw, ButtonH), null, button),
            Widget.Button(panel, "수리 10", new Rect(inn.x + bw + Gap, y, bw, ButtonH), null, button),
            Widget.Button(panel, "전량  (⇧R)", new Rect(inn.x + (bw + Gap) * 2f, y, bw, ButtonH), null, button),
        };
        repairButtons[0].callBack = () => Repair(1, repairButtons[0]);
        repairButtons[1].callBack = () => Repair(10, repairButtons[1]);
        repairButtons[2].callBack = () => Repair(RunState.Credits, repairButtons[2]);
        foreach (GUIButton b in repairButtons)
            Hoverable(b);
        y += ButtonH + Gap;

        float tw = (inn.width - Gap * 2f) / 3f;
        refuel = Widget.Button(panel, "급유", new Rect(inn.x, y, tw, ButtonH), Refuel, button);
        Hoverable(refuel);
        rearm = Widget.Button(panel, "재보급", new Rect(inn.x + tw + Gap, y, tw, ButtonH), Rearm, button);
        Hoverable(rearm);
        // 크레딧이 살 것이 수리뿐이라 남아돌았다. 희소한 것(탄약)과 바꿀 수 있어야 돈이 결정이 된다.
        buyAmmo = Widget.Button(panel, "탄약 구입", new Rect(inn.x + (tw + Gap) * 2f, y, tw, ButtonH), BuyAmmo, button);
        Hoverable(buyAmmo);
        y += ButtonH + Gap * 2f;

        depart = Widget.Button(panel, "출항  (Enter)", new Rect(inn.x, inn.yMax - ButtonH, inn.width, ButtonH), Depart, button);
        Hoverable(depart);

        // 모듈 목록은 수리 버튼부터 출항 버튼까지 **전부** 쓴다. 통신 카드가 100 px을 상시 잡고
        // 있었는데 거의 항상 안 보이는 것이라(isVisible false) 목록이 한 줄로 잘렸다. 카드는 목록 위에
        // 떠서 말할 때만 덮는다 - Layer를 명시한다(겹치는 것끼리 동률이면 순서가 정의되지 않는다).
        Rect commsRect = new Rect(panelRect.x, panelRect.yMax - ButtonH - Gap - 100f, panelRect.width, 100f);
        Widget.Label(panel, "모듈  (베이스에서 산다)", new Rect(inn.x, y, inn.width, 16f), eyebrow);
        y += 20f;
        lostRect = new Rect(inn.x, y, inn.width, Mathf.Max(RowH, inn.yMax - ButtonH - Gap - y));
        lostLabel = GUIStyleMaker.Label(Palette.Hull, 14).RichText();
        lostButton = GUIStyleMaker.Button(Palette.Hull, Palette.Bulkhead, Palette.Radiance, null, 13);
        BuildLost();
        comms = Widget.SetLayer(Widget.Window(window, "", commsRect, "Comms", card), WindowLayer + 2);
        Rect cmIn = LogisticsScreen.Inset(commsRect, 12f);
        Widget.Label(comms, "COMMUNICATION", new Rect(cmIn.x, cmIn.y, cmIn.width, 14f), eyebrow);
        commsSpeaker = Widget.Label(comms, "", new Rect(cmIn.x, cmIn.y + 16f, cmIn.width, 16f), GUIStyleMaker.Label(Palette.Hull, 12).Font(12, FontStyle.Bold));
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
        lost = p != null ? p.ModuleSlots() : new List<Ship.ModuleSlot>();

        lostGroup = Widget.SetLayer(Widget.Window(window, "", lostRect, "Lost", GUIStyleMaker.Box(Color.clear)), WindowLayer + 1);
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
            Widget.Label(lostGroup, LogisticsScreen.Tint("없음", Palette.Steel), new Rect(lostRect.x, lostRect.y, lostRect.width, RowH), lostLabel);
            lostScrollMax = 0f;
            return;
        }

        // def별 묶음. 순서는 첫 등장 순. 같은 자리가 여럿이면 버튼은 그중 첫 것을 산다.
        var order = new List<string>();
        var count = new Dictionary<string, int>();
        var first = new Dictionary<string, Ship.ModuleSlot>();
        foreach (Ship.ModuleSlot m in lost)
        {
            // 묶는 열쇠는 설계의 def다. 강화로 다른 것이 서 있어도 자리는 그 자리다.
            string key = m.placement.def;

            if (!count.ContainsKey(key))
            {
                order.Add(key);
                count[key] = 0;
                first[key] = m;
            }
            count[key]++;
        }

        float rowStep = RowH + 6f;
        float bw = 84f;
        float y = lostRect.y;

        foreach (string def in order)
        {
            Ship.ModuleSlot m = first[def];
            string have = m.IsEmpty
                ? LogisticsScreen.Tint("비어 있음", Palette.Breach)
                : (m.current.defName == def ? "" : LogisticsScreen.Tint(m.current.defName, Palette.Telemetry));

            string text = def + "  " + LogisticsScreen.Tint("x" + count[def], Palette.Steel)
                + (have.Length > 0 ? "  " + have : "");

            Widget.Label(lostGroup, text, new Rect(lostRect.x, y, lostRect.width - bw - Gap, RowH), lostLabel);

            // 빈 자리는 설계 그대로 되사기, 찬 자리는 펼쳐서 고른다. 둘이 같은 버튼인 이유는
            // 어느 쪽이든 "이 자리에 무엇을 세우나" 하나이기 때문이다.
            if (m.IsEmpty)
            {
                int price = Ship.PriceOf(def, m.placement);
                bool can = RunState.Credits >= price;
                GUIButton buy = Widget.Button(lostGroup, price + " CR", new Rect(lostRect.xMax - bw, y, bw, RowH), null, lostButton);
                Ship.ModuleSlot pick = m;
                buy.callBack = () => Buy(pick, null, buy);
                buy.isEnabled = buy.isInteractable = can;
                buy.Opacity = can ? 1f : LogisticsScreen.DisabledOpacity;
            }
            else
            {
                bool open = openSlot == m.index;
                GUIButton expand = Widget.Button(lostGroup, open ? "닫기" : "교체", new Rect(lostRect.xMax - bw, y, bw, RowH), null, lostButton);
                int pick = m.index;
                expand.callBack = () => { openSlot = open ? -1 : pick; BuildLost(); };
            }

            y += rowStep;

            if (openSlot != m.index || m.IsEmpty)
                continue;

            Ship ship = Campaign.current.Player;
            List<string> options = ship != null ? ship.Candidates(m) : new List<string>();

            if (options.Count == 0)
            {
                Widget.Label(lostGroup, LogisticsScreen.Tint("   맞는 것이 없다", Palette.Steel),
                    new Rect(lostRect.x, y, lostRect.width, RowH), lostLabel);
                y += rowStep;
                continue;
            }

            foreach (string option in options)
            {
                int price = Ship.PriceOf(option, m.placement);
                bool can = RunState.Credits >= price;

                Widget.Label(lostGroup, "   " + option, new Rect(lostRect.x, y, lostRect.width - bw - Gap, RowH), lostLabel);
                GUIButton swap = Widget.Button(lostGroup, price + " CR", new Rect(lostRect.xMax - bw, y, bw, RowH), null, lostButton);
                Ship.ModuleSlot at = m;
                string what = option;
                swap.callBack = () => Buy(at, what, swap);
                swap.isEnabled = swap.isInteractable = can;
                swap.Opacity = can ? 1f : LogisticsScreen.DisabledOpacity;
                y += rowStep;
            }
        }

        lostScrollMax = Mathf.Max(0f, y - lostRect.y - lostRect.height);
    }

    void Buy(Ship.ModuleSlot m, string defName, GUIButton pressed)
    {
        Ship p = Campaign.current.Player;
        int price = Ship.PriceOf(string.IsNullOrEmpty(defName) ? m.placement.def : defName, m.placement);

        if (p == null || !ready || !pressed.isInteractable || RunState.Credits < price)
            return;

        Thing bought = p.BuyModule(m, defName);
        if (bought == null)
        {
            BuildLost();
            return;
        }

        int m0 = RunState.Credits;
        RunState.Credits -= price;
        RunState.Save(p);
        repairedUntil[bought] = Time.unscaledTime + RepairFlash;

        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        RefreshStatus();
        BuildLost();
        Tween01(x => RenderStatus(p.DamagedPlateCount(), Mathf.RoundToInt(Mathf.Lerp(m0, RunState.Credits, x))));
    }

    /// <summary>한 줄에 두 칸. 각 칸은 라벨 반 · 값 반.</summary>
    void Row2(Rect area, ref float y, string labelA, out GUILabel a, string labelB, out GUILabel b)
    {
        float half = area.width * 0.5f, q = half * 0.5f;
        Widget.Label(panel, labelA, new Rect(area.x, y, q, RowH), rowLabel);
        a = Widget.Label(panel, "", new Rect(area.x + q, y, q - Gap, RowH), rowValue);
        Widget.Label(panel, labelB, new Rect(area.x + half, y, q, RowH), rowLabel);
        b = Widget.Label(panel, "", new Rect(area.x + half + q, y, q, RowH), rowValue);
        y += RowH;
    }

    GUILabel Row(Rect area, ref float y, string label)
    {
        Widget.Label(panel, label, new Rect(area.x, y, area.width * 0.5f, RowH), rowLabel);
        GUILabel v = Widget.Label(panel, "", new Rect(area.x + area.width * 0.5f, y, area.width * 0.5f, RowH), rowValue);
        y += RowH;
        return v;
    }

    void RenderStatus(int damaged, int credits)
    {
        plateValue.Content.text = LogisticsScreen.Tint(damaged.ToString(), damaged > 0 ? Palette.Breach : Palette.Signal);
        materialValue.Content.text = $"<b>{credits}</b>";
    }

    void RefreshStatus()
    {
        Ship p = Campaign.current.Player;
        int damaged = p != null ? p.DamagedPlateCount() : 0;
        bool can = damaged > 0 && RunState.Credits > 0;

        RenderStatus(damaged, RunState.Credits);
        foreach (GUIButton b in repairButtons)
        {
            b.isEnabled = b.isInteractable = can;
            b.Opacity = can ? 1f : LogisticsScreen.DisabledOpacity;
        }

        propellantValue.Content.text = $"<b>{RunState.Propellant}</b>";
        bool canFuel = p != null && RunState.Propellant > 0 && p.shipTanks.Count > 0;
        refuel.isEnabled = refuel.isInteractable = canFuel;
        refuel.Opacity = canFuel ? 1f : LogisticsScreen.DisabledOpacity;

        munitionsValue.Content.text = $"<b>{RunState.Munitions}</b>";
        bool canArm = p != null && RunState.Munitions > 0 && p.Rounds < p.MaxRounds;
        rearm.isEnabled = rearm.isInteractable = canArm;
        rearm.Opacity = canArm ? 1f : LogisticsScreen.DisabledOpacity;

        bool canBuy = RunState.Credits > 0 && RunState.Munitions < RunState.MaxMunitions;
        buyAmmo.isEnabled = buyAmmo.isInteractable = canBuy;
        buyAmmo.Opacity = canBuy ? 1f : LogisticsScreen.DisabledOpacity;

        // 수리가 돈을 쓰면 살 수 있던 모듈이 못 사는 것이 된다. 값은 버튼 글자 앞 숫자다.
        if (lostGroup != null)
            foreach (GUIItem item in lostGroup.Childrens)
                if (item is GUIButton buy)
                {
                    int.TryParse(buy.Content.text.Split(' ')[0], out int cost);
                    bool ok = RunState.Credits >= cost;
                    buy.isEnabled = buy.isInteractable = ok;
                    buy.Opacity = ok ? 1f : LogisticsScreen.DisabledOpacity;
                }
    }

    // 배가 실제로 쓴 만큼만 뺀다. 수리마다 배를 저장한다 - Battle.End의 저장은 수리 전이라,
    // 여기서 안 쓰면 돈은 빠졌는데(Credits setter는 즉시 쓴다) 수리는 다음 승리까지 파일에 없다.
    void Repair(int want, GUIButton pressed)
    {
        Ship p = Campaign.current.Player;
        if (p == null || !pressed.isInteractable)
            return;

        int d0 = p.DamagedPlateCount(), m0 = RunState.Credits;

        repairing.Clear();
        foreach (Armor a in p.shipArmors)
            if (a != null && a.HealthFraction < Ship.DamagedBelow)
                repairing.Add(a);

        int used = p.RepairPlates(Mathf.Min(want, RunState.Credits));
        RunState.Credits -= used;
        if (used > 0)
            RunState.Save(p);

        // 손상이었다가 지금 멀쩡한 판이 방금 고친 판이다.
        foreach (Armor a in repairing)
            if (a.HealthFraction >= Ship.DamagedBelow)
                repairedUntil[a] = Time.unscaledTime + RepairFlash;
        int d1 = p.DamagedPlateCount(), m1 = RunState.Credits;

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

        // 동료도 같은 창고에서. 출구에서 워프 Δv를 못 내면 낙오하므로, 여기가 그걸 막는 유일한 자리다.
        foreach (Ship mate in Campaign.current.Wingmates())
            poured += Mathf.RoundToInt(mate.Refuel(RunState.Propellant - poured));

        if (poured <= 0)
            return;

        RunState.Propellant -= poured;
        RunState.Save(p);
        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        LogisticsScreen.Punch(refuel);
        RefreshStatus();
    }

    // 창고의 탄약(발)을 탄약고에 넣는다. 급유와 같은 모양.
    void Rearm()
    {
        Ship p = Campaign.current.Player;

        if (p == null || !rearm.isInteractable)
            return;

        int loaded = p.Rearm(RunState.Munitions);

        if (loaded <= 0)
            return;

        RunState.Munitions -= loaded;
        RunState.Save(p);
        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        LogisticsScreen.Punch(rearm);
        RefreshStatus();
    }

    // 크레딧을 탄약으로. 창고가 찰 만큼만 사고 남는 돈은 그대로 둔다 - 반올림으로 조용히
    // 사라지면 "돈이 어디 갔나"가 되고, 그건 잔고가 있는 화면에서 제일 나쁜 버그다.
    void BuyAmmo()
    {
        if (!buyAmmo.isInteractable)
            return;

        int room = RunState.MaxMunitions - RunState.Munitions;
        int afford = Mathf.Min(RunState.Credits, Mathf.CeilToInt((float)room / AmmoPerCredit));

        if (afford <= 0)
            return;

        RunState.Credits -= afford;
        RunState.Munitions += afford * AmmoPerCredit;
        RunState.Save(Campaign.current.Player);
        LogisticsScreen.Sfx(LogisticsScreen.SfxClick);
        LogisticsScreen.Punch(buyAmmo);
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
            Repair(RunState.Credits, repairButtons[2]);
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
