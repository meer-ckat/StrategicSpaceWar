using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Core;

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

    private CampaignDef _def;
    private int _sector;
    private int _wait = -1;

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

    public SectorDef Current =>
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

        _battle?.Tick();
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

        foreach (SpawnDef spawn in sector.spawns)
            arriveX = Mathf.Min(arriveX, spawn.x);

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
            StoryScriptManager.current?.Play("run-cleared");
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

        Repair();

        foreach (SpawnDef spawn in sector.spawns)
            Spawn(spawn);

        SpawnWingmen();

        _battle = new Battle();

        // Awake가 이미 NoHostilesLeft를 넣었다. 표적이 있는 구역에서만 덮는다 -
        // ??= 가 아니라 대입이라 순서가 문제되지 않는다.
        if (_targets.Count > 0)
            _battle.objective = TargetsDown;

        if (!string.IsNullOrEmpty(sector.script))
            StoryScriptManager.current?.Play(sector.script);

        Debug.Log(
            $"[Campaign] {_sector + 1}구역 '{sector.name}' - {sector.spawns.Count}척, " +
            $"목표 {(_targets.Count > 0 ? $"표적 {_targets.Count}개 절단" : "적 소탕")}");
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
        int taken = CountSalvage();

        if (taken > 0)
        {
            RunState.Salvage += taken;
            Debug.Log($"[Campaign] 노획 판 {taken}장 (누적 {RunState.Salvage}).");
        }

        // **잔해를 걷기 전에 센다** - 노획과 같은 이유로 여기가 마지막 기회다.
        BuryWingmen();

        _sector++;
        RunState.Sector = _sector;

        _battle = null;

        // **마지막 구역이었어도 같은 길로 간다.** Begin이 "구역이 더 없다"를 이미 알고
        // 있으므로 여기서 또 세면 끝을 아는 자리가 둘이 된다.
        //
        // 다음 틱들에 걸쳐서 넘어가는 이유는 따로 있다. 여기는 Battle.OnTick 한가운데고,
        // TickManager가 목록을 훑는 도중에 TickBehaviour를 지우고 새로 다는 것이라 한 박자
        // 쉬어야 한다. 덤으로 승리 대사가 먼저 나오고 다음 구역 대사가 그 뒤에 겹친다 -
        // 여기서 바로 틀면 Campaign이 Awake, StoryScriptManager가 OnEnable이라 순서가
        // 뒤집혀서 마무리 대사가 교전 종료 보고보다 먼저 나온다.
        _wait = Mathf.Max(1, interludeTicks);
    }

    /// <summary>
    /// 적 선체에 **남아 있는** 판을 센다. 그것이 이 구역에서 뜯어올 수 있는 전부다.
    ///
    /// 새 장부가 없다 - <see cref="HullStructure.AliveCount"/>가 함선에도 잔해에도 이미
    /// 있고, 그 값이 곧 "아직 실물이 있는 칸"이다.
    ///
    /// **떨어져 나간 조각은 안 센다.** 배가 갈라지면 조각은 새 GameObject로 가고 여기
    /// 목록에 없다. 흩어진 것은 못 줍는다는 뜻이고, 그래서 배를 반토막 내면 노획도 반이
    /// 된다 - 충각으로 갈아버리면 아무것도 안 남는 것과 같은 방향이다.
    /// </summary>
    private int CountSalvage()
    {
        int plates = 0;

        foreach (GameObject wreck in _spawned)
        {
            if (wreck != null && wreck.TryGetComponent(out HullStructure structure))
                plates += structure.AliveCount;
        }

        return plates;
    }

    /// <summary>
    /// 노획으로 상한 판을 고친다. 구역에 들어서기 **전**이라, 다음 전투는 고쳐진 배로 한다.
    ///
    /// 남는 노획은 그대로 들고 간다 - 고칠 것이 없어서 못 쓴 것을 버리면, 곱게 이긴 전투가
    /// 보상이 아니라 낭비가 된다.
    /// </summary>
    private void Repair()
    {
        int budget = RunState.Salvage;

        if (budget <= 0)
            return;

        Ship player = null;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Ship.All[i] != null && Ship.All[i].IsPlayerControlled)
                player = Ship.All[i];
        }

        if (player == null)
            return;

        int used = player.RepairPlates(budget);

        if (used <= 0)
            return;

        RunState.Salvage = budget - used;
        Debug.Log($"[Campaign] 판 {used}장 수리. 노획 {RunState.Salvage}장 남음.");
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
    private void SpawnWingmen()
    {
        _wingmen.Clear();

        Ship player = PlayerShip();

        if (player == null)
            return;

        List<RunState.Wingman> roster = RunState.Wingmen;

        for (int i = 0; i < roster.Count; i++)
        {
            RunState.Wingman w = roster[i];

            // 자리를 미리 계산해서 거기 띄운다. 안 그러면 첫 구역 시작마다 동료가
            // 원점에서 편대까지 날아오는 그림이 나온다.
            Vector2 right = player.NoseDirection;
            Vector2 up = new(-right.y, right.x);

            Vector2 at = (Vector2)player.transform.position
                + right * w.slot.x + up * w.slot.y;

            Ship ship = SpawnAlly(w.ship, at, player.transform.localScale.x);

            _wingmen.Add(ship);   // 실패해도 null로 넣는다 - 인덱스가 명단과 같아야 한다

            if (ship != null && ship.TryGetComponent(out ShipAi ai))
            {
                ai._detatchBrain = true;
                ai._formation = player.transform;
                ai._formationOffset = w.slot;
            }
        }
    }

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

    private Ship PlayerShip()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Ship.All[i] != null && Ship.All[i].IsPlayerControlled)
                return Ship.All[i];
        }

        return null;
    }

    /// <summary>아군 한 척. <see cref="Spawn"/>과 같은 규칙(비활성으로 짓고 마지막에 켠다).</summary>
    private Ship SpawnAlly(string shipDef, Vector2 at, float facing)
    {
        if (string.IsNullOrEmpty(shipDef) || !File.Exists(ShipDef.PathOf(shipDef)))
        {
            Debug.LogError($"[Campaign] 동료 '{shipDef}' 설계도가 없다. 건너뛴다.");
            return null;
        }

        var go = new GameObject(shipDef);
        go.SetActive(false);

        go.transform.position = new Vector3(at.x, at.y, 0f);
        go.transform.localScale = new Vector3(facing < 0f ? -1f : 1f, 1f, 1f);

        var ship = go.AddComponent<Ship>();
        ship.shipDefName = shipDef;
        ship.team = Ship.Team.Ally;

        go.AddComponent<ShipAi>();

        go.SetActive(true);
        _spawned.Add(go);   // 다음 구역에 걷히는 것도 캠페인이 소환한 것들과 같다

        return ship;
    }

    private void Spawn(SpawnDef spawn)
    {
        if (string.IsNullOrEmpty(spawn.ship))
            return;

        // 파일만 본다. ShipDef.Load로 확인하면 Ship.Awake가 곧바로 또 읽어서 구역마다
        // 설계도를 두 번 파싱한다. 그리고 여기서 안 막으면 판이 없는 배가 태어나는데,
        // 그 배는 IsCombatEffective가 false라 적으로 안 세어져서 **구역이 영영 안 끝난다.**
        if (!File.Exists(ShipDef.PathOf(spawn.ship)))
        {
            Debug.LogError($"[Campaign] '{spawn.ship}' 설계도가 없다. 건너뛴다.");
            return;
        }

        // **비활성으로 만들고 마지막에 켠다.** AddComponent는 오브젝트가 활성이면 Awake를
        // 즉시 부르는데, Ship.Awake가 shipDefName을 읽어 배를 통째로 짓는다. 그냥 붙이면
        // 이름이 들어가기 전에 지어져서 빈 배가 태어난다. ThingDef.Spawn과 같은 규칙이다.
        var go = new GameObject(spawn.ship);
        go.SetActive(false);

        go.transform.position = new Vector3(spawn.x, spawn.y, 0f);
        go.transform.localScale = new Vector3(spawn.facing < 0f ? -1f : 1f, 1f, 1f);

        if (spawn.hulk)
        {
            var hulk = go.AddComponent<Hulk>();
            hulk.structureDefName = spawn.ship;
        }
        else
        {
            var ship = go.AddComponent<Ship>();
            ship.shipDefName = spawn.ship;
            ship.team = spawn.Side;

            // 조종하는 것이 붙어야 배가 움직인다. PlayerInput이 없으므로 Ship.Awake의
            // IsPlayerControlled는 false가 되고, 그래서 이 배는 RunState를 안 읽는다.
            go.AddComponent<ShipAi>();
        }

        go.SetActive(true);
        _spawned.Add(go);

        if (spawn.target && go.TryGetComponent(out HullStructure structure))
            _targets.Add(structure);
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
