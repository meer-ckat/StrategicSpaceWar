using UnityEngine;

/// <summary>
/// 탄 하나가 판 하나를 만났을 때 무슨 일이 일어나는지 결정한다. 그게 전부다.
///
/// Stateless, RNG-free. ProjectileState + SurfaceSet -> HitResult.
/// Nothing outside is mutated here; that is Projectile.Apply()'s and SpallResolver's job.
///
/// 같이 보는 파일:
///   Ballistics.Tuning.cs    상수. 숫자를 만지고 싶으면 거기.
///   Ballistics.Formula.cs   관통 공식, RHA 곡선
///   Ballistics.SubCell.cs   판 안쪽 6x6 격자
///   BallisticTypes.cs       ProjectileState / SurfaceSet / HitResult
///   PenetrationManager.Log.cs  디버그 링버퍼 (시뮬레이션에 영향 없음)
/// </summary>
public static partial class PenetrationManager
{
    public static HitResult Resolve(in ProjectileState p, SurfaceSet s)
    {
        HitResult r = default;

        float speed = p.Speed;
        if (speed <= 0f || s.count <= 0)
        {
            r.outcome = HitOutcome.Blocked;
            r.newState = p.state;
            r.newIntegrity = p.integrity;
            return r;
        }

        Vector2 dir = p.velocity / speed;

        // --- surface aggregation: equal weight resistance, most head-on face judges angle ---
        // Seeding best at MinFacing makes -dir win outright when every contacted normal is
        // corner garbage. One comparison, no branch, no seam ricochet, no seam tunnelling.
        float w = 1f / s.count;
        float weightedRHA = 0f;
        Vector2 judgeNormal = -dir;
        int judgeIndex = -1;
        float best = Ballistics.MinFacing;

        for (int i = 0; i < s.count; i++)
        {
            weightedRHA += w * s.rha[i];

            float c = Vector2.Dot(-dir, s.normal[i]);
            if (c > best)
            {
                best = c;
                judgeNormal = s.normal[i];
                judgeIndex = i;
            }
        }

        float rawCos = Mathf.Clamp01(Vector2.Dot(-dir, judgeNormal));
        float cosTheta = Mathf.Max(rawCos, Ballistics.MinCos);
        float effectiveRHA = weightedRHA / cosTheta;

        // --- impact ---
        Vector2 vRel = p.velocity - s.targetVelocity;
        float normalSpeed = Mathf.Abs(Vector2.Dot(vRel, judgeNormal));

        float penBefore = p.Penetration;
        float resistance = penBefore > 0f
            ? Mathf.Clamp01(effectiveRHA / penBefore)
            : 1f;

        float severity = p.shatterVelocity > 0f
            ? (normalSpeed / p.shatterVelocity) * resistance
            : 0f;

        // --- shell damage. Shattered is a label, integrity is a multiplier. Never the same axis. ---
        ShellState hitState =
            severity >= Ballistics.ShatterSeverity ? ShellState.Shattered :
            severity >= Ballistics.DeformSeverity ? ShellState.Deformed :
            ShellState.Intact;

        float decay =
            hitState == ShellState.Shattered ? Ballistics.ShatterDecay :
            hitState == ShellState.Deformed ? Ballistics.DeformDecay :
            Ballistics.IntactDecay;

        float newIntegrity = p.integrity * decay;
        ShellState newState = (ShellState)Mathf.Max((int)p.state, (int)hitState);

        // integrity is applied BEFORE the penetration comparison
        float penAfter = Ballistics.Penetration(
            p.penetrationK, newIntegrity, speed, p.mass, p.caliber);

        float baseDamage = 0.5f * p.mass * normalSpeed * normalSpeed * Ballistics.DamageScale;

        r.newState = newState;
        r.newIntegrity = newIntegrity;
        r.oldIntegrity = p.integrity;
        r.shellHitIndex = p.hitIndex;
        r.surfaceCount = s.count;
        r.judgeIndex = judgeIndex;
        r.angleDeg = Mathf.Acos(rawCos) * Mathf.Rad2Deg;
        r.effectiveRHA = effectiveRHA;
        r.impactSpeed = speed;
        r.normalSpeed = normalSpeed;
        r.resistance = resistance;
        r.severity = severity;
        r.penetrationBefore = penBefore;
        r.penetrationAfter = penAfter;
        r.spallOrigin = s.hitPoint;

        // --- ricochet. Overmatch only prevents bouncing; it guarantees nothing about penetration. ---
        float overmatch = s.plateThickness > 0f
            ? (p.caliber * 0.001f) / s.plateThickness
            : float.MaxValue;

        float critAngle = Ballistics.BaseCritAngle
            + Ballistics.OvermatchBonus * Mathf.Clamp01(overmatch - 1f);

        if (effectiveRHA > 0f && r.angleDeg > critAngle)
        {
            Vector2 vn = Vector2.Dot(p.velocity, judgeNormal) * judgeNormal;
            Vector2 vt = p.velocity - vn;

            r.outcome = HitOutcome.Ricochet;
            r.newVelocity = vt * Ballistics.RicochetTangent - vn * Ballistics.RicochetNormal;
            r.armorDamage = baseDamage * Ballistics.RicochetArmorDamage;
            return r;
        }

        // --- penetration ---
        if (penAfter >= effectiveRHA && penAfter > 0f)
        {
            float ratio = effectiveRHA / penAfter;                       // 0..1
            float residual = speed * Mathf.Sqrt(Mathf.Max(0f, 1f - ratio * ratio));

            r.outcome = HitOutcome.Penetrated;
            r.newVelocity = dir * residual;
            r.armorDamage = baseDamage * ratio * ratio;                  // energy actually spent

            // ratio 0 means the plate took nothing off the shell - it went through a hole.
            // No plate material was displaced, so there is nothing to spall.
            if (ratio > 0f)
            {
                float residualEnergy =
                    0.5f * p.mass * residual * residual * Ballistics.DamageScale;

                bool shattered = newState == ShellState.Shattered;

                BuildSpall(ref r, residualEnergy, dir, speed, residual, shattered, p);

                // Only a shell that got through AND broke up leaves real debris behind it.
                // Blocked + Shattered sprays off the outer face - light stuff, stays rays.
                r.heavySpall = shattered && r.spallCount > 0;
            }

            return r;
        }

        // --- blocked ---
        {
            float attackRatio = effectiveRHA > 0f ? penAfter / effectiveRHA : 0f;
            float f = Mathf.Clamp01(
                (attackRatio - Ballistics.AttackRatioFloor) / (1f - Ballistics.AttackRatioFloor));

            r.outcome = HitOutcome.Blocked;
            r.newVelocity = Vector2.zero;
            r.armorDamage = baseDamage * f * f;

            if (newState == ShellState.Shattered)
            {
                // shell broke up on the face - fragments spray back off the surface
                BuildSpall(ref r, baseDamage, judgeNormal, speed, 0f, true, p);
            }

            return r;
        }
    }

    private static void BuildSpall(
        ref HitResult r,
        float sourceEnergy,
        Vector2 direction,
        float speed,
        float residualSpeed,
        bool heavy,
        in ProjectileState p)
    {
        float energy = sourceEnergy * Ballistics.SpallEnergyFraction;
        if (energy < Ballistics.SpallMinEnergy)
            return;

        r.spallEnergy = energy;
        r.spallDirection = direction;
        r.spallSeed = Ballistics.Hash(p.projectileId, p.tick, p.hitIndex);

        r.spallCount = heavy
            ? Ballistics.HeavyFragmentCount
            : Mathf.Clamp(
                Mathf.RoundToInt(energy / Ballistics.SpallEnergyPerFragment),
                1, Ballistics.SpallMaxCount);

        // more residual energy -> tighter cone
        float tightness = speed > 0f ? Mathf.Clamp01(residualSpeed / speed) : 0f;
        r.spallSpread = Mathf.Lerp(Ballistics.SpallSpreadMax, Ballistics.SpallSpreadMin, tightness);
    }
}
