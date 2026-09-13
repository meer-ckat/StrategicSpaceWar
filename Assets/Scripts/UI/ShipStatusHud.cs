using System.Collections.Generic;
using IMGUI;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Player ship combat HUD.
///
/// AIRFRAME : 현재 함체 손실
/// FLIGHT   : 속도 / 진행방향 / ΔV / 기압
/// WPN      : 탄종 / 포문 수 / 탄약
///
/// 월드에는 velocity와 turret aim만 표시한다.
/// </summary>
public sealed class ShipStatusHud : MonoBehaviour
{
    // Palette 이름으로만 고른다. 본문 = Hull, 보조 = 투명한 Hull, 경고 = Radiance, 손상 = Breach,
    // 조준·리드 = Telemetry. 값을 바꿀 자리는 Palette 하나다.
    private static readonly Color HudColor = Palette.Hull;

    private static readonly Color DimColor = Palette.Hull.WithAlpha(0.45f);

    private static readonly Color WarnColor = Palette.Radiance;

    private static readonly Color CriticalColor = Palette.Breach;

    /// <summary>AIRFRAME 격자에서 탄약고·원자로 칸. 열·폭발의 색.</summary>
    private static readonly Color CitadelColor = Palette.Heat;

    private static readonly HashSet<Vector2Int> _citadel = new();

    private static readonly Color PanelBg = Palette.DeepSpace.WithAlpha(0.72f);

    private static readonly Color GunAimColor = Palette.Telemetry.WithAlpha(0.15f);

    private static readonly Color LeadColor = Palette.Telemetry.WithAlpha(0.60f);

    /// <summary>
    /// 미사일 잠금. 계기의 파랑에서 유일하게 벗어나는 색이다 - 리드 마커와 같은 십자
    /// 모양이라 색으로 갈리지 않으면 둘이 섞인다.
    /// </summary>
    private static readonly Color LockColor = Palette.Heat.WithAlpha(0.85f);

    private const float Margin = 16f;

    /// <summary>
    /// 패널이 차지하는 위·아래 띠의 높이(여백 포함). 가장자리 브래킷(<see cref="ContactView"/>)이
    /// 이 안으로 들어오지 않게 하는 값 - 신호 꺾쇠가 WPN·AIRFRAME 글자 밑에 깔리던 자리.
    /// 아래 띠는 WPN 높이가 포 수를 따라 변해서 그릴 때 적는다.
    /// </summary>
    public static float TopBand => Mathf.Max(AirframeHeight, FlightHeight) + Margin;
    public static float BottomBand { get; private set; } = AirframeHeight + Margin;

    private const float AirframeWidth = 300f;
    private const float AirframeHeight = 150f;

    private const float FlightWidth = 240f;
    private const float FlightHeight = 164f;   // EMIT 행(2026-09-12) + SYS 행(2026-09-13)

    private const float WeaponWidth = 340f;

    /// <summary>WPN 행의 정지 사유 칸. "LINE BLOCKED"가 제일 긴 문자열이다.</summary>
    private const float HoldWidth = 100f;

    /// <summary>WPN 행의 문 수 칸.</summary>
    private const float CountWidth = 45f;

    private const float HeaderHeight = 26f;
    private const float RowHeight = 21f;
    private const float Padding = 10f;

    // "SUPERDUPER ENGINE OUT"이 제일 긴 줄이다 - 모듈 이름이 defName 그대로 오므로
    // 판정 문구(SHELL SHATTERED)보다 길어질 수 있다.
    private const float GutterWidth = 250f;
    private const float GutterRowHeight = 20f;
    private const float GutterTagWidth = 38f;

    private const float VelocityPixelsPerSpeed = 2f;
    private const float MaxVelocityLineLength = 180f;
    private const float VelocityLineWidth = 2f;

    private const float GunAimLineLength = 120f;
    private const float GunAimLineWidth = 1f;

    private const float LeadMarkerSize = 6f;

    /// <summary>잠금 십자. 팔이 리드 마커보다 길고 가운데가 비어서 표적을 안 가린다.</summary>
    private const float LockMarkerArm = 16f;
    private const float LockMarkerGap = 5f;

    /// <summary>화면 밖 표지를 접을 때 가장자리에서 띄우는 여백(실제 픽셀).</summary>
    private const float MarkerEdgeInset = 8f;

    private const float MinVisibleSpeed = 0.05f;

    private readonly List<WeaponHudEntry> _weapons = new();

    private static GUIStyle _titleStyle;
    private static GUIStyle _titleRightStyle;
    private static GUIStyle _leftStyle;
    private static GUIStyle _rightStyle;
    private static GUIStyle _objectiveStyle;

    private const float ObjectiveWidth = 560f;
    private const float ObjectiveHeight = 22f;


    private struct WeaponHudEntry
    {
        public string projectile;
        public int guns;

        /// <summary>이 줄에서 제일 무거운 정지 사유. <see cref="anyReady"/>면 안 그린다.</summary>
        public Gun.HoldReason hold;

        /// <summary>한 문이라도 쏘고 있으면 이 줄은 멀쩡한 것이다.</summary>
        public bool anyReady;
    }


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindFirstObjectByType<ShipStatusHud>() != null)
            return;

        var go = new GameObject("Ship Status HUD");

        DontDestroyOnLoad(go);

        go.AddComponent<ShipStatusHud>();
    }


    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint)
            return;

        // 계기판은 GUIManager(depth 0)보다 뒤다. OnGUI끼리는 GUI.depth가 낮은 쪽이 위에 그려진다 -
        // 대사가 바닥 중앙으로 오면서(2026-09-12) AIRFRAME 패널과 겹치자 실행 순서에 따라 묻혔다.
        GUI.depth = 1;

        // 함선 선택 화면 위로 계기판이 뚫고 나온다. 이 파일은 GUIManager 밖에서 GUI.*를
        // 직접 부르므로 ImGui Layer로는 못 가린다 - 다른 OnGUI 콜백이라 실행 순서가
        // 곧 그리기 순서다. 화면이 열려 있으면 그리지 않는 것이 유일한 문이다.
        if (ShipSelectScreen.IsOpen || LogisticsScreen.IsOpen || RefitScreen.IsOpen
            || MapScreen.IsOpen || CutSceneManager.ControlsPlayer)
            return;

        EnsureStyles();

        // 격파 X-ray. 계기가 다 꺼지고 화면이 검어진 뒤, 재시작 전까지. 배가 이미 사라졌을 수
        // 있으므로 아래 null 검사보다 먼저다 - DeathXray가 설계도를 따로 들고 있다.
        if (GameManager.PlayerDown
            && GameManager.DownSeconds >= ShutdownSeconds + DieFlashSeconds + XrayAfterBlackSeconds)
        {
            DrawXray();
            return;
        }

        Ship ship = GameManager.Player();

        if (ship == null)
            return;

        Camera cam = Camera.main;

        // 월드에 붙는 조준 정보(속도 벡터·조준선·리드 마커)는 배율 행렬을 걸기 **전에**
        // 그린다 - 배율 밖에 완전히 둔다. 이건 읽기 편하라고 있는 값이 아니라 조준에
        // 쓰는 값이다: 리드 마커는 마우스를 그 십자에 정확히 두면 맞는다는 전제고
        // (DrawLeadMarkers 주석 참고, fireArc 0.7도가 기준), uiScale 슬라이더가 그
        // 자리를 1픽셀이라도 밀면 조준이 어긋난다. 패널(정보 표시)만 배율을 받는다.
        //
        // 전투 불능이 되면 조준 정보부터 즉시 끊긴다 - 패널은 하나씩 소등된다.
        if (cam != null && !GameManager.GuiHidden)
        {
            DrawVelocityVector(ship, cam);
            DrawGunAimVectors(ship, cam);
            DrawLeadMarkers(ship, cam);
            DrawLockMarkers(ship, cam);
        }

        // 이 파일은 GUIManager를 거치지 않고 GUI.*를 직접 부른다 - 그 중앙 OnGUI가
        // 거는 배율 행렬이 여기까지 안 온다(GUI.matrix는 컴포넌트마다 따로 관리해야
        // 한다, GUIManager.OnGUI의 흔들림 주석 참고). 같은 배율을 여기서 한 번 더
        // 걸어야 패널이 같이 커진다.
        float uiScale = GUIManager.UiScale;
        Matrix4x4 savedMatrix = GUI.matrix;
        bool scaling = !Mathf.Approximately(uiScale, 1f);

        if (scaling)
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));

        // 소등 순서: 무장 -> 비행 -> 함체. 선체 그림이 마지막 숨이다.
        if (BeginSection(ship, 0)) DrawObjective();
        if (BeginSection(ship, 0)) DrawHitGutter();
        if (BeginSection(ship, 0)) DrawContactPanel(ship);
        if (BeginSection(ship, 2)) DrawAirframePanel(ship);
        if (BeginSection(ship, 1)) DrawFlightPanel(ship);
        if (BeginSection(ship, 0)) DrawWeaponPanel(ship);

        _sectionDying = false;
        _sectionShake = Vector2.zero;

        if (scaling)
            GUI.matrix = savedMatrix;
    }


    // ------------------------------------------------------------
    // 사망 소등
    // ------------------------------------------------------------

    public const float SectionStagger = 0.6f;       // 섹션 사이 간격(초)
    public const float DieFlashSeconds = 0.3f;      // 빨갛게 흔들리는 시간
    private const float DieShakePixels = 30f;
    private const int SectionCount = 3;

    /// <summary>
    /// 죽고 이만큼은 계기가 멀쩡하다. 조준 정보만 끊긴 채 이명 속에 표류하는 정적 -
    /// "죽었다"를 인지할 호흡이 있어야 그 다음 소등이 연출로 읽힌다. 즉시 꺼지기
    /// 시작하면 전부 한 호흡으로 뭉개진다.
    /// </summary>
    public const float ShutdownDelaySeconds = 2f;

    /// <summary>마지막 섹션까지 다 꺼지는 시각. GameManager가 이 뒤에 암전을 잇는다.</summary>
    public const float ShutdownSeconds =
        ShutdownDelaySeconds + (SectionCount - 1) * SectionStagger + DieFlashSeconds;

    /// <summary>부팅에서 마지막 섹션이 켜지는 시각. GameManager.GuiHidden이 이걸 본다.</summary>
    public const float BootSpanSeconds = (SectionCount - 1) * SectionStagger;

    private static bool _sectionDying;
    private static Vector2 _sectionShake;

    /// <summary>
    /// 이 섹션을 그릴까. 소등 트리거는 승무원 사망이 아니라 **전투 불능**이다
    /// (쏠 수도 움직일 수도 없는 배가 잔해다. 오너 결정 2026-08-29). 승무원이 살아도
    /// 원자로가 나가면 계기는 나간다 - 전기가 없으니 오히려 그림이 맞다.
    /// 시계는 GameManager.DownSeconds 하나다 - 암전·재시작과 같은 원점을 써야
    /// "모든 HUD가 꺼진 뒤"라는 시점이 안 어긋난다. 수리로 되살아나면 계기도 돌아온다.
    /// 틴트와 흔들림은 여기서 정하고 Draw* 헬퍼들이 읽는다 - 그리기 코드는 모른다.
    /// </summary>
    private static bool BeginSection(Ship ship, int order)
    {
        _sectionDying = false;
        _sectionShake = Vector2.zero;

        float down = GameManager.DownSeconds;

        if (down < 0f)
        {
            // 부팅. 소등의 역순(함체 -> 비행 -> 무장)으로 하나씩 켜진다 - 마지막에
            // 꺼진 계기가 제일 먼저 돌아오는 대칭이고, 시동 순서로도 그게 맞다.
            float sinceBoot = GameManager.SceneSeconds - GameManager.GuiBootDelay;

            return sinceBoot >= (SectionCount - 1 - order) * SectionStagger;
        }

        // 정적 구간. 계기는 아직 멀쩡하다 - 죽음을 먼저 읽게 한다.
        down -= ShutdownDelaySeconds;

        if (down < 0f)
            return true;

        float dieAt = order * SectionStagger;

        if (down >= dieAt + DieFlashSeconds)
            return false;

        if (down < dieAt)
            return true;

        _sectionDying = true;

        // 카메라 Shake와 같은 이유로 UnityEngine.Random을 안 쓴다 - 그림도 결정론이다.
        var rng = new DeterministicRng(
            Ballistics.Hash(0, Core.TickManager.currentTick, order));

        _sectionShake = new Vector2(
            rng.Range(-DieShakePixels, DieShakePixels),
            rng.Range(-DieShakePixels, DieShakePixels));

        return true;
    }

    /// <summary>죽어가는 섹션의 색과 자리. 알파는 원본을 지킨다 - 배경 반투명이 유지된다.</summary>
    private static void ApplySection(ref Rect rect, ref Color color)
    {
        if (_sectionDying)
            color = Palette.Breach.WithAlpha(color.a);

        rect.position += _sectionShake;
    }


    // ------------------------------------------------------------
    // DEATH X-RAY
    // ------------------------------------------------------------

    /// <summary>암전이 시작되고 이만큼 뒤에 뜬다. GameManager.BlackFadeSeconds(1)와 같다 - 검정 위에 떠야 읽힌다.</summary>
    private const float XrayAfterBlackSeconds = 1f;
    private const float XrayCellMax = 14f;
    private const float XrayStepSeconds = 0.7f;    // 사건 하나 = 이 시간. 열 개면 7초
    private const float XrayFlashSeconds = 0.25f;  // 사건이 켜지는 순간 흰빛

    /// <summary>
    /// 마지막 열 사건을 **순서대로 재생한다.** 시간으로 칠하면 유폭 한 방이 판 40장을 같은 순간에
    /// 죽여서 전부 빨강이 되고, 그 직전의 관통 한 발이 묻힌다 - 그 한 발이 원인인데.
    /// 재생 중인 사건은 흰빛에서 빨강으로, 지난 사건은 주황, 열 개 밖의 옛 상처는 회색.
    /// 시타델은 언제나 표시 - 탄약고 옆이 먼저 뚫린 그림이 "왜 유폭했나"의 답이다.
    /// 다 돌면 그 자리에 멈춘다. 아무 키가 재시작이다.
    /// </summary>
    private static void DrawXray()
    {
        ShipGrid.Map design = DeathXray.Design;

        if (design == null || design.width <= 0 || design.height <= 0)
            return;

        float w = GUIManager.LogicalWidth, h = GUIManager.LogicalHeight;
        float uiScale = GUIManager.UiScale;
        Matrix4x4 saved = GUI.matrix;

        if (!Mathf.Approximately(uiScale, 1f))
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));

        var gridArea = new Rect(w * 0.06f, h * 0.14f, w * 0.54f, h * 0.68f);
        var textArea = new Rect(w * 0.64f, h * 0.14f, w * 0.30f, h * 0.68f);

        float cell = Mathf.Min(gridArea.width / design.width, gridArea.height / design.height, XrayCellMax);
        float gap = cell >= 4f ? 1f : 0f;
        float x0 = gridArea.x + (gridArea.width - design.width * cell) * 0.5f;
        float y0 = gridArea.y + (gridArea.height - design.height * cell) * 0.5f;

        // 재생 시계. X-ray가 뜬 순간부터.
        float t = GameManager.DownSeconds - (ShutdownSeconds + DieFlashSeconds + XrayAfterBlackSeconds);
        List<DeathXray.Group> groups = DeathXray.Groups;
        int shown = Mathf.Min(groups.Count, Mathf.FloorToInt(t / XrayStepSeconds) + 1);   // 지금까지 켜진 사건 수
        float inStep = t - (shown - 1) * XrayStepSeconds;

        // 칸 → 몇 번째 사건. 열 개 밖의 옛 상처는 -1.
        var when = new Dictionary<Vector2Int, int>();
        foreach (DeathXray.Lost l in DeathXray.LostPlates)
            when[l.cell] = -1;
        for (int g = 0; g < groups.Count; g++)
            foreach (Vector2Int c in groups[g].cells)
                when[c] = g;

        Color current = Color.Lerp(Color.white, Palette.Breach, Mathf.Clamp01(inStep / XrayFlashSeconds));

        for (int col = 0; col < design.width; col++)
        for (int row = 0; row < design.height; row++)
        {
            if (!ShipGrid.Solid(design.cells[col, row]))
                continue;

            var c = new Vector2Int(col, row);
            bool citadel = DeathXray.Citadel.Contains(c);
            Color color;

            if (when.TryGetValue(c, out int g))
            {
                if (g < 0) color = Palette.Steel;                           // 열 개 밖. 옛 상처
                else if (g >= shown) color = Palette.Hull.WithAlpha(0.28f);  // 아직 안 온 사건. 멀쩡한 척
                else if (g == shown - 1) color = current;                   // 지금 이 사건
                else color = Palette.Heat;                                  // 지난 사건

                if (citadel && g >= 0 && g < shown)
                    color = Color.Lerp(color, Palette.Radiance, 0.6f);
            }
            else
                color = citadel ? Palette.Heat.WithAlpha(0.9f) : Palette.Hull.WithAlpha(0.28f);

            GUI.color = color;
            GUI.DrawTexture(new Rect(x0 + col * cell, y0 + row * cell, cell - gap, cell - gap), Texture2D.whiteTexture);
        }

        GUI.color = Color.white;

        // 탄과 파편. 켜진 사건까지의 것만 - 선이 사건과 같이 나타나야 "이 탄이 이 판을"이 읽힌다.
        // 첫 사건보다 1.5초 앞부터 - 다가오는 탄의 마지막 구간이 보여야 어디서 왔는지 안다.
        long cutoff = shown > 0 ? groups[shown - 1].tick + 3 : (groups.Count == 0 ? DeathXray.DownTick : long.MinValue);
        long earliest = groups.Count > 0 ? groups[0].tick - 90 : DeathXray.DownTick - 90;

        Vector2 ToScreen(Vector2 g) => new(x0 + (g.x + 0.5f) * cell, y0 + (g.y + 0.5f) * cell);

        DeathXray.ForEachTrail(tr =>
        {
            if (tr.tick < earliest || tr.tick > cutoff)
                return;

            Color lc; float lw;

            switch (tr.kind)
            {
                case SpallTrails.Kind.Shell: lc = Palette.Hull; lw = 2f; break;
                case SpallTrails.Kind.Module: lc = Palette.Radiance; lw = 1f; break;
                case SpallTrails.Kind.Armor: lc = Palette.Breach.WithAlpha(0.8f); lw = 1f; break;
                default: lc = Palette.Steel.WithAlpha(0.5f); lw = 1f; break;
            }

            DrawLine(ToScreen(tr.a), ToScreen(tr.b), lc, lw);
        });

        foreach (DeathXray.Hit hit in DeathXray.Hits)
        {
            if (hit.tick < earliest || hit.tick > cutoff)
                continue;

            Color hc = hit.outcome switch
            {
                HitOutcome.Penetrated => Palette.Breach,
                HitOutcome.Ricochet => Palette.Radiance,
                _ => Palette.Steel,
            };

            Vector2 p = ToScreen(hit.at);
            float d = Mathf.Max(5f, cell * 0.6f);
            GUI.color = hc;
            GUI.DrawTexture(new Rect(p.x - d * 0.5f, p.y - d * 0.5f, d, d), Texture2D.whiteTexture);
        }

        GUI.color = Color.white;

        // 오른쪽. 원인, 그 아래 사건 열 줄 - 켜진 것까지만 밝다.
        float y = textArea.y;
        GUI.Label(new Rect(textArea.x, y, textArea.width, 20f), "격파", _titleStyle);
        y += 24f;
        GUI.color = Palette.Breach;
        GUI.Label(new Rect(textArea.x, y, textArea.width, 30f), DeathXray.Cause, _objectiveStyle);
        GUI.color = Color.white;
        y += 40f;

        GUI.Label(new Rect(textArea.x, y, textArea.width, RowHeight), $"마지막 {groups.Count}개 사건", _titleStyle);
        y += RowHeight;

        float dt = Core.TickManager.TickDeltaTime;
        long down = DeathXray.DownTick;

        for (int g = 0; g < groups.Count; g++)
        {
            DeathXray.Group group = groups[g];
            bool lit = g < shown;
            bool now = g == shown - 1;
            float ago = (down - group.tick) * dt;
            string tag = string.IsNullOrEmpty(group.tag) ? "피탄" : group.tag;

            GUI.color = !lit ? Palette.Hull.WithAlpha(0.25f) : now ? current : Palette.Hull;
            GUI.Label(new Rect(textArea.x, y, textArea.width * 0.7f, RowHeight), $"{tag}  판 {group.cells.Count}장", _leftStyle);
            GUI.color = lit ? DimColor : Palette.Hull.WithAlpha(0.15f);
            GUI.Label(new Rect(textArea.x + textArea.width * 0.7f, y, textArea.width * 0.3f, RowHeight), $"-{ago:0.0} s", _rightStyle);
            y += RowHeight;
        }

        GUI.color = Color.white;
        y += RowHeight;

        void Legend(Color c, string text)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(textArea.x, y + 5f, 12f, 12f), Texture2D.whiteTexture);
            GUI.color = Palette.Hull;
            GUI.Label(new Rect(textArea.x + 18f, y, textArea.width - 18f, RowHeight), text, _leftStyle);
            y += RowHeight;
        }

        Legend(Palette.Breach, "지금 이 사건");
        Legend(Palette.Heat, "지난 사건");
        Legend(Palette.Steel, "그 전 상처");
        Legend(Palette.Radiance, "시타델 (탄약고·원자로)");
        y += 6f;
        Legend(Palette.Hull, "탄  (굵은 선)");
        Legend(Palette.Breach.WithAlpha(0.8f), "파편 → 판");
        Legend(Palette.Radiance, "파편 → 모듈");
        Legend(Palette.Breach, "관통 · 도탄 노랑 · 저지 회색");

        GUI.color = DimColor;
        GUI.Label(new Rect(textArea.x, textArea.yMax - RowHeight, textArea.width, RowHeight), "아무 키  -  다시", _leftStyle);
        GUI.color = Color.white;

        GUI.matrix = saved;
    }


    // ------------------------------------------------------------
    // OBJECTIVE
    // ------------------------------------------------------------

    /// <summary>
    /// 지금 뭘 해야 하는가, 한 줄, 화면 위 가운데. 문장은 Campaign이 만든다 - 여기는 자리만.
    /// 튜토리얼보다 이게 먼저다: 목표 한 줄이 없는 게임에 튜토리얼을 붙이면 튜토리얼이 목표 노릇을 한다.
    /// </summary>
    private static void DrawObjective()
    {
        string line = Campaign.current != null ? Campaign.current.ObjectiveLine() : "";

        if (string.IsNullOrEmpty(line))
            return;

        var rect = new Rect(
            (GUIManager.LogicalWidth - ObjectiveWidth) * 0.5f,
            Margin,
            ObjectiveWidth,
            ObjectiveHeight);

        DrawRect(rect, PanelBg);
        DrawText(rect, line, Palette.Radiance, _objectiveStyle);
    }


    // ------------------------------------------------------------
    // HIT GUTTER
    // ------------------------------------------------------------

    /// <summary>
    /// 관통 판정 몇 줄. **화면 구석에 쌓고 위로 밀어 올린다** - 중앙 토스트로 띄우면
    /// 교전 한복판에서 시야를 가리고, 워썬더 해군 유저의 불만이 정확히 그것이었다.
    /// AIRFRAME 바로 위에 붙는 것은 그 패널이 "내 배가 어떻게 되고 있나"이고 이 줄들이
    /// 그 이유라서다.
    ///
    /// 색은 셋뿐이다: 세운 것은 흐리게, 내가 뚫은 것은 밝게, 내가 뚫린 것은 빨갛게.
    /// 방향은 태그(OUT/IN)가 말하므로 색까지 방향을 또 말할 필요가 없다.
    /// </summary>
    private static void DrawHitGutter()
    {
        float now = Time.time;

        // AIRFRAME 패널의 윗변. 여기서부터 위로 쌓는다.
        float top = GUIManager.LogicalHeight - AirframeHeight - Margin;

        for (int i = 0; i < HitReadout.Count; i++)
        {
            HitReadout.Line line = HitReadout.Get(i);

            float age = now - line.time;

            // 목록이 시간순이라 하나가 늙었으면 그 뒤는 전부 늙었다.
            if (age >= HitReadout.HoldSeconds)
                break;

            float alpha = Mathf.Clamp01(
                (HitReadout.HoldSeconds - age) / HitReadout.FadeSeconds);

            Color color =
                line.minor ? DimColor :
                line.incoming ? CriticalColor : HudColor;

            color.a *= alpha;

            Color tagColor = DimColor;
            tagColor.a *= alpha;

            Color bg = PanelBg;
            bg.a *= alpha;

            Rect row = new(
                Margin,
                top - Padding - GutterRowHeight * (i + 1),
                GutterWidth,
                GutterRowHeight
            );

            DrawRect(row, bg);

            DrawText(
                new Rect(row.x + Padding, row.y, GutterTagWidth, row.height),
                line.incoming ? "IN" : "OUT",
                tagColor,
                _leftStyle
            );

            DrawText(
                new Rect(
                    row.x + Padding + GutterTagWidth,
                    row.y,
                    row.width - Padding * 2f - GutterTagWidth,
                    row.height
                ),
                // 대문자는 여기서 만든다 - 모듈 이름은 defName이라 "Fuel Tank"처럼
                // 섞여 있고, 판정 문구는 이미 대문자다. 명중마다 도는 자리가 아니라
                // 보이는 다섯 줄만이라 문자열 값이 여기서는 싸다.
                (line.count > 1 ? $"{line.text} x{line.count}" : line.text)
                    .ToUpperInvariant(),
                color,
                _leftStyle
            );
        }
    }


    // ------------------------------------------------------------
    // AIRFRAME
    // ------------------------------------------------------------

    private static void DrawAirframePanel(Ship ship)
        => DrawGridPanel(
            ship,
            new Rect(
                Margin,
                GUIManager.LogicalHeight - AirframeHeight - Margin,
                AirframeWidth,
                AirframeHeight
            ),
            "AIRFRAME",
            showHits: true);


    /// <summary>
    /// 적함 미니뷰. **줌아웃이 아니라 두 번째 프레임이다** - 화면 폭이 36~64 m인데
    /// FightDistance가 120~420 m라 적이 화면에 들어오는 경우가 구조적으로 없다. 넓히면
    /// 서브셀이 2px이 돼서 이 게임의 핵심이 안 보인다(Cosmoteer가 같은 문제를 PiP로 풀었다).
    ///
    /// 표적은 <c>NearestHostile</c> 하나다 - 포탑이 겨누는 것도 FLIGHT의 접근속도가
    /// 재는 것도 같은 배라, 세 자리가 다른 적을 말하면 화면이 거짓말을 한다.
    ///
    /// 그림은 AIRFRAME과 **같은 함수**다. 적함 손상을 따로 그리기 시작하면 내 배와
    /// 적 배의 "부서졌다"가 두 벌이 되고, 언젠가 한쪽만 고친다.
    /// </summary>
    private static void DrawContactPanel(Ship ship)
    {
        Ship target = ship.NearestHostile();

        if (target == null)
            return;

        string name = string.IsNullOrEmpty(target.shipDefName)
            ? target.name
            : target.shipDefName;

        DrawGridPanel(
            target,
            new Rect(Margin, Margin, AirframeWidth, AirframeHeight),
            $"CONTACT  {name.ToUpperInvariant()}",
            showHits: false);
    }


    /// <summary>
    /// 설계도 격자를 그리고 죽은 칸을 빨갛게 칠한다. 내 배(AIRFRAME)와 적함(CONTACT)이
    /// 같은 그림을 쓴다.
    ///
    /// **칸마다 DrawRect다.** 배 한 척이 수백 칸이라 두 척이면 그만큼 두 배인데,
    /// 텍스처로 굽는 길(ShipSelectScreen.BuildSchematic)은 판이 죽을 때마다 다시 구워야
    /// 해서 여기 오면 오히려 비싸다. 프레임이 모자라면 그때 손상 마스크만 굽는다.
    /// </summary>
    private static void DrawGridPanel(Ship ship, Rect panel, string title, bool showHits = false)
    {
        ShipGrid.Map design = ship.DesignMap;
        ShipGrid.Map current = ship.Map;

        if (design == null || current == null)
            return;

        int currentWidth = current.cells.GetLength(0);
        int currentHeight = current.cells.GetLength(1);

        // Stamp는 살아 있는 판의 bounding으로 origin을 다시 잡는다 - 가장자리 판이
        // 죽으면 current 격자가 통째로 밀린다. 칸 번호가 아니라 배 로컬 위치가 같은
        // 자리다: design의 (0,0)이 current의 어느 칸인지가 곧 그 차이다.
        // (ToLocal/ToCell이 각자의 origin을 이미 반영한다.)
        Vector2Int shift = current.ToCell(design.ToLocal(0, 0));

        // **세는 술어와 그리는 술어가 하나여야 한다.** 헤더 숫자와 모자이크가 갈라지면
        // 어느 쪽을 믿어야 하는지가 사라지고, 그게 계기가 죽는 방식이다.
        // HullStructure.AliveCount를 빌려 쓰지 않는 이유도 이것이다 - 저쪽은 설계도가
        // 아니라 실물을 세므로 이 그림과 분모가 다르다.
        bool Alive(int col, int row)
        {
            int c = col + shift.x;
            int r = row + shift.y;

            return c >= 0 && r >= 0 && c < currentWidth && r < currentHeight
                && ShipGrid.Solid(current.cells[c, r]);
        }

        // 시타델. 탄약고·원자로가 어느 칸인지 - 같은 blastDamage가 선수면 44칸, 중앙이면 두 동강이라
        // 이걸 모르면 제일 극적인 사건이 운으로 읽힌다. 내 배만. 적함 것은 센서가 열 정보다.
        // 좌표는 살아 있는 격자(current)로 찍고 shift로 설계 칸으로 되돌린다 - Alive와 같은 길.
        _citadel.Clear();

        if (ship.IsPlayerControlled)
        {
            foreach (CriticalModule m in ship.shipCriticals)
            {
                if (m == null || !Ship.StillAboard(m, ship))
                    continue;

                Vector2Int c = current.ToCell(ship.transform.InverseTransformPoint(m.transform.position));
                _citadel.Add(new Vector2Int(c.x - shift.x, c.y - shift.y));
            }
        }

        int total = 0;
        int alive = 0;

        for (int col = 0; col < design.width; col++)
        {
            for (int row = 0; row < design.height; row++)
            {
                if (!ShipGrid.Solid(design.cells[col, row]))
                    continue;

                total++;

                if (Alive(col, row))
                    alive++;
            }
        }

        float intact = total > 0 ? (float)alive / total : 1f;

        DrawPanel(
            panel,
            title,
            $"{alive}/{total}",
            intact >= 0.66f ? HudColor : intact >= 0.33f ? WarnColor : CriticalColor);

        Rect area = new(
            panel.x + Padding,
            panel.y + HeaderHeight,
            panel.width - Padding * 2f,
            panel.height - HeaderHeight - Padding
        );

        if (design.width <= 0 || design.height <= 0)
            return;

        float cellSize = Mathf.Min(
            area.width / design.width,
            area.height / design.height
        );

        float mapWidth = design.width * cellSize;
        float mapHeight = design.height * cellSize;

        float startX =
            area.x + (area.width - mapWidth) * 0.5f;

        float startY =
            area.y + (area.height - mapHeight) * 0.5f;

        float gap =
            cellSize >= 3f ? 1f : 0f;

        float drawSize =
            Mathf.Max(0.5f, cellSize - gap);

        for (int col = 0; col < design.width; col++)
        {
            for (int row = 0; row < design.height; row++)
            {
                if (!ShipGrid.Solid(design.cells[col, row]))
                    continue;

                DrawRect(
                    new Rect(
                        startX + col * cellSize,
                        startY + row * cellSize,
                        drawSize,
                        drawSize
                    ),
                    !Alive(col, row) ? CriticalColor
                    : _citadel.Contains(new Vector2Int(col, row)) ? CitadelColor
                    : HudColor
                );
            }
        }

        if (!showHits)
            return;

        // **칸 루프가 아니라 마크를 돈다.** 칸마다 마크를 조회하면 이사리비(95x55 = 5,225칸)
        // 에서 프레임당 12만 번 비교가 된다. 마크는 최대 24개라 이쪽이 세 자릿수 싸다.
        // 덧그리는 사각형이 최대 24개 느는데, 위 루프가 이미 수천 개를 그리므로 묻힌다.
        float now = Time.time;

        foreach (DamageLog.ArmorMark mark in DamageLog.Armors)
        {
            if (mark.ship != ship)
                continue;

            float alpha = HitEnvelope(now - mark.time, HitLife(mark));

            if (alpha <= 0f)
                continue;

            int col = mark.cell.x;
            int row = mark.cell.y;

            if (col < 0 || row < 0 || col >= design.width || row >= design.height)
                continue;

            // 아래 칸 색과 섞는다 - 온셋(alpha 1)만 완전히 덮고 잔광에서는 선체 상태가
            // 비쳐 보인다. 덮어쓰기만 하면 "살았나 죽었나"를 그 시간 동안 못 읽는다.
            Color under = Alive(col, row) ? HudColor : CriticalColor;

            DrawRect(
                new Rect(
                    startX + col * cellSize,
                    startY + row * cellSize,
                    drawSize,
                    drawSize
                ),
                Color.Lerp(under, HitColor(mark), alpha)
            );
        }
    }

    // ------------------------------------------------------------
    // 명중 하이라이트
    // ------------------------------------------------------------

    /// <summary>
    /// 관통이 빨강이 아닌 이유: 죽은 칸이 이미 CriticalColor(빨강)라 겹치면 안 보인다.
    /// 관통은 판을 죽이는 일이 잦아서 그 충돌이 상시다. 백열은 산 칸(연청)·죽은 칸(빨강)
    /// 어느 배경에서도 튀고, 적열(Heat)의 은유와도 맞는다.
    ///
    /// 도탄은 HUD에 이미 있는 WarnColor를 그대로 쓴다 - 색 어휘를 안 늘린다.
    /// </summary>
    private static Color HitColor(in DamageLog.ArmorMark mark)
    {
        // 충각은 판정 셋과 다른 사건이다 - 뚫린 것도 튕긴 것도 아니고 갈리는 것이라
        // 주황으로 따로 둔다. 접촉하는 동안 계속 켜져 있으므로 채도를 낮게 잡는다.
        if (mark.ram)
            return Palette.Heat;

        return mark.outcome switch
        {
            HitOutcome.Penetrated => Palette.Hull,
            HitOutcome.Ricochet => WarnColor,
            _ => Palette.Telemetry,
        };
    }

    /// <summary>
    /// 판정마다 다른 수명. **색이 아닌 둘째 채널이다** - 이사리비는 셀이 2.1px이라
    /// 테두리도 아이콘도 못 그린다. 색을 못 읽어도 "오래 남았다 = 나쁜 일"이 남는다.
    /// </summary>
    private static float HitLife(in DamageLog.ArmorMark mark)
    {
        // 충각은 짧다. 접촉 중에는 매 틱 갱신돼서 계속 켜져 있고, 떨어지는 순간
        // 꺼져야 "지금 긁히는 중"이 정확히 읽힌다 - 길게 잡으면 이미 떨어진 뒤에도
        // 남아서 접촉이 끝난 줄 모른다.
        if (mark.ram)
            return 0.25f;

        return mark.outcome switch
        {
            HitOutcome.Penetrated => 1.2f,
            HitOutcome.Ricochet => 0.3f,
            _ => 0.6f,
        };
    }

    /// <summary>
    /// 강하게 한 번 나타나고 조용히 남는다. **깜빡이지 않는다** - 6.7Hz 점멸은 주의를
    /// 납치해서 본 작업(조준·기동)을 방해하고, 빨강 고대비 다중 셀 동시 점멸은 광과민성
    /// 조건에 가까워질 이유가 없다. 시선을 끈 뒤에는 salience를 유지할 필요가 없다.
    /// </summary>
    private static float HitEnvelope(float age, float life)
    {
        if (age < 0f || age >= life)
            return 0f;

        if (age < 0.10f)
            return age / 0.10f;          // 온셋

        if (age < 0.25f)
            return 1f;                   // 읽히는 구간

        float fadeStart = Mathf.Max(0.25f, life - 0.35f);

        if (age < fadeStart)
            return 0.55f;                // 잔광

        return Mathf.Lerp(0.55f, 0f, (age - fadeStart) / Mathf.Max(0.01f, life - fadeStart));
    }


    // ------------------------------------------------------------
    // FLIGHT
    // ------------------------------------------------------------

    private static void DrawFlightPanel(Ship ship)
    {
        Rect panel = new(
            GUIManager.LogicalWidth - FlightWidth - Margin,
            Margin,
            FlightWidth,
            FlightHeight
        );

        DrawPanel(panel, "FLIGHT");

        Vector2 velocity = ship.velocity;

        float speed = velocity.magnitude;

        // 진행 방향은 월드의 속도 벡터가 이미 그려 준다 - 나침반 숫자는 그 중복이었다.
        // 눈이 세계에서 못 읽는 값은 **거리**다: 교전거리 120~240 m가 화면 폭 36~64 m 밖이라
        // 적이 얼마나 먼지 볼 길이 없었다. 접근속도(CLS)는 뺐다 - 리드 마커가 이미 그 답이다.
        Ship target = ship.NearestHostile();

        string range = "—";
        Color rangeColor = DimColor;

        if (target != null)
        {
            float d = Vector2.Distance(target.transform.position, ship.transform.position);
            range = d >= 1000f ? $"{d / 1000f:0.0} km" : $"{d:0} m";
            rangeColor = HudColor;
        }

        FuelStatus(
            ship,
            out string deltaV,
            out Color fuelColor
        );

        // 값은 배 전체 평균, 색은 최악의 방. 평균은 방 하나가 진공이어도 90%라고
        // 웃는다 - 숫자는 전체 상태를, 색은 제일 급한 곳을 말해야 한다.
        PressureStatus(
            ship,
            out float pressure,
            out Color pressureColor
        );

        float y =
            panel.y + HeaderHeight;

        DrawValue(
            panel,
            ref y,
            "SPD",
            $"{speed:0} m/s",
            HudColor
        );

        DrawValue(
            panel,
            ref y,
            "RNG",
            range,
            rangeColor
        );

        DrawValue(
            panel,
            ref y,
            "ΔV",
            deltaV,
            fuelColor
        );

        DrawValue(
            panel,
            ref y,
            "PRESS",
            $"{pressure * 100f:0}%",
            pressureColor
        );

        // 자기 열원을 못 보면 방출량은 없는 규칙이다. 시끄러우면 경고색.
        float emission = ship.Emission;

        DrawValue(
            panel,
            ref y,
            "EMIT",
            $"×{emission:0.0}",
            emission >= 1.4f ? WarnColor : HudColor
        );

        // 전기와 사람. 원자로가 나가면 조타·조준이 죽는데 증상이 "배가 말을 안 듣는다"뿐이었다 -
        // 어느 쪽이 나갔는지 두 단어로. 잃은 쪽만 붉다.
        {
            float width = panel.width - Padding * 2f;
            float x = panel.x + Padding;

            DrawText(new Rect(x, y, width * 0.5f, RowHeight), "SYS", DimColor, _leftStyle);
            DrawText(new Rect(x + width * 0.5f, y, width * 0.25f, RowHeight), "PWR",
                ship.HasPower ? HudColor : CriticalColor, _rightStyle);
            DrawText(new Rect(x + width * 0.75f, y, width * 0.25f, RowHeight), "CREW",
                ship.CrewAlive ? HudColor : CriticalColor, _rightStyle);
            y += RowHeight;
        }
    }


    private static void FuelStatus(
        Ship ship,
        out string deltaV,
        out Color color
    )
    {
        if (ship.shipTanks.Count == 0)
        {
            deltaV = "INF";
            color = HudColor;
            return;
        }

        float maxImpulse = 0f;

        for (int i = 0; i < ship.shipTanks.Count; i++)
            maxImpulse += ship.shipTanks[i].impulse;

        float remaining =
            ship.RemainingImpulse();

        float fraction =
            maxImpulse > 0f
                ? remaining / maxImpulse
                : 0f;

        deltaV =
            $"{ship.AvailableDeltaV():0} m/s";

        color =
            StatusColor(fraction);
    }


    private static void PressureStatus(
        Ship ship,
        out float average,
        out Color color
    )
    {
        float air = 0f;
        float volume = 0f;
        float worst = 1f;

        for (int i = 0; i < ship.rooms.Count; i++)
        {
            Room room = ship.rooms[i];

            air += room.air;
            volume += room.Volume;

            if (room.Volume > 0f)
                worst = Mathf.Min(worst, room.Pressure);
        }

        average =
            volume > 0f
                ? air / volume
                : 1f;

        color =
            StatusColor(worst);
    }


    // ------------------------------------------------------------
    // WPN
    // ------------------------------------------------------------

    private void DrawWeaponPanel(Ship ship)
    {
        BuildWeaponEntries(ship);

        int rows =
            Mathf.Max(1, _weapons.Count);

        float height =
            HeaderHeight +
            rows * RowHeight +
            Padding;

        Rect panel = new(
            GUIManager.LogicalWidth - WeaponWidth - Margin,
            GUIManager.LogicalHeight - height - Margin,
            WeaponWidth,
            height
        );

        BottomBand = Mathf.Max(AirframeHeight, height) + Margin;

        int guns = 0;

        for (int i = 0; i < _weapons.Count; i++)
            guns += _weapons[i].guns;

        // 머리 오른쪽이 탄약이다. 대사가 "25%"를 한 번 말해도 숫자는 상시 있어야 한다.
        // 탄약고 없는 설계(dart)는 포 수만.
        int rounds = ship.Rounds, maxRounds = ship.MaxRounds;
        string head = maxRounds > 0 ? $"{guns} · {rounds}/{maxRounds}" : (guns > 0 ? $"{guns}" : null);
        Color headColor = guns <= 0 || (maxRounds > 0 && rounds <= 0) ? CriticalColor
            : maxRounds > 0 && rounds <= maxRounds * 0.25f ? WarnColor
            : HudColor;

        DrawPanel(panel, "WPN", head, headColor);

        float y =
            panel.y + HeaderHeight;

        if (_weapons.Count == 0)
        {
            DrawText(
                new Rect(
                    panel.x + Padding,
                    y,
                    panel.width - Padding * 2f,
                    RowHeight
                ),
                "NO WEAPON",
                DimColor,
                _leftStyle
            );

            return;
        }

        for (int i = 0; i < _weapons.Count; i++)
        {
            WeaponHudEntry entry =
                _weapons[i];

            float x =
                panel.x + Padding;

            float width =
                panel.width - Padding * 2f;

            DrawText(
                new Rect(
                    x,
                    y,
                    width - CountWidth - HoldWidth,
                    RowHeight
                ),
                entry.projectile,
                HudColor,
                _leftStyle
            );

            // 한 문이라도 쏘고 있으면 빈칸이다. 멀쩡한 것에 이름표를 붙이면 이름표가
            // 배경이 되고, 진짜 막혔을 때 그것이 안 보인다.
            if (!entry.anyReady)
            {
                DrawText(
                    new Rect(
                        x + width - CountWidth - HoldWidth,
                        y,
                        HoldWidth,
                        RowHeight
                    ),
                    HoldLabel(entry.hold),
                    entry.hold >= Gun.HoldReason.LineBlocked ? WarnColor : DimColor,
                    _leftStyle
                );
            }

            DrawText(
                new Rect(
                    x + width - CountWidth,
                    y,
                    CountWidth,
                    RowHeight
                ),
                $"×{entry.guns}",
                DimColor,
                _rightStyle
            );

            y += RowHeight;
        }
    }


    /// <summary>
    /// 같은 projectile을 사용하는 포는 한 줄로 묶는다.
    ///
    /// Dictionary까지 만들 이유가 없는 작은 목록이므로
    /// 단순 선형 탐색한다.
    /// </summary>
    private void BuildWeaponEntries(Ship ship)
    {
        _weapons.Clear();

        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            Gun gun = ship.shipGuns[i];

            // 잔해로 간 포와 부서진 포는 무장이 아니다.
            if (gun == null || gun.Neutralized || !Ship.StillAboard(gun, ship))
                continue;

            string projectile =
                string.IsNullOrEmpty(gun.projectile)
                    ? "UNKNOWN"
                    : gun.projectile;

            int found = -1;

            for (int j = 0; j < _weapons.Count; j++)
            {
                if (_weapons[j].projectile == projectile)
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                WeaponHudEntry entry =
                    _weapons[found];

                entry.guns++;

                Merge(ref entry, gun.Hold);

                _weapons[found] = entry;
            }
            else
            {
                WeaponHudEntry entry = new()
                {
                    projectile = projectile,
                    guns = 1
                };

                Merge(ref entry, gun.Hold);

                _weapons.Add(entry);
            }
        }
    }


    /// <summary>
    /// 포 한 문의 사유를 줄에 접는다. **제일 무거운 것 하나만 남긴다** - 열두 문이
    /// 저마다 다른 이유로 멈춰 있어도 함장이 할 일은 하나고, 그것은 제일 큰 이유다.
    /// <see cref="Gun.HoldReason"/>의 선언 순서가 그 무게다.
    ///
    /// 세는 대신 최댓값인 이유: 여섯 문이 선회 중이고 여섯 문이 사선에 막혔으면
    /// 다수결은 "선회"라고 말하는데, 배를 돌려야 한다는 사실은 그대로다.
    /// </summary>
    private static void Merge(ref WeaponHudEntry entry, Gun.HoldReason hold)
    {
        if (hold == Gun.HoldReason.None)
        {
            entry.anyReady = true;
            return;
        }

        if (hold > entry.hold)
            entry.hold = hold;
    }


    private static string HoldLabel(Gun.HoldReason hold) => hold switch
    {
        // 방아쇠·컷신 사격중지·미사일 잠금 실패가 전부 여기다. 셋 다 "지금은 안 쏜다"고
        // 스스로 정한 것이라 함장이 고칠 것이 없다.
        Gun.HoldReason.Trigger => "HOLD",
        Gun.HoldReason.Reloading => "RELOAD",
        Gun.HoldReason.Slewing => "SLEWING",
        Gun.HoldReason.NoTarget => "NO TARGET",
        Gun.HoldReason.LineBlocked => "LINE BLOCKED",
        Gun.HoldReason.OutOfArc => "OUT OF ARC",
        Gun.HoldReason.NoGunner => "NO GUNNER",
        Gun.HoldReason.NoAmmo => "NO AMMO",

        // Adrift/Destroyed는 BuildWeaponEntries가 이미 걸러서 여기 안 온다.
        _ => "",
    };


    // ------------------------------------------------------------
    // WORLD OVERLAY
    // ------------------------------------------------------------

    private static void DrawVelocityVector(
        Ship ship,
        Camera cam
    )
    {
        Vector2 velocity =
            ship.velocity;

        float speed =
            velocity.magnitude;

        if (speed < MinVisibleSpeed)
            return;

        Vector3 originWorld =
            ship.transform.position;

        Vector2 origin =
            WorldToGui(
                cam,
                originWorld
            );

        Vector2 direction =
            WorldDirectionToGui(
                cam,
                originWorld,
                new Vector3(
                    velocity.x,
                    velocity.y,
                    0f
                )
            );

        if (direction.sqrMagnitude <= 0f)
            return;

        float length =
            Mathf.Min(
                speed * VelocityPixelsPerSpeed,
                MaxVelocityLineLength
            );

        DrawLine(
            origin,
            origin + direction * length,
            HudColor,
            VelocityLineWidth
        );
    }


    /// <summary>
    /// 탄종(muzzleSpeed)별 리드 마커. 마우스를 이 십자에 두면 그 탄이 표적을 요격한다.
    ///
    /// 마커 자리는 표적의 미래 위치가 아니라 <c>표적 + 상대속도 × t</c>다 - 탄이 내 배
    /// 속도를 물려받으므로(Projectile.Launch) 조준 방향은 상대 프레임에서 풀리고, 마우스가
    /// 정하는 것은 포신 방향이라 마커도 그 방향 선상에 있어야 한다. 표적 미래 위치에 찍으면
    /// 내 배 속도만큼 어긋난다 - 탄속 1100에 배속 40이면 2도, fireArc(0.7도)보다 크다.
    ///
    /// 포구 위치·포탑 회전(w×r) 몫은 배 중심으로 근사한다 - 수백 m 사거리에서 반 함체
    /// 오차는 마커 픽셀 하나 아래다.
    /// </summary>
    private static readonly List<float> _leadSpeeds = new();

    private static void DrawLeadMarkers(
        Ship ship,
        Camera cam
    )
    {
        Ship target = ship.NearestHostile();

        if (target == null)
            return;

        _leadSpeeds.Clear();

        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            Gun gun = ship.shipGuns[i];

            if (gun == null || gun.Neutralized || !Ship.StillAboard(gun, ship))
                continue;

            if (!_leadSpeeds.Contains(gun.muzzleSpeed))
                _leadSpeeds.Add(gun.muzzleSpeed);
        }

        Vector2 d =
            (Vector2)target.transform.position
            - (Vector2)ship.transform.position;

        Vector2 relativeVelocity =
            target.velocity - ship.velocity;

        for (int i = 0; i < _leadSpeeds.Count; i++)
        {
            if (!Ballistics.InterceptTime(d, relativeVelocity, _leadSpeeds[i], out float t))
                continue;

            Vector2 aim =
                (Vector2)target.transform.position
                + relativeVelocity * t;

            Vector2 p = ClampToScreen(
                WorldToGui(cam, aim), LeadMarkerSize + MarkerEdgeInset);

            DrawLine(
                p + Vector2.left * LeadMarkerSize,
                p + Vector2.right * LeadMarkerSize,
                LeadColor,
                1f
            );

            DrawLine(
                p + Vector2.up * LeadMarkerSize,
                p + Vector2.down * LeadMarkerSize,
                LeadColor,
                1f
            );
        }
    }


    /// <summary>
    /// 미사일이 잠근 표적마다 십자 하나. **판이 아니라 몸에 찍힌다** - Launcher가
    /// Rigidbody의 transform을 잡기 때문이고, 그래서 잠긴 판이 죽어도 표시가 안 사라진다.
    ///
    /// 발사대가 여럿이면 전부 같은 조준점(커서)을 보므로 대개 같은 것을 잠근다. 겹쳐
    /// 그리면 알파가 쌓여 유독 진한 십자가 되므로 한 번만 그린다.
    ///
    /// 여기서 <c>Launcher.Acquire</c>를 안 부르는 것이 중요하다 - 400m OverlapCircle이
    /// 틱(6틱마다)이 아니라 프레임을 타면 그것만으로 프레임이 죽는다. 캐시된 값만 읽는다.
    /// </summary>
    private static readonly List<Transform> _locks = new();

    private static void DrawLockMarkers(
        Ship ship,
        Camera cam
    )
    {
        _locks.Clear();

        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            // 부서진 포와 잔해로 간 포는 아무것도 안 잠근다 - WPN 목록과 같은 필터다.
            if (ship.shipGuns[i] is not Launcher launcher
                || launcher.Neutralized
                || !Ship.StillAboard(launcher, ship))
                continue;

            Transform locked = launcher.Locked;

            if (locked == null || _locks.Contains(locked))
                continue;

            _locks.Add(locked);
        }

        for (int i = 0; i < _locks.Count; i++)
        {
            Vector2 p = ClampToScreen(
                WorldToGui(cam, _locks[i].position), LockMarkerArm + MarkerEdgeInset);

            DrawLine(
                p + Vector2.left * LockMarkerArm,
                p + Vector2.left * LockMarkerGap,
                LockColor,
                1f
            );

            DrawLine(
                p + Vector2.right * LockMarkerGap,
                p + Vector2.right * LockMarkerArm,
                LockColor,
                1f
            );

            DrawLine(
                p + Vector2.up * LockMarkerArm,
                p + Vector2.up * LockMarkerGap,
                LockColor,
                1f
            );

            DrawLine(
                p + Vector2.down * LockMarkerGap,
                p + Vector2.down * LockMarkerArm,
                LockColor,
                1f
            );
        }
    }


    private static void DrawGunAimVectors(
        Ship ship,
        Camera cam
    )
    {
        for (int i = 0; i < ship.shipGuns.Count; i++)
        {
            Gun gun =
                ship.shipGuns[i];

            // 잔해로 간 포탑은 null이 아니다 - 소속을 다시 확인해야 남의 조준선을 안 그린다.
            if (gun == null || !Ship.StillAboard(gun, ship))
                continue;

            // 못 쏘는 포에는 조준선이 없다 - 부서졌거나 포수가 없다(전력·승무원). 선이 있으면 쏠 수 있다는 약속이다.
            if (gun.Neutralized || gun.Hold == Gun.HoldReason.NoGunner)
                continue;

            Transform turret =
                gun.Turret;

            if (turret == null)
                continue;

            Vector2 origin =
                WorldToGui(
                    cam,
                    turret.position
                );

            Vector2 direction =
                WorldDirectionToGui(
                    cam,
                    turret.position,
                    turret.up
                );

            if (direction.sqrMagnitude <= 0f)
                continue;

            DrawLine(
                origin,
                origin + direction * GunAimLineLength,
                GunAimColor,
                GunAimLineWidth
            );
        }
    }


    // ------------------------------------------------------------
    // GUI
    // ------------------------------------------------------------

    /// <summary>
    /// 패널 하나. <paramref name="value"/>는 헤더 오른쪽 끝에 붙는 **그 패널의 한 줄 요약**이다.
    ///
    /// 이게 있어야 하는 이유: FLIGHT만 라벨+숫자+단위를 주고 AIRFRAME·CONTACT·WPN은
    /// 그림이거나 단어라 "얼마나"를 못 답했다. 네 패널이 네 문법을 쓰면 플레이어가 매번
    /// 새로 읽는다. 제목 옆 숫자 하나가 그 넷을 같은 문법으로 만든다.
    /// </summary>
    private static void DrawPanel(
        Rect rect,
        string title,
        string value = null,
        Color? valueColor = null
    )
    {
        DrawRect(
            rect,
            PanelBg
        );

        // 에컴 느낌의 최소한의 ㄱ자 테두리.
        DrawRect(
            new Rect(
                rect.x,
                rect.y,
                rect.width,
                1f
            ),
            DimColor
        );

        DrawRect(
            new Rect(
                rect.x,
                rect.y,
                1f,
                rect.height
            ),
            DimColor
        );

        DrawText(
            new Rect(
                rect.x + Padding,
                rect.y,
                rect.width - Padding * 2f,
                HeaderHeight
            ),
            title,
            HudColor,
            _titleStyle
        );

        if (string.IsNullOrEmpty(value))
            return;

        DrawText(
            new Rect(
                rect.x + Padding,
                rect.y,
                rect.width - Padding * 2f,
                HeaderHeight
            ),
            value,
            valueColor ?? DimColor,
            _titleRightStyle
        );
    }


    private static void DrawValue(
        Rect panel,
        ref float y,
        string label,
        string value,
        Color valueColor
    )
    {
        float width =
            panel.width - Padding * 2f;

        float x =
            panel.x + Padding;

        DrawText(
            new Rect(
                x,
                y,
                70f,
                RowHeight
            ),
            label,
            DimColor,
            _leftStyle
        );

        DrawText(
            new Rect(
                x + 70f,
                y,
                width - 70f,
                RowHeight
            ),
            value,
            valueColor,
            _rightStyle
        );

        y += RowHeight;
    }


    private static void DrawRect(
        Rect rect,
        Color color
    )
    {
        ApplySection(ref rect, ref color);

        Color old =
            GUI.color;

        GUI.color =
            color;

        GUI.DrawTexture(
            rect,
            Texture2D.whiteTexture
        );

        GUI.color =
            old;
    }


    private static void DrawText(
        Rect rect,
        string text,
        Color color,
        GUIStyle style
    )
    {
        ApplySection(ref rect, ref color);

        Color old =
            GUI.contentColor;

        GUI.contentColor =
            color;

        GUI.Label(
            rect,
            text,
            style
        );

        GUI.contentColor =
            old;
    }


    private static void DrawLine(
        Vector2 a,
        Vector2 b,
        Color color,
        float width
    )
    {
        Vector2 delta =
            b - a;

        if (delta.sqrMagnitude <= 0.0001f)
            return;

        float angle =
            Mathf.Atan2(
                delta.y,
                delta.x
            )
            * Mathf.Rad2Deg;

        Matrix4x4 oldMatrix =
            GUI.matrix;

        Color oldColor =
            GUI.color;

        GUI.color =
            color;

        GUIUtility.RotateAroundPivot(
            angle,
            a
        );

        GUI.DrawTexture(
            new Rect(
                a.x,
                a.y - width * 0.5f,
                delta.magnitude,
                width
            ),
            Texture2D.whiteTexture
        );

        GUI.matrix =
            oldMatrix;

        GUI.color =
            oldColor;
    }


    private static void EnsureStyles()
    {
        if (_titleStyle != null)
            return;

        _titleStyle =
            new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

        _titleRightStyle =
            new GUIStyle(_titleStyle)
            {
                alignment = TextAnchor.MiddleRight
            };

        _leftStyle =
            new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

        _objectiveStyle =
            new GUIStyle(_leftStyle)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

        _rightStyle =
            new GUIStyle(_leftStyle)
            {
                alignment = TextAnchor.MiddleRight
            };
    }


    // ------------------------------------------------------------
    // COORDINATES
    // ------------------------------------------------------------

    /// <summary>
    /// 화면 밖 조준 표지를 가장자리로 접는다.
    ///
    /// 안 접으면 <c>GUI.DrawTexture</c>가 조용히 아무것도 안 그린다 - 예외도 로그도
    /// 없어서 "약속만 하고 안 뜨는 계기"가 된다. `DrawLeadMarkers`의 주석은 "마우스를
    /// 이 십자에 두면 맞는다"고 말하는데, 교전거리 120~420 m에 화면 반폭이 17.8~32 m라
    /// **그 약속이 지켜지는 거리가 교전거리 안에 없었다.**
    ///
    /// <see cref="ContactView"/>가 접촉 표지에 이미 하는 일이다. 조준선만 안 하고 있었다.
    /// 접힌 십자는 위치가 아니라 방위를 뜻한다 - "그쪽으로 겨눠라"가 화면 밖 표적에
    /// 대해 할 수 있는 유일하게 정직한 말이다.
    ///
    /// **배율 행렬 밖이라 실제 화면 픽셀이다**(OnGUI 참고). GUIManager.Logical*이 아니다.
    /// </summary>
    private static Vector2 ClampToScreen(Vector2 p, float inset)
        => new(
            Mathf.Clamp(p.x, inset, Screen.width - inset),
            Mathf.Clamp(p.y, inset, Screen.height - inset));


    private static Vector2 WorldToGui(
        Camera cam,
        Vector3 world
    )
    {
        Vector3 screen =
            cam.WorldToScreenPoint(world);

        // 배율 행렬 밖에서 부르므로(OnGUI 참고) 나눌 것도 없다 - 그대로 실제 화면
        // 픽셀이다.
        return new Vector2(
            screen.x,
            Screen.height - screen.y
        );
    }


    private static Vector2 WorldDirectionToGui(
        Camera cam,
        Vector3 origin,
        Vector3 direction
    )
    {
        if (direction.sqrMagnitude <= 0.000001f)
            return Vector2.zero;

        Vector2 a =
            WorldToGui(
                cam,
                origin
            );

        Vector2 b =
            WorldToGui(
                cam,
                origin + direction.normalized
            );

        Vector2 delta =
            b - a;

        return delta.sqrMagnitude > 0.000001f
            ? delta.normalized
            : Vector2.zero;
    }


    // ------------------------------------------------------------
    // STATUS
    // ------------------------------------------------------------

    private static Color StatusColor(
        float fraction
    )
    {
        if (fraction < 0.15f)
            return CriticalColor;

        if (fraction < 0.40f)
            return WarnColor;

        return HudColor;
    }


}
