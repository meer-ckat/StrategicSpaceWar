using UnityEngine;
using Core;

/// <summary>
/// 판정 결과를 실제로 적용하는 쪽. 여기서만 바깥이 바뀐다 - 장갑 HP가 깎이고,
/// 부서진 탄이 중파편으로 갈라진다.
/// </summary>
public abstract partial class Projectile
{
    // CollectSurfaces의 _hits와 따로 쓴다. 같은 배열을 돌려쓰면 이번 충돌의 면 정보를
    // 아직 다 쓰기 전에 덮어쓰는 사고가 조용히 난다.
    private static readonly RaycastHit2D[] _moduleHits = new RaycastHit2D[8];

    /// <summary>
    /// 이번 구간에서 지나친 모듈을 전부 친다. 모듈은 장갑이 아니라 탄을 세우지 못하고,
    /// 지나가는 값으로 운동에너지 일부를 가져갈 뿐이다 - 그래서 가장 가까운 것 하나가
    /// 아니라 선에 걸린 전부에 들어간다.
    /// </summary>
    private void StrikeModules(Vector2 from, Vector2 dir, float distance)
    {
        if (distance <= 0f)
            return;

        int n = Physics2D.RaycastNonAlloc(from, dir, _moduleHits, distance, moduleLayer);

        for (int i = 0; i < n; i++)
        {
            Collider2D col = _moduleHits[i].collider;

            if (col == null || !col.TryGetComponent(out IDamageable target))
                continue;

            float speed = velocity.magnitude;

            if (speed <= 0f)
                return;

            float energy = 0.5f * mass * speed * speed;
            float taken = energy * Ballistics.ModuleHitFraction;

            target.TakeDamage(taken * Ballistics.DamageScale);
            DamageLog.Hit(col.transform, taken * Ballistics.DamageScale, target);

            // 놓고 간 에너지만큼 느려진다. 속도를 직접 깎지 않고 에너지에서 되돌리는 이유는,
            // 관통력이 speed^1.43이라 여기서 대충 빼면 뒤쪽 장갑 판정이 통째로 어긋나서다.
            float left = Mathf.Max(0f, energy - taken);
            velocity = dir * Mathf.Sqrt(2f * left / Mathf.Max(1e-4f, mass));
        }
    }

    private ProjectileState CaptureState() => new()
    {
        velocity = velocity,
        mass = mass,
        caliber = caliber,
        shatterVelocity = shatterVelocity,
        penetrationK = penetrationK,
        integrity = integrity,
        state = state,
        projectileId = ProjectileId,
        hitIndex = hitIndex,
        tick = TickManager.currentTick,
    };

    private void Apply(in HitResult r, Vector2 dir)
    {
        integrity = r.newIntegrity;
        state = r.newState;
        velocity = r.newVelocity;
        hitIndex++;

        if (_surfaces.count <= 0)
            return;

        SoundManager.AudioShot(
            r.outcome switch
            {
                HitOutcome.Penetrated => "Penetrate",
                HitOutcome.Ricochet => "Ricochet",
                _ => "Blocked",
            },
            _surfaces.hitPoint);

        // How far into the line the shell got. Penetrated = all the way. Blocked = it
        // stopped inside, so it only chewed that much of it - dumping the whole lot on the
        // entry sub-cell is what left the far side untouched until it failed all at once.
        float depth = r.outcome == HitOutcome.Penetrated
            ? 1f
            : Mathf.Clamp01(r.effectiveRHA > 0f ? r.penetrationAfter / r.effectiveRHA : 0f);

        float share = r.armorDamage / _surfaces.count;

        for (int i = 0; i < _surfaces.count; i++)
        {
            Armor armor = _surfaces.armor[i];

            // A plate can lose its last sub-cell and destroy itself while an earlier
            // surface in this same loop is still being resolved.
            if (armor == null)
                continue;

            float[] channel = _surfaces.channel[i];

            if (r.outcome == HitOutcome.Ricochet)
            {
                // never entered the plate at all
                for (int s = 0; s < channel.Length; s++)
                    channel[s] = 0f;

                channel[_surfaces.subIndex[i]] = 1f;
            }
            else if (r.outcome == HitOutcome.Blocked)
            {
                armor.TraceChannel(_surfaces.hitPoint, dir, depth, channel, out _, caliber * 0.001f);
            }

            armor.ApplyDamageAlong(channel, share);

            // 앞판을 뚫었으면 그 자리 뒷벽까지 본다. 후면은 콜라이더가 없어서 위
            // 레이캐스트에 절대 안 잡히므로, 관통이 확정된 이 자리에서 직접 물어야 한다.
            if (r.outcome == HitOutcome.Penetrated)
                HullStructure.PunchRear(_surfaces.hitPoint, r.penetrationAfter, share);
            DamageLog.Hit(armor, _surfaces.hitPoint, _surfaces.subIndex[i], r.outcome);
        }

        // Snapshot for the readout after the channel is final, not before.
        for (int i = 0; i < PenetrationManager.LastChannel.Length; i++)
            PenetrationManager.LastChannel[i] = _surfaces.channel[0][i];
    }

    /// <summary>
    /// The shell broke up going through. It is about to be destroyed, so its mass leaves
    /// as real projectiles that can cross the rest of the ship - a one-tick ray cannot.
    /// Same seed as the ray spall would have used, so replays match either way.
    /// </summary>
    private void SpawnHeavyFragments(in HitResult r)
    {
        int count = Mathf.Max(1, r.spallCount);
        var rng = new DeterministicRng(r.spallSeed);

        // mass splits; caliber follows from it, so a fragment is a smaller, weaker shell
        float fragmentMass = mass / count;
        float fragmentCaliber = caliber * Mathf.Pow(1f / count, 1f / 3f);
        float scale = fragmentCaliber / caliber;

        // Fragment speed comes from the spall energy, not the shell's residual velocity -
        // a shell blocked on the face has residual 0 and its debris still flies. spallEnergy
        // is on the HP scale (DamageScale already applied), so undo that before v = sqrt(2E/m).
        float fragmentEnergy = r.spallEnergy / (count * Ballistics.DamageScale);
        float speed = Mathf.Sqrt(2f * fragmentEnergy / Mathf.Max(1e-4f, fragmentMass));

        Vector2 direction = r.spallDirection.normalized;
        Vector2 right = new Vector2(direction.y, -direction.x);

        for (int i = 0; i < count; i++)
        {
            // -1 = 왼쪽 가장자리, 0 = 중심, +1 = 오른쪽 가장자리
            float lateral = rng.Range(-1f, 1f);

            // 원래 탄두 단면상의 파편 생성 위치. caliber는 mm, 월드는 m - 0.001을 빼먹으면
            // 100mm 탄이 착탄점에서 50m 옆에 파편을 낳는다.
            Vector2 fragmentOrigin =
                r.spallOrigin
                + right * lateral * (caliber * 0.0005f);

            // 중심 0, 가장자리 1
            float sign = Mathf.Sign(lateral);
            float edgeFactor = Mathf.Abs(lateral);

            float minAngle = r.spallSpread * edgeFactor;
            float maxAngle = Mathf.Lerp(
                r.spallSpread,
                90f,
                edgeFactor
            );

            float angle = rng.Range(minAngle, maxAngle) * sign;

            Vector2 heading = Ballistics.Rotate(
                direction,
                angle
            );

            // 실제 파편 진행 방향으로 epsilon만큼 밀기
            fragmentOrigin += heading * Ballistics.Epsilon;

            Projectile fragment =
                Instantiate(this, fragmentOrigin, transform.rotation);

            fragment.velocity =
                heading *
                (speed * rng.Range(Ballistics.HeavyFragmentSlowest, 1f));

            fragment.mass = fragmentMass;
            fragment.caliber = fragmentCaliber;
            fragment.integrity = r.newIntegrity;
            fragment.state = ShellState.Deformed;
            fragment.hitIndex = 0;
            fragment.generation = generation + 1;
            fragment.lifeTick = Ballistics.HeavyFragmentLifeTick;

            fragment.transform.up = heading;
            fragment.transform.localScale =
                transform.localScale * scale;
        }
    }
}
