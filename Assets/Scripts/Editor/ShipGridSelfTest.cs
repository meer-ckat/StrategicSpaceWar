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

        // 방을 다시 지어도 공기는 상태로 남아야 한다. Room 생성자가 만 기압으로 시작하므로
        // 그냥 두면 선체가 갈라질 때마다 진공이던 방이 다시 숨을 쉬고, 벽은 여전히 뚫려
        // 있으니 또 샌다 - "채워지고 새는" 증상이 그것이다.
        {
            var before = ShipGrid.ParseMap(Lines(
                "#####",
                "#...#",
                "#####"));

            List<Room> was = ShipGrid.BuildRooms(before, null, null);
            was[0].air = 0f;                       // 우주로 다 샜다

            var after = ShipGrid.ParseMap(Lines(
                "#####",
                "#...#",
                "#####"));

            List<Room> now = ShipGrid.BuildRooms(after, null, null);
            Check("rebuild: fresh room starts full", Mathf.Approximately(now[0].Pressure, 1f));

            ShipGrid.CarryAir(before, was, after, now);
            Check("rebuild: vacuum stays vacuum", Mathf.Approximately(now[0].Pressure, 0f));
        }

        // 그리고 **칸 번호로 옮기면 안 된다.** Stamp가 원점을 살아남은 판의 극값에서 뽑기
        // 때문에 조각이 떨어지면 남은 칸의 번호가 통째로 밀린다. 아래는 그 상황이다 -
        // 넓은 격자의 오른쪽 방만 남았는데, 번호로 맞추면 왼쪽(진공)의 공기를 가져온다.
        {
            var wide = ShipGrid.ParseMap(Lines(
                "#########",
                "#...#...#",
                "#########"));

            List<Room> was = ShipGrid.BuildRooms(wide, null, null);
            was[0].air = 0f;                       // 왼쪽 방은 진공, 오른쪽은 만 기압 그대로

            var narrow = ShipGrid.ParseMap(Lines(
                "#####",
                "#...#",
                "#####"));

            List<Room> now = ShipGrid.BuildRooms(narrow, null, null);
            ShipGrid.CarryAir(wide, was, narrow, now);

            // 좁은 격자의 세 칸은 로컬 x가 -1, 0, +1이다. 넓은 격자에서 그 자리는
            // 왼쪽 방의 끝 칸(진공) / 가운데 벽(없음) / 오른쪽 방의 첫 칸(만 기압)이다.
            Check($"rebuild: 원점이 밀려도 로컬 좌표로 따라간다 (got {now[0].air:0.###})",
                Mathf.Approximately(now[0].air, 1f));
        }

        RingRejectTest();
        StampTest();
        MirroredHullTest();
        OffGridPlateTest();
        PlateLostTest();
        AuthoredFrameTest();
        HullSkinSizeTest();
        StampFromDefTest();
        RearSplitTest();
        Check("shed rear: parent plate owns exactly its rear cell",
            HullStructure.ShedRearOwnershipSelfTest());
        Check("rear view: follows runtime mirror scale",
            BackPlateView.MirroredFollowSelfTest());
        Check("rear view: footprint crosses its anchor cell",
            BackPlateView.FootprintOverflowSelfTest());
        Check("rear view: quad uv maps back to the design rect",
            BackPlateView.QuadUvSelfTest());
        Check("rear view: plates enclose the rear silhouette",
            BackPlateView.SilhouetteFloodSelfTest());
        Check("solo debris: child origin and centre of mass are zero",
            HullStructure.SoloDebrisOriginSelfTest());
        DebrisHpTest();

        // 모듈 배치는 기하다. 판 넓이 비율 · 모듈 겹침 넓이 · 마운트 = 최대 겹침 판 · 자리 = 상자 중심 칸.
        {
            static ModulePlacement.Plate Rect(Vector2Int cell, Vector2 size, float rot = 0f)
            {
                Vector2[] poly = ModulePlacement.PlatePolygon(cell, "?", rot, size, Vector2.zero, null);
                return new ModulePlacement.Plate { cell = cell, poly = poly, area = Mathf.Abs(Ballistics.PolygonArea(poly)) };
            }

            var none = new List<ModulePlacement.Other>();
            System.Func<Vector2Int, bool> ship = _ => true;
            var at = new Vector2Int(5, 5);

            // 0.4 m 패널(row 4, 세로 0.4)에 1x1 모듈이 0.25 파고들면 패널의 62% -> 파묻힘
            var panel = new List<ModulePlacement.Plate> { Rect(new Vector2Int(5, 4), new Vector2(1f, 0.4f)) };
            var r = ModulePlacement.Evaluate("?", at, 0f, Vector2.one, new Vector2(0f, 0.55f), ship, panel, none);
            Check($"module: 얇은 판 62% 덮으면 InPlate (got {r.verdict})", r.verdict == ModulePlacement.Verdict.InPlate);

            r = ModulePlacement.Evaluate("?", at, 0f, Vector2.one, new Vector2(0f, 0.2f), ship, panel, none);
            Check($"module: 얇은 판 살짝 걸치면 Ok (got {r.verdict})", r.verdict == ModulePlacement.Verdict.Ok);

            // 자리 = 상자 중심 칸. offset 0.6이면 옆 칸이 자리다
            r = ModulePlacement.Evaluate("?", at, 0f, Vector2.one, new Vector2(0.6f, 0f), ship, new List<ModulePlacement.Plate>(), none);
            Check($"module: offset 0.6의 자리는 (6,5) (got {r.cell})", r.cell == new Vector2Int(6, 5));

            // 모듈끼리: 0.05 m² 겹침은 통과, 0.5 m²는 거부
            var otherA = new List<ModulePlacement.Other>
            {
                new() { origin = new Vector2Int(6, 5), poly = ModulePlacement.ModulePolygon(new Vector2Int(6, 5), "?", 0f, Vector2.one, new Vector2(-0.05f, 0f), out _, out _) },
            };
            r = ModulePlacement.Evaluate("?", at, 0f, Vector2.one, Vector2.zero, ship, new List<ModulePlacement.Plate>(), otherA);
            Check($"module: 0.05 m² 겹침은 Ok (got {r.verdict})", r.verdict == ModulePlacement.Verdict.Ok);

            var otherB = new List<ModulePlacement.Other>
            {
                new() { origin = new Vector2Int(6, 5), poly = ModulePlacement.ModulePolygon(new Vector2Int(6, 5), "?", 0f, Vector2.one, new Vector2(-0.5f, 0f), out _, out _) },
            };
            r = ModulePlacement.Evaluate("?", at, 0f, Vector2.one, Vector2.zero, ship, new List<ModulePlacement.Plate>(), otherB);
            Check($"module: 0.5 m² 겹침은 OnModule (got {r.verdict})", r.verdict == ModulePlacement.Verdict.OnModule);

            // 실내 모듈이 우주 칸을 덮으면 InSpace
            r = ModulePlacement.Evaluate("?", at, 0f, new Vector2(3f, 1f), Vector2.zero, c => c.x != 6, new List<ModulePlacement.Plate>(), none);
            Check($"module: 실내 모듈이 우주를 덮으면 InSpace (got {r.verdict})", r.verdict == ModulePlacement.Verdict.InSpace);

            // 대칭 복사: def 콜라이더 offset에 y가 있으면 배치 offset이 그만큼 메워야 상자가 거울상이 된다
            {
                var d = new Vector2(0f, 0.85f);
                Vector2 o = ModulePlacement.MirrorOffset(d, 90f, Vector2.zero);
                // 원본 rot 90: 배 좌표 offset Rotate(d,90) = (-0.85, 0). 복사본 rot -90: Rotate(d,-90) + o 가 (-0.85, 0)이어야 한다
                Vector2 copy = Ballistics.Rotate(d, -90f) + o;
                Check($"mirror: def y offset을 배치 offset이 메운다 (got {copy})", (copy - new Vector2(-0.85f, 0f)).magnitude < 1e-3f);
                Check("mirror: dy가 0이면 (x, -y)", ModulePlacement.MirrorOffset(Vector2.zero, 30f, new Vector2(0.2f, -0.2f)) == new Vector2(0.2f, 0.2f));
            }

            if (ShipBuilder.IsExteriorModule("m12"))
            {
                // 갑판 판 6장(row 6)을 각각 0.15씩 무는 3.4x3 포: 외장이라 Ok, 마운트는 가운데(5,6)
                var deck = new List<ModulePlacement.Plate>();
                for (int x = 3; x <= 8; x++) deck.Add(Rect(new Vector2Int(x, 6), Vector2.one));
                r = ModulePlacement.Evaluate("m12", at, 0f, Vector2.zero, Vector2.zero, c => c.y >= 6, deck, none);
                Check($"module: 포는 갑판을 물어도 Ok (got {r.verdict})", r.verdict == ModulePlacement.Verdict.Ok);
                Check($"module: 포 마운트는 제일 많이 덮은 판 (got {r.mount})", r.hasMount && r.mount.y == 6 && Mathf.Abs(r.mount.x - 5) <= 1);

                // 45도 경사판 옆의 포: 그 판이 마운트
                var slope = new List<ModulePlacement.Plate> { Rect(new Vector2Int(5, 7), new Vector2(1.41f, 1f), 45f) };
                r = ModulePlacement.Evaluate("m12", at, 0f, Vector2.zero, Vector2.zero, c => c.y >= 6, slope, none);
                Check($"module: 경사판 옆 포의 마운트는 그 판 (got {r.verdict} {r.mount})", r.hasMount && r.mount == new Vector2Int(5, 7));

                // 허공의 포는 NoMount
                r = ModulePlacement.Evaluate("m12", new Vector2Int(20, 20), 0f, Vector2.zero, Vector2.zero, _ => false, deck, none);
                Check($"module: 허공의 포는 NoMount (got {r.verdict})", r.verdict == ModulePlacement.Verdict.NoMount);
            }
        }

        Debug.Log($"[ShipGrid] {_pass} passed, {_fail} failed.");
    }

    /// <summary>
    /// 격자를 자식들의 '위치'에서 파생한다. 원점 중심이 아니어도, 폭이 짝수여도 맞아야 한다 -
    /// 예전 ToCell은 원점 중심을 가정해서 짝수 폭에서 정확히 0.5를 반올림했고,
    /// RoundToInt의 은행가 반올림이 거기서 한 칸을 먹었다.
    /// </summary>
    /// <summary>
    /// 링 검사. false = "절대 못 가른다"(BFS 생략), true = "몰라서 BFS에 묻는다".
    /// 그래서 false 쪽이 틀리면 배가 갈라져야 하는데 안 갈라지는 시뮬 버그고,
    /// true 쪽이 틀리면 그냥 헛BFS다 - 비대칭이라 false 케이스를 집중적으로 못 박는다.
    /// </summary>
    private static void RingRejectTest()
    {
        static HashSet<Vector2Int> Alive(params (int x, int y)[] cells)
        {
            var set = new HashSet<Vector2Int>();
            foreach ((int x, int y) c in cells)
                set.Add(new Vector2Int(c.x, c.y));
            return set;
        }

        // 일자 한가운데 - 가른다. true여야 한다.
        Check("ring: middle of a line might split",
            ShipGrid.RemovalMightSplit(Alive((0, 0), (2, 0)), new Vector2Int(1, 0)));

        // 일자 끝 - 이웃 하나뿐이라 못 가른다.
        Check("ring: end of a line cannot split",
            !ShipGrid.RemovalMightSplit(Alive((1, 0), (2, 0)), new Vector2Int(0, 0)));

        // 고립 칸 - 이웃 0개.
        Check("ring: isolated cell cannot split",
            !ShipGrid.RemovalMightSplit(Alive(), new Vector2Int(0, 0)));

        // L자 팔꿈치 - 남는 두 칸이 대각으로 이어진다. **8방향으로 세는 이유가 이 케이스다** -
        // 4방향이면 두 덩어리로 보여 매번 헛BFS를 돈다.
        Check("ring: L-corner elbow cannot split (diagonal survives)",
            !ShipGrid.RemovalMightSplit(Alive((0, 0), (1, 1)), new Vector2Int(1, 0)));

        // 계단 팔꿈치 - 남는 두 칸이 (0,0)과 (2,2), 체비쇼프 2라 링 안에서 못 잇는다.
        // 진짜로 갈라지는 케이스고 true여야 한다.
        Check("ring: staircase elbow might split",
            ShipGrid.RemovalMightSplit(Alive((0, 0), (2, 2)), new Vector2Int(1, 1)));

        // 꽉 찬 3x3의 한가운데 - 링 여덟 칸이 전부 살아 한 덩어리.
        Check("ring: centre of a full block cannot split",
            !ShipGrid.RemovalMightSplit(
                Alive((0, 0), (1, 0), (2, 0), (0, 1), (2, 1), (0, 2), (1, 2), (2, 2)),
                new Vector2Int(1, 1)));

        // 링 안에서는 두 덩어리인데 링 밖으로 돌아 이어지는 ㄷ자 - 안 갈라지지만
        // 링 검사는 모른다. true(보수적 폴백)가 맞다.
        Check("ring: U-shape falls back to BFS (conservative)",
            ShipGrid.RemovalMightSplit(
                Alive((0, 0), (2, 0), (0, 1), (2, 1), (0, 2), (1, 2), (2, 2)),
                new Vector2Int(1, 0)));
    }

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

    /// <summary>
    /// **손상은 authored frame을 안 건드린다.** 판이 빠지면 살아남은 판의 극값이 줄어들지만,
    /// 격자는 언제나 원본 설계도(basedOn)의 bbox로 잡혀야 한다.
    ///
    /// 안 그러면 저장/로드마다 배가 옆으로 밀린다. 증상이 전투가 아니라 **구역 전환에서만**
    /// 나오고, 뱃머리가 뜯긴 판에서만 나오는 탓에 원인에서 한참 떨어진 자리에서 보인다.
    /// </summary>
    private static void AuthoredFrameTest()
    {
        ShipDef design = ShipDef.Load("destroyer");

        if (design == null)
            return;   // Load가 이미 에러를 찍었다

        (ShipGrid.Map map, Vector2Int mins) full = ShipBuilder.AuthoredMap(design);

        int minCol = int.MaxValue;

        foreach (Placement p in design.placements)
            minCol = Mathf.Min(minCol, p.col);

        // 뱃머리 세 칸을 뜯는다. minCol이 움직이는 유일한 손상이라 유일하게 위험하다 -
        // 선미가 날아가면 칸 번호는 그대로고 localPosition만 밀리므로 증상이 더 약하다.
        var damaged = new ShipDef
        {
            defName = design.defName,
            basedOn = "destroyer",
            placements = new List<Placement>(),
        };

        foreach (Placement p in design.placements)
        {
            if (p.col > minCol + 2)
                damaged.placements.Add(p);
        }

        Check("authored frame: 손상 def가 판을 실제로 잃었다",
            damaged.placements.Count > 0 && damaged.placements.Count < design.placements.Count);

        (ShipGrid.Map map, Vector2Int mins) hurt = ShipBuilder.AuthoredMap(damaged);

        if (full.map == null || hurt.map == null)
        {
            Check("authored frame: 맵이 나온다", false);
            return;
        }

        Check($"authored frame: 크기가 그대로 " +
              $"({hurt.map.width}x{hurt.map.height} vs {full.map.width}x{full.map.height})",
            hurt.map.width == full.map.width && hurt.map.height == full.map.height);

        Check($"authored frame: mins가 그대로 ({hurt.mins} vs {full.mins})",
            hurt.mins == full.mins);

        // 진짜 불변식은 크기가 아니라 이것이다: 살아남은 판 하나가 손상 전후로 **같은 배
        // 좌표**에 선다. 크기와 mins가 둘 다 맞아도 여기서 틀릴 수 있다.
        Placement survivor = damaged.placements[0];

        Vector2 before = full.map.ToLocal(survivor.col - full.mins.x, survivor.row - full.mins.y);
        Vector2 after = hurt.map.ToLocal(survivor.col - hurt.mins.x, survivor.row - hurt.mins.y);

        Check($"authored frame: 살아남은 판이 안 움직인다 (Δ{(after - before).magnitude:0.###} m)",
            (after - before).sqrMagnitude < 1e-6f);

        // basedOn이 없는 것은 사고가 아니라 Hulk다. 이 가지를 지우면 운석·거울이 죽는다.
        ShipDef hulk = ShipDef.Load("asteroid");

        if (hulk != null)
            Check("authored frame: basedOn 없는 Hulk도 맵이 나온다",
                ShipBuilder.AuthoredMap(hulk).map != null);
    }

    /// <summary>
    /// 함선 그림 크기 검사. **파일도 Unity도 안 쓴다** - 순수 함수라서.
    ///
    /// 이 테스트가 지키는 것이 둘이다. 하나는 규격 자체(칸 x ppu == 픽셀), 다른 하나는
    /// **함수가 조용하다는 것**이다. 실패 케이스를 세 개 돌리는데 콘솔이 깨끗해야 한다 -
    /// 판정 안에서 로그를 찍으면 통과한 테스트와 진짜 오류가 같은 색으로 섞인다.
    /// </summary>
    private static void HullSkinSizeTest()
    {
        // destroyer는 56x18칸이다. 96 ppu면 캔버스가 5376x1728.
        Check("hull skin: 56x18칸 @96 = 5376x1728",
            ShipDef.CheckTextureSize(56, 18, 96, 5376, 1728, out _));

        Check("hull skin: 1픽셀 모자라면 거부",
            !ShipDef.CheckTextureSize(56, 18, 96, 5376, 1727, out _));

        // 축척은 인자다. 함수가 정책을 모르는지 - 48로 물으면 48로 답해야 한다.
        Check("hull skin: 다른 축척도 함수는 다룬다 (@48 = 2688x864)",
            ShipDef.CheckTextureSize(56, 18, 48, 2688, 864, out _));

        // 그래서 96 규격에 48 캔버스를 내밀면 크기에서 걸린다. 선언이 아니라 픽셀 수로.
        Check("hull skin: 96 규격에 48 캔버스는 거부",
            !ShipDef.CheckTextureSize(56, 18, 96, 2688, 864, out _));

        Check("hull skin: ppu 0은 거부", !ShipDef.CheckTextureSize(56, 18, 0, 0, 0, out _));
        Check("hull skin: 빈 격자는 거부", !ShipDef.CheckTextureSize(0, 0, 96, 0, 0, out _));

        // 실패한 쪽만 문장을 든다. 통과한 쪽이 문장을 들면 부르는 쪽이 그걸 찍는다.
        ShipDef.CheckTextureSize(56, 18, 96, 5376, 1727, out string bad);
        ShipDef.CheckTextureSize(56, 18, 96, 5376, 1728, out string good);

        Check("hull skin: 실패는 이유를 말하고 성공은 침묵한다",
            !string.IsNullOrEmpty(bad) && good == null);
    }

    /// <summary>
    /// **def만으로 찍은 격자가 지어진 배와 같은 틀을 쓰는가.** 오브젝트가 하나도 없으므로
    /// 플레이 모드가 필요 없다.
    ///
    /// 두 번째 검사가 진짜다 - 실물 칸 수와 판 배치 수가 같다는 것은 하나도 안 빠졌고
    /// 두 배치가 같은 칸을 먹지도 않았다는 뜻이다. 숫자를 손으로 적지 않고 def에서
    /// 파생하므로 배를 고쳐도 안 깨진다.
    /// </summary>
    private static void StampFromDefTest()
    {
        ShipDef design = ShipDef.Load("destroyer");

        if (design == null)
            return;   // Load가 이미 에러를 찍었다

        ShipGrid.Map map = ShipBuilder.StampFromDef(design);
        (ShipGrid.Map map, Vector2Int mins) authored = ShipBuilder.AuthoredMap(design);

        if (map == null || authored.map == null)
        {
            Check("stamp from def: 격자가 나온다", false);
            return;
        }

        Check($"stamp from def: 틀이 AuthoredMap과 같다 " +
              $"({map.width}x{map.height} vs {authored.map.width}x{authored.map.height})",
            map.width == authored.map.width && map.height == authored.map.height);

        int solid = 0, indoors = 0;

        for (int col = 0; col < map.width; col++)
        for (int row = 0; row < map.height; row++)
        {
            if (ShipGrid.Solid(map.cells[col, row]))
                solid++;
            else if (map.cells[col, row] != ShipGrid.Cell.Exterior)
                indoors++;
        }

        // 판·문만 격자에 도장을 찍는다. 모듈(포탑·엔진·원자로)은 판의 자식이라 안 센다.
        int plates = 0;

        foreach (Placement p in design.placements)
        {
            ThingDef thing = DefDatabase.Get(p.def);

            if (thing?.MainType != null &&
                (typeof(Armor).IsAssignableFrom(thing.MainType) ||
                 typeof(Door).IsAssignableFrom(thing.MainType)))
                plates++;
        }

        Check($"stamp from def: 실물 칸 {solid} == 판 배치 {plates}", solid == plates);

        // MarkExterior가 돌았고 배가 밀폐돼 있다는 뜻. 0이면 flood가 배 속까지 샜다.
        Check($"stamp from def: 실내 칸이 있다 (got {indoors})", indoors > 0);
    }

    /// <summary>
    /// **#6이 구현되기 전에 먼저 쓴 테스트다.** `SplitRear`가 아직 던지므로 여기는 빨갛게
    /// 시작한다 - 그게 정상이고, 구현이 끝나면 초록이 된다.
    ///
    /// 지키는 불변식 하나: **모든 후면 칸은 정확히 한 주인을 갖거나, 아무 주인도 없어서
    /// 사라진다.** 계획 문서 §6(3)이 "통과 불가능"이라고 경고한 그 문장인데, 고아를
    /// 삭제하기로 정해서 이제 성립한다.
    /// </summary>
    private static void RearSplitTest()
    {
        try
        {
            TwoLobesTest();
            OrphanTest();
        }
        catch (System.NotImplementedException)
        {
            Check("rear split: SplitRear가 아직 구현되지 않았다", false);
        }
    }

    /// <summary>
    /// 허리가 잘린 배. 왼쪽 조각과 오른쪽 조각이 자기 쪽 후면만 가져가야 한다.
    ///
    /// 허리 칸(col 3,4)이 요점이다 - 어느 조각에도 판이 없지만 후면은 있다. col 3은
    /// 왼쪽에서 1칸, col 4는 오른쪽에서 1칸이라 동점이 없다. 동점을 일부러 피한 것은
    /// 이 케이스가 "가까운 쪽이 가져간다"만 시험하게 하려는 것이다.
    /// </summary>
    private static void TwoLobesTest()
    {
        ShipGrid.Map map = ShipGrid.ParseMap(Lines(
            "########",
            "#..##..#",
            "########"));

        var rear = new HashSet<Vector2Int>();

        for (int col = 0; col < map.width; col++)
        for (int row = 0; row < map.height; row++)
        {
            if (map.cells[col, row] != ShipGrid.Cell.Exterior)
                rear.Add(new Vector2Int(col, row));
        }

        Check($"rear split: 이 픽스처는 후면이 24칸이다 (got {rear.Count})", rear.Count == 24);

        // 허리(col 3,4)의 판이 죽었다고 치고 조각 둘을 손으로 만든다. SplitRear는 조각을
        // 어떻게 얻었는지 모르는 순수 함수라 이렇게 넣어도 실제와 같은 입력이다.
        var left = new List<Vector2Int>();
        var right = new List<Vector2Int>();

        for (int col = 0; col < map.width; col++)
        for (int row = 0; row < map.height; row++)
        {
            if (!ShipGrid.Solid(map.cells[col, row]))
                continue;

            if (col <= 2) left.Add(new Vector2Int(col, row));
            else if (col >= 5) right.Add(new Vector2Int(col, row));
        }

        var chunks = new List<List<Vector2Int>> { left, right };
        List<HashSet<Vector2Int>> owned = ShipGrid.SplitRear(chunks, rear);

        if (owned == null || owned.Count != 2)
        {
            Check($"rear split: 조각 수만큼 돌려준다 (got {owned?.Count ?? -1})", false);
            return;
        }

        bool leftOk = true, rightOk = true;

        foreach (Vector2Int c in owned[0]) leftOk &= c.x <= 3;
        foreach (Vector2Int c in owned[1]) rightOk &= c.x >= 4;

        Check($"rear split: 왼쪽 조각은 col<=3만 (got {owned[0].Count}칸)",
            leftOk && owned[0].Count == 12);

        Check($"rear split: 오른쪽 조각은 col>=4만 (got {owned[1].Count}칸)",
            rightOk && owned[1].Count == 12);

        // 여기가 §6(3)의 문장이다. 겹치면 후면 한 칸이 두 몸에 그려지고, 빠지면 사라진다.
        var union = new HashSet<Vector2Int>(owned[0]);
        int overlap = 0;

        foreach (Vector2Int c in owned[1])
        {
            if (!union.Add(c))
                overlap++;
        }

        Check($"rear split: 두 조각이 겹치지 않는다 (겹침 {overlap}칸)", overlap == 0);
        Check($"rear split: 한 칸도 안 잃었다 ({union.Count}/{rear.Count})", union.Count == rear.Count);

        // 결정론. 같은 입력이 같은 분배를 내야 리플레이가 어긋나지 않는다.
        List<HashSet<Vector2Int>> again = ShipGrid.SplitRear(chunks, rear);

        Check("rear split: 두 번 돌려도 같다",
            again != null && again.Count == 2 &&
            again[0].SetEquals(owned[0]) && again[1].SetEquals(owned[1]));
    }

    /// <summary>
    /// 판이 한 장도 안 남은 영역의 후면은 주인이 없다. 삭제되어야 한다.
    ///
    /// 상자 둘이 우주(col 5)로 갈려 있다. 왼쪽 상자에만 조각을 주면 오른쪽 상자의 후면은
    /// 어느 소스에서도 4방향으로 닿을 수 없다 - 사이가 Exterior라 그래프에 없기 때문이다.
    /// </summary>
    private static void OrphanTest()
    {
        ShipGrid.Map map = ShipGrid.ParseMap(Lines(
            "#####.#####",
            "#...#.#...#",
            "#####.#####"));

        var rear = new HashSet<Vector2Int>();
        var left = new List<Vector2Int>();

        for (int col = 0; col < map.width; col++)
        for (int row = 0; row < map.height; row++)
        {
            var cell = new Vector2Int(col, row);

            if (map.cells[col, row] != ShipGrid.Cell.Exterior)
                rear.Add(cell);

            if (col <= 4 && ShipGrid.Solid(map.cells[col, row]))
                left.Add(cell);
        }

        Check($"rear split: 고아 픽스처의 후면은 30칸이다 (got {rear.Count})", rear.Count == 30);

        var chunks = new List<List<Vector2Int>> { left };
        List<HashSet<Vector2Int>> owned = ShipGrid.SplitRear(chunks, rear);

        if (owned == null || owned.Count != 1)
        {
            Check("rear split: 조각 하나면 집합도 하나", false);
            return;
        }

        Check($"rear split: 왼쪽 상자만 가져간다 (got {owned[0].Count}, 기대 15)",
            owned[0].Count == 15);

        bool onlyLeft = true;

        foreach (Vector2Int c in owned[0])
            onlyLeft &= c.x <= 4;

        Check("rear split: 우주 건너편은 안 가져간다", onlyLeft);
    }

    /// <summary>
    /// 뜯긴 판은 상한 채로 간다. **깎기만 하고 아무도 안 죽여야 한다** - 이 호출은 파단
    /// BFS 한가운데서 일어나므로, 서브셀을 실제로 죽이면 그 자리에서 붕괴 연쇄가 시작된다.
    /// 그래서 "비율이 맞나"와 "아무도 안 뚫렸나"를 같이 본다.
    /// </summary>
    private static void DebrisHpTest()
    {
        var go = new GameObject("debris hp self test");

        try
        {
            var box = go.AddComponent<BoxCollider2D>();
            box.size = Vector2.one;

            // AddComponent가 곧 Awake다(오브젝트가 활성이라). 콜라이더를 먼저 붙였으므로
            // Armor.Awake가 넓이를 읽고 HP를 만땅으로 채운 상태로 나온다.
            Armor plate = go.AddComponent<BallisticArmor>();

            Check($"debris hp: 태어날 때 만땅 (got {plate.HealthFraction:F3})",
                Mathf.Abs(plate.HealthFraction - 1f) < 1e-3f);

            plate.ScaleHealth(0.35f);

            Check($"debris hp: 비율만큼 깎인다 (got {plate.HealthFraction:F3}, 기대 0.350)",
                Mathf.Abs(plate.HealthFraction - 0.35f) < 1e-3f);

            bool anyBreached = false;

            for (int i = 0; i < Armor.SubCount; i++)
                anyBreached |= plate.IsBreached(i);

            Check("debris hp: 깎아도 뚫린 칸은 안 생긴다", !anyBreached);

            // 0을 주면 판이 통째로 죽는 게 아니라 값만 0이 된다 - 죽이는 것은 여전히
            // ApplyDamage의 몫이라, 여기서 붕괴가 시작되면 안 된다.
            plate.ScaleHealth(0f);

            Check($"debris hp: 0이어도 값만 놓는다 (got {plate.HealthFraction:F3})",
                plate.HealthFraction < 1e-3f && plate != null);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
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
