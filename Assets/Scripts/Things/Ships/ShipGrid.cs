using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 함선의 위상은 1m 정수 격자다. 판 하나 = 칸 하나. 방 구획도 선체 연결성도 여기서만 나온다.
///
/// **콜라이더는 여기 안 온다.** 판의 콜라이더는 2x2까지 커질 수 있고 자유롭게 기울 수 있지만,
/// 격자는 판의 위치 하나만 읽는다. 경사장갑을 위해 콜라이더를 키운 것이 방 모양을 바꾸면
/// 안 되기 때문이다. 대가로 눈에 보이는 선체와 방 경계가 어긋난다 - 버그가 아니라 설계다.
/// </summary>
public static class ShipGrid
{
    public const float CellSize = 1f;

    /// <summary>
    /// Unset이 0인 것이 중요하다. 맵은 2단계로 지어진다 - 배치가 Wall/Door를 찍고, 그 다음
    /// <see cref="MarkExterior"/>가 남은 칸을 우주와 실내로 가른다. 기본값이 Exterior면
    /// "아직 안 정해진 칸"과 "우주"가 구분되지 않아 flood가 자기 결과를 다시 읽는다.
    /// </summary>
    /// <remarks>
    /// Vent는 "실물인데 밀폐하지 않는 칸"이다. def의 <c>sealsRoom: false</c>가 여기로
    /// 온다 - 안테나·마스트·노즐처럼 선체 밖으로 튀어나온 구조물. 판으로 붙어 있으므로
    /// 맞고, 끊기고, 잔해가 된다. 장식 레이어로 뺐으면 하나도 못 하는 일이다.
    /// </remarks>
    public enum Cell { Unset, Exterior, Wall, Empty, Door, Vent }

    public class Map
    {
        /// <summary>
        /// [col, row]. **as-built가 아니라 현재 상태다.** <see cref="ShipBuilder.Stamp"/>가
        /// 처음 찍고, 판이 죽을 때마다 <see cref="HullStructure.ReportPlateLost"/>가 그 칸을
        /// Empty로 되돌린다. "지어질 때 여기 뭐가 있었나"에는 아무도 답하지 않는다 - 필요한
        /// 것은 "지금 살아 있나"뿐이고, 그건 HullStructure가 장부로 들고 있다. 스냅샷이 하나
        /// 남아 있긴 한데(<c>_attached</c>) 그건 "이 덩어리가 생길 때 붙어 있었나"라는
        /// 다른 질문에 답한다.
        ///
        /// 죽은 칸은 Empty이지 Exterior가 아니다. 우주와 실내를 가르는 것은
        /// <see cref="MarkExterior"/>의 flood이고, 판 한 장 죽을 때마다 다시 번지지 않는다.
        /// </summary>
        public Cell[,] cells;
        public int width;
        public int height;
        public int version;

        /// <summary>
        /// 칸 (0,0)의 로컬 좌표. 예전에는 격자가 원점 중심이라고 가정했는데, 맵을 자식들에서
        /// 파생하면 그 가정이 깨진다. 원점을 들고 다니면 짝수 폭에서 0.5를 반올림하는
        /// 지뢰(RoundToInt는 은행가 반올림이다)도 같이 사라진다 - 정수 차만 반올림한다.
        /// </summary>
        public Vector2 origin;

        public Map(int width, int height, Vector2 origin)
        {
            this.width = width;
            this.height = height;
            this.origin = origin;
            cells = new Cell[Mathf.Max(width, 1), Mathf.Max(height, 1)];
        }

        /// <summary>원점 중심 정렬. JSON에서 새로 짓는 배와 테스트 픽스처가 쓴다.</summary>
        public static Map Centred(int width, int height) =>
            new(width, height,
                new Vector2(-(width - 1) * 0.5f, (height - 1) * 0.5f) * CellSize);

        public bool Inside(Vector2Int c) =>
            c.x >= 0 && c.y >= 0 && c.x < width && c.y < height;

        /// <summary>row가 증가하면 아래로 간다. 맵 첫 줄이 배의 위쪽이어야 글로 보이는 대로 나온다.</summary>
        public Vector2 ToLocal(int col, int row) =>
            new(origin.x + col * CellSize, origin.y - row * CellSize);

        public Vector2Int ToCell(Vector2 local) =>
            new(Mathf.RoundToInt((local.x - origin.x) / CellSize),
                Mathf.RoundToInt((origin.y - local.y) / CellSize));
    }

    private static readonly Vector2Int[] Dirs =
    {
        Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down,
    };

    /// <summary>공기가 다니는 칸. 엔진은 이제 벽에 붙은 모듈이라 격자에 아예 안 나온다.</summary>
    public static bool Passable(Cell c) => c == Cell.Empty;
    /// <summary>
    /// 뒤에 후면 판이 깔리는 칸. Vent가 빠지는 것은 그 칸이 선체 안쪽이 아니라 우주에
    /// 튀어나온 자리이기 때문이다. 넣어두면 안테나 뒤에 보이지 않는 장갑이 생기고,
    /// 증상은 "안테나가 이상하게 단단하다"뿐이라 원인이 안 보인다.
    /// </summary>
    public static bool BackPlate(Cell c) => c != Cell.Exterior && c != Cell.Vent;

    /// <summary>
    /// 하중을 받는 칸. 방은 빈 칸으로 이어지고 선체는 실물로 이어진다.
    ///
    /// **"실물이 있다"와 "공기를 막는다"는 다른 질문이다.** 예전에는 이 하나가 둘 다에
    /// 답했는데, <see cref="Cell.Vent"/>가 그 둘을 갈랐다 - 안테나는 실물이지만 밀폐하지
    /// 않는다. 공기 쪽은 <see cref="Airtight"/>가 답한다.
    /// </summary>
    public static bool Solid(Cell c) => c == Cell.Wall || c == Cell.Door || c == Cell.Vent;

    /// <summary>
    /// 공기를 막는 칸. <see cref="MarkExterior"/>의 flood **두 자리**만 이걸 본다.
    ///
    /// Vent를 그냥 <see cref="Solid"/>에서 빼면 안 되는 이유가 이 함수가 존재하는 이유다.
    /// Solid는 flood 말고도 <see cref="HullStructure"/>의 살아 있는 칸 장부와 판 소실
    /// 처리를 먹인다. Vent를 거기서 빼면 안테나가 구조 BFS에 안 들어와서 **쏴도 안 끊기고**,
    /// 게다가 MarkExterior의 마지막 칠하기 루프가 Solid로 걸러내므로 Vent 칸이 통째로
    /// Exterior로 덮어써진다 - 값이 태어나자마자 지워져서 기능이 조용히 0이 된다.
    ///
    /// 그래서 칠하기 루프는 Solid를 그대로 쓴다. **Airtight로 바꾸면 안 된다.**
    /// </summary>
    public static bool Airtight(Cell c) => Solid(c) && c != Cell.Vent;

    /// <summary>
    /// Wall/Door가 다 찍힌 맵에서 나머지 칸을 우주와 실내로 가른다.
    ///
    /// 규칙 한 줄: **밀폐된 주머니가 실내다.** 테두리의 비실물 칸에서 4방향으로 번져서
    /// 닿는 곳이 우주고, 안 닿는 곳이 실내다. 예전에 텍스트 맵의 ' '가 공짜로 주던 정보를
    /// BFS 하나로 되만든다 - 그래야 배치 리스트만으로 배가 완결된다.
    ///
    /// 테두리 씨앗은 맵을 한 겹 패딩한 것과 정확히 동치다. 4방향으로 바깥 링에서 들어올 수
    /// 있는 실제 칸 = 비실물 테두리 칸에서 도달 가능한 칸.
    /// </summary>
    public static void MarkExterior(Map map)
    {
        // 평면 인덱스(row * w + col). Vector2Int 큐는 칸마다 해시/구조체 복사가 붙는다 -
        // 건조 1회 경로라 할당은 그냥 두고 좌표형만 int로 간다.
        int w = map.width;
        int h = map.height;
        var outside = new bool[w * h];
        var queue = new int[w * h];   // 칸당 최대 1회 입큐라 이 크기면 절대 안 넘친다
        int head = 0;
        int tail = 0;

        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            bool border = col == 0 || row == 0 || col == w - 1 || row == h - 1;

            // flood 두 자리는 Airtight다 - Vent(밀폐 안 하는 실물)는 공기가 지나간다.
            if (!border || Airtight(map.cells[col, row]) || outside[row * w + col])
                continue;

            outside[row * w + col] = true;
            queue[tail++] = row * w + col;
        }

        while (head < tail)
        {
            int at = queue[head++];
            int col = at % w;
            int row = at / w;

            for (int d = 0; d < Dirs.Length; d++)
            {
                int nc = col + Dirs[d].x;
                int nr = row + Dirs[d].y;

                if (nc < 0 || nr < 0 || nc >= w || nr >= h)
                    continue;

                int ni = nr * w + nc;

                if (outside[ni] || Airtight(map.cells[nc, nr]))
                    continue;

                outside[ni] = true;
                queue[tail++] = ni;
            }
        }

        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            if (Solid(map.cells[col, row]))
                continue;

            map.cells[col, row] = outside[row * w + col] ? Cell.Exterior : Cell.Empty;
        }
    }

    /// <summary>
    /// 테스트 픽스처 전용. 함선은 더 이상 텍스트로 지어지지 않는다 - 배치 리스트가 원본이다.
    /// 그래도 ASCII는 손으로 쓰는 테스트 맵으로는 여전히 최고라서 남겨둔다.
    ///
    /// '#'과 'D'만 찍고 나머지는 <see cref="MarkExterior"/>에게 맡긴다. 진짜 배와 같은 길을
    /// 타야 픽스처가 실제 코드를 시험하는 것이 된다.
    /// </summary>
    public static Map ParseMap(string text)
    {
        // 파일 끝 개행이 유령 행을 만들어 함선 중심을 반 칸 밀어버린다
        string[] lines = (text ?? string.Empty).Replace("\r", "").Trim('\n').Split('\n');

        int width = 0;
        foreach (string line in lines)
            width = Mathf.Max(width, line.Length);

        Map map = Map.Centred(width, lines.Length);

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < width; col++)
        {
            char c = col < lines[row].Length ? lines[row][col] : ' ';

            map.cells[col, row] = c switch
            {
                '#' => Cell.Wall,
                'D' => Cell.Door,
                _ => Cell.Unset,
            };
        }

        MarkExterior(map);
        return map;
    }

    // 구조는 대각선으로도 붙어 있다. 계단처럼 놓인 판을 4방향으로만 보면 멀쩡한 선체가
    // 두 동강 난 것으로 읽힌다.
    private static readonly Vector2Int[] Around =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    /// <summary>
    /// 후면 칸의 주인. **집합이 아니라 평면 표다** - 조각마다 HashSet을 만들면 파단 한 번에
    /// 1,200칸이 해시 집합 여럿으로 흩어지고, 그 할당이 그대로 GC가 된다. 여기는 bbox
    /// 평면 배열 하나뿐이고, 호출자가 자기 장부를 한 번 훑으며 주인을 물어본다.
    ///
    /// 배열은 정적 재사용이라 **다음 SplitRear 호출 전까지만 유효하다.**
    /// </summary>
    public readonly struct RearOwners
    {
        private readonly int[] _owner;
        private readonly int _minX;
        private readonly int _minY;
        private readonly int _width;
        private readonly int _height;

        internal RearOwners(int[] owner, int minX, int minY, int width, int height)
        {
            _owner = owner;
            _minX = minX;
            _minY = minY;
            _width = width;
            _height = height;
        }

        /// <summary>
        /// 이 칸을 어느 조각이 가져가나. false면 **어느 조각도 안 가져간다 = 삭제다** -
        /// 판이 전멸한 토막의 후면이 그 경우고, 지우는 코드가 따로 없는 것이 요점이다.
        /// </summary>
        public bool TryOwnerOf(Vector2Int cell, out int chunk)
        {
            chunk = -1;

            if (_owner == null)
                return false;

            int cx = cell.x - _minX;
            int cy = cell.y - _minY;

            if (cx < 0 || cy < 0 || cx >= _width || cy >= _height)
                return false;

            chunk = _owner[cy * _width + cx];
            return chunk >= 0;
        }
    }

    /// <summary>테스트 픽스처용. 실전은 <see cref="SplitRearOwners"/>가 평면 표를 그대로 쓴다.</summary>
    public static List<HashSet<Vector2Int>> SplitRear(
        List<List<Vector2Int>> chunks, HashSet<Vector2Int> rear)
    {
        RearOwners owners = SplitRearOwners(chunks, rear);
        var results = new List<HashSet<Vector2Int>>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
            results.Add(new HashSet<Vector2Int>());

        foreach (Vector2Int cell in rear)
        {
            if (owners.TryOwnerOf(cell, out int chunk))
                results[chunk].Add(cell);
        }

        return results;
    }

    /// <summary>
    /// 후면 칸을 조각들에게 나눠 준다. 반환의 i번째가 <paramref name="chunks"/>의 i번째가
    /// 가져가는 후면 칸이다.
    ///
    /// **다중 소스 BFS.** 소스는 각 조각의 살아 있는 판 칸이고, 퍼지는 그래프는 설계도
    /// 격자에서 <see cref="Cell.Exterior"/>가 아닌 칸을 **4방향**으로 이은 것이다.
    /// 8방향으로 하면 계단식 절단면의 대각선을 타고 남의 조각으로 건너뛴다 - 방 BFS가
    /// 4방향인 것과 같은 이유다.
    ///
    /// **어느 소스에도 안 닿은 칸은 어느 집합에도 안 들어간다 = 삭제된다.** 판이 한 장도
    /// 없는 영역이 생길 수 있기 때문이다: 조각은 살아 있는 판에서 나오는데 후면은 설계도
    /// (불변)에서 나오므로, 판이 전멸한 토막의 후면 칸은 주인이 없다. Armor에는 이 상황이
    /// 존재할 수 없다 - 판이 0장이면 조각 자체가 안 생긴다.
    ///
    /// **동점은 조각 순서로 깬다.** 두 조각에서 같은 거리인 칸은 chunks에서 먼저 오는 쪽이
    /// 가져간다. BuildStructure의 조각 순서가 결정론적이므로 여기 결과도 결정론적이고,
    /// 그게 깨지면 리플레이가 어긋난다.
    /// </summary>
    /// <param name="chunks">구조 조각. <see cref="BuildStructure"/>의 반환값.</param>
    /// <param name="rear">지금 이 몸이 들고 있는 후면 칸. 이미 갈라진 뒤라면 부분집합이다.</param>
    public static RearOwners SplitRearOwners(
        List<List<Vector2Int>> chunks, ICollection<Vector2Int> rear)
    {
        if (rear.Count == 0)
            return default;

        // 맵 크기를 안 받는 함수라 rear의 bbox로 평면 격자를 만든다. rear 밖은 애초에
        // 배열에 없으니 "이 집합이 경계다"가 인덱스 범위 그 자체가 된다.
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        foreach (Vector2Int c in rear)
        {
            minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
            minY = Mathf.Min(minY, c.y); maxY = Mathf.Max(maxY, c.y);
        }

        int w = maxX - minX + 1;
        int h = maxY - minY + 1;

        // BFS 스크래치와 같은 이유로 정적 재사용이다: 파단은 OnTick 앞에서만 돌고 그 안에서
        // 피해가 안 나가므로 재진입이 없다. 파단은 연쇄로 오는데 매번 bbox 크기 배열 셋을
        // 새로 만들면 그 틱에 GC.Collect가 걸린다.
        int size = w * h;

        if (_rearIsRear.Length < size)
        {
            _rearIsRear = new bool[size];
            _rearOwner = new int[size];
            _rearQueue = new int[size];
        }
        else
        {
            System.Array.Clear(_rearIsRear, 0, size);
        }

        bool[] isRear = _rearIsRear;
        int[] owner = _rearOwner;

        for (int i = 0; i < size; i++)
            owner[i] = -1;

        foreach (Vector2Int c in rear)
            isRear[(c.y - minY) * w + (c.x - minX)] = true;

        int[] queue = _rearQueue;
        int head = 0;
        int tail = 0;

        // **모든 조각의 씨앗을 먼저 다 넣는다.** 조각 하나씩 끝까지 퍼뜨리면 첫 조각이
        // 전부 먹는다. 같이 퍼져야 "가까운 쪽이 가져간다"가 되고, 동점은 큐에 먼저 들어간
        // 조각 - 즉 chunks 순서 - 이 이긴다.
        for (int i = 0; i < chunks.Count; i++)
        {
            foreach (Vector2Int cell in chunks[i])
            {
                int cx = cell.x - minX;
                int cy = cell.y - minY;

                // 씨앗은 rear의 부분집합이 아닐 수 있다(판은 살았는데 후면은 뚫린 칸).
                if (cx < 0 || cy < 0 || cx >= w || cy >= h)
                    continue;

                int idx = cy * w + cx;

                if (!isRear[idx] || owner[idx] >= 0)
                    continue;

                owner[idx] = i;
                queue[tail++] = idx;
            }
        }

        while (head < tail)
        {
            int at = queue[head++];
            int ac = at % w;
            int ar = at / w;

            // 방과 같은 4방향이다. 8방향으로 하면 계단식 절단면의 대각선을 타고 남의
            // 조각으로 건너뛴다.
            for (int d = 0; d < Dirs.Length; d++)
            {
                int nc = ac + Dirs[d].x;
                int nr = ar + Dirs[d].y;

                if (nc < 0 || nr < 0 || nc >= w || nr >= h)
                    continue;

                int ni = nr * w + nc;

                if (!isRear[ni] || owner[ni] >= 0)
                    continue;

                owner[ni] = owner[at];
                queue[tail++] = ni;
            }
        }

        // 어느 씨앗에도 안 닿은 칸은 주인이 -1로 남는다. 그것이 곧 삭제다 - 지우는 코드가
        // 따로 없는 것이 요점이다.
        return new RearOwners(owner, minX, minY, w, h);
    }
    /// <summary>Around[i]와 Around[j]가 서로 8이웃인가(체비쇼프 거리 1)를 비트로 깐 표.</summary>
    private static readonly int[] RingAdjacency = BuildRingAdjacency();

    private static int[] BuildRingAdjacency()
    {
        var adj = new int[8];

        for (int i = 0; i < 8; i++)
        for (int j = 0; j < 8; j++)
        {
            if (i == j)
                continue;

            Vector2Int d = Around[i] - Around[j];

            if (Mathf.Abs(d.x) <= 1 && Mathf.Abs(d.y) <= 1)
                adj[i] |= 1 << j;
        }

        return adj;
    }

    /// <summary>
    /// 칸 하나가 죽을 때 선체가 갈라질 **가능성**이 있는가. false면 파단 BFS를 안 돌아도 된다.
    ///
    /// 규칙: 죽는 칸의 살아 있는 8이웃들이 3x3 링 안에서 **8방향으로** 한 덩어리면, 그 칸을
    /// 지나던 모든 경로가 링으로 우회되므로 전역 연결성이 안 변한다. 링 안에서 못 이어져도
    /// 링 밖으로 돌아 이어질 수 있으므로, true는 "가른다"가 아니라 "몰라서 BFS에 물어본다"다 -
    /// 보수적으로만 틀린다.
    ///
    /// **선체와 같은 8방향으로 세야 한다.** 4방향이면 L자 모서리의 대각 연결을 못 보고
    /// 매번 헛BFS를 돈다. 이웃 0~1개는 자명하게 못 가른다 - 외판 표면 피격 대부분이
    /// 여기서 끝난다. 죽는 칸 자신은 안 보므로 alive에서 빼기 전이든 후든 결과가 같다.
    /// </summary>
    public static bool RemovalMightSplit(HashSet<Vector2Int> alive, Vector2Int cell)
    {
        int mask = 0;

        for (int i = 0; i < 8; i++)
        {
            if (alive.Contains(cell + Around[i]))
                mask |= 1 << i;
        }

        return RingMaskMightSplit(mask);
    }

    /// <summary>평면 마스크판. HullStructure의 장부가 이 모양이라 실전은 이쪽으로 온다.</summary>
    public static bool RemovalMightSplit(bool[] alive, int width, int height, Vector2Int cell)
    {
        int mask = 0;

        for (int i = 0; i < 8; i++)
        {
            int nc = cell.x + Around[i].x;
            int nr = cell.y + Around[i].y;

            if (nc < 0 || nr < 0 || nc >= width || nr >= height)
                continue;

            if (alive[nr * width + nc])
                mask |= 1 << i;
        }

        return RingMaskMightSplit(mask);
    }

    private static bool RingMaskMightSplit(int mask)
    {
        // 비트가 1개 이하 = 이웃 0~1개.
        if ((mask & (mask - 1)) == 0)
            return false;

        // 8칸짜리 그래프라 flood도 비트마스크로 돈다. 최하위 비트에서 출발.
        int reached = mask & -mask;
        bool grew = true;

        while (grew)
        {
            grew = false;

            for (int i = 0; i < 8; i++)
            {
                if ((reached & (1 << i)) == 0)
                    continue;

                int add = RingAdjacency[i] & mask & ~reached;

                if (add != 0)
                {
                    reached |= add;
                    grew = true;
                }
            }
        }

        // 링 안에서 두 덩어리면 가를 가능성이 있다.
        return reached != mask;
    }

    // BuildStructure 전용 스크래치. 연사 맞는 동안 판이 죽는 틱마다 전체 선체 BFS가 도는
    // 자리라, HashSet<Vector2Int>(칸·이웃마다 해싱)와 큐를 매번 새로 만들면 그게 곧 프레임이다.
    // 정적 재사용이 안전한 이유: 호출자는 Build와 TrySplitIfBroken뿐이고 둘 다 틱 루프
    // 바깥이라 재진입이 없다 - 유폭 한가운데서 불리는 RamImpact.Conduct와 다른 점.
    private static bool[] _bfsAlive = System.Array.Empty<bool>();
    private static bool[] _bfsVisited = System.Array.Empty<bool>();

    // 큐도 평면 배열 + head/tail이다. BFS는 칸당 최대 1회 입큐라 되감기(wrap)가 필요 없고,
    // Queue<T>의 버전 검사·용량 조정이 통째로 빠진다.
    private static int[] _bfsQueue = System.Array.Empty<int>();

    private static bool[] _rearIsRear = System.Array.Empty<bool>();
    private static int[] _rearOwner = System.Array.Empty<int>();
    private static int[] _rearQueue = System.Array.Empty<int>();

    /// <summary>테스트 픽스처용. 실전은 마스크판이 쓴다 - 장부가 이미 마스크라 복사가 없다.</summary>
    public static List<List<Vector2Int>> BuildStructure(Map map, HashSet<Vector2Int> alive)
    {
        int size = map.width * map.height;

        if (_bfsAlive.Length < size)
            _bfsAlive = new bool[size];
        else
            System.Array.Clear(_bfsAlive, 0, size);

        foreach (Vector2Int c in alive)
            _bfsAlive[c.y * map.width + c.x] = true;

        return BuildStructure(map, _bfsAlive);
    }

    /// <summary>
    /// 아직 살아 있는 실물 칸들을 8방향으로 이어 붙여 덩어리로 나눈다.
    /// 큰 것부터 정렬해서 돌려주므로 [0]이 본체다.
    /// 씨앗은 인덱스 순(행 우선)으로 돈다 - HashSet 순회 순서에 기대던 시절보다 오히려
    /// 예측 가능해졌고, 동률 크기 조각의 순서가 곧 발견 순서다.
    /// </summary>
    // 조각 목록도 정적 재사용이다. BFS 스크래치와 같은 근거이고 여기 더해 하나 더:
    // **반환값은 다음 BuildStructure 호출 전까지만 유효하다.** 소비자는 TrySplitIfBroken과
    // Build 둘뿐이고 둘 다 받은 자리에서 다 쓴 뒤 놓는다(Breakaway -> Adopt는 칸을
    // 자기 장부로 베껴 간다). 그 사이에 BuildStructure가 다시 불리는 길이 없다.
    private static readonly List<List<Vector2Int>> _chunks = new();
    private static readonly List<List<Vector2Int>> _chunkPool = new();
    private static int _chunkPoolUsed;

    private static List<Vector2Int> RentChunk()
    {
        if (_chunkPoolUsed == _chunkPool.Count)
            _chunkPool.Add(new List<Vector2Int>());

        List<Vector2Int> chunk = _chunkPool[_chunkPoolUsed++];
        chunk.Clear();
        return chunk;
    }
   
    public static List<List<Vector2Int>> BuildStructure(Map map, bool[] alive)
    {
        List<List<Vector2Int>> chunks = _chunks;
        chunks.Clear();
        _chunkPoolUsed = 0;

        int width = map.width;
        int size = width * map.height;

        if (_bfsVisited.Length < size)
        {
            _bfsVisited = new bool[size];
            _bfsQueue = new int[size];
        }
        else
        {
            System.Array.Clear(_bfsVisited, 0, size);
        }

        for (int si = 0; si < size; si++)
        {
            if (!alive[si] || _bfsVisited[si])
                continue;

            _bfsVisited[si] = true;

            List<Vector2Int> cells = RentChunk();
            int head = 0;
            int tail = 0;
            _bfsQueue[tail++] = si;

            while (head < tail)
            {
                int at = _bfsQueue[head++];
                int col = at % width;
                int row = at / width;
                cells.Add(new Vector2Int(col, row));

                foreach (Vector2Int dir in Around)
                {
                    int nc = col + dir.x;
                    int nr = row + dir.y;

                    if (nc < 0 || nr < 0 || nc >= width || nr >= map.height)
                        continue;

                    int ni = nr * width + nc;

                    if (!alive[ni] || _bfsVisited[ni])
                        continue;

                    _bfsVisited[ni] = true;
                    _bfsQueue[tail++] = ni;
                }
            }

            chunks.Add(cells);
        }

        chunks.Sort((a, b) => b.Count.CompareTo(a.Count));
        map.version++;
        return chunks;
    }

    /// <summary>
    /// 옛 방들의 기압을 새 방들로 옮겨 담는다. <see cref="BuildRooms"/> 직후에 부른다.
    ///
    /// **공기는 상태다.** 방은 다시 지을 때마다 새 객체이고 <see cref="Room"/> 생성자는
    /// 만 기압으로 시작하므로, 그냥 두면 선체가 갈라질 때마다 진공이던 방이 다시 숨을 쉰다.
    /// 채워지고 새고 또 채워지는 증상이 그것이다.
    ///
    /// **칸 번호가 아니라 로컬 좌표를 열쇠로 쓴다.** <see cref="ShipBuilder.Stamp"/>가 원점을
    /// 살아남은 판의 극값에서 뽑기 때문에, 뱃머리가 떨어져 나가면 남은 칸의 번호가 통째로
    /// 밀린다. 같은 방이 (3,4)였다가 (0,4)가 되는 것이라 번호로 맞추면 엉뚱한 방의 공기를
    /// 붓는다. 판의 로컬 좌표는 재부모화를 겪지 않은 쪽에서는 안 변한다.
    ///
    /// 옛 방에 없던 칸은 0이다 - 방이 아니었다면 우주였고, 우주는 가져올 공기가 없다.
    /// </summary>
    public static void CarryAir(Map from, List<Room> fromRooms, Map to, List<Room> toRooms)
    {
        if (from == null || to == null || fromRooms == null || toRooms == null)
            return;

        var was = new Dictionary<Vector2Int, float>();

        foreach (Room room in fromRooms)
        {
            float pressure = room.Pressure;

            foreach (Vector2Int c in room.cells)
                was[Anchor(from, c)] = pressure;
        }

        foreach (Room room in toRooms)
        {
            float air = 0f;

            // 부피가 아니라 칸별 기압의 합이다. 벽이 사라져 두 방이 하나가 되면 이것이
            // 곧 섞인 결과고, 따로 섞는 코드가 필요 없다.
            foreach (Vector2Int c in room.cells)
                air += was.TryGetValue(Anchor(to, c), out float p) ? p : 0f;

            room.air = air;
        }
    }

    /// <summary>칸의 로컬 좌표를 정수로 굳힌 것. 격자가 밀려도 같은 칸은 같은 값이 나온다.</summary>
    public static Vector2Int Anchor(Map map, Vector2Int cell) =>
        Vector2Int.RoundToInt(map.ToLocal(cell.x, cell.y) / CellSize);

    /// <summary>
    /// 공기가 통하는 칸을 4방향으로 번진다. Wall에 닿으면 그 칸의 Armor를 방의 벽 목록에,
    /// Door에 닿으면 그 문을 방의 문 목록에 넣고 거기서 멈춘다.
    /// Exterior와 맵 밖은 막기만 하고 아무것도 기록하지 않는다 - 붙잡을 판이 없다.
    /// </summary>
    public static List<Room> BuildRooms(
        Map map,
        Dictionary<Vector2Int, Armor> armorAt,
        Dictionary<Vector2Int, Door> doorAt)
    {
        int w = map.width;
        int h = map.height;
        var rooms = new List<Room>();
        var visited = new bool[w * h];   // 평면 인덱스. 2차원 배열은 인덱싱마다 경계검사가 두 번이다
        var queue = new int[w * h];      // 모든 방을 합쳐도 칸당 1회 입큐라 리셋 없이 이어 쓴다
        int head = 0;
        int tail = 0;
        var border = new HashSet<Vector2Int>();

        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            if (visited[row * w + col] || map.cells[col, row] != Cell.Empty)
                continue;

            var cells = new List<Vector2Int>();

            visited[row * w + col] = true;
            queue[tail++] = row * w + col;

            // 120x40 함선이면 재귀는 스택을 넘긴다
            while (head < tail)
            {
                int at = queue[head++];
                int ac = at % w;
                int ar = at / w;
                cells.Add(new Vector2Int(ac, ar));

                for (int d = 0; d < Dirs.Length; d++)
                {
                    int nc = ac + Dirs[d].x;
                    int nr = ar + Dirs[d].y;

                    if (nc < 0 || nr < 0 || nc >= w || nr >= h)
                        continue;

                    int ni = nr * w + nc;

                    if (!Passable(map.cells[nc, nr]) || visited[ni])
                        continue;

                    visited[ni] = true;
                    queue[tail++] = ni;
                }
            }

            var room = new Room(cells);
            border.Clear();

            bool openToSpace = false;

            foreach (Vector2Int cell in cells)
            foreach (Vector2Int dir in Dirs)
            {
                Vector2Int n = cell + dir;

                if (!map.Inside(n))
                {
                    openToSpace = true;
                    continue;
                }

                Cell kind = map.cells[n.x, n.y];

                if (kind == Cell.Exterior)
                    openToSpace = true;

                if (Passable(kind) || kind == Cell.Exterior || !border.Add(n))
                    continue;

                if (kind == Cell.Wall)
                    room.boundaryPlates++;

                if (kind == Cell.Door)
                {
                    if (doorAt != null && doorAt.TryGetValue(n, out Door door) && door != null)
                        room.doors.Add(door);
                }
                else if (armorAt != null && armorAt.TryGetValue(n, out Armor wall) && wall != null)
                {
                    room.walls.Add(wall);
                }
            }

            // 판이 없는 경계는 영원히 새지 않는 벽으로 조용히 굳는다. 배치 누락의 증상이
            // 아무것도 아니라서, 여기서 소리내지 않으면 몇 시간을 태운다.
            //
            // MarkExterior 이후로는 이게 뜨는 경우가 하나뿐이다: 실내 칸이 배 테두리에
            // 닿아 있는데 그 바깥을 막는 판이 없다.
            if (openToSpace)
                Debug.LogWarning(
                    $"[ShipGrid] 방 {rooms.Count}({cells[0]} 부근)의 경계 일부가 뚫려 있다. " +
                    "판이 빠졌는지 확인할 것 - 그쪽으로는 절대 감압되지 않는다.");

            rooms.Add(room);
        }

        return rooms;
    }
}
