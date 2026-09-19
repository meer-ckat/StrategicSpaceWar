using UnityEngine;

/// <summary>
/// 방사형(트리) 전력망의 직류 해석. 노드 0이 전원 - emf와 내부저항 rBranch[0].
/// parent[k] < k 여야 한다(위상 순서). rLoad[k] <= 0 이면 부하 없음. v[k] = 노드 전압, i[k] = 부모에서 k로 흐르는 전류.
/// n을 안 주면 parent.Length. 배열은 호출자 것이고 여기서는 할당하지 않는다.
/// </summary>
public static class PowerNet
{
    /// <summary>eLoad 없는 옛 서명. 전부 0으로 푼다.</summary>
    public static void Solve(float emf, int[] parent, float[] rBranch, float[] rLoad, float[] v, float[] i, int n = -1)
        => Solve(emf, parent, rBranch, rLoad, null, v, i, n);

    /// <summary>
    /// 표 21번(M3). 부하 k는 역기전력 eLoad[k]를 가진다 - 모터: i = (v - e) / r. eLoad가 null이거나 0이면 저항 부하.
    /// 노턴 등가로 두 패스 그대로다: 가지 밑 전체를 부모에서 본 (컨덕턴스 G, 주입 전류 J) 한 쌍으로 접는다.
    /// 직렬 저항 R 너머로 접으면 G' = G/(1+GR), J' = J/(1+GR). 뒤로 패스는 G를 i[]에, J를 v[]에 임시로 둔다 -
    /// 앞으로 패스가 k를 풀 때 v[parent]는 이미 전압이고 v[k]는 아직 J라 겹치지 않는다. 할당 없음.
    /// </summary>
    public static void Solve(float emf, int[] parent, float[] rBranch, float[] rLoad, float[] eLoad, float[] v, float[] i, int n = -1)
    {
        if (n < 0) n = parent.Length;
        if (n == 0) return;

        for (int k = 0; k < n; k++)
        {
            float g = rLoad[k] > 0f ? 1f / rLoad[k] : 0f;
            i[k] = g;
            v[k] = eLoad != null ? g * eLoad[k] : 0f;   // 부하의 노턴 전류원 J = g·e
        }

        for (int k = n - 1; k >= 1; k--)
        {
            float g = i[k];
            if (g <= 0f) continue;                    // 밑이 열려 있으면 이 가지엔 전류가 없다
            float fold = 1f / (1f + g * rBranch[k]);
            i[parent[k]] += g * fold;
            v[parent[k]] += v[k] * fold;
        }

        // 뿌리: 전원 emf, 내부저항 rBranch[0]. (emf - v0)/r0 = G0·v0 - J0.
        float g0 = i[0], j0 = v[0], r0 = rBranch[0];
        v[0] = (emf + j0 * r0) / (1f + g0 * r0);
        i[0] = r0 > 0f ? (emf - v[0]) / r0 : g0 * v[0] - j0;

        for (int k = 1; k < n; k++)
        {
            float g = i[k], j = v[k];
            float vp = v[parent[k]];
            i[k] = (g * vp - j) / (1f + g * rBranch[k]);
            v[k] = vp - i[k] * rBranch[k];
        }
    }
}
