using System.Collections.Generic;
using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;
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
    private const float PausedFlightScale = 0.7f;   // 정지 회로도에서 FLIGHT 패널 크기
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

            // 정지 회로도: 시뮬이 들고 있는 값을 전부 그 자리에 적는다 - 기기 전압·전류, 뜨거운 구간, 승무원.
            if (Core.TickManager.UserPaused && PauseControl.Blend >= 1f)
            {
                DrawInteriorReadouts(ship, cam);
                DrawSelection(cam);
            }
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
        DrawPausedBanner();
        if (BeginSection(ship, 0)) DrawHitGutter();
        if (BeginSection(ship, 0)) DrawContactPanel(ship);
        // 정지 회로도에서는 AIRFRAME이 왼쪽으로, WEAPONS가 오른쪽으로 빠지고 FLIGHT는 작아진다 - 화면이 배의 것이다.
        // 블루프린트 전선과 같은 시계(PauseControl.Blend)라 따로 타이머가 없다.
        float tween = Mathf.SmoothStep(0f, 1f, PauseControl.Blend);
        Matrix4x4 panels = GUI.matrix;

        if (BeginSection(ship, 2))
        {
            GUI.matrix = panels * Matrix4x4.Translate(new Vector3(-(AirframeWidth + Margin) * tween, 0f, 0f));
            DrawAirframePanel(ship);
            GUI.matrix = panels;
        }

        if (BeginSection(ship, 1))
        {
            // 오른쪽 위 모서리를 축으로 줄인다 - 자리는 그대로, 크기만.
            float k = Mathf.Lerp(1f, PausedFlightScale, tween);
            var pivot = new Vector3(GUIManager.LogicalWidth - Margin, Margin, 0f);
            GUI.matrix = panels * Matrix4x4.Translate(pivot) * Matrix4x4.Scale(new Vector3(k, k, 1f)) * Matrix4x4.Translate(-pivot);
            DrawFlightPanel(ship);
            GUI.matrix = panels;
        }

        if (BeginSection(ship, 0))
        {
            GUI.matrix = panels * Matrix4x4.Translate(new Vector3((WeaponWidth + Margin) * tween, 0f, 0f));
            DrawWeaponPanel(ship);
            GUI.matrix = panels;
        }

        // 정지 검사 패널. 소등과 무관 - 죽어가는 배일수록 읽어야 한다.
        if (Core.TickManager.UserPaused && Inspect.Any)
            DrawInspectPanel(ship);

        // 마지막이라 전부 위에 온다. 같은 OnGUI 안에서는 나중에 그린 것이 위다.
        if (BeginSection(ship, 0)) DrawScuttleWarning(ship);

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
    // INSPECT (정지 검사 - 림월드식. 클릭한 것 하나의 진짜 값 전부)
    // ------------------------------------------------------------

    private const float InspectWidth = 360f;
    private static readonly List<(string label, string value, Color color)> _inspectRows = new();
    private static string _inspectTitle = "";
    private static Color _inspectTitleColor = Color.white;

    private static void Row(string label, string value) => _inspectRows.Add((label, value, HudColor));
    private static void Row(string label, string value, Color c) => _inspectRows.Add((label, value, c));

    private static void DrawInspectPanel(Ship player)
    {
        _inspectRows.Clear();
        _inspectTitle = "";
        _inspectTitleColor = HudColor;

        if (Inspect.Crew != null) FillCrew(Inspect.CrewShip, Inspect.Crew);
        else if (Inspect.Wire is Inspect.WireSel w && w.ship != null) FillWire(w);
        else if (Inspect.Thing != null) FillThing(Inspect.Thing);

        if (_inspectRows.Count == 0)
            return;

        float height = HeaderHeight + RowHeight * _inspectRows.Count + Padding;
        var panel = new Rect((GUIManager.LogicalWidth - InspectWidth) * 0.5f, GUIManager.LogicalHeight - height - Margin, InspectWidth, height);

        DrawPanel(panel, "INSPECT");
        DrawText(new Rect(panel.x + InspectWidth * 0.35f, panel.y + 3f, InspectWidth * 0.63f, HeaderHeight - 4f),
            _inspectTitle, _inspectTitleColor, _rightStyle);

        float x = panel.x + Padding, width = panel.width - Padding * 2f, y = panel.y + HeaderHeight;

        foreach ((string label, string value, Color color) in _inspectRows)
        {
            DrawText(new Rect(x, y, width * 0.42f, RowHeight), label, DimColor, _leftStyle);
            DrawText(new Rect(x + width * 0.42f, y, width * 0.58f, RowHeight), value, color, _rightStyle);
            y += RowHeight;
        }
    }

    private static void FillThing(Component thing)
    {
        Ship ship = thing.GetComponentInParent<Ship>();
        string where = ship != null ? ship.SectionName(ship.transform.InverseTransformPoint(thing.transform.position)) : "";
        bool wired = ship != null && ship.HasWiring;
        float v = 0f, i = 0f;
        bool hasPower = ship != null && ship.TryGetReadout(thing, out v, out i);

        switch (thing)
        {
            case Gun g:
                _inspectTitle = g.defName;
                Row("상태", g.Neutralized ? "파괴" : g.Hold == Gun.HoldReason.None ? "사격 가능" : HoldLabel(g.Hold),
                    g.Neutralized ? CriticalColor : g.Hold >= Gun.HoldReason.NoGunner ? CriticalColor : HudColor);
                Row("내구", $"{g.Health01:P0}", g.Health01 < 0.5f ? WarnColor : HudColor);
                if (wired)
                {
                    bool fed = v >= Ballistics.PowerNominal * Ballistics.PowerBrownout;
                    Row("전압 · 전류", hasPower ? $"{v:0} V   {i:0.#} A" : "연결 없음", fed ? HudColor : CriticalColor);
                    Row("선회", $"{g.slewRate * Mathf.Clamp01(v / Ballistics.PowerNominal):0}°/s  (정격 {g.slewRate:0})");
                }
                else Row("선회", $"{g.slewRate:0}°/s");
                Row("정격", $"{g.powerWatts:0} W");
                Row("발사", $"{g.roundsPerMinute:0} rpm · {g.muzzleSpeed:0} m/s");
                Row("탄", string.IsNullOrEmpty(g.projectile) ? "-" : g.projectile);
                if (g.traverse > 0f) Row("사각", $"±{g.traverse:0}°");
                Row("위치", where);
                break;

            case CriticalModule c when c.providesPower:
                _inspectTitle = c.defName;
                _inspectTitleColor = Palette.Radiance;
                Row("상태", c.Neutralized ? "유폭" : "정상", c.Neutralized ? CriticalColor : HudColor);
                Row("내구", $"{c.Health01:P0}", c.Health01 < 0.5f ? WarnColor : HudColor);
                Row("상전압", $"{c.PhaseVoltage:0} V  (선간 {c.lineVoltage:0} V, {c.phases}상)");
                Row("내부저항", $"{c.sourceResistance * 1000f:0} mΩ");
                if (wired) Row("출력", hasPower ? $"{i:0.#} A · {v * i / 1000f:0.0} kW" : "연결 없음");
                Row("유폭 위력", $"{c.blastDamage:0}");
                Row("위치", where);
                break;

            case CriticalModule c:
                _inspectTitle = c.defName;
                Row("상태", c.Neutralized ? "유폭" : "정상", c.Neutralized ? CriticalColor : HudColor);
                Row("내구", $"{c.Health01:P0}", c.Health01 < 0.5f ? WarnColor : HudColor);
                if (c.maxRounds > 0) Row("탄약", $"{c.Rounds:N0} / {c.maxRounds:N0} 칸", c.Rounds01 < 0.25f ? WarnColor : HudColor);
                Row("유폭 위력", $"{c.blastDamage:0}");
                Row("위치", where);
                break;

            case Engine e:
                _inspectTitle = e.defName;
                Row("내구", $"{e.Health01:P0}", e.Health01 < 0.5f ? WarnColor : HudColor);
                Row("추력", $"{e.MaxPower:0} kN");
                Row("위치", where);
                break;

            case Tank t:
                _inspectTitle = t.defName;
                Row("내구", $"{t.Health01:P0}", t.Health01 < 0.5f ? WarnColor : HudColor);
                Row("추진제", $"{t.remaining:0} / {t.impulse:0} kN·s", t.impulse > 0f && t.remaining / t.impulse < 0.25f ? WarnColor : HudColor);
                Row("위치", where);
                break;

            case Armor a:
                _inspectTitle = a.defName;
                Row("내구", $"{a.HealthFraction:P0}", a.HealthFraction < 0.5f ? WarnColor : HudColor);
                Row("서브셀", $"{a.AliveSubs} / {Armor.SubCount}", a.AliveSubs < Armor.SubCount ? WarnColor : HudColor);
                Row("밀폐", a.sealsRoom ? "방을 막는다" : "안 막는다 (Vent)");
                Row("적열", $"{a.Heat:0.00}", a.Heat > 0.3f ? Palette.Heat : HudColor);
                Row("위치", where);
                break;

            default:
                if (thing is Thing th) { _inspectTitle = th.defName; Row("위치", where); }
                break;
        }
    }

    private static void FillWire(Inspect.WireSel sel)
    {
        Ship.WireSeg s = sel.ship.Segment(sel.wire, sel.seg);
        _inspectTitle = $"전선 {sel.wire + 1} · 구간 {sel.seg + 1}";
        _inspectTitleColor = Palette.Radiance;

        string state = s.state switch
        {
            Ship.WireState.Live => "급전",
            Ship.WireState.Dark => "살아 있음 · 0 V",
            _ => s.cause switch
            {
                Ship.CutCause.Burned => "끊김 · 과열로 탐",
                Ship.CutCause.Holed => "끊김 · 판이 없는 자리",
                Ship.CutCause.PlateGone => "끊김 · 판 소실",
                Ship.CutCause.PlateLeft => "끊김 · 판이 잔해로 떠남",
                Ship.CutCause.Breached => "끊김 · 판 관통",
                _ => "끊김",
            },
        };
        Row("상태", state, s.state == Ship.WireState.Live ? HudColor : s.state == Ship.WireState.Dark ? Palette.Steel : CriticalColor);
        Row("길이 · 저항", $"{(s.b - s.a).magnitude:0.0} m · {s.ohms * 1000f:0.0} mΩ");
        Row("전류", $"{s.amps:0.#} A");
        Row("온도", $"+{s.kelvin:0} K", s.kelvin > Ballistics.WireBurnKelvin * 0.5f ? Palette.Heat : HudColor);
        Row("지나는 판", s.subs == 0 ? "없음 (공기 - 못 끊긴다)" : $"서브셀 {s.subs}개");
    }

    private static void FillCrew(Ship ship, Crewman c)
    {
        int idx = ship != null ? ship.crewmen.IndexOf(c) : -1;
        _inspectTitle = $"승무원 {idx + 1}";
        _inspectTitleColor = c.alive ? Palette.Signal : CriticalColor;
        Row("상태", c.alive ? "생존" : "사망", c.alive ? HudColor : CriticalColor);

        Room room = ship != null ? ship.RoomOf(c) : null;
        Row("방 기압", room == null ? "방 없음" : $"{room.Pressure:0.00} atm",
            room == null || room.Pressure < Ballistics.CrewMinPressure ? CriticalColor : HudColor);
        if (ship != null) Row("위치", ship.SectionName((Vector2)c.anchor * ShipGrid.CellSize));
    }

    /// <summary>선택한 것의 테두리(TELEMETRY). 배율 밖 월드 좌표.</summary>
    private static void DrawSelection(Camera cam)
    {
        if (Inspect.Crew != null && Inspect.CrewShip != null)
        {
            Vector2 p = WorldToGui(cam, Inspect.CrewShip.transform.TransformPoint((Vector2)Inspect.Crew.anchor * ShipGrid.CellSize));
            Box(p - Vector2.one * 7f, p + Vector2.one * 7f);
            return;
        }

        if (Inspect.Wire is Inspect.WireSel w && w.ship != null)
        {
            Ship.WireSeg s = w.ship.Segment(w.wire, w.seg);
            DrawLine(WorldToGui(cam, w.ship.transform.TransformPoint(s.a)), WorldToGui(cam, w.ship.transform.TransformPoint(s.b)), Palette.Telemetry, 3f);
            return;
        }

        Component t = Inspect.Thing;
        if (t == null) return;

        Vector2 size = Vector2.one, offset = Vector2.zero;
        if (t.TryGetComponent(out BoxCollider2D col)) { size = col.size; offset = col.offset; }

        Transform tr = t.transform;
        Vector2 h = size * 0.5f;
        Vector2 c0 = WorldToGui(cam, tr.TransformPoint(offset + new Vector2(-h.x, -h.y)));
        Vector2 c1 = WorldToGui(cam, tr.TransformPoint(offset + new Vector2(h.x, -h.y)));
        Vector2 c2 = WorldToGui(cam, tr.TransformPoint(offset + new Vector2(h.x, h.y)));
        Vector2 c3 = WorldToGui(cam, tr.TransformPoint(offset + new Vector2(-h.x, h.y)));
        DrawLine(c0, c1, Palette.Telemetry, 2f); DrawLine(c1, c2, Palette.Telemetry, 2f);
        DrawLine(c2, c3, Palette.Telemetry, 2f); DrawLine(c3, c0, Palette.Telemetry, 2f);
    }

    private static void Box(Vector2 min, Vector2 max)
    {
        DrawLine(new Vector2(min.x, min.y), new Vector2(max.x, min.y), Palette.Telemetry, 2f);
        DrawLine(new Vector2(max.x, min.y), new Vector2(max.x, max.y), Palette.Telemetry, 2f);
        DrawLine(new Vector2(max.x, max.y), new Vector2(min.x, max.y), Palette.Telemetry, 2f);
        DrawLine(new Vector2(min.x, max.y), new Vector2(min.x, min.y), Palette.Telemetry, 2f);
    }

    /// <summary>Space 정지. 목표 줄 바로 밑, 소등과 무관하게 - 멈춘 것은 죽어가는 배에서도 알아야 한다.</summary>
    private static void DrawPausedBanner()
    {
        if (!Core.TickManager.UserPaused)
            return;

        EnsureStyles();

        var rect = new Rect(
            (GUIManager.LogicalWidth - ObjectiveWidth) * 0.5f,
            Margin + ObjectiveHeight + 6f,
            ObjectiveWidth,
            ObjectiveHeight);

        DrawRect(rect, PanelBg);
        DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), Palette.Telemetry);
        DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), Palette.Telemetry);
        GUI.contentColor = Palette.Telemetry;
        GUI.Label(rect, $"PAUSED  ·  {PauseControl.FocusLabel}  ·  TAB", _objectiveStyle);
        GUI.contentColor = Color.white;
    }


    // ------------------------------------------------------------
    // DEATH X-RAY
    // ------------------------------------------------------------

    /// <summary>암전이 시작되고 이만큼 뒤에 뜬다. GameManager.BlackFadeSeconds(1)와 같다 - 검정 위에 떠야 읽힌다.</summary>
    private const float XrayAfterBlackSeconds = 1f;
    private const float XrayCellMax = 14f;
    private const float XrayStepSeconds = 1.6f;    // 사건 하나 = 비행 1초 + 읽는 0.6초. 열 개면 16초
    private const float XrayFlightSeconds = 1f;    // 지금 사건의 탄이 날아오는 시간
    private const float XrayKillFadeSeconds = 0.9f; // 마지막 사건 뒤 배 전체가 붉어지는 시간

    private static readonly Dictionary<int, List<DeathXray.Trail>> _xrayChains = new();

    /// <summary>탄 하나의 궤적을 이어 붙인 점들. 부른 자리에서 바로 쓰고 버린다 - 자세한 이유는 BuildXrayPath.</summary>
    private static readonly List<Vector2> _xrayPath = new();

    /// <summary>탄종별 판정 집계. 매 프레임 다시 세므로 재사용한다 - 판정은 64발 상한이다.</summary>
    private static readonly List<(string shell, int pen, int ric, int stop, float mm)> _xrayLesson = new();

    /// <summary>이번 화면에 실제로 나온 것만. 화면 맨 아래 한 줄로 편다.</summary>
    private static readonly List<(Color colour, string text)> _xrayLegend = new();

    /// <summary>분석창에 띄울 탄. 마우스를 올리면 그것, 클릭하면 고정. 빈 값이면 창 자체가 없다.</summary>
    private static string _xrayHoverShell, _xrayPinnedShell;
    private static readonly HashSet<int> _xrayHitIds = new();

    /// <summary>
    /// 이 포를 어떻게 상대하는가. 맞아 본 탄의 관통력과 우리 면별 방호력을 견준다.
    ///
    /// 경사장갑의 유효 RHA는 `rha / cos(법선각)`이므로, 세워야 하는 각은 그 식을 뒤집은
    /// `acos(rha / pen)` 하나다. 60도가 상한인 이유는 그 너머가 실전에서 유지 안 되는 자세이고,
    /// `Ballistics.BaseCritAngle`(70도)에 닿기 전에 도탄이 먼저 먹기 때문이다.
    /// 조언 세 줄은 그 각도에서 **파생된다** - 분기마다 다른 규칙을 적지 않는다.
    /// </summary>
    private static void DrawXrayThreats(Rect gridArea, Matrix4x4 saved, float uiScale)
    {
        // **고른 탄만 띄운다.** 늘 떠 있으면 사건 현장을 가린다 - 이 창은 분석이지 상황이 아니다.
        string want = _xrayPinnedShell ?? _xrayHoverShell;

        if (want == null || DeathXray.ArmorBow <= 0f)
            return;

        int at = _xrayLesson.FindIndex(e => e.shell == want);

        if (at < 0 || _xrayLesson[at].mm <= 0f)
            return;

        (string shell, int _p, int _r, int _s, float mm) shot = _xrayLesson[at];

        // 함선 **옆**. 탄 궤적이 그 위를 지나므로 불투명 패널을 깔고 제일 마지막에 그린다 -
        // X-ray는 선이 화면을 가로지르는 그림이라, 배경 없는 글자는 반드시 한 번 묻힌다.
        float pad = RowHeight * 0.5f;
        float diagram = RowHeight * 3.4f;
        var panel = new Rect(gridArea.x, gridArea.y, gridArea.width * 0.42f,
                             RowHeight * 5.4f + diagram + pad * 2f);

        GUI.color = Palette.Bulkhead;
        GUI.DrawTexture(panel, Texture2D.whiteTexture);
        GUI.color = Palette.DeepSpace.WithAlpha(0.97f);
        GUI.DrawTexture(new Rect(panel.x + 1f, panel.y + 1f, panel.width - 2f, panel.height - 2f), Texture2D.whiteTexture);

        float x = panel.x + pad;
        float inner = panel.width - pad * 2f;
        float nameW = inner * 0.28f;
        float colW = inner * 0.24f;
        float y = panel.y + pad;

        Rect Col(int f) => new(x + nameW + colW * f, y, colW, RowHeight);

        GUI.color = Palette.Hull;
        GUI.Label(new Rect(x, y, inner, RowHeight), $"{shot.shell}을 어떻게 상대하나", _titleStyle);
        y += RowHeight;

        GUI.color = Palette.Breach;
        GUI.Label(new Rect(x, y, inner, RowHeight), $"관통력 {shot.mm:0} mm", _leftStyle);
        y += RowHeight * 1.2f;

        GUI.color = DimColor;
        string[] names = { "전면", "측면", "후면" };

        for (int f = 0; f < 3; f++)
            GUI.Label(Col(f), names[f], _leftStyle);

        y += RowHeight;

        float[] facings = { DeathXray.ArmorBow, DeathXray.ArmorSide, DeathXray.ArmorStern };

        GUI.color = Palette.Steel;
        GUI.Label(new Rect(x, y, nameW, RowHeight), "장갑 mm", _leftStyle);

        for (int f = 0; f < 3; f++)
            GUI.Label(Col(f), $"{facings[f]:0}", _leftStyle);

        y += RowHeight;

        GUI.color = Palette.Steel;
        GUI.Label(new Rect(x, y, nameW, RowHeight), "필요한 각", _leftStyle);

        int worstBand = -1;
        float showAngle = 0f;

        for (int f = 0; f < 3; f++)
        {
            int band = ThreatBand(shot.mm, facings[f], out string verdict);

            if (band > worstBand)
                worstBand = band;

            if (band is 1 or 2)
                showAngle = Mathf.Acos(Mathf.Clamp01(facings[f] / shot.mm)) * Mathf.Rad2Deg;

            GUI.color = BandColor(band);
            GUI.Label(Col(f), verdict, _leftStyle);
        }

        y += RowHeight * 1.1f;

        DrawAngleDiagram(new Rect(x, y, inner, diagram), showAngle, BandColor(worstBand), saved, uiScale);

        GUI.color = BandColor(worstBand);
        GUI.Label(new Rect(x, panel.yMax - pad - RowHeight, inner, RowHeight), worstBand switch
        {
            3 => $"{shot.shell}은 피하는 게 낫습니다.",
            2 => "각도를 줘서 도탄되게 하세요.",
            1 => "절대 수직을 내주지 마세요.",
            _ => "정면으로도 막힙니다.",
        }, _leftStyle);

        GUI.color = Color.white;
    }

    /// <summary>
    /// 각도가 무엇에서 재는 각인지 한 번만 그린다. 판을 세우고, 탄이 옆에서 들어오고, 판의
    /// 법선과 탄이 이루는 각이 그 숫자다 - "함선이 몇 도로 보인다"가 아니라 **입사각**이다.
    /// 숫자만 있으면 처음 보는 사람은 반드시 둘을 헷갈린다.
    /// </summary>
    private static void DrawAngleDiagram(Rect box, float angleDeg, Color tint, Matrix4x4 saved, float uiScale)
    {
        void Seg(Vector2 a, Vector2 b, Color c, float lw)
        {
            Matrix4x4 inside = GUI.matrix;
            GUI.matrix = saved;
            DrawLine(a * uiScale, b * uiScale, c, lw * uiScale);
            GUI.matrix = inside;
        }

        var hit = new Vector2(box.center.x + box.width * 0.12f, box.center.y);
        float half = box.height * 0.42f;
        float rad = angleDeg * Mathf.Deg2Rad;

        // 판. 각이 0이면 수직(= 탄을 정면으로 받는다), 커질수록 눕는다.
        var along = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        Seg(hit - along * half, hit + along * half, Palette.Hull, 3f);

        // 법선. 판에 수직인 선 - 각을 재는 기준이다.
        var normal = new Vector2(-Mathf.Cos(rad), Mathf.Sin(rad));
        Seg(hit, hit + normal * half * 0.9f, Palette.Steel.WithAlpha(0.7f), 1f);

        // 탄. 언제나 수평으로 들어온다 - 배를 돌리는 것이 판을 돌리는 것이라는 뜻이다.
        Seg(new Vector2(box.x + 4f, hit.y), hit, tint, 2f);

        GUI.color = tint;
        GUI.Label(new Rect(hit.x - box.width * 0.42f, hit.y - RowHeight, box.width * 0.4f, RowHeight),
                  angleDeg > 0.5f ? $"{angleDeg:0}°" : "", _rightStyle);
        GUI.color = DimColor;
        GUI.Label(new Rect(box.x, box.yMax - RowHeight, box.width, RowHeight), "탄 · 법선 · 판", _leftStyle);
        GUI.color = Color.white;
    }

    private static Color BandColor(int band) => band switch
    {
        3 => Palette.Breach,
        2 => Palette.Heat,
        1 => Palette.Radiance,
        _ => Palette.Signal,
    };

    /// <summary>
    /// 0 = 수직으로도 막는다, 1 = 조금만 세우면 막는다(30도 미만), 2 = 각도가 필요하다(60도까지),
    /// 3 = 60도로도 못 막는다. 경계는 전부 acos(rha / pen) 하나에서 나온다.
    /// </summary>
    private static int ThreatBand(float pen, float rha, out string verdict)
    {
        if (rha <= 0f || pen <= rha)
        {
            verdict = "막는다";
            return 0;
        }

        float need = Mathf.Acos(Mathf.Clamp01(rha / pen)) * Mathf.Rad2Deg;

        if (need >= 60f)
        {
            verdict = "뚫린다";
            return 3;
        }

        // 이 각도 이상으로 세워야 막는다. 숫자 자체가 조언이라 꾸밀 말이 필요 없다.
        verdict = $"{need:0}°";
        return need >= 30f ? 2 : 1;
    }

    /// <summary>Liang-Barsky. 선분을 사각형 안으로 자른다. 하나도 안 남으면 false.</summary>
    private static bool ClipToRect(ref Vector2 a, ref Vector2 b, Rect r)
    {
        float t0 = 0f, t1 = 1f;
        Vector2 d = b - a;
        float[] p = { -d.x, d.x, -d.y, d.y };
        float[] q = { a.x - r.xMin, r.xMax - a.x, a.y - r.yMin, r.yMax - a.y };

        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0f)
            {
                if (q[i] < 0f) return false;
                continue;
            }

            float t = q[i] / p[i];
            if (p[i] < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }

        Vector2 a0 = a;
        a = a0 + d * t0;
        b = a0 + d * t1;
        return true;
    }
    private const float XrayFlashSeconds = 0.25f;  // 사건이 켜지는 순간 흰빛
    private const float XrayPastTrailAlpha = 0.28f; // 지난 사건의 선. 지금 사건만 100%다
    private const float XraySpallSeconds = 0.22f;  // 관통 프레임에서 파편이 다 퍼지기까지
    private const float XrayBlastSeconds = 0.35f;  // 충격파 원이 다 퍼지기까지. 파편보다 느리게 - 뒤에 남는 것이 폭발이다
    private const int XrayBlastSegments = 28;      // 원 한 바퀴의 선분 수
    private const float XrayGaugeThickness = 4f;   // LogisticsScreen.GaugeThickness와 같다
    private static float _xrayReplayFrom;          // R로 되감은 시각(DownSeconds). 0이면 처음 그대로

    /// <summary>새 런. DeathXray.Reset이 부른다 - 안 지우면 다음 죽음의 재생이 지난 되감기 시각에서 시작한다.</summary>
    public static void XrayRewindReset() => _xrayReplayFrom = 0f;

    /// <summary>
    /// 마지막 열 사건을 **순서대로 재생한다.** 시간으로 칠하면 유폭 한 방이 판 40장을 같은 순간에
    /// 죽여서 전부 빨강이 되고, 그 직전의 관통 한 발이 묻힌다 - 그 한 발이 원인인데.
    /// 재생 중인 사건은 흰빛에서 빨강으로, 지난 사건은 주황, 열 개 밖의 옛 상처는 회색.
    /// 시타델은 언제나 표시 - 탄약고 옆이 먼저 뚫린 그림이 "왜 유폭했나"의 답이다.
    /// 다 돌면 그 자리에 멈춘다. Space를 3초 누르면 재시작이다.
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
        // R = 처음부터 다시. 재생 시계를 지금으로 되돌린다 - 열 사건 16초를 한 번 보고 끝이면 X-ray가 아니라 컷신이다.
        if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            _xrayReplayFrom = GameManager.DownSeconds;

        float t = GameManager.DownSeconds - Mathf.Max(_xrayReplayFrom, ShutdownSeconds + DieFlashSeconds + XrayAfterBlackSeconds);
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

        // 마지막 사건까지 다 보여준 뒤의 한 박자. 사건이 5-4-3-2-1로 지나가고 0에서 배가
        // 통째로 붉어진다 - 마지막 피격과 격파 사이가 비어 있으면 재생이 그냥 멎은 것으로 읽힌다.
        float kill = groups.Count > 0
            ? Mathf.Clamp01((t - groups.Count * XrayStepSeconds) / XrayKillFadeSeconds)
            : 0f;

        Color CellColor(Vector2Int c) => Color.Lerp(CellColorAt(c), Palette.Breach, kill);

        Color CellColorAt(Vector2Int c)
        {
            bool citadel = DeathXray.Citadel.Contains(c);

            if (when.TryGetValue(c, out int g))
            {
                Color color;
                if (g < 0) color = Palette.Steel;                           // 열 개 밖. 옛 상처
                else if (g >= shown) color = Palette.Hull.WithAlpha(0.28f);  // 아직 안 온 사건. 멀쩡한 척
                else if (g == shown - 1) color = current;                   // 지금 이 사건
                else color = Palette.Heat;                                  // 지난 사건

                return citadel && g >= 0 && g < shown ? Color.Lerp(color, Palette.Radiance, 0.6f) : color;
            }

            return citadel ? Palette.Heat.WithAlpha(0.9f) : Palette.Hull.WithAlpha(0.28f);
        }

        // 칸. 격자의 자리다 - 판이 어디 **앉아** 있나. 흐리게.
        for (int col = 0; col < design.width; col++)
        for (int row = 0; row < design.height; row++)
        {
            if (!ShipGrid.Solid(design.cells[col, row]))
                continue;

            GUI.color = CellColor(new Vector2Int(col, row)).WithAlpha(0.35f);
            GUI.DrawTexture(new Rect(x0 + col * cell, y0 + row * cell, cell - gap, cell - gap), Texture2D.whiteTexture);
        }

        // 콜라이더 윤곽. 탄이 **실제로 맞는** 모양이다 - 2×2·경사판이 칸 밖으로 나온 만큼 여기서 보인다.
        // 칸만 그리면 탄이 허공에서 멈추고 파편이 선체 밖에서 터지는 그림이 된다(2026-09-13).
        foreach ((Vector2Int c, Vector2[] poly) in DeathXray.Footprints)
        {
            Color oc = CellColor(c);

            for (int i = 0; i < poly.Length; i++)
                Line(ToScreen(poly[i]), ToScreen(poly[(i + 1) % poly.Length]), oc, 1f);
        }

        GUI.color = Color.white;

        // 탄과 파편. 지난 사건의 것은 그대로, **지금 사건의 탄은 날아온다** - 1초 동안 궤적을 따라
        // 머리가 가고, 닿는 순간 판정(관통·저지·도탄)이 찍히고 그제야 파편이 퍼진다. 정지 선분은
        // 어디서 왔는지가 안 읽혔다("이상한 위치에서 날아온다"). 도탄은 튕겨 나가는 구간까지
        // 같은 궤적이라 저절로 이어진다.
        long earliest = groups.Count > 0 ? groups[0].tick - 90 : DeathXray.DownTick - 90;
        long nowFrom = shown > 0 ? groups[shown - 1].tick - 90 : long.MaxValue;
        long nowTo = shown > 0 ? groups[shown - 1].tick + 3 : long.MinValue;
        long pastTo = shown > 1 ? groups[shown - 2].tick + 3 : long.MinValue;
        if (groups.Count == 0) { pastTo = DeathXray.DownTick; }

        float flight = Mathf.Clamp01(inStep / XrayFlightSeconds);   // 지금 사건의 탄이 얼마나 날아왔나
        bool landed = flight >= 1f;

        Vector2 ToScreen(Vector2 g) => new(x0 + (g.x + 0.5f) * cell, y0 + (g.y + 0.5f) * cell);

        // 선은 그림 영역 안에서만. 탄 궤적이 45칸 밖에서 오므로 안 자르면 글 위를 가로지른다.
        //
        // **배율 밖에서 그린다.** DrawLine의 RotateAroundPivot은 GUI.matrix에 배율이 걸려 있으면 피벗을
        // 논리 좌표로 받아 화면 좌표에서 돌린다 - 피벗에서 먼 선일수록 밀려서 화면 아래쪽 선이 8칸씩
        // 떠 있었다. 점(DrawTexture)은 맞고 선만 틀렸던 이유. 조준선을 배율 밖에서 그리는 것과 같다.
        void Line(Vector2 a, Vector2 b, Color c, float lw)
        {
            if (!ClipToRect(ref a, ref b, gridArea))
                return;

            Matrix4x4 inside = GUI.matrix;
            GUI.matrix = saved;
            DrawLine(a * uiScale, b * uiScale, c, lw * uiScale);
            GUI.matrix = inside;
        }

        // 파편도 맞은 자리가 있다. 선만 그리면 어디서 멈췄는지가 굵기 1px 끝점에 숨는다 -
        // 탄의 판정 점과 같은 자리를 파편에도 준다. 새 자료가 필요 없다: 선의 끝(tr.b)이 곧
        // SpallResolver가 판을 때린 hit.point다.
        void Dot(Vector2 p, Color c, float d)
        {
            if (!gridArea.Contains(p))
                return;

            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - d * 0.5f, p.y - d * 0.5f, d, d), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        float spallDot = Mathf.Max(3f, cell * 0.35f);

        // 충격파. 반경은 세기에서 나온다(Ballistics.BlastRadiusFor) - 작약 90은 한 칸,
        // 탄약고 3,200은 다섯 칸. 시뮬이 실제로 때리는 원과 같은 식이라 그림이 판정을 안 속인다.

        void Ring(Vector2 centre, float radius, Color c, float lw)
        {
            Vector2 prev = ToScreen(centre + new Vector2(radius, 0f));

            for (int i = 1; i <= XrayBlastSegments; i++)
            {
                float a = i * 2f * Mathf.PI / XrayBlastSegments;
                Vector2 next = ToScreen(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
                Line(prev, next, c, lw);
                prev = next;
            }
        }

        // 범례는 화면에 실제로 나온 것만 설명한다. 아홉 줄을 늘 띄우면 처음 보는 사람이
        // 범례를 읽다가 사건을 놓친다.
        bool sawShell = false, sawArmor = false, sawModule = false, sawOld = false, sawFrag = false, sawBlast = false;
        bool sawPen = false, sawRic = false, sawStop = false, sawRam = false;

        foreach (DeathXray.Lost l in DeathXray.LostPlates)
            if (when.TryGetValue(l.cell, out int wg) && wg < 0) { sawOld = true; break; }

        Color TrailColor(SpallTrails.Kind k, bool frag, out float lw)
        {
            // 파편이 된 탄은 가늘고 차갑게. 주포 한 발과 같은 굵기로 그리면 유폭 한 번에
            // 굵은 선 수십 개가 깔려서 원인이 된 그 한 발이 묻힌다.
            if (frag) { lw = 1f; sawFrag = true; return Palette.Steel; }

            switch (k)
            {
                case SpallTrails.Kind.Shell: lw = 2f; sawShell = true; return Palette.Hull;
                case SpallTrails.Kind.Module: lw = 1f; sawModule = true; return Palette.Radiance;
                case SpallTrails.Kind.Armor: lw = 1f; sawArmor = true; return Palette.Breach.WithAlpha(0.8f);
                default: lw = 1f; return Palette.Steel.WithAlpha(0.5f);
            }
        }

        // 맞힌 탄만 그린다. 45칸 안을 스쳐 간 빗나간 탄은 굵은 선으로 화면을 가로질러 "저게 왜"가 됐다.
        _xrayHitIds.Clear();
        foreach (DeathXray.Hit hit in DeathXray.Hits)
            if (!hit.ram) _xrayHitIds.Add(hit.id);

        // 1) 지난 사건의 파편. **탄은 여기서 안 그린다** - 구간을 이어 붙여야 하므로(아래 체인)
        // 한 자리에서만 그린다. 두 자리로 두면 사건이 "지금"에서 "지난"으로 넘어가는 순간
        // 이어 붙인 선이 날것 구간으로 바뀌어 궤적이 통째로 순간이동한다.
        DeathXray.ForEachTrail(tr =>
        {
            if (tr.kind == SpallTrails.Kind.Shell || tr.tick < earliest || tr.tick > pastTo)
                return;

            // 지난 사건의 선은 흐리게. 굵은 흰 탄 궤적이 전부 100%면 제일 먼저 보이는 것이
            // "함선이 뚫렸다"가 아니라 백색 레이저빔 다발이 된다.
            Color lc = TrailColor(tr.kind, tr.frag, out float lw);
            Color dim = lc.WithAlpha(lc.a * XrayPastTrailAlpha);
            Line(ToScreen(tr.a), ToScreen(tr.b), dim, lw);

            if (tr.kind == SpallTrails.Kind.Armor || tr.kind == SpallTrails.Kind.Module)
                Dot(ToScreen(tr.b), dim, spallDot);
        });

        // 2) 탄: 지난 것까지 전부 id별로 모은다. 지금 사건의 것만 flight만큼 그리고 나머지는 흐리게.
        _xrayChains.Clear();

        DeathXray.ForEachTrail(tr =>
        {
            if (tr.kind != SpallTrails.Kind.Shell || tr.tick < earliest || tr.tick > nowTo)
                return;

            if (!_xrayHitIds.Contains(tr.id))
                return;

            if (!_xrayChains.TryGetValue(tr.id, out List<DeathXray.Trail> chain))
                _xrayChains[tr.id] = chain = new List<DeathXray.Trail>();

            chain.Add(tr);   // ForEachTrail이 오래된 것부터 주므로 이미 틱 순이다
        });

        // 틱마다 배가 움직이므로 구간마다 좌표계가 다르다. 특히 **관통한 그 틱에 탄이 배를
        // 밀어서**(ImpactImpulse) 다음 구간부터 몇 m씩 밀린 자리에 찍힌다 - 증상이 "잘 날아가다
        // 갑자기 순간이동해서 같은 방향으로 계속"이다. 방향과 길이는 맞고 원점만 어긋난 것이라,
        // 마지막 구간(명중점이 있는 자리)을 붙잡고 거꾸로 이어 붙인다. 배에 탄 사람이 보는
        // 상대 운동이 그것이고, X-ray가 답해야 하는 질문도 "어디서 왔나"지 우주 절대 좌표가 아니다.
        List<Vector2> BuildXrayPath(List<DeathXray.Trail> chain)
        {
            List<Vector2> path = _xrayPath;
            path.Clear();

            for (int i = 0; i <= chain.Count; i++)
                path.Add(Vector2.zero);

            path[chain.Count] = chain[chain.Count - 1].b;

            for (int i = chain.Count - 1; i >= 0; i--)
                path[i] = path[i + 1] - (chain[i].b - chain[i].a);

            return path;
        }

        // 판정이 궤적의 **어디**인가. 탄은 뚫고도 계속 날아가므로 명중점은 궤적의 끝이 아니다 -
        // 비행 전체가 끝난 뒤(flight == 1)에 파편을 내면 관통과 파편 사이가 비어서 두 사건으로
        // 읽힌다. 워썬더 X-ray가 한 프레임에 붙여 보여주는 것이 정확히 이 지점이다.
        float ChainProgressAt(List<Vector2> path, Vector2 at)
        {
            float total = 0f, run = 0f, best = float.MaxValue, bestRun = 0f;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                Vector2 d = path[i + 1] - path[i];
                float len = d.magnitude;
                float t = len > 1e-4f ? Mathf.Clamp01(Vector2.Dot(at - path[i], d) / (len * len)) : 0f;
                float dist = (Vector2.LerpUnclamped(path[i], path[i + 1], t) - at).sqrMagnitude;

                if (dist < best) { best = dist; bestRun = run + len * t; }

                run += len;
                total = run;
            }

            return total > 1e-4f ? bestRun / total : 1f;
        }

        // 지금 사건에서 제일 먼저 닿는 판정. 파편은 그 프레임부터 퍼진다.
        float burst = 1f;

        foreach (DeathXray.Hit hit in DeathXray.Hits)
        {
            if (hit.ram || hit.tick <= pastTo || hit.tick < nowFrom || hit.tick > nowTo)
                continue;

            if (_xrayChains.TryGetValue(hit.id, out List<DeathXray.Trail> c))
                burst = Mathf.Min(burst, ChainProgressAt(BuildXrayPath(c), hit.at));
        }

        // 파편은 그 자리에서 **퍼져 나간다.** 다 그려 놓고 켜면 관통은 순간이고 파편은 이미
        // 끝나 있는 그림이라, 인과가 시간으로 안 읽힌다.
        float spall01 = Mathf.Clamp01((inStep - burst * XrayFlightSeconds) / XraySpallSeconds);

        if (spall01 > 0f)
            DeathXray.ForEachTrail(tr =>
            {
                if (tr.kind == SpallTrails.Kind.Shell || tr.tick < nowFrom || tr.tick > nowTo || tr.tick <= pastTo)
                    return;

                Color lc = TrailColor(tr.kind, false, out float lw);
                Line(ToScreen(tr.a), ToScreen(Vector2.Lerp(tr.a, tr.b, spall01)), lc, lw);

                if (spall01 >= 1f && (tr.kind == SpallTrails.Kind.Armor || tr.kind == SpallTrails.Kind.Module))
                    Dot(ToScreen(tr.b), lc, spallDot);
            });

        foreach (KeyValuePair<int, List<DeathXray.Trail>> entry in _xrayChains)
        {
            List<DeathXray.Trail> chain = entry.Value;
            List<Vector2> path = BuildXrayPath(chain);
            Color shellColor = TrailColor(SpallTrails.Kind.Shell, chain[0].frag, out float shellWidth);

            // 지금 사건의 탄인가. 마지막 구간이 이번 사건 창 안이면 날아오는 중이고,
            // 그 앞의 것은 이미 도착한 선이다.
            long last = chain[chain.Count - 1].tick;
            bool nowShell = last > pastTo && last >= nowFrom && last <= nowTo;

            if (!nowShell)
                shellColor = shellColor.WithAlpha(shellColor.a * XrayPastTrailAlpha);

            float total = 0f;

            for (int i = 0; i + 1 < path.Count; i++) total += (path[i + 1] - path[i]).magnitude;

            float budget = total * (nowShell ? flight : 1f);
            Vector2 head = ToScreen(path[0]);

            for (int i = 0; i + 1 < path.Count; i++)
            {
                float len = (path[i + 1] - path[i]).magnitude;

                if (budget <= 0f)
                    break;

                float f = Mathf.Clamp01(budget / Mathf.Max(1e-4f, len));
                Vector2 b = Vector2.Lerp(path[i], path[i + 1], f);
                Line(ToScreen(path[i]), ToScreen(b), shellColor, shellWidth);
                head = ToScreen(b);
                budget -= len;
            }

            // 머리. 궤적이 다 그려질 때까지 - 도중의 판정 점은 지나쳐 간다.
            if (nowShell && !landed && gridArea.Contains(head))
            {
                float d = chain[0].frag ? Mathf.Max(3f, cell * 0.35f) : Mathf.Max(6f, cell * 0.7f);
                GUI.color = chain[0].frag ? Palette.Steel : Color.white;
                GUI.DrawTexture(new Rect(head.x - d * 0.5f, head.y - d * 0.5f, d, d), Texture2D.whiteTexture);
            }
        }

        // 3) 판정 점. 지난 사건은 그대로, 지금 사건은 **탄의 머리가 그 자리를 지나는 프레임에**
        // 부풀며 찍힌다. 충각은 궤적이 없으므로 비행이 끝날 때.
        foreach (DeathXray.Hit hit in DeathXray.Hits)
        {
            bool past = hit.tick >= earliest && hit.tick <= pastTo;
            bool now = hit.tick > pastTo && hit.tick >= nowFrom && hit.tick <= nowTo;

            float at = !now ? 0f
                     : hit.ram || !_xrayChains.TryGetValue(hit.id, out List<DeathXray.Trail> hitChain) ? 1f
                     : ChainProgressAt(BuildXrayPath(hitChain), hit.at);

            if (!past && !(now && flight >= at))
                continue;

            float pulse = 1f + 1.5f * Mathf.Clamp01(1f - (inStep - at * XrayFlightSeconds) / 0.3f);

            if (hit.ram) sawRam = true;
            else if (hit.outcome == HitOutcome.Penetrated) sawPen = true;
            else if (hit.outcome == HitOutcome.Ricochet) sawRic = true;
            else sawStop = true;

            Color hc = hit.ram ? Palette.Heat : hit.outcome switch
            {
                HitOutcome.Penetrated => Palette.Breach,
                HitOutcome.Ricochet => Palette.Radiance,
                _ => Palette.Steel,
            };

            Vector2 p = ToScreen(hit.at);

            if (!gridArea.Contains(p))
                continue;

            // 실체 파편의 판정은 작게. 그 탄은 굵은 흰 궤적이 아니라 가는 회색 선으로 오므로,
            // 점만 주포와 같은 크기면 "탄이 안 보이는 피탄"으로 읽힌다.
            float d = (hit.ram ? Mathf.Max(7f, cell * 0.9f) : Mathf.Max(hit.frag ? 3f : 5f, cell * (hit.frag ? 0.35f : 0.6f))) * (now ? pulse : 1f);
            GUI.color = hc;
            GUI.DrawTexture(new Rect(p.x - d * 0.5f, p.y - d * 0.5f, d, d), Texture2D.whiteTexture);
        }

        // 4) 폭발. 작약·유폭·충각 유폭이 전부 RamImpact.Detonate 한 문으로 가므로 종류를 안 가른다.
        // 지금 사건의 것은 파편과 같은 순간(burst)에 시작해 퍼지고, 지난 사건의 것은 다 퍼진 원으로 남는다.
        foreach (DeathXray.Blast blast in DeathXray.Blasts)
        {
            bool past = blast.tick >= earliest && blast.tick <= pastTo;
            bool now = blast.tick > pastTo && blast.tick >= nowFrom && blast.tick <= nowTo;

            if (!past && !now)
                continue;

            float grow = now ? Mathf.Clamp01((inStep - burst * XrayFlightSeconds) / XrayBlastSeconds) : 1f;

            if (grow <= 0f)
                continue;

            sawBlast = true;

            // 퍼질수록 옅어진다 - 지금 막 터진 것이 제일 밝다.
            float radiusCells = Ballistics.BlastRadiusFor(blast.damage) / ShipGrid.CellSize;

            if (radiusCells <= 0f)
                continue;

            Color bc = Palette.Radiance.WithAlpha((past ? XrayPastTrailAlpha : 1f) * Mathf.Lerp(1f, 0.35f, grow));
            Ring(blast.at, radiusCells * grow, bc, blast.damage >= 1000f ? 2f : 1f);
        }

        GUI.color = Color.white;

        DrawXrayThreats(gridArea, saved, uiScale);

        // 오른쪽. 원인, 그 아래 사건 열 줄 - 켜진 것까지만 밝다.
        float y = textArea.y;
        GUI.Label(new Rect(textArea.x, y, textArea.width, 20f), "격파", _titleStyle);
        y += 24f;
        GUI.color = Palette.Breach;
        GUI.Label(new Rect(textArea.x, y, textArea.width, 30f), DeathXray.Cause, _objectiveStyle);
        GUI.color = Color.white;
        y += 40f;

        GUI.Label(new Rect(textArea.x, y, textArea.width, RowHeight), groups.Count == 1 ? "마지막 사건" : $"마지막 {groups.Count}개 사건", _titleStyle);
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
            string line = string.IsNullOrEmpty(group.shell) ? $"{tag}  판 {group.cells.Count}장"
                                                            : $"{group.shell} — 판 {group.cells.Count}장 {tag}";

            GUI.Label(new Rect(textArea.x, y, textArea.width * 0.7f, RowHeight), line, _leftStyle);
            GUI.color = lit ? DimColor : Palette.Hull.WithAlpha(0.15f);
            // 죽기 0.04초 전은 죽는 순간이다. "-0.0 s"는 반올림이 만든 마이너스 0초다.
            GUI.Label(new Rect(textArea.x + textArea.width * 0.7f, y, textArea.width * 0.3f, RowHeight),
                      ago < 0.05f ? "격파" : $"-{ago:0.0} s", _rightStyle);
            y += RowHeight;
        }

        GUI.color = Color.white;
        y += RowHeight;

        // 학습. **결론을 안 쓴다** - 이 배가 이번 전투에서 받은 판정을 탄종별로 세어서 올린다.
        // "155mm에는 뚫리고 20mm는 막는다"는 문장은 숫자가 이미 말하고, 지어낸 교훈 한 줄보다
        // 다음 판에 쓸 수 있는 것이 그쪽이다. 워썬더 킬 카메라가 데미지 모델을 가르치는 방식이
        // 설명이 아니라 그림인 것과 같은 이유다.
        _xrayLesson.Clear();
        int fragHits = 0, ramHits = 0;

        foreach (DeathXray.Hit hit in DeathXray.Hits)
        {
            if (hit.ram) { ramHits++; continue; }
            if (hit.frag) { fragHits++; continue; }

            string name = string.IsNullOrEmpty(hit.shell) ? "?" : hit.shell;
            int at = _xrayLesson.FindIndex(e => e.shell == name);

            if (at < 0)
            {
                _xrayLesson.Add((name, 0, 0, 0, 0f));
                at = _xrayLesson.Count - 1;
            }

            (string shell, int pen, int ric, int stop, float mm) e2 = _xrayLesson[at];

            if (hit.outcome == HitOutcome.Penetrated) e2.pen++;
            else if (hit.outcome == HitOutcome.Ricochet) e2.ric++;
            else e2.stop++;

            // 같은 탄종이라도 속도가 달라 관통력이 다르다. 제일 셌던 한 발로 재는 것이
            // 조언의 기준이다 - 평균으로 재면 "가끔 뚫리는 포"가 안전해 보인다.
            e2.mm = Mathf.Max(e2.mm, hit.pen);
            _xrayLesson[at] = e2;
        }

        if (_xrayLesson.Count > 0 || fragHits > 0)
        {
            _xrayLesson.Sort((a, b) => (b.pen + b.ric + b.stop).CompareTo(a.pen + a.ric + a.stop));

            GUI.Label(new Rect(textArea.x, y, textArea.width, RowHeight), "받은 탄", _titleStyle);
            GUI.color = DimColor;
            GUI.Label(new Rect(textArea.x, y, textArea.width, RowHeight),
                      _xrayPinnedShell != null ? "클릭 해제" : "마우스를 올리면 분석", _rightStyle);
            GUI.color = Color.white;
            y += RowHeight;

            // GUIManager.MousePos가 이미 UiScale로 나눈 논리 좌표다. 여기서 또 나누면
            // 1.31^2만큼 왼쪽 위로 어긋나 어느 줄에도 안 걸린다(증상: 마우스를 올려도 분석이 안 뜬다).
            Vector2 mouse = GUIManager.MousePos;
            _xrayHoverShell = null;

            for (int i = 0; i < _xrayLesson.Count && i < 5; i++)
            {
                (string shell, int pen, int ric, int stop, float mm) e = _xrayLesson[i];
                int total = e.pen + e.ric + e.stop;
                var row = new Rect(textArea.x, y, textArea.width, RowHeight);
                bool over = row.Contains(mouse);

                if (over)
                {
                    _xrayHoverShell = e.shell;

                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                        _xrayPinnedShell = _xrayPinnedShell == e.shell ? null : e.shell;
                }

                if (over || _xrayPinnedShell == e.shell)
                {
                    GUI.color = Palette.Bulkhead.WithAlpha(0.7f);
                    GUI.DrawTexture(row, Texture2D.whiteTexture);
                }

                GUI.color = e.pen > 0 ? Palette.Hull : DimColor;
                GUI.Label(new Rect(textArea.x, y, textArea.width * 0.5f, RowHeight), e.shell, _leftStyle);

                // 관통 / 전체. 도탄이 있었으면 그것만 따로 - 각도가 값을 한 증거다.
                GUI.color = e.pen > 0 ? Palette.Breach : Palette.Steel;
                GUI.Label(new Rect(textArea.x + textArea.width * 0.5f, y, textArea.width * 0.5f, RowHeight),
                          e.ric > 0 ? $"관통 {e.pen}/{total}  도탄 {e.ric}" : $"관통 {e.pen}/{total}", _rightStyle);
                y += RowHeight;
            }

            if (fragHits > 0 || ramHits > 0)
            {
                GUI.color = DimColor;
                GUI.Label(new Rect(textArea.x, y, textArea.width, RowHeight),
                          ramHits > 0 ? $"파편 {fragHits}발 · 충각 {ramHits}회" : $"파편 {fragHits}발", _leftStyle);
                y += RowHeight;
            }

            GUI.color = Color.white;
            y += RowHeight;
        }

        // 범례는 화면 맨 아래 한 줄로 간다. 사건의 색(지금·지난·옛 상처)은 안 넣는다 -
        // 오른쪽 사건 목록이 같은 색으로 같은 것을 이미 말하고 있어서, 두 번 적으면
        // 처음 보는 사람이 범례부터 읽다가 재생을 놓친다.
        _xrayLegend.Clear();
        if (DeathXray.Citadel.Count > 0) _xrayLegend.Add((Palette.Radiance, "시타델"));
        if (sawShell) _xrayLegend.Add((Palette.Hull, "탄"));
        if (sawFrag) _xrayLegend.Add((Palette.Steel, "파편 탄"));
        if (sawArmor) _xrayLegend.Add((Palette.Breach.WithAlpha(0.8f), "파편이 때린 판"));
        if (sawModule) _xrayLegend.Add((Palette.Radiance, "파편이 때린 모듈"));
        if (sawPen) _xrayLegend.Add((Palette.Breach, "관통"));
        if (sawRic) _xrayLegend.Add((Palette.Radiance, "도탄"));
        if (sawStop) _xrayLegend.Add((Palette.Steel, "막힘"));
        if (sawBlast) _xrayLegend.Add((Palette.Radiance, "폭발"));
        if (sawRam) _xrayLegend.Add((Palette.Heat, "충각"));

        if (_xrayLegend.Count > 0)
        {
            float slot = w / _xrayLegend.Count;
            float ly = h - RowHeight * 1.6f;

            for (int i = 0; i < _xrayLegend.Count; i++)
            {
                (Color c, string text) item = _xrayLegend[i];
                float lx = slot * i + RowHeight * 0.4f;

                GUI.color = item.c;
                GUI.DrawTexture(new Rect(lx, ly + 5f, 12f, 12f), Texture2D.whiteTexture);
                GUI.color = Palette.Hull;
                GUI.Label(new Rect(lx + 18f, ly, slot - 18f, RowHeight), item.text, _leftStyle);
            }
        }

        GUI.color = DimColor;
        GUI.Label(new Rect(textArea.x, textArea.yMax - RowHeight, textArea.width, RowHeight), "R  다시 보기      Space 길게  -  재시작", _leftStyle);

        // 재시작 게이지. 도착 카드(LogisticsScreen.ShowCard)와 같은 그림 - 위는 왼쪽에서, 아래는 오른쪽에서.
        // 누르는 동안만 보인다. 떼면 0으로 돌아가니 그림도 사라진다.
        float hold = GameManager.RestartHold01;

        if (hold > 0f)
        {
            float fill = w * hold;
            GUI.color = Palette.Telemetry;
            GUI.DrawTexture(new Rect(0f, 0f, fill, XrayGaugeThickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(w - fill, h - XrayGaugeThickness, fill, XrayGaugeThickness), Texture2D.whiteTexture);
        }

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
    // SCUTTLE
    // ------------------------------------------------------------

    private const float ScuttleWidth = 360f;
    private const float ScuttleHeight = 56f;
    private const float ScuttleTop = 0.30f;      // 논리 높이 비율
    private const float ScuttleBarHeight = 4f;
    private const float ScuttleBlinkPeriod = 0.32f;

    /// <summary>
    /// 자침 카운트다운. 대사는 누른 순간 한 번뿐이라 남은 시간을 말할 수 없다 - 되돌릴 수
    /// 있는 3초를 되돌릴 수 있게 보이려면 매 프레임 줄어드는 값이 필요하다.
    ///
    /// 시계는 <see cref="Ship.ScuttleHeldSeconds"/> 하나다. 여기서 따로 세면 손 뗀 프레임과
    /// 어긋나서 배는 안 터졌는데 계기만 0을 찍는다.
    /// </summary>
    private static void DrawScuttleWarning(Ship ship)
    {
        float held = ship.ScuttleHeldSeconds;

        if (held <= 0f)
            return;

        float left = Mathf.Max(0f, Ship.Action_SelfDestructTime - held);

        var box = new Rect(
            (GUIManager.LogicalWidth - ScuttleWidth) * 0.5f,
            GUIManager.LogicalHeight * ScuttleTop,
            ScuttleWidth,
            ScuttleHeight);

        DrawRect(box, PanelBg);

        // 테두리만 깜빡인다. 글자를 깜빡이면 남은 시간을 읽는 동안 사라진다.
        bool lit = Mathf.Repeat(Time.unscaledTime, ScuttleBlinkPeriod) < ScuttleBlinkPeriod * 0.5f;

        if (lit)
        {
            DrawRect(new Rect(box.x, box.y, box.width, 1f), CriticalColor);
            DrawRect(new Rect(box.x, box.yMax - 1f, box.width, 1f), CriticalColor);
            DrawRect(new Rect(box.x, box.y, 1f, box.height), CriticalColor);
            DrawRect(new Rect(box.xMax - 1f, box.y, 1f, box.height), CriticalColor);
        }

        DrawText(
            new Rect(box.x, box.y + 6f, box.width, 22f),
            "자침 절차 진행 중",
            CriticalColor,
            _objectiveStyle);

        DrawText(
            new Rect(box.x, box.y + 26f, box.width, 20f),
            $"T-{left:0.0}s   J를 놓으면 중단",
            WarnColor,
            _objectiveStyle);

        // 차오르는 막대. 끝까지 차면 터진다 - 숫자와 같은 사실의 두 번째 표현이다.
        float fill = Mathf.Clamp01(held / Ship.Action_SelfDestructTime);

        DrawRect(
            new Rect(box.x, box.yMax - ScuttleBarHeight, box.width * fill, ScuttleBarHeight),
            CriticalColor);
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
            FlightHeight + (ship.HasWiring ? RowHeight : 0f)   // 전선 있는 배는 GRID 줄이 하나 더
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
            // 전선 있는 배는 버스 전압을 숫자로. 정격의 절반 밑이면 붉게, 사이는 경고색.
            float bus = ship.BusVoltage;
            DrawText(new Rect(x + width * 0.5f, y, width * 0.25f, RowHeight),
                ship.HasWiring ? $"{bus:0}V" : "PWR",
                !ship.HasPower || bus < Ballistics.PowerNominal * Ballistics.PowerBrownout ? CriticalColor
                    : bus < Ballistics.PowerNominal * 0.9f ? WarnColor : HudColor, _rightStyle);
            // 승무원 모델이 없는 배(방 없음)는 숫자가 없다 - 죽을 수 없으니 라벨만.
            int total = ship.crewmen.Count;
            int alive = ship.AliveCrew;
            DrawText(new Rect(x + width * 0.75f, y, width * 0.25f, RowHeight),
                total == 0 ? "CREW" : $"CREW {alive}/{total}",
                alive == total ? HudColor : alive > 0 ? WarnColor : CriticalColor, _rightStyle);
            y += RowHeight;

            // 전력망. 전선 있는 배만. 끊긴 구간이 있으면 WIRE가, 못 먹는 포가 있으면 FED가 색을 바꾼다 -
            // "포탑이 왜 안 도나"의 답이 두 숫자 사이에 있다. 전압은 SYS 줄에 이미 있다.
            if (ship.HasWiring)
            {
                int wAlive = ship.WireSegmentsAlive, wTotal = ship.WireSegmentsTotal;
                int fed = ship.GunsFed, guns = ship.GunsTotal;

                DrawText(new Rect(x, y, width * 0.5f, RowHeight), $"GRID {ship.GridAmps:0}A", DimColor, _leftStyle);
                DrawText(new Rect(x + width * 0.5f, y, width * 0.25f, RowHeight), $"WIRE {wAlive}/{wTotal}",
                    wAlive == wTotal ? HudColor : wAlive > 0 ? WarnColor : CriticalColor, _rightStyle);
                DrawText(new Rect(x + width * 0.75f, y, width * 0.25f, RowHeight), $"FED {fed}/{guns}",
                    fed == guns ? HudColor : fed > 0 ? WarnColor : CriticalColor, _rightStyle);
                y += RowHeight;
            }
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
        // 칸 수는 만 단위라 화면에 쓸 숫자가 아니다. 쏠 수 있는 시간의 비율이 읽을 값이다.
        string head = maxRounds > 0 ? $"{guns} · {100f * rounds / maxRounds:0}%" : (guns > 0 ? $"{guns}" : null);
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
        Gun.HoldReason.NoPower => "NO POWER",
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


    private static readonly List<Ship.DeviceReadout> _devices = new();
    private static readonly List<Ship.WireSeg> _segments = new();
    private static GUIStyle _readoutStyle;

    /// <summary>속보기 라벨. 뒤판을 깔아야 회로도 선 위에서도 읽힌다 - 목업에서 고른 값(15 px, 뒤판 알파 1).</summary>
    private static void Readout(Vector2 at, string text, Color color)
    {
        _readoutStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = Ballistics.ReadoutFontSize,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow,
        };

        Vector2 size = _readoutStyle.CalcSize(new GUIContent(text));
        var rect = new Rect(at.x + 6f, at.y - size.y * 0.5f, size.x + 8f, size.y);

        if (Ballistics.ReadoutBackAlpha > 0f)
        {
            GUI.color = Palette.Void.WithAlpha(Ballistics.ReadoutBackAlpha);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        GUI.contentColor = color;
        GUI.Label(new Rect(rect.x + 4f, rect.y, size.x, size.y), text, _readoutStyle);
        GUI.contentColor = Color.white;
    }

    /// <summary>
    /// 속보기(Tab) 라벨. 배율 행렬 밖(월드 좌표)이라 조준 정보와 같은 자리에서 그린다.
    /// 기기: 전원은 `320V 62A`(RADIANCE), 부하는 `318V 6A`(급전이면 본문색, 아니면 BREACH).
    /// 전선: 1 K 넘게 데워진 구간만 온도, 탄 구간은 BURNT, 구멍은 HOLE.
    /// 승무원: 살아 있으면 SIGNAL 점, 죽었으면 BREACH 점. 자리는 칸 중심(anchor).
    /// </summary>
    private static void DrawInteriorReadouts(Ship ship, Camera cam)
    {
        EnsureStyles();
        PauseControl.Layer focus = PauseControl.Focus;

        if (ship.HasWiring && focus != PauseControl.Layer.Air)
        {
            ship.CollectDeviceReadouts(_devices);

            for (int i = 0; i < _devices.Count; i++)
            {
                Ship.DeviceReadout d = _devices[i];
                if (d.device == null) continue;

                Vector2 p = WorldToGui(cam, d.device.transform.position);
                bool source = d.kind == PowerGraph.Kind.Source;
                bool fed = d.v >= Ballistics.PowerNominal * Ballistics.PowerBrownout;
                Color c = source ? Palette.Radiance : fed ? HudColor : CriticalColor;

                Readout(p, $"{d.v:0}V {d.i:0.#}A", c);
            }

            ship.CollectWireSegments(_segments);

            for (int i = 0; i < _segments.Count; i++)
            {
                Ship.WireSeg s = _segments[i];
                string note = s.holed ? "HOLE" : s.burned ? "BURNT" : s.kelvin >= 1f ? $"+{s.kelvin:0}K" : null;
                if (note == null) continue;

                Vector2 mid = WorldToGui(cam, ship.transform.TransformPoint((s.a + s.b) * 0.5f));
                Color c = s.state == Ship.WireState.Cut ? CriticalColor : WarnColor;
                Readout(mid, note, c);
            }
        }

        // 승무원. 방이 없는 배는 0명이라 아무것도 안 그린다.
        for (int i = 0; i < ship.crewmen.Count && focus != PauseControl.Layer.Power; i++)
        {
            Crewman m = ship.crewmen[i];
            Vector3 world = ship.transform.TransformPoint((Vector2)m.anchor * ShipGrid.CellSize);
            Vector2 p = WorldToGui(cam, world);
            Color c = m.alive ? Palette.Signal : CriticalColor;

            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - 3f, p.y - 3f, 7f, 7f), Texture2D.whiteTexture);
            GUI.color = Color.white;
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
            if (gun.Neutralized || gun.Hold == Gun.HoldReason.NoGunner || gun.Hold == Gun.HoldReason.NoPower)
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
