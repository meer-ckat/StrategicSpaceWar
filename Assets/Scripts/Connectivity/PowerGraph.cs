using UnityEngine;

/// <summary>
/// 배 한 척의 전력망. SoA, int 핸들. 그물(loop)은 섬마다 전원에서 BFS 신장트리로 접어
/// <see cref="PowerNet.Solve"/>에 넘긴다 - 병렬 경로의 전류는 먼저 닿은 쪽에 몰린다(설계 §2.3).
/// Unity를 모른다 - 기기 ↔ 노드 대응은 Ship.Power가 든다.
/// </summary>
public sealed class PowerGraph
{
    public enum Kind : byte { Junction, Source, Load }

    public int nodeCount, edgeCount;

    public Kind[] kind = new Kind[16];
    public float[] emf = new float[16];      // Source: 기전력. Load: 역기전력(M3, 매 풀이 전에 Ship이 쓴다)
    public float[] rInt = new float[16];     // Source: 내부저항, Load: 부하저항
    public float[] v = new float[16];
    public float[] i = new float[16];        // 부모 간선으로 이 노드에 들어오는 전류. 뿌리는 전원이 내는 전류
    public bool[] isRoot = new bool[16];     // 이번 풀이에서 섬의 뿌리였던 전원. 약한 전원은 false - 그 i는 뿌리 i에 포함된다
    public Vector2Int[] cell = new Vector2Int[16];

    public int[] ea = new int[16], eb = new int[16], ewire = new int[16], eseg = new int[16];   // 간선 → (전선, 구간)
    public float[] er = new float[16];
    public bool[] ealive = new bool[16];
    public bool[] efault = new bool[16];     // 접지 사고 노드로 가는 반쪽 간선. 생사는 SegmentIntact가 아니라 단락 여부로 읽는다
    public float[] ei = new float[16];       // 간선 전류(부호 없음). 트리 밖 간선은 0

    // 풀이 스크래치. 노드·간선 수가 자랄 때만 다시 잡는다.
    private int[] _head = new int[16], _next = new int[32], _adjEdge = new int[32];
    private int[] _order = new int[16], _local = new int[16], _parentEdge = new int[16], _parent = new int[16];
    private float[] _rBranch = new float[16], _rLoad = new float[16], _eLoad = new float[16], _sv = new float[16], _si = new float[16];
    private bool[] _visited = new bool[16];
    private int _slots;

    public void Clear()
    {
        nodeCount = 0;
        edgeCount = 0;
    }

    public int AddNode(Kind k, float e, float r, Vector2Int at = default)
    {
        if (nodeCount == kind.Length) GrowNodes();
        int n = nodeCount++;
        kind[n] = k; emf[n] = e; rInt[n] = r; cell[n] = at; v[n] = 0f; i[n] = 0f;
        return n;
    }

    public int AddEdge(int a, int b, float r, int wire = 0, int seg = 0, bool alive = true, bool fault = false)
    {
        if (edgeCount == ea.Length) GrowEdges();
        int e = edgeCount++;
        ea[e] = a; eb[e] = b; er[e] = r; ewire[e] = wire; eseg[e] = seg; ealive[e] = alive; ei[e] = 0f; efault[e] = fault;
        return e;
    }

    /// <summary>
    /// 섬마다: emf 제일 큰 미방문 전원을 뿌리로 BFS. 방문 순서가 곧 로컬 번호라 parent[k] &lt; k 가 공짜다.
    /// 같은 섬의 다른 전원은 이번 풀이에서 Junction이다(버스 타이는 M5). 전원에 안 닿은 노드는 0 V.
    /// </summary>
    public void Solve()
    {
        int n = nodeCount, m = edgeCount;
        EnsureScratch(n, m);

        for (int k = 0; k < n; k++) { _head[k] = -1; _visited[k] = false; v[k] = 0f; i[k] = 0f; isRoot[k] = false; }

        for (int e = 0; e < m; e++)
        {
            ei[e] = 0f;
            if (!ealive[e]) continue;
            Push(ea[e], e);
            Push(eb[e], e);
        }

        while (true)
        {
            int root = -1;
            for (int k = 0; k < n; k++)
                if (kind[k] == Kind.Source && !_visited[k] && (root < 0 || emf[k] > emf[root]))
                    root = k;
            if (root < 0) break;

            isRoot[root] = true;
            int count = 0;
            _order[count] = root; _local[root] = count; _parentEdge[count] = -1; _visited[root] = true; count++;

            for (int q = 0; q < count; q++)
            {
                int node = _order[q];
                for (int slot = _head[node]; slot >= 0; slot = _next[slot])
                {
                    int e = _adjEdge[slot];
                    int other = ea[e] == node ? eb[e] : ea[e];
                    if (_visited[other]) continue;
                    _visited[other] = true;
                    _local[other] = count;
                    _order[count] = other;
                    _parentEdge[count] = e;
                    count++;
                }
            }

            for (int q = 0; q < count; q++)
            {
                int node = _order[q];
                int pe = _parentEdge[q];
                _parent[q] = q == 0 ? -1 : _local[ea[pe] == node ? eb[pe] : ea[pe]];
                _rBranch[q] = q == 0 ? rInt[root] : er[pe];
                _rLoad[q] = kind[node] == Kind.Load ? rInt[node] : 0f;
                _eLoad[q] = kind[node] == Kind.Load ? emf[node] : 0f;
            }

            PowerNet.Solve(emf[root], _parent, _rBranch, _rLoad, _eLoad, _sv, _si, count);

            for (int q = 0; q < count; q++)
            {
                int node = _order[q];
                v[node] = _sv[q];
                i[node] = _si[q];
                if (q > 0) ei[_parentEdge[q]] = _si[q];
            }
        }
    }

    private void Push(int node, int e)
    {
        _adjEdge[_slots] = e;
        _next[_slots] = _head[node];
        _head[node] = _slots++;
    }

    private void EnsureScratch(int n, int m)
    {
        _slots = 0;

        if (_head.Length < n)
        {
            _head = new int[n]; _order = new int[n]; _local = new int[n]; _parentEdge = new int[n]; _parent = new int[n];
            _rBranch = new float[n]; _rLoad = new float[n]; _eLoad = new float[n]; _sv = new float[n]; _si = new float[n]; _visited = new bool[n];
        }

        if (_next.Length < m * 2)
        {
            _next = new int[m * 2];
            _adjEdge = new int[m * 2];
        }
    }

    private void GrowNodes()
    {
        int c = kind.Length * 2;
        System.Array.Resize(ref kind, c); System.Array.Resize(ref emf, c); System.Array.Resize(ref rInt, c);
        System.Array.Resize(ref v, c); System.Array.Resize(ref i, c); System.Array.Resize(ref cell, c);
        System.Array.Resize(ref isRoot, c);
    }

    private void GrowEdges()
    {
        int c = ea.Length * 2;
        System.Array.Resize(ref ea, c); System.Array.Resize(ref eb, c); System.Array.Resize(ref er, c);
        System.Array.Resize(ref ewire, c); System.Array.Resize(ref eseg, c); System.Array.Resize(ref ealive, c);
        System.Array.Resize(ref ei, c); System.Array.Resize(ref efault, c);
    }
}
