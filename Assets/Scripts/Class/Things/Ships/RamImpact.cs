using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 충각. 물리가 이미 계산해 준 충격량을 받아서, 부딪힌 판에 관통 시스템과 같은 눈금의
/// 피해를 넣는다. 그 뒤로는 포탄에 맞았을 때와 완전히 같은 길을 탄다 - 서브셀이 죽고,
/// 죽은 서브셀이 파편을 뿌리고, 파편이 모듈을 친다.
///
/// Ship과 Hulk(잔해·운석·폐위성)가 같이 쓴다. 예전에는 Ship에만 있어서 잔해가 체력 무한인 벽이었다 -
/// 함선은 잔해에 부딪혀 자기 판만 깎였고, 잔해는 흠집 하나 안 났다.
/// </summary>
public static class RamImpact
{
    private static readonly ContactPoint2D[] _contacts = new ContactPoint2D[16];

    // 접점마다 새로 만들면 한 번 부딪힐 때 최대 16쌍이 쓰레기가 된다. 충각은 난전에서
    // 매 틱 들어온다.
    private static readonly Queue<Armor> _wave = new();
    private static readonly HashSet<Armor> _reached = new();

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

            plate.ApplyDamageEvenly(damage);

            Conduct(plate, c.normal, damage,
                Ballistics.RamConductAlong, Ballistics.RamConductAcross,
                Ballistics.RamConductCutoff, Ballistics.RamConductMaxPlates);
        }
    }

    /// <summary>
    /// 충격이 선체 구조를 타고 번진다. 포탄과 충각의 차이가 이것이다 - 포탄은 한 점을 뚫고
    /// 지나가고, 충각은 배를 굽힌다.
    ///
    /// **번지는 모양이 등방성이 아니다.** 충격축을 따라서는 거의 안 줄고, 옆으로는 급히 죽는다.
    /// 그래서 진입 지점에서 반대편 외판까지 폭 한두 칸짜리 띠가 통째로 상한다 - 배를 굽히면
    /// 그 단면 전체가 견디는 것이지 맞은 자리만 견디는 게 아니기 때문이다.
    ///
    /// **허리를 끊는 코드는 여기 없다.** 띠가 외판까지 이어지면 그 판들이 죽고, 다음 틱에
    /// HullStructure의 8방향 BFS가 두 덩어리를 찾아 알아서 떼어낸다. 원래 있던 길이다.
    ///
    /// BFS와 감쇠 공식이 하는 일이 다르다: BFS는 **어디까지 닿는가**(실물로 이어져 있어야
    /// 충격이 간다. 이미 뚫린 구멍 너머로는 안 넘어간다), 공식은 **얼마나 먹는가**. 감쇠를
    /// 경로에 누적하지 않고 위치에서 바로 구하므로, 어느 순서로 도달하든 같은 값이 나온다.
    /// </summary>
    /// <param name="axis">접촉면 법선. 부호는 안 쓴다 - 축의 양쪽으로 똑같이 번진다.</param>
    /// <param name="along">축을 1 m 따라갈 때 남는 몫.</param>
    /// <param name="across">축에서 1 m 벗어날 때 남는 몫. along과 같으면 등방성 = 폭발.</param>
    private static void Conduct(
        Armor origin, Vector2 axis, float damage,
        float along, float across, float cutoff01, int maxPlates)
    {
        _wave.Clear();
        _reached.Clear();

        _wave.Enqueue(origin);
        _reached.Add(origin);

        Vector2 pivot = origin.transform.position;
        Vector2 acrossAxis = new(-axis.y, axis.x);
        float cutoff = damage * cutoff01;

        while (_wave.Count > 0 && _reached.Count < maxPlates)
        {
            Armor at = _wave.Dequeue();

            if (at == null)
                continue;

            foreach (Armor neighbour in at.Neighbours)
            {
                // == null: 이미 부서진 판. 부서진 자리로는 충격이 안 지나간다.
                // SameBodyAs: 잔해로 갈라진 조각. 참조는 살아 있어도 이제 남의 몸이다.
                if (neighbour == null || !at.SameBodyAs(neighbour) || _reached.Contains(neighbour))
                    continue;

                Vector2 offset = (Vector2)neighbour.transform.position - pivot;

                // 칸이 1 m라 거리가 그대로 미터다. Abs인 이유: Unity의 접촉면 법선 부호는
                // 콜백을 받는 쪽에 따라 뒤집힌다. 어차피 축의 양쪽으로 똑같이 번지면 된다.
                float share = damage
                    * Mathf.Pow(along, Mathf.Abs(Vector2.Dot(offset, axis)))
                    * Mathf.Pow(across, Mathf.Abs(Vector2.Dot(offset, acrossAxis)));

                // 더 멀리는 더 작다. 여기서 끊어도 놓치는 판이 없다.
                if (share < cutoff)
                    continue;

                _reached.Add(neighbour);
                neighbour.ApplyDamageEvenly(share);
                _wave.Enqueue(neighbour);
            }
        }
    }

    /// <summary>
    /// 유폭. 충각과 같은 전도인데 등방성이다 - along과 across가 같으면 띠가 아니라 원이 된다.
    /// 그래서 "폭발"이라는 별도 시스템이 없다. 충각은 배를 굽혀 자르고, 폭발은 둥글게 판다.
    ///
    /// 어디까지 닿는지는 BlastCutoff가 정하고 damage는 "닿은 판이 죽느냐"만 정한다. destroyer로
    /// 재보면 damage 800이면 판 17장 구멍, 1600이면 선체가 갈라진다.
    /// </summary>
    public static void Detonate(Armor origin, float damage)
    {
        origin.ApplyDamageEvenly(damage);

        Conduct(origin, Vector2.up, damage,
            Ballistics.BlastFalloff, Ballistics.BlastFalloff,
            Ballistics.BlastCutoff, Ballistics.BlastMaxPlates);
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
