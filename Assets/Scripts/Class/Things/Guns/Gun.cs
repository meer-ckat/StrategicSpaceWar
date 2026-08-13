using UnityEngine;
using Core;

/// <summary>
/// 포탑 하나. 포신은 transform.up을 향한다 - Projectile이 발사 방향을 그렇게 읽는다.
/// 조준 대상을 어떻게 고르느냐만 파생 클래스가 정하고, 선회·발사·내구는 전부 공통이다.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public abstract class Gun : Thing, IDamageable
{
    [Header("무장")]
    public Projectile bulletPrefab;
    public float muzzleSpeed = 900f;
    public float roundsPerMinute = 60f;

    [Header("선회")]
    public float slewRate = 30f;    // 도/초
    public float fireArc = 2f;      // 도. 조준 오차가 이 안에 들어와야 쏜다

    /// <summary>
    /// 포구를 선체 밖으로 밀어내는 거리. 격자 한 칸이 1m이므로 최소 1m는 있어야
    /// 자기가 올라앉은 장갑판을 쏘지 않는다.
    /// </summary>
    public float muzzleOffset = 1f;

    [Header("내구")]
    public float maxHealth = 60f;

    private float _health;
    private float _pending;         // 발사 대기량. 1이면 한 발
    private Ship owner;

    public bool Neutralized => _health <= 0f;
    public float Health01 => maxHealth > 0f ? _health / maxHealth : 0f;

    protected override void Awake()
    {
        base.Awake();

        _health = maxHealth;
        owner = GetComponentInParent<Ship>();

        if (bulletPrefab == null)
            Debug.LogError($"[Gun] {name}에 bulletPrefab이 없다.", this);
    }

    public void TakeDamage(float amount)
    {
        if (amount <= 0f)
            return;

        _health = Mathf.Max(0f, _health - amount);
    }

    /// <summary>주포는 커서를, 고정 반격 포탑은 가장 가까운 적을 본다.</summary>
    protected abstract bool TryGetTarget(out Vector2 worldPoint);

    /// <summary>
    /// 조준과 격발은 다른 축이다. 수동 주포는 커서를 계속 따라가되 방아쇠를 당길 때만
    /// 쏘고, 자동 반격 포탑은 표적이 잡히면 그대로 쏜다 - 그래서 기본값이 true다.
    /// </summary>
    protected virtual bool WantsToFire => true;

    public override void OnTick()
    {
        if (Neutralized || bulletPrefab == null)
            return;

        // 포수가 다른 자리에 가 있으면 포탑은 멈춘다
        if (owner != null && !owner.isGunnerReady)
            return;

        if (!TryGetTarget(out Vector2 target))
        {
            _pending = 0f;
            return;
        }

        float dt = TickManager.TickDeltaTime;
        float error = Slew(target, dt);

        // 방아쇠를 놓고 있는 동안에도 포신은 따라간다. 장전된 채로 대기시켜 두어야
        // 당기는 순간 첫 발이 나가고, 기다린 시간만큼 연사가 쏟아지지도 않는다.
        if (!WantsToFire)
        {
            _pending = 1f;
            return;
        }

        // 1로 막아두는 이유: 조준하는 동안 쌓인 발사량이 조준선에 들어오는 순간
        // 한꺼번에 쏟아지는 것을 막는다. 대신 틱당 최대 한 발 - 3600 RPM이 천장이다.
        _pending = Mathf.Min(_pending + roundsPerMinute / 60f * dt, 1f);

        if (error > fireArc || _pending < 1f)
            return;

        _pending -= 1f;
        Fire();
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

        Projectile shell = Instantiate(bulletPrefab, muzzle, transform.rotation);
        shell.Launch(direction, muzzleSpeed);
    }
}
