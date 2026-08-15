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
        public Cell[,] cells;   // [col, row]
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
    private static bool Passable(Cell c) => c == Cell.Empty;

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

        return rooms;
    }
}
