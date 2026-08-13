#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Ship > Run Ship Grid Tests.
/// ParseMap과 BuildRooms는 씬을 건드리지 않으므로 플레이 모드도 GameObject도 필요 없다.
/// </summary>
public static class ShipGridSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Ship/Run Ship Grid Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        // 뚫린 상자 하나 -> 방 하나, 내부 3x2
        {
            var map = ShipGrid.ParseMap(Lines(
                "#####",
                "#...#",
                "#...#",
                "#####"));

            var rooms = ShipGrid.BuildRooms(map, null, null);

            Check("single box: one room", rooms.Count == 1);
            Check($"single box: 6 cells (got {(rooms.Count > 0 ? rooms[0].cells.Count : -1)})",
                rooms.Count == 1 && rooms[0].cells.Count == 6);
            Check("single box: starts at 1 atm",
                rooms.Count == 1 && Mathf.Approximately(rooms[0].Pressure, 1f));
        }

        // 통짜 벽으로 갈린 두 칸
        {
            var map = ShipGrid.ParseMap(Lines(
                "#######",
                "#..#..#",
                "#..#..#",
                "#######"));

            var rooms = ShipGrid.BuildRooms(map, null, null);

            Check("solid divider: two rooms", rooms.Count == 2);
            Check("solid divider: 4 cells each",
                rooms.Count == 2 && rooms[0].cells.Count == 4 && rooms[1].cells.Count == 4);
        }

        // 문이 뚫린 벽도 여전히 방을 가른다. 대신 양쪽이 같은 문을 든다.
        {
            var map = ShipGrid.ParseMap(Lines(
                "#######",
                "#..D..#",
                "#..#..#",
                "#######"));

            GameObject go = new GameObject("selftest door");
            go.SetActive(false);   // AddComponent가 OnEnable을 때려 TickManager를 깨우지 않도록
            go.AddComponent<BoxCollider2D>();   // RequireComponent가 추상 Collider2D를 붙이려 들지 않게

            var doorAt = new Dictionary<Vector2Int, Door> { { new Vector2Int(3, 1), go.AddComponent<Door>() } };
            var rooms = ShipGrid.BuildRooms(map, null, doorAt);

            Check("door divider: still two rooms", rooms.Count == 2);
            Check("door divider: both rooms list the same door",
                rooms.Count == 2
                && rooms[0].doors.Count == 1
                && rooms[1].doors.Count == 1
                && rooms[0].doors[0] == rooms[1].doors[0]);

            UnityEngine.Object.DestroyImmediate(go);
        }

        // 좌표 왕복. 짝수/홀수 폭 둘 다, 모서리 포함.
        {
            bool ok = true;

            foreach (Vector2Int size in new[] { new Vector2Int(7, 4), new Vector2Int(6, 5) })
            foreach (Vector2Int c in new[]
            {
                Vector2Int.zero,
                new Vector2Int(size.x - 1, 0),
                new Vector2Int(0, size.y - 1),
                new Vector2Int(size.x - 1, size.y - 1),
                new Vector2Int(size.x / 2, size.y / 2),
            })
            {
                ok &= ShipGrid.ToCell(ShipGrid.ToLocal(c.x, c.y, size.x, size.y), size.x, size.y) == c;
            }

            Check("cell <-> local round trip", ok);

            // 첫 줄이 위쪽이라는 규약 자체를 못 박는다
            Check("row 0 is the top", ShipGrid.ToLocal(0, 0, 5, 4).y > ShipGrid.ToLocal(0, 3, 5, 4).y);
        }

        // 손으로 센 내부 칸 수: 5 + 3 + 5 = 13, 기둥을 둘러 한 방으로 이어진다
        {
            var map = ShipGrid.ParseMap(Lines(
                "#######",
                "#.....#",
                "#.#.#.#",
                "#.....#",
                "#######"));

            var rooms = ShipGrid.BuildRooms(map, null, null);

            Check("pillared hall: one room", rooms.Count == 1);
            Check($"pillared hall: 13 cells (got {(rooms.Count > 0 ? rooms[0].cells.Count : -1)})",
                rooms.Count == 1 && rooms[0].cells.Count == 13);
            Check("pillared hall: 7x5", map.width == 7 && map.height == 5);
        }

        // 엔진은 방 안의 물건이지 격벽이 아니다. 이걸 막으면 기관실이 두 방으로 쪼개진다.
        {
            var map = ShipGrid.ParseMap(Lines(
                "#######",
                "#..E..#",
                "#######"));

            var rooms = ShipGrid.BuildRooms(map, null, null);

            Check("engine does not split the room", rooms.Count == 1);
            Check($"engine cell counts as volume (got {(rooms.Count > 0 ? rooms[0].cells.Count : -1)})",
                rooms.Count == 1 && rooms[0].cells.Count == 5);
        }

        Debug.Log($"[ShipGrid] {_pass} passed, {_fail} failed.");
    }

    private static string Lines(params string[] rows) => string.Join("\n", rows);

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            _pass++;
            return;
        }

        _fail++;
        Debug.LogError($"[ShipGrid] FAIL {name}");
    }
}
#endif
