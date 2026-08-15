using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Core;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(HullStructure))]
public abstract partial class Ship : Thing
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

    [Header("맵")]
    public TextAsset shipMap;
    public Armor armorPrefab;
    public Door doorPrefab;
    public Engine enginePrefab;

    [Header("공기")]
    public float leakRate = 2f;    // 파공 1개당 초당 유출량
    public float doorRate = 1f;    // 문 하나의 초당 유량 계수

    [Header("승무원")]
    public bool isDriverReady = true;
    public bool isGunnerReady = true;
    public bool isEngineerReady = true;
    public int crews;

    
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
    int mapWidth, mapHeight;

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

    protected override void Awake()
    {
        base.Awake();
        rig = GetComponent<Rigidbody2D>();

        // RequireComponent는 에디터에서 스크립트를 붙일 때만 채워준다. 이미 저장된 씬의
        // 함선에는 없을 수 있어서, 없으면 여기서 만든다.
        _structure = GetComponent<HullStructure>() ?? gameObject.AddComponent<HullStructure>();
        rig.bodyType = RigidbodyType2D.Dynamic;
        rig.gravityScale = 0f;

        // 항력을 물리에 넘긴다. 종단속도는 그대로 추력 / (질량 x drag).
        rig.linearDamping = drag;

        // 각감쇠는 0. 회전 제동은 Angle()의 RCS가 직접 하고, 그래야 충돌이 준 회전을
        // 물리가 먼저 갉아먹지 않는다.
        rig.angularDamping = 0f;
        //나중에는 Json 파싱으로 자동으로 만들거임. 지금은 프로토타입이니 이렇게.
        if (shipArmors.Count == 0) shipArmors = new List<Armor>(GetComponentsInChildren<Armor>());
        if (shipEngines.Count == 0) shipEngines = new List<Engine>(GetComponentsInChildren<Engine>());
        if (shipGuns.Count == 0) shipGuns = new List<Gun>(GetComponentsInChildren<Gun>());

        BuildRooms();
    }

    public override void OnTick()
    {
        // 물리 콜백 밖에서, 이번 틱의 힘을 걸기 전에. 재부모화가 안전한 유일한 자리다.
        SplitIfBroken();

        if (isDriverReady) { Angle(); Drive(); }
        if (isGunnerReady) AimGun();
        Atmosphere();
        Crew();
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
        CrewAlive = false;
        isDriverReady = false;
        isGunnerReady = false;
        isEngineerReady = false;

        // 조종간을 놓은 채로 마지막 입력이 남아 있으면 시체가 계속 가속한다.
        thrustInput = Vector2.zero;
        angleInput = 0f;
    }

    /// <summary>한 번 죽으면 끝. Crew()만 이 값을 내린다.</summary>
    public bool CrewAlive { get; private set; } = true;

    /// <summary>
    /// 아직 상대할 가치가 있는가. 저장된 상태가 아니라 파생값이다 - 쏠 수도, 움직일 수도,
    /// 들이받을 수도 없는 배가 잔해다. AI가 시체를 계속 쏘지 않게 하는 것이 이 값의 일이다.
    /// </summary>
    public bool IsCombatEffective
    {
        get
        {
            if (!CrewAlive)
                return false;

            for (int i = 0; i < shipGuns.Count; i++)
            {
                if (shipGuns[i] != null && !shipGuns[i].Neutralized)
                    return true;
            }

            // 포탑이 다 죽어도 움직일 수 있으면 충각이 남아 있다.
            return AvailableThrust(true) > 0f || AvailableThrust(false) > 0f;
        }
    }

    /// <summary>
    /// 텍스트 맵으로 구획을 나눈다. 자식들의 localPosition을 다시 칸으로 되돌려 맞추므로
    /// 손으로 옮긴 장갑판도 그대로 따라온다.
    /// </summary>
    void BuildRooms()
    {
        rooms.Clear();
        roomsOfDoor.Clear();

        if (shipMap == null)
        {
            Debug.LogWarning($"[{name}] shipMap이 없다. 방·기압 계산을 건너뛴다.");
            return;
        }

        ShipGrid.Map map = ShipGrid.ParseMap(shipMap.text);
        mapWidth = map.width;
        mapHeight = map.height;

        var armorAt = new Dictionary<Vector2Int, Armor>();
        foreach (Armor armor in GetComponentsInChildren<Armor>())
            armorAt[ShipGrid.ToCell(armor.transform.localPosition, mapWidth, mapHeight)] = armor;

        var doorAt = new Dictionary<Vector2Int, Door>();
        foreach (Door door in GetComponentsInChildren<Door>())
            doorAt[ShipGrid.ToCell(door.transform.localPosition, mapWidth, mapHeight)] = door;

        rooms = ShipGrid.BuildRooms(map, armorAt, doorAt);

        _structure.Build(map, breakawaySpeed);

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
            if (engine == null || engine.Neutralized)
                continue;

            total += forward ? engine.MaxPower : engine.MaxReversePower;
        }

        return total;
    }

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

    // 방 구획을 눈으로 확인하는 유일한 창구. 색은 방 번호, 투명도는 기압.
    void OnDrawGizmosSelected()
    {
        if (rooms == null || rooms.Count == 0)
            return;

        Gizmos.matrix = transform.localToWorldMatrix;

        var size = new Vector3(ShipGrid.CellSize * 0.9f, ShipGrid.CellSize * 0.9f, 0.01f);

        for (int i = 0; i < rooms.Count; i++)
        {
            Color color = Color.HSVToRGB(i * 0.618034f % 1f, 0.8f, 1f);
            color.a = 0.15f + 0.45f * rooms[i].Pressure;
            Gizmos.color = color;

            foreach (Vector2Int cell in rooms[i].cells)
                Gizmos.DrawCube(ShipGrid.ToLocal(cell.x, cell.y, mapWidth, mapHeight), size);
        }
    }

#if UNITY_EDITOR
    /// <summary>에디터 전용. 자식을 전부 갈아엎고 맵대로 다시 심는다.</summary>
    [ContextMenu("Build From Map")]
    void BuildFromMap()
    {
        if (shipMap == null)
        {
            Debug.LogWarning($"[{name}] shipMap이 없다.");
            return;
        }

        if (Application.isPlaying)
        {
            Debug.LogWarning($"[{name}] 플레이 모드에서 심으면 종료할 때 전부 사라진다. 나가서 다시 눌러라.");
            return;
        }

        ShipGrid.Map map = ShipGrid.ParseMap(shipMap.text);

        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        int planted = 0, missing = 0;

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            Thing prefab = null;

            switch (map.cells[col, row])
            {
                case ShipGrid.Cell.Wall:   prefab = armorPrefab;  break;
                case ShipGrid.Cell.Door:   prefab = doorPrefab;   break;
                case ShipGrid.Cell.Engine: prefab = enginePrefab; break;
                default: continue;
            }

            // 빈 슬롯을 조용히 건너뛰면 "아무 일도 안 일어남"으로 보인다
            if (prefab == null)
            {
                missing++;
                continue;
            }

            // Instantiate와 달리 프리팹 연결이 남아 나중에 장갑 수치를 한 번에 고칠 수 있다
            var spawned = (Thing)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, transform);

            spawned.transform.localPosition = ShipGrid.ToLocal(col, row, map.width, map.height);
            spawned.transform.localRotation = Quaternion.identity;
            planted++;
        }

        if (missing > 0)
            Debug.LogError(
                $"[{name}] {missing}칸을 심지 못했다. 비어 있는 프리팹 슬롯: " +
                $"{(armorPrefab == null ? "armorPrefab " : "")}" +
                $"{(doorPrefab == null ? "doorPrefab " : "")}" +
                $"{(enginePrefab == null ? "enginePrefab" : "")}");

        Debug.Log($"[{name}] {map.width}x{map.height} 맵에서 {planted}칸을 심었다.");

        shipArmors.Clear();
        shipEngines.Clear();
        BuildRooms();   // 심자마자 기즈모로 구획을 볼 수 있게
    }
#endif
}
