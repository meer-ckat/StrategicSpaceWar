using UnityEngine;

/// <summary>
/// 충각. 물리가 이미 계산해 준 충격량을 받아서, 부딪힌 판에 관통 시스템과 같은 눈금의
/// 피해를 넣는다. 그 뒤로는 포탄에 맞았을 때와 완전히 같은 길을 탄다 - 서브셀이 죽고,
/// 죽은 서브셀이 파편을 뿌리고, 파편이 모듈을 친다.
///
/// Ship과 HullDebris가 같이 쓴다. 예전에는 Ship에만 있어서 잔해가 체력 무한인 벽이었다 -
/// 함선은 잔해에 부딪혀 자기 판만 깎였고, 잔해는 흠집 하나 안 났다.
/// </summary>
public static class RamImpact
{
    private static readonly ContactPoint2D[] _contacts = new ContactPoint2D[16];

    /// <summary>
    /// 충돌은 양쪽에 각각 따로 들어온다. 각자 자기 판만 깎으므로 이중 계산이 없다.
    /// root는 "내 판"을 가리는 기준이다.
    /// </summary>
    public static void Resolve(Transform root, Collision2D collision)
    {
        int n = collision.GetContacts(_contacts);

        for (int i = 0; i < n; i++)
        {
            ContactPoint2D c = _contacts[i];

            // 이 아래는 접촉이지 충돌이 아니다. 스치기만 해도 장갑이 갈리면 안 된다.
            if (c.normalImpulse < Ballistics.RamMinImpulse)
                continue;

            // 부호 규약에 기대지 않으려고 절대값. 어느 쪽이 들이받았든 파고든 속도는 같다.
            float normalSpeed = Mathf.Abs(Vector2.Dot(collision.relativeVelocity, c.normal));

            // 충격량 x 속도 / 2 = 그 접점이 흡수한 에너지. DamageScale을 그대로 통과시켜
            // 포탄 피해와 같은 눈금 위에 올린다.
            float energy = 0.5f * c.normalImpulse * normalSpeed;
            float damage = energy * Ballistics.DamageScale * Ballistics.RamDamageFraction;

            if (damage <= 0f)
                continue;

            Armor plate = OwnPlateAt(root, c);

            if (plate == null)
            {
                // 판이 아니라 모듈을 직접 받았다. 모듈은 서브셀이 없어 통째로 먹는다.
                if (OwnCollider(root, c.collider) != null
                    && c.collider.TryGetComponent(out IDamageable module))
                    module.TakeDamage(damage);

                continue;
            }

            // 충각은 포탄과 다르다. 포탄은 한 점으로 파고들어 선을 그리지만, 충각은 판
            // 전체를 한 번에 밀어서 어디랄 것 없이 고르게 상한다 - 그래서 채널이 아니라
            // 균일 분배다.
            //
            // 다만 총량은 보존한다. 36칸에 damage를 '각각' 넣으면 균일한 게 아니라
            // 36배 센 것이다. 충각을 더 아프게 하려면 Ballistics.RamDamageFraction을 올려라.
            plate.ApplyDamageEvenly(damage);
        }
    }

    /// <summary>
    /// Unity의 collider / otherCollider 방향 규약은 콜백을 받는 쪽에 따라 뒤집힌다.
    /// 둘 다 보고 내 밑에 달린 쪽을 고르면 규약을 몰라도 항상 맞다.
    /// </summary>
    private static Armor OwnPlateAt(Transform root, in ContactPoint2D c)
    {
        Collider2D mine = OwnCollider(root, c.collider) ?? OwnCollider(root, c.otherCollider);

        return mine != null ? mine.GetComponent<Armor>() : null;
    }

    private static Collider2D OwnCollider(Transform root, Collider2D col)
        => col != null && col.transform.IsChildOf(root) ? col : null;
}
