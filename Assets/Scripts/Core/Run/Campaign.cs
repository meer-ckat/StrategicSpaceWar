using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Core;
using System.Collections;

/// <summary>
/// 구역에서 구역으로. **전투 하나를 런으로 만드는 것이 이 클래스의 전부다.**
///
/// <see cref="Battle"/>이 끝나는 순간을 만들고 <see cref="RunState"/>가 배를 저장하는데,
/// 그 다음이 없었다 - 이겨도 갈 데가 없다. 여기가 그 다음이다.
///
/// **씬을 다시 안 로드하고 배도 다시 안 짓는다.** 플레이어 함선은 상한 채로 그 자리에
/// 그대로 있고, 구역이 넘어가는 것은 적이 새로 뜨는 것뿐이다. RunState는 세션이 끊겼을
/// 때를 위한 것이지 구역 사이의 운반 수단이 아니다.
///
/// 이게 성립하는 이유가 <c>Battle._sawHostile</c> 걸쇠다. 없으면 다음 구역 적이 한 프레임
/// 뒤에 소환되는 순간 "적이 없다"로 읽혀서 첫 틱에 이긴다.
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

    /// <summary>
    /// 도착으로 보는 최소 완충 거리. 속도가 거의 없을 때(정지 근처)의 바닥값이다 -
    /// <see cref="Arrived"/>가 실제로 쓰는 값은 이것과 "속도 × <see cref="reactionSeconds"/>"
    /// 중 큰 쪽이다.
    /// </summary>
    public float arrivalBuffer = 300f;

    /// <summary>
    /// 적이 뜨는 순간부터 그 자리에 도달하기까지 남기고 싶은 시간(초). 완충을 고정 거리로
    /// 두면 부스터로 가속하다 마주쳤을 때 그 거리를 한순간에 삼켜버려 급정거를 강요한다 -
    /// 완충을 "속도 × 이 시간"으로 잡으면 몇 마하로 오든 적이 뜨고 도달할 때까지 남는
    /// 시간은 이 값으로 똑같다.
    /// </summary>
    public float reactionSeconds = 4f;

    /// <summary>
    /// 적이 배치 좌표보다 이만큼 **오른쪽**에서 워프를 빠져나온다. 화면 밖이라 워프 섬광
    /// (<c>Ship.Awake</c>의 <c>ShipIncoming</c>)은 플레이어가 못 본다 - 보이는 것은
    /// 그 뒤의 결과, 초고속으로 미끄러져 들어오는 배다.
    /// </summary>
    public float entryDistance = 1500f;

    /// <summary>
    /// 워프에서 빠져나온 속도(m/s, -x 방향). **한 번 주고 마는 초기 속도다** - 계속
    /// 밀어주는 것이 아니라 drag가 깎고 <see cref="ShipAi"/>가 조종간을 잡는다.
    ///
    /// 얼마나 멀리 미끄러지는지는 여기가 아니라 배의 <c>drag</c>가 정한다:
    /// 속도는 <c>v0·e^(-drag·t)</c>로 줄고 총 거리는 <c>v0/drag</c>가 상한이다.
    /// 구축함(drag 0.3)에 900이면 상한이 3000 m라 <see cref="entryDistance"/> 1500 m를
    /// 약 2초에 지나고 그때도 500 m/s쯤 남는다 - 화면에 들어올 때까지 안 죽는 속도다.
    /// **거리를 늘리려면 이 값보다 drag를 보는 편이 낫다.**
    ///
    /// 0이면 제자리에 가만히 나타난다.
    /// </summary>
    public float entrySpeed = 400f;

    /// <summary>
    /// 다음 배가 뜰 때까지의 틱(60틱 = 1초). 한 번에 다 뜨면 셋이 한 덩어리로 보여서
    /// 몇 척인지가 안 읽힌다. 0이면 예전처럼 동시에 뜬다.
    ///
    /// **읽히는 간격은 이 값이 아니라 "이 값 × <see cref="entrySpeed"/>"다.** 900 m/s에서
    /// 24틱(0.4초)은 330 m 차이인데, 그 속도로 화면을 지나가면 거의 동시로 보인다.
    /// 90틱이면 1350 m라 한 척씩 따로 들어오는 것이 보인다. 속도를 올리면 이 값도
    /// 같이 올려야 그림이 유지된다.
    /// </summary>
    public int entryStaggerTicks = 90;

    private CampaignDef _def;
    private int _sector;
    private int _wait = -1;

    /// <summary>
    /// 아직 안 뜬 이번 구역의 소환. <see cref="OnTick"/>이 하나씩 꺼낸다.
    ///
    /// **이것이 비었는지가 목표 판정에 들어간다** - 표적이 아직 안 떴는데 "표적이 다
    /// 죽었다"로 읽히면 구역이 첫 틱에 끝난다. <c>Battle._sawHostile</c>이 소탕 목표에
    /// 대해 막아주는 것과 같은 구멍이고, 표적 목표에는 그 걸쇠가 없다.
    /// </summary>
    private readonly Queue<SpawnDef> _toSpawn = new();

    private int _spawnWait;

    /// <summary>
    /// 막간이 끝났고 다음 구역까지 날아가는 중. <see cref="Arrived"/>가 참이 될 때까지
    /// 매틱 검사한다 - 반경이 아니라 **선**이다. 근접 반경으로 재면 부스터로 한 틱에
    /// 반경을 통째로 건너뛸 수 있고(마하 1, 반경 수백 유닛), 그러면 도착을 영영 못 잡는다.
    /// x축 문턱은 한 번 넘으면 그 뒤로 계속 참이라 통째로 건너뛰어도 그 틱에 걸린다.
    /// </summary>
    private bool _traveling;

    /// <summary>
    /// 이번 구역의 표적. 비어 있으면 목표는 "적이 없다"이고, 차 있으면 "이것들이 다 죽었다"다.
    /// <see cref="Battle.objective"/>에 대입할 술어가 이 목록을 읽는다.
    /// </summary>
    private readonly List<HullStructure> _targets = new();

    /// <summary>이번 구역이 소환한 것 전부. 다음 구역으로 넘어갈 때 걷어낸다.</summary>
    private readonly List<GameObject> _spawned = new();

    private SectorDef Current =>
        _def != null && _sector >= 0 && _sector < _def.sectors.Count ? _def.sectors[_sector] : null;
        
    private Battle _battle;

    private void Awake()
    {
        current = this;
        _def = CampaignDef.Load();
        RunState.ValidateOrClear();
        _sector = Mathf.Clamp(RunState.Sector, 0, (_def?.sectors.Count ?? 1) - 1);

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
    /// </summary>
    public void StartRun()
    {
        if (_def == null || _runStarted)
            return;

        _runStarted = true;

        // 씬에 손으로 놓아둔 적은 캠페인의 것이 아니다. 남겨두면 1구역이 실제로 무엇인지가
        // 씬과 def 두 곳에 적히고, 그 둘은 반드시 어긋난다.
        ClearHostiles();
        Begin();
    }

    public override void OnTick()
    {
        if (_wait >= 0)
        {
            if (--_wait > 0) return;
            _wait = -1;

            // 구역이 더 없으면(런 클리어) 날아갈 곳도 없다 - 바로 끝을 알린다.
            if (Current == null)
            {
                Begin();
                return;
            }

            _traveling = true;
            return;      // 막간이 먼저. Begin()이 방금 만든 _battle을 같은 틱에 안 돌린다
        }

        if (_traveling)
        {
            if (!Arrived()) return;
            _traveling = false;
            Begin();
            return;
        }

        // 전투보다 먼저. 이번 틱에 뜬 배가 같은 틱의 목표 판정에 들어간다 - 반대로 두면
        // 마지막 한 척이 뜨기 직전 틱에 "적이 없다"가 될 창이 열린다.
        DrainSpawnQueue();

        _battle?.Tick();
    }

    /// <summary>
    /// 대기 중인 소환을 <see cref="entryStaggerTicks"/> 간격으로 하나씩 꺼낸다.
    /// 간격이 0이면 이 틱에 전부 꺼낸다 - 예전 동작 그대로다.
    /// </summary>
    private void DrainSpawnQueue()
    {
        if (_toSpawn.Count == 0)
            return;

        if (--_spawnWait > 0)
            return;

        do
        {
            Spawn(_toSpawn.Dequeue());
        }
        while (entryStaggerTicks <= 0 && _toSpawn.Count > 0);

        _spawnWait = Mathf.Max(1, entryStaggerTicks);
    }

    /// <summary>
    /// 다음 구역 스폰 중 제일 가까운 x보다 <see cref="arrivalBuffer"/>만큼 앞에 왔는가.
    /// 구역 배치가 전부 +x 방향으로 벌어져 있다는 전제다 - y축으로 크게 벗어나 지나가도
    /// x 문턱만 넘으면 도착으로 본다.
    /// </summary>
    private bool Arrived()
    {
        SectorDef sector = Current;

        if (sector == null || sector.spawns.Count == 0)
            return true;

        Ship player = PlayerShip();

        if (player == null)
            return false;

        float arriveX = float.MaxValue;

        // **아군은 도착 좌표를 안 정한다.** 도착하는 곳은 적이 있는 자리지 호위가 서는
        // 자리가 아니다. 같이 세면 구역 뒤쪽에 세운 호위 한 척이 min을 끌어내려서, 적이
        // 아직 한참 앞인데 구역이 열린다 - 증상이 "적이 멀리서 갑자기 생긴다"뿐이라
        // 배치 실수인지 도착 판정 버그인지 안 갈린다.
        foreach (SpawnDef spawn in sector.spawns)
            if (spawn.Side != Ship.Team.Ally)
                arriveX = Mathf.Min(arriveX, spawn.x);

        // 적이 하나도 없는 구역(호위만 있는 막간)은 날아갈 곳이 없다.
        if (arriveX == float.MaxValue)
            return true;

        // +x로 접근할 때만 속도를 완충에 반영한다. 뒷걸음질이나 옆으로 미끄러지는 속도로
        // 완충을 늘리면 오히려 정지 상태보다 늦게 뜬다 - 여기서 볼 것은 "얼마나 빨리
        // 그 좌표에 닿는가"뿐이다.
        float approachSpeed = Mathf.Max(0f, player.velocity.x);
        float buffer = Mathf.Max(arrivalBuffer, approachSpeed * reactionSeconds);

        return player.transform.position.x >= arriveX - buffer;
    }

    /// <summary>
    /// 이번 구역을 연다. 소환하고, 대본을 틀고, 새 <see cref="Battle"/>을 세운다.
    ///
    /// Battle을 새로 만드는 이유는 <c>Ended</c>가 한 번 켜지면 안 꺼지기 때문이다. 목표
    /// 술어를 갈아끼우는 자리도 여기다 - 8구역은 "적이 없다"가 아니라 "거울이 죽었다"이고,
    /// 그것은 다른 함수를 대입하는 것일 뿐 틱 루프를 안 건드린다.
    /// </summary>
    private void Begin()
    {
        SectorDef sector = Current;

        // 구역이 더 없다 = 런을 깼다. **끝을 아는 자리는 여기 하나다.**
        if (sector == null)
        {
            Debug.Log("[Campaign] 초복사 시설 절단. 런 클리어.");
            ScriptManager.current?.Play("run-cleared");
            return;
        }

        // 지난 구역이 남긴 것을 걷는다. 플레이어 배는 여기 안 들어 있다 - 손상을 안고 가는
        // 것이 이 게임의 규칙이고, 걷는 것은 이 캠페인이 소환한 것뿐이다.
        foreach (GameObject old in _spawned)
        {
            if (old != null)
                Destroy(old);
        }

        _targets.Clear();
        _spawned.Clear();
        _toSpawn.Clear();

        // 수리는 더 이상 여기서 자동으로 안 일어난다. MTRL을 얼마나 쓸지는 플레이어가
        // 정한다 - Ship.RepairPlates(int)가 그 실행부고, 부를 자리는 Logistics UI다.

        // **바로 안 뜬다.** 큐에 넣고 OnTick이 시차를 두고 꺼낸다 - 셋이 한 덩어리로
        // 뜨면 몇 척인지가 안 읽힌다.
        foreach (SpawnDef spawn in sector.spawns)
            _toSpawn.Enqueue(spawn);

        _spawnWait = 1;   // 다음 틱에 첫 척

        _battle = new Battle();

        // **def가 정한다. 이미 뜬 것을 세면 안 된다** - 시차 소환이라 지금은 하나도
        // 안 떠 있고, `_targets.Count > 0`으로 보면 8구역이 소탕 목표로 떨어진다.
        // 그러면 거울을 안 부수고 호위만 잡아도 구역이 끝난다.
        if (HasTarget(sector))
            _battle.objective = TargetsDown;

        // 대본(script 필드)보다 먼저 적는다 - 진입 사건에 반응하는 대사가 구역 인트로
        // 대본과 겹치면 인트로가 이긴다(같은 프레임이면 나중 것이 난입이다).
        RunLog.SectorEntered(_sector + 1);

        if (!string.IsNullOrEmpty(sector.script))
            ScriptManager.current?.Play(sector.script);

        Debug.Log(
            $"[Campaign] {_sector + 1}구역 '{sector.name}' - {sector.spawns.Count}척, " +
            $"목표 {(HasTarget(sector) ? "표적 절단" : "적 소탕")}");
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
    private static bool HasTarget(SectorDef sector)
    {
        foreach (SpawnDef spawn in sector.spawns)
        {
            if (spawn.target)
                return true;
        }

        return false;
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
        if (!battle.Won)
        {
            // 진 것은 런이 끝난 것이다. Battle이 이미 RunState.Clear를 불렀고, 그 안에서
            // 구역도 0으로 돌아간다 - 다음 함장은 1구역부터다.
            Debug.Log("[Campaign] 런 종료. 다음 함장은 처음부터.");
            return;
        }

        // **잔해를 걷어내기 전에 센다.** Begin이 지난 구역의 소환물을 지우므로 여기가
        // 마지막 기회다.
        SalvageResult recovered = ComputeSalvage();

        if (!recovered.IsEmpty)
        {
            RunState.Materials += recovered.materials;
            RunState.Propellant += recovered.propellant;
            RunState.Munitions += recovered.munitions;

            Debug.Log(
                $"[Campaign] 노획 MTRL +{recovered.materials} PROP +{recovered.propellant} " +
                $"MUN +{recovered.munitions} (누적 MTRL {RunState.Materials} " +
                $"PROP {RunState.Propellant} MUN {RunState.Munitions}).");
        }

        // **잔해를 걷기 전에 센다** - 노획과 같은 이유로 여기가 마지막 기회다.
        BuryWingmen();

        RunLog.SectorCleared(_sector + 1);

        _sector++;
        RunState.Sector = _sector;

        _battle = null;

        // **마지막 구역이었어도 같은 길로 간다.** Begin이 "구역이 더 없다"를 이미 알고
        // 있으므로 여기서 또 세면 끝을 아는 자리가 둘이 된다.
        //
        // 다음 틱들에 걸쳐서 넘어가는 이유는 따로 있다. 여기는 Battle.OnTick 한가운데고,
        // TickManager가 목록을 훑는 도중에 TickBehaviour를 지우고 새로 다는 것이라 한 박자
        // 쉬어야 한다. 덤으로 승리 대사가 먼저 나오고 다음 구역 대사가 그 뒤에 겹친다 -
        // 여기서 바로 틀면 Campaign이 Awake, ScriptManager가 Awake이라 순서가
        // 뒤집혀서 마무리 대사가 교전 종료 보고보다 먼저 나온다.
        _wait = Mathf.Max(1, interludeTicks);
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

        public SalvageResult(int materials, int propellant, int munitions)
        {
            this.materials = materials;
            this.propellant = propellant;
            this.munitions = munitions;
        }

        public bool IsEmpty => materials <= 0 && propellant <= 0 && munitions <= 0;
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

        foreach (GameObject wreck in _spawned)
        {
            if (wreck == null)
                continue;

            // Hulk(시설)와 동료는 회수 대상이 아니다. Ship이면서 _wingmen에 없는 것만 본다.
            if (!wreck.TryGetComponent(out Ship ship) || _wingmen.Contains(ship))
                continue;

            if (wreck.TryGetComponent(out HullStructure structure))
                materials += structure.AliveCount;

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
            Mathf.RoundToInt(munitions));
    }

    /// <summary>
    /// 이번 구역에 살아 있는 동료들. 인덱스가 <see cref="RunState.Wingmen"/>의 것과 같아서,
    /// 전투가 끝날 때 누가 죽었는지 그 자리로 지운다 - 같은 설계도가 둘일 수 있으므로
    /// 이름으로 지우면 엉뚱한 쪽이 빠진다.
    /// </summary>
    private readonly List<Ship> _wingmen = new();

    /// <summary>
    /// 합류한 아군을 구역마다 다시 소환한다. **손상은 안 들고 온다** - 멀쩡한 몸으로
    /// 나오고, 대신 죽으면 명단에서 빠져 다음 구역부터 영영 없다.
    ///
    /// 조종은 편대에 묶고 사격은 안 건드린다. `_detatchBrain`이 켜져 있으면 ShipAi가
    /// 스스로 표적을 안 고르므로 조타가 편대 자리에 붙고, 포탑은 원래부터 ShipAi를 안
    /// 거치고 <c>Gun</c>이 직접 <c>NearestHostile</c>을 잡는다 - 그래서 "따라다니되
    /// 알아서 쏘는" 것이 분기 하나 없이 나온다.
    /// </summary>

    /// <summary>전투가 끝났다. 못 싸우게 된 동료는 명단에서 뺀다 - 뒤에서부터 지워야 인덱스가 안 밀린다.</summary>
    private void BuryWingmen()
    {
        for (int i = _wingmen.Count - 1; i >= 0; i--)
        {
            Ship ship = _wingmen[i];

            if (ship == null || !ship.IsCombatEffective)
            {
                Debug.Log($"[Campaign] 동료 {i + 1}번 상실. 남은 구역은 그만큼 혼자다.");
                RunState.Lose(i);
            }
        }

        _wingmen.Clear();
    }
    Ship _playerShip => PlayerShip();
    private Ship PlayerShip()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Ship.All[i] != null && Ship.All[i].IsPlayerControlled)
                return Ship.All[i];
        }

        return null;
    }

    private Ship Spawn(SpawnDef spawn)
    {
        if (string.IsNullOrEmpty(spawn.ship))
            return null;

        if (!File.Exists(ShipDef.PathOf(spawn.ship)))
        {
            Debug.LogError($"[Campaign] '{spawn.ship}' 설계도가 없다. 건너뛴다.");
            return null;
        }

        // 비활성으로 만들고 마지막에 켠다. AddComponent는 오브젝트가 활성이면 Awake를
        // 즉시 부르는데, Ship.Awake가 shipDefName을 읽어 배를 통째로 짓는다.
        var go = new GameObject(spawn.ship);
        go.SetActive(false);

        // 잔해, 거울(hulk)은 워프하지 않는다 - 시설은 원래 거기 있던 것이다. 날아가면 질량 무기고. 근데 질량 무기 컨셉도 괜찮은 것 같긴 함.
        float startX = spawn.hulk ? spawn.x : spawn.x + -entryDistance*spawn.facing;
        go.transform.position = new Vector3(startX, spawn.y, 0f);
        go.transform.localScale =
            new Vector3(spawn.facing < 0f ? -1f : 1f, 1f, 1f);

        if (spawn.hulk)
        {
            var hulk = go.AddComponent<Hulk>();
            hulk.structureDefName = spawn.ship;

            go.SetActive(true);
            _spawned.Add(go);

            return null;
        }
        else
        {
            var ship = go.AddComponent<Ship>();
            ship.shipDefName = spawn.ship;
            ship.team = spawn.Side;

            // 조종하는 것이 붙어야 배가 움직인다.
            var ai = go.AddComponent<ShipAi>();

            go.SetActive(true);
            _spawned.Add(go);

            if(spawn.isWingman)
            {
                int pingpong(int x) => ((x & 2) == 2) ? x >> 1 : -x >> 1;

                _wingmen.Add(ship);
                ai._detatchBrain = true;
                ai._formation = _playerShip.transform;
                ai._formationOffset = //기본 델타형
                -_playerShip.NoseDirection * (ship.DesignMap.width + 35) * _wingmen.Count/2 +
                _playerShip.PortDirection * (ship.DesignMap.height + 35) * pingpong(_wingmen.Count);
            }

            // 워프에서 남은 속도. 한 번 주고 만다.
            // SetActive 이후여야 Ship.Awake에서 Rigidbody2D가 생성되어 있다.
            if (go.TryGetComponent(out Rigidbody2D rig))
            {
                rig.collisionDetectionMode = CollisionDetectionMode2D.Discrete; //명시
                rig.linearVelocity = new Vector2(entrySpeed * spawn.facing, 0f);
                StartCoroutine(ResetVelocity(rig, 20));
            }

            if (spawn.target &&
                go.TryGetComponent(out HullStructure structure))
            {
                _targets.Add(structure);
            }

            return ship;
        }
    }

    IEnumerator ResetVelocity(Rigidbody2D rig, int tick)
    {
        long start = TickManager.currentTick;
        while(start + tick > TickManager.currentTick)
        {
            yield return null;
        }
        rig.linearVelocity = Vector2.zero;
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
