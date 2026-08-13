using UnityEngine;

/// <summary>
/// 충각. 물리가 이미 계산해 준 충격량을 그대로 받아서, 부딪힌 판에 관통 시스템과
/// 같은 눈금의 피해를 넣는다. 그 뒤로는 포탄에 맞았을 때와 완전히 같은 길을 탄다 -
/// 서브셀이 죽고, 죽은 서브셀이 파편을 뿌리고, 파편이 모듈과 승무원을 친다.
/// </summary>
public abstract partial class Ship
{
    private static readonly ContactPoint2D[] _contacts = new ContactPoint2D[16];

    /// <summary>
    /// 충돌은 두 함선에 각각 따로 들어온다. 각자 자기 판만 깎으므로 이중 계산이 없다.
    /// </summary>
    private void OnCollisionEnter2D(Collision2D collision)
    {
        int n = collision.GetContacts(_contacts);

        for (int i = 0; i < n; i++)
        {
            ContactPoint2D c = _contacts[i];

            // 이 아래는 접촉이지 충돌이 아니다. 스치기만 해도 장갑이 갈리면 안 된다.
            if (c.normalImpulse < Ballistics.RamMinImpulse)
                continue;

            Armor plate = OwnPlateAt(c);

            if (plate == null)
                continue;

            // 부호 규약에 기대지 않으려고 절대값. 어느 쪽이 들이받았든 파고든 속도는 같다.
            float normalSpeed = Mathf.Abs(Vector2.Dot(collision.relativeVelocity, c.normal));

            // 충격량 x 속도 / 2 = 그 접점이 흡수한 에너지. DamageScale을 그대로 통과시켜
            // 포탄 피해와 같은 눈금 위에 올린다.
            float energy = 0.5f * c.normalImpulse * normalSpeed;
            float damage = energy * Ballistics.DamageScale * Ballistics.RamDamageFraction;

            if (damage <= 0f)
                continue;

            // 접점에서 판 안쪽으로 파고드는 방향. relativeVelocity의 부호 규약 대신
            // 기하로 구해서 어느 Unity 버전에서도 같은 서브셀을 고른다.
            Vector2 into = (Vector2)plate.transform.position - c.point;

            if (into.sqrMagnitude < 1e-6f)
                into = -c.normal;

            plate.ApplyDamage(plate.SubIndexAt(c.point, into.normalized), damage);
        }
    }

    /// <summary>
    /// Unity의 collider / otherCollider 방향 규약은 콜백을 받는 쪽에 따라 뒤집힌다.
    /// 둘 다 보고 우리 배에 달린 쪽을 고르면 규약을 몰라도 항상 맞다.
    /// </summary>
    private Armor OwnPlateAt(in ContactPoint2D c)
    {
        Armor mine = OwnPlate(c.collider);

        return mine != null ? mine : OwnPlate(c.otherCollider);
    }

    private Armor OwnPlate(Collider2D col)
    {
        if (col == null || !col.transform.IsChildOf(transform))
            return null;

        return col.GetComponent<Armor>();
    }
}
