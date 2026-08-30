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

    /// <summary>
    /// 포탄과 달리 물려받은 속도에 방향을 맡기지 않는다 - 발사관(포신) 방향으로
    /// 튀어나가고, 배의 속도는 속력에만 남는다. 벡터로 더하면 발사 순간 기수와
    /// 진행 방향이 배의 표류 쪽으로 넘어가서, 선회 제한이 걸린 유도가 그 각도를
    /// 되감는 동안 미사일이 옆으로 날아가는 것처럼 보인다.
    /// </summary>
    public override void Launch(Vector2 direction, float speed, Vector2 inherited = default, Rigidbody2D owner = null, Transform target = null)
    {
        base.Launch(direction, speed + inherited.magnitude, default, owner, target);
        _hadTarget = target != null;
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

        // 놓침 자폭. 산개(weave)가 일부러 기수를 벌리는 동안은 놓친 것이 아니고,
        // 연료가 끝난 관성 비행에는 유도가 없으니 지나친 표적을 영영 못 물어서
        // 여기 걸린다 - 우주로 날아가는 것보다 끊는 것이 낫다.
        //
        // 표적 Transform이 파괴되면(배가 완전히 죽는 등) Unity가 == null로 돌려서
        // 내적을 잴 대상 자체가 없다 - 받은 적이 있는데 없어졌으면 그냥 놓친 것으로
        // 치고 같은 신관을 태운다. 안 그러면 유도도 판정도 없이 lifeTick까지 떠돈다.
        if (_currentTime >= weaveTime)
        {
            bool missed;

            if (Target == null)
                missed = _hadTarget;
            else
            {
                Vector2 toTarget = ((Vector2)Target.position - (Vector2)transform.position).normalized;
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
            if(Target != null && velocity.sqrMagnitude > 0f)
            {
                Vector2 toTarget = ((Vector2)Target.position - (Vector2)transform.position).normalized;
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
