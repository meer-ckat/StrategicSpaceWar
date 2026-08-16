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
        MirroredHullTest();
        OffGridPlateTest();
        PlateLostTest();

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

    /// <summary>
    /// **좌우 반전된 배도 똑같은 격자를 얻는다.** 반대쪽에서 오는 함선은 `localScale.x = -1`인데,
    /// `localPosition`은 부모의 scale과 무관하므로 Stamp가 보는 값이 하나도 안 바뀐다.
    ///
    /// 이게 버그가 아니라 설계다 - 격자는 로컬 위상이고 반전은 월드에 그릴 때의 일이다.
    /// Stamp에 scale을 곱하려 들면 같은 설계도가 두 벌이 되고, 칸 번호에 -1을 곱하는 순간
    /// (반사는 `-x`가 아니라 `width-1-x`다) 인덱스가 음수로 나가 배열 밖으로 나간다.
    ///
    /// 반전을 실제로 처리해야 하는 곳은 월드로 나가는 두 자리뿐이다: RoomView의 오버레이
    /// scale과 HullStructure.Breakaway의 잔해 scale.
    /// </summary>
    private static void MirroredHullTest()
    {
        var upright = new GameObject("selftest upright hull");
        var mirrored = new GameObject("selftest mirrored hull");
        upright.SetActive(false);
        mirrored.SetActive(false);

        try
        {
            mirrored.transform.localScale = new Vector3(-1f, 1f, 1f);

            // 좌우가 다른 모양이어야 의미가 있다. 대칭이면 반전돼도 티가 안 난다.
            foreach (GameObject hull in new[] { upright, mirrored })
            {
                Plate(hull.transform, 0f, 0f);
                Plate(hull.transform, 1f, 0f);
                Plate(hull.transform, 2f, 0f);
                Plate(hull.transform, 0f, -1f);
            }

            var armorA = new Dictionary<Vector2Int, Armor>();
            var armorB = new Dictionary<Vector2Int, Armor>();

            ShipGrid.Map a = ShipBuilder.Stamp(upright.transform, armorA, new Dictionary<Vector2Int, Door>());
            ShipGrid.Map b = ShipBuilder.Stamp(mirrored.transform, armorB, new Dictionary<Vector2Int, Door>());

            if (a == null || b == null)
            {
                Check("mirrored: both stamped", false);
                return;
            }

            Check($"mirrored: same size ({a.width}x{a.height} vs {b.width}x{b.height})",
                a.width == b.width && a.height == b.height);

            Check("mirrored: same origin", (a.origin - b.origin).sqrMagnitude < 1e-6f);

            bool sameCells = true;

            for (int row = 0; row < a.height && sameCells; row++)
            for (int col = 0; col < a.width && sameCells; col++)
                sameCells = a.cells[col, row] == b.cells[col, row];

            Check("mirrored: identical cells", sameCells);
            Check($"mirrored: same plate count ({armorA.Count} vs {armorB.Count})",
                armorA.Count == armorB.Count && armorA.Count == 4);
        }
        finally
        {
            Object.DestroyImmediate(upright);
            Object.DestroyImmediate(mirrored);
        }
    }

    /// <summary>
    /// 격자에서 반 칸 벗어난 판은 **조용히 사라진다.** 이게 ShipBuilder의 잔차 경고가 막는
    /// 사고다 - 경고 자체는 여기서 못 잡지만(로그 단언 장치가 없다), 경고가 말하는 결과는
    /// 잡을 수 있다.
    ///
    /// 격자의 불변식은 "원점이 정수"가 아니라 "판끼리의 간격이 CellSize의 정수배"다.
    /// 원점은 ToCell에서 상쇄되므로 배가 통째로 x.5에 놓여도 멀쩡하다. 어긋나는 것은 잔차고,
    /// 0.5 잔차는 RoundToInt의 은행가 반올림에 걸려 이웃 칸으로 넘어간다.
    ///
    /// 이 테스트를 돌리면 ShipBuilder가 경고 두 개를 찍는다. 그게 정상이다 - 사람이 읽어야
    /// 할 문장이 어떻게 생겼는지 여기서 같이 보인다.
    /// </summary>
    private static void OffGridPlateTest()
    {
        var hull = new GameObject("selftest offgrid hull");
        hull.SetActive(false);

        try
        {
            Plate(hull.transform, 0f, 0f);
            Plate(hull.transform, 1f, 0f);
            Plate(hull.transform, 0.5f, 0f);   // 반 칸 어긋남

            var armorAt = new Dictionary<Vector2Int, Armor>();
            var doorAt = new Dictionary<Vector2Int, Door>();

            ShipGrid.Map map = ShipBuilder.Stamp(hull.transform, armorAt, doorAt);

            if (map == null)
            {
                Check("off-grid: stamped", false);
                return;
            }

            // 어긋난 판이 세 번째 열을 만들지 못한다. maxX가 1이라 폭은 그대로 2다.
            Check($"off-grid: still 2 columns wide (got {map.width})", map.width == 2);

            // RoundToInt(0.5)는 은행가 반올림으로 0이다. 세 번째 판이 첫 판의 칸을 먹고,
            // 하나가 격자에서 통째로 빠진다 - 방 경계에도 Neighbours에도 안 들어간다.
            Check($"off-grid: a plate is silently lost (armorAt {armorAt.Count} of 3 plates)",
                armorAt.Count == 2);
        }
        finally
        {
            Object.DestroyImmediate(hull);
        }
    }

    /// <summary>
    /// 판이 죽으면 격자에서도 그 칸이 지워진다. 오버레이가 이미 뚫린 자리를 계속 갑판으로
    /// 그리던 버그를 막는 것이 목적이고, 여기서 못 박는 것은 **그 수정이 파단 판정을 건드리지
    /// 않는다**는 쪽이다 - 셋이 서로 다른 원본을 보기 때문이다. BuildStructure는 map에서
    /// Inside(폭·높이)만 읽고, 살아 있는 칸은 인자로 따로 받는다.
    /// </summary>
    private static void PlateLostTest()
    {
        var hull = new GameObject("selftest hull");
        hull.SetActive(false);

        try
        {
            // 가로 일렬 세 칸. 가운데가 죽으면 8방향으로도 양 끝이 안 이어진다.
            Plate(hull.transform, 0f, 0f);
            Transform middle = Plate(hull.transform, 1f, 0f);
            Plate(hull.transform, 2f, 0f);

            var armorAt = new Dictionary<Vector2Int, Armor>();
            var doorAt = new Dictionary<Vector2Int, Door>();

            ShipGrid.Map map = ShipBuilder.Stamp(hull.transform, armorAt, doorAt);

            if (map == null)
            {
                Check("plate lost: stamped", false);
                return;
            }

            HullStructure structure = hull.AddComponent<HullStructure>();
            structure.Build(map, 2f);

            var mid = new Vector2Int(1, 0);
            var alive = new HashSet<Vector2Int> { new(0, 0), new(2, 0) };

            Check("plate lost: the cell is solid before the plate dies",
                ShipGrid.Solid(map.cells[mid.x, mid.y]));

            // 장부는 Build에서 격자 그대로 채워진다. 자식을 다시 세는 코드가 없으므로
            // 이 수가 어긋나면 넣고 빼는 네 자리 중 하나가 빠진 것이다.
            Check($"plate lost: ledger starts at 3 (got {structure.AliveCount})",
                structure.AliveCount == 3);

            int before = ShipGrid.BuildStructure(map, alive).Count;

            structure.ReportPlateLost(middle);

            Check("plate lost: the cell is cleared from the grid",
                !ShipGrid.Solid(map.cells[mid.x, mid.y]));

            Check($"plate lost: ledger drops to 2 (got {structure.AliveCount})",
                structure.AliveCount == 2);

            // 같은 판을 두 번 신고해도 장부가 음수로 가거나 두 번 빠지면 안 된다.
            // 파편 연쇄 중에는 같은 판에 피해가 여러 번 들어온다.
            structure.ReportPlateLost(middle);

            Check($"plate lost: reporting twice is idempotent (got {structure.AliveCount})",
                structure.AliveCount == 2);

            int after = ShipGrid.BuildStructure(map, alive).Count;

            Check($"plate lost: split verdict unchanged ({before} -> {after})", before == after);
            Check($"plate lost: still reads as two chunks (got {after})", after == 2);
        }
        finally
        {
            Object.DestroyImmediate(hull);
        }
    }

    private static Transform Plate(Transform hull, float x, float y)
    {
        var go = new GameObject("selftest plate");
        go.SetActive(false);
        go.transform.SetParent(hull, worldPositionStays: false);
        go.transform.localPosition = new Vector3(x, y, 0f);
        go.AddComponent<BoxCollider2D>();
        go.AddComponent<BallisticArmor>();
        return go.transform;
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
