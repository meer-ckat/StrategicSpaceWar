using IMGUI;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 실제 피격점 옆에 관통 / 비관통 / 도탄만 표시한다.
///
/// GUIManager / Widget / Tween / DamageLog 유지.
/// Indicator 풀 + Impact 풀 유지.
///
/// 오른쪽 공간이 있으면:
///
///     <---------------- [ICON]
///     ^
///     hit
///
/// 오른쪽이 좁으면:
///
///     [ICON] ---------------->
///                            ^
///                            hit
///
/// 화살표는 화면 공간보다 길어지지 않는다.
/// </summary>
public sealed class HitIndicatorUI : MonoBehaviour
{
    [SerializeField] private Camera view;

    [Header("표시")]
    [SerializeField] private float holdSeconds = 1.1f;
    [SerializeField] private int layer = 90;

    [Header("배치")]
    [SerializeField] private float screenMargin = 16f;
    [SerializeField] private float minArrowLength = 28f;
    [SerializeField] private float maxArrowLength = 140f;
    [SerializeField] private float iconGap = 6f;

    [SerializeField]
    private Vector2 iconSize = new Vector2(48f, 32f);

    [Header("아이콘")]
    [SerializeField] private Texture2D penetratedIcon;
    [SerializeField] private Texture2D blockedIcon;
    [SerializeField] private Texture2D ricochetIcon;

    [Header("풀")]
    [FormerlySerializedAs("plateCount")]
    [SerializeField] private int indicatorCount = 4;

    [FormerlySerializedAs("popupCount")]
    [SerializeField] private int impactCount = 8;

    private static readonly Color Transparent =
        new Color(0f, 0f, 0f, 0f);

    private static readonly Color PenetratedColor =
        new Color(0.23f, 0.88f, 0.51f, 1f);

    private static readonly Color RicochetColor =
        new Color(1f, 0.79f, 0.24f, 1f);

    private static readonly Color BlockedColor =
        new Color(1f, 0.18f, 0.18f, 1f);

    private const float LineHeight = 2f;
    private const float ArrowHeadWidth = 12f;
    private const float ArrowHeadHeight = 20f;

    private const float ImpactSize = 8f;

    private sealed class Indicator
    {
        public GUIGroup root;

        public GUIImage line;
        public GUILabel head;
        public GUIImage icon;

        public DamageLog.ArmorMark mark;

        public bool busy;
        public bool releasing;
    }

    private sealed class Impact
    {
        public GUIGroup root;
        public GUIImage dot;

        public DamageLog.ArmorMark mark;

        public bool busy;
    }

    private Indicator[] _indicators;
    private Impact[] _impacts;

    private bool _built;

    // GUIStyleMaker는 OnGUI 안에서 초기화해야 한다.
    private void OnGUI()
    {
        GUIStyleMaker.Initialize();
    }

    private void Awake()
    {
        if (view == null)
            view = Camera.main;
    }

    private void Update()
    {
        if (!_built)
        {
            if (!GUIStyleMaker.Initialized)
                return;

            Build();
            _built = true;
        }

        if (view == null)
            view = Camera.main;

        if (view == null)
            return;

        DamageLog.PruneArmors(holdSeconds);

        SyncIndicators();
        SyncImpacts();
    }

    private void OnDisable()
    {
        if (!_built)
            return;

        foreach (Indicator indicator in _indicators)
        {
            GUITween.Kill(indicator.root);
            GUITween.Kill(indicator.icon);

            GUIManager.Unregister(indicator.root);
        }

        foreach (Impact impact in _impacts)
        {
            GUITween.Kill(impact.root);
            GUITween.Kill(impact.dot);

            GUIManager.Unregister(impact.root);
        }

        _built = false;
    }

    // =========================================================
    // BUILD
    // =========================================================

    private void Build()
    {
        if (GUIManager.instance == null)
        {
            Debug.LogWarning(
                "[HitIndicatorUI] 씬에 GUIManager가 없다.");
        }

        _indicators =
            new Indicator[Mathf.Max(1, indicatorCount)];

        for (int i = 0; i < _indicators.Length; i++)
            _indicators[i] = BuildIndicator(i);

        _impacts =
            new Impact[Mathf.Max(1, impactCount)];

        for (int i = 0; i < _impacts.Length; i++)
            _impacts[i] = BuildImpact(i);
    }

    private Indicator BuildIndicator(int index)
    {
        var indicator = new Indicator();

        float maxWidth =
            maxArrowLength +
            iconGap +
            iconSize.x;

        indicator.root = Widget.Window(
            "",
            new Rect(
                0f,
                0f,
                maxWidth,
                iconSize.y),
            $"HitIndicator_{index}",
            GUIStyleMaker
                .Box(Transparent)
                .NoSpacing()
                .Border(0));

        GUILayoutManager.SetPadding(
            indicator.root,
            0f);

        indicator.root.Mask = false;

        indicator.line = Widget.Image(
            indicator.root,
            GUIStyleMaker.Solid(PenetratedColor),
            new Rect());

        indicator.head = Widget.Label(
            indicator.root,
            "<",
            new Rect(),
            GUIStyleMaker
                .Label(
                    PenetratedColor,
                    16,
                    TextAnchor.MiddleCenter)
                .Font(
                    16,
                    FontStyle.Bold));

        indicator.icon = Widget.Image(
            indicator.root,
            penetratedIcon,
            new Rect(),
            ScaleMode.ScaleToFit);

        Widget.Layer(
            indicator.root,
            layer);

        Widget.Visible(
            indicator.root,
            false);

        return indicator;
    }

    private Impact BuildImpact(int index)
    {
        var impact = new Impact();

        impact.root = Widget.Window(
            "",
            new Rect(
                0f,
                0f,
                ImpactSize,
                ImpactSize),
            $"HitImpact_{index}",
            GUIStyleMaker
                .Box(Transparent)
                .NoSpacing()
                .Border(0));

        GUILayoutManager.SetPadding(
            impact.root,
            0f);

        impact.root.Mask = false;

        impact.dot = Widget.Image(
            impact.root,
            GUIStyleMaker.Solid(PenetratedColor),
            new Rect(
                0f,
                0f,
                ImpactSize,
                ImpactSize));

        Widget.Layer(
            impact.root,
            layer + 1);

        Widget.Visible(
            impact.root,
            false);

        return impact;
    }

    // =========================================================
    // INDICATORS
    // =========================================================

    private void SyncIndicators()
    {
        // 로그에서 사라진 표시를 닫는다.
        foreach (Indicator indicator in _indicators)
        {
            if (!indicator.busy ||
                indicator.releasing)
            {
                continue;
            }

            if (!StillLogged(indicator.mark.armorId))
                Release(indicator);
        }

        // 새 로그를 표시하거나 기존 표시를 갱신한다.
        foreach (DamageLog.ArmorMark mark in DamageLog.Armors)
        {
            Indicator indicator =
                FindIndicator(mark.armorId);

            if (indicator != null)
            {
                if (indicator.releasing)
                    Revive(indicator);

                if (indicator.mark.version != mark.version)
                {
                    Write(indicator, mark);

                    indicator.icon.PunchScale(
                        0.18f,
                        0.22f);

                    SpawnImpact(mark);
                }

                continue;
            }

            indicator = FreeIndicator();

            if (indicator == null)
                break;

            Claim(indicator, mark);
            SpawnImpact(mark);
        }

        // 월드 위치를 따라간다.
        foreach (Indicator indicator in _indicators)
        {
            if (!indicator.busy ||
                indicator.releasing)
            {
                continue;
            }

            bool visible =
                Follow(indicator);

            Widget.Visible(
                indicator.root,
                visible);
        }
    }

    private bool StillLogged(int armorId)
    {
        foreach (DamageLog.ArmorMark mark in DamageLog.Armors)
        {
            if (mark.armorId == armorId)
                return true;
        }

        return false;
    }

    private Indicator FindIndicator(int armorId)
    {
        foreach (Indicator indicator in _indicators)
        {
            if (!indicator.busy)
                continue;

            if (indicator.mark.armorId == armorId)
                return indicator;
        }

        return null;
    }

    private Indicator FreeIndicator()
    {
        foreach (Indicator indicator in _indicators)
        {
            if (!indicator.busy)
                return indicator;
        }

        return null;
    }

    private void Claim(
        Indicator indicator,
        in DamageLog.ArmorMark mark)
    {
        indicator.busy = true;
        indicator.releasing = false;

        Write(indicator, mark);

        Widget.Visible(
            indicator.root,
            true);

        indicator.root.FadeIn(
            0.08f);

        indicator.icon.RenderScale =
            new Vector2(1.25f, 1.25f);

        indicator.icon.ScaleTo(
            Vector2.one,
            0.18f,
            0f,
            TweenHelper.EaseOutBack);
    }

    private void Revive(Indicator indicator)
    {
        GUITween.Kill(indicator.root);

        indicator.releasing = false;

        indicator.root.Opacity = 1f;

        Widget.Visible(
            indicator.root,
            true);
    }

    private void Write(
        Indicator indicator,
        in DamageLog.ArmorMark mark)
    {
        indicator.mark = mark;

        Color color =
            OutcomeColor(mark.outcome);

        indicator.line.SetTexture(
            GUIStyleMaker.Solid(color));

        indicator.head.Style.Text(color);

        Texture2D icon =
            OutcomeIcon(mark.outcome);

        // 아이콘을 아직 안 넣었어도 NRE는 내지 않는다.
        // 대신 해당 outcome 색 네모가 나온다.
        indicator.icon.SetTexture(
            icon != null
                ? icon
                : GUIStyleMaker.Solid(color));
    }

    private void Release(Indicator indicator)
    {
        indicator.releasing = true;

        Indicator captured = indicator;

        indicator.root.FadeOut(
            0.18f,
            0f,
            null,
            () =>
            {
                // Fade 중 같은 장갑이 다시 맞아 Revive됐으면
                // 옛 callback이 새 표시를 죽이면 안 된다.
                if (!captured.releasing)
                    return;

                Widget.Visible(
                    captured.root,
                    false);

                captured.busy = false;
                captured.releasing = false;
            });
    }

    // =========================================================
    // IMPACT POOL
    // =========================================================

    private void SpawnImpact(
        in DamageLog.ArmorMark mark)
    {
        Impact impact =
            FreeImpact();

        if (impact == null)
            return;

        impact.busy = true;
        impact.mark = mark;

        Color color =
            OutcomeColor(mark.outcome);

        impact.dot.SetTexture(
            GUIStyleMaker.Solid(color));

        Widget.Visible(
            impact.root,
            true);

        impact.root.Opacity = 1f;

        impact.dot.RenderScale =
            new Vector2(1.8f, 1.8f);

        impact.dot.ScaleTo(
            Vector2.one,
            0.14f,
            0f,
            TweenHelper.EaseOutBack);

        Impact captured = impact;

        impact.root.FadeOut(
            0.10f,
            0.12f,
            null,
            () =>
            {
                Widget.Visible(
                    captured.root,
                    false);

                captured.busy = false;
            });
    }

    private Impact FreeImpact()
    {
        foreach (Impact impact in _impacts)
        {
            if (!impact.busy)
                return impact;
        }

        return null;
    }

    private void SyncImpacts()
    {
        foreach (Impact impact in _impacts)
        {
            if (!impact.busy)
                continue;

            Vector3 screen =
                view.WorldToScreenPoint(
                    impact.mark.CurrentWorldPoint);

            if (!OnScreen(screen))
            {
                Widget.Visible(
                    impact.root,
                    false);

                continue;
            }

            Widget.Visible(
                impact.root,
                true);

            impact.root.SetPos(
                new Vector2(
                    screen.x - ImpactSize * 0.5f,

                    Screen.height -
                    screen.y -
                    ImpactSize * 0.5f));
        }
    }

    // =========================================================
    // POSITION / LAYOUT
    // =========================================================

    private bool Follow(Indicator indicator)
    {
        Vector3 screen =
            view.WorldToScreenPoint(
                indicator.mark.CurrentWorldPoint);

        if (!OnScreen(screen))
            return false;

        float hitX = screen.x;

        // Unity screen Y -> IMGUI Y
        float hitY =
            Screen.height -
            screen.y;

        float leftSpace =
            hitX -
            screenMargin;

        float rightSpace =
            Screen.width -
            screenMargin -
            hitX;

        float preferredWidth =
            iconSize.x +
            iconGap +
            minArrowLength;

        bool iconOnRight;

        // 기본은 오른쪽.
        // 오른쪽이 부족할 때만 왼쪽으로 뒤집는다.
        if (rightSpace >= preferredWidth)
        {
            iconOnRight = true;
        }
        else if (leftSpace >= preferredWidth)
        {
            iconOnRight = false;
        }
        else
        {
            // 양쪽 다 좁으면 더 넓은 쪽.
            iconOnRight =
                rightSpace >= leftSpace;
        }

        float available =
            iconOnRight
                ? rightSpace
                : leftSpace;

        // 화면 경계가 minArrowLength보다 우선한다.
        float arrowLength =
            Mathf.Min(
                maxArrowLength,
                available -
                iconGap -
                iconSize.x);

        if (arrowLength < 8f)
            return false;

        Layout(
            indicator,
            hitX,
            hitY,
            arrowLength,
            iconOnRight);

        return true;
    }

    private void Layout(
        Indicator indicator,
        float hitX,
        float hitY,
        float arrowLength,
        bool iconOnRight)
    {
        float totalWidth =
            arrowLength +
            iconGap +
            iconSize.x;

        float rootX =
            iconOnRight
                ? hitX
                : hitX - totalWidth;

        // 아이콘 자체는 위/아래 화면 밖으로 나가지 않는다.
        //
        // 화살표의 Y는 hitY 그대로 유지하므로
        // 실제 피격점을 계속 가리킨다.
        float iconTop =
            Mathf.Clamp(
                hitY - iconSize.y * 0.5f,
                0f,
                Mathf.Max(
                    0f,
                    Screen.height - iconSize.y));

        indicator.root.SetRect(
            new Rect(
                rootX,
                iconTop,
                totalWidth,
                iconSize.y));

        float lineY =
            Mathf.Clamp(
                hitY -
                iconTop -
                LineHeight * 0.5f,
                0f,
                iconSize.y - LineHeight);

        float headY =
            Mathf.Clamp(
                hitY -
                iconTop -
                ArrowHeadHeight * 0.5f,
                0f,
                Mathf.Max(
                    0f,
                    iconSize.y - ArrowHeadHeight));

        if (iconOnRight)
        {
            // hit <--------------- [icon]

            indicator.head.Content.text = "<";

            SetLocalRect(
                indicator.head,
                indicator.root,
                new Rect(
                    0f,
                    headY,
                    ArrowHeadWidth,
                    ArrowHeadHeight));

            SetLocalRect(
                indicator.line,
                indicator.root,
                new Rect(
                    ArrowHeadWidth,
                    lineY,
                    Mathf.Max(
                        0f,
                        arrowLength -
                        ArrowHeadWidth),
                    LineHeight));

            SetLocalRect(
                indicator.icon,
                indicator.root,
                new Rect(
                    arrowLength + iconGap,
                    0f,
                    iconSize.x,
                    iconSize.y));
        }
        else
        {
            // [icon] ---------------> hit

            indicator.head.Content.text = ">";

            SetLocalRect(
                indicator.icon,
                indicator.root,
                new Rect(
                    0f,
                    0f,
                    iconSize.x,
                    iconSize.y));

            SetLocalRect(
                indicator.line,
                indicator.root,
                new Rect(
                    iconSize.x + iconGap,
                    lineY,
                    Mathf.Max(
                        0f,
                        arrowLength -
                        ArrowHeadWidth),
                    LineHeight));

            SetLocalRect(
                indicator.head,
                indicator.root,
                new Rect(
                    totalWidth -
                    ArrowHeadWidth,
                    headY,
                    ArrowHeadWidth,
                    ArrowHeadHeight));
        }
    }

    /// <summary>
    /// GUIGroup Mask=false에서는 자식 Rect도 화면 절대좌표다.
    /// 그래서 root 위치 + local 위치로 명시적으로 바꾼다.
    /// </summary>
    private static void SetLocalRect(
        GUIItem item,
        GUIGroup root,
        Rect local)
    {
        item.SetRect(
            new Rect(
                root.Pos + local.position,
                local.size));
    }

    private static bool OnScreen(Vector3 screen)
    {
        return
            screen.z > 0f &&
            screen.x >= 0f &&
            screen.x <= Screen.width &&
            screen.y >= 0f &&
            screen.y <= Screen.height;
    }

    // =========================================================
    // OUTCOME
    // =========================================================

    private Texture2D OutcomeIcon(
        HitOutcome outcome)
    {
        return outcome switch
        {
            HitOutcome.Penetrated =>
                penetratedIcon,

            HitOutcome.Ricochet =>
                ricochetIcon,

            _ =>
                blockedIcon,
        };
    }

    private static Color OutcomeColor(
        HitOutcome outcome)
    {
        return outcome switch
        {
            HitOutcome.Penetrated =>
                PenetratedColor,

            HitOutcome.Ricochet =>
                RicochetColor,

            _ =>
                BlockedColor,
        };
    }
}