using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 함선은 1m 격자다. 텍스트 맵이 원본이고, 자식 배치와 방 구획은 전부 여기서 파생된다.
/// </summary>
public static class ShipGrid
{
    public const float CellSize = 1f;

    public enum Cell { Exterior, Wall, Empty, Door, Engine }

    public class Map
    {
        public Cell[,] cells;   // [col, row]
        public int width;
        public int height;

        public bool Inside(Vector2Int c) =>
            c.x >= 0 && c.y >= 0 && c.x < width && c.y < height;
    }

    private static readonly Vector2Int[] Dirs =
    {
        Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down,
    };

    public static Map ParseMap(string text)
    {
        // 파일 끝 개행이 유령 행을 만들어 함선 중심을 반 칸 밀어버린다
        string[] lines = (text ?? string.Empty).Replace("\r", "").Trim('\n').Split('\n');

        int width = 0;
        foreach (string line in lines)
            width = Mathf.Max(width, line.Length);

        var map = new Map
        {
            width = width,
            height = lines.Length,
            cells = new Cell[width, lines.Length],
        };

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < width; col++)
        {
            char c = col < lines[row].Length ? lines[row][col] : ' ';

            // 모르는 문자를 조용히 Exterior로 삼키면 방 한가운데 장갑 없는 우주가 한 칸
            // 박히고, 증상은 다른 곳에서 엉뚱하게 나온다. 문자를 짚어서 말해준다.
            if (c != ' ' && c != '#' && c != '.' && c != 'D' && c != 'E')
                Debug.LogWarning(
                    $"[ShipGrid] {row}행 {col}열의 '{c}'는 모르는 문자다. " +
                    "함선 외부로 처리한다 - 아는 문자는 '#' '.' 'D' 'E' 뿐이다.");

            map.cells[col, row] = c switch
            {
                '#' => Cell.Wall,
                '.' => Cell.Empty,
                'D' => Cell.Door,
                'E' => Cell.Engine,
                _ => Cell.Exterior,
            };
        }

        return map;
    }

    /// <summary>y를 뒤집는 이유: 맵 첫 줄이 함선의 위쪽이어야 글로 보이는 대로 배가 나온다.</summary>
    public static Vector2 ToLocal(int col, int row, int width, int height) =>
        new Vector2(col - (width - 1) * 0.5f, -(row - (height - 1) * 0.5f)) * CellSize;

    public static Vector2Int ToCell(Vector2 local, int width, int height) =>
        new Vector2Int(
            Mathf.RoundToInt(local.x / CellSize + (width - 1) * 0.5f),
            Mathf.RoundToInt(-local.y / CellSize + (height - 1) * 0.5f));

    /// <summary>엔진은 방 안에 놓인 물건이지 격벽이 아니다. 공기는 그 위로 지나간다.</summary>
    private static bool Passable(Cell c) => c == Cell.Empty || c == Cell.Engine;

    /// <summary>
    /// 하중을 받는 칸. 공기가 다니는 그래프(Passable)와 정반대인 것이 요점이다 -
    /// 방은 빈 칸으로 이어지고, 선체는 실물로 이어진다.
    /// </summary>
    public static bool Solid(Cell c) => c == Cell.Wall || c == Cell.Door || c == Cell.Engine;

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

            // 판이 없는 경계는 영원히 새지 않는 벽으로 조용히 굳는다. 맵 오타의 증상이
            // 아무것도 아니라서, 여기서 소리내지 않으면 몇 시간을 태운다.
            if (openToSpace)
                Debug.LogWarning(
                    $"[ShipGrid] 방 {rooms.Count}({cells[0]} 부근)의 경계 일부가 맵에서 비어 있다. " +
                    "'#'을 빠뜨렸는지 확인할 것 - 그쪽으로는 절대 감압되지 않는다.");

            rooms.Add(room);
        }

        return rooms;
    }
}
