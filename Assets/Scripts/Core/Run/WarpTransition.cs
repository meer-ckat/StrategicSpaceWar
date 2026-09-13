using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 구역 사이의 워프 연출. 정비창의 출항에서 다음 구역의 첫 틱까지 코루틴 하나다.
/// Campaign은 이 파일을 모른다 - 컷신이 Campaign을 아는 방향 그대로다. 부르는 것은
/// <see cref="Campaign.Depart"/>, <see cref="Campaign.Prepare"/>, <see cref="Campaign.Enter"/> 셋뿐이다.
///
/// 네 박자다. **충전**(엔진이 달아오르고 카메라가 밀고 들어온다) → **점프**(늘어지며 섬광과
/// 함께 화면 밖으로) → **이동**(검정 카드) → **이탈**(섬광, 감속, 계기 복귀). 박자만 있고 안이
/// 비면 미완성으로 읽힌다 - Homeworld·BSG·FTL·Elite가 공통으로 가진 골격이다.
///
/// **연출 내내 틱이 멎어 있다.** 점프의 줄기는 물리가 아니라 transform을 미는 그림이고 어차피
/// 암전 밑에서 순간이동한다. 그래서 남은 탄·잔해가 입력 끊긴 배를 못 때리고, 실시간 페이드가
/// 틱 번호를 못 흔든다(Ballistics.Hash가 틱을 시드로 쓴다). 엔진 불빛·불꽃은 Update에서
/// 돌아서(`Ship.Boosting`, `BoosterComp`) 틱이 멎어도 켜진다.
///
/// 길이·크기·색·문구는 초안이다. 여기부터는 오너가 고친다.
/// </summary>
public static class WarpTransition
{
        // ─────────────────────────────────────────────────────────────────────────────
    // WARP TRANSITION — CINEMATIC PRESET
    // ─────────────────────────────────────────────────────────────────────────────

    // ── 전체 전환 / 암전 ──────────────────────────────────────────────────────────

    private const float BlackIn = 0.65f;
    private const float BlackOut = 0.70f;

    private const float ChargeSeconds = 4.20f;


    // ── 충전 카메라 ───────────────────────────────────────────────────────────────

    private const float CameraSize = 175f;
    private const float ChargeSize = 225f;

    private const float ChargeDamp = 0.35f;
    private const float ChargeShake = 3.80f;


    // ── 함대 출발 타이밍 ──────────────────────────────────────────────────────────

    private const float StaggerSeconds = 0.11f;
    private const float StreakBlackSeconds = 0.1f;   // 카메라가 배를 놓치는 순간 이만큼에 걸쳐 암전. 그 뒤 스트릭은 검정 밑이라 끊는다

    private const float AnticipationSeconds = 0.22f;
    private const float AnticipationBack = 1.40f;
    private const float AnticipationSquash = 0.10f;
    private const float AnticipationBulge = 0.04f;


    // ── 점프 추격 카메라 ──────────────────────────────────────────────────────────

    private const float ChaseMax = 640f;
    private const float ChaseAccel = 2400f;

    private const float LoseDistance = 210f;
    private const float ChaseDrag = 1.70f;

    private const float ChaseSize = 125f;
    private const float LostSize = 240f;

    private const float ChaseStarStretch = 0.65f;


    // ── 실제 점프 이동 ────────────────────────────────────────────────────────────

    public const float StreakSpeed = 4300f;
    public const float RampSeconds = 0.085f;
    public const float ExitDistance = 1050f;

    private const float StretchMax = 1.85f;


    // ── 점프 순간 ─────────────────────────────────────────────────────────────────

    private const float JumpFlashAlpha = 1.00f;
    private const float JumpFlashSeconds = 0.16f;

    private const float JumpShake = 14.0f;
    private const float MateShake = 0.65f;


    // ── 점프 후 여운 ──────────────────────────────────────────────────────────────

    private const float TailSeconds = 0f;
    private const float TitleSeconds = 2.20f;
    // 이동 중. 암전 뒤 거품 안의 배를 보여준다 - 바깥 우주는 검고 별은 흐르는 빛줄기, 캡션은 "어디 → 어디 이동 중".
    // 초안 - 숫자·문구는 오너가 고친다.
    private const float TransitSeconds = 5f;
    private const float TransitSize = 48f;           // 카메라 크기. 배 한 척이 화면 1/3
    private const float TransitDark = 0.92f;         // 거품 밖 어둠. 1이면 별이 아예 없다
    private const float TransitStretch = 0.7f;       // 별 늘림. 흐르는 빛줄기의 길이
    private const float TransitFlow = 6f;            // 하늘이 흐르는 속도(도/초)
    private const float TransitBubbleRadius = 0.42f; // 거품 반지름. 배가 안에 넉넉히
    private const float TransitDrive = 0.05f;
    private const float TransitPulse = 0.35f;        // 구동이 숨 쉬는 폭(비율)


    // ── 도착 ──────────────────────────────────────────────────────────────────────

    private const float ArriveFlashAlpha = 0.75f;
    private const float ArriveFlashSeconds = 0.18f;

    private const float ArriveShake = 6.50f;
    private const float ArriveOut = 2.60f;

    // 현재 root transform을 늘리는 구조라면,
    // 물리 틱을 선체 복구보다 먼저 풀지 않도록 맞춘다.
    private const float ArriveLead = 0f;   // 게이지가 다 찬 순간이 진입이다(2026-09-12). 섬광과 같은 프레임

    private const float ArriveStretch = 1.55f;
    private const float ArriveRecover = 0.32f;


    // ── 안전장치 ──────────────────────────────────────────────────────────────────

    private const float WatchdogSeconds = 25f;    // 충전 3 + 점프 ~1.5 + 이동 5 + 도착 2 + 암전 셋. 여유 두 배


    // ── 텍스트 / VFX / SFX ────────────────────────────────────────────────────────

    private const string ChargeText = "워프 드라이브 충전";

    private const string ArriveVfx = "ShipIncoming";

    private const string ChargeSfx = "Warp_Charge";
    private const string JumpSfx = "Warp_Jump";
    private const string ArriveSfx = "Warp_Arrive";


    // ── 워프 색 ───────────────────────────────────────────────────────────────────

    public static readonly Color WarpColour =
        new(0.55f, 0.80f, 1.00f);


    // ─────────────────────────────────────────────────────────────────────────────
    // ALCUBIERRE BUBBLE
    // ─────────────────────────────────────────────────────────────────────────────

    // 충전 시 함대를 감싸는 공간 왜곡 반경
    private const float ChargeBubbleRadius = 0.26f;

    // 초반에는 넓고 흐린 벽 → 충전 끝에는 날카로운 벽
    private const float ChargeWallSoft = 0.20f;
    private const float ChargeWallSharp = 0.025f;

    // 앞 공간 압축 / 뒤 공간 팽창
    private const float ChargeDrive = 0.052f;

    // 약한 도플러 계열 색 갈림
    private const float ChargeShift = 0.008f;


    // ── 점프 버블 ─────────────────────────────────────────────────────────────────

    private const float JumpDrive = 0.14f;
    private const float JumpBubbleSeconds = 1.48f;


    // ── 도착 버블 ─────────────────────────────────────────────────────────────────

    private const float ArriveBubbleRadius = 0.38f;
    private const float ArriveBubbleSeconds = 0.55f;
    private const float ArriveDrive = 0.075f;


    // ── 엔진 플룸 ─────────────────────────────────────────────────────────────────

    private const float PlumeLength = 24f;


    // ─────────────────────────────────────────────────────────────────────────────
    // DEPARTURE SCREEN FX
    // ─────────────────────────────────────────────────────────────────────────────

    // 공간 충격 링
    private const float JumpRingSeconds = 0.42f;
    private const float JumpRingRadius = 1.55f;
    private const float JumpRingWidth = 0.045f;
    private const float JumpRingPush = 0.032f;

    // RGB split은 매우 약하게
    private const float JumpSplit = 0.0055f;

    // 별 늘림
    private const float JumpStarStretch = 0.55f;
    private const float JumpStarSeconds = 2f;

    // 출발점 잔광
    private const float JumpGlow = 0.90f;
    private const float JumpGlowSeconds = 0.20f;


    // ─────────────────────────────────────────────────────────────────────────────
    // ARRIVAL SCREEN FX
    // ─────────────────────────────────────────────────────────────────────────────

    private const float ArriveRingSeconds = 0.34f;
    private const float ArriveRingRadius = 1.05f;
    private const float ArriveRingWidth = 0.040f;
    private const float ArriveRingPush = 0.018f;

    private const float ArriveSplit = 0.0035f;

    private const float ArriveStarStretch = 0.30f;
    private const float ArriveStarSeconds = 0.24f;

    /// <summary>
    /// 점프 순간의 화면. 링이 출발점에서 퍼지고, 별이 축으로 늘었다 돌아오고, 출발점이 잠깐 빛난다.
    /// 거품은 출발점이 아니라 **배를 따라간다** - 알쿠비에레는 배가 아니라 거품이 움직인다.
    /// 배가 화면 밖으로 나가면 구동이 사그라든다.
    /// </summary>
    private static void JumpFx(Vector2 origin, Vector2 shipAt, Vector2 axis, float flight, float chase)
    {
        float ring = Mathf.Clamp01(flight / JumpRingSeconds);

        // 별 늘림 = 점프 순간의 한 번 + 카메라가 달리는 동안. 카메라가 서면 별도 선다.
        WarpFx.Set(origin, axis, 0f,
            JumpStarStretch * Mathf.Max(0f, 1f - flight / JumpStarSeconds) + ChaseStarStretch * chase,
            JumpGlow * Mathf.Max(0f, 1f - flight / JumpGlowSeconds));
        WarpFx.Ring(ring * JumpRingRadius, JumpRingWidth, JumpRingPush * (1f - ring), JumpSplit * (1f - ring));

        float fade = 1f - Mathf.Clamp01((flight - StreakSeconds) / JumpBubbleSeconds);
        WarpFx.Bubble(shipAt, ChargeBubbleRadius, ChargeWallSharp, JumpDrive * fade, ChargeShift * 2f * fade);
    }

    /// <summary>도착 순간의 화면. 거품이 배 둘레에서 0으로 무너지고, 작은 링과 짧은 별 늘림. 출발보다 약하게.</summary>
    private static void ArriveFx(Ship player, float t)
    {
        float ring = Mathf.Clamp01(t / ArriveRingSeconds);

        WarpFx.Set(player.transform.position, player.NoseDirection, 0f,
            ArriveStarStretch * Mathf.Max(0f, 1f - t / ArriveStarSeconds), 0f);
        WarpFx.Ring(ring * ArriveRingRadius, ArriveRingWidth, ArriveRingPush * (1f - ring), ArriveSplit * (1f - ring));

        float collapse = 1f - Mathf.Clamp01(t / ArriveBubbleSeconds);
        WarpFx.Bubble(player.transform.position, ArriveBubbleRadius * collapse, ChargeWallSharp, ArriveDrive * collapse, ChargeShift * collapse);
    }

    private static List<BoosterComp> Boosters(Ship player, List<Ship> mates)
    {
        var boosters = new List<BoosterComp>();

        foreach (Ship ship in Fleet(player, mates))
            boosters.AddRange(ship.GetComponentsInChildren<BoosterComp>());

        return boosters;
    }

    private static bool _done;
    private static GameObject _anchor;
    private static readonly List<(Transform t, Vector3 scale)> _stretched = new();

    // 몇 번째 연출인가. 감시견이 자기 연출을 알아보는 표다 - 정비 노드가 생기면서 20초 안에 두 번
    // 출항하는 일이 정상이 됐고(도착 → 정비 → 출항), 앞 연출의 감시견이 뒤 연출의 _done=false를
    // 보고 멀쩡한 전환을 되돌렸다. 증상은 "항로 화면 전환이 뚝 끊긴다"였다.
    private static int _serial;

    public static IEnumerator Run(LogisticsScreen screen, Campaign campaign, int lane)
    {
        _done = false;
        _serial++;

        // 정비창이 timeScale을 0으로 뒀다. 틱은 TickManager.Paused가 따로 세우므로 시계는
        // 돌려준다 - VFX 그래프와 엔진 불꽃이 이 시계를 탄다.
        Core.TickManager.Paused = true;
        Time.timeScale = 1f;

        string from = campaign.Current != null ? campaign.Current.name : "";

        // 항로 확정. 여기서 꺼도 다음 실행이 그 구역에서 열린다.
        campaign.Depart(lane);

        yield return screen.Cover(1f, BlackIn);

        Ship player = campaign.Player;
        List<Ship> mates = campaign.Wingmates();

        if (player != null)
        {
            Align(player, mates);

            // 입력 차단. 돌려주는 것은 Campaign.Prepare의 CutSceneManager.Clear다.
            CutSceneManager.Borrow("player", player);

            _anchor = new GameObject("Warp Anchor");
            _anchor.transform.position = player.transform.position;
            CameraSystem.CutsceneDamp(0f, 0f);
            CameraSystem.CutsceneFrame(_anchor.transform, null, 0f, CameraSize);
        }

        yield return screen.Cover(0f, BlackOut);

        if (player != null)
        {
            yield return Charge(screen, player, mates);
            yield return Streak(screen, player, mates);
        }

        yield return new WaitForSecondsRealtime(TailSeconds);
        yield return screen.Cover(1f, player != null ? 0.05f : BlackIn);   // 스트릭이 이미 검게 덮었다. 없었으면(배 없음) 여기서 덮는다

        // 암전 밑. 카메라를 먼저 놓아야 앵커를 지워도 죽은 Transform을 안 든다. 블렌드는
        // 끈다 - 배가 다른 구역으로 순간이동한 뒤라 3초 동안 수천 m를 팬한다.
        screen.Caption(null);
        DropAnchor();

        campaign.Prepare();   // CutSceneManager.Clear가 플레이어의 입력과 부스터를 돌려준다

        // 이동 중. 배는 새 구역의 대기 자리에 서 있고(Stage), 틱은 멎어 있다. 그 배를 거품 안에서 보여준다.
        string to = campaign.Current != null ? campaign.Current.name : "";
        yield return Transit(screen, campaign, from, to);   // 끝은 암전이다

        // 도착 카드. 검정 위에 구역 이름과 위·아래 게이지. 둘 다 차는 순간 들어간다 - Arrive의 첫 프레임이 Enter다.
        screen.ShowCard(campaign.Current, "도착", TitleSeconds);
        yield return new WaitForSecondsRealtime(TitleSeconds);
        screen.ClearCard();

        LogisticsScreen.IsOpen = false;   // 계기판·대사 게이트. 여기서부터 화면은 전투다
        yield return Arrive(screen, campaign);

        _done = true;
    }

    /// <summary>
    /// 이동 중. 암전을 걷으면 검은 우주에 배와 거품 벽만 있다. 별은 축 방향으로 늘어난 채
    /// 희미하게 흐른다(BackgroundView가 하늘을 돌린다). 캡션이 출발지 → 도착지를 말한다.
    /// 끝나면 다시 암전 - 부르는 쪽이 덮는다.
    /// </summary>
    private static IEnumerator Transit(LogisticsScreen screen, Campaign campaign, string from, string to)
    {
        Ship player = campaign.Player;

        if (player == null)
            yield break;

        // 배는 안 움직인다 - 움직이는 것은 거품이고, 우리는 거품 안에 있다.
        CameraSystem.CutsceneDamp(0f, 0f);
        CameraSystem.CutsceneFrame(player.transform, null, 0f, TransitSize);

        // **켠 자리가 끄는 자리다.** Charge가 켠 것은 Prepare의 CutSceneManager.Clear가
        // 돌려주지만(플레이어를 Borrow했으니 _ships에 있다), 여기는 그 Clear **뒤**라
        // 아무도 안 끈다 - 워프 한 번에 부스터가 영구히 켜졌고 증상은 "shift를 떼도
        // 계속 분사한다"였다. 원인이 전투 코드가 아니라 지난 구역의 연출이라 안 보인다.
        player.cutsceneBoost = true;

        Vector2 at = player.transform.position;
        Vector2 axis = player.NoseDirection;
        WarpFx.Set(at, axis, 0f, TransitStretch, 0f);
        WarpFx.Bubble(at, TransitBubbleRadius, ChargeWallSharp, TransitDrive, ChargeShift);
        WarpFx.Transit(TransitDark);
        BackgroundView.transitYawPerSecond = TransitFlow;

        screen.Caption(string.IsNullOrEmpty(from) ? $"{to}  이동 중" : $"{from}  →  {to}   이동 중");

        yield return screen.Cover(0f, BlackOut);

        for (float t = 0f; t < TransitSeconds; t += Time.unscaledDeltaTime)
        {
            // 구동이 숨 쉰다. 정지 화면이 아니라는 신호.
            float pulse = 1f + TransitPulse * Mathf.Sin(t * 2.1f) * Mathf.Sin(t * 0.7f + 1f);
            WarpFx.Bubble(at, TransitBubbleRadius, ChargeWallSharp, TransitDrive * pulse, ChargeShift * pulse);
            yield return null;
        }

        yield return screen.Cover(1f, BlackIn);

        BackgroundView.transitYawPerSecond = 0f;
        WarpFx.Off();
        screen.Caption(null);
        player.cutsceneBoost = false;
        CameraSystem.ReleaseCutscene(blend: false);
    }

    /// <summary>
    /// 감시. 코루틴이 예외로 끊기면 틱 정지·입력 차단·덮개가 영영 남는다. 정해진 시간 안에
    /// 안 끝났으면 되돌린다 - 그러면 Campaign의 Choosing 폴백이 연출 없이 다음 구역을 연다.
    /// try/finally를 안 쓰는 이유: Unity가 예외로 죽은 코루틴의 finally를 돌린다는 보장이 없다.
    /// </summary>
    /// <param name="run">본체 코루틴. 되돌리기 전에 **먼저 끊는다** - 예외가 아니라 그냥 느린 것이었으면 다음 프레임에 되돌린 상태를 도로 덮어쓴다.</param>
    public static IEnumerator Watchdog(LogisticsScreen screen, Campaign campaign, Coroutine run)
    {
        int mine = _serial;   // Run이 먼저 시작돼(StartCoroutine은 첫 yield까지 즉시 돈다) 이미 올라가 있다

        yield return new WaitForSecondsRealtime(WatchdogSeconds);

        if (_done || mine != _serial)
            yield break;

        Debug.LogError("[WarpTransition] 전환이 제때 안 끝났다. 상태를 되돌리고 연출 없이 간다.");

        if (run != null)
            screen.StopCoroutine(run);   // 중첩 yield 전부 같이 선다

        BackgroundView.transitYawPerSecond = 0f;

        Unstretch();
        WarpFx.Off();
        SetBoost(campaign.Player, campaign.Wingmates(), false);
        screen.Caption(null);
        screen.ClearCard();
        screen.Fade(0f, 0.3f);
        DropAnchor();
        CutSceneManager.Clear();
        LogisticsScreen.IsOpen = false;
        Core.TickManager.Paused = false;
        Time.timeScale = 1f;
    }

    private static void DropAnchor()
    {
        CameraSystem.ReleaseCutscene(blend: false);

        if (_anchor != null)
        {
            Object.Destroy(_anchor);
            _anchor = null;
        }
    }

    /// <summary>
    /// 함대를 +X로 세우고 동료를 편대 자리에 순간이동한다. 엔진 잃은 반파 함선은 편대
    /// 자리로 못 날아가므로 비행이 아니라 순간이동이고, 그래서 암전 밑이다.
    /// 자리에 잔해가 있으면 그 동료는 안 옮긴다 - 옮기면 재개 순간 RamImpact가 매 틱 간다.
    /// </summary>
    private static void Align(Ship player, List<Ship> mates)
    {
        Place(player, player.transform.position, 0f);

        // 틱이 멎어 있어 SyncTransforms가 안 돈다. 방금 돌린 플레이어를 겹침 검사가 보게 한다.
        Physics2D.SyncTransforms();

        foreach (Ship mate in mates)
        {
            if (mate == null || !mate.TryGetComponent(out ShipAi ai))
                continue;

            // 편대 오프셋은 편대장 좌표계(x 뱃머리, y 좌현)고 편대장은 방금 +X를 봤다.
            Vector2 slot = (Vector2)player.transform.position
                + Vector2.right * ai._formationOffset.x
                + Vector2.up * ai._formationOffset.y;

            if (mate.SpotIsClear(slot))
            {
                Place(mate, slot, 0f);
                Physics2D.SyncTransforms();
            }
        }
    }

    private static void Place(Ship ship, Vector2 at, float angle)
    {
        ship.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, 0f, angle));

        if (ship.TryGetComponent(out Rigidbody2D rig))
        {
            rig.position = at;
            rig.rotation = angle;
            rig.linearVelocity = Vector2.zero;
            rig.angularVelocity = 0f;
        }
    }

    /// <summary>
    /// 충전. 엔진을 켜서 제자리에서 달아오르고(틱이 없으니 안 움직인다), 카메라가 천천히
    /// 밀고 들어오고, 흔들림이 올라가고, 캡션 밑 선이 자란다. 부스터는 점프 중에도 켜 둔다 -
    /// 노즐 불꽃이 곧 꼬리다. 플레이어 것은 Prepare의 CutSceneManager.Clear가 끄고 동료는 지워진다.
    /// </summary>
    private static IEnumerator Charge(LogisticsScreen screen, Ship player, List<Ship> mates)
    {
        SetBoost(player, mates, true);
        SoundManager.AudioShot(ChargeSfx, 0.8f);
        CameraSystem.CutsceneDamp(ChargeDamp, ChargeDamp);
        CameraSystem.CutsceneFrame(_anchor.transform, null, 0f, ChargeSize);
        screen.Caption(ChargeText);
        List<BoosterComp> boosters = Boosters(player, mates);

        for (float t = 0f; t < ChargeSeconds; t += Time.unscaledDeltaTime)
        {
            float u = t / ChargeSeconds;

            // 거품이 배 둘레에 자란다 - 흐릿한 굴절이 유리막으로 굳고 앞뒤 두 잎이 짙어진다. 배 자체는 안 일그러진다.
            // 엔진 불꽃은 기둥으로 자란다 - 배를 늘리는 것보다 배 뒤의 에너지가 늘어나는 쪽이 워프로 읽힌다.
            WarpFx.Set(player.transform.position, player.NoseDirection, 0f, 0f, 0f);
            float grow = Mathf.SmoothStep(0f, 1f, u);
            WarpFx.Bubble(player.transform.position, ChargeBubbleRadius * grow, Mathf.Lerp(ChargeWallSoft, ChargeWallSharp, u * u),
                ChargeDrive * u * u, ChargeShift * u * u);
            foreach (BoosterComp booster in boosters)
                booster.Plume(PlumeLength * u, WarpColour);

            // 끝 35%부터 진동이 완전히 매끈하지 않게 한다. 진폭은 작고 주파수만 섞어서
            // "카메라 노이즈"가 아니라 드라이브가 임계점에 접근하는 느낌만 남긴다.
            float unstable = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.65f, 1f, u));
            float chatter = Mathf.Sin(t * 41f) * 0.10f + Mathf.Sin(t * 67f + 1.2f) * 0.05f;
            float shake = ChargeShake * u * u * (1f + chatter * unstable);

            CameraSystem.Shake(Mathf.Max(0f, shake));
            yield return null;
        }
    }

    private static void SetBoost(Ship player, List<Ship> mates, bool on)
    {
        foreach (Ship ship in Fleet(player, mates))
            ship.cutsceneBoost = on;
    }

    /// <summary>플레이어 선두, 살아 있는 것만.</summary>
    private static List<Ship> Fleet(Ship player, List<Ship> mates)
    {
        var fleet = new List<Ship>();

        if (player != null)
            fleet.Add(player);

        foreach (Ship mate in mates)
        {
            if (mate != null)
                fleet.Add(mate);
        }

        return fleet;
    }

    /// <summary>
    /// 한 척이 출발 뒤 t초에 간 거리. 세제곱으로 가속해 <see cref="RampSeconds"/>에 최고 속도,
    /// 그 뒤 등속, <see cref="ExitDistance"/>에서 멈춘다. 등속 직선은 워프가 아니라 항해로 읽힌다.
    /// </summary>
    public static float StreakDistance(float t) => Mathf.Min(StreakDistanceRaw(t), ExitDistance);

    /// <summary>ExitDistance로 안 자른 거리. 추격 카메라가 자기 기준으로 자른다.</summary>
    public static float StreakDistanceRaw(float t)
    {
        float u = Mathf.Clamp01(t / RampSeconds);
        float d = StreakSpeed * RampSeconds * u * u * u * u / 4f;   // ∫ v·u³ = v·T·u⁴/4

        if (t > RampSeconds)
            d += StreakSpeed * (t - RampSeconds);

        return d;
    }

    /// <summary>한 척이 <see cref="ExitDistance"/>까지 가는 데 걸리는 시간.</summary>
    public static float StreakSeconds =>
        RampSeconds + (ExitDistance - StreakSpeed * RampSeconds / 4f) / StreakSpeed;

    /// <summary>
    /// 점프. 각 함선은 자기 차례 직전에 아주 짧게 뒤로 눌렸다가 발사된다. 플레이어 출발 때만
    /// 카메라 앵커도 진행 방향으로 끌려갔다 복귀한다. 선체 stretch는 과장하지 않고 속도감은
    /// 이동·섬광·카메라가 나눠 맡는다.
    /// </summary>
    private static IEnumerator Streak(LogisticsScreen screen, Ship player, List<Ship> mates)
    {
        List<Ship> ships = Fleet(player, mates);
        int count = ships.Count;
        var origins = new Vector2[count];
        var started = new bool[count];

        _stretched.Clear();

        for (int i = 0; i < count; i++)
        {
            origins[i] = ships[i].transform.position;
            _stretched.Add((ships[i].transform, ships[i].transform.localScale));
        }

        Vector3 anchorHome = _anchor != null ? _anchor.transform.position : Vector3.zero;
        // 거품이 사그라드는 시간까지 돈다 - 배는 이미 ExitDistance에서 멈춰 있어 더 안 움직인다.
        float total = StaggerSeconds * (count - 1) + AnticipationSeconds + StreakSeconds + JumpBubbleSeconds;

        // 추격 카메라 상태. 앵커가 배를 쫓아 달리다 LoseDistance에서 놓치고 미끄러져 선다.
        Vector2 camPos = anchorHome;
        float camSpeed = 0f;
        bool lost = false;
        float lostAt = -1f;
        Vector2 chaseAxis = player != null ? player.NoseDirection : Vector2.right;

        for (float t = 0f; t < total; t += Time.unscaledDeltaTime)
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < count; i++)
            {
                Ship ship = ships[i];
                float local = t - StaggerSeconds * i;

                if (ship == null || local < 0f)
                    continue;

                Vector3 home = _stretched[i].scale;

                // anticipation: 위치도 조금 뒤로 물리고 진행축을 눌렀다가 정확히 원점에서 발사한다.
                if (local < AnticipationSeconds)
                {
                    float a = Mathf.Clamp01(local / AnticipationSeconds);
                    float pulse = Mathf.Sin(a * Mathf.PI);

                    ship.transform.position = origins[i] - ship.NoseDirection * (AnticipationBack * pulse);
                    ship.transform.localScale = new Vector3(
                        home.x * (1f - AnticipationSquash * pulse),
                        home.y * (1f + AnticipationBulge * pulse),
                        home.z);
                    continue;
                }

                float flight = local - AnticipationSeconds;

                if (!started[i])
                {
                    started[i] = true;
                    CameraSystem.Shake(i == 0 ? JumpShake : MateShake);
                    SoundManager.AudioShot(JumpSfx, i == 0 ? 1f : 0.6f);

                    if (i == 0)
                        screen.Flash(JumpFlashAlpha, JumpFlashSeconds);
                }

                // 카메라가 따라온 만큼 더 간다 - ExitDistance는 원점이 아니라 카메라 기준이라야 화면 밖이다.
                // 이동축은 함대 공통(편대장 기수)이다. Align이 자리를 못 잡은 동료는 기수가 제멋대로라, 자기 기수로 보내면 딴 데로 간다.
                float camAlong = Vector2.Dot(camPos - origins[i], chaseAxis);
                float d = Mathf.Min(StreakDistanceRaw(flight), Mathf.Max(0f, camAlong) + ExitDistance);
                ship.transform.position = origins[i] + chaseAxis * d;

                if (i == 0)
                {
                    // 쫓는다: 배가 LoseDistance 안이면 가속, 넘어서면 놓친 것 - 그 뒤로는 미끄러지기만.
                    float gap = Vector2.Dot((Vector2)ship.transform.position - camPos, chaseAxis);
                    if (!lost && gap > LoseDistance)
                    {
                        lost = true;
                        lostAt = t;
                        screen.Fade(1f, StreakBlackSeconds);   // 놓친 것이 곧 전환이다
                    }

                    camSpeed = lost
                        ? camSpeed * Mathf.Exp(-ChaseDrag * dt)
                        : Mathf.Min(ChaseMax, camSpeed + ChaseAccel * dt);
                    camPos += chaseAxis * (camSpeed * dt);

                    if (_anchor != null)
                    {
                        _anchor.transform.position = camPos;
                        float chase = camSpeed / ChaseMax;
                        float size = lost
                            ? Mathf.Lerp(ChaseSize, LostSize, 1f - chase)
                            : Mathf.Lerp(ChargeSize, ChaseSize, chase);
                        CameraSystem.CutsceneFrame(_anchor.transform, null, 0f, size);
                    }

                    JumpFx(origins[0], ship.transform.position, chaseAxis, flight, camSpeed / ChaseMax);
                }

                float u = Mathf.Clamp01(flight / RampSeconds);
                float eased = u * u * (3f - 2f * u);
                float stretch = Mathf.Lerp(1f, StretchMax, eased);
                ship.transform.localScale = new Vector3(home.x * stretch, home.y, home.z);
            }

            // 검정이 다 덮였으면 나머지는 안 보인다. 끊는다.
            if (lostAt >= 0f && t - lostAt > StreakBlackSeconds + 0.05f)
                break;

            yield return null;
        }

        // 앵커는 놓친 자리에 둔다. 원점으로 되돌리면 꼬리 0.6초 동안 카메라가 수백 m를 튄다 - 암전 밑의 DropAnchor가 치운다.
        WarpFx.Off();
        Unstretch();
    }

    /// <summary>원래 크기로. 감시가 예외 뒤에도 부르므로 몇 번 불러도 같다.</summary>
    private static void Unstretch()
    {
        foreach ((Transform t, Vector3 scale) in _stretched)
        {
            if (t != null)
                t.localScale = scale;
        }

        _stretched.Clear();
    }

    /// <summary>
    /// 이탈. 암전 밑에서 선체를 길게 늘려 두고, 덮개가 걷히는 첫 0.24초에 원래 형태로 복구한다.
    /// ArriveLead에서 틱을 풀어 campaign.Enter의 슬라이드와 실루엣 복원이 겹치게 한다.
    /// </summary>
    private static IEnumerator Arrive(LogisticsScreen screen, Campaign campaign)
    {
        Ship player = campaign.Player;

        _stretched.Clear();
        if (player != null)
        {
            Vector3 home = player.transform.localScale;
            _stretched.Add((player.transform, home));
            player.transform.localScale = new Vector3(home.x * ArriveStretch, home.y, home.z);
            VfxOneShot.Play(ArriveVfx, player.transform.position);
        }

        screen.Flash(ArriveFlashAlpha, ArriveFlashSeconds);
        CameraSystem.Shake(ArriveShake);
        SoundManager.AudioShot(ArriveSfx, 1f);
        screen.Fade(0f, ArriveOut);

        bool entered = false;

        for (float t = 0f; t < ArriveOut; t += Time.unscaledDeltaTime)
        {
            if (player != null)
                ArriveFx(player, t);

            if (!entered && t >= ArriveLead)
            {
                entered = true;
                campaign.Enter(slide: true);
            }

            // t가 ArriveRecover를 넘어도 계속 놓는다 - 안 그러면 마지막 프레임의 찌그러짐이 Unstretch(2초 뒤)까지 남는다.
            if (player != null && _stretched.Count != 0)
            {
                Vector3 home = _stretched[0].scale;
                float u = Mathf.Clamp01(t / ArriveRecover);
                float easeOut = 1f - Mathf.Pow(1f - u, 3f);
                float stretch = Mathf.Lerp(ArriveStretch, 1f, easeOut);
                player.transform.localScale = new Vector3(home.x * stretch, home.y, home.z);
            }

            yield return null;
        }

        if (!entered)
            campaign.Enter(slide: true);

        WarpFx.Off();
        Unstretch();
    }
}
