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

    [Header("무장")]

    /// <summary>
    /// 쏘는 탄의 defName. 프리팹 참조가 아니라 이름이다 - def끼리는 GUID가 없으니 이것이
    /// 유일하게 가능한 방식이고, 동시에 모딩이 열리는 지점이다.
    /// </summary>
    public string projectile;

    public float muzzleSpeed = 900f;
    public float roundsPerMinute = 60f;

    [Header("조준")]
    [SerializeField] private AimMode aim = AimMode.FollowOwner;

    [Header("선회")]
    public float slewRate = 30f;    // 도/초
    public float fireArc = 2f;      // 도. 조준 오차가 이 안에 들어와야 쏜다

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

    [Header("내구")]
    public float maxHealth = 60f;

    private float _health;
    private float _pending;         // 발사 대기량. 1이면 한 발

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

    /// <summary>주포는 커서를, 자동 포탑은 가장 가까운 적을 본다.</summary>
    protected virtual bool TryGetTarget(out Vector2 worldPoint)
        => IsManual ? TryAimAtCursor(out worldPoint) : TryAimAtNearestHostile(out worldPoint);

    /// <summary>
    /// 조준과 격발은 다른 축이다. 수동 주포는 커서를 계속 따라가되 방아쇠를 당길 때만
    /// 쏘고, 자동 포탑은 표적이 잡히면 그대로 쏜다.
    /// </summary>
    protected virtual bool WantsToFire =>
        !IsManual || (Mouse.current != null && Mouse.current.leftButton.isPressed);

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

        Ship target = owner.NearestHostile();

        if (target == null)
            return false;

        // 편차 조준은 넣지 않는다. 탄속 900 m/s에 교전거리 40 m면 비행시간 0.045초,
        // 함선이 10 m/s로 움직여도 리드가 0.45 m라 함선 크기보다 작다.
        // 원거리 교전이 생기면 여기에 세 줄 추가하면 된다.
        worldPoint = target.transform.position;
        return true;
    }

    public override void OnTick()
    {
        if (Neutralized)
            return;

        // 포수가 다른 자리에 가 있으면 포탑은 멈춘다.
        // StillAboard: 이 포탑이 얹힌 판이 잔해로 떨어져 나갔으면 owner는 여전히 살아 있는
        // Ship을 가리키지만 더 이상 이 배의 포탑이 아니다. 우주로 날아가면서 쏘면 안 된다.
        // owner가 애초에 없는 포탑(테스트용 거치대)은 예전처럼 그냥 쏜다.
        if (owner != null && (!Ship.StillAboard(this, owner) || !owner.isGunnerReady))
            return;

        if (!TryGetTarget(out Vector2 target))
        {
            _pending = 0f;
            return;
        }

        float dt = TickManager.TickDeltaTime;
        float error = Slew(target, dt);

        _pending = Mathf.Min(_pending + roundsPerMinute / 60f * dt, 1f);

        if (!WantsToFire)
            return;

        // 1로 막아두는 이유: 조준하는 동안 쌓인 발사량이 조준선에 들어오는 순간
        // 한꺼번에 쏟아지는 것을 막는다. 대신 틱당 최대 한 발 - 3600 RPM이 천장이다.
        _pending = Mathf.Min(_pending + roundsPerMinute / 60f * dt, 1f);

        // LineIsClear가 맨 뒤인 것은 성능이 아니라 의미다. 앞의 둘이 통과했을 때만
        // "이 틱에 정말 쏜다"이고, 그때의 포신 방향이 탄이 실제로 갈 선이다.
        // 막혀서 안 쏜 발은 _pending을 소모하지 않으므로 사선이 열리는 순간 나간다.
        if (error > fireArc || _pending < 1f || !LineIsClear())
            return;

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
    private bool LineIsClear()
    {
        if (owner == null)
            return true;

        Vector2 direction = transform.up;
        Vector2 muzzle = (Vector2)transform.position + direction * muzzleOffset;

        RaycastHit2D hit = Physics2D.Raycast(muzzle, direction, friendlyCheckRange);

        if (hit.collider == null)
            return true;

        Ship blocking = hit.collider.GetComponentInParent<Ship>();

        return blocking == null || blocking.team != owner.team;
    }

    /// <summary>포신을 목표 쪽으로 slewRate만큼 돌리고, 남은 조준 오차를 도 단위로 준다.</summary>
    private float Slew(Vector2 target, float dt)
    {
        Vector2 toTarget = target - (Vector2)transform.position;

        if (toTarget.sqrMagnitude < 1e-6f)
            return 180f;

        // -90도: 포신이 transform.up이라 0도가 오른쪽이 아니라 위다
        float want = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg - 90f;
        float have = transform.eulerAngles.z;
        float next = Mathf.MoveTowardsAngle(have, want, slewRate * dt);

        transform.rotation = Quaternion.Euler(0f, 0f, next);

        return Mathf.Abs(Mathf.DeltaAngle(next, want));
    }

    protected virtual void Fire()
    {
        Vector2 direction = transform.up;
        Vector2 muzzle = (Vector2)transform.position + direction * muzzleOffset;

        var shell = DefDatabase.Spawn(projectile, null, muzzle, transform.eulerAngles.z) as Projectile;

        if (shell == null)
            return;

        shell.Launch(direction, muzzleSpeed);

        SoundManager.AudioShot("Cannon", muzzle);
    }
}
