using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using Core;
using System.Collections;

/// <summary>
/// 구역에서 구역으로. **전투 하나를 런으로 만드는 것이 이 클래스의 전부다.**
///
/// <see cref="Battle"/>이 끝나는 순간을 만들고 <see cref="RunState"/>가 배를 저장하는데,
/// 그 다음이 없었다 - 이겨도 갈 데가 없다. 여기가 그 다음이다.
///
/// **씬을 다시 안 로드하고 배도 다시 안 짓는다.** 플레이어 함선은 상한 채로 그대로 있고,
/// 구역이 넘어가는 것은 암전 밑에서 자리를 옮기고 적이 새로 뜨는 것뿐이다. RunState는
/// 세션이 끊겼을 때를 위한 것이지 구역 사이의 운반 수단이 아니다.
///
/// 이게 성립하는 이유가 <c>Battle._sawHostile</c> 걸쇠다. 없으면 다음 구역 적이 한 프레임
/// 뒤에 소환되는 순간 "적이 없다"로 읽혀서 첫 틱에 이긴다.
///
/// 워프 연출(<see cref="WarpTransition"/>)은 이 파일을 부르지 이 파일이 연출을 모른다 -
/// 컷신이 Campaign을 아는 방향 그대로다. 연출이 부르는 것은 <see cref="Depart"/>,
/// <see cref="Prepare"/>, <see cref="Enter"/> 셋뿐이고, 정비창이 없는 씬은 같은 셋을 틱에서
/// 연달아 부른다.
/// </summary>
public sealed class Campaign : TickBehaviour
{
    public static Campaign current;

    /// <summary>구역 사이의 사이. 전투가 끝나자마자 다음 적이 뜨면 무슨 일이 났는지 안 보인다.</summary>
    public int interludeTicks = 90;


    // 격파한 적함의 설계 판 1장당 연구점수.
    public float researchPerPlate = 0.5f;

    /// <summary>격파 급여. 적함 설계 판 1장당 크레딧 - 큰 배를 잡을수록 많이 받는다.</summary>
    public float creditsPerPlate = 0.3f;   // destroyer 533판 = 160 CR. 1.5는 800 CR이라 한 척이 내 배 전체 재건비의 80%였다

    /// <summary>
    /// 워프에서 빠져나온 속도(m/s). <see cref="slideTicks"/> 동안 제곱 곡선으로 0까지 줄어든다 -
    /// 처음에 확 죽고 끝이 길다. 한 틱에 0으로 자르면 급정거로 보인다. 카메라가 배를 픽셀에
    /// 고정하므로 이 슬라이드 동안 배는 서 있고 별만 흐른다. 0이면 제자리에 가만히 나타난다.
    /// </summary>
    public float entrySpeed = 200f;

    /// <summary>슬라이드 틱. 미끄러지는 거리는 <see cref="SlideDistance"/>고 스테이징이 그만큼 뒤에 세운다.</summary>
    public int slideTicks = 30;

    /// <summary>
    /// 다음 배가 뜰 때까지의 틱(60틱 = 1초). **대본이 engage로 풀어주는 장에서만 쓴다** -
    /// 소구역은 암전 밑에 미리 세워 두고 <see cref="wakeDelayTicks"/>로 깨운다.
    /// </summary>
    public int entryStaggerTicks = 15;

    /// <summary>플레이어 뒤로 동료가 따라 들어오는 간격(틱). 0.2초.</summary>
    public int wingmanStaggerTicks = 12;

    /// <summary>
    /// 미리 세워 둔 적이 깨는 틱. 첫 척은 도착 뒤 <see cref="wakeDelayTicks"/>, 그 뒤 한 척마다
    /// <see cref="wakeStaggerTicks"/>. 셋이 같은 틱에 표적을 잡고 같은 틱에 첫 탄을 쏘면 개전이
    /// 선택이 아니라 반응이 된다.
    /// </summary>
    public int wakeDelayTicks = 45;
    public int wakeStaggerTicks = 30;

    private CampaignDef _def;
    private int _sector;

    // 구역 하나의 흐름. OnTick은 이것 하나로 갈린다.
    //
    //   Prepare    (암전 밑) 지난 구역을 걷고 자리를 옮기고 Battle을 세운다. 대본 없는 구역은 적도 미리 세운다
    //   Enter      틱을 풀고 슬라이드. 끝나면 대본. 동료는 한 척씩 따라 들어온다
    //   Fighting   잠든 적을 깨우고, 대본이 풀면 한 척씩 소환. Battle.Tick이 목표를 판정
    //   EndSector  노획 → Advance(다음 자리) → Interlude 90틱
    //   Interlude  끝나면 정비 노드였으면 Refitting, 아니면 Choosing
    //   Refitting  RefitScreen(배는 씬에 그대로, 옆에 정비 패널). 출항하면 Choosing
    //   Choosing   LogisticsScreen(항로 화면, 세계 정지). 출항하면 WarpTransition이 Depart → Prepare → Enter
    //   Done       마지막 장을 깼다
    private enum Phase { Fighting, Interlude, Refitting, Choosing, Done }

    private Phase _phase;

    // 지금 선 노드가 정비 노드다. 저장은 RunState.pendingRefit - 정비 중에 끄면 재개가 정비 화면부터 연다.
    private bool _refitHere;
    private GameObject _refitAnchor;

    /// <summary>정비 카메라 크기(orthographicSize). 판 400장짜리 배가 왼쪽 60%에 들어가는 값.</summary>
    public float refitCameraSize = 28f;

    private bool _scriptHoldsSpawns;
    private int _scriptHoldTicks;
    private const int ScriptHoldMaxTicks = 60 * 90;   // 긴 브리핑도 기다리되 고장 난 대본은 제한한다

    // 슬라이드가 끝나고 대본이 실제로 시작될 때까지 적 소환을 묶는다. _scriptHoldsSpawns는
    // 대본이 "돌고 있는 동안"의 걸쇠라 아직 안 시작한 대본은 못 막는다.
    private bool _holdForScript;

    private SectorDef[] _fork;

    private int _forkLeg;
    private int _wait;

    /// <summary>
    /// 아직 안 뜬 이번 구역의 적. <see cref="OnTick"/>이 하나씩 꺼낸다.
    ///
    /// **이것이 비었는지가 목표 판정에 들어간다** - 표적이 아직 안 떴는데 "표적이 다
    /// 죽었다"로 읽히면 구역이 첫 틱에 끝난다. <c>Battle._sawHostile</c>이 소탕 목표에
    /// 대해 막아주는 것과 같은 구멍이고, 표적 목표에는 그 걸쇠가 없다.
    /// </summary>
    private readonly Queue<SpawnDef> _toSpawn = new();

    /// <summary>
    /// 들판의 아직 안 뜬 자리. 큐가 아니라 목록이다 - 순서가 아니라 **거리**로 꺼낸다.
    /// 덩어리 15~25개 × 10척을 도착 때 다 세우면 200척이 잠든 채 물리에 얹힌다. 플레이어에서
    /// <see cref="spawnDistance"/> 안에 든 자리만 세운다. 신호·보급·정비 자리 목록은 SpawnDef에서
    /// 읽으므로 안 뜬 자리도 브래킷은 가리킨다 - 신호는 "저기 뭐가 있다"지 "배가 서 있다"가 아니다.
    /// </summary>
    private readonly List<SpawnDef> _farSpawns = new();

    /// <summary>들판에서 자리를 세우는 거리(m). 항해 줌 폭 2.5 km와 센서 1.2 km보다 넉넉히 - 태어나는 것이 보이면 안 된다.</summary>
    public float spawnDistance = 5000f;

    /// <summary>
    /// 배경 실물(<see cref="SpawnDef.scenery"/>)만 쓰는 짧은 반경(m). 배와 갈라 둔 이유가
    /// 산수다: 500 m 간격에 5 km 반경이면 원 안에 314개가 들어와 판 28,000장이 된다
    /// (구축함 45척). 1.5 km면 서른 개 안쪽이라 지금 밀도와 같다. 배는 오는 것이 보여야
    /// 하지만 운석은 코앞에서 나타나도 된다 - 안 움직이니까.
    /// </summary>
    public float scenerySpawnDistance = 1500f;

    /// <summary>
    /// 이 거리를 넘긴 배경 실물은 지우고 자리를 <see cref="_farSpawns"/>로 되돌린다.
    /// **소환 반경보다 넓어야 한다** - 같으면 경계에서 세우고 지우기를 매 틱 반복한다.
    /// 항해 줌 폭이 2.5 km(반폭 1.25 km)라 2 km면 화면 밖에서 사라진다. 이 값이 상주 수를
    /// 정한다: 반경 2 km 안에 50개, 판 4,500장이다.
    /// </summary>
    public float sceneryDropDistance = 2000f;

    /// <summary>세운 배경 실물과 그 자리. 회수할 때 자리를 다시 넣으려면 짝을 들고 있어야 한다.</summary>
    private readonly List<(GameObject go, SpawnDef def)> _streamed = new();

    private int _spawnWait;

    /// <summary>
    /// 떠돌이 타이머(틱). 들판의 작은 엔카운터는 공간이 아니라 시간이다 - 15~25초마다 1척이
    /// 플레이어 진행 방향 센서 밖에서 태어난다. 속도와 무관하게 박자가 지켜지고, 가만히
    /// 있어도 순찰이 찾아온다. 적 접촉 중에는 안 보낸다(겹치면 전투가 안 끝난다).
    /// </summary>
    public int wandererMinTicks = 900;
    public int wandererMaxTicks = 1500;
    public float wandererDistance = 1800f;   // 항해 줌 폭 2.5 km의 반 밖. 태어나는 것이 보이면 안 된다
    private int _wandererWait;

    /// <summary>
    /// 추격 하나. 들판 진입 뒤 이만큼 지나면 출구 반대쪽 멀리서 한 척이 태어나 플레이어의 **마지막 큰 방출
    /// 위치**로 온다. 완성형 추격 AI가 아니다 - 이동 중 신경 쓸 대상 하나다. 부스터를 켜면 위치가 갱신되고,
    /// 운석 뒤·저추력이면 갱신이 안 된다. 센서 안에 들면 보통 AI로 넘어간다.
    /// </summary>
    public int hunterDelayTicks = 5400;      // 90초
    public float hunterDistance = 8000f;
    public float hunterLoud = 1.4f;          // 이 방출량 이상일 때만 위치를 잡힌다. 전추력 1.0, 부스터 1.6~2.0
    public int hunterListenTicks = 60;       // 1초마다 듣는다
    private bool _hunterSent;

    /// <summary>
    /// 이번 들판에서 방출량이 <see cref="hunterLoud"/>를 넘은 틱. 출구에서 <see cref="RunState.Heat"/>로 접힌다.
    ///
    /// **구역을 넘는 결과가 이것뿐이다.** 예전에는 출구만 넘으면 추격이 증발해서 도망이 언제나 정답이었고,
    /// 언제나 정답인 것은 결정이 아니다. 열기가 남으면 조용히 나가는 것에 처음으로 장기 보상이 붙는다.
    /// </summary>
    private int _loudTicks;

    /// <summary>열기 한 칸이 다음 들판의 추격을 앞당기는 틱. 5칸이면 75초가 당겨진다.</summary>
    public int hunterHeatTicks = 900;

    /// <summary>열기 한 칸에 필요한 시끄러운 초.</summary>
    public int heatSecondsPerLevel = 20;

    /// <summary>점프 한 번이 내는 소음(초). 시끄러운 사건이라 열기에 바로 얹는다.</summary>
    public int jumpNoiseSeconds = 15;

    /// <summary>마지막 발사 뒤 이만큼은 시끄럽다(틱). 3초 - 연사 사이 빈틈에 소음이 꺼졌다 켜졌다 하지 않을 길이.</summary>
    public int fireNoiseTicks = 180;

    /// <summary>
    /// 지금 플레이어가 시끄러운가. **추격의 귀와 열기가 같은 술어를 써야 한다** - 갈라지면
    /// "안 들켰는데 열기가 오른다"가 되고, 그 어긋남은 화면에 증상이 안 나온다.
    ///
    /// 둘이다: 부스터·전추력(<see cref="Ship.Emission"/>)과 **사격**. 사격을 안 세면 부스터를 안 쓰는
    /// 플레이에서 열기 축이 통째로 없는 것과 같아진다 - 기본 방출 0.6에 추력 최대 0.4라 천장이 1.0이고
    /// 문턱이 1.4이기 때문이다. 싸우면 들킨다는 것이 이 게임에서 제일 당연한 규칙인데 빠져 있었다.
    /// </summary>
    private bool Loud(Ship player)
        => player != null
        && (player.Emission >= hunterLoud
            || TickManager.currentTick - player.lastFireTick < fireNoiseTicks);

    /// <summary>
    /// 순찰. 덩어리 사이를 도는 배 - 지도 위에 움직이는 무리(M&B). 자리 셋을 돌고, 플레이어가 보이면 보통 AI.
    /// 진입 10초 뒤 둘. 추격과 같은 몸(Rover)이라 조종 코드가 하나다.
    /// </summary>
    public int patrolCount = 2;
    public int patrolDelayTicks = 600;
    public float patrolReach = 600f;         // 이 안에 들면 다음 자리로
    private bool _patrolSent;

    /// <summary>움직이는 적 하나. 추격이면 route[0]이 마지막으로 들은 플레이어 위치, 순찰이면 route가 도는 자리들.</summary>
    public sealed class Rover
    {
        public Ship ship;
        public ShipAi ai;
        public readonly List<Vector2> route = new();
        public int leg;
        public bool hunter;
        public string tag;   // 지도 라벨: 추격 / 순찰
    }

    /// <summary>지금 들판을 움직이는 적 전부. 트래커가 방위 신호로 찍는다.</summary>
    public readonly List<Rover> Rovers = new();

    /// <summary>
    /// 단거리 점프. Visited·Identified인 고정 앵커(보급·잔해·출구)로만 - "아무 데나 12 km"면 들판이 죽는다.
    /// 탱크에서 Δv를 태우고, 앵커 앞 standoff에 내리고, 시끄러운 사건이라 추격이 그 자리를 듣는다.
    /// </summary>
    public float jumpDeltaV = 1500f;
    public float jumpStandoff = 1500f;
    public float jumpBlack = 0.35f;
    private bool _jumping;

    /// <summary>출구에서 워프 Δv를 못 태운 동료. 구역이 끝날 때 상실로 센다 - 연료 없는 배는 못 따라온다.</summary>
    private readonly HashSet<Ship> _stranded = new();

    /// <summary>이 속도 아래여야 보급·정비가 된다. 부스터로 스치면 지나친다 - 제동 타이밍이 이동의 손맛이다.</summary>
    public float dockSpeed = 80f;

    /// <summary>정비 자리 반경 안인데 너무 빠르다. 프롬프트가 "감속"을 띄운다.</summary>
    public bool RefitSpotTooFast { get; private set; }

    /// <summary>
    /// 들판 소환의 건조 초. 격납고가 배를 뽑는 길 그대로 - 판을 하나씩 심어 한 프레임 스파이크를
    /// 프레임 수백 개로 편다. 5 km는 전속으로 10초라 이 안에 끝나야 한다. 메인 섹터·동료는 0(즉시).
    /// </summary>
    public float farBuildSeconds = 3f;

    /// <summary>플레이어 뒤로 따라 들어올 동료. 대본과 무관하게 <see cref="wingmanStaggerTicks"/>마다 하나.</summary>
    private readonly Queue<SpawnDef> _wingQueue = new();

    private int _wingWait;

    /// <summary>암전 밑에 미리 세워 둔 적. delay 틱이 지나면 <see cref="WakeDormant"/>가 깨운다.</summary>
    private readonly List<(Ship ship, ShipAi ai, int delay, int alive)> _dormant = new();

    /// <summary>들판에서 출구에 닿았다. <see cref="Battle.objective"/>가 읽는 유일한 값.</summary>
    private bool _departed;

    /// <summary>들판의 잠든 배가 깨는 거리(m)와 출구 반경(m). 둘 다 감이다 - 1단계 측정이 정한다.</summary>
    public float wakeDistance = 400f;   // 식별(500 m) 안쪽. 보고 돌아설 수 있어야 접근이 결정이다
    public float gateRadius = 300f;

    /// <summary>들판의 정비 잔해 자리. 이 반경 안에서 R이 정비다.</summary>
    public float refitRadius = 300f;
    private readonly List<Vector2> _refitSpots = new();

    /// <summary>들판 한가운데서 연 정비. 출항이 항로 화면이 아니라 전투로 돌아간다.</summary>
    private bool _refitFromField;

    /// <summary>플레이어가 들판의 정비 자리 안에 있는가. HUD가 "R 정비"를 띄울지 읽는다.</summary>
    public bool RefitSpotNear { get; private set; }

    /// <summary>
    /// 들판의 잔해 자리(운석 제외)와 그중 플레이어가 다녀간 것. 잔해는 가야 노획이다 -
    /// 안 가 본 잔해밭이 출항 때 자재로 들어오면 자리를 고를 이유가 없다.
    /// </summary>
    private readonly List<Vector2> _wreckSpots = new();

    /// <summary>닿으면 한 번 주는 자리. 값은 SpawnDef에 있고 준 뒤에는 뺀다.</summary>
    private readonly List<SpawnDef> _supplySpots = new();
    private readonly List<Vector2> _visitedWrecks = new();

    /// <summary>
    /// 항로 자료를 건질 수 있는 비전투 자리. 별도 저장값을 만들지 않는다 - ContactView의
    /// Visited 원장이 이미 "실제로 갔다"를 저장하므로, 그 사실을 정보 보상에도 그대로 쓴다.
    /// </summary>
    private readonly List<Vector2> _intelSpots = new();

    /// <summary>다녀간 잔해에서 떼어 판 부품 값. 판 1장당 크레딧 - 훑는 것이라 격파 급여보다 훨씬 적다.</summary>
    public float wreckStrip = 0.25f;

    /// <summary>
    /// 들판의 신호. 자리마다 하나 - 운석은 빼고, 같은 자리의 배들은 하나로 접는다.
    /// HUD가 방위로만 그린다. 정체는 가서 본다.
    /// </summary>
    public readonly List<Signal> Signals = new();

    /// <summary>들판의 자리 하나. 같은 자리의 배들은 SiteFold 안에서 하나로 접힌다 - 첫 배(겉 템플릿)가 크기를 정한다.</summary>
    public readonly struct Signal
    {
        public readonly Vector2 at;
        public readonly float size;
        public readonly int gate;   // 출구면 몇 번째 출구인가. 자리면 -1

        /// <summary>
        /// 여기서 더 얻을 것도 맞을 것도 없다. <see cref="SweepSignals"/>가 켠다 - 켜지면 지도에 흐리게만 남는다.
        /// **지우지 않고 표시만 바꾸는 것이 요점이다** - 지우면 "안 가본 곳"과 "정리한 곳"이 다시 같아져서,
        /// 들판을 치운 만큼 조용해지는 감각이 사라진다. 지나온 길이 지도에 남아야 경로가 기록이 된다.
        /// </summary>
        public readonly bool dead;

        /// <summary>
        /// 이 자리에 **추력이 있는 것**이 있나. Hulk는 못 움직이므로 이 값이 곧 "위험이 스스로 다가올 수 있나"다.
        /// 좌표만 잡힌 단계(Resolved)에서 줄 수 있는 유일하게 정직한 위험 단서 - 정체(보급/적/잔해)는 여전히 500 m다.
        /// 절반만 보여주는 것이라 가 보고 싶은 이유가 남는다.
        /// </summary>
        public readonly bool crewed;

        public Signal(Vector2 at, float size, int gate = -1, bool dead = false, bool crewed = false)
        {
            this.at = at;
            this.size = size;
            this.gate = gate;
            this.dead = dead;
            this.crewed = crewed;
        }
    }

    /// <summary>들판의 출구. Open이 아니면 null.</summary>
    public int GateCount => Current != null ? Current.GateCount : 0;
    public Vector2 GateAt(int k) => Current.GateAt(k);

    /// <summary>닿은 출구 = 갈래. 들판에서 정해지고 항로 화면은 확인만 한다. -1이면 아직 안 닿았다.</summary>
    public int ChosenLane { get; private set; } = -1;

    /// <summary>출구 k 너머의 이름. 갈림길이 있으면 그 갈래 소구역, 장의 마지막이면 다음 장(둘 다 같은 곳).</summary>
    public string GateLabel(int k) => _gateLabels != null && k >= 0 && k < _gateLabels.Length ? _gateLabels[k] ?? "" : "";

    /// <summary>잔해·보급 자리에서 항로 자료를 하나라도 회수했는가.</summary>
    public bool RouteIntelUnlocked
    {
        get
        {
            foreach (Vector2 at in _intelSpots)
                if (ContactView.StateAt(at) >= ContactView.Reveal.Visited)
                    return true;

            return false;
        }
    }

    /// <summary>현재 들판에 항로 자료를 얻을 자리가 있는가. 지도 안내 문구가 읽는다.</summary>
    public bool RouteIntelAvailable => _intelSpots.Count > 0;

    /// <summary>출구 k 너머를 회수 자료가 요약한 한 줄. 테스트·로그용 원자료다.</summary>
    public string GateIntel(int k) => _gateIntel != null && k >= 0 && k < _gateIntel.Length ? _gateIntel[k] ?? "" : "";

    /// <summary>
    /// 항법 기록을 플레이어 결정 언어로 옮긴 카드. 원자료의 적/보급/엄폐 수치가 아니라,
    /// 다음 구역에서 무엇을 감수하고 무엇을 할 수 있는지를 지도에 준다.
    /// </summary>
    public RouteBriefing GateBriefing(int k) => _gateBriefings != null && k >= 0 && k < _gateBriefings.Length
        ? _gateBriefings[k] : RouteBriefing.None;

    public readonly struct RouteBriefing
    {
        public static readonly RouteBriefing None = new("정보 없음", "정보 없음", "정보 없음", "항로 자료가 부족함");

        public readonly string risk;
        public readonly string supplies;
        public readonly string evasion;
        public readonly string advice;

        public RouteBriefing(string risk, string supplies, string evasion, string advice)
        {
            this.risk = risk;
            this.supplies = supplies;
            this.evasion = evasion;
            this.advice = advice;
        }
    }

    private string[] _gateLabels;
    private string[] _gateIntel;
    private RouteBriefing[] _gateBriefings;

    /// <summary>출구 신호의 크기. 기항지와 같다 - 들판에서 제일 큰 것.</summary>
    public const float GateSignalSize = 15000f;

    private long _enterTick;

    /// <summary>
    /// 이번 구역의 표적. 비어 있으면 목표는 "적이 없다"이고, 차 있으면 "이것들이 다 죽었다"다.
    /// <see cref="Battle.objective"/>에 대입할 술어가 이 목록을 읽는다.
    /// </summary>
    private readonly List<HullStructure> _targets = new();

    /// <summary>이번 구역이 소환한 것 전부. 다음 구역으로 넘어갈 때 걷어낸다.</summary>
    public static List<GameObject> _spawned = new(); //이름 바꾸기 귀찮음

    public SectorDef Current => _pending ?? Chapter;

    private SectorDef Chapter =>
        _def != null && _sector >= 0 && _sector < _def.sectors.Count ? _def.sectors[_sector] : null;

    private SectorDef _pending;

    private int _leg = -1;

    private Battle _battle;

    /// <summary>1구역이 아니다 = 워프로 왔다. 1구역은 프롤로그가 세운 자리에 그대로 선다.</summary>
    private bool Warped => _sector > 0 || _leg >= 0;

    /// <summary>슬라이드가 미끄러지는 거리. <see cref="Slide"/>가 매 틱 주는 속도의 합과 같은 식이라 도착점이 정확하다.</summary>
    public float SlideDistance => SlideDistanceOf(entrySpeed, slideTicks);

    /// <summary>k번째 틱의 슬라이드 속도. 제곱 곡선 - 처음에 확 죽고 끝이 길다.</summary>
    public static float SlideSpeed(float speed, int k, int ticks)
    {
        float left = 1f - (float)k / ticks;
        return speed * left * left;
    }

    /// <summary>
    /// 슬라이드 거리. 적분(v·T/3)이 아니라 이산 합이다 - 30틱이면 둘이 5 % 갈려서 적분으로
    /// 세우면 도착점을 7 m 지나친다. 스테이징과 Slide가 같은 식을 써야 하는 이유다.
    /// </summary>
    public static float SlideDistanceOf(float speed, int ticks)
    {
        float sum = 0f;

        for (int k = 0; k < ticks; k++)
            sum += SlideSpeed(speed, k, ticks);

        return sum * TickManager.TickDeltaTime;
    }

    private void Awake()
    {
        current = this;
        _def = CampaignDef.Load();
        RunState.ValidateOrClear();
        _sector = Mathf.Clamp(RunState.Sector, 0, _def?.sectors.Count ?? 0);
        _leg = Mathf.Clamp(RunState.Leg - 1, -1, OpenSectorGen.LegsPerChapter - 1);

        // 소구역 한가운데서 껐다 켠 경우. 마지막으로 고른 레인이 곧 지금 서 있는 노드다.
        if (_leg >= 0)
        {
            List<int> lanes = RunState.Lanes;

            _pending = lanes.Count > 0
                ? OpenSectorGen.Make(_sector, _leg, lanes[lanes.Count - 1])
                : null;

            // 생성이 실패하면(템플릿 파일이 없다) 소구역을 건너뛰고 장으로 돌아간다.
            if (_pending == null)
                _leg = -1;
        }

        // 항로를 내놓았는데 안 고른 채 끈 저장. 깬 구역을 다시 싸우지 않고 갈림길부터 연다.
        int pendingLeg = RunState.PendingLeg;

        if (pendingLeg >= 0 && _def != null && _sector < _def.sectors.Count - 1)
        {
            _forkLeg = pendingLeg;
            _fork = new SectorDef[OpenSectorGen.Lanes];
            int made = 0;

            for (int i = 0; i < _fork.Length; i++)
            {
                _fork[i] = OpenSectorGen.Make(_sector, pendingLeg, i);

                if (_fork[i] != null)
                    made++;
            }

            // 템플릿 파일이 사라진 저장이면 갈래가 전부 null이다. 버튼 없는 화면 대신 장으로.
            if (made == 0)
            {
                _fork = null;
                ToChapter();
            }
        }

        Battle.onAnyEnd += OnBattleEnd;
    }

    private void OnDestroy()
    {
        Battle.onAnyEnd -= OnBattleEnd;

        if (current == this)
            current = null;
    }

    /// <summary>
    /// 켜면 <see cref="Start"/>가 1구역을 안 연다. 컷신이 끝나고 <see cref="StartRun"/>을
    /// 부를 때까지 캠페인은 가만히 있는다 - 프롤로그가 도는 동안 이미 전투가 굴러가던 것이
    /// 이 걸쇠 하나가 없어서였다.
    ///
    /// **인스펙터 값이다.** 컷신을 안 쓰는 씬은 예전 그대로 Start에서 시작한다.
    /// </summary>
    [SerializeField] private bool waitForCutscene;

    private bool _runStarted;

    private void Start()
    {
        if (_def == null || waitForCutscene)
            return;

        // 함선 선택이 열려 있으면 런을 아예 안 시작한다. 대사가 unscaled 시계를 타서
        // timeScale 0으로는 안 멎는다 - 시작 자체를 막아야 한다. 선택은 반드시 씬
        // 리로드로 끝나므로, 그 다음 로드의 Start가 정상적으로 연다.
        if (ShipSelectScreen.IsOpen)
            return;

        StartRun();
    }

    /// <summary>
    /// 1구역을 연다. 컷신이 끝나는 자리에서 부른다 - **Campaign은 컷신을 모른다.**
    /// 반대로 컷신이 Campaign을 아는 쪽이라, 연출이 늘어도 이 파일은 안 바뀐다.
    ///
    /// 두 번 불러도 안전하다. 컷신이 중간에 끊기는 길이 여럿이라(스킵·표적 소실) 한쪽만
    /// 부르게 두면 언젠가 두 번 불리고, 그러면 구역이 두 번 열려 소환물이 두 배가 된다.
    ///
    /// 재개(구역 > 1)도 이 길이다. 타이틀 없이 Prepare와 Enter를 연달아 부른다 - 부팅
    /// 암전이 이미 덮고 있어서 순간이동이 안 보인다.
    /// </summary>
    public void StartRun()
    {
        if (_def == null || _runStarted)
            return;

        // **선택 화면이 열려 있으면 아직이다.** Start만 이걸 봤는데, 컷신 쪽 길
        // (EndAndStartRun)은 sceneLoaded 구독 순서에 따라 선택 화면이 열리기 **전**에
        // 돌 수 있다 - 그러면 틱이 풀린 채로 1구역이 선택 화면 뒤에서 굴러간다
        // (증상: 배를 고르고 있는데 대본 90초 경고가 뜨고 전투가 끝나 있다).
        // 확정은 반드시 씬 리로드라, 여기서 거절해도 다음 Start가 연다.
        if (ShipSelectScreen.IsOpen)
            return;

        _runStarted = true;

        // 씬에 손으로 놓아둔 적은 캠페인의 것이 아니다. 남겨두면 1구역이 실제로 무엇인지가
        // 씬과 def 두 곳에 적히고, 그 둘은 반드시 어긋난다.
        ClearHostiles();

        // 정비 중에 껐다. 갈림길이든 장 경계든 정비 화면이 먼저다.
        _refitHere = RunState.PendingRefit;

        if (_fork != null || _refitHere)
        {
            Sky();
            _phase = Phase.Interlude;   // 갈림길부터. 다음 틱에 화면이 열린다
            _wait = 1;
            return;
        }

        // 재개는 슬라이드 없이. 부팅 암전 밑이라 워프 인 연출이 없는데 배만 800 m/s로 나오면 급발진으로 보인다.
        Prepare();
        Enter(slide: false);
    }

    /// <summary>하늘은 1구역에도 있다. 시드가 같으면 같은 하늘·같은 소품이라 재개해도 그대로다.</summary>
    private void Sky()
    {
        BackgroundView.Jump(Ballistics.Hash(RunState.Seed, _sector, _leg + 1), Current != null ? Current.kind : "");

        // 60 km 들판에서 기본 드리프트(1 m당 0.006도)는 한 바퀴다. 하늘이 도는 것으로 안 읽히고 어지럽다.
        BackgroundView.DriftScale = Current != null && Current.Field ? 0.1f : 1f;
    }

    public override void OnTick()
    {
        switch (_phase)
        {
            case Phase.Fighting:
                ReturnHelm();
                WakeDormant();
                ReachGate();
                WatchRefitSpot();
                // 소환이 전투 판정보다 먼저. 이번 틱에 뜬 배가 같은 틱의 목표 판정에 들어간다.
                DrainSpawnQueue();
                RecycleScenery();
                SpawnWanderer();
                SpawnHunter();
                SpawnPatrols();
                SteerRovers();
                SweepSignals();
                ListenSelf();
                FieldLog.Sample(this, PlayerShip());
                // 임시 적함을 실제 전투의 첫 적으로 세면, endCut 직후 승리할 수 있다.
                if (!_scriptHoldsSpawns)
                    _battle?.Tick();
                break;

            case Phase.Interlude:
                if (--_wait > 0)
                    break;

                // battle-won 다섯 줄이 15초인데 막간은 1.5초라, 물류창이 열리면 대사가
                // 안 보이는 채로 흘러 사라졌다. 대본이 화면을 비울 때까지 기다린다.
                if (ScriptManager.current != null && ScriptManager.current.IsBusy
                    && ++_scriptHoldTicks < ScriptHoldMaxTicks)
                    break;

                if (Current == null)
                {
                    RunCleared();
                    break;
                }

                if (_refitHere)
                    EnterRefit();
                else
                    EnterChoosing();
                break;

            case Phase.Refitting:
                // 틱이 멎어 여기 안 온다. RefitScreen의 출항이 DepartRefit을 부른다.
                break;

            case Phase.Choosing:
                // 열려 있는 동안은 틱이 멈춰 여기 안 온다. 정비창이 있으면 워프 연출이
                // Depart → Prepare → Enter를 부르고 Fighting으로 바꾼 뒤에야 틱이 돈다.
                // 여기 오는 것은 정비창이 없는 씬뿐이다 - 연출 없이 같은 셋을 바로 부른다.
                if (LogisticsScreen.IsOpen)
                    break;

                Depart(0);
                Prepare();
                Enter(slide: false);
                break;
        }
    }

    /// <summary>
    /// 대본이 끝났는데 조종간이 아직 컷신에 있으면 돌려준다.
    ///
    /// 대본의 마지막 줄(endCut)에만 맡기면 그 줄이 안 도는 길마다 배가 영영 안 움직인다 -
    /// 스킵으로 줄을 건너뛰거나, 큐 하나가 예외를 던져 코루틴이 죽거나, 새 대본에서
    /// endCut을 빠뜨리거나. 여기는 틱이 도는 자리(Fighting)뿐이라 워프·정비가 빌린
    /// 조종간(둘 다 틱이 멎어 있다)은 안 건드린다.
    /// </summary>
    private static void ReturnHelm()
    {
        if (!CutSceneManager.ControlsPlayer)
            return;

        if (ScriptManager.current != null && ScriptManager.current.IsBusy)
            return;

        Debug.LogWarning("[Campaign] 대본이 끝났는데 조종간이 컷신에 남아 있다. 돌려준다 - 대본에 endCut이 빠졌을 수 있다.");
        CutSceneManager.Remove("player");
    }

    /// <summary>구역 대본의 engage cue. 대본이 아직 돌아도 이 틱부터 소환이 시작된다.</summary>
    public void ReleaseSpawns()
    {
        _scriptHoldsSpawns = false;
        ApplyFleetHold();
    }

    /// <summary>
    /// 대본이 소환을 붙들고 있는 동안은 동료의 방아쇠도 잠근다. 동료의 포탑은 ShipAi를 안
    /// 거치고 Gun이 직접 NearestHostile을 잡는데 DetectionDistance가 2000이라, 안 잠그면
    /// 브리핑 중에 1000 m 밖의 컷신 배를 쏜다. 조준은 계속 돈다 - 겨눈 채 멈춘다.
    /// </summary>
    private bool FleetHolds => _holdForScript || _scriptHoldsSpawns;

    private void ApplyFleetHold()
    {
        foreach (Ship mate in _wingmen)
        {
            if (mate != null)
                mate.cutsceneHoldFire = FleetHolds;
        }
    }

    /// <summary>
    /// 동료를 <see cref="wingmanStaggerTicks"/>, 적을 <see cref="entryStaggerTicks"/> 간격으로
    /// 하나씩 꺼낸다. 적 간격이 0이면 이 틱에 전부 꺼낸다 - 예전 동작 그대로다.
    /// </summary>
    private void DrainSpawnQueue()
    {
        // 동료는 플레이어 뒤를 따라 들어온다. 대본과 무관하다.
        if (_wingQueue.Count > 0 && --_wingWait <= 0)
        {
            Spawn(_wingQueue.Dequeue(), dormant: false);
            _wingWait = Mathf.Max(1, wingmanStaggerTicks);
        }

        if (_toSpawn.Count == 0 && _farSpawns.Count == 0)
            return;

        // 슬라이드가 끝나고 대본이 시작될 때까지. OpenScript가 푼다.
        if (_holdForScript)
            return;

        // 구역 대본이 아직 화면을 쓰는 중이면 기다린다.
        if (_scriptHoldsSpawns)
        {
            bool busy = ScriptManager.current != null && ScriptManager.current.IsBusy;

            if (busy && ++_scriptHoldTicks < ScriptHoldMaxTicks)
                return;

            if (busy)
                Debug.LogWarning("[Campaign] 구역 대본이 90초를 넘겼다. 기다리지 않고 소환한다.");

            _scriptHoldsSpawns = false;
            ApplyFleetHold();
        }

        if (--_spawnWait > 0)
            return;

        // 들판은 한 번에 넷. 하나씩이면 50척에 12초라, 부스터로 달리는 플레이어가 아직 안 태어난 자리에 닿는다.
        int burst = Current != null && Current.Open ? 4 : 1;

        // **거리 목록이 큐를 굶기면 안 된다.** 배경 실물이 500 m 격자라 _farSpawns는 구역이
        // 끝날 때까지 비지 않는다 - 예전처럼 여기서 return하면 손대본 구역의 적이 영영
        // 안 나온다. 사거리 안에 아무것도 없었을 때만 큐로 내려간다.
        if (_farSpawns.Count > 0 && SpawnNear(4) > 0)
        {
            _spawnWait = Mathf.Max(1, entryStaggerTicks);
            return;
        }

        if (_toSpawn.Count == 0)
        {
            _spawnWait = Mathf.Max(1, entryStaggerTicks);
            return;
        }

        do
        {
        {
            SpawnDef next = _toSpawn.Dequeue();
            // 들판의 적은 틱에 걸쳐 태어나도 잠든 채다 - 멀어서 안 보이고, 깨는 것은 거리다.
            //
            // **잔해·운석만 판을 나눠 심는다.** 메인 운석밭은 여기로 오는데 buildSeconds가 0이라
            // 운석 하나가 90장을 한 프레임에 세웠다 - 거리로 꺼내는 들판 쪽(SpawnNear)은 이미
            // farBuildSeconds를 준다. 배에까지 주면 대본이 부른 적이 3초에 걸쳐 자라난다.
            Spawn(next,
                dormant: Current != null && Current.Open && !next.hulk,
                buildSeconds: next.hulk ? farBuildSeconds : 0f);
        }
        }
        while ((entryStaggerTicks <= 0 || --burst > 0) && _toSpawn.Count > 0);

        _spawnWait = Mathf.Max(1, entryStaggerTicks);
    }

    /// <summary>
    /// 플레이어 <see cref="spawnDistance"/>(배경 실물은 <see cref="scenerySpawnDistance"/>)
    /// 안에 든 자리를 최대 burst개 세우고 **세운 수를 돌려준다** - 부르는 쪽이 "사거리 안에
    /// 아무것도 없었다"를 알아야 큐로 내려갈 수 있다. 뒤에서부터 훑어 제거가 O(1)이다.
    /// </summary>
    private int SpawnNear(int burst)
    {
        Ship player = PlayerShip();

        if (player == null)
            return 0;

        Vector2 eye = player.transform.position;
        float ships = spawnDistance * spawnDistance;
        float scenery = scenerySpawnDistance * scenerySpawnDistance;
        int made = 0;

        for (int i = _farSpawns.Count - 1; i >= 0 && burst > 0; i--)
        {
            SpawnDef spawn = _farSpawns[i];

            if ((new Vector2(spawn.x, spawn.y) - eye).sqrMagnitude
                > (spawn.scenery ? scenery : ships))
                continue;

            _farSpawns.RemoveAt(i);

            GameObject before = _spawned.Count > 0 ? _spawned[^1] : null;

            Spawn(spawn, dormant: !spawn.hulk, buildSeconds: farBuildSeconds);

            // Spawn은 hulk에 대해 null을 돌려주므로(목록에만 담는다) 새로 담긴 것을 뒤에서
            // 집는다. 배경 실물만 짝으로 들고 있으면 되고, 그 짝이 회수의 유일한 근거다.
            if (spawn.scenery && _spawned.Count > 0 && _spawned[^1] != before)
                _streamed.Add((_spawned[^1], spawn));

            burst--;
            made++;
        }

        return made;
    }

    /// <summary>
    /// 멀어진 배경 실물을 지우고 그 자리를 목록에 되돌린다. **500 m 격자가 성립하는 근거가
    /// 이 함수다** - 회수가 없으면 60 km를 건너는 동안 14,400개가 전부 쌓인다.
    ///
    /// 자리(좌표)를 되돌리므로 되돌아가면 같은 운석이 같은 자리에 다시 선다 - 격자가
    /// 결정론이라 기억할 상태가 없다.
    ///
    /// **건드린 것은 회수하지 않는다.** 쏴서 부순 운석이 한 바퀴 돌고 오니 멀쩡한 것은
    /// "결과를 안고 끝까지 간다"가 깨지는 자리다. 판이 하나라도 줄었으면 추적만 그만두고
    /// 오브젝트는 그 자리에 남긴다 - 쌓이는 것은 플레이어가 실제로 쏜 것뿐이라 탄약이
    /// 상한이다.
    /// </summary>
    private void RecycleScenery()
    {
        Ship player = PlayerShip();

        if (player == null || _streamed.Count == 0)
            return;

        Vector2 eye = player.transform.position;
        float drop = sceneryDropDistance * sceneryDropDistance;
        int budget = SceneryRecyclePerTick;

        for (int i = _streamed.Count - 1; i >= 0 && budget > 0; i--)
        {
            (GameObject go, SpawnDef def) = _streamed[i];

            if (go == null)
            {
                _streamed.RemoveAt(i);
                continue;
            }

            if (((Vector2)go.transform.position - eye).sqrMagnitude <= drop)
                continue;

            _streamed.RemoveAt(i);
            budget--;

            // 판이 줄었으면 손댄 것이다. 짝만 놓고 오브젝트는 남긴다.
            if (!go.TryGetComponent(out HullStructure hull) || hull.AliveCount < IntactPlates(def.ship))
                continue;

            // 뜯겨 나간 조각은 이 오브젝트가 아니라 별개의 Hulk다 - 그건 위 판 수 검사에서
            // 이미 "손댄 것"으로 걸러진다.
            _spawned.Remove(go);
            Destroy(go);
            _farSpawns.Add(def);
        }
    }

    /// <summary>한 틱에 회수하는 최대 개수. 소환 burst와 같은 이유로 상한이 있다.</summary>
    private const int SceneryRecyclePerTick = 4;

    /// <summary>
    /// 멀쩡한 이 설계도의 판 수. <see cref="ShipDef.Bbox"/>와 같은 술어로 세야 한다 -
    /// 갈라지면 한 번도 안 맞은 운석이 "손댄 것"으로 읽혀 회수가 통째로 죽고, 증상은
    /// 에러가 아니라 "오래 날면 느려진다"다.
    /// </summary>
    private static readonly Dictionary<string, int> _intactPlates = new();

    private static int IntactPlates(string shipName)
    {
        if (_intactPlates.TryGetValue(shipName, out int cached))
            return cached;

        ShipDef def = ShipDef.Load(shipName);
        int plates = 0;

        if (def != null)
        {
            foreach (Placement p in def.placements)
            {
                if (ShipBuilder.StampsGrid(DefDatabase.Get(p.def), out _))
                    plates++;
            }
        }

        return _intactPlates[shipName] = plates;
    }

    /// <summary>
    /// 떠돌이 1척. 시간으로 낸다 - 주기가 차면 플레이어 진행 방향 <see cref="wandererDistance"/> 앞,
    /// 좌우 ±400 m에 세운다. 시드는 런 시드 + 틱이라 같은 런은 같은 자리에 같은 배다.
    /// </summary>
    private void SpawnWanderer()
    {
        if (Current == null || !Current.Open || _departed || _holdForScript)
            return;

        if (--_wandererWait > 0)
            return;

        var rng = new DeterministicRng(Ballistics.Hash(RunState.Seed, TickManager.currentTick, 0x57));
        _wandererWait = (int)rng.Range(wandererMinTicks, wandererMaxTicks);

        Ship player = PlayerShip();

        if (player == null || ContactView.HasHostileContact(player))
            return;

        string ship = OpenSectorGen.PickWandererShip(_sector, ref rng);

        if (string.IsNullOrEmpty(ship))
            return;

        Vector2 heading = player.velocity.sqrMagnitude > 25f ? player.velocity.normalized : player.NoseDirection;
        Vector2 side = new(-heading.y, heading.x);
        Vector2 at = (Vector2)player.transform.position + heading * wandererDistance + side * rng.Range(-400f, 400f);

        Ship wanderer = Spawn(new SpawnDef
        {
            ship = ship,
            team = "Enemy",
            x = at.x,
            y = at.y,
            facing = heading.x > 0f ? -1f : 1f,   // 플레이어를 마주본다
        }, dormant: false, buildSeconds: farBuildSeconds);

        if (wanderer != null)
            _noBounty.Add(wanderer);
    }

    /// <summary>
    /// 급여가 없는 배. 떠돌이는 적 접촉이 없으면 15~25초마다 무한히 오고, 순찰·추격은 자리가 아니라
    /// 압박이다 - 이것들을 세면 "죽이고 기다리고 반복"이 출구보다 낫다. 급여는 **자리**(들판에
    /// 놓인 것)에만 있다. Rover는 <see cref="Rovers"/>가 이미 목록이라 여기엔 떠돌이만.
    /// </summary>
    private readonly HashSet<Ship> _noBounty = new();

    private bool HasBounty(Ship ship)
    {
        if (_noBounty.Contains(ship))
            return false;

        foreach (Rover r in Rovers)
            if (r.ship == ship)
                return false;

        return true;
    }

    private void SpawnHunter()
    {
        // 지난 들판에서 시끄러웠으면 이번엔 더 일찍 온다. 하한은 10초 - 도착하자마자 붙으면 항해가 아니라 처형이다.
        int delay = Mathf.Max(600, hunterDelayTicks - RunState.Heat * hunterHeatTicks);

        if (_hunterSent || Current == null || !Current.Open || _departed || _holdForScript
            || TickManager.currentTick - _enterTick < delay)
            return;

        _hunterSent = true;
        Ship player = PlayerShip();

        if (player == null)
            return;

        var rng = new DeterministicRng(Ballistics.Hash(RunState.Seed, TickManager.currentTick, 0x48));
        string ship = OpenSectorGen.PickWandererShip(_sector, ref rng);

        if (string.IsNullOrEmpty(ship))
            return;

        // 출구 반대쪽 = 뒤. 곧장 출구로 달리면 뒤에서 오고, 돌아가면 옆에서 온다.
        Vector2 pos = player.transform.position;
        Vector2 back = -player.NoseDirection;

        if (GateCount > 0)
        {
            Vector2 centre = Vector2.zero;
            for (int k = 0; k < GateCount; k++) centre += GateAt(k);
            back = (pos - centre / GateCount).normalized;
        }
        Vector2 at = pos + back * hunterDistance;

        Rover r = AddRover(ship, at, back.x > 0f ? -1f : 1f, hunter: true, "추격");

        if (r != null)
            r.route.Add(pos);   // 보일 때까지는 마지막 관측 위치로 간다

        Debug.Log($"[Campaign] 추격 {ship} ({at.x:0},{at.y:0}).");
    }

    /// <summary>순찰 둘. 자리 셋씩 돌게 한다 - 출구는 안 돈다(출구 앞을 순찰이 지키면 갈래가 아니라 관문이다).</summary>
    private void SpawnPatrols()
    {
        if (_patrolSent || Current == null || !Current.Open || _departed || _holdForScript
            || TickManager.currentTick - _enterTick < patrolDelayTicks)
            return;

        _patrolSent = true;

        var sites = new List<Vector2>();

        foreach (Signal s in Signals)
            if (s.gate < 0) sites.Add(s.at);

        if (sites.Count < 2)
            return;

        var rng = new DeterministicRng(Ballistics.Hash(RunState.Seed, _sector * 16 + _leg + 1, 0x50));

        for (int n = 0; n < patrolCount; n++)
        {
            string ship = OpenSectorGen.PickWandererShip(_sector, ref rng);

            if (string.IsNullOrEmpty(ship))
                continue;

            // 시작 자리 하나, 거기서 가까운 둘. 멀리 있는 자리끼리 이으면 들판을 가로지르는 직선이 된다.
            Vector2 start = sites[(int)rng.Range(0, sites.Count - 0.001f)];
            var route = new List<Vector2> { start };
            sites.Sort((a, b) => (a - start).sqrMagnitude.CompareTo((b - start).sqrMagnitude));

            for (int i = 1; i < sites.Count && route.Count < 3; i++)
                route.Add(sites[i]);

            Vector2 at = start + new Vector2(rng.Range(-300f, 300f), rng.Range(-300f, 300f));
            Rover r = AddRover(ship, at, 1f, hunter: false, "순찰");

            if (r == null)
                continue;

            r.route.AddRange(route);
            r.leg = 1;
            Debug.Log($"[Campaign] 순찰 {ship} - 자리 {route.Count}개.");
        }
    }

    private Rover AddRover(string ship, Vector2 at, float facing, bool hunter, string tag)
    {
        Ship spawned = Spawn(new SpawnDef
        {
            ship = ship,
            team = "Enemy",
            x = at.x,
            y = at.y,
            facing = facing,
        }, dormant: false, buildSeconds: farBuildSeconds);

        if (spawned == null)
            return null;

        var r = new Rover { ship = spawned, ai = spawned.GetComponent<ShipAi>(), hunter = hunter, tag = tag };

        if (r.ai != null)
            r.ai._detatchBrain = true;

        Rovers.Add(r);
        return r;
    }

    /// <summary>
    /// 로버의 귀와 발. 보이면 보통 AI. 안 보이면 - 추격은 플레이어가 시끄러울 때의 위치로, 순찰은 다음 자리로.
    /// </summary>
    private void SteerRovers()
    {
        Ship player = PlayerShip();

        for (int i = Rovers.Count - 1; i >= 0; i--)
        {
            Rover r = Rovers[i];

            if (r.ship == null || r.ai == null)
            {
                Rovers.RemoveAt(i);
                continue;
            }

            bool sees = r.ship.NearestHostile() != null;
            r.ai._detatchBrain = !sees;

            if (sees || r.route.Count == 0)
                continue;

            Vector2 here = r.ship.transform.position;

            if (r.hunter)
            {
                if (TickManager.currentTick % hunterListenTicks == 0 && Loud(player)
                    && !ContactView.RockBetween(player.transform.position, here))
                    r.route[0] = player.transform.position;

                r.ai._targetPos = (Vector3)r.route[0];
                continue;
            }

            if ((r.route[r.leg] - here).sqrMagnitude <= patrolReach * patrolReach)
                r.leg = (r.leg + 1) % r.route.Count;

            r.ai._targetPos = (Vector3)r.route[r.leg];
        }
    }

    /// <summary>점프해도 되나. 이유는 지도가 그대로 보여준다.</summary>
    public bool CanJump(out string why)
    {
        Ship player = PlayerShip();
        why = "";

        if (player == null || Current == null || !Current.Open || _departed || _jumping)
            why = "지금은 안 된다";
        else if (ContactView.HasHostileContact(player))
            why = "적 접촉 중";
        else if (player.shipTanks.Count > 0 && player.AvailableDeltaV() < jumpDeltaV)
            why = $"Δv 부족 {player.AvailableDeltaV():0}/{jumpDeltaV:0}";

        return why.Length == 0;
    }

    /// <summary>앵커로 점프. 검정 - 옮김 - 걷힘. 동료도 같은 만큼 옮긴다. 추격은 착지점을 듣는다.</summary>
    public void JumpTo(Vector2 anchor)
    {
        if (!CanJump(out _))
            return;

        StartCoroutine(Jump(anchor));
    }

    private System.Collections.IEnumerator Jump(Vector2 anchor)
    {
        Ship player = PlayerShip();

        if (player == null || !player.Burn(jumpDeltaV))
            yield break;

        _jumping = true;
        TickManager.Paused = true;
        LogisticsScreen screen = LogisticsScreen.Instance;
        screen?.Fade(1f, jumpBlack);
        yield return new WaitForSecondsRealtime(jumpBlack + 0.05f);

        Vector2 from = player.transform.position;
        Vector2 dir = (anchor - from).normalized;
        Vector2 land = anchor - dir * jumpStandoff;
        Vector2 delta = land - from;

        // 나와 동료. 리지드바디도 같이 - transform만 옮기면 다음 Simulate가 도로 끌어온다.
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship s = Ship.All[i];

            if (s == null || (s != player && (s.team != Ship.Team.Ally || s.dormant)))
                continue;

            Vector2 to = (Vector2)s.transform.position + delta;
            s.transform.position = to;

            if (s.Rig != null)
            {
                s.Rig.position = to;
                s.Rig.linearVelocity *= 0.3f;   // 거품에서 나오면 느리다. 정지 거리를 다시 잰다
            }
        }

        _loudTicks += jumpNoiseSeconds * 60;

        // 점프는 시끄럽다. 추격이 착지점을 안다.
        foreach (Rover r in Rovers)
            if (r.hunter && r.route.Count > 0) r.route[0] = land;

        Debug.Log($"[Campaign] 점프 {delta.magnitude / 1000f:0.0} km → 앵커 앞 {jumpStandoff:0} m. Δv -{jumpDeltaV:0}.");

        TickManager.Paused = false;
        screen?.Fade(0f, jumpBlack * 1.5f);
        _jumping = false;
    }

    /// <summary>때가 된 잠든 적을 깨운다. 뇌를 붙이고 방아쇠를 푼다 - 그게 잠의 전부였다.</summary>
    private void WakeDormant()
    {
        for (int i = _dormant.Count - 1; i >= 0; i--)
        {
            (Ship ship, ShipAi ai, int delay, int alive) = _dormant[i];

            // 들판에서는 시간이 아니라 거리로 깬다 - 아니면 45틱 뒤 60 km에 흩어진 자리가
            // 전부 깨서 "안 건드린 자리"가 없다. **피해로도 깬다**: 잠든 배는 표적이 안 될
            // 뿐 탄은 물리라 맞으므로, 거리만 보면 사거리 밖에서 반격 없이 죽일 수 있다.
            if (Current != null && Current.Open)
            {
                if (ship == null)
                {
                    _dormant.RemoveAt(i);
                    continue;
                }

                Ship player = PlayerShip();
                bool near = false;

                if (player != null)
                {
                    Vector2 to = (Vector2)ship.transform.position - (Vector2)player.transform.position;
                    // 돌아서는 배는 안 깨운다 - 순항 속도면 400 m를 2초에 지나서, 멀어지는 중까지 깨우면 "보고 돌아가기"가 없다.
                    // 시끄러우면 멀리서 깬다(방출량), 운석 뒤면 안 깬다 - 내가 못 보는 자리는 나를 못 본다.
                    float reach = wakeDistance * player.Emission;
                    near = to.sqrMagnitude <= reach * reach
                        && Vector2.Dot(player.velocity, to.normalized) > -20f
                        && !ContactView.RockBetween(player.transform.position, ship.transform.position);
                }

                // 건조 중에 태어난 배는 alive가 0으로 적혔다(판 장부가 아직 없다). 완성되면
                // 그때의 판 수를 기준으로 다시 적는다 - 안 그러면 "맞았다"를 영영 못 본다.
                HullStructure hull = ship.GetComponent<HullStructure>();

                if (alive == 0 && hull != null && hull.AliveCount > 0)
                {
                    alive = hull.AliveCount;
                    _dormant[i] = (ship, ai, delay, alive);
                }

                bool hurt = hull != null && alive > 0 && hull.AliveCount < alive;

                if (!near && !hurt)
                    continue;
            }
            else if (TickManager.currentTick - _enterTick < delay)
                continue;

            if (ship != null)
            {
                ship.dormant = false;
                ship.cutsceneHoldFire = false;
            }

            if (ai != null)
            {
                ai._detatchBrain = false;
                ai._targetPos = null;
            }

            _dormant.RemoveAt(i);
        }
    }

    /// <summary>
    /// 암전 밑에서 도는 반쪽. 지난 구역을 걷고, 배를 식히고, 자리를 옮기고, Battle을 세운다.
    /// **화면에 닿는 것은 하나도 없다** - 대사·로그·슬라이드는 <see cref="Enter"/>다.
    ///
    /// 대본이 없는 구역(소구역 전부)은 적을 여기서 실제 자리에 세우고 재운다. 대본이 있는
    /// 장은 큐에 넣고 engage를 기다린다 - sector-01이 자기 컷신 배를 플레이어 앞 1000 m에
    /// 세우는데, 캠페인 적을 미리 세우면 그 자리에 둘이 겹쳐 태어난다.
    ///
    /// Battle을 새로 만드는 이유는 <c>Ended</c>가 한 번 켜지면 안 꺼지기 때문이다. 목표
    /// 술어를 갈아끼우는 자리도 여기다 - 8구역은 "적이 없다"가 아니라 "거울이 죽었다"이고,
    /// 그것은 다른 함수를 대입하는 것일 뿐 틱 루프를 안 건드린다.
    /// </summary>
    public void Prepare()
    {
        SectorDef sector = Current;
        Ship player = PlayerShip();

        // 지난 구역이 남긴 것을 걷는다. 플레이어 배는 여기 안 들어 있다 - 손상을 안고 가는
        // 것이 이 게임의 규칙이다. 1구역은 걷을 것이 없고 프롤로그가 세운 자리 그대로 선다.
        if (Warped)
        {
            ClearField();
            player?.CoolDown();
        }

        Sky();

        // 구역이 더 없다 = 런을 깼다. **끝을 아는 자리는 여기와 Interlude 둘뿐이다.**
        if (sector == null)
        {
            RunCleared();
            return;
        }

        Stage(sector, player);

        _toSpawn.Clear();
        _farSpawns.Clear();
        _streamed.Clear();
        _wingQueue.Clear();
        _dormant.Clear();
        _targets.Clear();
        _refitSpots.Clear();
        _wreckSpots.Clear();
        _supplySpots.Clear();
        _visitedWrecks.Clear();
        _intelSpots.Clear();
        Signals.Clear();
        RefitSpotNear = false;

        foreach (SpawnDef spawn in sector.spawns)
        {
            var at = new Vector2(spawn.x, spawn.y);

            if (spawn.hulk && spawn.refit)
                _refitSpots.Add(at);

            if (spawn.hulk && !spawn.refit && !spawn.scenery)
                _wreckSpots.Add(at);

            if (spawn.hulk && (spawn.credits > 0 || spawn.propellant > 0 || spawn.munitions > 0))
            {
                // **사본을 든다.** 아래 보급이 남은 양을 빼는데, 원본은 SectorDef가 들고 있는 공유 객체다 -
                // 거기서 빼면 같은 구역을 다시 준비할 때 이미 비어 있고(운석이 두 배로 쌓이던 것과 같은 함정),
                // Identify가 읽는 "보급" 판정도 같이 사라진다. 자리 좌표와 실을 것 셋만 있으면 된다.
                _supplySpots.Add(new SpawnDef
                {
                    x = spawn.x,
                    y = spawn.y,
                    credits = spawn.credits,
                    propellant = spawn.propellant,
                    munitions = spawn.munitions,
                });
            }

            // 잔해·보급·기항지는 항법 기록을 남긴다. 같은 자리의 잔해 여러 개는 신호 하나로 접는다.
            if (sector.Open && spawn.hulk && !spawn.scenery && !Near(_intelSpots, at, SiteFold))
                _intelSpots.Add(at);

            if (sector.Open && !spawn.scenery && !NearSignal(at))
                Signals.Add(new Signal(at, spawn.signalSize > 0f ? spawn.signalSize : 500f));
        }

        // 출구도 신호다. 처음부터 좌표가 아니라 방위 + 진한 부채꼴이고, 15 km에서 좌표, 센서 안에서 "출구 · 어디".
        _gateLabels = new string[sector.GateCount];
        _gateIntel = new string[sector.GateCount];
        var beyond = new SectorDef[sector.GateCount];

        for (int k = 0; k < sector.GateCount; k++)
        {
            Signals.Add(new Signal(sector.GateAt(k), GateSignalSize, k));
            beyond[k] = PeekBeyond(k);
            _gateLabels[k] = beyond[k]?.name ?? "";
        }

        for (int k = 0; k < sector.GateCount; k++)
            _gateIntel[k] = DescribeRoute(beyond[k], sector.GateCount == 2 ? beyond[1 - k] : null);

        _gateBriefings = new RouteBriefing[sector.GateCount];
        for (int k = 0; k < sector.GateCount; k++)
            _gateBriefings[k] = BriefRoute(beyond[k], sector.GateCount == 2 ? beyond[1 - k] : null);

        // **활성 여부는 자리 전체를 접은 뒤에 나온다.** 신호는 첫 배가 만들지만(NearSignal) 겉과 속이
        // 다를 수 있다 - trap은 표류 잔해 속에 습격조가 숨어 있다. 첫 배만 보면 그 자리가 "표류"로 읽힌다.
        for (int i = 0; i < Signals.Count; i++)
        {
            Signal s = Signals[i];

            if (s.gate >= 0)
                continue;

            foreach (SpawnDef spawn in sector.spawns)
            {
                if (spawn.hulk || spawn.scenery
                    || (new Vector2(spawn.x, spawn.y) - s.at).sqrMagnitude > SiteFold * SiteFold)
                    continue;

                Signals[i] = new Signal(s.at, s.size, s.gate, s.dead, crewed: true);
                break;
            }
        }

        _loudTicks = 0;

        // 판정 지표. 재기만 하고 아무것도 안 바꾼다 - 들판이 아닐 때는 아예 안 켠다.
        // 도착점이 원점인 것은 규칙이다(CLAUDE.md "워프 도착점은 언제나 원점, 뱃머리 +X") - 우회 비율의 분모가 여기서 나온다.
        if (sector.Open)
            FieldLog.Begin(sector.name, Vector2.zero);

        bool preSpawn = string.IsNullOrEmpty(sector.script);

        foreach (SpawnDef spawn in sector.spawns)
        {
            if (spawn.Side == Ship.Team.Ally && !spawn.hulk)
                _wingQueue.Enqueue(spawn);
            // 배경 실물은 구역 종류와 무관하게 거리로 꺼낸다. 500 m 격자라 큐에 넣으면
            // 메인 100 km에서 40,000개를 순서대로 다 세운다.
            else if (spawn.scenery || sector.Open)
                _farSpawns.Add(spawn);
            else if (preSpawn)
                Spawn(spawn, dormant: !spawn.hulk);   // 시설은 원래 거기 있던 것이라 잘 것이 없다
            else
                _toSpawn.Enqueue(spawn);
        }

        _wandererWait = wandererMinTicks;
        Rovers.Clear();
        _hunterSent = false;
        _patrolSent = false;
        _stranded.Clear();

        // 메인도 들판이다(100 km). 운석은 def에 넣지 않고 여기서 뽑는다 - def는 공유 객체라
        // 같은 구역을 두 번 준비하면 운석이 두 배로 쌓인다. 시드가 같으니 결과는 같다.
        if (sector.Field && !sector.Open)
        {
            foreach (SpawnDef rock in OpenSectorGen.Rocks(sector, _sector))
                _farSpawns.Add(rock);
        }

        // 들판 전체를 500 m 격자로 채운다. 덩어리 운석은 그 위의 웃돈이다.
        foreach (SpawnDef prop in OpenSectorGen.Scenery(sector, _sector))
            _farSpawns.Add(prop);

        _spawnWait = 1;   // 풀리면 다음 틱에 첫 척
        _holdForScript = !preSpawn;

        _battle = new Battle();
        Visited.Add(sector);
        _departed = false;
        ChosenLane = -1;

        // 들판: 출구에 닿는 것이 승리. 아래 두 분기보다 먼저다 - Wreck·Depot만 뽑힌 들판이
        // "싸울 것 없음"으로 떨어지면 동료가 들어오는 순간 끝난다. peaceful은 전승 대사를
        // 막는 값이라 여기서도 켠다: 자리 하나 안 건드리고 나가도 전승 보고가 나오면 거짓말이다.
        if (sector.Open)
        {
            _battle.boundless = true;
            _battle.peaceful = true;
            _battle.objective = () => _departed;
            _battle.objectiveText = () => "출구로 간다  " + GateBrief(0) + "  ·  " + GateBrief(1);
        }

        // **def가 정한다. 이미 뜬 것을 세면 안 된다** - 대본 있는 장은 시차 소환이라 지금은
        // 하나도 안 떠 있고, `_targets.Count > 0`으로 보면 8구역이 소탕 목표로 떨어진다.
        // 그러면 거울을 안 부수고 호위만 잡아도 구역이 끝난다.
        else if (HasTarget(sector))
        {
            _battle.objective = TargetsDown;
            _battle.objectiveText = () => $"표적 격파  {TargetsAlive()}척 남음";
        }

        // 싸울 것이 없는 노드는 함대가 다 들어온 것이 곧 완료다.
        else if (!HasHostile(sector))
        {
            _battle.peaceful = true;
            _battle.objective = () => _toSpawn.Count == 0 && _wingQueue.Count == 0;
            _battle.objectiveText = () => "함대 집결 대기";
        }
        else
            _battle.objectiveText = () => $"적 소탕  {HostilesAlive()}척";

        Debug.Log(
            $"[Campaign] {_sector + 1}구역 '{sector.name}' - {sector.spawns.Count}척, " +
            $"목표 {(HasTarget(sector) ? "표적 절단" : "적 소탕")}, " +
            $"{(preSpawn ? "미리 세움" : "대본 뒤 소환")}");
    }

    /// <summary>
    /// 화면이 밝아지는 순간의 반쪽. 틱을 풀고 슬라이드를 주고 Fighting으로 넘어간다.
    /// 대본은 슬라이드가 끝난 뒤에 튼다 - sector-01의 컷신 적은 플레이어 앞 1000 m인데
    /// 슬라이드 중에 재면 그 거리가 짧아진다.
    /// </summary>
    /// <param name="slide">워프 인 슬라이드를 주나. 연출 뒤에만 true다 - 재개나 정비창 없는 씬은 암전도 섬광도 없이 배만 튀어나온다.</param>
    public void Enter(bool slide)
    {
        TickManager.Paused = false;
        Time.timeScale = 1f;

        if (_phase == Phase.Done)
            return;

        _enterTick = TickManager.currentTick;
        _wingWait = Mathf.Max(1, wingmanStaggerTicks);

        Ship player = PlayerShip();
        int slideLeft = 0;

        if (player != null && player.TryGetComponent(out Rigidbody2D rig))
        {
            if (slide && Warped && entrySpeed > 0f)
            {
                StartCoroutine(Slide(rig, player.NoseDirection));
                slideLeft = slideTicks;
            }
            else
            {
                // 슬라이드를 안 줘도 스테이징은 슬라이드 거리만큼 뒤에 세웠다. 도착점으로 그냥 놓는다.
                Vector2 goal = (Vector2)player.transform.position + player.NoseDirection * (Warped ? SlideDistance : 0f);
                player.transform.position = goal;
                rig.position = goal;
            }
        }

        _phase = Phase.Fighting;
        StartCoroutine(OpenScript(Current, slideLeft));
    }

    private IEnumerator OpenScript(SectorDef sector, int afterTicks)
    {
        long until = TickManager.currentTick + afterTicks;

        while (TickManager.currentTick < until)
            yield return null;

        // 그새 구역이 끝났거나(싸울 것 없는 노드) 바뀌었다. 대본은 없던 일이다.
        if (_phase != Phase.Fighting || sector != Current || sector == null)
        {
            _holdForScript = false;
            ApplyFleetHold();
            yield break;
        }

        // 대본(script 필드)보다 먼저 적는다 - 진입 사건에 반응하는 대사가 구역 인트로
        // 대본과 겹치면 인트로가 이긴다(같은 프레임이면 나중 것이 난입이다).
        // 소구역은 이 사건을 안 낸다 - sector-entered-N 대사가 한 장에 세 번 나온다.
        if (_pending == null)
            RunLog.SectorEntered(_sector + 1);

        // 들판은 대본이 없어서 조용한데, 조용한 것과 아무 말도 안 하는 것은 다르다 - 출구가 둘이고
        // 어느 쪽이든 닿으면 끝이라는 것을 한 번은 말해야 한다. 소환은 안 미룬다(아래 블록 안 탄다).
        // 런에 한 번. 들판이 장마다 둘씩이라 매번 들으면 두 번째부터 벽지다.
        if (sector.Open && !RunState.HasFlag("field-briefed"))
        {
            RunState.SetFlag("field-briefed");
            ScriptManager.current?.Play("field-entered");
        }

        // 대본이 도는 동안 소환을 미룬다. 적은 4초 뒤 도착하고 다섯 줄 대본은 20초다.
        if (!string.IsNullOrEmpty(sector.script) && ScriptManager.current != null
            && ScriptManager.current.Play(sector.script))
        {
            _scriptHoldsSpawns = true;
            _scriptHoldTicks = 0;
        }

        _holdForScript = false;
        ApplyFleetHold();
    }

    private void RunCleared()
    {
        Debug.Log("[Campaign] 초복사 시설 절단. 런 클리어.");
        ScriptManager.current?.Play("run-cleared");
        _phase = Phase.Done;
    }

    /// <summary>
    /// 지난 구역이 남긴 것을 전부 걷는다. **플레이어 배 하나만 남긴다** - 거기서 떨어져
    /// 나간 조각은 이미 배가 아니다. 예전에는 <see cref="_spawned"/>만 지워서 Breakaway가
    /// 만든 잔해와 플레이어 배에서 뜯긴 조각이 구역을 넘어 영원히 남았다.
    ///
    /// 컷신이 빌린 플레이어 조종간도 여기서 돌아온다. 워프 연출이 Borrow로 입력을 끊고
    /// 아무도 안 돌려주면 도착한 배가 안 움직인다.
    /// </summary>
    private void ClearField()
    {
        CutSceneManager.Clear();

        Ship player = PlayerShip();
        GameObject keep = player != null ? player.gameObject : null;

        for (int i = HullStructure.All.Count - 1; i >= 0; i--)
        {
            HullStructure body = HullStructure.All[i];

            if (body != null && body.gameObject != keep)
                Destroy(body.gameObject);
        }

        foreach (Projectile shot in FindObjectsByType<Projectile>(FindObjectsSortMode.None))
            Destroy(shot.gameObject);

        // 구조 없는 소환물(짓다 만 배)까지.
        foreach (GameObject old in _spawned)
        {
            if (old != null && old != keep)
                Destroy(old);
        }

        _spawned.Clear();
        _noBounty.Clear();
        _targets.Clear();
        _toSpawn.Clear();
        _farSpawns.Clear();
        _streamed.Clear();
        _wingQueue.Clear();
        _dormant.Clear();
    }

    /// <summary>
    /// 워프 도착점은 언제나 원점, 뱃머리 +X다. 구역 JSON의 좌표는 전부 이 도착점 기준이라
    /// 구역을 그릴 때 세계 어디인지 셀 필요가 없다 - 배경은 어차피 노드마다 새로 뜬다.
    /// 슬라이드 거리만큼 뒤에 세워서 슬라이드가 끝나는 곳이 곧 원점이다. 1구역은 안 옮긴다 -
    /// 프롤로그가 세운 자리에 그대로 서고 그 JSON만 씬 좌표다.
    /// </summary>
    private void Stage(SectorDef sector, Ship player)
    {
        if (!Warped || player == null)
            return;

        Vector2 start = Vector2.left * (entrySpeed > 0f ? SlideDistance : 0f);

        player.transform.SetPositionAndRotation(start, Quaternion.identity);

        // 항행하던 속도를 들고 오면 도착하자마자 구역을 지나쳐 버린다.
        if (player.TryGetComponent(out Rigidbody2D body))
        {
            body.position = start;
            body.rotation = 0f;
            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
        }

        Debug.Log($"[Campaign] '{sector.name}'로 워프. 원점에 선다.");
    }

    /// <summary>
    /// 워프 이탈 속도. <see cref="slideTicks"/> 동안 제곱 곡선으로 0까지. 프레임마다 다시
    /// 쓰므로 그 사이 항력은 무시된다 - 슬라이드 거리를 스테이징이 믿고 쓰기 때문이다.
    /// 지나온 길에 <see cref="SlideTrailStep"/>마다 빛 꼬리 하나 - 워프에서 흘러나온 것이다.
    /// </summary>
    private IEnumerator Slide(Rigidbody2D rig, Vector2 heading)
    {
        long start = TickManager.currentTick;
        Vector2 last = rig.position;

        while (rig != null)
        {
            long k = TickManager.currentTick - start;

            if (k >= slideTicks)
            {
                rig.linearVelocity = Vector2.zero;
                yield break;
            }

            rig.linearVelocity = heading * SlideSpeed(entrySpeed, (int)k, slideTicks);

            // 프레임이 아니라 거리로 뿌린다. 800 m/s에서 프레임마다 뿌리면 13 m 간격 점선이고
            // 멎을 때는 한 자리에 쌓인다.
            if ((rig.position - last).sqrMagnitude >= SlideTrailStep * SlideTrailStep)
            {
                BoosterTrail.Add(rig.position, WarpTransition.WarpColour);
                last = rig.position;
            }

            yield return null;
        }
    }

    private const float SlideTrailStep = 5f;

    // Hulk는 편이 없어서 안 센다.
    private static bool HasHostile(SectorDef sector)
    {
        foreach (SpawnDef spawn in sector.spawns)
        {
            if (!spawn.hulk && spawn.Side == Ship.Team.Enemy)
                return true;
        }

        return false;
    }

    private static bool HasTarget(SectorDef sector)
    {
        foreach (SpawnDef spawn in sector.spawns)
        {
            if (spawn.target)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 표적이 전부 끝났는가. 끝나는 길이 둘이다.
    ///
    /// **갈라졌다** - 구조가 두 덩어리 이상이 됐다. 거울 껍질에서 이것이 진짜 조건이다.
    /// 초복사는 껍질이 닫혀 있어서 되는 것이라, 고리가 두 조각이 나는 순간 시설이 죽는다.
    /// 고리는 한 군데를 끊어도 여전히 이어진 호 하나라 안 갈라진다 - **두 군데를 끊어야
    /// 한다.** 그게 lance가 존재하는 이유고, 마지막 관문이 충각 두 번인 것이 그래서다.
    ///
    /// **판이 하나도 안 남았다** - 포탄으로 776장을 다 지우는 길도 막지는 않는다. 다만
    /// 그것을 하겠다는 사람이 있으면 그 사람 마음이다.
    ///
    /// 표적이 Hulk라 Ship.All에 없고 GameObject는 판이 다 죽어도 남으므로, 둘 다 판
    /// 장부(<see cref="HullStructure"/>)로 본다.
    /// </summary>
    /// <summary>HUD 한 줄. "구역 2/8  ·  표적 격파  1척 남음". 전투가 없으면 빈 문자열.</summary>
    public string ObjectiveLine()
    {
        if (_battle == null || _battle.objectiveText == null || _def == null)
            return "";

        return $"구역 {_sector + 1}/{_def.sectors.Count}  ·  " + _battle.objectiveText();
    }

    private int TargetsAlive()
    {
        int n = _toSpawn.Count;

        foreach (HullStructure target in _targets)
            if (target != null && target.AliveCount > 0 && !target.HasSplit)
                n++;

        return n;
    }

    private int HostilesAlive()
    {
        Ship player = PlayerShip();
        int n = _toSpawn.Count;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship s = Ship.All[i];

            if (s != null && s != player && !s.dormant && s.IsCombatEffective && s.IsHostileTo(player))
                n++;
        }

        return n;
    }

    /// <summary>출구 하나를 "A 좌 12° 8.3 km"로. 방위는 늘 안다(신호), 거리는 좌표가 잡힌 뒤.</summary>
    private string GateBrief(int k)
    {
        Ship player = PlayerShip();

        if (player == null || k >= GateCount)
            return "";

        Vector2 g = GateAt(k) - (Vector2)player.transform.position;
        float rel = Mathf.DeltaAngle(player.transform.eulerAngles.z, Mathf.Atan2(g.y, g.x) * Mathf.Rad2Deg);
        string bearing = Mathf.Abs(rel) < 0.5f ? "정면" : rel > 0f ? $"좌 {rel:0}°" : $"우 {-rel:0}°";
        bool located = ContactView.StateAt(GateAt(k)) >= ContactView.Reveal.Resolved;
        string dist = located ? $" {g.magnitude / 1000f:0.0} km" : "";

        return (k == 0 ? "A " : "B ") + bearing + dist;
    }

    private bool TargetsDown()
    {
        // 아직 안 뜬 것이 있으면 끝난 게 아니다. 소탕 목표는 Battle._sawHostile이 이
        // 구멍을 막아 주지만 표적 목표에는 그 걸쇠가 없다 - 없으면 표적이 뜨기도 전에
        // 빈 목록을 보고 "다 죽었다"가 된다.
        if (_toSpawn.Count > 0)
            return false;

        foreach (HullStructure target in _targets)
        {
            if (target != null && target.AliveCount > 0 && !target.HasSplit)
                return false;
        }

        return true;
    }

    private void OnBattleEnd(Battle battle)
    {
        // 정적 이벤트라 씬 교체 중 겹친 옛 Campaign도 받는다. 내 전투가 아니면 무시.
        if (current != this || battle != _battle)
            return;

        if (!battle.Won)
        {
            _toSpawn.Clear();
            _farSpawns.Clear();
            _streamed.Clear();
            _battle = null;
            _phase = Phase.Done;

            // 진 것은 런이 끝난 것이다. Battle이 이미 RunState.Clear를 불렀고, 그 안에서
            // 구역도 0으로 돌아간다 - 다음 함장은 1구역부터다.
            ShipSelectScreen.Forget();

            Debug.Log("[Campaign] 런 종료. 다음 함장은 처음부터.");
            return;
        }

        // **잔해를 걷어내기 전에 센다.** Prepare가 지난 구역의 소환물을 지우므로 여기가
        // 마지막 기회다.
        BountyResult earned = Bounty();

        if (!earned.IsEmpty)
        {
            RunState.Credits += earned.credits;
            RunState.Research += earned.research;

            if (earned.credits > 0)
                RunLog.Paid(earned.credits);

            Debug.Log(
                $"[Campaign] 급여 CR +{earned.credits} RSCH +{earned.research} " +
                $"(누적 CR {RunState.Credits} RSCH {RunState.Research}).");
        }

        // _sector++ 뒤에는 Current가 다음 구역이라 방금 깬 것을 여기서 잡아 둔다.
        SectorDef cleared = Current;

        _refitHere = cleared != null && cleared.refit;

        // 도착만으로 주는 물자. 전투 노획과 다른 축이라 따로 더한다.
        if (cleared != null && cleared.credits > 0)
        {
            RunState.Credits += cleared.credits;
            Debug.Log($"[Campaign] '{cleared.name}' 지원금 CR +{cleared.credits}.");
        }

        // **잔해를 걷기 전에 센다** - 노획과 같은 이유로 여기가 마지막 기회다.
        BuryWingmen(cleared != null && cleared.Open);

        if (_pending == null)
            RunLog.SectorCleared(_sector + 1);

        Advance();

        _battle = null;

        // **마지막 구역이었어도 같은 길로 간다.** Prepare가 "구역이 더 없다"를 이미 알고
        // 있으므로 여기서 또 세면 끝을 아는 자리가 둘이 된다.
        //
        // 다음 틱들에 걸쳐서 넘어가는 이유는 따로 있다. 여기는 Battle.OnTick 한가운데고,
        // TickManager가 목록을 훑는 도중에 TickBehaviour를 지우고 새로 다는 것이라 한 박자
        // 쉬어야 한다. 덤으로 승리 대사가 먼저 나오고 다음 구역 대사가 그 뒤에 겹친다 -
        // 여기서 바로 틀면 Campaign이 Awake, ScriptManager가 Awake이라 순서가
        // 뒤집혀서 마무리 대사가 교전 종료 보고보다 먼저 나온다.
        // 들판은 막간이 없다. 이 사이 _battle이 null이라 경계도 패배 판정도 없는데 틱은
        // 돌아서, 2 km 안 적이 90틱 동안 계속 쏜다. 다음 틱에 물류창이 열리며 틱을 세운다.
        _wait = cleared != null && cleared.Open ? 1 : Mathf.Max(1, interludeTicks);
        _scriptHoldTicks = 0;
        _phase = Phase.Interlude;
    }

    /// <summary>들판의 출구. 플레이어가 반경 안에 들어오면 출항이고, 그것이 이 노드의 승리다.</summary>
    private void ReachGate()
    {
        SectorDef sector = Current;

        if (_departed || sector == null || !sector.Open)
            return;

        Ship player = PlayerShip();

        if (player == null)
            return;

        Vector2 at = player.transform.position;
        int reached = -1;

        for (int k = 0; k < sector.GateCount; k++)
        {
            if ((at - sector.GateAt(k)).sqrMagnitude <= gateRadius * gateRadius)
                reached = k;
        }

        if (reached < 0)
            return;

        // 워프는 탱크에서 나간다. 모자라면 출구에 서 있어도 안 나간다 - Depot·잔해가 필요해지는 자리.
        if (!player.Burn(Ballistics.WarpDeltaV))
            return;

        // 동료도 같은 값을 낸다. 못 내면 남는다 - 연료 없는 배를 공짜로 데려가면 탱크가 플레이어 배에만 있는 셈이다.
        foreach (Ship mate in Wingmates())
        {
            if (!mate.Burn(Ballistics.WarpDeltaV))
            {
                _stranded.Add(mate);
                Debug.Log($"[Campaign] 동료 '{mate.name}' Δv 부족 - 낙오.");
            }
        }

        // 열기는 **반으로 줄이고 이번 몫을 더한다.** 순수 누적이면 한 번 시끄러운 판이 남은 런을 영영 망치고,
        // 매번 새로 쓰면 조용히 다닌 보람이 그 구역에서 끝난다. 반감이 "만회할 수 있다"를 만든다.
        // 열기를 갱신하기 **전에** 적는다 - 이번 판 내내 걸려 있던 값이 이번 판의 행동과 짝이다.
        FieldLog.End(this, player, reached);
        RunState.Heat = RunState.Heat / 2 + _loudTicks / Mathf.Max(1, 60 * heatSecondsPerLevel);
        Debug.Log($"[Campaign] 열기 {RunState.Heat} (시끄러운 시간 {_loudTicks / 60f:0}초).");

        ChosenLane = reached;   // 닿은 출구가 갈래다. 항로 화면은 이걸 확인만 한다
        _departed = true;
        Debug.Log($"[Campaign] 출구 {reached} 도착. '{sector.name}' 출항 - {GateLabel(reached)} 쪽.");
    }

    // 다음 자리로 한 칸.
    private void EnterChoosing()
    {
        LogisticsScreen.Open();
        _phase = Phase.Choosing;
    }

    /// <summary>
    /// 정비 노드. 배는 씬에 그대로 두고 옆에 정비 패널을 연다 - 고친 판이 배에서 바로 보이는 것이
    /// 이 화면의 값어치다(ArmorSkin은 틱이 아니라 프레임을 탄다). 틱만 세우고 시계는 돌린다 -
    /// 항로 화면과 달리 씬이 보이므로 엔진 불빛·VFX가 살아 있어야 한다. 입력은 끊는다 - Shift가
    /// Update에서 부스터를 켠다. 돌려주는 자리는 워프의 Prepare(CutSceneManager.Clear)다.
    /// </summary>
    private void EnterRefit(bool fromField = false)
    {
        Ship player = PlayerShip();

        if (player == null || !RefitScreen.Open())
        {
            if (fromField)
                return;

            _refitHere = false;
            EnterChoosing();
            return;
        }

        _refitFromField = fromField;

        TickManager.Paused = true;
        Time.timeScale = 1f;
        CutSceneManager.Borrow("player", player);

        // 배를 왼쪽에. 패널이 오른쪽 PanelFraction을 덮으니 남은 폭의 가운데가 배다.
        _refitAnchor = new GameObject("Refit Anchor");
        float halfWidth = refitCameraSize * Screen.width / Mathf.Max(1f, Screen.height);
        _refitAnchor.transform.position = player.transform.position
            + Vector3.right * (halfWidth * RefitScreen.PanelFraction);
        CameraSystem.CutsceneFrame(_refitAnchor.transform, null, 0f, refitCameraSize);

        _phase = Phase.Refitting;
    }

    /// <summary>정비 화면의 출항. 항로 화면으로 넘어간다.</summary>
    public void DepartRefit()
    {
        if (_phase != Phase.Refitting)
            return;

        CameraSystem.ReleaseCutscene(blend: false);

        if (_refitAnchor != null)
            Destroy(_refitAnchor);

        // 들판에서 연 정비는 전투로 돌아간다. 빌린 조종간을 돌려주고 틱을 푼다 - 항로 화면
        // 경로는 워프의 Prepare(CutSceneManager.Clear)가 돌려주지만 여기는 워프가 없다.
        if (_refitFromField)
        {
            _refitFromField = false;
            CutSceneManager.Remove("player");
            TickManager.Paused = false;
            _phase = Phase.Fighting;
            return;
        }

        RunState.ClearPendingRefit();
        _refitHere = false;
        EnterChoosing();
    }

    /// <summary>들판의 정비 자리. 반경 안에서 R이면 정비 화면. 잔해는 안 움직이므로 소환 좌표로 잰다.</summary>
    private void WatchRefitSpot()
    {
        RefitSpotNear = false;

        if (_departed || (_refitSpots.Count == 0 && _wreckSpots.Count == 0 && _supplySpots.Count == 0))
            return;

        Ship player = PlayerShip();

        if (player == null)
            return;

        Vector2 at = player.transform.position;
        bool slow = player.velocity.magnitude <= dockSpeed;

        // 도착 자세. 반경 안이라도 빠르면 지나친다 - 부스터 접근은 빠르지만 못 대고, 관성 접근은 느리지만 댄다.
        bool refitNear = Near(_refitSpots, at, refitRadius);
        RefitSpotNear = refitNear && slow;
        RefitSpotTooFast = refitNear && !slow;

        // 다녀간 잔해. 한 번 적히면 안 지운다 - 노획은 출항 때 한 번 센다.
        foreach (Vector2 spot in _wreckSpots)
        {
            if ((spot - at).sqrMagnitude <= refitRadius * refitRadius && !Near(_visitedWrecks, spot, 1f))
            {
                _visitedWrecks.Add(spot);
                Debug.Log($"[Campaign] 잔해 확인 ({spot.x:0},{spot.y:0}).");
            }
        }

        // 보급 자리. 한 번 주고 목록에서 뺀다 - 같은 자리에 다시 와도 두 번 안 준다.
        for (int i = _supplySpots.Count - 1; i >= 0; i--)
        {
            SpawnDef spot = _supplySpots[i];

            if (!slow || (new Vector2(spot.x, spot.y) - at).sqrMagnitude > refitRadius * refitRadius)
                continue;

            // **들어가는 만큼만 싣고 나머지는 자리에 남는다.** 통째로 지우면 창고 상한이 그냥 벌점이 된다 -
            // 가득 찬 채로 지나간 보급이 영영 사라지니까. 남겨두면 비우고 다시 오는 것이 경로가 된다.
            int cr = spot.credits;   // 돈은 창고가 아니라 계좌라 상한이 없다
            int prop = Take(RunState.Propellant, spot.propellant, RunState.MaxPropellant);
            int mun = Take(RunState.Munitions, spot.munitions, RunState.MaxMunitions);

            if (cr == 0 && prop == 0 && mun == 0)
                continue;   // 셋 다 가득. 자리는 그대로 두고 비운 뒤 다시 온다

            RunState.Credits += cr;
            RunState.Propellant += prop;
            RunState.Munitions += mun;

            spot.credits -= cr;
            spot.propellant -= prop;
            spot.munitions -= mun;

            Debug.Log($"[Campaign] 보급 CR +{cr} PROP +{prop} MUN +{mun} ({spot.x:0},{spot.y:0}). 남은 것 {spot.credits}/{spot.propellant}/{spot.munitions}.");
            AnnounceSupply(player, cr, prop, mun, spot);

            if (spot.credits <= 0 && spot.propellant <= 0 && spot.munitions <= 0)
                _supplySpots.RemoveAt(i);
        }

        if (RefitSpotNear && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            EnterRefit(fromField: true);
    }

    /// <summary>같은 자리의 배들이 신호 하나로 접히는 거리. 자리 산포(±90 m)보다 크고 자리 간격(1.2 km)보다 작다.</summary>
    private const float SiteFold = 400f;

    /// <summary>창고에 실제로 들어가는 양. 상한을 넘는 몫은 자리에 남는다.</summary>
    private static int Take(int have, int offer, int cap) => Mathf.Clamp(cap - have, 0, Mathf.Max(0, offer));

    /// <summary>
    /// 보급이 도착한 순간을 화면에 띄운다.
    ///
    /// **이게 없으면 가는 이유가 없다.** 예전에는 Debug.Log 한 줄이 전부라 플레이어는 자원이 쌓이는지
    /// 아닌지를 몰랐고, 쓰는 곳은 두 노드 뒤 정비 화면이었다. 받은 것과 쓰는 것 사이가 그만큼 멀면
    /// 인과가 안 느껴지고, 안 느껴지는 보상을 찾아 6분을 우회할 사람은 없다.
    ///
    /// **얻은 양과 지금 총량을 같이 적는다.** "+200"만으로는 그게 큰지 작은지 모른다 - 분모가 있어야
    /// 다음 자리에 갈지 말지가 판단이 된다. 남은 것이 있으면 그것도 적는다: 창고가 가득이라 두고 가는
    /// 것이 곧 "여길 다시 올 이유"다.
    ///
    /// System 레인을 쓰는 이유는 이게 자막이 아니라 알림이어서다(머리글이 남는 레인).
    /// </summary>
    private static void AnnounceSupply(Ship player, int cr, int prop, int mun, SpawnDef spot)
    {
        if (DialogueManager.current == null)
            return;

        var got = new System.Text.StringBuilder();

        if (mun > 0) got.Append($"MUN +{mun} ({RunState.Munitions})  ");
        if (cr > 0) got.Append($"CR +{cr} ({RunState.Credits})  ");
        if (prop > 0) got.Append($"PROP +{prop / 1000}k");

        string left = spot.credits > 0 || spot.propellant > 0 || spot.munitions > 0
            ? "  ·  창고 가득, 남기고 간다"
            : "";

        DialogueManager.current.Spawn(
            got.ToString().TrimEnd() + left, "보급", duration: 5f, style: "system");

        // 시스템 줄은 숫자고, 승무원 줄은 "그래서 뭐가 달라졌나"다. 둘 다 있어야 읽힌다.
        RunLog.Supplied(mun >= prop / 1000 && mun >= cr ? "MUN" : prop / 1000 >= cr ? "PROP" : "CR");
    }

    /// <summary>신호 소멸 검사 주기(틱). 자리 수십 개 x Ship.All이라 매 틱 돌 이유가 없다 - 자리가 죽는 것은 초 단위 사건이다.</summary>
    public int signalSweepTicks = 30;

    /// <summary>
    /// 다 치운 자리의 신호를 끈다.
    ///
    /// **예전에는 신호가 영영 안 죽었다** - <see cref="Signals"/>는 Prepare에서 한 번 만들고 아무도 안 고쳤다.
    /// 자리를 통째로 부숴도 지도에서 같은 밝기로 계속 빛나서, "여기는 끝났다"가 화면에 없고 같은 곳을 또 갔다.
    /// 들판을 치운 만큼 조용해지는 것이 곧 내 행동이 공간에 남는 것이고, 그게 없으면 경로가 기록이 안 된다.
    ///
    /// **아직 안 세운 자리를 죽었다고 하면 안 된다.** 들판은 거리로 꺼내므로(<see cref="_farSpawns"/>)
    /// 멀리 있는 자리는 배가 하나도 없다 - 그것만 보면 도착하기도 전에 들판 전체가 꺼진다.
    /// 그래서 "이 자리 몫이 아직 소환 대기에 남아 있나"를 먼저 본다.
    /// </summary>
    private void SweepSignals()
    {
        if (Current == null || !Current.Open || TickManager.currentTick % Mathf.Max(1, signalSweepTicks) != 0)
            return;

        for (int i = 0; i < Signals.Count; i++)
        {
            Signal s = Signals[i];

            // 출구는 안 죽는다. 나가는 문은 치운다고 사라지지 않는다.
            if (s.dead || s.gate >= 0)
                continue;

            if (PendingNear(s.at) || LiveHostileNear(s.at) || StillWorthIt(s.at))
                continue;

            Signals[i] = new Signal(s.at, s.size, s.gate, dead: true, crewed: s.crewed);
            Debug.Log($"[Campaign] 신호 소멸 ({s.at.x:0},{s.at.y:0}).");
        }
    }

    /// <summary>내가 얼마나 시끄러웠나를 센다. 추격의 귀(<see cref="SteerRovers"/>)와 같은 문턱을 쓴다 - 둘이 갈라지면 "안 들켰는데 열기가 오른다"가 된다.</summary>
    private void ListenSelf()
    {
        if (_departed)
            return;

        if (Loud(PlayerShip()))
            _loudTicks++;
    }

    /// <summary>이 자리 몫이 아직 소환 대기에 있나. 있으면 아직 안 가본 자리다.</summary>
    private bool PendingNear(Vector2 at)
    {
        foreach (SpawnDef spawn in _farSpawns)
        {
            if ((new Vector2(spawn.x, spawn.y) - at).sqrMagnitude <= SiteFold * SiteFold)
                return true;
        }

        return false;
    }

    /// <summary>살아 있는 적이 이 자리에 있나. **잠든 배도 센다** - 자고 있을 뿐 거기 있고, 깨면 쏜다.</summary>
    private bool LiveHostileNear(Vector2 at)
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship s = Ship.All[i];

            if (s == null || s.team != Ship.Team.Enemy || !s.IsCombatEffective)
                continue;

            if (((Vector2)s.transform.position - at).sqrMagnitude <= SiteFold * SiteFold)
                return true;
        }

        return false;
    }

    /// <summary>아직 받을 것이 남았나. 정비는 계속 쓸모 있고, 보급·잔해는 한 번 받으면 목록에서 빠진다.</summary>
    private bool StillWorthIt(Vector2 at)
    {
        if (Near(_refitSpots, at, SiteFold))
            return true;

        foreach (SpawnDef spot in _supplySpots)
        {
            if ((new Vector2(spot.x, spot.y) - at).sqrMagnitude <= SiteFold * SiteFold)
                return true;
        }

        foreach (Vector2 wreck in _wreckSpots)
        {
            if ((wreck - at).sqrMagnitude <= SiteFold * SiteFold && !Near(_visitedWrecks, wreck, 1f))
                return true;
        }

        return false;
    }

    /// <summary>출구 k 너머가 무엇인가. Advance와 같은 셈 - 다음 소구역이 있으면 그 갈래, 없으면 다음 장.</summary>
    private SectorDef PeekBeyond(int k)
    {
        int next = _leg + 1;

        if (next < OpenSectorGen.LegsPerChapter && _def != null && _sector < _def.sectors.Count - 1)
            return OpenSectorGen.Make(_sector, next, k);

        return _def != null && _sector + 1 < _def.sectors.Count ? _def.sectors[_sector + 1] : null;
    }

    /// <summary>
    /// 항법 기록의 부분 정보. 정확한 함급·좌표를 까지 않고, 두 갈래의 상대적인 성격만 준다.
    /// 플레이어가 "안전/보급/엄폐 중 무엇을 택할지" 결정할 만큼만 말한다.
    /// </summary>
    public static string DescribeRoute(SectorDef route, SectorDef other = null)
    {
        if (route == null)
            return "자료 없음";

        RouteScan here = ScanRoute(route);

        if (other == null || ReferenceEquals(route, other))
            return $"적 {Level(here.hostiles)} · 보급 {Level(here.supplies)} · 엄폐 {CoverLevel(here.rocks)}";

        RouteScan there = ScanRoute(other);
        return $"적 {Relative(here.hostiles, there.hostiles)} · 보급 {Relative(here.supplies, there.supplies)} · 엄폐 {Relative(here.rocks, there.rocks)}";
    }

    /// <summary>
    /// 지도용 판단 카드. 군사 용어를 줄이는 일이 아니라, 정보의 대상과 결과를 명시하는 일이다.
    /// "적 많음" 대신 "위험 높음", "엄폐 많음" 대신 "회피 유리"처럼 행동 결과로 번역한다.
    /// </summary>
    public static RouteBriefing BriefRoute(SectorDef route, SectorDef other = null)
    {
        if (route == null)
            return RouteBriefing.None;

        RouteScan here = ScanRoute(route);
        RouteScan there = other != null && !ReferenceEquals(route, other) ? ScanRoute(other) : default;
        bool compare = other != null && !ReferenceEquals(route, other);

        string enemy = compare ? Relative(here.hostiles, there.hostiles) : Level(here.hostiles);
        string supply = compare ? Relative(here.supplies, there.supplies) : Level(here.supplies);
        string cover = compare ? Relative(here.rocks, there.rocks) : CoverLevel(here.rocks);

        string risk = enemy switch
        {
            "없음" => "없음",
            "적음" => "낮음",
            "많음" => "높음",
            _ => "보통",
        };
        string supplies = supply switch
        {
            "없음" => "없음",
            "적음" => "부족",
            "많음" => "넉넉",
            _ => "보통",
        };
        string evasion = cover switch
        {
            "없음" => "불리",
            "적음" => "불리",
            "많음" => "유리",
            _ => "보통",
        };

        string advice = risk == "낮음" && supplies == "넉넉" ? "수리·보급 후 전진하기 좋음"
            : risk == "높음" && supplies == "넉넉" ? "위험하지만 보급을 확보할 수 있음"
            : risk == "높음" && evasion == "유리" ? "교전보다 우회·회피에 유리"
            : evasion == "유리" ? "추격을 피하며 이동하기 좋음"
            : risk == "낮음" ? "안전하게 다음 구역을 탐색하기 좋음"
            : supplies == "넉넉" ? "보급 확보에 적합"
            : "교전 대비 후 전진 권장";

        return new RouteBriefing(risk, supplies, evasion, advice);
    }

    private readonly struct RouteScan
    {
        public readonly int hostiles, supplies, rocks;

        public RouteScan(int hostiles, int supplies, int rocks)
        {
            this.hostiles = hostiles;
            this.supplies = supplies;
            this.rocks = rocks;
        }
    }

    private static RouteScan ScanRoute(SectorDef sector)
    {
        var hostileSites = new List<Vector2>();
        var supplySites = new List<Vector2>();
        int rocks = 0;

        foreach (SpawnDef spawn in sector.spawns)
        {
            if (spawn.scenery)
            {
                rocks++;
                continue;
            }

            var at = new Vector2(spawn.x, spawn.y);

            if (!spawn.hulk && spawn.Side == Ship.Team.Enemy && !Near(hostileSites, at, SiteFold))
                hostileSites.Add(at);

            bool supply = spawn.refit || spawn.credits > 0 || spawn.propellant > 0 || spawn.munitions > 0;
            if (supply && !Near(supplySites, at, SiteFold))
                supplySites.Add(at);
        }

        return new RouteScan(hostileSites.Count, supplySites.Count, rocks);
    }

    private static string Relative(int value, int other)
    {
        int margin = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(value, other) * 0.15f));
        if (value + margin <= other) return "적음";
        if (value >= other + margin) return "많음";
        return "비슷";
    }

    private static string Level(int value) => value switch
    {
        0 => "없음",
        <= 3 => "적음",
        <= 10 => "보통",
        _ => "많음",
    };

    private static string CoverLevel(int value) => value switch
    {
        0 => "없음",
        <= 30 => "적음",
        <= 100 => "보통",
        _ => "많음",
    };

    /// <summary>이 좌표의 신호. 지도·트래커가 활성 여부와 소멸을 읽는다.</summary>
    public bool SignalAt(Vector2 at, out Signal found)
    {
        foreach (Signal s in Signals)
        {
            if (s.at == at)
            {
                found = s;
                return true;
            }
        }

        found = default;
        return false;
    }

    private bool NearSignal(Vector2 at)
    {
        foreach (Signal s in Signals)
            if ((s.at - at).sqrMagnitude <= SiteFold * SiteFold)
                return true;

        return false;
    }

    private static bool Near(List<Vector2> spots, Vector2 at, float radius)
    {
        foreach (Vector2 spot in spots)
        {
            if ((spot - at).sqrMagnitude <= radius * radius)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 물류창의 출항. 항로를 확정한다. **옮기는 것은 <see cref="Prepare"/>다** - 클릭 즉시
    /// 확정해야 워프 연출 도중에 꺼도 다음 실행이 그 구역에서 열린다.
    /// </summary>
    public void Depart(int lane)
    {
        if (_fork != null)
            TakeLane(ChosenLane >= 0 ? ChosenLane : lane);
    }

    public Ship Player => PlayerShip();

    /// <summary>
    /// 지금 살아 있는 동료. 워프 연출이 편대 자리에 세우고 같이 띄운다. <see cref="_wingmen"/>은
    /// 전투가 끝날 때 비워지므로 소환물에서 다시 고른다.
    /// </summary>
    public List<Ship> Wingmates()
    {
        var mates = new List<Ship>();

        foreach (GameObject go in _spawned)
        {
            if (go != null && go.TryGetComponent(out Ship ship)
                && ship.team == Ship.Team.Ally && ship.IsCombatEffective)
                mates.Add(ship);
        }

        return mates;
    }

    // 이 실행에서 거쳐온 구역 이름. 저장 안 한다 - 재개하면 지금 구역부터 다시 쌓인다.
    public readonly List<SectorDef> Visited = new();

    private void Advance()
    {
        int next = _leg + 1;

        // 이 장의 소구역을 다 지났거나 마지막 장이다.
        if (next >= OpenSectorGen.LegsPerChapter
            || _def == null || _sector >= _def.sectors.Count - 1)
        {
            ToChapter(_refitHere);
            return;
        }

        _fork = new SectorDef[OpenSectorGen.Lanes];
        _forkLeg = next;

        int made = 0;

        for (int i = 0; i < _fork.Length; i++)
        {
            _fork[i] = OpenSectorGen.Make(_sector, next, i);

            if (_fork[i] != null)
                made++;
        }

        // 템플릿 파일이 없으면 소구역을 통째로 건너뛴다. 런이 멈추는 것보다 낫다.
        if (made == 0)
            ToChapter(_refitHere);
        else
            RunState.SetPendingFork(next, _refitHere);
    }

    // refit: 방금 깬 마지막 소구역이 정비 노드다. 갈림길이 없어 정비 플래그를 여기서 저장한다.
    private void ToChapter(bool refit = false)
    {
        _fork = null;
        _pending = null;
        _leg = -1;

        _sector++;

        RunState.CommitChapter(_sector, refit);
    }

    public SectorDef[] Fork => _fork;

    public void TakeLane(int lane)
    {
        if (_fork == null)
            return;

        lane = Mathf.Clamp(lane, 0, _fork.Length - 1);

        // 그 갈래의 생성이 실패했으면 살아 있는 아무 갈래로 떨어진다.
        if (_fork[lane] == null)
        {
            for (int i = 0; i < _fork.Length; i++)
            {
                if (_fork[i] != null)
                {
                    lane = i;
                    break;
                }
            }
        }

        if (_fork[lane] == null)
        {
            ToChapter();
            return;
        }

        _pending = _fork[lane];
        _leg = _forkLeg;
        _fork = null;

        RunState.CommitLane(_leg + 1, lane);

        float x = float.MaxValue;

        foreach (SpawnDef spawn in _pending.spawns)
            x = Mathf.Min(x, spawn.x);

        Debug.Log(
            $"[Campaign] 항로 {lane}번 '{_pending.name}' - {_pending.spawns.Count}척, " +
            $"x {x:0}부터. 장 {_sector + 1} 소구역 {_leg}.");
    }

    /// <summary>
    /// 전투 하나가 남긴 노획. **RunState를 안 건드리는 순수 계산이다** - 계산과 지갑에
    /// 쓰는 것을 가르면, 나중에 "화면에 미리 보여주고 확정은 나중에" 같은 UI가 이 값을
    /// 몇 번을 다시 구해도 지갑이 안 늘어난다.
    /// </summary>
    /// <summary>격파 급여와 연구 자료. 물자는 여기서 안 나온다 - 아래 <see cref="Bounty"/> 참고.</summary>
    public readonly struct BountyResult
    {
        public readonly int credits;
        public readonly int research;

        public BountyResult(int credits, int research)
        {
            this.credits = credits;
            this.research = research;
        }

        public bool IsEmpty => credits <= 0 && research <= 0;
    }

    /// <summary>
    /// 이번 구역에서 번 돈. **노획이 아니라 급여다.**
    ///
    /// 예전에는 적함의 남은 판·안 터진 탱크·안 터진 탄약고를 세서 물자로 바꿨다. 두 가지가
    /// 틀렸다. 하나는 게임 쪽 - 배를 죽이는 제일 흔한 방법이 탄약고 유폭이라 이기면 노획이
    /// 0이었고, 알뜰히 부술수록 적게 받는 규칙은 승리를 벌준다. 다른 하나는 설정 쪽 -
    /// 전차를 잡아서 변속기를 떼어 쓰는 일은 있어도 그 장갑판을 뜯어 내 전차 벽에 덧대지는
    /// 않는다. 함선을 고치는 것은 베이스이고, 베이스는 돈을 받는다.
    ///
    /// 그래서 격파는 **설계 크기에 비례한 급여**다. 어떻게 죽였는지는 값에 안 들어간다 -
    /// 판정하는 쪽이 전장이 아니라 회계라서 그렇다.
    ///
    /// 시설(Hulk)은 대상이 아니고(거울 껍질을 부순다고 급여가 나오지 않는다), 동료
    /// (<see cref="_wingmen"/>)도 뺀다.
    /// </summary>
    private BountyResult Bounty()
    {
        int credits = 0;
        float research = 0f;

        foreach (GameObject wreck in _spawned)
        {
            if (wreck == null)
                continue;

            // 잔해(Hulk)는 다녀간 것만. 부품을 떼어 판 값이라 배 한 척을 잡은 것보다 적다.
            // 잔해는 밀려날 수 있어 소환 좌표가 아니라 지금 자리로 잰다.
            if (!wreck.TryGetComponent(out Ship ship))
            {
                if (Near(_visitedWrecks, wreck.transform.position, refitRadius)
                    && wreck.TryGetComponent(out HullStructure hull))
                    credits += Mathf.RoundToInt(hull.AliveCount * wreckStrip);

                continue;
            }

            if (_wingmen.Contains(ship) || !HasBounty(ship))
                continue;

            // 죽은 것만. 전멸이 승리였을 때는 저절로 참이었는데, 들판은 출항이 승리라
            // 안 건드린 자리의 멀쩡한 배가 여기 그대로 들어온다.
            if (ship.IsCombatEffective)
                continue;

            if (!wreck.TryGetComponent(out HullStructure structure))
                continue;

            // 설계 전체를 센다. 잔해에 뭐가 남았는지는 안 본다 - 급여는 무엇을 주웠느냐가
            // 아니라 무엇을 잡았느냐로 나온다.
            ShipGrid.Map design = structure.DesignMap;
            int designPlates = 0;

            if (design != null)
            {
                for (int col = 0; col < design.width; col++)
                for (int row = 0; row < design.height; row++)
                {
                    if (ShipGrid.Solid(design.cells[col, row]))
                        designPlates++;
                }
            }

            credits += Mathf.RoundToInt(designPlates * creditsPerPlate);
            research += designPlates * researchPerPlate;
        }

        return new BountyResult(credits, Mathf.RoundToInt(research));
    }

    /// <summary>
    /// 이번 구역에 살아 있는 동료들. 인덱스가 <see cref="RunState.Wingmen"/>의 것과 같아서,
    /// 전투가 끝날 때 누가 죽었는지 그 자리로 지운다 - 같은 설계도가 둘일 수 있으므로
    /// 이름으로 지우면 엉뚱한 쪽이 빠진다.
    /// </summary>
    public static List<Ship> _wingmen = new(); //이름 바꾸기 귀찮음

    /// <summary>전투가 끝났다. 못 싸우게 된 동료는 명단에서 뺀다 - 뒤에서부터 지워야 인덱스가 안 밀린다.</summary>
    /// <param name="field">들판이면 연료가 떨어진 것은 상실이 아니다 - 60 km를 편대로 따라오다 마른 배는 끌고 간다. 사람과 전기가 나간 것만 잃는다.</param>
    private void BuryWingmen(bool field)
    {
        for (int i = _wingmen.Count - 1; i >= 0; i--)
        {
            Ship ship = _wingmen[i];

            bool lost = ship == null
                || _stranded.Contains(ship)
                || (field ? !ship.CrewAlive || !ship.HasPower : !ship.IsCombatEffective);

            if (lost)
            {
                Debug.Log($"[Campaign] 동료 {i + 1}번 상실. 남은 구역은 그만큼 혼자다.");
                RunState.Lose(i);
            }
        }

        _wingmen.Clear();
    }

    private Ship PlayerShip()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Ship.All[i] != null && Ship.All[i].IsPlayerControlled)
                return Ship.All[i];
        }

        return null;
    }

    /// <summary>
    /// 동료 n번(1부터)의 편대 자리, 편대장 좌표계(x 뱃머리, y 좌현). 사다리꼴 델타 -
    /// 우현·좌현을 번갈아 가며 한 척마다 반 척씩 더 뒤로.
    /// </summary>
    public static Vector2 FormationOffset(int index, RectInt box)
    {
        int pingpong(int x) => ((x & 2) == 2) ? x >> 1 : -x >> 1;

        return new Vector2(-(box.width + 35) * index / 2f, (box.height + 35) * pingpong(index));
    }

    /// <summary>
    /// 한 척을 세운다. 시설과 잠든 적은 제자리에, 나머지는 <see cref="SlideDistance"/>만큼
    /// 뒤에서 미끄러져 들어온다. 동료는 JSON 좌표가 아니라 플레이어 옆 편대 자리다 -
    /// 스테이징이 구역마다 다른 곳에 플레이어를 놓으므로 절대 좌표는 맞을 수가 없다.
    /// </summary>
    private Ship Spawn(SpawnDef spawn, bool dormant, float buildSeconds = 0f)
    {
        if (string.IsNullOrEmpty(spawn.ship))
            return null;

        if (!ShipDef.Exists(spawn.ship))
        {
            Debug.LogError($"[Campaign] '{spawn.ship}' 설계도가 없다. 건너뛴다.");
            return null;
        }

        Ship player = PlayerShip();
        Vector2 heading = spawn.facing < 0f ? Vector2.left : Vector2.right;
        float angle = spawn.facing < 0f ? 180f : 0f;
        Vector2 at = new(spawn.x, spawn.y);
        Vector2 slot = Vector2.zero;
        bool wingman = spawn.isWingman && !spawn.hulk && player != null;

        if (wingman)
        {
            // 크기가 자리를 정하는데 배는 아직 안 지어졌다. 설계도만 읽는다.
            ShipDef def = ShipDef.Load(spawn.ship, checkSkin: false);
            slot = FormationOffset(_wingmen.Count + 1, def != null ? def.Bbox() : new RectInt(0, 0, 10, 5));
            heading = player.NoseDirection;
            angle = player.transform.eulerAngles.z;
            at = (Vector2)player.transform.position + heading * slot.x + player.PortDirection * slot.y;
        }

        bool slides = !spawn.hulk && !dormant && entrySpeed > 0f;

        if (slides)
            at -= heading * SlideDistance;

        // 비활성으로 만들고 마지막에 켠다. AddComponent는 오브젝트가 활성이면 Awake를
        // 즉시 부르는데, Ship.Awake가 shipDefName을 읽어 배를 통째로 짓는다.
        var go = new GameObject(spawn.ship);
        go.SetActive(false);

        go.transform.position = at;
        // 반대편 배는 거울이 아니라 180도 회전이다. 거울(scale -1)은 좌현·우현이 뒤집힌
        // 배를 만들고, 그걸 보정하는 코드가 여섯 자리에 흩어져 있었다.
        go.transform.rotation = Quaternion.Euler(0f, 0f, angle);

        if (spawn.hulk)
        {
            var hulk = go.AddComponent<Hulk>();
            hulk.structureDefName = spawn.ship;
            hulk.buildSeconds = buildSeconds;

            // 켜기가 곧 Awake다 - 실패하면 Thing.Activate가 지우고, 여기서는 목록에도
            // 안 담는다. 예전에는 Add가 켜는 줄 아래라 실패한 오브젝트가 청소 대상에도
            // 안 들어 비활성인 채 영원히 남았다.
            if (!Thing.Activate(go))
                return null;

            _spawned.Add(go);

            return null;
        }

        var ship = go.AddComponent<Ship>();
        ship.shipDefName = spawn.ship;
        ship.team = spawn.Side;
        ship.buildSeconds = buildSeconds;

        // 조종하는 것이 붙어야 배가 움직인다.
        var ai = go.AddComponent<ShipAi>();

        // Ship.Awake가 배를 통째로 짓는 자리라 이 리포에서 제일 많이 던질 수 있는
        // 켜기다. 실패하면 Thing.Activate가 지우고 여기서 그만둔다 - 아래 편대·속도
        // 설정은 살아 있는 배를 전제한다.
        if (!Thing.Activate(go))
            return null;

        _spawned.Add(go);

        // 조종은 편대에 묶고 사격은 안 건드린다. `_detatchBrain`이 켜져 있으면 ShipAi가
        // 스스로 표적을 안 고르므로 조타가 편대 자리에 붙고, 포탑은 원래부터 ShipAi를 안
        // 거치고 Gun이 직접 NearestHostile을 잡는다 - "따라다니되 알아서 쏘는" 것이 분기
        // 하나 없이 나온다.
        if (wingman)
        {
            _wingmen.Add(ship);
            ai._detatchBrain = true;
            ai._formation = player.transform;
            ai._formationOffset = slot;
            ship.cutsceneHoldFire = FleetHolds;   // 브리핑 중에 태어난 동료는 잠긴 채로
        }

        // 잠든 적. 적으로 안 세고(Ship.dormant), 뇌를 떼고, 방아쇠를 잠근다 - WakeDormant가
        // 셋을 되돌린다.
        if (dormant)
        {
            ship.dormant = true;
            ai._detatchBrain = true;
            ai._targetPos = null;
            ship.cutsceneHoldFire = true;
            _dormant.Add((
                ship, ai,
                wakeDelayTicks + _dormant.Count * wakeStaggerTicks,
                go.TryGetComponent(out HullStructure hull) ? hull.AliveCount : 0));
        }

        // SetActive 이후여야 Ship.Awake에서 Rigidbody2D가 생성되어 있다.
        if (go.TryGetComponent(out Rigidbody2D rig))
        {
            rig.collisionDetectionMode = CollisionDetectionMode2D.Discrete; //명시
            rig.linearVelocity = Vector2.zero;

            if (slides)
                StartCoroutine(Slide(rig, heading));
        }

        if (spawn.target &&
            go.TryGetComponent(out HullStructure structure))
        {
            _targets.Add(structure);
        }

        return ship;
    }

    /// <summary>씬에 놓여 있던 비플레이어 함선을 걷어낸다. 구역 1을 정하는 것은 def다.</summary>
    private void ClearHostiles()
    {
        for (int i = Ship.All.Count - 1; i >= 0; i--)
        {
            Ship ship = Ship.All[i];

            if (ship != null && !ship.IsPlayerControlled)
                Destroy(ship.gameObject);
        }
    }
}
