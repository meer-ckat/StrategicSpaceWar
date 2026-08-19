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
    public enum Cell { Unset, Exterior, Wall, Empty, Door }

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
    public static bool BackPlate(Cell c) => c != Cell.Exterior;

    /// <summary>
    /// 하중을 받는 칸. 공기가 다니는 그래프(Passable)와 정반대인 것이 요점이다 -
    /// 방은 빈 칸으로 이어지고, 선체는 실물로 이어진다.
    /// </summary>
    public static bool Solid(Cell c) => c == Cell.Wall || c == Cell.Door;

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
        var outside = new bool[map.width, map.height];
        var queue = new Queue<Vector2Int>();

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            bool border = col == 0 || row == 0 || col == map.width - 1 || row == map.height - 1;

            if (!border || Solid(map.cells[col, row]) || outside[col, row])
                continue;

            outside[col, row] = true;
            queue.Enqueue(new Vector2Int(col, row));
        }

        while (queue.Count > 0)
        {
            Vector2Int at = queue.Dequeue();

            foreach (Vector2Int dir in Dirs)
            {
                Vector2Int n = at + dir;

                if (!map.Inside(n) || outside[n.x, n.y] || Solid(map.cells[n.x, n.y]))
                    continue;

                outside[n.x, n.y] = true;
                queue.Enqueue(n);
            }
        }

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (Solid(map.cells[col, row]))
                continue;

            map.cells[col, row] = outside[col, row] ? Cell.Exterior : Cell.Empty;
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
    /// <param name="design">설계도 격자. 판이 죽어도 안 변하는 쪽이다.</param>
    /// <param name="chunks">구조 조각. <see cref="BuildStructure"/>의 반환값.</param>
    /// <param name="rear">지금 이 몸이 들고 있는 후면 칸. 이미 갈라진 뒤라면 부분집합이다.</param>
    public static List<HashSet<Vector2Int>> SplitRear(
        List<List<Vector2Int>> chunks, HashSet<Vector2Int> rear)
    {
        var results = new List<HashSet<Vector2Int>>(chunks.Count);

        // 용량이 아니라 개수다. new List(n)은 자리를 잡아둘 뿐이라 results[i]가 아직 없다.
        for (int i = 0; i < chunks.Count; i++)
            results.Add(new HashSet<Vector2Int>());

        var queue = new Queue<Vector2Int>();
        var owner = new Dictionary<Vector2Int, int>();

        // **모든 조각의 씨앗을 먼저 다 넣는다.** 조각 하나씩 끝까지 퍼뜨리면 첫 조각이
        // 전부 먹는다. 같이 퍼져야 "가까운 쪽이 가져간다"가 되고, 동점은 큐에 먼저 들어간
        // 조각 - 즉 chunks 순서 - 이 이긴다.
        for (int i = 0; i < chunks.Count; i++)
        {
            foreach (Vector2Int cell in chunks[i])
            {
                if (!rear.Contains(cell) || owner.ContainsKey(cell))
                    continue;

                owner[cell] = i;
                results[i].Add(cell);
                queue.Enqueue(cell);
            }
        }

        while (queue.Count > 0)
        {
            Vector2Int at = queue.Dequeue();

            // 방과 같은 4방향이다. 8방향으로 하면 계단식 절단면의 대각선을 타고 남의
            // 조각으로 건너뛴다.
            foreach (Vector2Int dir in Dirs)
            {
                Vector2Int next = at + dir;

                // rear 밖이면 안 간다. 이 검사가 없으면 사방으로 영원히 번진다 -
                // 격자 경계가 아니라 이 집합이 경계다.
                if (!rear.Contains(next) || owner.ContainsKey(next))
                    continue;

                owner[next] = owner[at];
                results[owner[next]].Add(next);
                queue.Enqueue(next);
            }
        }

        // 어느 씨앗에도 안 닿은 칸은 어느 집합에도 없다. 그것이 곧 삭제다 - 지우는 코드가
        // 따로 없는 것이 요점이다.
        return results;
    }
    /// <summary>
    /// 아직 살아 있는 실물 칸들을 8방향으로 이어 붙여 덩어리로 나눈다.
    /// 큰 것부터 정렬해서 돌려주므로 [0]이 본체다.
    /// </summary>
    public static List<List<Vector2Int>> BuildStructure(Map map, HashSet<Vector2Int> alive)
    {
        
        var chunks = new List<List<Vector2Int>>();
        var visited = new HashSet<Vector2Int>();
        var queue = new Queue<Vector2Int>();

        foreach (Vector2Int seed in alive)
        {
            if (!visited.Add(seed))
                continue;

            var cells = new List<Vector2Int>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                Vector2Int at = queue.Dequeue();
                cells.Add(at);

                foreach (Vector2Int dir in Around)
                {
                    Vector2Int n = at + dir;

                    if (!map.Inside(n) || !alive.Contains(n) || !visited.Add(n))
                        continue;

                    queue.Enqueue(n);
                }
            }

            chunks.Add(cells);
        }

        chunks.Sort((a, b) => b.Count.CompareTo(a.Count));
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
    private static Vector2Int Anchor(Map map, Vector2Int cell) =>
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
        var rooms = new List<Room>();
        var visited = new bool[map.width, map.height];
        var queue = new Queue<Vector2Int>();
        var border = new HashSet<Vector2Int>();

        //BFS, 원래 map의 총 셀 수와 visited가 같아질 때까지 반복하는게 기본이긴 한데, 여기선 전체 map의 행과 열의 크기를 앎으로 이런식으로 한듯?
        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (visited[col, row] || map.cells[col, row] != Cell.Empty)
                continue;

            var cells = new List<Vector2Int>();

            visited[col, row] = true;
            queue.Enqueue(new Vector2Int(col, row));

            // 120x40 함선이면 재귀는 스택을 넘긴다
            while (queue.Count > 0)
            {
                Vector2Int at = queue.Dequeue();
                cells.Add(at);

                foreach (Vector2Int dir in Dirs)
                {
                    Vector2Int n = at + dir;

                    if (!map.Inside(n) || !Passable(map.cells[n.x, n.y]) || visited[n.x, n.y])
                        continue;

                    visited[n.x, n.y] = true;
                    queue.Enqueue(n);
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

        return rooms; //모든 청크를 Return
    }
}
