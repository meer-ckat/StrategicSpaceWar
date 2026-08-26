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
    public List<Tank> shipTanks = new();
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
    public bool HasLiveEngine => AnyModuleInLivingRoom(this, shipEngines); 
    public bool HasLiveGun => AnyModuleInLivingRoom(this, shipGuns);
    private bool _engineerLost, _gunnerLost;

    
    /// <summary>
    /// 승무원의 자리. **개별 승무원이 아니다** - 이 배에 그 일을 할 사람이 아직 있느냐다.
    ///
    /// 여기에 대사 화자 이름("기관")을 안 적는 것이 요점이다. Ship은 대본을 모른다.
    /// enum -> 화자 이름 대응은 대사 쪽에 한 번만 둔다.
    /// </summary>
    public enum ShipRole
    {
        /// <summary>기관. 살아 있는 조건은 <see cref="HasLiveEngine"/>.</summary>
        Engineer,

        /// <summary>전술. 살아 있는 조건은 <see cref="HasLiveGun"/>.</summary>
        Gunner,
    }

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
    public ShipGrid.Map Map => _map;//Ship이 Map을 Filed로 든다.
    public ShipGrid.Map DesignMap;

    // 물리가 진실이다. 예전엔 Ship이 velocity를 따로 들고 transform을 직접 옮겼는데,
    // 그러면 충돌이 밀어낸 결과를 다음 틱에 우리가 덮어써서 충각이 성립하지 않는다.
    public Vector2 velocity => rig != null ? rig.linearVelocity : Vector2.zero;
    public float hullAngle => rig != null ? rig.rotation : 0f;
    public float angleRate => rig != null ? rig.angularVelocity : 0f;   // 도/초

    // 입력은 저장만 한다. 계산은 전부 틱 안에서.
    //
    // **월드 축이다.** 함체 각도가 이 값에 하나도 안 섞인다 - Drive()가 그대로 힘으로 쓴다.
    // 배를 180도 돌려도 같은 입력이 같은 월드 방향으로 민다. 실제 우주선의 RCS와 같은
    // 구조이고, 그래서 옆으로 미끄러지면서 등을 보일 수 있다.
    //
    // ShipAi도 월드 방향을 그대로 넣는다. **둘이 같은 좌표계여야 한다** - 한쪽만 함체
    // 기준으로 바꾸면 AI가 플레이어와 다른 물리를 타고, 증상이 "AI만 이상하게 움직인다"라
    // 원인이 안 보인다.
    protected Vector2 thrustInput;
    protected float angleInput;     // -1..1

    /// <summary>
    /// 컷신이 이 배의 포탑을 겨누게 하는 점. **null이면 아무 일도 안 일어난다** - 포탑은
    /// 평소대로 가장 가까운 적을 잡는다. 컷신 전용이지만 `Gun`은 그 사실을 모른다:
    /// `IsManual`이 `owner.IsPlayerControlled`를 읽는 것과 같은 방향이고, 그래서 포탑에
    /// 컷신용 분기가 안 생긴다.
    /// </summary>
    [NonSerialized] public Vector2? cutsceneAimAt;

    /// <summary>
    /// 켜면 포탑이 **조준은 계속하되 안 쏜다.** 조준과 격발이 `Gun`에서 이미 다른 축
    /// (`TryGetTarget` / `WantsToFire`)이라 이 둘을 따로 줄 수 있다 - 겨눈 채 멈춰 있는
    /// 그림이 컷신에서 제일 자주 필요하다.
    /// </summary>
    [NonSerialized] public bool cutsceneHoldFire;

    /// <summary>
    /// 켜면 포탑이 **방아쇠 없이 쏜다.** 수동 주포(플레이어 배)는 평소 마우스를 눌러야
    /// 쏘는데, 컷신에는 누를 사람이 없다. <see cref="cutsceneHoldFire"/>가 이것보다 세다 -
    /// 둘 다 켜면 안 쏜다.
    /// </summary>
    [NonSerialized] public bool cutsceneForceFire;

    /// <summary>
    /// 컷신이 켜는 부스터. <see cref="Boosting"/>은 매 프레임 입력에서 다시 읽히므로
    /// 밖에서 대입해 봐야 그 자리에서 지워진다 - 그래서 따로 든다.
    /// </summary>
    [NonSerialized] public bool cutsceneBoost;

    /// <summary>
    /// AI가 켜는 부스터. <see cref="ShipAi"/>가 매 틱 다시 쓴다 - 사람이 키를 누르는 것과
    /// 같은 문으로 들어가므로 BoosterComp는 누가 켰는지 몰라도 된다.
    ///
    /// 컷신의 것과 따로 두는 이유: 컷신이 켜 둔 부스터를 AI가 매 틱 꺼 버리면 연출이
    /// 안 먹고, 반대로 한 필드를 나눠 쓰면 컷신이 끝날 때 AI 것까지 꺼진다. 주인이
    /// 둘이면 언젠가 서로를 덮어쓴다.
    /// </summary>
    [NonSerialized] public bool pilotBoost;

    /// <summary>
    /// 원자로를 터뜨린다. 연출이 "저 배가 지금 폭발한다"를 말할 수 있어야 하고, 그것이
    /// 시뮬레이션의 유폭과 **같은 길**이어야 한다 - 컷신 전용 폭발을 따로 만들면 화면에
    /// 나오는 그림이 실제 전투와 달라진다.
    ///
    /// 원자로가 없으면 탄약고라도 터뜨린다. 둘 다 없으면 아무 일도 안 일어난다.
    /// </summary>
    public void DetonateReactor()
    {
        CriticalModule pick = null;

        for (int i = 0; i < shipCriticals.Count; i++)
        {
            CriticalModule critical = shipCriticals[i];

            if (critical == null || critical.Neutralized || !StillAboard(critical, this))
                continue;

            // 원자로가 있으면 그것부터. 없으면 처음 만난 탄약고로 떨어진다.
            if (critical.providesPower)
            {
                pick = critical;
                break;
            }

            pick ??= critical;
        }

        // TakeDamage가 0으로 떨어지는 순간 Detonate를 부른다. 그 길을 그대로 탄다.
        pick?.TakeDamage(float.MaxValue);
    }

    Rigidbody2D rig;
    [NonSerialized] private Texture2D shipHullPng;
    public Texture2D ShipHullPng => shipHullPng;

    /// <summary>
    /// 배 그림의 픽셀 사본. ArmorSkin이 판마다 굽는 자리에서 GetPixelBilinear를 픽셀당
    /// 한 번씩 부르면 그것만으로 소환 스파이크가 난다 - 함선당 한 번만 뽑아 두고 나눠 쓴다.
    /// 사본은 **텍스처 단위 정적 공유다** - destroyer급 사본이 ~37MB라, 같은 그림을 쓰는
    /// 배 두 척이 각자 뽑으면 그 메가바이트가 소환 프레임에 두 번 할당된다.
    /// </summary>
    [NonSerialized] private Color32[] _hullPixels;
    private static readonly Dictionary<Texture2D, Color32[]> _hullPixelsShared = new();

    public Color32[] HullPixels
    {
        get
        {
            if (_hullPixels == null && shipHullPng != null)
            {
                if (!_hullPixelsShared.TryGetValue(shipHullPng, out _hullPixels) || _hullPixels == null)
                    _hullPixelsShared[shipHullPng] = _hullPixels = shipHullPng.GetPixels32();
            }

            return _hullPixels;
        }
    }

    /// <summary>
    /// 그림 파일명 -> 디코드된 텍스처. PNG 동기 디코드(수십 ms)가 함선 소환마다 나가던 것을
    /// 그림당 한 번으로 줄인다. 설계도 그림은 런타임 불변이라 안 썩고, 종류가 몇 개 안 돼서
    /// 런 끝까지 들고 있어도 된다 - 그래서 개별 함선이 OnDestroy에서 지우면 **안 된다**.
    /// </summary>
    private static readonly Dictionary<string, Texture2D> _hullSkinShared = new();

    /// <summary>
    /// 배 한 척이 태어나는 값. 소환 스파이크를 "몇 ms"가 아니라 "어디서"로 보려면
    /// 이 마커와 ShipBuilder·ThingDef·ShipDef.Load의 것이 같이 있어야 한다 - 합계만
    /// 보고 다중틱으로 쪼개면 정작 고정비는 그대로 남는다. 릴리스에선 no-op.
    /// </summary>
    private static readonly Unity.Profiling.ProfilerMarker _mSpawn = new("Ship.Spawn");

    protected override void Awake()
    {
        using var _ = _mSpawn.Auto();

        base.Awake();
        rig = GetComponent<Rigidbody2D>();

        IsPlayerControlled = GetComponent<PlayerInput>() != null && GetComponent<ShipAi>() == null;

        if (IsPlayerControlled)
            BindBoost();

        // RequireComponent는 에디터에서 스크립트를 붙일 때만 채워준다. 이미 저장된 씬의
        // 함선에는 없을 수 있어서, 없으면 여기서 만든다.
        _structure = GetComponent<HullStructure>() ?? gameObject.AddComponent<HullStructure>();

        // 설계도가 제일 먼저다. drag·angleAccel 같은 수치가 def에서 오므로 리지드바디에
        // 옮겨 담기 전에 들어와 있어야 하고, 자식도 여기서 갈아엎으니 목록을 걷기 전이다.
        //
        // 저장된 런이 있으면 그것이 이긴다. 전투 결과를 안고 다음 구역으로 가는 것이
        // 이 게임의 규칙이라, 설계도로 다시 짓는 것은 런이 끝났을 때뿐이다.
        // 설계도와 런 def를 갈라 든다. 판은 런 def로 짓고(부서진 자리가 비어야 한다),
        // 후면은 설계도로 seed한다.
        //
        // **손상된 def로 seed하면 안 된다.** 죽은 판 자리가 Empty라 MarkExterior의 flood가
        // 그 구멍으로 배 안에 들어가고, 실내가 Exterior로 표시돼서 후면이 통째로 사라진다.
        // 증상은 "저장하고 열 때마다 후면이 저절로 줄어든다"인데, 줄어드는 자리가 뚫린 구멍
        // 근처라 마치 그럴듯해 보인다.
        ShipDef blueprint = string.IsNullOrEmpty(shipDefName) ? null : ShipDef.Load(shipDefName);
        ShipDef design = RunShipFor(blueprint);

        if (ShipBuilder.SpawnFrom(transform, design, this))
        {
            // 인스펙터에 남아 있던 목록은 방금 지운 자식을 가리킨다.
            shipArmors.Clear();
            shipEngines.Clear();
            shipTanks.Clear();
            shipGuns.Clear();
            shipCriticals.Clear();
        }
        DesignMap = ShipBuilder.StampFromDef(blueprint ?? design);

        if(design!=null&&!string.IsNullOrEmpty(design.hullSkin))
        {
            try{

                // 같은 그림은 한 번만 디코드한다. 아군·적군이 같은 급이면 소환 프레임에
                // 같은 PNG를 두 번 디코드하고 있었다.
                if (_hullSkinShared.TryGetValue(design.hullSkin, out Texture2D shared) && shared != null)
                {
                    shipHullPng = shared;
                }
                else
                {
                    byte[] bytes = File.ReadAllBytes(ShipDef.SkinPathOf(design.hullSkin));
                    shipHullPng = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        // 기본 Repeat면 최외곽 판의 가장자리 샘플이 반대편 색과 섞인다.
                        // ArmorSkin의 수제 쌍선형과 BackPlateView의 GetPixelBilinear가 같은
                        // 가장자리 규칙(clamp)을 쓰게 여기서 못 박는다.
                        wrapMode = TextureWrapMode.Clamp,
                    };
                    bool ok = shipHullPng.LoadImage(bytes);
                    if(!ok)
                    {
                        Destroy(shipHullPng);
                        shipHullPng = null;
                        Debug.LogAssertion("I'm not fucking ok. IM NOT FUCKING OK. FIX. I couldnt load the image, and i fired from cpu. fuck it.");
                    }
                    else
                    {
                        _hullSkinShared[design.hullSkin] = shipHullPng;
                    }
                }

            }
            catch(Exception e)
            {
                Debug.LogAssertion("Hell ye: " + e);
            }
        }
        if (DesignMap != null)
        {
            _structure.SeedRear(DesignMap, shipHullPng);

            // 씨앗은 언제나 설계도 전체다. 저장이 하는 일은 거기서 빼는 것뿐이라, 순서가
            // 뒤집히면 SeedRear가 방금 뺀 칸을 도로 넣는다.
            _structure.ForgetRear(design?.rearLost);
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
        if (shipTanks.Count == 0) shipTanks = new List<Tank>(GetComponentsInChildren<Tank>());
        if (shipGuns.Count == 0) shipGuns = new List<Gun>(GetComponentsInChildren<Gun>());
        if (shipCriticals.Count == 0)
            shipCriticals = new List<CriticalModule>(GetComponentsInChildren<CriticalModule>());

        // **설계에 원자로가 있었는가.** 지금 목록을 세는 것으로는 이 질문에 답할 수 없다 -
        // 터진 원자로는 판과 함께 잔해로 떠나거나 파괴돼서 목록에서 사라지고, 그러면
        // "원자로를 아예 안 단 설계"와 글자 그대로 같아 보인다. 설계 사실은 안 변하므로
        // 여기서 한 번만 적어 둔다.
        _needsPower = false;

        // **설계에 그 역할이 있었는가.** 위와 같은 질문이고 답이 갈리는 자리도 같다.
        // 걸쇠를 처음부터 올려두면 WatchForRoles가 아예 안 본다 - 무장이 없는 dart도,
        // 엔진도 포탑도 없는 asteroid/derelict/mirror도 첫 틱에 "상실"을 기록하지 않는다.
        // 잃은 적이 없는 것을 잃었다고 적으면 로그가 사건 넷으로 시작하고, 거기에
        // 붙는 것들(tension, UI)이 전부 그 거짓말 위에 선다.
        _engineerLost = shipEngines.Count == 0;
        _gunnerLost = shipGuns.Count == 0;

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

        // **소환 연출은 여기 하나다.** 배가 태어나는 길이 셋인데(Campaign의 적, 동료,
        // 컷신) 셋 다 결국 Ship.Awake를 지나므로, 부르는 자리를 여기 두면 새 소환 경로가
        // 생겨도 저절로 따라온다. 잔해(Hulk)는 Ship이 아니라서 안 걸린다 - 뜯겨 나온
        // 조각이 워프해 들어올 이유가 없다.
        //
        // 크기는 안 준다. 그래프가 자기 반경을 들고 있고, 배마다 맞추기 시작하면
        // 그 규칙이 코드와 그래프 두 군데로 갈린다.
        VfxOneShot.Play("ShipIncoming", transform.position);
    }

    /// <summary>
    /// 플레이어 함선이고 저장된 런이 있으면 그 def를, 아니면 설계도를 돌려준다.
    ///
    /// AI 함선은 항상 설계도다 - 적은 매 전투 새로 나온다. 손상을 들고 가는 것은
    /// 플레이어 한 척뿐이고, 그게 이 게임에서 배 한 척이 특별한 유일한 자리다.
    /// </summary>
    private ShipDef RunShipFor(ShipDef design)
    {
        if (!IsPlayerControlled)
            return design;

        ShipDef saved = RunState.Load();

        return saved ?? design;
    }

    // Tick.Ship이 self로 뭉쳐 보여서 안을 가르는 마커. 릴리스에선 no-op.
    private static readonly Unity.Profiling.ProfilerMarker _mSplit = new("Ship.Split");
    private static readonly Unity.Profiling.ProfilerMarker _mRam = new("Ship.Ram");
    private static readonly Unity.Profiling.ProfilerMarker _mDrive = new("Ship.Drive");
    private static readonly Unity.Profiling.ProfilerMarker _mAim = new("Ship.Aim");
    private static readonly Unity.Profiling.ProfilerMarker _mAtmosphere = new("Ship.Atmosphere");
    private static readonly Unity.Profiling.ProfilerMarker _mWatch = new("Ship.Watch");

    public override void OnTick()
    {
        // 물리 콜백 밖에서, 이번 틱의 힘을 걸기 전에. 재부모화가 안전한 유일한 자리다.
        _mSplit.Begin();
        SplitIfBroken();
        _mSplit.End();

        // 지난 틱에 닿은 곳을 지금 부순다. Simulate보다 앞이라, 솔버는 살아남은 판만 본다.
        _mRam.Begin();
        Ram();
        _mRam.End();

        _mDrive.Begin();
        if (isDriverReady) { Angle(); Drive(); }
        _mDrive.End();

        _mAim.Begin();
        if (isGunnerReady) AimGun();
        _mAim.End();

        _mAtmosphere.Begin();
        Atmosphere();
        _mAtmosphere.End();

        _mWatch.Begin();
        Crew();
        WatchForCritical();
        WatchForRoles();
        _mWatch.End();
    }

    void WatchForRoles()
    {
        if(!_engineerLost && !HasLiveEngine)
        {
            _engineerLost = true;
            RunLog.RoleLost(this, ShipRole.Engineer);
        }
        if(!_gunnerLost && !HasLiveGun)
        {
            _gunnerLost = true;
            RunLog.RoleLost(this, ShipRole.Gunner);
        }
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

            // 포탑이 다 죽어도 움직일 수 있으면 충각이 남아 있다. 엔진이 살아 있다고
            // 움직일 수 있는 게 아니다 - 탱크를 단 배는 연료가 없으면 Drive()가 힘을
            // 0으로 스케일한다. 탱크가 하나도 없는 배는(아직 배치 안 끝난 배) 예전처럼
            // 엔진만 본다 - Drive()의 하위호환 게이트와 같은 조건이어야 둘이 안 어긋난다.
            if (shipTanks.Count > 0 && AvailableDeltaV() <= 0f)
                return false;

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

        // 선체 직속 자식으로 남은 모듈을 발밑 판에 매단다. **JSON 경로에는 이미 있던
        // 규칙이고 씬 경로에만 없었다** - ShipBuilder.Spawn은 mountCol/mountRow로 판 밑에
        // 넣는데, shipDefName이 빈 배(= export 원본)는 그 단계를 안 거친다. 매달리지 않은
        // 모듈은 판이 죽어도 안 죽고 잔해로도 안 따라가는 불사가 된다.
        //
        // Stamp가 아니라 여기서 부르는 이유: Stamp는 순수 질의라 export와 self-test도
        // 부르고, 거기서 계층이 바뀌면 저작 중인 씬이 조용히 변한다.
        //
        // 파단으로 다시 부를 때는 이미 다 매달려 있어 아무 일도 안 한다.
        ShipBuilder.MountLooseModules(transform, _map, armorAt, doorAt);

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

            // 뒤가 뚫린 칸도 구멍이다. **앞과 세는 방식이 다르다** - 전면 파공은 방을
            // 둘러싼 판에서 나오지만(옆으로 뚫린 구멍), 후면은 방이 차지한 칸 자체가
            // 구멍 후보다. 바닥이 없어진 셈이라 벽을 봐도 안 나온다.
            //
            // leakRate를 앞과 공유한다. 뒤 구멍이 다른 속도로 샐 이유가 없고, 상수를
            // 하나 더 두면 "왜 뒤가 더 빨리 새지"를 두 곳에서 튜닝하게 된다.
            for (int i = 0; i < room.cells.Count; i++)
            {
                if (_structure.RearBreached(room.cells[i]))
                    breaches++;
            }

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
    /// 남은 탱크 잔량의 합(kN·s). <see cref="Drive"/>가 쓰는 예산이고, <see cref="AvailableDeltaV"/>가
    /// 이걸 질량으로 나눠 속도로 바꾼다.
    /// </summary>
    public float RemainingImpulse()
    {
        float total = 0f;

        foreach (Tank tank in shipTanks)
        {
            if (!StillAboard(tank, this) || tank.Neutralized)
                continue;

            total += tank.remaining;
        }

        return total;
    }

    /// <summary>
    /// 남은 기동력. 탱크 잔량 합을 지금 질량으로 나눈다 - 판을 잃어 질량이 줄면 같은
    /// 연료로도 이 값이 오른다. 실용 최고속도는 이 값의 절반이다: 전부 밀고 나면
    /// 같은 만큼 되밀어야 멈추기 때문이다.
    /// </summary>
    public float AvailableDeltaV() => RemainingImpulse() * 1000f / rig.mass;

    /// <summary>
    /// request(kN·s)만큼 탱크에서 뺀다. 앞에서부터 순서대로 비운다 - 어느 탱크가
    /// 먼저 마르는지는 지금 안 정한다, 배치에 따라 자연히 갈릴 값이다.
    /// </summary>
    private void ConsumeFuel(float request)
    {
        foreach (Tank tank in shipTanks)
        {
            if (request <= 0f)
                break;

            if (!StillAboard(tank, this) || tank.Neutralized)
                continue;

            request -= tank.Consume(request);
        }
    }

    /// <summary>
    /// 이 부품이 아직 이 배의 것인가.
    ///
    /// null 검사로는 안 된다. 모듈은 자기가 올라앉은 판과 함께 잔해로 재부모화되는데,
    /// Awake에 캐시해 둔 Ship 참조도 shipEngines 목록도 그대로 살아 있다. 그냥 두면
    /// 배가 100m 뒤에 떠 있는 엔진으로 계속 가속하고, 날아간 포탑이 본체 포수의 명령을 받는다.
    /// </summary>
    // IsChildOf는 네이티브 한 번이다. GetComponentInParent<Ship>였을 때 이 한 줄이
    // 매 틱 × (엔진+포탑+원자로+판) 만큼 관리 계층 탐색을 냈다. 잔해는 루트가 다른
    // 오브젝트라 "아직 이 트리 소속"과 "아직 이 배 소속"이 동치다.
    public static bool StillAboard(Component part, Ship ship)
        => part != null && ship != null && part.transform.IsChildOf(ship.transform);

    public static bool AnyModuleInLivingRoom(Ship ship, IEnumerable<Component> comps)
    {
        if(ship == null) return false;
        if(ship.rooms.Count <= 0) return true;

        foreach(Component comp in comps)
        {
            if(ModuleInLivingRoom(comp, ship, ship.rooms))
            {
                return true;
            }
        }
        return false;
    }

    public static bool ModuleInLivingRoom(Component part, Ship ship, List<Room> rooms)
    {
        if(StillAboard(part, ship))
        {
            if(rooms.Count <= 0)
                return true;

            //먼저 벽에 붙어있는지 검사
            var armor = (part.transform.parent != null)? part.transform.parent.GetComponent<Armor>() : null;
            if(armor == null) return false;
                
            //해당 cell이 기압 있는 room에 있는지 검사
            foreach(Room room in rooms)
            {
                if(room.Pressure < Ballistics.CrewMinPressure)
                    continue;

                if(room.walls.Contains(armor))
                {
                    return true;
                }
            
            }
        }
        return false;
    }

    /// <summary>
    /// 뱃머리가 향하는 월드 방향. 배는 격자에서 col 축으로 길게 그려지므로(destroyer가
    /// 69x23) 코는 로컬 +X, 즉 <c>transform.right</c>다 - 포신이 transform.up인 것과
    /// 다르고, 그래서 Gun.Slew의 -90도가 여기엔 없다.
    ///
    /// **localScale.x가 -1이면 뒤집는다.** 반대쪽에서 온 배는 렌더링이 거울상이라 코가
    /// 반대편에 보이는데, transform.right는 rotation만 읽어서 scale을 모른다. ShipAi.Turn이
    /// 목표 각도에 180도를 더하는 것과 같은 보정이고, 같은 규칙이라야 조준한 쪽으로 민다.
    ///
    /// **지금 부르는 데가 없다.** 추력은 월드 축이라 이 값을 안 읽는다 - 이건 포탑 사격각
    /// (함체 기준 사각·블라인드 아크)을 넣을 때 쓰려고 남긴다. 그때 Gun이 "포신이 함체
    /// 기준 몇 도인가"를 물어야 하고, 거울상 보정이 이미 여기 들어 있어야 두 벌이 안 생긴다.
    /// </summary>
    public Vector2 NoseDirection =>
        (Vector2)transform.right * (transform.localScale.x < 0f ? -1f : 1f);

    /// <summary>
    /// 뱃머리 왼쪽(횡추력 +y가 미는 쪽). 코를 90도 돌린 것.
    /// </summary>
    public Vector2 LateralDirection
    {
        get
        {
            Vector2 nose = NoseDirection;
            return new Vector2(-nose.y, nose.x);
        }
    }

    /// <summary>
    /// 추력은 **월드 축**이다. 함체 각도가 한 방울도 안 섞인다 - RCS가 사방에 붙어 있어서
    /// 어느 쪽으로든 같은 세기로 민다. 자세는 <see cref="Angle"/>이 따로 제어한다.
    ///
    /// **그래서 자세와 속도가 완전히 분리된다.** 옆으로 미끄러지면서 등을 보일 수 있고,
    /// 코를 어디로 돌리든 미는 방향은 안 바뀐다. 코를 돌릴 이유는 이동이 아니라 장갑
    /// 각도와 사격각이다.
    ///
    /// 예전에는 같은 월드 x축을 쓰면서 좌우만 `engagementSign` 1비트로 복원했다. 그
    /// 1비트에 25 m 데드밴드가 걸려 있어서 짧은 이동에서는 부호가 얼어붙었고, 컷신의
    /// 15 m짜리 moveTo가 정확히 그 자리였다 - 축이 문제가 아니라 **압축이 문제였다.**
    /// 벡터를 그대로 들고 가면 그 왕복 자체가 없다.
    /// </summary>
    protected void Drive()
    {
        float main = AvailableThrust(true);
        
        Vector2 force = thrustInput * main * 1000f;   // kN -> N

        // 탱크가 하나도 없는 배는 예산 없이 예전처럼 무제한이다 - 배치를 아직 안 끝낸
        // 배가 갑자기 못 움직이면 안 된다. 탱크를 한 장이라도 달면 그때부터 예산이 걸린다.
        if (shipTanks.Count > 0)
        {
            float dt = TickManager.TickDeltaTime;
            float wantImpulse = force.magnitude * dt / 1000f;   // N·s -> kN·s

            if (wantImpulse > 0f)
            {
                float available = RemainingImpulse();

                // 남은 것보다 많이 밀려 하면 있는 만큼만 나간다 - 뚝 끊기지 않고 힘이 준다.
                if (wantImpulse > available)
                    force *= available / wantImpulse;

                ConsumeFuel(Mathf.Min(wantImpulse, available));
            }
        }

        // 충각이 읽는다. 유리에 대고 가속하는 것도 충각이라, 속도가 아니라 힘이 예산이 된다.
        // 예산으로 깎인 뒤의 값이다 - 연료가 없으면 충각도 약해진다.
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

        // **제동도 RCS가 하는 일이라 같은 토크 상한을 받는다.** 예전에는 비율로만 깎아서
        // (rate *= 1 - brake*dt) RCS에 무한한 토크가 있었다 - 2 도/초든 300 도/초든 똑같이
        // 0.3초면 멎었고, 그래서 충각으로 배를 팽이처럼 돌려도 아무 일도 없었던 것처럼
        // 즉시 자세가 잡혔다. 반동을 넣어도 태어나자마자 지워지는 것도 같은 이유다.
        //
        // 조종감은 한 톨도 안 바뀐다. 입력 중 종단 각속도가 angleAccel / angleDrag이고,
        // **바로 그 지점에서 깎는 양이 정확히 angleAccel * dt가 된다** - 클램프 경계와
        // 종단이 같은 값이라 종단 아래에서는 클램프가 아예 안 걸린다. 걸리는 것은 종단을
        // 넘는 회전(충각, 반동, 유폭)뿐이다.
        //
        // 이 클램프가 손잡이 둘을 갈라 놓는다. 큰 회전에서 되잡는 시간은 angleAccel 혼자
        // 정하고(300 도/초 / 20 도/초² = 15초), angleBrake는 경계 아래에서 마무리를 얼마나
        // 야무지게 하느냐만 정한다. 예전에는 둘이 같은 것을 두 번 말하고 있었다.
        float damp = rate * (angleInput == 0f ? angleBrake : angleDrag) * dt;
        float limit = angleAccel * dt;

        rate -= Mathf.Clamp(damp, -limit, limit);

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
    /// 부스터 방아쇠. <see cref="BoosterComp"/>가 매 프레임 읽어서 자기 엔진의 추력을
    /// 켰다 껐다 한다.
    ///
    /// 여기 상태가 하나뿐인 것이 요점이다 - 배가 "부스터를 몇 개 달았나"를 몰라도 되고,
    /// 부스터가 잔해로 떠나도 이 값은 아무 뜻이 안 바뀐다.
    /// </summary>
    public bool Boosting { get; private set; }

    /// <summary>
    /// 누르고 있는 동안만 참이어야 하는 값이라 **메시지를 안 쓴다.**
    ///
    /// <c>PlayerInput</c>의 SendMessage는 누를 때는 오는데 뗄 때 안 오는 설정이 있다
    /// (액션 타입과 interaction에 따라 canceled가 안 나간다). 그러면 <see cref="Boosting"/>이
    /// true로 걸린 채 남아서 부스터가 영원히 켜진다.
    ///
    /// 매 프레임 액션의 현재 상태를 그냥 읽으면 "눌렸다/떼졌다"를 기억할 필요가 없고,
    /// 기억하는 상태가 없으면 어긋날 상태도 없다. OnMove/OnAngle이 메시지를 쓰는 것은
    /// 그쪽이 **값**이라 마지막으로 받은 값이 곧 지금 값이기 때문이다 - 방아쇠는 다르다.
    /// </summary>
    private InputAction _boostAction;

    private void BindBoost()
    {
        var input = GetComponent<PlayerInput>();

        if (input == null || input.actions == null)
            return;

        _boostAction = input.actions.FindAction("Boost", throwIfNotFound: false);

        if (_boostAction == null)
            Debug.LogWarning(
                $"[{name}] 입력에 'Boost' 액션이 없다. InputSystem_Actions에 Button으로 " +
                "추가하고 Shift를 바인딩해라. 없으면 부스터가 영영 안 켜진다.", this);
    }

    private void Update()
    {
        // 컷신이 켜면 입력과 무관하게 켜진다. 사람이 누르는 것과 같은 값을 쓰므로
        // BoosterComp는 누가 켰는지 몰라도 된다.
        Boosting = cutsceneBoost || pilotBoost
            || (_boostAction != null && _boostAction.IsPressed());
    }

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
        // shipHullPng는 안 지운다 - _hullSkinShared가 같은 그림의 다른 배와 공유하는
        // 텍스처라, 지우면 살아 있는 배의 판 굽기가 죽은 텍스처를 읽는다.
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

    // 틱스탬프 캐시. 자동 포탑 N문 + AI가 같은 틱에 같은 답을 각자 전수 스캔으로 다시
    // 구하고 있었다. 틱 안에서는 입력(All 목록·위치·전투력)이 불변이라 언제 계산해도
    // 같은 답이다 - 피해는 다음 틱의 Simulate/ITickLate에서 들어온다.
    private long _hostileTick = -1;
    private Ship _cachedHostile;

    /// <summary>DetectionDistance 안에서 가장 가까운 적. 없으면 null.</summary>
    public Ship NearestHostile()
    {
        if (_hostileTick == Core.TickManager.currentTick)
            return _cachedHostile;

        _hostileTick = Core.TickManager.currentTick;

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

        _cachedHostile = best;
        return best;
    }

    public void RecalcMass() => rig.mass = Mathf.Max(1f, shipArmors.Count * massPerPlate);

    protected void AimGun()
    {
        //Not Implemented, and should make Gun class first
    }

    protected void Repair()
    {
        //Not Implemented
    }

}
