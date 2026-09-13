using System.Collections;
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
            // NearestHostile과 같은 틱스탬프 캐시. isDriverReady·isGunnerReady·포탑마다
            // 이 게터를 지나는데 안은 StillAboard(네이티브 IsChildOf) 루프다. 피해가
            // ITickLate에서 들어오니 판정이 1틱 늦을 수 있는 것도 NearestHostile과 같고,
            // 그쪽이 이미 수용한 지연이다.
            if (_powerTick == Core.TickManager.currentTick)
                return _cachedPower;

            _powerTick = Core.TickManager.currentTick;
            return _cachedPower = ComputePower();
        }
    }

    private long _powerTick = -1;
    private bool _cachedPower;

    private bool ComputePower()
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

    /// <summary>설계에 발전하는 모듈이 하나라도 있었는가. Awake가 한 번 정하고 안 바뀐다.</summary>
    private bool _needsPower;

    // 설계에 탄약고가 있었는가. 없던 배(dart·asteroid)는 탄약을 안 센다 - _needsPower와 같은 질문이다.
    private bool _hasMagazine;

    /// <summary>탄약 경고 걸쇠. 0 = 안 했다, 1 = 25% 알렸다, 2 = 0 알렸다. Rearm이 되돌린다.</summary>
    private int _ammoWarned;

    private const float AmmoLowFraction = 0.25f;

    /// <summary>
    /// 한 발 꺼낸다. <paramref name="cost"/>는 그 탄이 먹는 칸 수(질량에서 나온다 - Gun.RoundCost).
    /// 탄약고가 설계에 없으면 언제나 true, 있는데 어디서도 그만큼을 못 꺼내면 false.
    ///
    /// **한 탄약고에서 다 꺼낸다.** 305mm 한 발을 여러 탄약고에서 조금씩 긁으면 배가 거의 빈
    /// 상태에서도 계속 쏘는데, 그건 "탄이 떨어진다"를 무의미하게 만든다.
    /// </summary>
    public bool TakeRound(int cost = 1)
    {
        if (!_hasMagazine)
            return true;

        for (int i = 0; i < shipCriticals.Count; i++)
        {
            CriticalModule m = shipCriticals[i];

            if (m != null && StillAboard(m, this) && m.TakeRound(cost))
            {
                // 전이만 적는다 - 상태를 매 발 적으면 25% 아래에서 쏘는 모든 발이 경고다.
                // 여기가 아니라 Gun에서 하면 포탑 수만큼 걸쇠가 생긴다.
                if (IsPlayerControlled)
                {
                    int left = Rounds, max = MaxRounds;

                    if (_ammoWarned < 2 && left <= 0)
                    {
                        _ammoWarned = 2;
                        RunLog.AmmoOut();
                    }
                    else if (_ammoWarned < 1 && max > 0 && left <= max * AmmoLowFraction)
                    {
                        _ammoWarned = 1;
                        RunLog.AmmoLow(Mathf.RoundToInt(100f * left / max));
                    }
                }

                return true;
            }
        }

        return false;
    }

    public int Rounds
    {
        get
        {
            int n = 0;
            for (int i = 0; i < shipCriticals.Count; i++)
                if (shipCriticals[i] != null && !shipCriticals[i].Neutralized && StillAboard(shipCriticals[i], this))
                    n += shipCriticals[i].Rounds;
            return n;
        }
    }

    public int MaxRounds
    {
        get
        {
            int n = 0;
            for (int i = 0; i < shipCriticals.Count; i++)
                if (shipCriticals[i] != null && !shipCriticals[i].Neutralized && StillAboard(shipCriticals[i], this))
                    n += shipCriticals[i].maxRounds;
            return n;
        }
    }

    /// <summary>창고 탄약을 탄약고에 넣는다. 들어간 발수를 돌려준다 - Refuel과 같은 모양.</summary>
    public int Rearm(int offer)
    {
        int loaded = 0;
        _ammoWarned = 0;   // 채웠으면 다음에 또 알린다

        for (int i = 0; i < shipCriticals.Count && loaded < offer; i++)
        {
            CriticalModule m = shipCriticals[i];

            if (m != null && StillAboard(m, this))
                loaded += m.Load(offer - loaded);
        }

        return loaded;
    }

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
    /// Atmosphere/Crew를 이 틱마다 한 번만 돈다(오너 승인, 2026-08-29). 죽는 순간이
    /// 최대 이만큼(166ms) 늦게 잡힌다는 뜻이다 - WatchForCritical/WatchForRoles는
    /// 그대로 매 틱이라 IsCombatEffective 자체는 안 늦는다, CrewAlive 전이만 늦는다.
    /// </summary>
    private const int AtmosphereInterval = 10;

    /// <summary>
    /// 배마다 다른 나머지. **전역 tick % N을 쓰면 안 된다** - 모든 배가 같은 틱에
    /// 몰려서 그 틱만 스파이크가 된다. 예전 `tick % seed`가 정확히 이 함정이었다
    /// (seed가 1인 배는 매 틱 걸려 공기가 안 새고, 2인 배는 절반 속도로 샜다).
    /// Awake에서 한 번만 정해서 배 수명 내내 안 바뀐다.
    /// </summary>
    private int _atmosphereOffset;

    
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

    /// <summary>
    /// 배 로컬 좌표 하나를 승무원이 부를 이름으로. "함수 좌현", "중앙부", "함미 우현".
    ///
    /// **저작하지 않는다.** 격자 범위 안에서의 상대 위치로만 정한다 - 배마다 구역 이름을
    /// 손으로 적으면 배가 아홉 척이고 def가 데이터인 이 설계에서 그것만 코드가 된다.
    /// 3x3이면 손상 보고로 충분하고, 더 잘게 나눠도 사람이 못 외운다.
    ///
    /// 축은 <see cref="Nose"/>/<see cref="Port"/>와 같은 규약이다: 로컬 +x가 함수,
    /// +y가 좌현. **row가 아래로 증가하는 것은 칸 좌표계 사정이고 여기는 배 좌표계다.**
    /// 반대편 배의 180도 회전도 로컬에는 없다 - 회전은 월드에만 있다.
    /// </summary>
    public string SectionName(Vector3 localPosition)
    {
        if (_map == null)
            return "선체";

        // 격자 원점과 크기에서 로컬 범위를 얻는다. ToLocal이 칸 -> 배 좌표라 두 끝을
        // 통과시키면 그대로 경계가 나온다.
        Vector2 a = _map.ToLocal(0, 0);
        Vector2 b = _map.ToLocal(_map.width - 1, _map.height - 1);

        float minX = Mathf.Min(a.x, b.x);
        float maxX = Mathf.Max(a.x, b.x);
        float minY = Mathf.Min(a.y, b.y);
        float maxY = Mathf.Max(a.y, b.y);

        string fore = Band(localPosition.x, minX, maxX, "함미", "중앙부", "함수");
        string side = Band(localPosition.y, minY, maxY, "우현", "", "좌현");

        if (side.Length == 0)
            return fore;

        return fore.Length == 0 ? side : $"{fore} {side}";
    }

    /// <summary>범위를 3등분해 이름 하나를 고른다. 가운데 이름이 빈 문자열이면 생략된다.</summary>
    private static string Band(float v, float min, float max, string low, string mid, string high)
    {
        float span = max - min;

        if (span <= 1e-3f)
            return mid;

        float t = Mathf.Clamp01((v - min) / span);

        return t < 1f / 3f ? low : t < 2f / 3f ? mid : high;
    }
    public ShipGrid.Map DesignMap;

    // 물리가 진실이다. 예전엔 Ship이 velocity를 따로 들고 transform을 직접 옮겼는데,
    // 그러면 충돌이 밀어낸 결과를 다음 틱에 우리가 덮어써서 충각이 성립하지 않는다.
    public Vector2 velocity => rig != null ? rig.linearVelocity : Vector2.zero;
    public float hullAngle => rig != null ? rig.rotation : 0f;
    public float angleRate => rig != null ? rig.angularVelocity : 0f;   // 도/초

    /// <summary>입력을 끝까지 넣었을 때의 각속도(도/초). Angle()의 감쇠가 추력과 같아지는 지점이다.</summary>
    public float TerminalAngleRate => angleDrag > 1e-4f ? angleAccel / angleDrag : angleAccel;

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
    /// 켜면 자세 제어가 키 입력(angleInput) 대신 **마우스 포인터 바라보기**가 된다.
    /// 기수가 커서를 쫓고, 모든 포탑은 기수 방향으로 잠긴다(Gun.directionLockTo) -
    /// 함체로 조준하는 전투기식 조종. ship JSON에서 켠다.
    /// </summary>
    public bool isMouseAim;

    /// <summary>
    /// 마우스 조준의 비례 밴드(도). 이 각도를 넘으면 전타, 안쪽에서는 비례해 줄인다.
    /// 작을수록 붙는 맛이 딱딱하다 - 제동은 아래 물리항이 하므로 이 값이 떨림을 안 만든다.
    /// </summary>
    public float mouseAimBand = 5f;

    /// <summary>
    /// 적분 이득(1/도·초). 남는 오차를 시간으로 메운다 - 배가 움직이면 화면 한 점을 가리키는
    /// **월드 각도가 계속 돌아서**, P와 D만으로는 그만큼 뒤처진 채로 평형이 잡힌다.
    /// 0이면 PD 그대로다. 크면 느린 진동이 생기므로 밴드 안에서만 쌓고 상한을 건다.
    /// </summary>
    public float mouseAimKi = 0.35f;

    /// <summary>적분항이 낼 수 있는 입력의 상한. 1이면 적분만으로 전타가 나온다 - 그러면 감기(windup)를 못 막는다.</summary>
    public float mouseAimIClamp = 0.35f;

    private float _aimIntegral;

    /// <summary>기수의 월드 방향 = 로컬 +X. 반대편 배는 180도 회전이라 부호 보정이 없다.</summary>
    public Vector2 NoseDirection => transform.right;

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
    public Rigidbody2D Rig => rig;
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

    /// <summary>
    /// 방의 파공 수를 무효화하는 단 하나의 신호. 판이 뚫리거나(<see cref="Armor.AnyBreached"/>가
    /// 서는 순간), 판이 죽거나(<see cref="HullStructure.ReportPlateLost"/>), 후면 칸이
    /// 날아갈 때(HullStructure.KillRear) 오른다.
    ///
    /// ponytail: 전역 하나다. 배 A가 맞으면 배 B의 캐시까지 같이 죽는다. 피해 사건은
    /// 초당 60틱에 비해 드물어서 그 낭비가 안 보이고, 배마다 들려면 판 하나가 자기
    /// 주인을 찾아 올라가야 한다 - 그 GetComponentInParent가 아끼려는 것보다 비싸다.
    /// 전투 중 파공이 매 틱 나는 것이 프로파일러에 잡히면 그때 배별로 쪼갠다.
    /// </summary>
    public static int BreachVersion;

    protected override void Awake()
    {
        using var _ = _mSpawn.Auto();

        base.Awake();
        rig = GetComponent<Rigidbody2D>();

        // stableId는 배 자신에는 안 찍힌다 - ShipBuilder가 그 값을 배치(판·모듈)에만
        // 매기고 컨테이너인 Ship 자체는 건드리지 않는다. 그래서 여기선 사실상 항상
        // GetInstanceID로 빠지는데, 배마다 갈라주기만 하면 되는 스로틀 위상이라 무해하다 -
        // 결정론이 걸리는 자리(파편 시드 등)와 달리 여긴 재현성이 필요 없다.
        int atmosphereId = stableId >= 0 ? stableId : GetInstanceID();
        _atmosphereOffset = ((atmosphereId % AtmosphereInterval) + AtmosphereInterval) % AtmosphereInterval;

        IsPlayerControlled = GetComponent<PlayerInput>() != null && GetComponent<ShipAi>() == null;

        if (IsPlayerControlled)
            BindBoost();

        // 함선 선택 화면이 고른 배가 씬의 기본값을 이긴다. 선택 화면은 고르고 나서
        // 씬을 다시 여는 방식이라(암전이 공짜다), 그 선택이 살아남는 자리가 여기다.
        if (IsPlayerControlled && !string.IsNullOrEmpty(ShipSelectScreen.Chosen))
            shipDefName = ShipSelectScreen.Chosen;

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

        // 인스펙터에 남아 있던 목록은 곧 지울 자식을 가리킨다.
        shipArmors.Clear();
        shipEngines.Clear();
        shipTanks.Clear();
        shipGuns.Clear();
        shipCriticals.Clear();

        if (design != null)
            design.Apply(this);

        if (buildSeconds > 0f && design != null)
        {
            // **건조 모드.** 판을 하나씩 심고, 다 심은 뒤에야 아래 Finish가 돈다.
            // 그때까지 _structure._hasMap이 false라 파단 BFS가 첫 가드에서 멈추고,
            // 방이 없어서 기압도 안 돈다 - 반쯤 지어진 배가 스스로 조각나는 것을
            // 막는 것이 이 순서다.
            UnderConstruction = true;

            // 자리를 우리가 매 틱 맞추므로 솔버가 끼어들면 안 된다. Finish가 Dynamic으로
            // 되돌린다.
            rig.bodyType = RigidbodyType2D.Kinematic;

            StartCoroutine(BuildOverTime(design, blueprint));
            return;
        }

        if (design != null)
            ShipBuilder.Spawn(transform, design);

        DesignMap = ShipBuilder.StampFromDef(blueprint ?? design);

        LoadHullSkin(design);

        if (DesignMap != null)
        {
            _structure.SeedRear(DesignMap, shipHullPng);

            // 씨앗은 언제나 설계도 전체다. 저장이 하는 일은 거기서 빼는 것뿐이라, 순서가
            // 뒤집히면 SeedRear가 방금 뺀 칸을 도로 넣는다.
            _structure.ForgetRear(design?.rearLost);
        }

        Finish();
    }

    /// <summary>
    /// 선체 그림 한 장. 건조 모드는 판을 다 심은 뒤에 부르므로 경로가 둘이라 함수로 뗐다.
    /// </summary>
    private void LoadHullSkin(ShipDef design)
    {
        if (design == null || string.IsNullOrEmpty(design.hullSkin))
            return;

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

    /// <summary>
    /// 건조에 걸리는 초. 0이면 지금까지처럼 한 프레임에 완성된다 - 캠페인·컷신이
    /// 소환하는 배가 전부 이쪽이다.
    ///
    /// **def가 아니라 소환자가 꽂는다.** 같은 scout이라도 격납고가 만들면 건조 연출이
    /// 있고 캠페인이 소환하면 즉시여야 한다 - def에 넣으면 그 구분이 사라진다.
    /// shipDefName과 같은 자리에서 Awake 전에 넣는다.
    /// </summary>
    public float buildSeconds;

    /// <summary>
    /// 아직 판을 심는 중인가. **격자·구조·후면이 아직 없다는 뜻이다** - 이 동안
    /// 파단도 기압도 안 돌고(각자의 가드가 이미 막는다) 콜라이더가 전부 꺼져 있어서
    /// 솔버와 충각에도 안 잡힌다. 탄은 맞는다(TraceWorld는 계층을 읽는다).
    /// </summary>
    public bool UnderConstruction { get; private set; }

    /// <summary>
    /// 판을 하나씩 심고 마지막에 <see cref="Finish"/>. 격납고가 배를 뽑는 연출이
    /// 이 코루틴 하나다 - 시뮬레이션은 완성 순간에 통째로 켜진다.
    /// </summary>
    private IEnumerator BuildOverTime(ShipDef design, ShipDef blueprint)
    {
        int plates = design.placements?.Count ?? 0;
        float perPlate = plates > 0 ? buildSeconds / plates : 0f;

        yield return ShipBuilder.SpawnOverTime(
            transform, design, perPlate,
            placed =>
            {
                _platesPlaced++;
                rig.mass = Mathf.Max(1f, transform.childCount * massPerPlate);
                ConstructionFx.Wireframe(placed);
            });

        // **자리가 빌 때까지 완성을 미룬다.** 건조 자리는 모함 기준 고정이라 동료나
        // 적이 마침 거기 있을 수 있는데, 그 상태로 Finish가 콜라이더를 켜면 두 배가
        // 겹친 채로 태어나서 RamImpact가 매 틱 갈아버린다.
        //
        // 상한을 두는 이유: 무한정 기다리면 낀 자리에서 영영 Kinematic 골조로 남는다.
        // 드물게 겹치는 것이 영영 안 나오는 것보다 낫다.
        for (float waited = 0f; waited < BuildClearTimeout && !SpotIsClear(design); waited += 0.25f)
            yield return new WaitForSeconds(0.25f);

        DesignMap = ShipBuilder.StampFromDef(blueprint ?? design);
        LoadHullSkin(design);

        if (DesignMap != null)
        {
            _structure.SeedRear(DesignMap, shipHullPng);
            _structure.ForgetRear(design?.rearLost);
        }

        UnderConstruction = false;
        Finish();
        StartCoroutine(ConstructionFx.Reveal(transform));

        // 모함 속도를 물려받는다. 안 주면 항행 중인 모함이 배를 그 자리에 두고 떠난다.
        if (buildAnchor != null && buildAnchor.TryGetComponent(out Rigidbody2D mother))
            rig.linearVelocity = mother.linearVelocity;

        buildAnchor = null;
    }

    /// <summary>지금까지 심은 판 수. 0이면 아직 하나도 안 심어서 "다 죽었다"와 구분이 안 된다.</summary>
    private int _platesPlaced;

    /// <summary>건조 자리가 안 비어도 이 초가 지나면 그냥 완성한다.</summary>
    private const float BuildClearTimeout = 20f;

    private static readonly Collider2D[] _spotProbe = new Collider2D[8];

    /// <summary>
    /// 건조 자리에 남의 몸이 있나. 우리 콜라이더는 건조 중 전부 꺼져 있어서 자기를
    /// 집지 않고, 모함은 명시적으로 뺀다 - 모함을 세면 영영 안 비어서 상한까지 기다린다.
    /// </summary>
    private bool SpotIsClear(ShipDef design)
    {
        if (design == null)
            return true;

        RectInt box = design.Bbox();
        Rigidbody2D mother = buildAnchor != null ? buildAnchor.GetComponent<Rigidbody2D>() : null;

        return SpotIsClear(rig.position, rig.rotation, new Vector2(box.width, box.height) * ShipGrid.CellSize, mother);
    }

    /// <summary>다 지어진 배가 저 자리로 옮겨가도 되나. 워프 정렬이 편대 자리를 검사할 때 쓴다.</summary>
    public bool SpotIsClear(Vector2 at)
    {
        Vector2 size = DesignMap != null
            ? new Vector2(DesignMap.width, DesignMap.height) * ShipGrid.CellSize
            : new Vector2(10f, 5f);

        return SpotIsClear(at, 0f, size, null);
    }

    private bool SpotIsClear(Vector2 at, float angle, Vector2 size, Rigidbody2D ignore)
    {
        int n = Physics2D.OverlapBoxNonAlloc(at, size, angle, _spotProbe);

        for (int i = 0; i < n; i++)
        {
            Rigidbody2D body = _spotProbe[i] != null ? _spotProbe[i].attachedRigidbody : null;

            if (body != null && body != rig && body != ignore)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 건조 중 이 자리에 붙어 있는다. 격납고가 꽂는다.
    ///
    /// **격납고 안이 아니라 밖이다.** 격납고는 4x3인데 나오는 배는 30x14일 수 있어서,
    /// 안에서 지으면 배가 모함 선체를 통째로 뚫고 겹친다. 격납고는 컨테이너가 아니라
    /// 문이고, 배는 그 문 바깥 빈 자리에서 자란다.
    /// </summary>
    public Transform buildAnchor;

    /// <summary>buildAnchor 로컬 기준 자리. 선체 밖으로 빼는 거리가 여기 들어온다.</summary>
    public Vector2 buildOffset;

    /// <summary>
    /// 건조 중 격침. **잔해를 안 남긴다** - HullStructure가 없어서 Breakaway 경로를 못
    /// 타기도 하지만, 골조 잔해가 남으면 그것을 만든 격납고가 자기 잔해에 맞아 유폭한다
    /// (CriticalModule이라 연쇄까지 간다). 폭발 한 방으로 가리고 지운다.
    /// </summary>
    private void WatchConstruction()
    {
        // 모함을 따라다닌다. 부모로 넣지 않는 이유는 리지드바디를 리지드바디 밑에 두는
        // 것이 Unity 2D에서 물리를 망가뜨리기 때문이다 - 자리만 매 틱 맞춘다.
        if (buildAnchor != null)
            rig.position = buildAnchor.TransformPoint(buildOffset);

        if (_platesPlaced == 0 || GetComponentInChildren<Armor>() != null)
            return;

        VfxOneShot.Play("Explosion", transform.position, 6f, 1.5f, 240f);
        Destroy(gameObject);
    }

    /// <summary>
    /// 판이 다 선 뒤의 조립. **건조 모드가 미루는 것이 전부 여기 있다** - 격자에서
    /// 나오는 것(방·구조·후면)과 목록·질량이 한 덩어리라, 이 함수가 안 돌면 배는
    /// 골조일 뿐이다.
    /// </summary>
    private void Finish()
    {
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

        _hasMagazine = false;
        for (int i = 0; i < shipCriticals.Count; i++)
        {
            if (shipCriticals[i] != null && shipCriticals[i].maxRounds > 0)
            {
                _hasMagazine = true;
                break;
            }
        }

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
        // 건조 중에는 조타도 사격도 없다. 아래 전부가 격자에서 나오는 값(방·구조·후면)을
        // 전제하는데 그것이 아직 없다 - 각자의 가드가 막아주긴 하지만, 골조가 스스로
        // 항행하려 드는 것 자체가 틀린 그림이다.
        if (UnderConstruction)
        {
            WatchConstruction();
            return;
        }

        // 물리 콜백 밖에서, 이번 틱의 힘을 걸기 전에. 재부모화가 안전한 유일한 자리다.
        _mSplit.Begin();
        SplitIfBroken();
        _mSplit.End();

        // 지난 틱에 닿은 곳을 지금 부순다. Simulate보다 앞이라, 솔버는 살아남은 판만 본다.
        _mRam.Begin();
        Ram();
        _mRam.End();

        _mDrive.Begin();
        if (isDriverReady)
        {
            if (isMouseAim) MouseAim();
            Angle();
            Drive();
        }
        _mDrive.End();

        _mAim.Begin();
        if (isGunnerReady) AimGun();
        _mAim.End();

        // Atmosphere/Crew만 10틱에 한 번 - 죽는 순간이 최대 166ms 늦게 잡히는 대신
        // 매 틱 방 전체 순회를 없앤다. WatchForCritical은 안 늦춘다 - 배마다 위상이
        // 갈라 있으니(_atmosphereOffset) 같은 틱에 다 몰리지 않는다.
        bool dueForAtmosphere =
            (Core.TickManager.currentTick + _atmosphereOffset) % AtmosphereInterval == 0;

        _mAtmosphere.Begin();
        if (dueForAtmosphere) Atmosphere();
        _mAtmosphere.End();

        _mWatch.Begin();
        if (dueForAtmosphere) Crew();
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
            // 원자로를 수리해 되살아난 배의 수리 죽음까지 남는다.
            RunLog.Finished(this);

            // 터진 배는 명단에서 뺀다 - 표적 검색·FireAndForget·분리 해시가 시체를
            // 후보로 다시 안 집는다. 오브젝트와 틱은 그대로 산다(잔해도 물리는 돈다).
            // **플레이어는 남긴다** - Battle.Player()가 이 목록으로 찾아서, 빼면 상호
            // 격침이 "플레이어 없음 = 패배"로 오판된다. 죽은 플레이어가 남아 있어도
            // 표적 쪽은 IsHostileTo가 IsCombatEffective로 이미 거른다.
            if (!IsPlayerControlled)
                All.Remove(this);
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
            // 틱스탬프 캐시. IsHostileTo가 표적 후보마다 이 값을 물어서, 배 N척이 서로
            // 스캔하면 N²으로 곱하던 자리다. 안은 모듈 목록 × StillAboard 루프 넷이다.
            if (_effectiveTick == Core.TickManager.currentTick)
                return _cachedEffective;

            _effectiveTick = Core.TickManager.currentTick;
            return _cachedEffective = ComputeCombatEffective();
        }
    }

    private long _effectiveTick = -1;
    private bool _cachedEffective;

    private bool ComputeCombatEffective()
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
        ShipBuilder.MountLooseModules(transform, _map, armorAt, doorAt, firstBuild: old == null);

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
        // Ship.OnTick이 이 함수를 AtmosphereInterval틱마다 한 번만 부른다 - 그 사이
        // 흐른 시간이 dt다. 실제 틱 간격 그대로 두면 10틱에 한 번 새는 것이 매 틱 새던
        // 것보다 10배 느려진다 - 감쇠율이 아니라 **호출 빈도만** 줄여야 한다.
        float dt = TickManager.TickDeltaTime * AtmosphereInterval;

        foreach (Room room in rooms)
        {
            // 파공 수는 사건으로만 바뀐다. 안 바뀐 틱에는 세지 않고 지난 값을 쓴다 -
            // 세는 값은 벽 수 + 칸 수라, 아무도 안 맞은 틱에도 배마다 수백 번 돌던 자리다.
            //
            // 예전에는 `tick % seed`로 통째로 걸렀는데 그것이 두 가지를 같이 망쳤다:
            // seed가 1인 배(Ship.All.Count가 짝수일 때)는 매 틱 걸려서 **공기가 영영
            // 안 샜고**, seed가 2인 배는 두 틱에 한 번 도는데 leakRate는 그대로라
            // 새는 속도가 조용히 절반이 됐다. 캐시는 매 틱 감쇠를 그대로 두고 세는
            // 것만 건너뛰므로 그 창이 없다.
            if (room.breachVersion != BreachVersion)
            {
                room.breachVersion = BreachVersion;

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


                // **판이 통째로 없어진 방은 우주에 닿은 것이다.** 그러면 그 벽은 껍질이므로
                // 물리 세계로 돌려보낸다 - MarkExterior의 정의를 그대로 따르는 것뿐이다.
                //
                // Armor.Die가 죽은 판의 이웃 8칸을 깨우지만 그건 구멍 둘레뿐이다. 방
                // 반대편 벽은 아무 신호도 못 받아서, 안 켜면 방에 들어온 잔해가 그쪽
                // 벽을 통과해 배 속으로 계속 가라앉는다.
                //
                // **관통(breaches)이 아니라 판 소실(standing < boundaryPlates)로 판정한다.**
                // 서브셀 하나는 17cm짜리 구멍이라 잔해가 못 지나가고, 탄은 콜라이더를 안
                // 보므로(TraceWorld가 계층에서 읽는다) 켤 이유가 없다. 파공마다 켜면
                // 전투가 길어질수록 켜진 콜라이더가 늘어 절감분이 조용히 증발한다.
                //
                // 걸쇠가 방에 있어서 한 번만 돈다. 되묻지 않으므로 공기가 다시 차도
                // 그대로 켜져 있다(Armor.SetBuried 참고).
                if (standing < room.boundaryPlates && !room.wallsSurfaced)
                {
                    room.wallsSurfaced = true;

                    // 같은 걸쇠를 함내 보고도 나눠 쓴다. "격실이 뚫렸다"의 경계가
                    // 물리와 표시에서 다를 이유가 없다.
                    RunLog.RoomBreached(this, room);

                    if (IsPlayerControlled)
                        HitReadout.RoomBreach();

                    foreach (Armor wall in room.walls)
                    {
                        if (wall != null)
                            wall.Surface();
                    }
                }

                room.breaches = breaches;
            }

            if (room.breaches > 0)
                room.air = Mathf.Max(0f, room.air - room.breaches * leakRate * dt);
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
    /// 주추력만으로 낼 수 있는 순항 속도(m/s). 추력과 항력이 같아지는 지점이라 <c>a / drag</c>다.
    /// Ark 실측 169 m/s가 이 값이고, 관성 정지 거리 564 m가 <c>v / drag</c>(563)와 맞는다 -
    /// 모델이 측정과 같은 것을 말하고 있다는 확인이다.
    /// </summary>
    public float CruiseSpeed =>
        drag > 1e-4f && rig != null && rig.mass > 0f
            ? AvailableThrust(true) * 1000f / rig.mass / drag
            : 0f;

    /// <summary>
    /// 여기서 거리 d를 가는 데 드는 Δv(m/s).
    ///
    /// **순항 몫이 속도와 무관하다는 것이 요점이다.** 순항 중에는 추력이 항력과 같으므로 초당
    /// <c>drag x v</c>를 쓰고, 걸리는 시간이 <c>d / v</c>라 곱하면 <c>drag x d</c>로 v가 사라진다 -
    /// 부스터로 빨리 가도 연료는 같이 들고 시간만 준다. 거기에 한 번 가속하는 몫 <c>v</c>를 더한다.
    /// 감속은 0이다 - 관성 정지는 항력이 공짜로 해 준다(단 그 거리가 v/drag뿐이라 60 km에선 계속 밀어야 한다).
    /// </summary>
    public float TravelCost(float distance) => drag * distance + CruiseSpeed;

    /// <summary>
    /// 워프처럼 한 번에 Δv를 태운다. 모자라면 아무것도 안 빼고 false. 탱크가 없는 배는
    /// <see cref="Drive"/>와 같은 이유로 언제나 된다.
    /// </summary>
    public bool Burn(float deltaV)
    {
        if (shipTanks.Count == 0)
            return true;

        float need = deltaV * rig.mass / 1000f;   // m/s × kg → N·s → kN·s

        if (RemainingImpulse() < need)
            return false;

        ConsumeFuel(need);
        return true;
    }

    /// <summary>offer(kN·s)를 살아 있는 탱크에 앞에서부터 채운다. 실제로 들어간 양을 돌려준다.</summary>
    public float Refuel(float offer)
    {
        float poured = 0f;

        foreach (Tank tank in shipTanks)
        {
            if (offer <= poured)
                break;

            if (!StillAboard(tank, this) || tank.Neutralized)
                continue;

            poured += tank.Refill(offer - poured);
        }

        return poured;
    }

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

    /// <summary>좌현의 월드 방향. 배는 col 축으로 길어 코가 로컬 +X고, 그 왼쪽 +Y가 좌현이다.</summary>
    public Vector2 PortDirection => transform.up;

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

        HoldInBattleZone();
    }

    /// <summary>
    /// 전투 중에는 전장을 못 벗어난다. 플레이어 함선만.
    ///
    /// **로그라이크의 전투는 끝나야 다음이 있다.** 흘러 나가 버리면 그 판이 안 끝나고
    /// 승리도 노획도 다음 구역도 없다. Battle.Stranded가 5초 뒤에 패배로 끊고는 있는데,
    /// 그건 안전장치지 규칙이 아니다 - 플레이어는 자기가 왜 졌는지 모른다.
    ///
    /// **벽이 아니라 조류다.** 넘은 만큼에 비례해 되민다. 딱딱한 벽이면 부딪히는 순간
    /// 속도가 사라져 물리가 거짓말을 하고, 충각으로 튕겨 나간 배가 벽에 박혀 못 돌아온다.
    ///
    /// AI는 안 건다 - 적이 스스로 도망가는 것은 이 게임에 아직 없고, 있게 되면 그건
    /// 이탈이 아니라 후퇴라 다른 규칙이 붙는다.
    /// </summary>
    private void HoldInBattleZone()
    {
        if (!IsPlayerControlled)
            return;

        Battle battle = Battle.current;

        if (battle == null || battle.Ended || !battle.HasZone)
            return;

        Vector2 away = (Vector2)transform.position - battle.Centre;
        float distance = away.magnitude;
        float over = distance - Ballistics.BattleZoneRadius;

        if (over <= 0f || distance < 1e-3f)
        {
            _leftZone = false;
            return;
        }

        // 질량을 곱해서 가속으로 준다 - 무거운 배가 더 멀리 나가면 안 된다.
        rig.AddForce(-away / distance * (over * Ballistics.BattleZonePull * rig.mass));

        if (_leftZone)
            return;

        _leftZone = true;

        DialogueManager.current?.Spawn(
            "전장을 벗어나고 있습니다. <color=yellow>기수를 돌리십시오.</color>",
            "전술",
            duration: 4f,
            intensity: 1.2f,
            style: "damage",
            interrupt: true);
    }

    /// <summary>경계 밖에 있다고 한 번 말했나. 매 틱 말하면 그건 경고가 아니라 소음이다.</summary>
    private bool _leftZone;

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
    /// 방출량. 남이 이 배를 보는 거리의 배율 - 탐지 거리에 곱한다. 순항 0.6, 전추력 1.0, 부스터 +1.0.
    /// 빨리 가면 들키고 조용히 가면 늦는다. 이동 중의 결정은 이 숫자 하나에서 나온다. 계수는 오너 손잡이.
    /// 수리는 안 센다 - 정비는 틱이 선 화면이라 열원이 될 시간이 없다.
    /// </summary>
    public float Emission =>
        EmissionIdle + Mathf.Min(1f, thrustInput.sqrMagnitude) * EmissionThrust + (Boosting ? EmissionBoost : 0f);

    public const float EmissionIdle = 0.6f, EmissionThrust = 0.4f, EmissionBoost = 1f;

    /// <summary>
    /// 이 배의 포가 마지막으로 발사한 틱. **방출량에 안 섞는다** - Emission은 탐지 거리에 곱해지는 값이라
    /// 거기에 넣으면 한 발 쏠 때마다 사거리가 늘어 교전 균형이 통째로 바뀐다. 사격은 "들킨다"에만 쓴다.
    /// </summary>
    [System.NonSerialized] public long lastFireTick = long.MinValue;

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
        // **지도를 보는 동안은 손을 뗀다.** 클릭으로 자리를 고르게 하면 그 클릭이 곧 사격이 되고,
        // WASD로 지도를 훑는 동안 배가 날아간다. 틱은 계속 도므로 배는 관성으로 흐른다 - 그것이 의도다.
        // **값을 0으로 덮어쓰는 것이 요점이다** - PlayerInput을 끄면 마지막 값이 걸린 채 남는다(아래 주석).
        if (MapScreen.IsOpen && IsPlayerControlled)
        {
            thrustInput = Vector2.zero;
            angleInput = 0f;
            pilotBoost = false;
        }

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

    /// <summary>워프 뒤의 배는 식어 있다. 판과 후면 둘 다 - 한쪽만 끄면 뜯긴 단면의 앞뒤가 어긋난다.</summary>
    public void CoolDown()
    {
        foreach (Armor plate in shipArmors)
        {
            if (plate != null)
                plate.Cool();
        }

        if (TryGetComponent(out HullStructure structure))
            structure.CoolRear();
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
    /// <summary>
    /// 암전 밑에 미리 세워 두고 아직 안 깨운 적. **양쪽 다 적이 아니다** - 잠든 배는 표적을
    /// 안 잡고, 잠든 배도 표적이 안 된다. 사격 중지만으로는 모자란다: 동료 포탑이
    /// DetectionDistance 2000으로 잠든 배를 잡아 도착 페이드 중에 교전을 연다. Campaign이
    /// 켜고 WakeDormant가 끈다. 탄은 물리라 맞기는 한다.
    /// </summary>
    [NonSerialized] public bool dormant;

    public bool IsHostileTo(Ship other)
        => other != null
        && other != this
        && !dormant
        && !other.dormant
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
        float bestSqr = float.PositiveInfinity;

        for (int i = 0; i < All.Count; i++)
        {
            Ship other = All[i];

            if (!IsHostileTo(other))
                continue;

            float sqr = ((Vector2)other.transform.position - (Vector2)transform.position)
                .sqrMagnitude;
            float reach = DetectionDistance * other.Emission;   // 시끄러운 배는 멀리서 보인다

            if (sqr >= reach * reach || sqr >= bestSqr)
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

    /// <summary>
    /// 마우스 조준 조종. 기수가 커서를 쫓도록 angleInput을 덮어쓰고(비례 제어 - 오차
    /// 15도 안에서 감속해 떨림 없이 잡는다), 포탑 전부를 기수 방향으로 잠근다.
    ///
    /// 잠금은 **값이라 매 틱 다시 넣는다** - 기수는 계속 도는데 Vector2?는 살아 있는
    /// 참조가 아니다. AI 배(커서 없음)는 isMouseAim을 켜도 각도 제어만 평소대로 남는다.
    /// </summary>
    private void MouseAim()
    {
        Vector2 nose = NoseDirection;

        for (int i = 0; i < shipGuns.Count; i++)
        {
            if (shipGuns[i] != null)
                shipGuns[i].directionLockTo = nose;
        }

        if (!IsPlayerControlled || Camera.main == null || Mouse.current == null)
            return;

        Vector3 screen = Mouse.current.position.ReadValue();
        screen.z = -Camera.main.transform.position.z;

        Vector2 cursor = Camera.main.ScreenToWorldPoint(screen);
        Vector2 toCursor = cursor - (Vector2)transform.position;

        if (toCursor.sqrMagnitude < 1e-4f)
            return;

        float want = Mathf.Atan2(toCursor.y, toCursor.x) * Mathf.Rad2Deg;
        float have = Mathf.Atan2(nose.y, nose.x) * Mathf.Rad2Deg;
        float error = Mathf.DeltaAngle(have, want);
        float rate = angleRate;

        // **D항은 상수가 아니라 제동 각이다.** 지금 각속도에서 실제로 멈추는 데 쓸 각을 오차에서
        // 미리 뺀다. 그러면 어떤 배에서도 임계 제동이 되고, ShipAi의 turnLead처럼 초 단위 상수를
        // 손으로 맞출 필요가 없다 - 그 상수가 15도/초짜리 함선 기준이라 fly에서 16배 과했다.
        float braking = BrakingAngle(rate);

        // **I항은 오버슛이 아니라 뒤처짐을 고친다.** 배가 움직이면 화면 한 점을 가리키는 월드 각도가
        // 계속 돌아서, P와 D만으로는 그만큼 뒤처진 자리에서 평형이 잡힌다(위치 오차만 보는 제어기의
        // 원리적 지연). 시간으로 메우는 것이 적분이다.
        //
        // **밴드 안에서만 쌓는다.** 밖에서 쌓으면 큰 선회 한 번에 통째로 감겨서(windup) 도착할 때
        // 반대로 밀어붙인다 - 오버슛을 고치려다 오버슛을 만드는 전형이다. 밴드를 벗어나면 즉시 비운다.
        float band = Mathf.Max(0.1f, mouseAimBand);

        if (Mathf.Abs(error) < band)
            _aimIntegral = Mathf.Clamp(
                _aimIntegral + error * Time.deltaTime,
                -mouseAimIClamp / Mathf.Max(1e-3f, mouseAimKi),
                mouseAimIClamp / Mathf.Max(1e-3f, mouseAimKi));
        else
            _aimIntegral = 0f;

        angleInput = Mathf.Clamp(
            (error - braking) / band + _aimIntegral * mouseAimKi, -1f, 1f);
    }

    /// <summary>
    /// 지금 각속도에서 멈추기까지 쓸 각(도, 부호 있음).
    ///
    /// **항력을 빼면 안 된다.** 제동 중에도 Angle()이 rate x angleDrag를 같이 깎으므로 실제 감속은
    /// angleAccel + angleDrag x ω다. 항력을 무시한 ω²/(2a)는 그래서 **필요한 각을 과대평가**하고,
    /// 제동을 일찍 밟은 뒤 남은 거리를 기어간다 - sunkiller(종단 48도/초)에서 48도 대 29.5도,
    /// 거의 두 배다. 증상이 "조준에 시간이 오래 걸린다"였고 원인은 이득이 아니라 이 식이었다.
    ///
    /// dω/dt = -(a + d·ω)를 각도로 적분하면 닫힌 해가 나온다:
    ///   θ = (d·ω - a·ln(1 + d·ω/a)) / d²
    /// d가 0으로 가면 ω²/(2a)로 수렴하므로 항력 없는 배도 같은 식으로 덮인다.
    /// </summary>
    private float BrakingAngle(float rate)
    {
        float a = Mathf.Max(1f, angleAccel);
        float w = Mathf.Abs(rate);
        float d = angleDrag;

        float angle = d > 1e-3f
            ? (d * w - a * Mathf.Log(1f + d * w / a)) / (d * d)
            : w * w / (2f * a);

        return angle * Mathf.Sign(rate);
    }

    protected void Repair()
    {
        //Not Implemented
    }

}
