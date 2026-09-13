using UnityEngine;
using UnityEngine.InputSystem;
using Core;

/// <summary>
/// 포탑 하나. 포신은 transform.up을 향한다 - Projectile이 발사 방향을 그렇게 읽는다.
///
/// 수동과 자동이 한 클래스에 있다. 예전에는 M7Cannon(커서)과 AiGun(최근접 적)으로 갈려
/// 있었는데, 그러면 같은 포탑이 프리팹 두 개가 되고 배 JSON이 defName으로 하나를 골라야 한다 -
/// 같은 물건이 누가 타느냐에 따라 다른 이름을 갖는 셈이다. 조준 방식은 포탑의 속성이 아니라
/// 그 배를 누가 모느냐의 속성이다.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class Gun : Thing, IDamageable
{
    public enum AimMode
    {
        /// <summary>배를 따라간다. 플레이어가 모는 배면 커서, AI 배면 자동.</summary>
        FollowOwner,

        /// <summary>커서를 따라가고 좌클릭을 누르는 동안만 쏜다.</summary>
        Manual,

        /// <summary>가장 가까운 적을 겨누고 사선에 들어오면 알아서 쏜다. 대공·부포용.</summary>
        Auto,
    }

    /// <summary>
    /// 이 포탑이 이번 틱에 안 쏜 이유. **순서가 곧 표시 우선순위다** - 한 줄에 묶인 포가
    /// 열둘이면 이유도 열둘이라, HUD는 그중 값이 제일 큰 것 하나를 고른다. 뒤로 갈수록
    /// 플레이어가 할 일이 크다: 장전은 기다리면 되고 사선은 배를 돌려야 하고 포수는
    /// 원자로를 고쳐야 한다.
    ///
    /// 시뮬레이션은 이 값을 안 읽는다. 넷이 전부 "포탑이 안 쏜다" 하나로 보이던 것을
    /// 가르는 것이 전부다.
    /// </summary>
    public enum HoldReason
    {
        /// <summary>안 막혔다. 이번 틱에 쐈거나 쏠 수 있었다.</summary>
        None = 0,

        /// <summary>방아쇠를 안 당겼다. 컷신의 사격 중지도 여기로 온다.</summary>
        Trigger,

        /// <summary>장전 중.</summary>
        Reloading,

        /// <summary>선회 중이라 조준 오차가 아직 fireArc 밖이다.</summary>
        Slewing,

        /// <summary>겨눌 것이 없다.</summary>
        NoTarget,

        /// <summary>사선에 아군이 있다. **배를 돌리지 않으면 안 열린다.**</summary>
        LineBlocked,

        /// <summary>표적이 사각(traverse) 밖이다. 포탑이 한계에 걸린 채 선다.</summary>
        OutOfArc,

        /// <summary>포수가 없다 - 전기가 나갔거나 승무원이 죽었다.</summary>
        NoGunner,

        /// <summary>얹혔던 판이 잔해로 떨어져 나갔다. 이제 이 배의 포가 아니다.</summary>
        Adrift,

        /// <summary>부서졌다.</summary>
        Destroyed,
        NoAmmo,
    }

    /// <summary>지난 틱에 안 쏜 이유. HUD 전용.</summary>
    public HoldReason Hold { get; private set; }

    /// <summary>Fire가 실제로 쏠 수 있는가. 탄을 꺼내기 전에 묻는다 - 발사대는 표적이 없으면 Fire에서 빠져나가므로.</summary>
    protected virtual bool ReadyToFire() => true;

    [Header("무장")]

    /// <summary>
    /// 쏘는 탄의 defName. 프리팹 참조가 아니라 이름이다 - def끼리는 GUID가 없으니 이것이
    /// 유일하게 가능한 방식이고, 동시에 모딩이 열리는 지점이다.
    /// </summary>
    public string projectile;

    public float muzzleSpeed = 900f;
    public float roundsPerMinute = 60f;

    /// <summary>발사 순간 포구에서 포신 방향으로 띄울 VFX 이름(Resources/VFX). 비면 없음.</summary>
    public string muzzleVfx;

    /// <summary>
    /// MuzzleFlash.vfx의 FirePower 어트리뷰트(0~1). 1이 최대 화력, 0.01이 기관총 - 커질수록
    /// 섬광의 지속시간·크기가 커진다. 진공이라 포구 화염이 대기 중보다 훨씬 크게 퍼지는 것이
    /// 그래프의 전제라, 이 값이 곧 "이 포가 얼마나 큰가"다.
    /// </summary>
    public float muzzleFirePower = 0.2f;

    /// <summary>MuzzleFlash.vfx의 color 내장 어트리뷰트. 화약이면 주황, 전자기 가속이면 다른 색.</summary>
    public Color muzzleColor = new(1f, 0.75f, 0.4f);

    /// <summary>
    /// 값이 있으면 표적 대신 이 **방향**(월드 좌표가 아니라 월드 프레임의 방향 벡터)을
    /// 겨눈다. 마우스 조준 배(Ship.isMouseAim)가 매 틱 기수 방향을 넣어준다 - 값이라
    /// 살아 있는 참조가 아니므로 주는 쪽이 갱신해야 한다. def 값이 아니다.
    /// </summary>
    [System.NonSerialized] public Vector2? directionLockTo;

    [Header("조준")]
    [SerializeField] private AimMode aim = AimMode.FollowOwner;

    [Header("선회")]

    /// <summary>
    /// 도/초. **월드 기준이다** - 자이로 안정화 마운트라 함체 동요를 흡수한다. 그래서
    /// 이 값은 "포탑이 **표적을 바꿀 때**의 속도"지 "배를 되잡는 속도"가 아니다.
    ///
    /// 선체 기준으로 바꿔 봤다가 되돌렸다. 이 배들의 종단 각속도가 26~80도/초인데
    /// slewRate는 9~30도/초라, 선회 예산 전부를 배 회전을 상쇄하는 데 쓰고도 모자란다 -
    /// 표적을 한 번도 못 겨누는 포탑이 된다.
    /// </summary>
    public float slewRate = 30f;

    public float fireArc = 2f;      // 도. 조준 오차가 이 안에 들어와야 쏜다

    /// <summary>
    /// 사각 반각(도). 마운트 전방(포 오브젝트의 +X = 기수와 같은 축, 배치 rot이 돌린다)에서
    /// 좌우 이만큼. 0이면 무제한. 판정만 함체 기준이고 선회 속도는 여전히 월드 기준이다.
    /// </summary>
    public float traverse;

    /// <summary>
    /// 조준이 안 끝나도 쏜다 - fireArc 검사를 건너뛴다. 발사 후 스스로 표적을 무는
    /// FireAndForget 미사일이나, 쏘는 것 자체가 조준인 빔 무기용. 선회는 평소대로
    /// 계속 도니까 "겨누지 않는다"가 아니라 "겨눠질 때까지 안 기다린다"다.
    /// </summary>
    public bool AimNotRequired;

    [Header("터렛")]

    /// <summary>
    /// 회전부의 PNG. **비우면 2단이 아니다** - 예전처럼 이 오브젝트 자신이 돌고, 기존 def는
    /// 한 글자도 안 바뀐 채 그대로 돈다.
    ///
    /// 채우면 자식 오브젝트가 하나 생기고 그것만 돈다. 포대(이 오브젝트)는 판에 고정이다.
    /// **터렛은 그림뿐이다** - 콜라이더도 체력도 없다. 주면 StrikeModules가 포대와 터렛을
    /// 둘 다 때려 한 발에 피해가 두 번 들어간다.
    /// </summary>
    public string turretTexture;

    /// <summary>터렛 그림의 세계 크기(m). 가로는 PNG 가로와 정확히 맞아야 한다(SolidSkin.Fits).</summary>
    public Vector2 turretSize = new(1f, 1f);

    /// <summary>터렛의 그리기 순서. 포대(10)보다 커야 위에 얹힌다.</summary>
    public int turretOrder = 20;

    /// <summary>
    /// 포구를 선체 밖으로 밀어내는 거리. 격자 한 칸이 1m이므로 최소 1m는 있어야
    /// 자기가 올라앉은 장갑판을 쏘지 않는다.
    /// </summary>
    public float muzzleOffset = 1f;

    /// <summary>
    /// 사선에 아군이 있는지 볼 거리. 여기까지 아무것도 없으면 그냥 쏜다 - 이 검사가 막으려는
    /// 것은 "코앞의 자기 함교"지 "저 멀리 어딘가의 아군"이 아니다.
    /// </summary>
    public float friendlyCheckRange = 100f;

    /// <summary>
    /// 이 질량(kg)을 넘는 배는 표적으로 안 본다. 0이면 상한이 없다 - 지금까지의 모든 포다.
    ///
    /// **대공포가 전함을 겨누는 것을 막는 값이다.** 자동 포탑은 제일 가까운 적을 보는데,
    /// 함대전에서 제일 가까운 것은 거의 언제나 눈앞의 큰 배라 CIWS가 뚫지도 못할 장갑에
    /// 초당 스무 발을 붓고 정작 전투기는 아무도 안 본다. 구경으로 자동 판정하지 않는
    /// 이유는 임계값을 코드에 두면 새 배 한 척이 그 선을 넘나들 때마다 설계자가 모르는
    /// 사이에 방공망이 켜졌다 꺼지기 때문이다 - 무엇을 쏠지는 def가 정한다.
    /// </summary>
    public float maxTargetMass;

    [Header("내구")]
    public float maxHealth = 60f;

    private float _health;
    private float _pending;         // 발사 대기량. 1이면 한 발

    /// <summary>지난 틱의 부호 있는 조준 오차와 그 틱 번호. 틱이 끊기면(표적을 잃었다가 다시 잡음) 변화율을 0으로 본다.</summary>
    private float _lastError;
    private long _errorTick = -2;

    /// <summary>사격 게이트가 내다보는 비행시간 상한(초). 발사대(muzzleSpeed 2)처럼 느린 것이 게이트를 영영 닫지 않게.</summary>
    private const float MaxFireLead = 1.5f;

    /// <summary>이 포탑이 올라앉은 함선. 파생 포탑이 표적을 고를 때 쓴다.</summary>
    protected Ship owner;

    public bool Neutralized => _health <= 0f;
    public float Health01 => maxHealth > 0f ? _health / maxHealth : 0f;

    protected override void Awake()
    {
        base.Awake();

        _health = maxHealth;
        owner = GetComponentInParent<Ship>();

        if (string.IsNullOrEmpty(projectile))
            Debug.LogError($"[Gun] {name}에 쏠 탄이 없다. projectile(defName)을 채워라.", this);
        else if (!DefDatabase.Has(projectile))
            Debug.LogError($"[Gun] {name}이 부르는 탄 '{projectile}'이 없다.", this);

        // 포탑 둘이 한 오브젝트에 올라앉으면 같은 transform을 서로 다른 곳으로 선회시킨다.
        // 옛 껍데기(M7Cannon/AiGun)를 지우지 않고 Gun을 '추가'하면 이렇게 된다. 증상이
        // 포탑이 떨면서 절반만 쏘는 것이라 원인을 찾기 어렵다.
        if (GetComponents<Gun>().Length > 1)
            Debug.LogError(
                $"[Gun] {name}에 Gun이 둘 이상 붙어 있다. 하나만 남겨라 - 서로의 조준을 " +
                "덮어쓴다.", this);

        BuildTurret();

        // 배치 rot. 터렛이 없는 포는 이 오브젝트 자체가 도니까 transform.right를 매 틱 읽으면
        // 마운트가 포를 따라 돌아 휴지 자세가 자기 꼬리를 쫓는다. 켜지기 전에 넣은 값이라 여기서 한 번.
        _mountLocalZ = transform.localEulerAngles.z;

        // **스폰 순간부터 마운트 전방을 본다.** 안 하면 경사판(45도) 위 포는 부모 판의
        // 기울기를 그대로 입고 태어나고, 그 오차를 첫 틱들의 Slew가 눈에 보이게 정정한다 -
        // 큰 포일수록(slewRate가 느릴수록) 스폰 직후 헛도는 것이 오래 보인다. MountWant는
        // 판이 아니라 선체 기준이라 이 한 줄로 판의 기울기가 사라진다.
        _turret.rotation = Quaternion.Euler(0f, 0f, MountWant());
    }

    private float _mountLocalZ;

    /// <summary>
    /// 도는 부분. <see cref="turretTexture"/>가 비면 이 오브젝트 자신이라, 2단이 아닌
    /// 기존 def는 예전과 글자 그대로 같이 돈다.
    /// </summary>
    protected Transform _turret;
    public Transform Turret => _turret;

    /// <summary>
    /// 회전부를 자식으로 만든다. **판 - 포대 - 터렛이 부모 자식 한 줄이라, 판이 죽으면
    /// 셋 다 죽고 잔해로 가면 셋 다 따라간다** - 판과 모듈의 관계와 똑같은 규칙이고
    /// 별도 코드가 없다.
    ///
    /// 비활성으로 만들고 값을 다 넣은 뒤에 켠다 - SolidSkin.Start가 텍스처·크기·순서를
    /// 읽으므로, 활성 상태에서 붙이면 빈 값으로 한 번 그려진다. ThingDef.Spawn과 같은 규칙.
    /// </summary>
    private void BuildTurret()
    {
        _turret = transform;

        if (string.IsNullOrEmpty(turretTexture))
            return;

        // 2단이면 포대는 그림자 쪽이다. 단색끼리 같은 밝기면 회전부와 한 덩어리로 뭉쳐 포대가 안 보인다.
        if (TryGetComponent(out SolidSkin baseSkin))
            baseSkin.DimFallback(0.45f);

        var go = new GameObject("turret");
        go.SetActive(false);
        go.transform.SetParent(transform, worldPositionStays: false);
        go.layer = gameObject.layer;

        go.AddComponent<SpriteRenderer>();
        go.AddComponent<SolidSkin>().Configure(turretTexture, turretSize, Color.white, turretOrder);

        go.SetActive(true);

        _turret = go.transform;
    }

    /// <summary>로드 경로. 값을 그냥 놓는다 - TakeDamage의 부작용을 타지 않는다.</summary>
    public void RestoreHealth01(float fraction)
        => _health = maxHealth * UnityEngine.Mathf.Clamp01(fraction);

    public void TakeDamage(float amount)
    {
        if (amount <= 0f)
            return;

        _health = Mathf.Max(0f, _health - amount);
    }

    /// <summary>
    /// 이 포탑이 지금 수동인가. FollowOwner는 배에게 물어본다 - PlayerInput이 붙은 배만
    /// 사람이 몬다. 인스펙터에서 Manual/Auto로 못박으면 배와 무관하게 그쪽으로 간다.
    /// </summary>
    public bool IsManual => aim switch
    {
        AimMode.Manual => true,
        AimMode.Auto => false,
        _ => owner != null && owner.IsPlayerControlled,
    };

    /// <summary>
    /// 주포는 커서를, 자동 포탑은 가장 가까운 적을 본다.
    ///
    /// **컷신이 점을 지정했으면 그것이 이긴다.** 안 지정하면(null) 아래 두 길로 그대로
    /// 내려가므로, 컷신에 안 쓰이는 포탑은 이 줄이 생겨도 한 바이트도 안 바뀐다.
    /// </summary>
    protected virtual bool TryGetTarget(out Vector2 worldPoint)
    {
        if (owner != null && owner.cutsceneAimAt.HasValue)
        {
            worldPoint = owner.cutsceneAimAt.Value;
            return true;
        }

        return IsManual ? TryAimAtCursor(out worldPoint) : TryAimAtNearestHostile(out worldPoint);
    }

    /// <summary>
    /// 조준과 격발은 다른 축이다. 수동 주포는 커서를 계속 따라가되 방아쇠를 당길 때만
    /// 쏘고, 자동 포탑은 표적이 잡히면 그대로 쏜다.
    /// </summary>
    /// <summary>
    /// **컷신의 사격 중지가 제일 위다.** 조준은 위에서 계속 도므로 겨눈 채 멈춘다.
    /// 그 다음이 강제 발사 - 수동 주포는 평소 마우스를 눌러야 쏘는데 컷신에는 누를
    /// 사람이 없다. 둘 다 켜면 안 쏜다(중지가 이긴다).
    /// </summary>
    ///
    /// **방향 잠금(마우스 조준 배)은 조준만 정하지 방아쇠가 아니다.** 잠금이 표적 탐색을
    /// 이기므로 AI 배의 자동 포탑은 "표적 있음"으로 내려와 적이 없어도 기수 방향으로 쐈다 -
    /// 격납고에서 나온 fly가 빈 우주에 갈기던 것이 그것이다. 잠긴 자동 포탑은 탐지 거리 안에
    /// 적이 있을 때만 쏜다. 플레이어 배의 잠긴 포탑은 IsManual이라 원래 마우스가 방아쇠다.
    /// </summary>
    protected virtual bool WantsToFire =>
        (owner == null || !owner.cutsceneHoldFire)
        && ((owner != null && owner.cutsceneForceFire)
            || (!IsManual && (!directionLockTo.HasValue || owner == null || owner.NearestHostile() != null))
            || (IsManual && Mouse.current != null && Mouse.current.leftButton.isPressed));

    /// <summary>
    /// <see cref="maxTargetMass"/> 이하인 적 중 제일 가까운 것. 없으면 null - 그러면
    /// 이 포탑은 표적 없음으로 서 있는다. **큰 배를 대신 쏘지 않는다**, 그것이 요점이다.
    /// </summary>
    private Ship NearestLightHostile()
    {
        Ship best = null;
        float bestSqr = float.PositiveInfinity;
        Vector2 here = _turret.position;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship other = Ship.All[i];

            if (!owner.IsHostileTo(other))
                continue;

            // 사각 밖 적을 고르면 한계에 걸린 채 사각 안의 다른 적을 무시한다.
            if (!InArc(other.transform.position))
                continue;

            // Rigidbody2D.mass는 판 수에서 나온다(Ship.RecalcMass). 반파된 큰 배가
            // 상한 아래로 내려오면 CIWS가 그때부터 쏘는 것이 맞다 - 남은 것이 실제로
            // 전투기만 한 조각이다.
            if (maxTargetMass > 0f && (other.Rig == null || other.Rig.mass > maxTargetMass))
                continue;

            float sqr = ((Vector2)other.transform.position - here).sqrMagnitude;
            float reach = owner.DetectionDistance * other.Emission;

            if (sqr >= reach * reach || sqr >= bestSqr)
                continue;

            bestSqr = sqr;
            best = other;
        }

        return best;
    }

    private bool TryAimAtCursor(out Vector2 worldPoint)
    {
        worldPoint = default;

        Camera camera = Camera.main;

        if (camera == null || Mouse.current == null)
            return false;

        Vector3 screen = Mouse.current.position.ReadValue();

        // 2D: ScreenToWorldPoint는 카메라에서 z=0 평면까지의 거리를 원한다
        screen.z = -camera.transform.position.z;

        worldPoint = camera.ScreenToWorldPoint(screen);
        return true;
    }

    private bool TryAimAtNearestHostile(out Vector2 worldPoint)
    {
        worldPoint = default;

        if (owner == null)
            return false;

        // 상한이 없으면 배가 이미 이번 틱에 구한 답을 그대로 쓴다(틱스탬프 캐시).
        // 상한이 있으면 그 캐시가 답할 수 없는 질문이라 여기서 따로 훑는다 - 포탑 수가
        // 배당 한 자릿수라 Ship.All 한 바퀴가 NearestHostile 한 번과 같은 크기다.
        Ship target = maxTargetMass > 0f || traverse > 0f
            ? NearestLightHostile()
            : owner.NearestHostile();

        if (target == null)
            return false;

        worldPoint = target.transform.position;

        // **편차 조준.** 예전 주석은 "교전거리 40 m면 리드가 0.45 m라 함선보다 작다"였는데
        // 실제 FightDistance가 120~420 m다. destroyer의 200 m에서 M12(1100 m/s)는 비행이
        // 0.18초라 횡속 30 m/s면 5.5 m 뒤를 쏘는데, fireArc 0.7도가 그 거리에서 허용하는
        // 것은 2.4 m다 - **리드 오차가 조준 정밀도의 두 배**라 fireArc를 조인 것이 무의미했다.
        //
        // 리드 마커(ShipStatusHud)와 **같은 함수**를 쓴다. 마커가 "여기 두면 맞는다"고
        // 약속하는데 AI가 다른 식으로 겨누면 그 약속이 플레이어에게만 참이다.
        //
        // 상대 프레임인 것이 핵심이다 - 탄이 내 배 속도를 물려받으므로(Projectile.Launch)
        // 표적의 절대 미래 위치를 겨누면 내 배 속도만큼 어긋난다. 포탑 위치에서 재는 것은
        // 마커보다 정직한 값이다(마커는 배 중심으로 근사한다).
        //
        // 못 따라잡으면(탄보다 빠른 표적) 그냥 지금 자리를 겨눈다 - 안 쏘는 것보다 낫다.
        Vector2 d = worldPoint - (Vector2)_turret.position;
        Vector2 relative = target.velocity - owner.velocity;

        // **탄이 상대속도보다 빠를 때만 리드한다.** 미사일 발사대는 muzzleSpeed가 2다
        // (추진은 Missile이 한다) - 그 값으로 요격을 풀면 접근 중일 때 수백 초짜리 근이
        // 나와 조준점이 우주 밖으로 날아간다. 판정도 이 조건에서만 근이 하나라 깨끗하다.
        if (relative.sqrMagnitude < muzzleSpeed * muzzleSpeed
            && Ballistics.InterceptTime(d, relative, muzzleSpeed, out float t))
            worldPoint += relative * t;

        return true;
    }

    public override void OnTick()
    {
        if (Neutralized)
        {
            Hold = HoldReason.Destroyed;
            return;
        }

        // 포수가 다른 자리에 가 있으면 포탑은 멈춘다.
        // StillAboard: 이 포탑이 얹힌 판이 잔해로 떨어져 나갔으면 owner는 여전히 살아 있는
        // Ship을 가리키지만 더 이상 이 배의 포탑이 아니다. 우주로 날아가면서 쏘면 안 된다.
        // owner가 애초에 없는 포탑(테스트용 거치대)은 예전처럼 그냥 쏜다.
        //
        // 예전에는 이 둘이 한 조건이었다. 가른 것은 판정이 아니라 답이다 - 잔해로 간 포는
        // 돌아오지 않고, 포수가 없는 포는 원자로를 고치면 돌아온다.
        if (owner != null && !Ship.StillAboard(this, owner))
        {
            Hold = HoldReason.Adrift;
            return;
        }

        if (owner != null && !owner.isGunnerReady)
        {
            Hold = HoldReason.NoGunner;
            return;
        }

        Vector2 target;
        float dt = TickManager.TickDeltaTime;

        // 방향 잠금이 표적 탐색을 이긴다. 방향을 자기 위치 기준 먼 점으로 바꿔서
        // 기존 Slew(점을 겨눈다)를 그대로 쓴다 - 자기에서 뻗은 점이라 각도 오차가 없다.
        if (directionLockTo.HasValue && directionLockTo.Value.sqrMagnitude > 1e-6f)
        {
            target = (Vector2)_turret.position + directionLockTo.Value.normalized * 1000f;
        }
        else if (!TryGetTarget(out target))
        {
            _pending = 0f;
            Hold = HoldReason.NoTarget;
            // 휴지 자세 = 마운트 전방. 없으면 스폰 각도(위)에 영원히 서 있는다.
            Slew((Vector2)_turret.position + MountForward() * 1000f, dt, out _);
            return;
        }

        float error = Slew(target, dt, out bool inArc, out float signed);

        // **PD 사격 게이트의 D항.** P(= error > fireArc)만 보면 조준선을 스쳐 지나가는 틱에도 쏜다 -
        // m4는 slewRate 60도/초에 fireArc 1.0도라 그 창을 틱당 정확히 한 번 지난다.
        //
        // 잘 물고 있는 포탑은 표적이 횡단해도 오차가 0 근처에서 **가만히** 있는다(리드가 등속을 이미 푼다).
        // 오차가 빠르게 변하는 것은 둘 중 하나다: 포탑이 아직 따라잡는 중이거나, 표적이 리드로 못 푸는
        // 기동을 하는 중. 둘 다 지금 쏘면 빗나간다. 비행시간을 곱해서 거리로 자동으로 엄해진다 -
        // 2 km 밖의 fly를 1도 오차로 쏘는 것은 35 m를 빗나가는 것이다.
        float rate = _errorTick == TickManager.currentTick - 1 ? (signed - _lastError) / dt : 0f;
        _lastError = signed;
        _errorTick = TickManager.currentTick;

        float flight = Mathf.Min(
            Vector2.Distance(target, _turret.position) / Mathf.Max(1f, muzzleSpeed), MaxFireLead);

        // 장전은 WantsToFire보다 **위**에 있어야 한다. 방아쇠를 당기는 동안에만 차오르게
        // 하면, 아래 LineIsClear 주석이 약속하는 "막혀서 안 쏜 발은 _pending을 소모하지
        // 않으므로 사선이 열리는 순간 나간다"가 성립하지 않는다 - 사선이 막힌 동안
        // WantsToFire가 꺼지는 포탑은 장전마저 멈춘다.
        //
        // 1로 막아두는 이유: 조준하는 동안 쌓인 발사량이 조준선에 들어오는 순간
        // 한꺼번에 쏟아지는 것을 막는다. 대신 틱당 최대 한 발 - 3600 RPM이 천장이다.
        _pending = Mathf.Min(_pending + roundsPerMinute / 60f * dt, 1f);

        if (!inArc)
        {
            Hold = HoldReason.OutOfArc;
            return;
        }

        if (!WantsToFire)
        {
            Hold = HoldReason.Trigger;
            return;
        }

        // LineIsClear가 맨 뒤인 것은 성능이 아니라 의미다. 앞의 둘이 통과했을 때만
        // "이 틱에 정말 쏜다"이고, 그때의 포신 방향이 탄이 실제로 갈 선이다.
        // 막혀서 안 쏜 발은 _pending을 소모하지 않으므로 사선이 열리는 순간 나간다.
        //
        // 세 줄로 편 것은 단락 평가 순서를 그대로 두면서 이유를 갈라 적기 위해서다 -
        // 조건 하나였을 때 셋이 전부 "포탑이 안 쏜다" 하나로 보였다.
        if (!AimNotRequired && (error > fireArc || Mathf.Abs(rate) * flight > fireArc))
        {
            Hold = HoldReason.Slewing;
            return;
        }

        if (_pending < 1f)
        {
            Hold = HoldReason.Reloading;
            return;
        }

        if (!LineIsClear())
        {
            Hold = HoldReason.LineBlocked;
            return;
        }

        // 탄약은 맨 마지막이다 - 앞의 이유들이 다 통과한 발만 한 발을 쓴다. 막힌 발처럼
        // _pending을 안 쓰므로 탄이 들어오는 순간 나간다.
        if (!ReadyToFire())
        {
            Hold = HoldReason.NoTarget;
            return;
        }

        if (owner != null && !owner.TakeRound())
        {
            Hold = HoldReason.NoAmmo;
            return;
        }

        Hold = HoldReason.None;

        // 포성은 방출이다. Emission에 안 더하는 이유는 그쪽이 탐지 **거리**의 배수라 사거리가 늘어나서다 -
        // 여기 틱스탬프는 "지금 시끄럽다"만 말하고 Campaign의 추격·열기가 그것을 읽는다.
        if (owner != null)
            owner.lastFireTick = TickManager.currentTick;

        _pending -= 1f;
        Fire();
    }

    /// <summary>
    /// 포구 앞 첫 물체가 아군인가. 아니면 쏜다.
    ///
    /// 조준할 때가 아니라 격발 직전에 본다. 조준 시점에 보면 두 가지가 틀린다 - 포신은
    /// 아직 표적 쪽으로 다 안 돌았고(선회 중이다), 막혔다고 조준을 놓으면 포탑이 표적
    /// 추적을 통째로 그만둔다. 안 쏘는 것과 안 겨누는 것은 다른 일이다.
    ///
    /// 레이는 포구에서 출발한다 - 탄이 태어나는 자리와 같아야, 자기가 올라앉은 판을
    /// 자기가 맞는 것으로 세지 않는다. 반대로 포신을 함내로 돌리면 포구가 선체 안에 들어가
    /// 아군 판이 0거리에서 잡히고, 그래서 배를 관통해 반대편을 쏘는 것도 여기서 막힌다.
    ///
    /// 첫 물체만 본다. 적 뒤에 아군이 있는 것은 막지 않는다 - 탄은 적에게 먼저 닿는다.
    /// 잔해는 Ship이 아니라서 막지 않는다. 자기 배의 파편은 쏴서 치워도 된다.
    /// </summary>
    /// <summary>
    /// 사선 검사가 반드시 덮어야 하는 거리(m). **자기 배의 대각선이다** - def의
    /// friendlyCheckRange가 배보다 짧으면 후미 포탑이 자기 뱃머리를 못 보고 그대로 쏜다.
    /// destroyer가 69 m인데 pd20의 값이 60이라 정확히 그 일이 났다.
    ///
    /// def 숫자를 손으로 맞추지 않는 이유: 배가 커질 때마다 사람이 모든 포의 값을 다시
    /// 적어야 하고, 그 규칙은 언젠가 반드시 잊힌다. 배 길이는 배가 아는 값이라 여기서 끌어온다.
    /// 0이면(격자를 아직 모르는 포탑) 예전처럼 def 값만 쓴다.
    /// </summary>
    private float _ownSpan;
    private bool _spanRead;

    private float OwnSpan()
    {
        // Awake에서 읽으면 안 된다 - 그때는 Ship.Awake가 아직 배를 안 지어서 맵이 없다.
        if (_spanRead)
            return _ownSpan;

        _spanRead = true;

        ShipGrid.Map map = owner != null ? owner.DesignMap : null;

        if (map != null)
            _ownSpan = Mathf.Sqrt(map.width * map.width + map.height * map.height)
                     * ShipGrid.CellSize;

        return _ownSpan;
    }

    /// <summary>
    /// 이 틱에 쏘면 탄이 실제로 갈 자리와 방향. **포신 방향이 아니다** - 탄은 포구 속도를
    /// 물려받으므로(<see cref="Fire"/>) 실제 탄도는 `포신 x 탄속 + 포구 속도`다.
    ///
    /// 40 m/s로 움직이는 배에서 탄속이 900이면 그 둘이 2.5도 벌어지고, 35 m 반선체 끝에서
    /// 1.5 m가 샌다. M12의 fireArc가 0.7도라 **조준 정밀도보다 이 어긋남이 크다** - 사선
    /// 검사가 포신 선만 보면 통과시킨 탄이 자기 외판을 긁는다.
    ///
    /// 발사와 검사가 이 한 함수를 같이 쓰는 것이 요점이다. 두 벌로 두면 언젠가 한쪽만
    /// 고치고, 그 증상이 "가끔 내 배에 맞는다"라 원인이 한참 안 보인다.
    /// </summary>
    protected void MuzzleShot(out Vector2 muzzle, out Vector2 direction, out Vector2 inherited)
    {
        // **포대가 아니라 터렛이다.** 2단이 아닌 포탑은 _turret이 자기 자신이라 같은 값이다.
        Vector2 barrel = _turret.up;

        muzzle = (Vector2)_turret.position + barrel * muzzleOffset;
        inherited = Vector2.zero;

        // 매번 부모를 훑는 것은 캐시를 안 한 것이 아니라 못 하는 것이다 - 포탑은 판이
        // 떨어져 나가면 잔해로 재부모화되므로 Awake에 잡아 둔 참조는 그 뒤로 남의 몸이다.
        Rigidbody2D platform = GetComponentInParent<Rigidbody2D>();

        if (platform != null)
        {
            // 포구는 v + w x r로 움직인다 - 중심 속도만 주면 도는 배의 현측 포탑이 틀린다.
            inherited = platform.linearVelocity
                + Ballistics.Rotate(muzzle - platform.worldCenterOfMass, 90f)
                    * (platform.angularVelocity * Mathf.Deg2Rad);
        }

        Vector2 launch = barrel * muzzleSpeed + inherited;

        direction = launch.sqrMagnitude > 1e-6f ? launch.normalized : barrel;
    }

    protected virtual bool LineIsClear()
    {
        if (owner == null)
            return true;

        MuzzleShot(out Vector2 muzzle, out Vector2 direction, out _);

        // **자기 배는 거리와 무관하게 봐야 한다.** def 값은 "남의 아군까지 보는 거리"로
        // 남고, 자기 선체는 이 하한이 덮는다.
        float range = Mathf.Max(friendlyCheckRange, OwnSpan());

        RaycastHit2D hit = Physics2D.Raycast(muzzle, direction, range);

        if (hit.collider == null)
            return true;

        Ship blocking = hit.collider.GetComponentInParent<Ship>();

        return blocking == null || blocking.team != owner.team;
    }

    /// <summary>
    /// 마운트 전방의 월드 방향 = 판 기준 배치 rot을 부모(판·선체)의 회전으로 돌린 것.
    /// </summary>
    private Vector2 MountForward()
    {
        // 기준은 판이 아니라 **선체**다. 포는 판 밑으로 재부모화되는데(경사판이면 45도)
        // 배치 rot은 선체 기준이라, 부모 판을 기준으로 돌리면 경사판 위 포의 사각이 45도 틀어진다.
        Vector3 local = Quaternion.Euler(0f, 0f, _mountLocalZ) * Vector3.right;
        Transform hull = owner != null ? owner.transform : transform.parent;
        return hull != null
            ? ((Vector2)hull.TransformVector(local)).normalized
            : (Vector2)local;
    }

    /// <summary>Slew의 want와 같은 눈금(포신이 up이라 -90)으로 잰 마운트 전방.</summary>
    private float MountWant()
    {
        Vector2 f = MountForward();
        return Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg - 90f;
    }

    /// <summary>이 점이 사각 안인가. traverse 0이면 언제나 참.</summary>
    private bool InArc(Vector2 worldPoint)
    {
        if (traverse <= 0f)
            return true;

        Vector2 d = worldPoint - (Vector2)_turret.position;
        float want = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 90f;
        return Mathf.Abs(Mathf.DeltaAngle(MountWant(), want)) <= traverse;
    }

    /// <summary>
    /// 포신을 목표 쪽으로 slewRate만큼 돌리고, 남은 조준 오차를 도 단위로 준다.
    /// 사각 밖이면 한계각까지만 돌고 inArc가 false다 - 오차는 한계각 기준이라 포탑은
    /// 거기 도달해 선다.
    /// </summary>
    private float Slew(Vector2 target, float dt, out bool inArc) => Slew(target, dt, out inArc, out _);

    /// <param name="signed">부호 있는 조준 오차(도). 부호가 있어야 변화율을 잴 수 있다 - 절댓값은 조준선을 지날 때 꺾여서 미분이 폭발한다.</param>
    private float Slew(Vector2 target, float dt, out bool inArc, out float signed)
    {
        inArc = true;
        signed = 180f;
        Vector2 toTarget = target - (Vector2)_turret.position;

        if (toTarget.sqrMagnitude < 1e-6f)
            return 180f;

        // -90도: 포신이 transform.up이라 0도가 오른쪽이 아니라 위다
        float want = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - 90f;
        float mount = traverse > 0f ? MountWant() : 0f;

        if (traverse > 0f)
        {
            float off = Mathf.DeltaAngle(mount, want);

            if (Mathf.Abs(off) > traverse)
            {
                inArc = false;
                want = mount + Mathf.Clamp(off, -traverse, traverse);
            }
        }

        // **선회는 월드 기준이다. 안정화 마운트라서 그렇다.**
        //
        // 로컬로 돌려 봤다가 되돌렸다. 로컬이면 선체 회전이 그대로 조준 오차가 되는데,
        // 이 배들의 종단 각속도가 26~80도/초(angleAccel / angleDrag)인 반면 포탑 slewRate는
        // 9~30도/초다 - **선회 예산 전부를 배를 되잡는 데 쓰고도 모자란다.** 표적을 겨눈
        // 적이 없는 포탑이 되고, 증상이 "AI 포탑이 병신이 됐다"였다.
        //
        // 실제 함포도 자이로 안정화라 함체 동요를 마운트가 흡수한다. 그래서 slewRate는
        // "포탑이 **표적을 바꿀 때**의 속도"지 "배를 되잡는 속도"가 아니다.
        //
        // 사격각(블라인드 아크)을 넣을 때는 그때 함체 기준으로 **판정만** 하면 된다 -
        // 어디를 겨눌 수 있느냐는 선체 기준이고, 얼마나 빨리 겨누느냐는 월드 기준이다.
        // 그 둘은 다른 질문이라 같은 좌표계일 이유가 없다.
        float have = _turret.eulerAngles.z;
        float next = Mathf.MoveTowardsAngle(have, want, slewRate * dt);

        // **want를 자른 것만으로는 안 끝난다.** mount는 선체를 따라 매 틱 움직이는데
        // MoveTowardsAngle은 지난 틱의 have에서 출발한다 - 배가 slewRate보다 빨리 돌면
        // have가 "지난 틱 경계" 근처에 있다가 mount가 반대로 움직여, 거기서 이번 틱
        // want로 가는 중간값(next)이 지금 사각을 넘어선 채로 찍힐 수 있다. 결과 next
        // 자체를 다시 지금 mount 기준으로 자른다 - 화면에 실제로 걸리는 값이 이거다.
        if (traverse > 0f)
            next = mount + Mathf.Clamp(Mathf.DeltaAngle(mount, next), -traverse, traverse);

        _turret.rotation = Quaternion.Euler(0f, 0f, next);

        signed = Mathf.DeltaAngle(next, want);
        return Mathf.Abs(signed);
    }

    protected virtual void Fire()
    {
        // **포구와 물려받는 속도는 LineIsClear와 같은 함수에서 온다.** 사선 검사가 통과시킨
        // 그 선으로 정확히 쏘지 않으면, 검사를 통과한 탄이 자기 외판을 긁는다.
        //
        // 반동과 Launch에는 여기서 다시 읽은 **포신** 방향을 준다 - 포가 만든 운동량은
        // 포구 속도 몫뿐이고, 물려준 속도는 배가 이미 갖고 있던 것이라 배에서 뺄 이유가 없다.
        MuzzleShot(out Vector2 muzzle, out _, out Vector2 inherited);

        Vector2 direction = _turret.up;

        var shell = DefDatabase.Spawn(projectile, null, muzzle, _turret.eulerAngles.z) as Projectile;

        if (shell == null)
            return;

        Rigidbody2D body = GetComponentInParent<Rigidbody2D>();

        shell.Launch(direction, muzzleSpeed, inherited, body);

        // 탄이 가져간 만큼 배가 뒤로 간다. **회전을 만드는 코드가 없는 것이 요점이다** -
        // AddForceAtPosition이 무게중심에서 벗어난 힘을 알아서 토크로 바꾼다. 뱃머리
        // 포탑이 배를 돌리는 것도, 무게중심에 놓은 포탑이 안 돌리는 것도 분기문이 없다.
        //
        // 미는 자리가 포구인지 포탑 중심인지는 결과가 같다. 둘 다 같은 힘의 작용선 위에
        // 있고, 작용선을 따라 점을 옮겨도 r x F는 안 변한다. 포구가 더 정직해서 포구다.
        //
        // 그리고 미는 자리는 **병진에 아무 영향이 없다.** 무게중심에 놓든 30 m 밖에
        // 놓든 배는 같은 속도로 밀린다 - 보존되는 것이 에너지가 아니라 운동량이라서다.
        // 자리가 정하는 것은 그 운동량이 안에서 어떻게 배분되느냐(= 회전이 붙느냐)뿐이다.
        // 회전 에너지가 공짜로 생긴 것처럼 보이는데, 배가 가져가는 몫이 화약 에너지의
        // 0.1%라 그 차이가 노이즈에 묻힌다.
        //
        // 매번 부모를 훑는 것은 캐시를 안 한 것이 아니라 못 하는 것이다. 포탑은 판이
        // 떨어져 나가면 잔해로 재부모화되므로, Awake에 잡아 둔 Rigidbody2D는 그 뒤로
        // 남의 배를 민다. 없으면 그냥 안 민다 - 사격장 거치대가 그렇다.
        //
        // angularDamping이 0이라 이 회전은 저절로 안 멎는다. 현측 사격이 배를 계속
        // 돌리고 조타 RCS가 그걸 붙잡는다. 그것도 여기 코드가 아니다.
        // 반동은 포가 만든 운동량만 본다 - 물려준 속도는 배가 이미 갖고 있던 것이다.
        if (body != null)
        {
            body.AddForceAtPosition(
                -direction * (shell.mass * muzzleSpeed * Ballistics.RecoilScale),
                muzzle,
                ForceMode2D.Impulse);
        }

        SoundManager.AudioShot("Cannon", muzzle);

        // 빈 이름이면 VfxOneShot이 알아서 무시한다. -90: 그래프가 +x로 뿜는데 포신은 +y다.
        // 수명 1초: 발사 연출은 순간이고, 3600 RPM이면 초당 60개가 태어난다 - 기본 4초로
        // 두면 동시 240개가 산다.
        VfxOneShot.Play(muzzleVfx, muzzle, 1f, _turret.eulerAngles.z - 90f, muzzleFirePower, muzzleColor);
    }
}
