using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Core;
using System.IO;
using System;

/// <summary>
/// 함선 한 척. **abstract가 아니다** - 함선의 종류는 C# 클래스가 아니라 shipDefName이 정한다.
/// 예전에는 인스턴스화하려고 몸통이 빈 Destroyer 클래스가 있었는데, 그건 새 함선마다 코드를
/// 쓰게 만드는 프리팹 시절의 병이었다. 런이 "적 프리깃 하나"를 소환하려면 종류가 데이터여야 한다.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(HullStructure))]
public partial class Ship : Thing
{
    [Header("기동")]
    public float drag = 0.02f;             // 종단속도 = 가속도 / drag

    [Header("Angling")]
    public float angleAccel = 7.5f;        // 도/초². 함선 관성 모멘트가 여기 녹아 있다
    public float angleDrag = 0.5f;         // 종단 각속도 = angleAccel / angleDrag = 15 도/초
    public float angleBrake = 2f;          // 입력을 놓았을 때의 RCS 역분사

    [Header("구성")]
    public List<Armor> shipArmors = new();
    public List<Engine> shipEngines = new();
    public List<Gun> shipGuns = new();
    public List<CriticalModule> shipCriticals = new();

    /// <summary>
    /// 판 한 장당 질량. 배가 지어진 뒤 판 수를 곱해 Rigidbody2D에 넣는다 - 설계를 바꾸면
    /// 질량이 알아서 따라온다. Hulk의 같은 이름 필드와 같은 뜻이다.
    /// </summary>
    public float massPerPlate = 420f;

    [Header("설계도")]
    /// <summary>
    /// StreamingAssets/Ships/&lt;이름&gt;.json. 채우면 Awake에서 자식을 싹 지우고 JSON대로 짓는다.
    ///
    /// 비워두면 씬에 손으로 지어놓은 자식을 그대로 쓴다 - 그게 export의 원본이다.
    /// 두 원본을 동시에 살려두면 반드시 어긋나므로, 채워져 있으면 JSON이 이긴다.
    /// </summary>
    public string shipDefName;

    [Header("공기")]
    public float leakRate = 2f;    // 파공 1개당 초당 유출량
    public float doorRate = 1f;    // 문 하나의 초당 유량 계수

    [Header("승무원")]
    public int crews;

    /// <summary>
    /// 살아 있는 원자로가 하나라도 있는가. 포탑 선회에도 전기가 들어서 조타와 조준이 같이
    /// 걸린다. 원자로를 아예 안 단 설계(운석·구형 함선)는 전기 걱정이 없는 것으로 친다.
    ///
    /// 이중화는 코드가 아니라 배치다 - def에 원자로를 둘 넣으면 하나 터져도 배가 산다.
    /// </summary>
    public bool HasPower
    {
        get
        {
            for (int i = 0; i < shipCriticals.Count; i++)
            {
                CriticalModule module = shipCriticals[i];

                if (module == null || !module.providesPower || !StillAboard(module, this))
                    continue;

                if (!module.Neutralized)
                    return true;
            }

            // **"지금 목록에 없다"를 세면 안 된다.** 터진 원자로는 판과 함께 잔해로 떠나거나
            // 파괴돼서 목록에서 사라진다. 남은 것을 세는 것으로 판단하면 원자로가 전멸한
            // 배가 "원자로를 안 단 설계"로 읽혀서 전기가 되살아나고, 다 터졌는데 계속
            // 조타하고 조준하는 배가 된다.
            //
            // 설계에 원자로가 있었는지는 Awake가 적어 둔다. 그 사실은 안 변한다.
            return !_needsPower;
        }
    }

    /// <summary>설계에 발전하는 모듈이 하나라도 있었는가. Awake가 한 번 정하고 안 바뀐다.</summary>
    private bool _needsPower;

    /// <summary>
    /// 조타·사격·수리가 되는가. **저장값이 아니라 파생값이다.**
    ///
    /// 예전에는 public bool 셋이었고 Crew()가 껐다. 거기에 원자로까지 끄게 하면 주인이 둘이
    /// 되고, 원자로가 복구된 순간 승무원이 죽었는데도 다시 켜진다. 조건을 읽는 자리를 하나로
    /// 두면 그 버그가 존재할 자리가 없다.
    /// </summary>
    public bool isDriverReady => CrewAlive && HasPower;

    public bool isGunnerReady => CrewAlive && HasPower;

    /// <summary>수리는 사람이 한다. 전기가 나가도 손으로 때운다.</summary>
    public bool isEngineerReady => CrewAlive;

    
    public enum Team
    {
        Neutral,
        Ally,
        Enemy
    }
    [Header("AI")]
    public Team team;
    public float FightDistance; //이 거리를 유지한다는 뜻임. 안으로 계속 들어가면서 공격한다는게 아니고
    public float DetectionDistance; //이 거리에서 발견한다는 뜻임. 여기서 FightDistance까지 들어감.

    // Room은 Unity 직렬화 대상이 아니라 인스펙터에 뜨지 않는다. 런타임 전용.
    public List<Room> rooms = new();

    readonly Dictionary<Door, List<Room>> roomsOfDoor = new();

    // 격자 원본. 방 BFS도, 선체 구조도, 오버레이도 전부 이걸 읽는다.
    ShipGrid.Map _map;

    /// <summary>
    /// 읽기 전용. BuildRooms가 배를 다시 지을 때마다 **새 객체**가 되므로, 참조가 바뀌었는지
    /// 보는 것만으로 "배가 갈라졌다"를 알 수 있다 - RoomView가 그걸로 오버레이를 다시 굽는다.
    /// </summary>
    public ShipGrid.Map Map => _map;

    // 물리가 진실이다. 예전엔 Ship이 velocity를 따로 들고 transform을 직접 옮겼는데,
    // 그러면 충돌이 밀어낸 결과를 다음 틱에 우리가 덮어써서 충각이 성립하지 않는다.
    public Vector2 velocity => rig != null ? rig.linearVelocity : Vector2.zero;
    public float hullAngle => rig != null ? rig.rotation : 0f;
    public float angleRate => rig != null ? rig.angularVelocity : 0f;   // 도/초

    // 입력은 저장만 한다. 계산은 전부 틱 안에서.
    protected Vector2 thrustInput;  // x: 이탈 -1 .. +1 접근, y: 회피
    protected float angleInput;     // -1..1

    /// <summary>
    /// 접근(+x)이 월드의 어느 쪽인가. 왼쪽에서 오른쪽을 보는 플레이어가 +1이고,
    /// 반대편 함선은 ShipAi가 매 틱 -1로 뒤집는다. Drive()만 이 값을 읽는다.
    /// </summary>
    public float engagementSign = 1f;

    Rigidbody2D rig;
    [NonSerialized] private Texture2D shipHullPng;
    public Texture2D ShipHullPng => shipHullPng;

    protected override void Awake()
    {
        base.Awake();
        rig = GetComponent<Rigidbody2D>();

        IsPlayerControlled = GetComponent<PlayerInput>() != null && GetComponent<ShipAi>() == null;

        // RequireComponent는 에디터에서 스크립트를 붙일 때만 채워준다. 이미 저장된 씬의
        // 함선에는 없을 수 있어서, 없으면 여기서 만든다.
        _structure = GetComponent<HullStructure>() ?? gameObject.AddComponent<HullStructure>();

        // 설계도가 제일 먼저다. drag·angleAccel 같은 수치가 def에서 오므로 리지드바디에
        // 옮겨 담기 전에 들어와 있어야 하고, 자식도 여기서 갈아엎으니 목록을 걷기 전이다.
        //
        // 저장된 런이 있으면 그것이 이긴다. 전투 결과를 안고 다음 구역으로 가는 것이
        // 이 게임의 규칙이라, 설계도로 다시 짓는 것은 런이 끝났을 때뿐이다.
        var design = RunShipFor(shipDefName);
        if (ShipBuilder.SpawnFrom(transform, design, this))
        {
            // 인스펙터에 남아 있던 목록은 방금 지운 자식을 가리킨다.
            shipArmors.Clear();
            shipEngines.Clear();
            shipGuns.Clear();
            shipCriticals.Clear();
        }
        if(design!=null&&!string.IsNullOrEmpty(design.hullSkin))
        {
            try{

                byte[] bytes = File.ReadAllBytes(ShipDef.SkinPathOf(design.hullSkin));
                shipHullPng = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                bool ok = shipHullPng.LoadImage(bytes);
                if(!ok)
                {
                    Destroy(shipHullPng);
                    shipHullPng = null;
                    Debug.LogAssertion("I'm not fucking ok. IM NOT FUCKING OK. FIX. I couldnt load the image, and i fired from cpu. fuck it.");
                }
            }
            catch(Exception e)
            {
                Debug.LogAssertion("Hell ye: " + e);
            }
        }

        rig.bodyType = RigidbodyType2D.Dynamic;
        rig.gravityScale = 0f;
        // 항력을 물리에 넘긴다. 종단속도는 그대로 추력 / (질량 x drag).
        rig.linearDamping = drag;

        // 각감쇠는 0. 회전 제동은 Angle()의 RCS가 직접 하고, 그래야 충돌이 준 회전을
        // 물리가 먼저 갉아먹지 않는다.
        rig.angularDamping = 0f;

        if (shipArmors.Count == 0) shipArmors = new List<Armor>(GetComponentsInChildren<Armor>());
        if (shipEngines.Count == 0) shipEngines = new List<Engine>(GetComponentsInChildren<Engine>());
        if (shipGuns.Count == 0) shipGuns = new List<Gun>(GetComponentsInChildren<Gun>());
        if (shipCriticals.Count == 0)
            shipCriticals = new List<CriticalModule>(GetComponentsInChildren<CriticalModule>());

        // **설계에 원자로가 있었는가.** 지금 목록을 세는 것으로는 이 질문에 답할 수 없다 -
        // 터진 원자로는 판과 함께 잔해로 떠나거나 파괴돼서 목록에서 사라지고, 그러면
        // "원자로를 아예 안 단 설계"와 글자 그대로 같아 보인다. 설계 사실은 안 변하므로
        // 여기서 한 번만 적어 둔다.
        _needsPower = false;

        for (int i = 0; i < shipCriticals.Count; i++)
        {
            if (shipCriticals[i] != null && shipCriticals[i].providesPower)
            {
                _needsPower = true;
                break;
            }
        }

        BuildRooms();

        // 질량을 판 수에서 뽑는다. 손으로 맞추면 설계를 바꿀 때마다 잊고, 세 척에 같은 값을
        // 적어두면 작은 배가 큰 배만큼 굼떠진다 - 정찰함이 빨라야 하는 이유가 이것이다.
        rig.mass = Mathf.Max(1f, shipArmors.Count * massPerPlate);

        _wasEffective = IsCombatEffective;
    }

    /// <summary>
    /// 플레이어 함선이고 저장된 런이 있으면 그 def를, 아니면 설계도를 돌려준다.
    ///
    /// AI 함선은 항상 설계도다 - 적은 매 전투 새로 나온다. 손상을 들고 가는 것은
    /// 플레이어 한 척뿐이고, 그게 이 게임에서 배 한 척이 특별한 유일한 자리다.
    /// </summary>
    private ShipDef RunShipFor(string designName)
    {
        ShipDef design = string.IsNullOrEmpty(designName) ? null : ShipDef.Load(designName);

        if (!IsPlayerControlled)
            return design;

        ShipDef saved = RunState.Load();

        return saved ?? design;
    }

    public override void OnTick()
    {
        // 물리 콜백 밖에서, 이번 틱의 힘을 걸기 전에. 재부모화가 안전한 유일한 자리다.
        SplitIfBroken();

        // 지난 틱에 닿은 곳을 지금 부순다. Simulate보다 앞이라, 솔버는 살아남은 판만 본다.
        Ram();

        if (isDriverReady) { Angle(); Drive(); }
        if (isGunnerReady) AimGun();
        Atmosphere();
        Crew();
        WatchForCritical();
    }

    /// <summary>
    /// 승무원은 기압으로 산다. 살 만한 방이 하나도 안 남으면 죽고, 그 순간 조타·사격·수리가
    /// 전부 멎는다 - 배는 표류하는 잔해가 된다.
    ///
    /// 이것이 이 게임의 격파 판정 전부다. 함선 HP도, 폭발 연출도, "격침" 이벤트도 없다.
    /// 이미 도는 기압 시뮬레이션이 이미 있던 세 플래그를 끄는 것뿐이고, 나머지 배선은
    /// 원래부터 그 플래그를 보고 있었다 (Gun.OnTick의 owner.isGunnerReady 등).
    /// </summary>
    void Crew()
    {
        if (!CrewAlive)
            return;

        // 맵이 없는 함선은 방 자체가 없다. 기압 모델이 없는 것이지 진공인 것이 아니다.
        if (rooms.Count == 0)
            return;

        foreach (Room room in rooms)
        {
            if (room.Pressure >= Ballistics.CrewMinPressure)
                return;
        }

        // 되돌릴 수 없다. 재가압해도 죽은 사람은 안 돌아온다 - 그래야 결과가 결과로 남는다.
        // 세 준비 플래그는 여기서 안 건드린다. CrewAlive에서 파생되므로 저절로 꺼진다.
        CrewAlive = false;

        // 조종간을 놓은 채로 마지막 입력이 남아 있으면 시체가 계속 가속한다.
        thrustInput = Vector2.zero;
        angleInput = 0f;

        // CrewAlive가 걸쇠라 이 자리는 배 한 척당 정확히 한 번이다.
        RunLog.CrewLost(this);
    }

    /// <summary>
    /// 전투불능이 되는 **순간**을 잡는다. IsCombatEffective는 파생값이라 아무도 보고 있지
    /// 않았다 - 승무원이 질식했든, 원자로가 다 나갔든, 포탑이 전멸했든 여기서 한 번 울린다.
    ///
    /// 걸쇠가 필요한 이유: 파생값이라 다음 틱에도 계속 false다. 상태가 아니라 전이가 사건이다.
    /// </summary>
    private void WatchForCritical()
    {
        bool effective = IsCombatEffective;

        if (_wasEffective && !effective)
        {
            SoundManager.AudioShot("Critical", transform.position);

            // 런 기록도 여기서 적는다. **이 걸쇠가 이미 상태를 사건으로 바꿔 놓았기
            // 때문이다** - IsCombatEffective를 매 틱 읽으면 같은 죽음을 60번 적고,
            // 원자로를 수리해 되살아난 배의 죽음까지 남는다.
            RunLog.Finished(this);
        }

        _wasEffective = effective;
    }

    // 처음부터 무장도 기관도 없는 배(잔해로 시작하는 것)가 첫 틱에 경보를 울리지 않게,
    // 배가 다 지어진 뒤의 실제 상태로 시작한다.
    private bool _wasEffective = true;

    /// <summary>한 번 죽으면 끝. Crew()만 이 값을 내린다.</summary>
    public bool CrewAlive { get; private set; } = true;

    /// <summary>
    /// 지금 쏠 수 있는 포탑이 하나라도 있는가.
    ///
    /// **`StillAboard`가 여기 있어야 한다.** 포탑이 잔해에 실려 100 m 밖으로 날아가도
    /// <see cref="shipGuns"/>의 참조는 그대로 살아 있어서, null도 아니고 Neutralized도
    /// 아니다. 안 거르면 판 세 장짜리 조각이 "우리 배엔 아직 주포가 있다"고 말한다.
    ///
    /// 읽는 쪽이 둘이라 프로퍼티다 - <see cref="IsCombatEffective"/>는 "아직 적인가"를,
    /// <see cref="ShipAi"/>는 "거리를 둘 것인가 들이받을 것인가"를 이 값 하나로 정한다.
    /// 두 벌로 두면 언젠가 한쪽만 고친다.
    /// </summary>
    public bool HasUsableGun
    {
        get
        {
            for (int i = 0; i < shipGuns.Count; i++)
            {
                if (shipGuns[i] != null && !shipGuns[i].Neutralized
                    && StillAboard(shipGuns[i], this))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 아직 상대할 가치가 있는가. 저장된 상태가 아니라 파생값이다 - 쏠 수도, 움직일 수도,
    /// 들이받을 수도 없는 배가 잔해다. AI가 시체를 계속 쏘지 않게 하는 것이 이 값의 일이다.
    /// </summary>
    public bool IsCombatEffective
    {
        get
        {
            // 전기가 없으면 겨누지도 돌리지도 못한다. 포탑이 멀쩡해도 잔해다.
            if (!CrewAlive || !HasPower)
                return false;

            if (HasUsableGun)
                return true;

            // 포탑이 다 죽어도 움직일 수 있으면 충각이 남아 있다.
            return AvailableThrust(true) > 0f || AvailableThrust(false) > 0f;
        }
    }

    /// <summary>
    /// 자식 판들의 위치에서 격자를 짓고 구획을 나눈다. 별도의 맵 파일이 없다 - 씬에 있는 것이
    /// 곧 맵이라, 손으로 판 하나를 옮기면 방도 따라온다. 어긋날 두 번째 원본이 없다.
    /// </summary>
    void BuildRooms()
    {
        // 다시 짓기 **전에** 들고 있던 격자와 방을 잡아 둔다. 이 둘이 있어야 옛 기압을
        // 새 방으로 옮길 수 있다 - 없으면(= 처음 짓는 배) 새 방이 만 기압으로 태어난다.
        ShipGrid.Map old = _map;
        List<Room> oldRooms = rooms;

        rooms = new List<Room>();
        roomsOfDoor.Clear();

        var armorAt = new Dictionary<Vector2Int, Armor>();
        var doorAt = new Dictionary<Vector2Int, Door>();

        _map = ShipBuilder.Stamp(transform, armorAt, doorAt);

        if (_map == null)
        {
            Debug.LogWarning($"[{name}] 판이 하나도 없다. 방·기압 계산을 건너뛴다.");
            return;
        }

        rooms = ShipGrid.BuildRooms(_map, armorAt, doorAt);

        // 파단으로 다시 지은 것이면 진공은 진공으로 남아야 한다.
        ShipGrid.CarryAir(old, oldRooms, _map, rooms);

        _structure.Build(_map, breakawaySpeed);

        // 문 -> 접한 방들. 틱마다 다시 뒤지지 않으려고 여기서 한 번만 만든다.
        foreach (Room room in rooms)
        foreach (Door door in room.doors)
        {
            if (!roomsOfDoor.TryGetValue(door, out List<Room> touching))
                roomsOfDoor[door] = touching = new List<Room>();

            touching.Add(room);
        }
    }

    /// <summary>파공은 우주로 새고, 열린 문은 기압차만큼 옆방과 주고받는다.</summary>
    void Atmosphere()
    {
        float dt = TickManager.TickDeltaTime;

        foreach (Room room in rooms)
        {
            int breaches = 0;

            int standing = 0;

            foreach (Armor wall in room.walls)
            {
                if (wall == null)
                    continue;

                standing++;

                if (wall.AnyBreached)
                    breaches++;
            }

            // 맵이 둘러주기로 한 판 중 없어진 만큼은 통째로 구멍이다. 부서져 사라졌든
            // 선체째 떨어져 나갔든 방 입장에서는 똑같이 우주로 열린 것이다.
            breaches += room.boundaryPlates - standing;

            if (breaches > 0)
                room.air = Mathf.Max(0f, room.air - breaches * leakRate * dt);
        }

        foreach (KeyValuePair<Door, List<Room>> pair in roomsOfDoor)
        {
            // 방 하나만 접한 문은 선체 에어락이다. 진공 배출은 아직 다루지 않으므로 건너뛴다.
            if (pair.Value.Count != 2 || pair.Key == null || !pair.Key.open)
                continue;

            Room a = pair.Value[0];
            Room b = pair.Value[1];

            // 어느 쪽도 음수가 되지 않게 - 한 틱에 방 하나를 통째로 비울 수는 없다
            float flow = Mathf.Clamp((a.Pressure - b.Pressure) * doorRate * dt, -b.air, a.air);

            a.air -= flow;
            b.air += flow;
        }
    }

    /// <summary>
    /// 부서지지 않은 엔진의 출력 합. kN.
    /// forward = 주기관(전진), 그 외 = 보조추진기(후진·측면 회피).
    /// </summary>
    public float AvailableThrust(bool forward)
    {
        float total = 0f;

        foreach (Engine engine in shipEngines)
        {
            if (!StillAboard(engine, this) || engine.Neutralized)
                continue;

            total += forward ? engine.MaxPower : engine.MaxReversePower;
        }

        return total;
    }

    /// <summary>
    /// 이 부품이 아직 이 배의 것인가.
    ///
    /// null 검사로는 안 된다. 모듈은 자기가 올라앉은 판과 함께 잔해로 재부모화되는데,
    /// Awake에 캐시해 둔 Ship 참조도 shipEngines 목록도 그대로 살아 있다. 그냥 두면
    /// 배가 100m 뒤에 떠 있는 엔진으로 계속 가속하고, 날아간 포탑이 본체 포수의 명령을 받는다.
    /// </summary>
    public static bool StillAboard(Component part, Ship ship)
        => part != null && ship != null && part.GetComponentInParent<Ship>() == ship;

    /// <summary>
    /// 추력은 함체 방향과 무관하게 월드 축으로 작용한다. 자세는 Angle()이 따로 제어한다.
    /// 실제 우주선의 RCS와 같은 구조 - 옆으로 미끄러지면서 등을 보일 수 있다.
    /// x는 적과의 거리, y는 사선에서 비켜나는 회피축이다.
    /// </summary>
    protected void Drive()
    {
        float main = AvailableThrust(true);
        float aux = AvailableThrust(false);

        // thrustInput.x는 월드 축이 아니라 '접근/이탈'이다. 적이 왼쪽에 있는 함선은
        // 접근이 월드 -x라, engagementSign 없이 그냥 밀면 주기관으로 도망가고
        // 보조추진기로 다가간다. 플레이어(왼쪽, +1)는 예전과 완전히 동일하다.
        float along = thrustInput.x * engagementSign;

        // 회피는 보조추진기로만 한다 - 접근보다 약한 것이 의도다.
        Vector2 force = new Vector2(
            (thrustInput.x >= 0f ? main : aux) * along,
            aux * thrustInput.y) * 1000f;   // kN -> N

        // 충각이 읽는다. 유리에 대고 가속하는 것도 충각이라, 속도가 아니라 힘이 예산이 된다.
        _thrust = force;

        // 적분도 항력도 물리가 한다. 이 틱 끝의 Simulate에서 한꺼번에 처리된다.
        rig.AddForce(force);
    }

    /// <summary>
    /// 회전에도 관성이 있다. 입력을 놓으면 즉시 멈추지 않고 RCS 역분사로 감속한다.
    /// 이동과 달리 제동을 붙인 이유는, 각도가 정밀 조작이기 때문이다 - 원하는 각을
    /// 잡고 유지하지 못하면 Angling은 기능이 아니라 사고다.
    /// </summary>
    protected void Angle()
    {
        float dt = TickManager.TickDeltaTime;

        // 물리가 준 회전을 물려받고 시작한다. 들이받으면 배가 돌아야 하고, RCS는 그걸
        // 덮어쓰는 게 아니라 되잡는 것이다.
        float rate = rig.angularVelocity;

        rate += angleInput * angleAccel * dt;
        rate *= 1 - (angleInput == 0f ? angleBrake : angleDrag) * dt;

        rig.angularVelocity = rate;
    }

    /// <summary>
    /// 사람이 모는 배인가. PlayerInput이 붙어 있느냐가 전부다 - OnMove/OnAngle이 그 컴포넌트를
    /// 통해서만 들어오므로, 없으면 정의상 AI다. 포탑이 커서를 볼지 적을 볼지를 이걸로 정한다.
    ///
    /// Awake에서 한 번만 본다. 매 틱 GetComponent를 부르면 포탑 수만큼 곱해진다.
    /// </summary>
    public bool IsPlayerControlled { get; private set; }

    // PlayerInput이 붙은 프리팹에서만 호출된다.
    // AI 함선은 컴포넌트가 없으므로 두 필드를 직접 세팅하면 된다.
    public void OnMove(InputValue v)  => thrustInput = v.Get<Vector2>();
    public void OnAngle(InputValue v) => angleInput  = v.Get<float>();

    /// <summary>
    /// AI가 조종간을 잡는 자리. PlayerInput의 OnMove/OnAngle과 같은 문으로 들어오므로,
    /// 플레이어와 AI가 서로 다른 물리를 타는 일이 생기지 않는다.
    /// </summary>
    public void SetPilotInput(Vector2 thrust, float angle)
    {
        thrustInput = thrust;
        angleInput = angle;
    }

    /// <summary>살아 있는 함선 전부. 적을 찾을 때마다 씬을 뒤지지 않으려고 여기서 센다.</summary>
    public static readonly List<Ship> All = new();

    protected override void OnEnable()
    {
        base.OnEnable();
        All.Add(this);
    }

    protected override void OnDisable()
    {
        All.Remove(this);
        base.OnDisable();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        Destroy(shipHullPng);
    }

    /// <summary>
    /// Neutral은 아무와도 싸우지 않는다. 인스펙터에서 팀 지정을 잊었을 때 조용히 아군이
    /// 되는 것보다, 아무도 안 쏘는 쪽이 눈에 띈다.
    /// </summary>
    public bool IsHostileTo(Ship other)
        => other != null
        && other != this
        && team != Team.Neutral
        && other.team != Team.Neutral
        && team != other.team
        && other.IsCombatEffective;   // 잔해는 표적이 아니다

    /// <summary>DetectionDistance 안에서 가장 가까운 적. 없으면 null.</summary>
    public Ship NearestHostile()
    {
        Ship best = null;
        float bestSqr = DetectionDistance * DetectionDistance;

        for (int i = 0; i < All.Count; i++)
        {
            Ship other = All[i];

            if (!IsHostileTo(other))
                continue;

            float sqr = ((Vector2)other.transform.position - (Vector2)transform.position)
                .sqrMagnitude;

            if (sqr >= bestSqr)
                continue;

            bestSqr = sqr;
            best = other;
        }

        return best;
    }

    protected void AimGun()
    {
        //Not Implemented, and should make Gun class first
    }

    protected void Repair()
    {
        //Not Implemented
    }

}
