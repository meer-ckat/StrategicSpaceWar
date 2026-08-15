using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 배치 리스트 <-> 자식 오브젝트. 격자와 씬 사이의 유일한 통로다.
///
/// 양방향이 한 파일에 있는 이유: 심는 규칙과 읽는 규칙이 어긋나면 export/import 왕복이
/// 닫히지 않는데, 증상은 "저장했다 열었더니 방이 하나 늘었다" 같은 것이라 원인을 못 찾는다.
/// </summary>
public static class ShipBuilder
{
    /// <summary>
    /// 격자에 도장을 찍는 직속 자식. Armor와 Door만이다 - 모듈은 격자에 안 나온다.
    ///
    /// **직속 자식만 본다.** localPosition은 부모 기준이라, 판이 선체 직속이 아니면 칸 좌표가
    /// 통째로 틀린다. 모듈은 판의 자식으로 한 겹 아래 들어가고, 그래서 여기 안 걸린다 -
    /// 판이 잔해로 넘어갈 때 모듈이 딸려가는 것도 같은 이유로 공짜다.
    /// </summary>
    private static bool StampsGrid(Component child, out bool isDoor)
    {
        isDoor = child.GetComponent<Door>() != null;
        return isDoor || child.GetComponent<Armor>() != null;
    }

    /// <summary>
    /// 자식들의 위치에서 맵을 짓는다. 콜라이더는 절대 안 읽는다 - 2x2 경사장갑이 이웃 칸을
    /// 덮고 있어도 격자는 판의 위치 하나만 본다.
    ///
    /// 판이 하나도 없으면 null.
    /// </summary>
    public static ShipGrid.Map Stamp(
        Transform hull,
        Dictionary<Vector2Int, Armor> armorAt,
        Dictionary<Vector2Int, Door> doorAt)
    {
        armorAt.Clear();
        doorAt.Clear();

        float minX = float.MaxValue, maxX = float.MinValue;
        float minRow = float.MaxValue, maxRow = float.MinValue;
        bool any = false;

        foreach (Transform child in hull)
        {
            if (child == null || !StampsGrid(child, out _))
                continue;

            Vector2 p = child.localPosition;
            float rowAxis = -p.y;   // 맵 첫 줄이 위쪽

            minX = Mathf.Min(minX, p.x);
            maxX = Mathf.Max(maxX, p.x);
            minRow = Mathf.Min(minRow, rowAxis);
            maxRow = Mathf.Max(maxRow, rowAxis);
            any = true;
        }

        if (!any)
            return null;

        // 차이만 반올림한다. 위치 자체를 반올림하면 짝수 폭에서 정확히 0.5가 나오고,
        // RoundToInt는 거기서 은행가 반올림(0.5 -> 0, 1.5 -> 2)을 해서 한 칸이 어긋난다.
        var map = new ShipGrid.Map(
            Mathf.RoundToInt((maxX - minX) / ShipGrid.CellSize) + 1,
            Mathf.RoundToInt((maxRow - minRow) / ShipGrid.CellSize) + 1,
            new Vector2(minX, -minRow));

        foreach (Transform child in hull)
        {
            if (child == null || !StampsGrid(child, out bool isDoor))
                continue;

            Vector2Int cell = map.ToCell(child.localPosition);

            if (!map.Inside(cell))
                continue;   // 위 패스가 극값을 잡았으므로 여기 오면 안 된다

            if (map.cells[cell.x, cell.y] != ShipGrid.Cell.Unset)
                Debug.LogWarning(
                    $"[ShipBuilder] {cell}에 판이 둘 이상 겹쳐 있다. 뒤에 오는 것이 이긴다 - " +
                    $"'{child.name}'.", child);

            if (isDoor)
            {
                map.cells[cell.x, cell.y] = ShipGrid.Cell.Door;
                doorAt[cell] = child.GetComponent<Door>();
            }
            else
            {
                map.cells[cell.x, cell.y] = ShipGrid.Cell.Wall;
                armorAt[cell] = child.GetComponent<Armor>();
            }
        }

        ShipGrid.MarkExterior(map);
        WireNeighbours(armorAt, doorAt);
        return map;
    }

    // 구조는 대각선으로도 붙어 있다. 선체 연결성 BFS와 같은 8방향이어야 "붙어 있다"가
    // 한 가지 뜻만 갖는다.
    private static readonly Vector2Int[] Around =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    /// <summary>
    /// 판마다 8방향 이웃 목록을 심는다. 격자를 아는 자리가 여기뿐이라 여기서 한다 -
    /// 이게 없으면 충각 충격 전도 같은 것이 이웃을 찾으려고 매번 물리 질의를 돌린다.
    ///
    /// 문도 격자에서는 실물이라 충격이 통과한다. armorAt에는 안 들어가 있어서(방 경계 목록이
    /// 문을 판으로 세면 안 된다) 여기서만 합쳐 본다.
    /// </summary>
    private static void WireNeighbours(
        Dictionary<Vector2Int, Armor> armorAt,
        Dictionary<Vector2Int, Door> doorAt)
    {
        var plateAt = new Dictionary<Vector2Int, Armor>(armorAt);

        foreach (KeyValuePair<Vector2Int, Door> pair in doorAt)
        {
            if (pair.Value != null && pair.Value.TryGetComponent(out Armor hatch))
                plateAt[pair.Key] = hatch;
        }

        var buffer = new List<Armor>(Around.Length);

        foreach (KeyValuePair<Vector2Int, Armor> pair in plateAt)
        {
            if (pair.Value == null)
                continue;

            buffer.Clear();

            foreach (Vector2Int dir in Around)
            {
                if (plateAt.TryGetValue(pair.Key + dir, out Armor neighbour) && neighbour != null)
                    buffer.Add(neighbour);
            }

            pair.Value.Neighbours = buffer.ToArray();
        }
    }

    /// <summary>
    /// 이름으로 설계도를 읽어 그대로 심는다. 함선과 Hulk가 같은 문으로 들어오게 하는 자리 -
    /// 두 곳에 적어두면 언젠가 한쪽만 고친다. 이름이 비어 있으면 아무것도 안 한다(씬 저작 모드).
    /// </summary>
    public static bool SpawnFrom(Transform hull, string shipDefName, Component pourInto)
    {
        if (string.IsNullOrEmpty(shipDefName))
            return false;

        ShipDef def = ShipDef.Load(shipDefName);

        if (def == null)
            return false;

        // 수치를 먼저 붓고 자식을 심는다. 뒤집히면 배가 인스펙터 기본값으로 한 틱을 산다.
        def.Apply(pourInto);
        Spawn(hull, def);
        return true;
    }

    /// <summary>
    /// 배치 리스트대로 자식을 심는다. 판을 먼저 다 심고 모듈을 나중에 붙인다 - 모듈이
    /// 자기 판을 찾으려면 판이 이미 있어야 한다.
    /// </summary>
    public static void Spawn(Transform hull, ShipDef def)
    {
        // 빈 리스트를 그냥 통과시키면 아래 min/max가 int.MaxValue인 채로 빼기에 들어가 뒤집힌다.
        if (def == null || def.placements == null || def.placements.Count == 0)
        {
            Debug.LogError($"[ShipBuilder] {hull.name}에 심을 배치가 없다. 자식을 그대로 둔다.");
            return;
        }

        // Destroy는 프레임 끝까지 미뤄진다. 그 사이에 Stamp가 돌면 옛 자식과 새 자식을
        // 함께 읽어 칸이 겹친다. 여기서는 즉시 지워야 한다.
        for (int i = hull.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(hull.GetChild(i).gameObject);

        int minCol = int.MaxValue, maxCol = int.MinValue;
        int minRow = int.MaxValue, maxRow = int.MinValue;

        foreach (Placement p in def.placements)
        {
            minCol = Mathf.Min(minCol, p.col);
            maxCol = Mathf.Max(maxCol, p.col);
            minRow = Mathf.Min(minRow, p.row);
            maxRow = Mathf.Max(maxRow, p.row);
        }

        ShipGrid.Map map = ShipGrid.Map.Centred(maxCol - minCol + 1, maxRow - minRow + 1);

        var plateAt = new Dictionary<Vector2Int, Transform>();
        var modules = new List<(Placement placement, Transform spawned)>();
        int missing = 0;

        foreach (Placement p in def.placements)
        {
            var cell = new Vector2Int(p.col - minCol, p.row - minRow);

            // 자리와 각도를 스폰에 같이 넘긴다. def로 지은 물건은 전부 붙이고 자리를 잡은
            // 뒤에 활성화돼야 해서, 밖에서 나중에 옮기면 Awake가 이미 지나간 뒤가 된다.
            Thing spawned = DefDatabase.Spawn(
                p.def, hull, map.ToLocal(cell.x, cell.y), p.rot);

            if (spawned == null)
            {
                Debug.LogError($"[ShipBuilder] '{def.defName}'이 부르는 defName '{p.def}'이 없다.");
                missing++;
                continue;
            }

            if (StampsGrid(spawned, out _))
                plateAt[cell] = spawned.transform;
            else
                modules.Add((p, spawned.transform));
        }

        // 심은 것을 그대로 들고 온다. 위치로 다시 찾으면 판 위에 올라앉은 모듈이 자기 판을
        // 자기 자신으로 착각하고 스스로의 부모가 된다.
        foreach ((Placement p, Transform module) in modules)
        {
            if (!p.IsMounted)
                continue;

            var mount = new Vector2Int(p.mountCol - minCol, p.mountRow - minRow);

            if (!plateAt.TryGetValue(mount, out Transform plate))
            {
                Debug.LogWarning(
                    $"[ShipBuilder] '{p.def}'이 ({p.mountCol},{p.mountRow})의 판에 붙는다고 하는데 거기 판이 없다. " +
                    "선체 직속으로 둔다 - 이 모듈은 벽이 부서져도 안 죽는다.");
                continue;
            }

            // 판 밑으로 한 겹 내려간다. 판이 죽으면 같이 죽고, 판이 잔해로 떨어져 나가면
            // 같이 날아간다 - 둘 다 별도 코드 없이 부모 자식 관계 하나로 나온다.
            module.SetParent(plate, worldPositionStays: true);
        }

        if (missing > 0)
            Debug.LogError($"[ShipBuilder] '{def.defName}'에서 {missing}개를 심지 못했다.");
    }
}
