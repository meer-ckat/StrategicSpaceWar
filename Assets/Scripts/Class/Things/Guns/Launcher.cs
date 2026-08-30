using UnityEngine;

public class Launcher : Gun
{
    /// <summary>조준점에서 이 반경(m) 안의 몸을 표적으로 잠근다.</summary>
    private const float LockRadius = 40f;

    private static readonly Collider2D[] _lock = new Collider2D[16];

    /// <summary>
    /// 표적이 안 잠기면 발사 자체를 미룬다. WantsToFire가 false면 Gun.OnTick이
    /// _pending을 소모하지 않으므로, 잠기는 순간 모아둔 발이 바로 나간다 -
    /// 사선 검사(LineIsClear)와 같은 리듬이다.
    /// </summary>
    protected override bool WantsToFire => base.WantsToFire && Acquire() != null;

    /// <summary>
    /// 조준점 근처에서 가장 가까운 남의 몸. 포구 레이로 꿰는 방식은 조준선이 표적
    /// 콜라이더를 정확히 지나야만 잠겼다 - 커서가 몇 m만 비껴도 null이었다.
    /// 몸통(Rigidbody)을 잡는 이유는 판 하나를 잡으면 그 판이 죽는 순간 표적도
    /// 같이 사라져서다.
    /// </summary>
    private Transform Acquire()
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
        // WantsToFire가 이미 걸렀지만 공짜 방어다 - 같은 틱이라 결과가 같다.
        Transform target = Acquire();

        if (target == null)
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
