using UnityEngine;

public class Launcher : Gun
{
    /// <summary>조준점에서 이 반경(m) 안의 몸을 표적으로 잠근다.</summary>
    public float LockRadius = 40f;

    /// <summary>표적이 없어도 쏜다. 유도는 미사일 자신(FireAndForget)에게 맡긴다.</summary>
    public bool NoTargeting = false;

    private static readonly Collider2D[] _lock = new Collider2D[16];

    /// <summary>
    /// 잠금 재검색 간격(틱). 400m OverlapCircle은 전장 절반을 브로드페이즈에 태우는
    /// 값이라 매 틱 돌리면 그것만으로 프레임이 죽는다(실측 35ms). 0.1초 낡은 잠금은
    /// 표적이 몇 m 움직인 오차일 뿐이고, 발사 뒤의 추적은 어차피 미사일 몫이다.
    /// </summary>
    private const int AcquireInterval = 6;

    private Transform _acquired;
    private long _acquireTick = long.MinValue;

    /// <summary>
    /// 표적이 안 잠기면 발사 자체를 미룬다. WantsToFire가 false면 Gun.OnTick이
    /// _pending을 소모하지 않으므로, 잠기는 순간 모아둔 발이 바로 나간다 -
    /// 사선 검사(LineIsClear)와 같은 리듬이다.
    ///
    /// **NoTargeting이면 잠금을 아예 안 본다** - 검색 비용도 없고, "잠금 실패 = 발사
    /// 금지"가 뒤집혀 미사일이 영영 안 나가는 일도 없다.
    /// </summary>
    protected override bool WantsToFire =>
        base.WantsToFire && (NoTargeting || Acquire() != null);

    /// <summary>
    /// 조준점 근처에서 가장 가까운 남의 몸. 포구 레이로 꿰는 방식은 조준선이 표적
    /// 콜라이더를 정확히 지나야만 잠겼다 - 커서가 몇 m만 비껴도 null이었다.
    /// 몸통(Rigidbody)을 잡는 이유는 판 하나를 잡으면 그 판이 죽는 순간 표적도
    /// 같이 사라져서다.
    ///
    /// 결과는 <see cref="AcquireInterval"/>틱 동안 캐시된다 - WantsToFire(매 틱)와
    /// Fire(격발 틱)가 같은 값을 나눠 쓰고, 검색은 간격마다 한 번만 돈다.
    /// </summary>
    private Transform Acquire()
    {
        long tick = Core.TickManager.currentTick;

        if (tick - _acquireTick < AcquireInterval)
            return _acquired;

        _acquireTick = tick;
        _acquired = AcquireNow();
        return _acquired;
    }

    private Transform AcquireNow()
    {
        if (!TryGetTarget(out Vector2 aim))
            return null;

        Rigidbody2D body = GetComponentInParent<Rigidbody2D>();
        Transform target = null;
        float best = float.MaxValue;

        int n = Physics2D.OverlapCircleNonAlloc(aim, LockRadius, _lock);

        for (int i = 0; i < n; i++)
        {
            Collider2D c = _lock[i];

            if (body != null && c.attachedRigidbody == body)
                continue;

            // **판정은 IsHostileTo 하나다.** 자기 몸만 걸렀을 때는 조준점 근처의 편대
            // 윙맨이 그대로 잠겼다 - 팀 비교를 여기 손으로 적으면 그 술어가 두 벌이 되고,
            // 중립(운석)과 시체(IsCombatEffective) 조건은 또 빠진다. Ship이 아닌 몸
            // (Hulk - 잔해·거울)은 예전처럼 잠긴다: 8구역 표적이 그것이다.
            if (owner != null && c.attachedRigidbody != null)
            {
                Ship theirs = c.attachedRigidbody.GetComponent<Ship>();

                if (theirs != null && !owner.IsHostileTo(theirs))
                    continue;
            }

            float d = ((Vector2)c.transform.position - aim).sqrMagnitude;

            if (d < best)
            {
                best = d;
                target = c.attachedRigidbody != null ? c.attachedRigidbody.transform : c.transform;
            }
        }

        return target;
    }

    protected override void Fire()
    {
        // WantsToFire와 같은 캐시를 읽는다 - 같은 간격 안이라 공짜다.
        Transform target = Acquire();

        if (target == null && !NoTargeting)
            return;

        MuzzleShot(out Vector2 muzzle, out _, out Vector2 inherited);

        Vector2 direction = _turret.up;

        var shell = DefDatabase.Spawn(projectile, null, muzzle, _turret.eulerAngles.z) as Projectile;

        if (shell == null)
            return;

        Rigidbody2D body = GetComponentInParent<Rigidbody2D>();

        // 산개 방향은 발사대가 배의 어느 쪽에 붙었는지로 정한다 - 위쪽 발사대는 위로,
        // 아래쪽은 아래로 갈라진다. 배 로컬 좌표라 좌우 반전(localScale.x=-1)도 따라온다.
        // 포탑은 판의 자식이라 transform.localPosition은 판 기준이다 - 배 기준이 필요하다.
        if (shell is Missile missile && owner != null)
            missile.side = owner.transform.InverseTransformPoint(transform.position).y >= 0f ? 1f : -1f;

        shell.Launch(direction, muzzleSpeed, inherited, body, target);

        if (body != null)
        {
            body.AddForceAtPosition(
                -direction * (shell.mass * muzzleSpeed * Ballistics.RecoilScale),
                muzzle,
                ForceMode2D.Impulse);
        }

        SoundManager.AudioShot("Cannon", muzzle);
    }
}
