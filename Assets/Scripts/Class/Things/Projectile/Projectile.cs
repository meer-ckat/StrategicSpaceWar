using UnityEngine;
using Core;
using System.Reflection;

/// <summary>
/// 탄 한 발. 한 틱 동안 자기 경로를 훑으면서, 만나는 판마다 판정을 받고 계속 간다.
///
/// The shell carries its own state. Penetration is derived, never stored,
/// so speed and integrity losses follow it into the next wall automatically.
///
/// 같이 보는 파일:
///   Projectile.Surfaces.cs  레이캐스트해서 이번에 닿은 면들을 모으는 쪽
///   Projectile.Damage.cs    판정 결과를 실제로 적용하는 쪽 (장갑 HP, 중파편 생성)
///   PenetrationManager.cs   판정 그 자체
/// </summary>
public abstract partial class Projectile : Thing, ITickLate
{
    [Header("Projectile")]
    public float mass = 5f;              // kg
    public float caliber = 100f;         // mm
    public float shatterVelocity = 800f; // m/s
    public float penetrationK = 0.18f; //영차원 상수
    //탄두의 데미지
    public float blastDamage;

    public int lifeTick = 1800;          // 30 s
    public float muzzleSpeed = 900f;     // m/s, used only if nothing calls Launch
    public LayerMask armorLayer;    // 본체 레이캐스트 - Armor 레이어만
    public LayerMask moduleLayer;   // 직격 - Module 레이어만
    private LayerMask spallLayer; //캐시 a | m

    [Header("Runtime")]
    public Vector2 velocity; //현재 속도
    public float integrity = 1f;         // 0..1. Shattered does NOT mean 0.
    public ShellState state = ShellState.Intact;
    public int hitIndex;
    public int generation; //generation 0은 처음 발사된 탄두, 이후엔 파편들.
    public Transform Target; //Raycast해서 만약 hit.collider를 get 했다면 그거 transform 넘겨주는 걸로, 나중에 레이저 포인터 기능 넣어서 유도함이 레이저 유도 대신해주는 기믹 같은 거 추가할 거임.

    public int ProjectileId { get; private set; } //Projectile을 구분하기 위한 ID
    public float Speed => velocity.magnitude;
    public float IntegrityFactor => integrity;

    public float Penetration =>
        Ballistics.Penetration(penetrationK, IntegrityFactor, Speed, mass, caliber);

    // shared scratch - consumed synchronously inside one loop iteration
    private static readonly RaycastHit2D[] _hits = new RaycastHit2D[4]; //다중 충돌 처리
    private static readonly SurfaceSet _surfaces = new();
    private static int _nextId; //다음 Projectile ID 할당
    private Rigidbody2D _ownerRigidbody;

    protected override void Awake()
    {
        spallLayer = armorLayer | spallLayer;
        base.Awake();
        ProjectileId = ++_nextId;

        if (velocity.sqrMagnitude <= 0f) //만약 속도가 개같다면 리제로
            Launch(transform.up, muzzleSpeed >= 1 ? muzzleSpeed : 1); //1 이상으로 고정
    }

    /// <summary>
    /// 포구 속도에 **쏜 자리의 속도를 더한다.** 달리는 배에서 쏜 탄은 배의 속도를 안고
    /// 나간다 - 그게 물리이기도 하고, 안 그러면 자기 배를 맞힌다: 탄이 우주 기준 직선으로
    /// 나가는 동안 배는 옆으로 미끄러져서 그 직선 위로 자기 외판을 들이민다. 25 m/s로
    /// 항행하며 현측 사격하면 선체를 빠져나가는 10 m 동안 배가 0.28 m 옆으로 가는데,
    /// 포구가 외판에 붙어 있으니 그 정도면 옆 판을 긁는다.
    ///
    /// 반동은 이 값을 안 본다. 포가 만든 운동량은 포구 속도 몫뿐이고, 물려받은 속도는
    /// 배가 이미 갖고 있던 것이라 배에서 빼야 할 이유가 없다.
    /// </summary>
    public virtual void Launch(Vector2 direction, float speed, Vector2 inherited = default, Rigidbody2D owner = null, Transform target = null)
    {
        _ownerRigidbody = owner;
        velocity = direction.normalized * speed + inherited;
        transform.position += (Vector3)velocity * TickManager.TickDeltaTime / 2; // 0.5틱 먼저 이동 판정

        if (velocity.sqrMagnitude > 0f) //속도가 유효하다면
            transform.up = velocity.normalized;
        Target = target;
    }

    /// <summary>
    /// 한 틱치 시간을 다 쓸 때까지 앞으로 훑는다. 판을 뚫으면 남은 시간으로 계속 가므로
    /// 한 틱 안에서 외벽을 뚫고 안쪽 격벽까지 맞을 수 있다.
    /// </summary>
    public override void OnTick()
    {
        if (TickManager.currentTick - spawnTick >= lifeTick) //Dead man's switch
        {
            Destroy(gameObject);
            return;
        }

        Vector2 position = transform.position;
        float remainingTime = TickManager.TickDeltaTime; // 1틱당 계산할 수 있는 최대 시간
        Collider2D lastCollider = null;

        for (int guard = 0; guard < Ballistics.MaxHitsPerTick && remainingTime > 0f; guard++)
        {
            float speed = velocity.magnitude;
            if (speed <= Ballistics.MinSpeed) //스톨 시 제거
            {
                transform.position = position;
                Destroy(gameObject);
                return;
            }

            Vector2 dir = velocity / speed;
            float distance = speed * remainingTime;

            if (!CollectSurfaces(position, dir, distance, lastCollider, _ownerRigidbody)) //레이케스트 진행, 만약 아무것도 없으면 이동하고 다음 틱 기다리기.
            {
                // 판을 안 만났어도 이번 구간에 모듈이 있었으면 그건 맞은 것이다
                StrikeModules(position, dir, distance);
                position += dir * distance; //그리고 이번틱을 스킵하고 이동한다.
                break;
            }

            // 판보다 앞에 있던 모듈이 먼저다. 여기서 느려진 속도로 장갑 판정을 받는 것이
            // 실제 순서와 맞다.
            StrikeModules(position, dir, _surfaces.minDistance);

            remainingTime -= _surfaces.minDistance / speed; //먼저 탄착 위치로 이동 및 비행시간도 그에 따라 줄임
            position = _surfaces.hitPoint;

            HitResult result = PenetrationManager.Resolve(CaptureState(), _surfaces);
            PenetrationManager.Record(result, _surfaces);

            Apply(result, dir);

            if (result.heavySpall && generation < Ballistics.MaxFragmentGeneration)
                SpawnHeavyFragments(result);
            else
                SpallResolver.Resolve(result, spallLayer);

            lastCollider = _surfaces.primaryCollider;

            // push off AFTER Apply, so a ricochet leaves along its new heading
            if (velocity.sqrMagnitude > 0f)
                position += velocity.normalized * Ballistics.Epsilon;

            if (result.outcome == HitOutcome.Blocked)
            {
                velocity = Vector2.zero;
                transform.position = position;
                Destroy(gameObject);
                return;
            }

            // heavy fragments were already resolved by SpallResolver
            if (state == ShellState.Shattered)
            {
                transform.position = position;
                Destroy(gameObject);
                return;
            }
        }

        transform.position = position;

        if (velocity.sqrMagnitude > 0f)
            transform.up = velocity.normalized;
    }
}
