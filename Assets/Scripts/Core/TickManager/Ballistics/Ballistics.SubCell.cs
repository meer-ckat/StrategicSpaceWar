using UnityEngine;

/// <summary>
/// Sub-cell grid. Logical damage resolution INSIDE one collider - never geometry.
/// Everything here works in the plate's local space, so a rotated (sloped) plate needs
/// no special case: the caller inverse-transforms once and this file never knows.
/// </summary>
public static partial class Ballistics
{
    public const int SubGrid = 6;
    public const int SubCount = SubGrid * SubGrid;

    // ponytail: sample march, not an exact DDA. Count scales with the sub-grid so a
    // worst-case diagonal (crossing ~1.41 * SubGrid cells) still lands ~5 samples per cell
    // at any resolution. Swap for a DDA if this ever shows up in a profile.
    private const int Samples = SubGrid * 8; //해상도

    /// <summary>Where the first sample sits, as a fraction of the channel length.</summary>
    private const float FirstSampleOffset = 0.5f / Samples;

    /// <summary>
    /// 탄은 선이 아니라 굵기가 있다. 6x6 격자에서 서브셀 하나가 1/6 m인데 400mm 탄은
    /// 옆으로 2칸 반을 덮으므로, 중심선 하나만 훑으면 나머지가 통째로 무사해진다.
    /// 직경을 가로지르는 평행선 여러 개를 쏘고 무게를 나눠 갖는다.
    /// </summary>
    private const int MaxLanes = 5;

    /// <summary>직경이 서브셀 하나에 못 미치면 예전처럼 선 하나. 공짜다.</summary>
    private static int LaneCount(float diameter, Vector2 cellSize)
    {
        if (diameter <= 0f)
            return 1;

        float subCell = Mathf.Min(cellSize.x, cellSize.y) / SubGrid;

        if (subCell <= 0f)
            return 1;

        return Mathf.Clamp(Mathf.CeilToInt(diameter / subCell), 1, MaxLanes);
    }

    /// <summary>
    /// local is relative to the CENTRE of the cell. cellSize must be the real collider
    /// size - a grid wider than the collider puts the entry point in the wrong sub-cell
    /// and lets the channel run out past the actual edge of the plate.
    /// </summary>
    public static int SubIndex(Vector2 local, Vector2 cellSize) //인덱스 getter
    {
        int cx = Axis(local.x, cellSize.x);
        int cy = Axis(local.y, cellSize.y);

        return cy * SubGrid + cx;
    }

    private static int Axis(float local, float size)
    {
        if (size <= 0f)
            return 0;

        return Mathf.Clamp(
            Mathf.FloorToInt((local + size * 0.5f) / (size / SubGrid)),
            0,
            SubGrid - 1);
    }

    /// <summary>Centre of a sub-cell in the plate's local space. Used to place debris.</summary>
    public static Vector2 SubCellCentre(int subIndex, Vector2 cellSize)
    {
        int col = subIndex % SubGrid;
        int row = subIndex / SubGrid;

        return new Vector2(
            (col + 0.5f) * cellSize.x / SubGrid - cellSize.x * 0.5f,
            (row + 0.5f) * cellSize.y / SubGrid - cellSize.y * 0.5f);
    }

    /// <summary>
    /// Sub-cell the shell actually enters. Defined as the channel's first sample, so it
    /// can never disagree with the channel. A hit point sitting exactly on a sub-cell
    /// boundary - which is every shot that lands on a corner or a grid line - otherwise
    /// floors into whichever side wins the rounding, and that is often the side the shell
    /// is leaving, not entering.
    /// </summary>
    public static int EntrySubIndex(Vector2 localEntry, Vector2 localDir, Vector2 cellSize)
    {
        Vector2 d = localDir.normalized;
        float exit = CellExitDistance(localEntry, d, cellSize);

        if (exit <= 1e-4f)
            return SubIndex(localEntry, cellSize);

        return SubIndex(localEntry + d * (exit * FirstSampleOffset), cellSize);
    }

    /// <summary>
    /// Fraction of the shell's channel through this cell that falls in each sub-cell.
    /// A shell does not stop at the face it entered - it drills a line, and every sub-cell
    /// on that line loses integrity. Weights sum to 1.
    /// False when the ray leaves immediately (entry point grazing the boundary).
    /// </summary>
    /// <param name="depthFraction">
    /// How far along the channel the shell actually got. 1 = straight through. A blocked
    /// round stopped partway and only chewed the part of the line it reached - crediting
    /// all of it to the entry sub-cell is what makes the far side behave like air.
    /// Weights still sum to 1: the energy budget does not change, only where it lands.
    /// </param>
    /// <param name="diameter">
    /// 탄 직경, 판의 로컬 단위(= m, localScale 1 가정). 0이면 중심선 하나만 훑는다.
    /// </param>
    public static bool SubCellPath(
        Vector2 localEntry,
        Vector2 localDir,
        Vector2 cellSize,
        float[] weights,
        float depthFraction = 1f,
        float diameter = 0f)
    {
        for (int i = 0; i < SubCount; i++)
            weights[i] = 0f;

        Vector2 d = localDir.normalized;
        Vector2 perp = new Vector2(-d.y, d.x);

        int lanes = LaneCount(diameter, cellSize);
        float depth = Mathf.Clamp01(depthFraction);
        bool crossed = false;

        // 레인은 탄 직경이 아니라 탄과 판이 '겹치는 폭'에 걸쳐 편다. 판보다 굵은 탄은
        // 레인 절반이 판 밖에서 출발하는데, SubIndex는 판 밖을 가장 가까운 가장자리 칸으로
        // 걷어낸다 - 그냥 두면 굵은 탄일수록 테두리에만 무게가 쌓이고 안쪽은 멀쩡해진다.
        float reach = diameter * 0.5f;
        float plus = Mathf.Min(reach, CellExitDistance(localEntry, perp, cellSize));
        float minus = Mathf.Min(reach, CellExitDistance(localEntry, -perp, cellSize));
        float span = plus + minus;

        float laneWeight = 1f / lanes;

        for (int lane = 0; lane < lanes; lane++)
        {
            // 칸 중심 샘플링. 양 끝점을 쓰면 가장자리 레인이 경계선에 정확히 얹혀서
            // 다시 테두리 칸으로 몰린다.
            Vector2 start = lanes == 1
                ? localEntry
                : localEntry + perp * (span * (lane + 0.5f) / lanes - minus);

            float exit = CellExitDistance(start, d, cellSize) * depth;

            // 이 레인은 판을 스치기만 했다. 무게를 버리면 합이 1이 아니게 되므로
            // 닿은 칸에 통째로 준다.
            if (exit <= 1e-4f)
            {
                weights[SubIndex(start, cellSize)] += laneWeight;
                continue;
            }

            crossed = true;

            float step = laneWeight / Samples;

            for (int i = 0; i < Samples; i++)
                weights[SubIndex(start + d * (exit * (i + 0.5f) / Samples), cellSize)] += step;
        }

        return crossed;
    }

    // 연결 성분 탐색용 스크래치. 한 스레드에서 한 번에 하나씩만 돈다.
    private static readonly int[] _stack = new int[SubCount];
    private static readonly int[] _label = new int[SubCount];

    /// <summary>
    /// 살아 있는 서브셀 중 가장 큰 연결 성분을 찾아 inLargest에 표시한다.
    /// 나머지는 판에 붙어 있지 않은 조각이다 - 아무것도 떠받치지 않는데 혼자 남아
    /// 화면에 픽셀로 떠 있는 것을 막는다.
    ///
    /// 8방향이다. CLAUDE.md의 불변식대로 실물은 8방향, 빈 칸은 4방향으로 잇는다 -
    /// 4방향으로 보면 대각으로만 이어진 멀쩡한 판이 두 조각으로 갈린다.
    ///
    /// 순수 함수. 동점이면 인덱스가 작은 성분이 이긴다(결정론).
    /// </summary>
    // ponytail: 근사다. 판이 두 조각 나면 진짜로는 둘 다 남아야 하는데, 여기서는 작은 쪽을
    // 부서진 것으로 처리해 오차를 재료 손실 쪽으로 몰았다. 반반으로 갈리면 인덱스가 낮은
    // 절반(왼쪽아래)이 이기는데, 결정론적일 뿐 물리적 근거는 없다. 제대로 하려면 콜라이더를
    // 쪼개야 하고 그건 잔해 재분할과 같은 크기의 작업이다 - TODOS.md 참고.
    // 실전 빈도는 낮다: 판은 29/36에서 어차피 통째로 무너져서 깔끔한 이등분이 드물다.
    public static void LargestLivingComponent(bool[] alive, bool[] inLargest)
    {
        for (int i = 0; i < SubCount; i++)
        {
            _label[i] = 0;
            inLargest[i] = false;
        }

        int bestLabel = 0;
        int bestSize = 0;
        int label = 0;

        for (int seed = 0; seed < SubCount; seed++)
        {
            if (!alive[seed] || _label[seed] != 0)
                continue;

            label++;

            int top = 0;
            int size = 0;

            _stack[top++] = seed;
            _label[seed] = label;

            while (top > 0)
            {
                int at = _stack[--top];
                size++;

                int col = at % SubGrid;
                int row = at / SubGrid;

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    int nc = col + dx;
                    int nr = row + dy;

                    if (nc < 0 || nc >= SubGrid || nr < 0 || nr >= SubGrid)
                        continue;

                    int next = nr * SubGrid + nc;

                    if (!alive[next] || _label[next] != 0)
                        continue;

                    _label[next] = label;
                    _stack[top++] = next;
                }
            }

            if (size > bestSize)
            {
                bestSize = size;
                bestLabel = label;
            }
        }

        if (bestLabel == 0)
            return;

        for (int i = 0; i < SubCount; i++)
            inLargest[i] = _label[i] == bestLabel;
    }

    private static float CellExitDistance(Vector2 p, Vector2 d, Vector2 cellSize)
    {
        Vector2 half = cellSize * 0.5f;
        float t = float.MaxValue;

        if (Mathf.Abs(d.x) > 1e-6f)
            t = Mathf.Min(t, ((d.x > 0f ? half.x : -half.x) - p.x) / d.x);

        if (Mathf.Abs(d.y) > 1e-6f)
            t = Mathf.Min(t, ((d.y > 0f ? half.y : -half.y) - p.y) / d.y);

        return t == float.MaxValue ? 0f : Mathf.Max(0f, t);
    }
}
