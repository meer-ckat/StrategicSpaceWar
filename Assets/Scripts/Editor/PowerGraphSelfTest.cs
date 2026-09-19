#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Tools > Ship > Run PowerGraph Tests. 그물→트리, 끊긴 간선, 전원 둘, 섬 둘.
public static class PowerGraphSelfTest
{
    private static int _pass, _fail;
    private const float E = 320f;

    [MenuItem("Tools/Ship/Run PowerGraph Tests")]
    public static void Run()
    {
        _pass = 0; _fail = 0;
        var g = new PowerGraph();

        // 1) 전원 -0.2Ω- 부하 10Ω
        {
            g.Clear();
            int s = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int l = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            int e = g.AddEdge(s, l, 0.2f, 0);
            g.Solve();
            float I = E / 10.3f;
            Near(g.i[l], I, "직결: 부하 전류");
            Near(g.ei[e], I, "직결: 간선 전류");
            Near(g.v[l], I * 10f, "직결: 부하 전압");
        }

        // 2) 그물: 전원 → J1 → 부하, 전원 → J2 → 부하. 한 경로에만 전류가 흐른다
        {
            g.Clear();
            int s = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int j1 = g.AddNode(PowerGraph.Kind.Junction, 0f, 0f);
            int j2 = g.AddNode(PowerGraph.Kind.Junction, 0f, 0f);
            int l = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            g.AddEdge(s, j1, 0.1f, 0);
            int a2 = g.AddEdge(j1, l, 0.1f, 0);
            g.AddEdge(s, j2, 0.1f, 1);
            int b2 = g.AddEdge(j2, l, 0.1f, 1);
            g.Solve();
            float I = E / (0.1f + 0.2f + 10f);
            Near(g.i[l], I, "그물: 부하 전류 = 한 경로 값");
            bool oneSide = (Mathf.Abs(g.ei[a2] - I) < 0.05f && g.ei[b2] == 0f)
                        || (Mathf.Abs(g.ei[b2] - I) < 0.05f && g.ei[a2] == 0f);
            Check(oneSide, "그물: 전류가 한쪽 경로에만");
            Check(g.v[j1] > 0f && g.v[j2] > 0f, "그물: 두 분기점 다 전압이 있다");
        }

        // 3) 끊긴 간선 - 부하 0 V, 0 A
        {
            g.Clear();
            int s = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int l = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            g.AddEdge(s, l, 0.2f, 0, alive: false);
            g.Solve();
            Near(g.v[l], 0f, "끊김: 0 V");
            Near(g.i[l], 0f, "끊김: 0 A");
        }

        // 4) 전원 둘 한 섬: 320이 뿌리, 300은 분기점 취급
        {
            g.Clear();
            int weak = g.AddNode(PowerGraph.Kind.Source, 300f, 0.1f);
            int strong = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int l = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            g.AddEdge(strong, l, 0.2f, 0);
            g.AddEdge(weak, l, 0.2f, 1);
            g.Solve();
            Near(g.i[l], E / 10.3f, "전원 둘: 큰 emf가 뿌리");
            Check(g.v[weak] > 0f && g.i[weak] == 0f, "전원 둘: 약한 전원은 전압만 받고 전류 없음");
        }

        // 5) 섬 둘 - 서로 모른다
        {
            g.Clear();
            int sa = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int la = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            int sb = g.AddNode(PowerGraph.Kind.Source, 200f, 0.1f);
            int lb = g.AddNode(PowerGraph.Kind.Load, 0f, 5f);
            g.AddEdge(sa, la, 0.2f, 0);
            g.AddEdge(sb, lb, 0.2f, 1);
            g.Solve();
            Near(g.i[la], E / 10.3f, "섬 A");
            Near(g.i[lb], 200f / 5.3f, "섬 B");
        }

        // 6) 전원에 안 닿는 부하 - 0
        {
            g.Clear();
            int s = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int l = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            int lone = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            g.AddEdge(s, l, 0.2f, 0);
            g.Solve();
            Near(g.v[lone], 0f, "고립 부하: 0 V");
        }

        // 7) 약한 전원 밑에 부하: 그 전류는 뿌리를 거쳐 흐른다. 뿌리 i = 전체, 약한 전원 i = 자기 밑 부하 몫
        {
            g.Clear();
            int strong = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int weak = g.AddNode(PowerGraph.Kind.Source, 300f, 0.1f);
            int la = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            int lb = g.AddNode(PowerGraph.Kind.Load, 0f, 20f);
            g.AddEdge(strong, la, 0.2f);
            g.AddEdge(strong, weak, 0.2f);
            g.AddEdge(weak, lb, 0.2f);
            g.Solve();
            Check(g.isRoot[strong] && !g.isRoot[weak], "전원 둘: 뿌리 표시");
            Near(g.i[strong], g.i[la] + g.i[weak], "전원 둘: 뿌리 전류 = 가지 합");
            Near(g.i[weak], g.i[lb], "전원 둘: 약한 전원 i = 자기 밑 부하");
            float rb = 0.2f + 0.2f + 20f, ra = 0.2f + 10f;
            float rPar = 1f / (1f / ra + 1f / rb);
            Near(g.i[strong], E / (0.1f + rPar), "전원 둘: 전체 전류 = 직병렬 합성");
        }

        // 8) 부하 노드를 거쳐 다음 부하로(사슬 배선). 중간 부하 노드가 버스 노릇을 한다
        {
            g.Clear();
            int s = g.AddNode(PowerGraph.Kind.Source, E, 0.1f);
            int l1 = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            int l2 = g.AddNode(PowerGraph.Kind.Load, 0f, 10f);
            g.AddEdge(s, l1, 0.2f);
            g.AddEdge(l1, l2, 0.3f);
            g.Solve();
            float r2 = 0.3f + 10f, r1 = 1f / (1f / 10f + 1f / r2);
            float I0 = E / (0.1f + 0.2f + r1);
            float v1 = E - I0 * 0.3f;
            Near(g.v[l1], v1, "사슬: 첫 부하 전압");
            Near(g.i[l2], v1 / r2, "사슬: 둘째 부하 전류");
            Near(g.i[l1], I0, "사슬: 첫 부하 노드로 들어오는 전류 = 전체");
        }

        Debug.Log($"[PowerGraph] {_pass}개 통과, {_fail}개 실패.");
    }

    private static void Near(float got, float want, string what)
    {
        float tol = Mathf.Max(1e-3f, Mathf.Abs(want) * 1e-3f);
        if (Mathf.Abs(got - want) <= tol) { _pass++; return; }
        _fail++;
        Debug.LogError($"[PowerGraph] 실패: {what} - 기대 {want:0.###}, 나옴 {got:0.###}");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Debug.LogError($"[PowerGraph] 실패: {what}");
    }
}
#endif
