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
        float laneWeight = 1f / lanes;
        float depth = Mathf.Clamp01(depthFraction);
        bool crossed = false;

        for (int lane = 0; lane < lanes; lane++)
        {
            // -0.5 .. +0.5. 레인이 하나면 정확히 0 - 예전 동작 그대로다.
            float across = lanes == 1 ? 0f : lane / (lanes - 1f) - 0.5f;
            Vector2 start = localEntry + perp * (across * diameter);

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
