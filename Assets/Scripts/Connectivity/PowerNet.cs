using UnityEngine;

/// <summary>
/// 방사형(트리) 전력망의 직류 해석. 노드 0이 전원 - emf와 내부저항 rBranch[0].
/// parent[k] < k 여야 한다(위상 순서). rLoad[k] <= 0 이면 부하 없음. v[k] = 노드 전압, i[k] = 부모에서 k로 흐르는 전류.
/// n을 안 주면 parent.Length. 배열은 호출자 것이고 여기서는 할당하지 않는다.
/// </summary>
public static class PowerNet
{
    // 표 20번. 뒤로 한 번(잎→뿌리, 합성 컨덕턴스), 앞으로 한 번(뿌리→잎, 전압 강하). 반복 없음 -
    // 전류를 되먹이는 스윕은 단락(1 mΩ)에서 발산한다(수축률 = rBranch/rLoad ≫ 1).
    public static void Solve(float emf, int[] parent, float[] rBranch, float[] rLoad, float[] v, float[] i, int n = -1)
    {
        if (n < 0) n = parent.Length;
        if (n == 0) return;

        // 뒤로: i[k]를 임시로 "k 밑 전체가 부모에서 보이는 컨덕턴스"로 쓴다. 이래서 할당이 없다 -
        // v[]를 쓰면 앞으로 패스에서 부모 전압을 읽을 때 컨덕턴스를 읽는다.
        for (int k = 0; k < n; k++)
            i[k] = rLoad[k] > 0f ? 1f / rLoad[k] : 0f;

        for (int k = n - 1; k >= 1; k--)
        {
            if (i[k] <= 0f) continue;                 // 밑이 열려 있으면 이 가지엔 전류가 없다
            i[parent[k]] += 1f / (rBranch[k] + 1f / i[k]);
        }

        // 앞으로: 뿌리 전압, 그리고 자식마다 (부모 전압 / 가지 밑 합성저항).
        float g0 = i[0];
        i[0] = g0 > 0f ? emf / (rBranch[0] + 1f / g0) : 0f;
        v[0] = emf - i[0] * rBranch[0];

        for (int k = 1; k < n; k++)
        {
            float g = i[k];
            i[k] = g > 0f ? v[parent[k]] / (rBranch[k] + 1f / g) : 0f;
            v[k] = v[parent[k]] - i[k] * rBranch[k];
        }
    }
}
