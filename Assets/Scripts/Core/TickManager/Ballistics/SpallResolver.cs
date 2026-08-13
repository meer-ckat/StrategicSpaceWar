using UnityEngine;

/// <summary>
/// 파편을 실제로 날려서 맞은 것에 피해를 준다. 무엇이 파편을 낳을지는 PenetrationManager가
/// 정하고, 여기는 그걸 집행만 한다.
///
/// Phase A: fragments are not Things. One-shot rays resolved inside the same tick,
/// so there is no ongoing cost. Promote to real Projectiles when they need to cross a ship.
/// </summary>
public static class SpallResolver
{
    private static readonly RaycastHit2D[] _hits = new RaycastHit2D[8];

    /// <summary>
    /// 파편이 서브셀을 죽이면 그 서브셀이 또 파편을 낳는다. 한 세대는 판이 부서지는
    /// 것처럼 읽히지만, 막지 않으면 한 발이 함선을 통째로 지운다.
    /// </summary>
    private static int _depth;

    public static void Resolve(in HitResult r, LayerMask mask)
    {
        if (r.spallCount <= 0 || r.spallEnergy <= 0f)
            return;

        Burst(
            r.spallOrigin,
            r.spallDirection,
            r.spallSpread,
            r.spallEnergy,
            r.spallCount,
            r.spallSeed,
            mask);
    }

    /// <summary>
    /// 한 점에서 부채꼴로 파편을 뿌린다. 관통 뒤의 스폴도, 무너지는 판의 파편도 전부 이것 하나다.
    /// spread는 반각(도).
    /// </summary>
    public static void Burst(
        Vector2 origin,
        Vector2 direction,
        float spread,
        float energy,
        int count,
        uint seed,
        LayerMask mask)
    {
        if (count <= 0 || energy <= 0f || _depth >= Ballistics.MaxSpallDepth)
            return;

        if (direction.sqrMagnitude < 1e-6f)
            direction = Vector2.up;

        _depth++;

        try
        {
            var rng = new DeterministicRng(seed);

            float perFragment = energy / count;
            float range = Mathf.Clamp(
                perFragment * Ballistics.SpallRangePerEnergy,
                Ballistics.SpallRangeMin,
                Ballistics.SpallRangeMax);

            // origin sits exactly on the face that was just hit. Nudge along the spray
            // direction first, or every fragment re-hits that plate at distance 0 and the
            // shell gets paid twice for one penetration.
            Vector2 start = origin + direction.normalized * Ballistics.Epsilon;

            for (int i = 0; i < count; i++)
            {
                Vector2 d = Ballistics.Rotate(direction, rng.Range(-spread, spread));

                int n = Physics2D.RaycastNonAlloc(start, d, _hits, range, mask);
                if (n <= 0)
                    continue;

                // Armor is sub-cell addressed and needs the hit point. A module has one health
                // pool and no geometry, so the two damage models never merge into one interface.
                RaycastHit2D h = Nearest(n);

                if (h.collider == null)
                    continue;

                if (h.collider.TryGetComponent(out Armor armor))
                {
                    armor.ApplyDamage(armor.SubIndexAt(h.point, d), perFragment);
                }
                else if (h.collider.TryGetComponent(out IDamageable target))
                {
                    target.TakeDamage(perFragment);
                    DamageLog.Hit(h.collider.transform, perFragment, target);
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    private static RaycastHit2D Nearest(int count)
    {
        int best = 0;

        for (int i = 1; i < count; i++)
        {
            if (_hits[i].distance < _hits[best].distance)
                best = i;
        }

        return _hits[best];
    }
}
