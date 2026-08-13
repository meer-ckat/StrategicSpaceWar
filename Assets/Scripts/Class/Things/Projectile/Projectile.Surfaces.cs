using UnityEngine;

/// <summary>
/// 이번 충돌에서 탄이 동시에 닿은 면들을 모으는 쪽. 판정은 하지 않는다 - 모아서
/// PenetrationManager에 넘기는 것까지가 여기 일이다.
/// </summary>
public abstract partial class Projectile
{
    private bool CollectSurfaces(Vector2 origin, Vector2 dir, float distance, Collider2D lastCollider)
    {
        _surfaces.count = 0;
        _surfaces.primaryCollider = null;

        int n = Physics2D.RaycastNonAlloc(origin, dir, _hits, distance, armorLayer);
        if (n <= 0)
            return false;

        // Rejected hits are skipped for BOTH passes, so a discarded corner artifact lets
        // min fall through to whatever real surface is behind it instead of tunnelling.
        float min = float.MaxValue;

        for (int i = 0; i < n; i++)
        {
            if (!Accept(_hits[i], dir, lastCollider))
                continue;

            if (_hits[i].distance < min)
                min = _hits[i].distance;
        }

        if (min == float.MaxValue)
            return false;

        float thicknessSum = 0f;

        for (int i = 0; i < n && _surfaces.count < SurfaceSet.MaxSurfaces; i++)
        {
            RaycastHit2D h = _hits[i];

            if (!Accept(h, dir, lastCollider))
                continue;

            if (h.distance - min > Ballistics.EdgeEpsilon)
                continue;

            if (h.collider == null || !h.collider.TryGetComponent(out Armor armor))
                continue;

            int k = _surfaces.count;

            _surfaces.normal[k] = h.normal;
            // 구경 mm -> m. 저항도 손상도 탄이 실제로 덮은 폭 전체에서 나와야 한다.
            _surfaces.rha[k] = armor.ChannelRha(
                h.point, dir, _surfaces.channel[k], out int sub, caliber * 0.001f);
            _surfaces.armor[k] = armor;
            _surfaces.subIndex[k] = sub;
            _surfaces.count++;

            thicknessSum += armor.PlateThickness;

            if (k == 0)
                _surfaces.primaryCollider = h.collider;
        }

        if (_surfaces.count == 0)
            return false;

        _surfaces.minDistance = min;
        _surfaces.hitPoint = origin + dir * min;
        _surfaces.plateThickness = thicknessSum / _surfaces.count;

        Rigidbody2D body = _surfaces.primaryCollider.attachedRigidbody;
        _surfaces.targetVelocity = body != null ? body.linearVelocity : Vector2.zero;

        return true;
    }

    private static bool Accept(RaycastHit2D h, Vector2 dir, Collider2D lastCollider)
    {
        // Immediate self-rehit only. A full-tick ignore would eat a legitimate
        // A -> B -> A ricochet chain.
        if (h.collider == lastCollider && h.distance < Ballistics.Epsilon * 2f)
            return false;

        // Drop back faces only. A tangential normal is corner garbage, but the shell IS
        // entering material there - discarding it would tunnel through the seam.
        // Resolve repairs the angle instead (see MinFacing).
        return Vector2.Dot(-dir, h.normal) >= 0f;
    }
}
