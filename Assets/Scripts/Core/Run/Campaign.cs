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

    private CampaignDef _def;
    private int _sector;
    private int _wait = -1;

    /// <summary>
    /// 이번 구역의 표적. 비어 있으면 목표는 "적이 없다"이고, 차 있으면 "이것들이 다 죽었다"다.
    /// <see cref="Battle.objective"/>에 대입할 술어가 이 목록을 읽는다.
    /// </summary>
    private readonly List<HullStructure> _targets = new();

    /// <summary>이번 구역이 소환한 것 전부. 다음 구역으로 넘어갈 때 걷어낸다.</summary>
    private readonly List<GameObject> _spawned = new();

    public SectorDef Current =>
        _def != null && _sector >= 0 && _sector < _def.sectors.Count ? _def.sectors[_sector] : null;

    private void Awake()
    {
        current = this;
        _def = CampaignDef.Load();
        _sector = Mathf.Clamp(RunState.Sector, 0, (_def?.sectors.Count ?? 1) - 1);

        Battle.onAnyEnd += OnBattleEnd;
    }

    private void OnDestroy()
    {
        Battle.onAnyEnd -= OnBattleEnd;

        if (current == this)
            current = null;
    }

    private void Start()
    {
        if (_def == null)
            return;

        // 씬에 손으로 놓아둔 적은 캠페인의 것이 아니다. 남겨두면 1구역이 실제로 무엇인지가
        // 씬과 def 두 곳에 적히고, 그 둘은 반드시 어긋난다.
        ClearHostiles();
        Begin();
    }

    public override void OnTick()
    {
        if (_wait < 0)
            return;

        if (--_wait > 0)
            return;

        _wait = -1;
        Begin();
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

        var battle = gameObject.AddComponent<Battle>();

        // Awake가 이미 NoHostilesLeft를 넣었다. 표적이 있는 구역에서만 덮는다 -
        // ??= 가 아니라 대입이라 순서가 문제되지 않는다.
        if (_targets.Count > 0)
            battle.objective = TargetsDown;

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

        _sector++;
        RunState.Sector = _sector;

        Destroy(battle);

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
