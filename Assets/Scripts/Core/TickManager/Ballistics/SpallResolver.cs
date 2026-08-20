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

    // 깊이마다 따로. ApplyDamageAlong이 서브셀을 죽이면 그 자리에서 Collapse가 다시
    // Burst를 부르므로, 하나를 돌려쓰면 바깥 루프가 읽는 중인 채널이 덮인다.
    private static readonly float[][] _channel = BuildChannels();

    private static float[][] BuildChannels()
    {
        var buffers = new float[Ballistics.MaxSpallDepth + 1][];

        for (int i = 0; i < buffers.Length; i++)
            buffers[i] = new float[Ballistics.SubCount];

        return buffers;
    }

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
            mask,
            r.calliber);
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
        LayerMask mask, float caliber = 0f)
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
                Vector2 right = new Vector2(direction.y, -direction.x);

                // -1 = 왼쪽 가장자리, 0 = 탄두 중심, +1 = 오른쪽 가장자리
                float lateral = rng.Range(-1f, 1f);

                // caliber는 mm, 월드는 m. Armor 붕괴처럼 탄두 단면이 없는 호출은 0을 넘겨
                // 한 점에서 뿌린다.
                Vector2 fragmentOrigin =
                    origin + right * lateral * (caliber * 0.0005f);

                // 중심 0, 가장자리 1
                float edgeFactor = Mathf.Abs(lateral);

                // 중심에서는 좁게, 가장자리에서는 넓게
                float localSpread = Mathf.Lerp(
                    spread,
                    Mathf.Min(180f, spread * 10f),
                    edgeFactor
                );

                Vector2 d = Ballistics.Rotate(
                    direction,
                    rng.Range(-localSpread, localSpread)
                );

                Vector2 fragmentStart =
                    fragmentOrigin + d * Ballistics.Epsilon;

                int n = Physics2D.RaycastNonAlloc(
                    fragmentStart,
                    d,
                    _hits,
                    range,
                    mask
                );
                // 파편이 지나간 선을 화면에 남긴다. 그림뿐이고, 판정에는 관여하지 않는다.
                if (n <= 0)
                {
                    Vector2 far = fragmentStart + d.normalized * range;

                    SpallTrails.Add(fragmentStart, far, SpallTrails.Kind.Miss);

                    // 앞판을 하나도 못 맞고 날아갔다는 것은 **가로막은 실물이 없었다**는
                    // 뜻이다. 그 끝에 반대편 벽이 있으면 거기 박힌다 - 후면은 콜라이더가
                    // 없어서 위 레이캐스트에는 애초에 안 잡힌다.
                    HullStructure.SpallRear(far, perFragment);

                    continue;
                }

                RaycastHit2D h = Nearest(n);

                if (h.collider == null)
                    continue;

                if (h.collider.TryGetComponent(out Armor armor))
                {
                    SpallTrails.Add(fragmentStart, h.point, SpallTrails.Kind.Armor);

                    // 파편도 선이다. 맞은 면의 칸에만 넣으면 6x6 격자의 테두리만 갉히고
                    // 안쪽은 영원히 멀쩡하다 - 파편은 언제나 표면에 닿으니까.
                    float[] channel = _channel[_depth];

                    armor.TraceChannel(
                        h.point, d, Ballistics.SpallChannelDepth, channel, out _);

                    armor.ApplyDamageAlong(channel, perFragment);
                }
                else if (h.collider.TryGetComponent(out IDamageable target))
                {
                    SpallTrails.Add(fragmentStart, h.point, SpallTrails.Kind.Module);

                    target.TakeDamage(perFragment);
                    DamageLog.Hit(h.collider.transform, perFragment, target);
                }
                else
                {
                    // 맞긴 맞았는데 피해를 받는 물건이 아니었다. 선은 거기서 끊긴다.
                    SpallTrails.Add(fragmentStart, h.point, SpallTrails.Kind.Miss);
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
