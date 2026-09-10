using UnityEngine;

/// <summary>
/// Sub-cell grid. Logical damage resolution INSIDE one collider - never geometry.
/// Everything here works in the plate's local space, so a rotated (sloped) plate needs
/// no special case: the caller inverse-transforms once and this file never knows.
/// </summary>
public static partial class Ballistics
{
    public const int SubGrid = 6; //서브셀이 하나당 6x6개 있다는 뜻.
    public const int SubCount = SubGrid * SubGrid; //36

    /// <summary>
    /// DDA 반복 상한. 한 칸을 가로지르며 지나는 서브셀은 최대 <c>2 * SubGrid</c>개다
    /// (x 경계 SubGrid번 + y 경계 SubGrid번). 부동소수점이 경계에 정확히 얹혀도 안 도는
    /// 안전장치이지 알고리즘의 일부가 아니다 - 여기 걸리면 그건 버그다.
    /// </summary>
    private const int MaxDdaSteps = SubGrid * 2 + 2;

    /// <summary>
    /// 이 아래는 그 축에 성분이 없는 것으로 친다.
    ///
    /// **<see cref="CellExitDistance"/>와 <see cref="March"/>가 같은 값을 써야 한다.**
    /// 갈라지면 이런 일이 난다: d.x = -1e-8이면 CellExitDistance는 x를 무시하고 y로만
    /// exit을 잡는데, March가 그걸 유효한 방향으로 읽으면 tMaxX가 0이 나와서 첫 반복에
    /// 길이 0으로 판 밖으로 나가버린다 - 무게 합이 0이 되고 진입 칸도 채널 밖이 된다.
    /// 접선에 가까운 입사가 상시라 반드시 걸린다.
    /// </summary>
    private const float AxisEpsilon = 1e-6f;

    /// <summary>
    /// 탄은 선이 아니라 굵기가 있다. 6x6 격자에서 서브셀 하나가 1/6 m인데 400mm 탄은
    /// 옆으로 2칸 반을 덮으므로, 중심선 하나만 훑으면 나머지가 통째로 무사해진다.
    /// 직경을 가로지르는 평행선 여러 개를 쏘고 무게를 나눠 갖는다.
    /// </summary>
    private const int MaxLanes = 5; //띠의 최대 width

    /// <summary>직경이 서브셀 하나에 못 미치면 예전처럼 선 하나. 공짜다.</summary>
    private static int LaneCount(float diameter, Vector2 cellSize) //1m,1m 당 얼마냐
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

        // **채널과 같은 순회를 쓴다.** 예전에는 "첫 샘플 위치"로 따로 구했는데, 그러면
        // 샘플 간격을 바꿀 때마다 이 정의도 같이 움직인다. DDA의 첫 유효 칸은 간격이라는
        // 개념 자체가 없으므로 둘이 어긋날 자리가 없다.
        March(localEntry, d, cellSize, exit, null, 0f, out int entry);
        return entry;
    }

    /// <summary>
    /// 서브셀 격자를 실제로 가로지른다(Amanatides-Woo DDA). 지나는 칸마다 **실제 통과
    /// 길이**를 알므로 무게가 근사가 아니라 정확하다.
    ///
    /// <paramref name="weights"/>가 null이면 아무것도 안 쓰고 <paramref name="firstCell"/>만
    /// 낸다 - <see cref="EntrySubIndex"/>가 그 길로 들어와서, 진입 칸과 채널이 같은
    /// 순회에서 나온다.
    ///
    /// **경계에 정확히 얹힌 진입점이 저절로 풀린다.** 히트 지점이 격자선에 걸리는 일은
    /// 상시인데(모서리 명중, 격자선 명중), 그때 <c>floor</c>는 탄이 *떠나는* 칸을 고른다.
    /// 여기서는 그 칸의 통과 길이가 0이라 무게를 못 받고 firstCell도 안 된다 - 특수 처리가
    /// 아니라 길이가 0이라는 사실 하나로 걸러진다.
    /// </summary>
    private static void March(
        Vector2 start,
        Vector2 d,
        Vector2 cellSize,
        float exit,
        float[] weights,
        float laneWeight,
        out int firstCell)
    {
        float subW = cellSize.x / SubGrid;
        float subH = cellSize.y / SubGrid;

        // 칸 중심 기준 -> 좌하단 기준. 격자 좌표가 [0, SubGrid) 범위로 들어온다.
        float px = start.x + cellSize.x * 0.5f;
        float py = start.y + cellSize.y * 0.5f;

        int ix = Mathf.Clamp(Mathf.FloorToInt(px / subW), 0, SubGrid - 1);
        int iy = Mathf.Clamp(Mathf.FloorToInt(py / subH), 0, SubGrid - 1);

        firstCell = iy * SubGrid + ix;
        bool foundFirst = false;

        // **문턱이 CellExitDistance와 같아야 한다.** 위 AxisEpsilon 주석 참고.
        int stepX = d.x > AxisEpsilon ? 1 : (d.x < -AxisEpsilon ? -1 : 0);
        int stepY = d.y > AxisEpsilon ? 1 : (d.y < -AxisEpsilon ? -1 : 0);

        // 다음 경계까지의 거리, 그리고 한 칸을 건너는 데 드는 거리. 축 성분이 0이면
        // 그 축 경계는 영영 안 오므로 무한대로 둔다.
        float tMaxX = stepX == 0 ? float.PositiveInfinity
            : ((stepX > 0 ? (ix + 1) * subW : ix * subW) - px) / d.x;

        float tMaxY = stepY == 0 ? float.PositiveInfinity
            : ((stepY > 0 ? (iy + 1) * subH : iy * subH) - py) / d.y;

        float tDeltaX = stepX == 0 ? float.PositiveInfinity : Mathf.Abs(subW / d.x);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : Mathf.Abs(subH / d.y);

        float t = 0f;
        float inv = laneWeight / exit;   // 길이 -> 무게. 합이 정확히 laneWeight가 된다

        for (int guard = 0; guard < MaxDdaSteps; guard++)
        {
            float tNext = Mathf.Min(tMaxX, tMaxY);
            bool last = tNext >= exit;

            if (last)
                tNext = exit;

            float length = tNext - t;

            if (length > 0f)
            {
                if (!foundFirst)
                {
                    firstCell = iy * SubGrid + ix;
                    foundFirst = true;
                }

                if (weights != null)
                    weights[iy * SubGrid + ix] += length * inv;
            }

            if (last)
                return;

            // 더 가까운 경계를 넘는다. 동률(정확한 대각선)이면 x를 먼저 - 결정론이다.
            if (tMaxX <= tMaxY)
            {
                ix += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                iy += stepY;
                tMaxY += tDeltaY;
            }

            // 판 밖으로 나갔다. exit가 판 가장자리라 정상적으로는 위의 last에서 끝나야
            // 하고, 여기 오는 것은 부동소수점 오차뿐이다.
            //
            // **그래도 남은 길이를 버리지 않는다.** 무게의 합이 1이라는 것이
            // "멀쩡한 판은 명목 RHA를 그대로 읽는다"의 근거라(CLAUDE.md), 조금이라도
            // 새면 그 판이 이유 없이 약해진다. 잔여분은 방금까지 있던 칸의 몫이다.
            if (ix < 0 || ix >= SubGrid || iy < 0 || iy >= SubGrid)
            {
                if (exit > tNext)
                {
                    int back = (iy < 0 || iy >= SubGrid ? iy - stepY : iy) * SubGrid
                             + (ix < 0 || ix >= SubGrid ? ix - stepX : ix);

                    // 무게를 받는 칸과 진입 칸이 갈리면 안 된다 - 검사가 정확히 그걸 본다.
                    if (!foundFirst)
                    {
                        firstCell = back;
                        foundFirst = true;
                    }

                    if (weights != null)
                        weights[back] += (exit - tNext) * inv;
                }

                return;
            }

            t = tNext;
        }
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

            March(start, d, cellSize, exit, weights, laneWeight, out _);
        }

        return crossed;
    }

    // 연결 성분 탐색용 스크래치. 한 스레드에서 한 번에 하나씩만 돈다.
    /// <summary>36칸 전부. 6×6이 ulong 하나에 들어가는 것이 이 파일 절반의 근거다.</summary>
    public const ulong SubMaskFull = (1UL << SubCount) - 1;

    // 열 경계 마스크. 왼쪽 시프트(>>1)가 열0을 열5로 감아 올리는 것을 막는다 -
    // 비트 0,6,12,18,24,30이 열0이고, 열5는 그것을 5칸 민 것이다.
    private const ulong SubMaskCol0 = 0x41041041UL;
    private const ulong SubMaskCol5 = SubMaskCol0 << (SubGrid - 1);

    /// <summary>
    /// 8방향 한 칸 팽창. 좌우는 열 경계 마스크로 감김을 막고, 상하는 행 폭(6)만큼
    /// 시프트한 뒤 36비트로 잘라낸다. 대각은 따로 없다 - 좌우로 번진 것을 상하로
    /// 다시 번지게 하면 그 합성이 대각이다.
    /// </summary>
    private static ulong DilateSub8(ulong m)
    {
        ulong h = m | ((m & ~SubMaskCol0) >> 1) | ((m & ~SubMaskCol5) << 1);
        return (h | (h << SubGrid) | (h >> SubGrid)) & SubMaskFull;
    }

    /// <summary>
    /// 살아 있는 서브셀 중 가장 큰 연결 성분의 마스크. 나머지는 판에 붙어 있지 않은
    /// 조각이다 - 아무것도 떠받치지 않는데 혼자 남아 화면에 픽셀로 떠 있는 것을 막는다.
    ///
    /// 8방향이다. CLAUDE.md의 불변식대로 실물은 8방향, 빈 칸은 4방향으로 잇는다 -
    /// 4방향으로 보면 대각으로만 이어진 멀쩡한 판이 두 조각으로 갈린다.
    ///
    /// 순수 함수. 성분은 최하위 비트부터 떼고 크기 비교가 strict라, 동점이면 인덱스가
    /// 작은 성분이 이긴다 - 옛 라벨 BFS와 같은 규칙(결정론).
    /// </summary>
    // ponytail: 근사다. 판이 두 조각 나면 진짜로는 둘 다 남아야 하는데, 여기서는 작은 쪽을
    // 부서진 것으로 처리해 오차를 재료 손실 쪽으로 몰았다. 반반으로 갈리면 인덱스가 낮은
    // 절반(왼쪽아래)이 이기는데, 결정론적일 뿐 물리적 근거는 없다. 제대로 하려면 콜라이더를
    // 쪼개야 하고 그건 잔해 재분할과 같은 크기의 작업이다 - TODOS.md 참고.
    // 실전 빈도는 낮다: 판은 29/36에서 어차피 통째로 무너져서 깔끔한 이등분이 드물다.
    public static ulong LargestLivingComponent(ulong alive)
    {
        ulong remaining = alive & SubMaskFull;
        ulong best = 0;
        int bestCount = 0;

        while (remaining != 0)
        {
            // 최하위 비트에서 시작해 이웃이 안 자랄 때까지 팽창 - 그것이 성분 하나다.
            ulong component = remaining & (ulong)(-(long)remaining);

            while (true)
            {
                ulong grown = DilateSub8(component) & remaining;

                if (grown == component)
                    break;

                component = grown;
            }

            remaining &= ~component;

            int count = Unity.Mathematics.math.countbits(component);

            if (count > bestCount)
            {
                bestCount = count;
                best = component;
            }
        }

        return best;
    }

    private static float CellExitDistance(Vector2 p, Vector2 d, Vector2 cellSize)
    {
        Vector2 half = cellSize * 0.5f;
        float t = float.MaxValue;

        // 문턱은 March와 공유한다 - 갈라지면 접선 입사에서 채널이 통째로 사라진다.
        if (Mathf.Abs(d.x) > AxisEpsilon)
            t = Mathf.Min(t, ((d.x > 0f ? half.x : -half.x) - p.x) / d.x);

        if (Mathf.Abs(d.y) > AxisEpsilon)
            t = Mathf.Min(t, ((d.y > 0f ? half.y : -half.y) - p.y) / d.y);

        return t == float.MaxValue ? 0f : Mathf.Max(0f, t);
    }
}
