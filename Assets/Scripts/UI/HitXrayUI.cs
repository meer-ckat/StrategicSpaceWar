using IMGUI;
using UnityEngine;

/// <summary>
/// 맞은 자리에 그대로 뜨는 피격 x-ray. 장갑판 위에 서브셀 체력 격자를 겹쳐 그리고,
/// 탄이 뚫고 지나간 채널을 입사점부터 순서대로 훑는 섬광으로 보여준다.
/// 체력이 있는 모듈은 깎인 양과 남은 체력을 띄운다.
///
/// 구석의 수치 패널이 아니라 월드에 붙여 그리는 이유는 기획서 11.1의 인과 사슬 때문이다.
/// "어디를" 없이 "무엇이 뚫렸는지"만 읽히면 플레이어는 둘을 잇지 못한다.
///
/// 씬에 GUIManager가 있어야 그려진다. 트윈도 GUIManager가 돌린다.
/// </summary>
public class HitXrayUI : MonoBehaviour
{
    [SerializeField] private Camera view;

    [Header("표시")]
    [SerializeField] private float holdSeconds = 2.5f;

    /// <summary>
    /// 줌과 무관하게 고정 크기다. 진단용 주석이지 판의 투영이 아니고,
    /// 줌아웃했을 때 6x6이 4픽셀로 뭉개지면 아무것도 못 읽는다.
    /// </summary>
    [SerializeField] private float plateSize = 108f;

    [SerializeField] private int layer = 90;

    [Header("풀")]
    [SerializeField] private int plateCount = 4;
    [SerializeField] private int popupCount = 8;

    // ===== 팔레트. GUIStyleMaker.Solid는 색을 키로 캐시하므로 단계를 고정해 둔다 =====
    private static readonly Color Obsidian = new(0f, 0f, 0f, 0.72f);
    private static readonly Color Frame = new(0.94f, 0.71f, 0f, 1f);
    private static readonly Color Breached = new(1f, 1f, 1f, 1f);
    private static readonly Color Healthy = new(0.85f, 0.84f, 0.81f, 0.10f);
    private static readonly Color Spark = new(1f, 0.98f, 0.86f, 1f);

    private const int Steps = 6;

    private static readonly Color VitalGreen = new(0.23f, 0.88f, 0.51f, 1f);
    private static readonly Color Marigold = new(1f, 0.79f, 0.24f, 1f);
    private static readonly Color NeonRed = new(1f, 0.18f, 0.18f, 1f);

    private const float CellGap = 1f;
    private const float CaptionH = 18f;

    private sealed class Plate
    {
        public GUIGroup root;
        public GUIImage[] cell;
        public GUIImage[] spark;
        public GUILabel caption;

        public Armor armor;
        public bool busy;
    }

    private sealed class Popup
    {
        public GUIGroup root;
        public GUILabel amount;
        public GUIImage barBack;
        public GUIImage barFill;
        public GUILabel offline;

        public Transform at;
        public float shown;
        public float born;
        public bool busy;
    }

    private Plate[] _plates;
    private Popup[] _popups;
    private Texture2D[] _ramp;

    private bool _built;

    // GUIStyleMaker는 OnGUI 밖에서 돌기를 거부한다. 여기서도 불러 두면 씬에
    // GUIManager가 없어도 패널이 지어지고 경고도 그대로 나온다.
    private void OnGUI() => GUIStyleMaker.Initialize();

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
            return;

        DamageLog.Prune(holdSeconds);

        SyncPlates();
        SyncPopups();
    }

    private void OnDisable()
    {
        if (!_built)
            return;

        foreach (Plate p in _plates) GUIManager.Unregister(p.root);
        foreach (Popup p in _popups) GUIManager.Unregister(p.root);

        _built = false;
    }

    // =========================================================
    // Build
    // =========================================================

    private void Build()
    {
        if (GUIManager.instance == null)
            Debug.LogWarning("[HitXrayUI] 씬에 GUIManager가 없다. 아무것도 그려지지 않는다.");

        BuildRamp();

        _plates = new Plate[Mathf.Max(1, plateCount)];
        for (int i = 0; i < _plates.Length; i++)
            _plates[i] = BuildPlate();

        _popups = new Popup[Mathf.Max(1, popupCount)];
        for (int i = 0; i < _popups.Length; i++)
            _popups[i] = BuildPopup();
    }

    /// <summary>
    /// 체력을 연속 색으로 만들면 Solid가 색마다 텍스처를 새로 굽는다.
    /// 단계를 고정해 두면 구워지는 텍스처가 Steps + 3장으로 끝난다.
    /// </summary>
    private void BuildRamp()
    {
        _ramp = new Texture2D[Steps];

        for (int i = 0; i < Steps; i++)
        {
            float hp = i / (Steps - 1f);

            Color c = hp >= 0.5f
                ? Color.Lerp(Marigold, VitalGreen, (hp - 0.5f) * 2f)
                : Color.Lerp(NeonRed, Marigold, hp * 2f);

            // 많이 깎일수록 진하게. 성한 칸이 조용해야 손상이 혼자 소리친다
            c.a = 0.35f + 0.55f * (1f - hp);
            _ramp[i] = GUIStyleMaker.Solid(c);
        }
    }

    private Plate BuildPlate()
    {
        var plate = new Plate
        {
            cell = new GUIImage[Armor.SubCount],
            spark = new GUIImage[Armor.SubCount],
        };

        plate.root = Widget.Window(
            "",
            new Rect(0f, 0f, plateSize, plateSize + CaptionH),
            "HitXrayPlate",
            GUIStyleMaker.Box(Frame).NoSpacing().Border(0));

        GUILayoutManager.SetPadding(plate.root, 0f);
        plate.root.Mask = false;

        Widget.Image(plate.root, GUIStyleMaker.Solid(Obsidian), new Rect(1f, 1f, plateSize - 2f, plateSize - 2f));

        float step = plateSize / Armor.SubGrid;
        Texture2D healthy = GUIStyleMaker.Solid(Healthy);

        for (int i = 0; i < Armor.SubCount; i++)
        {
            int col = i % Armor.SubGrid;
            int row = i / Armor.SubGrid;

            var r = new Rect(
                col * step + CellGap,
                row * step + CellGap,
                step - CellGap * 2f,
                step - CellGap * 2f);

            plate.cell[i] = Widget.Image(plate.root, healthy, r);

            // 채널 섬광은 체력 칸 위에 따로 얹는다. 같은 이미지를 돌려쓰면
            // 애니메이션이 끝나는 순간 체력 색을 되돌려야 해서 상태가 두 겹이 된다.
            plate.spark[i] = Widget.Image(plate.root, GUIStyleMaker.Solid(Spark), r);
            Widget.Visible(plate.spark[i], false);
        }

        plate.caption = Widget.Label(
            plate.root,
            "",
            new Rect(0f, plateSize + 2f, plateSize, CaptionH),
            GUIStyleMaker.Label(Frame, 11, TextAnchor.MiddleCenter));

        Widget.Layer(plate.root, layer);
        Widget.Visible(plate.root, false);

        return plate;
    }

    private Popup BuildPopup()
    {
        var popup = new Popup();

        popup.root = Widget.Window("", new Rect(0f, 0f, 84f, 46f), "HitXrayPopup");

        GUILayoutManager.SetPadding(popup.root, 0f);
        popup.root.Mask = false;

        popup.amount = Widget.Label(
            popup.root,
            "",
            new Rect(0f, 0f, 84f, 20f),
            GUIStyleMaker.Label(NeonRed, 15, TextAnchor.MiddleCenter).Font(15, FontStyle.Bold));

        popup.barBack = Widget.Image(
            popup.root, GUIStyleMaker.Solid(Obsidian), new Rect(17f, 21f, 50f, 5f));

        popup.barFill = Widget.Image(
            popup.root, GUIStyleMaker.Solid(VitalGreen), new Rect(17f, 21f, 50f, 5f));

        popup.offline = Widget.Label(
            popup.root,
            "OFFLINE",
            new Rect(0f, 28f, 84f, 14f),
            GUIStyleMaker.Label(NeonRed, 10, TextAnchor.MiddleCenter).Font(10, FontStyle.Bold));

        Widget.Layer(popup.root, layer);
        Widget.Visible(popup.root, false);

        return popup;
    }

    // =========================================================
    // 장갑판
    // =========================================================

    private void SyncPlates()
    {
        // 이미 붙어 있는 판은 그대로 둔다. 매 프레임 다시 배정하면 애니메이션이 계속 처음부터 다시 돈다.
        foreach (Plate plate in _plates)
        {
            if (plate.busy && !StillLogged(plate.armor))
                Release(plate);
        }

        foreach (DamageLog.ArmorMark mark in DamageLog.Armors)
        {
            if (mark.armor == null || Assigned(mark.armor))
                continue;

            Plate free = FreePlate();

            if (free == null)
                break;

            Claim(free, mark.armor);
        }

        foreach (Plate plate in _plates)
        {
            if (!plate.busy)
                continue;

            if (!Follow(plate.root, plate.armor.transform.position, 0f, plateSize, plateSize + CaptionH))
            {
                Release(plate);
                continue;
            }

            RefreshCells(plate);
        }
    }

    private bool StillLogged(Armor armor)
    {
        if (armor == null)
            return false;

        foreach (DamageLog.ArmorMark mark in DamageLog.Armors)
        {
            if (mark.armor == armor)
                return true;
        }

        return false;
    }

    private bool Assigned(Armor armor)
    {
        foreach (Plate plate in _plates)
        {
            if (plate.busy && plate.armor == armor)
                return true;
        }

        return false;
    }

    private Plate FreePlate()
    {
        foreach (Plate plate in _plates)
        {
            if (!plate.busy)
                return plate;
        }

        return null;
    }

    private void Claim(Plate plate, Armor armor)
    {
        plate.armor = armor;
        plate.busy = true;

        Widget.Visible(plate.root, true);

        plate.root.RenderScale = new Vector2(1.3f, 1.3f);
        plate.root.FadeIn(0.12f);
        plate.root.ScaleTo(Vector2.one, 0.3f, 0f, TweenHelper.EaseOutBack);

        WriteCaption(plate);
        PlayChannel(plate);
    }

    private void Release(Plate plate)
    {
        Plate captured = plate;

        plate.busy = false;
        plate.armor = null;

        plate.root.FadeOut(0.3f, 0f, null, () => Widget.Visible(captured.root, false));
    }

    /// <summary>
    /// 탄이 실제로 그은 선을 입사점부터 순서대로 훑는다. 방향 대신 입사 서브셀에서의
    /// 거리로 순서를 잡으므로, 도탄이든 관통이든 항상 들어온 자리에서 시작한다.
    /// </summary>
    private void PlayChannel(Plate plate)
    {
        for (int i = 0; i < Armor.SubCount; i++)
            Widget.Visible(plate.spark[i], false);

        // 채널은 가장 최근 한 발 것뿐이다. 다른 판이면 그 판의 선이 아니다.
        if (PenetrationManager.LastArmor != plate.armor)
            return;

        int entry = Mathf.Clamp(PenetrationManager.LastSubIndex, 0, Armor.SubCount - 1);
        var entryAt = new Vector2(entry % Armor.SubGrid, entry / Armor.SubGrid);

        for (int sub = 0; sub < Armor.SubCount; sub++)
        {
            if (PenetrationManager.LastChannel[sub] <= 0f)
                continue;

            var at = new Vector2(sub % Armor.SubGrid, sub / Armor.SubGrid);
            float order = Vector2.Distance(entryAt, at);

            GUIImage spark = plate.spark[ToDisplay(sub)];

            Widget.Visible(spark, true);
            spark.Opacity = 0f;

            // 들어와서 밝아지고, 지나간 자리는 뒤에서 꺼진다
            spark.FadeTo(1f, 0.05f, order * 0.025f, TweenHelper.EaseOutQuad,
                () => spark.FadeTo(0f, 0.22f, 0f, TweenHelper.EaseInQuad));
        }

        if (PenetrationManager.LogCount > 0 &&
            PenetrationManager.GetLog(0).outcome == HitOutcome.Penetrated)
        {
            plate.root.PunchScale(0.18f, 0.3f);
        }
    }

    private void WriteCaption(Plate plate)
    {
        if (PenetrationManager.LogCount == 0 || PenetrationManager.LastArmor != plate.armor)
        {
            plate.caption.Content.text = "SPALL";
            plate.caption.Style.Text(Marigold);
            return;
        }

        HitResult r = PenetrationManager.GetLog(0);

        Color tint = r.outcome switch
        {
            HitOutcome.Penetrated => VitalGreen,
            HitOutcome.Ricochet => Marigold,
            _ => NeonRed,
        };

        plate.caption.Content.text = r.effectiveRHA <= 0f
            ? $"{r.outcome.ToString().ToUpperInvariant()}  ·  BREACHED"
            : $"{r.outcome.ToString().ToUpperInvariant()}  ·  {r.angleDeg:F0}°  ·  {r.effectiveRHA:F0}mm";

        plate.caption.Style.Text(tint);
    }

    private void RefreshCells(Plate plate)
    {
        for (int sub = 0; sub < Armor.SubCount; sub++)
        {
            float hp = plate.armor.HpFraction(sub);
            GUIImage cell = plate.cell[ToDisplay(sub)];

            if (hp <= 0f)
                cell.SetTexture(GUIStyleMaker.Solid(Breached));
            else if (hp >= 0.995f)
                cell.SetTexture(GUIStyleMaker.Solid(Healthy));
            else
                cell.SetTexture(_ramp[Mathf.Clamp(Mathf.RoundToInt(hp * (Steps - 1)), 0, Steps - 1)]);
        }
    }

    /// <summary>서브셀 0번은 월드 기준 아래줄, 화면 0번 줄은 위. 뒤집어야 실제 자리에 찍힌다.</summary>
    private static int ToDisplay(int sub)
    {
        int col = sub % Armor.SubGrid;
        int row = sub / Armor.SubGrid;

        return (Armor.SubGrid - 1 - row) * Armor.SubGrid + col;
    }

    // =========================================================
    // 모듈
    // =========================================================

    private void SyncPopups()
    {
        foreach (Popup popup in _popups)
        {
            if (popup.busy && !StillLogged(popup.at))
                Release(popup);
        }

        foreach (DamageLog.ModuleMark mark in DamageLog.Modules)
        {
            if (mark.at == null)
                continue;

            Popup existing = Find(mark.at);

            // 같은 모듈을 또 맞으면 새 팝업이 아니라 숫자를 갱신하고 한 번 튕긴다
            if (existing != null)
            {
                if (existing.shown >= mark.amount)
                    continue;

                Write(existing, mark);
                existing.root.PunchScale(0.2f, 0.22f);
                continue;
            }

            Popup free = FreePopup();

            if (free == null)
                break;

            Claim(free, mark);
        }

        foreach (Popup popup in _popups)
        {
            if (!popup.busy)
                continue;

            // 나이만큼 떠오르게 해서 같은 자리에 연달아 맞아도 겹쳐 읽히지 않는다
            float rise = 28f + (Time.time - popup.born) * 26f;

            if (!Follow(popup.root, popup.at.position, rise, 84f, 46f))
                Release(popup);
        }
    }

    private Popup Find(Transform at)
    {
        foreach (Popup popup in _popups)
        {
            if (popup.busy && popup.at == at)
                return popup;
        }

        return null;
    }

    private Popup FreePopup()
    {
        foreach (Popup popup in _popups)
        {
            if (!popup.busy)
                return popup;
        }

        return null;
    }

    private bool StillLogged(Transform at)
    {
        if (at == null)
            return false;

        foreach (DamageLog.ModuleMark mark in DamageLog.Modules)
        {
            if (mark.at == at)
                return true;
        }

        return false;
    }

    private void Claim(Popup popup, in DamageLog.ModuleMark mark)
    {
        popup.at = mark.at;
        popup.busy = true;
        popup.born = Time.time;

        Widget.Visible(popup.root, true);

        popup.root.RenderScale = new Vector2(1.4f, 1.4f);
        popup.root.FadeIn(0.1f);
        popup.root.ScaleTo(Vector2.one, 0.28f, 0f, TweenHelper.EaseOutBack);

        Write(popup, mark);
    }

    private void Release(Popup popup)
    {
        Popup captured = popup;

        popup.busy = false;
        popup.at = null;
        popup.shown = 0f;

        popup.root.FadeOut(0.3f, 0f, null, () => Widget.Visible(captured.root, false));
    }

    private void Write(Popup popup, in DamageLog.ModuleMark mark)
    {
        popup.shown = mark.amount;

        popup.amount.Content.text = $"-{mark.amount:F0}";

        Color left = mark.health01 <= 0f ? NeonRed : mark.health01 < 0.5f ? Marigold : VitalGreen;

        popup.barFill.SetTexture(GUIStyleMaker.Solid(left));
        popup.barFill.SetRect(new Rect(
            popup.barBack.Rect.x,
            popup.barBack.Rect.y,
            50f * Mathf.Clamp01(mark.health01),
            5f));

        Widget.Visible(popup.offline, mark.neutralized);
    }

    // =========================================================
    // 월드 앵커
    // =========================================================

    /// <summary>월드 점을 화면 좌표로 옮겨 그룹을 그 위에 놓는다. 카메라 뒤면 false.</summary>
    private bool Follow(GUIGroup root, Vector3 world, float rise, float w, float h)
    {
        Vector3 screen = view.WorldToScreenPoint(world);

        if (screen.z < 0f)
            return false;

        root.SetPos(new Vector2(
            screen.x - w * 0.5f,
            Screen.height - screen.y - h * 0.5f - rise));

        return true;
    }
}
