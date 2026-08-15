#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Tools > Ship > Run Ship Grid Tests.
/// MarkExterior와 BuildRooms는 순수 함수라 플레이 모드가 필요 없다. Stamp만 자식 트랜스폼이
/// 필요해서 비활성 GameObject를 잠깐 만든다.
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

            Object.DestroyImmediate(go);
        }

        // 좌표 왕복. 짝수/홀수 폭 둘 다, 모서리 포함.
        {
            bool ok = true;

            foreach (Vector2Int size in new[] { new Vector2Int(7, 4), new Vector2Int(6, 5) })
            {
                ShipGrid.Map map = ShipGrid.Map.Centred(size.x, size.y);

                foreach (Vector2Int c in new[]
                {
                    Vector2Int.zero,
                    new Vector2Int(size.x - 1, 0),
                    new Vector2Int(0, size.y - 1),
                    new Vector2Int(size.x - 1, size.y - 1),
                    new Vector2Int(size.x / 2, size.y / 2),
                })
                {
                    ok &= map.ToCell(map.ToLocal(c.x, c.y)) == c;
                }
            }

            Check("cell <-> local round trip", ok);

            // 첫 줄이 위쪽이라는 규약 자체를 못 박는다
            Check("row 0 is the top",
                ShipGrid.Map.Centred(5, 4).ToLocal(0, 0).y > ShipGrid.Map.Centred(5, 4).ToLocal(0, 3).y);
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

        // 실내/우주는 이제 도장이 아니라 flood로 갈린다. 도넛 한가운데 구멍은 테두리에서
        // 4방향으로 못 닿으므로 실내고, 따라서 한 칸짜리 방이다.
        //
        // 이걸 틀리면 증상이 "배 한복판에 진공이 한 칸 생겼는데 아무도 감압을 안 한다"라서
        // 원인을 절대 못 찾는다.
        {
            var map = ShipGrid.ParseMap(Lines(
                "#####",
                "#####",
                "##.##",
                "#####",
                "#####"));

            var rooms = ShipGrid.BuildRooms(map, null, null);

            Check("sealed pocket: hole is interior",
                map.cells[2, 2] == ShipGrid.Cell.Empty);
            Check($"sealed pocket: one room of one cell " +
                  $"(got {rooms.Count} rooms)",
                rooms.Count == 1 && rooms[0].cells.Count == 1);
        }

        // 배 바깥의 오목한 곳은 우주다. 4방향으로 테두리에서 들어올 수 있으면 실내가 아니다.
        {
            var map = ShipGrid.ParseMap(Lines(
                "#.#",
                "#.#",
                "###"));

            Check("open notch is space", map.cells[1, 0] == ShipGrid.Cell.Exterior
                                      && map.cells[1, 1] == ShipGrid.Cell.Exterior);
        }

        StampTest();

        Debug.Log($"[ShipGrid] {_pass} passed, {_fail} failed.");
    }

    /// <summary>
    /// 격자를 자식들의 '위치'에서 파생한다. 원점 중심이 아니어도, 폭이 짝수여도 맞아야 한다 -
    /// 예전 ToCell은 원점 중심을 가정해서 짝수 폭에서 정확히 0.5를 반올림했고,
    /// RoundToInt의 은행가 반올림이 거기서 한 칸을 먹었다.
    /// </summary>
    private static void StampTest()
    {
        var hull = new GameObject("selftest hull");
        hull.SetActive(false);

        try
        {
            Plate(hull.transform, 5f, -5f);
            Plate(hull.transform, 6f, -5f);
            Plate(hull.transform, 5f, -4f);

            var armorAt = new Dictionary<Vector2Int, Armor>();
            var doorAt = new Dictionary<Vector2Int, Door>();

            ShipGrid.Map map = ShipBuilder.Stamp(hull.transform, armorAt, doorAt);

            Check("stamp: 2x2 map from off-origin plates",
                map != null && map.width == 2 && map.height == 2);

            if (map == null)
                return;

            // y = -5가 아래쪽이므로 row 1. y = -4가 row 0.
            Check("stamp: plates land on the right cells",
                map.cells[0, 1] == ShipGrid.Cell.Wall
                && map.cells[1, 1] == ShipGrid.Cell.Wall
                && map.cells[0, 0] == ShipGrid.Cell.Wall);

            Check("stamp: the empty corner is space", map.cells[1, 0] == ShipGrid.Cell.Exterior);
            Check("stamp: armorAt is keyed by the same cells", armorAt.Count == 3);
        }
        finally
        {
            Object.DestroyImmediate(hull);
        }
    }

    private static void Plate(Transform hull, float x, float y)
    {
        var go = new GameObject("selftest plate");
        go.SetActive(false);
        go.transform.SetParent(hull, worldPositionStays: false);
        go.transform.localPosition = new Vector3(x, y, 0f);
        go.AddComponent<BoxCollider2D>();
        go.AddComponent<BallisticArmor>();
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
