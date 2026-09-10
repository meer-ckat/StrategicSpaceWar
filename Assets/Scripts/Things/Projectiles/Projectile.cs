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

    // Launch 전용 스크래치. _hits와 절대 같이 쓰면 안 된다 - 파편은 부모 탄의 OnTick
    // **한가운데서** Launch되므로, 부모가 아직 읽는 _hits를 여기서 덮으면 조용히 깨진다.
    private static readonly RaycastHit2D[] _launchHits = new RaycastHit2D[8];
    private static readonly SurfaceSet _surfaces = new();
    private static int _nextId; //다음 Projectile ID 할당
    private Rigidbody2D _ownerRigidbody;

    protected override void Awake()
    {
        spallLayer = armorLayer | spallLayer;
        base.Awake();
        ProjectileId = ++_nextId;

        // 속도가 0이면 리제로. **Launch를 안 부른다** - 파생의 Launch(표적 탐색, 산개
        // 준비)가 Awake 한가운데서 돌 이유가 없고, 거기서 예외가 나면 Unity가 이
        // 컴포넌트를 꺼 버려 자폭도 수명도 없는 좀비 미사일이 표적에 박힌 채 남는다.
        // 진짜 Launch는 쏜 쪽이 스폰 직후에 부른다 - 이건 그때까지의 안전값일 뿐이다.
        if (velocity.sqrMagnitude <= 0f)
            velocity = (Vector2)transform.up * (muzzleSpeed >= 1 ? muzzleSpeed : 1f);
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

        // 0.5틱 선행 이동 - 단 **검사 없이 순간이동하면 안 된다.** 10000 m/s면 반 틱이
        // 83 m라 총구 앞의 판을 통째로 건너뛰고, 증상이 "10 m 앞인데 안 맞는다"다.
        // 선행 구간을 레이로 재서 첫 남 직전에 멈춘다 - 명중 판정 자체는 다음 틱
        // OnTick이 그 자리에서 정상 경로로 한다. 자기 배(owner)는 통과.
        float step = velocity.magnitude * TickManager.TickDeltaTime * 0.5f;

        if (step > 0f)
        {
            Vector2 dir = velocity.normalized;

            int n = Physics2D.RaycastNonAlloc(
                transform.position, dir, _launchHits, step, armorLayer | moduleLayer);

            for (int i = 0; i < n; i++)
            {
                RaycastHit2D h = _launchHits[i];

                if (owner != null && h.collider.attachedRigidbody == owner)
                    continue;

                step = Mathf.Min(step, Mathf.Max(0f, h.distance - Ballistics.Epsilon));
            }

            transform.position += (Vector3)(dir * step);
        }

        if (velocity.sqrMagnitude > 0f) //속도가 유효하다면
            transform.up = velocity.normalized;
        Target = target;
    }

    /// <summary>
    /// 이 탄은 끝났다. **Unity의 == null이나 enabled를 죽음의 신호로 쓰지 않는다** -
    /// <c>Destroy</c>는 실제 파괴를 프레임 끝으로 미루므로 그 사이의 상태를 어떻게
    /// 읽어야 하는지가 Unity 버전과 경로에 달려 있다. 우리가 정한 플래그 하나면
    /// 그 질문이 아예 없다.
    /// </summary>
    protected bool Spent { get; private set; }

    /// <summary>
    /// 죽는 유일한 문. **두 번 불러도 안전하다** - 틱 루프가 한 틱에 판을 여러 장
    /// 지나므로 같은 탄이 여러 경로에서 끝날 수 있다.
    ///
    /// 이 문이 있어야 하는 진짜 이유는 <see cref="SpawnHeavyFragments"/>다. 그쪽은
    /// <c>Instantiate(this)</c>로 자기를 복제하는데, 이미 <c>Destroy</c>된 뒤라면
    /// **컴포넌트가 꺼진 상태까지 복제돼서** 틱을 못 받는 유령이 태어난다. 죽었다는
    /// 사실을 복제 전에 물어볼 수 있어야 그 창이 닫힌다.
    /// </summary>
    protected void Retire()
    {
        if (Spent)
            return;

        Spent = true;
        Destroy(gameObject);
    }

    /// <summary>
    /// 던져서 명단에서 빠졌으면 그 자리에서 죽는다. **탄에게는 이것이 유일한 출구다** -
    /// 수명 검사도(lifeTick) 스톨도 자폭도 전부 <see cref="OnTick"/> 안에 있어서, 틱을
    /// 못 받는 탄은 죽을 길이 하나도 안 남는다. 증상은 "컴포넌트가 전부 꺼진 미사일이
    /// 그 자리에 박혀 있다"이고, 원인인 예외와 화면상 한참 떨어져 보인다.
    /// </summary>
    public override void OnTickThrew() => Retire();

    /// <summary>
    /// 한 틱치 시간을 다 쓸 때까지 앞으로 훑는다. 판을 뚫으면 남은 시간으로 계속 가므로
    /// 한 틱 안에서 외벽을 뚫고 안쪽 격벽까지 맞을 수 있다.
    /// </summary>
    public override void OnTick()
    {
        if (TickManager.currentTick - spawnTick >= lifeTick) //Dead man's switch
        {
            Retire();
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
                Retire();
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

            // **파편은 Apply보다 먼저 낳는다.** SpawnHeavyFragments가 `Instantiate(this)`로
            // 자기 자신을 복제하는데, Apply는 Explode에서 `Destroy(gameObject)`를 부를 수
            // 있다(blastDamage가 있는 탄 전부). Destroy는 **그 자리에서 컴포넌트를 전부
            // 끄고** 실제 파괴만 프레임 끝으로 미루므로, 그 뒤에 복제하면 꺼진 상태까지
            // 같이 복제된다 - 태어나자마자 틱을 못 받아 수명도 자폭도 없는 유령이
            // 탄착점에 영원히 남는다. 증상이 "미사일이 목표 지점에 컴포넌트가 다 꺼진 채
            // 박혀 있다"였고, 이름이 `<def>(Clone)`이라 진짜 탄과 구분이 안 갔다.
            //
            // 순서를 바꿔도 파편은 한 톨도 안 달라진다 - 읽는 값이 전부 result(Apply보다
            // 먼저 나온 것)와 탄의 정체(mass·caliber·generation)뿐이고, Apply가 바꾸는
            // velocity·integrity·state는 하나도 안 본다.
            bool heavy = result.heavySpall && generation < Ballistics.MaxFragmentGeneration;

            if (heavy)
                SpawnHeavyFragments(result);

            Apply(result, dir);

            // 파편 쪽과 달리 이건 자리를 안 옮긴다 - 피해를 넣는 일이라 Apply 뒤라는
            // 순서 자체가 결과의 일부다.
            if (!heavy)
                SpallResolver.Resolve(result, spallLayer);

            // **Apply가 우리를 죽였으면 루프를 끝낸다.** Explode는 blastDamage가 있는
            // 탄 전부에서 Retire를 부르는데 그것만으로는 루프가 안 멈춘다 - 관통이면
            // 아래 두 검사(Blocked/Shattered)에 안 걸려 다음 판까지 계속 돌고, 거기서
            // 또 터지면서 이미 꺼진 자기를 복제한 파편(유령)을 낳는다. 한 발이 한 번만
            // 터진다는 것 자체가 규칙이라, 그 규칙을 여기서 지킨다.
            if (Spent)
                return;

            lastCollider = _surfaces.primaryCollider;

            // push off AFTER Apply, so a ricochet leaves along its new heading
            if (velocity.sqrMagnitude > 0f)
                position += velocity.normalized * Ballistics.Epsilon;

            if (result.outcome == HitOutcome.Blocked)
            {
                velocity = Vector2.zero;
                transform.position = position;
                Retire();
                return;
            }

            // heavy fragments were already resolved by SpallResolver
            if (state == ShellState.Shattered)
            {
                transform.position = position;
                Retire();
                return;
            }
        }

        transform.position = position;

        if (velocity.sqrMagnitude > 0f)
            transform.up = velocity.normalized;
    }
}
