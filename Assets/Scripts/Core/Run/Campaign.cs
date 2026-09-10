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

    /// <summary>
    /// 탄약고 blastDamage 1당 MUN 몇을 주는가. **물리에서 뽑은 값이 아니라 첫 느낌 값이다** -
    /// destroyer 주포 탄약고가 blastDamage 1600이니 0.02면 MUN 32. 회수 경제를 실제로
    /// 굴려보고 감으로 고칠 손잡이다, Ballistics.Tuning처럼 근거가 있는 상수가 아니다.
    /// </summary>
    public float munitionsPerBlastDamage = 0.02f;

    // 격파한 적함의 설계 판 1장당 연구점수.
    public float researchPerPlate = 0.5f;

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

    private int _spawnWait;

    /// <summary>플레이어 뒤로 따라 들어올 동료. 대본과 무관하게 <see cref="wingmanStaggerTicks"/>마다 하나.</summary>
    private readonly Queue<SpawnDef> _wingQueue = new();

    private int _wingWait;

    /// <summary>암전 밑에 미리 세워 둔 적. delay 틱이 지나면 <see cref="WakeDormant"/>가 깨운다.</summary>
    private readonly List<(Ship ship, ShipAi ai, int delay, int alive)> _dormant = new();

    /// <summary>들판에서 출구에 닿았다. <see cref="Battle.objective"/>가 읽는 유일한 값.</summary>
    private bool _departed;

    /// <summary>들판의 잠든 배가 깨는 거리(m)와 출구 반경(m). 둘 다 감이다 - 1단계 측정이 정한다.</summary>
    public float wakeDistance = 1500f;
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
    private readonly List<Vector2> _visitedWrecks = new();

    /// <summary>다녀간 잔해에서 건지는 비율. 뜯는 것이 아니라 훑는 것이라 판 수 그대로는 아니다.</summary>
    public float wreckSalvage = 0.25f;

    /// <summary>
    /// 들판의 신호. 자리마다 하나 - 운석은 빼고, 같은 자리의 배들은 하나로 접는다.
    /// HUD가 방위로만 그린다. 정체는 가서 본다.
    /// </summary>
    public readonly List<Vector2> Signals = new();

    /// <summary>들판의 출구. Open이 아니면 null.</summary>
    public Vector2? Gate => Current != null && Current.Open ? new Vector2(Current.gateX, Current.gateY) : null;

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
        _leg = Mathf.Clamp(RunState.Leg - 1, -1, SubSectorGen.LegsPerChapter - 1);

        // 소구역 한가운데서 껐다 켠 경우. 마지막으로 고른 레인이 곧 지금 서 있는 노드다.
        if (_leg >= 0)
        {
            List<int> lanes = RunState.Lanes;

            _pending = lanes.Count > 0
                ? SubSectorGen.Make(_sector, _leg, lanes[lanes.Count - 1])
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
            _fork = new SectorDef[SubSectorGen.Lanes];
            int made = 0;

            for (int i = 0; i < _fork.Length; i++)
            {
                _fork[i] = SubSectorGen.Make(_sector, pendingLeg, i);

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
        BackgroundView.DriftScale = Current != null && Current.Open ? 0.1f : 1f;
    }

    public override void OnTick()
    {
        switch (_phase)
        {
            case Phase.Fighting:
                WakeDormant();
                ReachGate();
                WatchRefitSpot();
                // 소환이 전투 판정보다 먼저. 이번 틱에 뜬 배가 같은 틱의 목표 판정에 들어간다.
                DrainSpawnQueue();
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

        if (_toSpawn.Count == 0)
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

        do
        {
        {
            SpawnDef next = _toSpawn.Dequeue();
            // 들판의 적은 틱에 걸쳐 태어나도 잠든 채다 - 멀어서 안 보이고, 깨는 것은 거리다.
            Spawn(next, dormant: Current != null && Current.Open && !next.hulk);
        }
        }
        while ((entryStaggerTicks <= 0 || --burst > 0) && _toSpawn.Count > 0);

        _spawnWait = Mathf.Max(1, entryStaggerTicks);
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
                bool near = player != null
                    && ((Vector2)player.transform.position - (Vector2)ship.transform.position).sqrMagnitude
                        <= wakeDistance * wakeDistance;
                bool hurt = ship.TryGetComponent(out HullStructure hull) && hull.AliveCount < alive;

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
        _wingQueue.Clear();
        _dormant.Clear();
        _targets.Clear();
        _refitSpots.Clear();
        _wreckSpots.Clear();
        _visitedWrecks.Clear();
        Signals.Clear();
        RefitSpotNear = false;

        foreach (SpawnDef spawn in sector.spawns)
        {
            var at = new Vector2(spawn.x, spawn.y);

            if (spawn.hulk && spawn.refit)
                _refitSpots.Add(at);

            if (spawn.hulk && !spawn.refit && spawn.ship != SubSectorGen.Rock)
                _wreckSpots.Add(at);

            if (sector.Open && spawn.ship != SubSectorGen.Rock && !Near(Signals, at, SiteFold))
                Signals.Add(at);
        }

        bool preSpawn = string.IsNullOrEmpty(sector.script);

        foreach (SpawnDef spawn in sector.spawns)
        {
            if (spawn.Side == Ship.Team.Ally && !spawn.hulk)
                _wingQueue.Enqueue(spawn);
            else if (preSpawn && !sector.Open)
                Spawn(spawn, dormant: !spawn.hulk);   // 시설은 원래 거기 있던 것이라 잘 것이 없다
            else
                _toSpawn.Enqueue(spawn);   // 들판은 50척이 넘는다. 한 프레임에 다 지으면 도착 첫 프레임이 멎는다
        }

        _spawnWait = 1;   // 풀리면 다음 틱에 첫 척
        _holdForScript = !preSpawn;

        _battle = new Battle();
        Visited.Add(sector);
        _departed = false;

        // 들판: 출구에 닿는 것이 승리. 아래 두 분기보다 먼저다 - Wreck·Depot만 뽑힌 들판이
        // "싸울 것 없음"으로 떨어지면 동료가 들어오는 순간 끝난다. peaceful은 전승 대사를
        // 막는 값이라 여기서도 켠다: 자리 하나 안 건드리고 나가도 전승 보고가 나오면 거짓말이다.
        if (sector.Open)
        {
            _battle.boundless = true;
            _battle.peaceful = true;
            _battle.objective = () => _departed;
        }

        // **def가 정한다. 이미 뜬 것을 세면 안 된다** - 대본 있는 장은 시차 소환이라 지금은
        // 하나도 안 떠 있고, `_targets.Count > 0`으로 보면 8구역이 소탕 목표로 떨어진다.
        // 그러면 거울을 안 부수고 호위만 잡아도 구역이 끝난다.
        else if (HasTarget(sector))
            _battle.objective = TargetsDown;

        // 싸울 것이 없는 노드는 함대가 다 들어온 것이 곧 완료다.
        else if (!HasHostile(sector))
        {
            _battle.peaceful = true;
            _battle.objective = () => _toSpawn.Count == 0 && _wingQueue.Count == 0;
        }

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
        _targets.Clear();
        _toSpawn.Clear();
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
        SalvageResult recovered = ComputeSalvage();

        if (!recovered.IsEmpty)
        {
            RunState.Materials += recovered.materials;
            RunState.Propellant += recovered.propellant;
            RunState.Munitions += recovered.munitions;
            RunState.Research += recovered.research;

            Debug.Log(
                $"[Campaign] 노획 MTRL +{recovered.materials} PROP +{recovered.propellant} " +
                $"MUN +{recovered.munitions} RSCH +{recovered.research} " +
                $"(누적 MTRL {RunState.Materials} PROP {RunState.Propellant} " +
                $"MUN {RunState.Munitions} RSCH {RunState.Research}).");
        }

        // _sector++ 뒤에는 Current가 다음 구역이라 방금 깬 것을 여기서 잡아 둔다.
        SectorDef cleared = Current;

        _refitHere = cleared != null && cleared.refit;

        // 도착만으로 주는 물자. 전투 노획과 다른 축이라 따로 더한다.
        if (cleared != null && cleared.materials > 0)
        {
            RunState.Materials += cleared.materials;
            Debug.Log($"[Campaign] '{cleared.name}' 보급 MTRL +{cleared.materials}.");
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

        Vector2 gate = new(sector.gateX, sector.gateY);

        if (((Vector2)player.transform.position - gate).sqrMagnitude > gateRadius * gateRadius)
            return;

        // 워프는 탱크에서 나간다. 모자라면 출구에 서 있어도 안 나간다 - Depot·잔해가 필요해지는 자리.
        if (!player.Burn(Ballistics.WarpDeltaV))
            return;

        _departed = true;
        Debug.Log($"[Campaign] 출구 도착. '{sector.name}' 출항.");
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

        if (_departed || (_refitSpots.Count == 0 && _wreckSpots.Count == 0))
            return;

        Ship player = PlayerShip();

        if (player == null)
            return;

        Vector2 at = player.transform.position;

        RefitSpotNear = Near(_refitSpots, at, refitRadius);

        // 다녀간 잔해. 한 번 적히면 안 지운다 - 노획은 출항 때 한 번 센다.
        foreach (Vector2 spot in _wreckSpots)
        {
            if ((spot - at).sqrMagnitude <= refitRadius * refitRadius && !Near(_visitedWrecks, spot, 1f))
            {
                _visitedWrecks.Add(spot);
                Debug.Log($"[Campaign] 잔해 확인 ({spot.x:0},{spot.y:0}).");
            }
        }

        if (RefitSpotNear && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            EnterRefit(fromField: true);
    }

    /// <summary>같은 자리의 배들이 신호 하나로 접히는 거리. 자리 산포(±90 m)보다 크고 자리 간격(1.2 km)보다 작다.</summary>
    private const float SiteFold = 400f;

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
            TakeLane(lane);
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
        if (next >= SubSectorGen.LegsPerChapter
            || _def == null || _sector >= _def.sectors.Count - 1)
        {
            ToChapter(_refitHere);
            return;
        }

        _fork = new SectorDef[SubSectorGen.Lanes];
        _forkLeg = next;

        int made = 0;

        for (int i = 0; i < _fork.Length; i++)
        {
            _fork[i] = SubSectorGen.Make(_sector, next, i);

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
    public readonly struct SalvageResult
    {
        public readonly int materials;
        public readonly int propellant;
        public readonly int munitions;
        public readonly int research;

        public SalvageResult(int materials, int propellant, int munitions, int research)
        {
            this.materials = materials;
            this.propellant = propellant;
            this.munitions = munitions;
            this.research = research;
        }

        public bool IsEmpty =>
            materials <= 0 && propellant <= 0 && munitions <= 0 && research <= 0;
    }

    /// <summary>
    /// 이번 구역에서 실제로 회수 가능한 것. **적 함선 잔해만 본다** - 시설(Hulk)은 애초에
    /// 노획 대상이 아니다(거울 껍질을 부순다고 그 파편이 물자가 되지 않는다), 동료
    /// (<see cref="_wingmen"/>)도 뺀다(내 편을 내가 약탈하지 않는다).
    ///
    /// **같은 적이라도 어떻게 죽였느냐로 값이 갈린다.** 별도 "정밀 처치 보너스" 규칙이
    /// 없다 - 시뮬레이션이 이미 계산해 둔 파괴 상태를 읽을 뿐이다.
    ///   - MTRL = 남은 판 수(<see cref="HullStructure.AliveCount"/>, 판 한 장 = 1). 떨어져
    ///     나간 조각은 안 센다 - 배를 반토막 내면 노획도 반이다.
    ///   - PROP = 안 터진 탱크(<see cref="Tank"/>)의 <see cref="Tank.remaining"/> 합. 탱크가
    ///     죽으면(<see cref="Tank.Neutralized"/>) 그 연료는 이미 우주로 샜으니 0이다.
    ///   - MUN = 안 터진 탄약고(<see cref="CriticalModule"/>, <c>providesPower == false</c>
    ///     && !<see cref="CriticalModule.Neutralized"/>)의 blastDamage에 비례. 원자로는
    ///     지금 버전에서 아무 자원도 안 준다 - "멀쩡한 부품 회수품"은 나중 자리다.
    /// </summary>
    private SalvageResult ComputeSalvage()
    {
        int materials = 0;
        float propellant = 0f;
        float munitions = 0f;
        float research = 0f;

        foreach (GameObject wreck in _spawned)
        {
            if (wreck == null)
                continue;

            // 잔해(Hulk)는 다녀간 것만, 그것도 일부만. 잔해는 밀려날 수 있어 소환 좌표가 아니라
            // 지금 자리로 잰다 - 반경은 정비 반경과 같다(같은 "옆에 갔다").
            if (!wreck.TryGetComponent(out Ship ship))
            {
                if (Near(_visitedWrecks, wreck.transform.position, refitRadius)
                    && wreck.TryGetComponent(out HullStructure hull))
                    materials += Mathf.RoundToInt(hull.AliveCount * wreckSalvage);

                continue;
            }

            // 동료는 회수 대상이 아니다.
            if (_wingmen.Contains(ship))
                continue;

            // 죽은 것만. 전멸이 승리였을 때는 저절로 참이었는데, 들판은 출항이 승리라
            // 안 건드린 자리의 멀쩡한 배가 여기 그대로 들어온다.
            if (ship.IsCombatEffective)
                continue;

            if (wreck.TryGetComponent(out HullStructure structure))
            {
                materials += structure.AliveCount;

                // 연구는 남은 판이 아니라 설계 전체에서 나온다.
                ShipGrid.Map design = structure.DesignMap;

                if (design != null)
                {
                    for (int col = 0; col < design.width; col++)
                    for (int row = 0; row < design.height; row++)
                    {
                        if (ShipGrid.Solid(design.cells[col, row]))
                            research += researchPerPlate;
                    }
                }
            }

            foreach (Tank tank in wreck.GetComponentsInChildren<Tank>())
            {
                if (!tank.Neutralized)
                    propellant += tank.remaining;
            }

            foreach (CriticalModule module in wreck.GetComponentsInChildren<CriticalModule>())
            {
                if (!module.providesPower && !module.Neutralized)
                    munitions += module.blastDamage * munitionsPerBlastDamage;
            }
        }

        return new SalvageResult(
            materials,
            Mathf.RoundToInt(propellant),
            Mathf.RoundToInt(munitions),
            Mathf.RoundToInt(research));
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
    private Ship Spawn(SpawnDef spawn, bool dormant)
    {
        if (string.IsNullOrEmpty(spawn.ship))
            return null;

        if (!File.Exists(ShipDef.PathOf(spawn.ship)))
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
