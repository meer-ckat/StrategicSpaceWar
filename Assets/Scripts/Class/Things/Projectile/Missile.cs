using UnityEngine;
using Core;

public class Missile : Projectile
{
    public float thrust;
    public float fuelTime;
    float _currentTime = 0;

    /// <summary>도/틱. 유도가 한 틱에 꺾을 수 있는 최대 각.</summary>
    public float maxRotationPerTick;

    [Header("발사 직후 산개")]

    /// <summary>산개 중 조준선에서 옆으로 최대 몇 도 벗어나는가.</summary>
    public float weaveAngle = 60f;

    /// <summary>부풀었다 돌아오는 데 걸리는 시간(초). sin 반주기 하나다.</summary>
    public float weaveTime = 1.5f;

    /// <summary>어느 쪽으로 부푸는가(±1). 발사대가 자기 위치로 정해준다 - def 값이 아니다.</summary>
    [System.NonSerialized] public float side = 1f;

    /// <summary>놓침 판정 내적 문턱. 기수와 표적 방향이 약 78도 넘게 벌어지면 놓친 것이다.</summary>
    private const float MissDot = 0.2f;

    /// <summary>놓친 채로 이만큼(초) 지나면 자폭한다.</summary>
    private const float MissFuseSeconds = 0.5f;

    private float _missedTime;
    public bool FireAndForget; // 사실 지금도 능동에 가깝긴 하지만, 얘는 조준할 필요도 없이 일단 쏘고 나서 앞에 적이 있다면 걔를 따라간다.

    /// <summary>
    /// 포탄과 달리 물려받은 속도에 방향을 맡기지 않는다 - 발사관(포신) 방향으로
    /// 튀어나가고, 배의 속도는 속력에만 남는다. 벡터로 더하면 발사 순간 기수와
    /// 진행 방향이 배의 표류 쪽으로 넘어가서, 선회 제한이 걸린 유도가 그 각도를
    /// 되감는 동안 미사일이 옆으로 날아가는 것처럼 보인다.
    /// </summary>
    public override void Launch(Vector2 direction, float speed, Vector2 inherited = default, Rigidbody2D owner = null, Transform target = null)
    {
        // owner가 없으면 탐색도 없다 - Projectile.Awake가 velocity 0일 때 owner 없이
        // Launch를 먼저 부르는데(자동 리제로), 그 호출에서 owner.transform을 읽으면
        // 모든 FireAndForget 미사일이 스폰 즉시 NRE로 죽는다.
        if (FireAndForget && target == null && owner != null)
        {
            Ship shooter = owner.GetComponent<Ship>();   // 루프 밖에서 한 번만
            float best = float.MaxValue;

            foreach (Ship ship in Ship.All)
            {
                if (ship == null || ship.Rig == null || ship.Rig == owner)
                    continue;

                // 팀 비교가 아니라 IsHostileTo다 - 그쪽이 중립(운석)과 시체
                // (IsCombatEffective)까지 같이 거른다. 술어를 손으로 다시 적으면
                // NearestHostile과 갈라지고, 증상은 "미사일만 시체를 쫓는다"다.
                // shooter가 없는 것은 사격장 거치대뿐이라 예전처럼 아무나 문다.
                if (shooter != null && !shooter.IsHostileTo(ship))
                    continue;

                Vector2 to = (Vector2)ship.transform.position - (Vector2)transform.position;

                // 발사 방향 앞쪽 원뿔(내적 > MissDot, 약 ±78도)만. **up 기준이다** -
                // 2D에서 forward는 화면 안쪽(+z)이라 내적이 항상 0에 붙어 아무나 잡힌다.
                // direction이 곧 발사 방향이라 스폰 회전에 안 기댄다.
                if (Vector2.Dot(direction.normalized, to.normalized) <= MissDot)
                    continue;

                // 원뿔 안에서 가장 가까운 것. 목록 순서로 끊으면 뒤 배가 앞 배를 가린다.
                float d = to.sqrMagnitude;

                if (d < best)
                {
                    best = d;
                    target = ship.transform;
                }
            }
        }
        // 발사대의 잠금 캐시가 6틱 낡을 수 있어서(Launcher.AcquireInterval) 이미
        // 파괴된 배의 Transform이 여기 올 수 있다. 가짜 null을 진짜 null로 접어야
        // OnTick이 그것을 "잃은 표적"으로 읽고 신관을 태운다.
        if (target == null)
            target = null;

        _hadTarget = target != null;
        base.Launch(direction, speed + inherited.magnitude, default, owner, target);
    }

    /// <summary>표적을 받은 적이 있는가. 파괴된 Transform은 == null이 돼서 이걸로만 "잃었다"를 안다.</summary>
    private bool _hadTarget;

    public override void OnTick()
    {
        _currentTime += TickManager.TickDeltaTime;

        // 멈춘 미사일은 그 자리에서 죽는다. base의 스톨 검사와 겹치지만, 그건 틱 예산
        // 루프 안이라 이 클래스의 추력이 매 틱 되살릴 수 있다 - 여기서 먼저 끊는다.
        if (Speed <= Ballistics.MinSpeed)
        {
            Destroy(gameObject);
            return;
        }

        // **이 틱의 표적을 한 번만 읽고, 죽었으면 그 자리에서 필드까지 비운다.**
        // 아래 두 자리(자폭 판정, 유도)가 각자 Target을 검사하면 판정과 조종이 다른
        // 답을 볼 수 있고, 무엇보다 **파괴된 Transform은 == null이 true인데 참조는
        // 살아 있어서** 멤버에 손대는 순간 MissingReferenceException이 난다. 그 예외는
        // 아래 자폭 검사보다 위에서 터지므로 미사일이 표적에 처박힌 채 영영 안 사라진다.
        // 진짜 null로 접어 두면 그 창이 존재하지 않는다.
        Transform target = Target;

        if (target == null)
            target = Target = null;

        // 놓침 자폭. 산개(weave)가 일부러 기수를 벌리는 동안은 놓친 것이 아니고,
        // 연료가 끝난 관성 비행에는 유도가 없으니 지나친 표적을 영영 못 물어서
        // 여기 걸린다 - 우주로 날아가는 것보다 끊는 것이 낫다. 표적이 사라진 것도
        // (배가 완전히 죽는 등) 놓친 것으로 치고 같은 신관을 태운다.
        if (_currentTime >= weaveTime)
        {
            bool missed;

            if (target == null)
                missed = _hadTarget;
            else
            {
                Vector2 toTarget = ((Vector2)target.position - (Vector2)transform.position).normalized;
                missed = Vector2.Dot(transform.up, toTarget) <= MissDot;
            }

            if (missed)
            {
                _missedTime += TickManager.TickDeltaTime;

                if (_missedTime >= MissFuseSeconds)
                {
                    Destroy(gameObject);
                    return;
                }
            }
            else
                _missedTime = 0f;
        }

        if(_currentTime < fuelTime)
        {
            if(target != null && velocity.sqrMagnitude > 0f)
            {
                Vector2 toTarget = ((Vector2)target.position - (Vector2)transform.position).normalized;
                float want = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;

                // 발사 직후 옆으로 부풀었다 돌아오는 편향. sin 반주기라 weaveTime이
                // 끝나는 순간 정확히 0으로 닫혀 순수 추적으로 이어진다.
                if (_currentTime < weaveTime)
                    want += side * weaveAngle * Mathf.Sin(Mathf.PI * _currentTime / weaveTime);

                // 선회 제한: 원하는 방향으로 틱당 maxRotationPerTick도까지만 꺾는다.
                float have = Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg;
                float next = Mathf.MoveTowardsAngle(have, want, maxRotationPerTick) * Mathf.Deg2Rad;

                Vector2 dir = new(Mathf.Cos(next), Mathf.Sin(next));
                velocity = dir * velocity.magnitude;
                transform.up = dir;
            }
            //Get new target logic (Not implemented)
            velocity += (Vector2)transform.up * thrust;
        }
        base.OnTick();
    }
}
