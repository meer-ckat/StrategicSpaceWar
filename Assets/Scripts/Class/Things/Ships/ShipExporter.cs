#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 씬에 손으로 지은 배 -> StreamingAssets/Ships/&lt;defName&gt;.json.
/// Tools > Ship > Export Selected Ship To Json.
///
/// 저작 도구는 씬이고 JSON은 그 결과물이다. 반대 방향(JSON -> 씬)은 런타임의
/// <see cref="ShipBuilder.Spawn"/>이 한다 - 두 방향이 같은 <see cref="ShipBuilder.Stamp"/>
/// 규칙을 쓰기 때문에 왕복이 닫힌다.
/// </summary>
public static class ShipExporter
{
    [MenuItem("Tools/Ship/Export Selected Ship To Json")]
    private static void ExportSelected()
    {
        var ship = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<Ship>()
            : null;

        if (ship == null)
        {
            Debug.LogError("[ShipExporter] Ship이 붙은 오브젝트를 하나 골라라.");
            return;
        }

        // 오브젝트 이름은 파일 이름이 되기에 나쁘다 - Unity가 복제할 때 붙이는 " (1)"이
        // 그대로 defName이 되고, 다음에 복제하면 또 다른 배가 된다.
        string defName = !string.IsNullOrEmpty(ship.shipDefName)
            ? ship.shipDefName
            : Sanitise(ship.name);

        ShipDef def = Export(ship.transform, defName);

        if (def == null)
            return;

        def.Save();
        AssetDatabase.Refresh();

        Debug.Log($"[ShipExporter] {def.placements.Count}개를 {ShipDef.PathOf(def.defName)}에 썼다. " +
                  $"Ship의 shipDefName에 '{def.defName}'을 넣으면 이걸로 짓는다.");

        Verify(ship.transform, def);
    }

    /// <summary>
    /// 자식들을 배치 리스트로. 판은 격자 칸으로, 모듈은 자기가 붙을 판과 함께.
    /// </summary>
    public static ShipDef Export(Transform hull, string defName)
    {
        var armorAt = new Dictionary<Vector2Int, Armor>();
        var doorAt = new Dictionary<Vector2Int, Door>();

        ShipGrid.Map map = ShipBuilder.Stamp(hull, armorAt, doorAt);

        if (map == null)
        {
            Debug.LogError($"[ShipExporter] {hull.name}에 판이 하나도 없다.");
            return null;
        }

        var def = new ShipDef { defName = defName };
        int nameless = 0;

        // 판이 있는 칸 목록과, 배치별로 그게 판인지 여부. 칸으로만 판별하면 판 위에 올라앉은
        // 모듈이 자기를 판으로 착각해서 영영 마운트를 못 받는다.
        var plateCells = new List<Vector2Int>();
        var isPlate = new List<bool>();

        foreach (Transform child in hull)
        {
            if (child == null)
                continue;

            Thing thing = DefDatabase.NameOf(child.gameObject);

            if (thing == null)
                continue;

            if (string.IsNullOrEmpty(thing.defName))
            {
                // 프리팹 이름에서 추측하지 않는다. 조용히 틀린 이름을 써 두면 다음에 로드할 때
                // 엉뚱한 물건이 나오거나, 더 나쁘게는 비슷한 이름의 물건이 나온다.
                Debug.LogError($"[ShipExporter] '{child.name}'에 defName이 없다. 건너뛴다.", child);
                nameless++;
                continue;
            }

            Vector2Int cell = map.ToCell(child.localPosition);
            bool plate = child.GetComponent<Armor>() != null || child.GetComponent<Door>() != null;

            if (plate)
                plateCells.Add(cell);

            isPlate.Add(plate);

            def.placements.Add(new Placement
            {
                def = thing.defName,
                col = cell.x,
                row = cell.y,
                rot = child.localEulerAngles.z,
            });
        }

        // 모듈의 마운트를 정한다. 이미 판의 자식으로 들어가 있으면 그 판이 답이고,
        // 선체 직속이면 가장 가까운 판을 골라 붙인다 - 텍스트 맵 시절의 배는 엔진이
        // 방 한가운데 떠 있어서 손으로 옮기지 않으면 하나도 안 붙는다.
        int autoMounted = 0, unmounted = 0;

        for (int i = 0; i < def.placements.Count; i++)
        {
            if (isPlate[i])
                continue;

            Placement p = def.placements[i];
            Vector2Int mount = Nearest(p.Cell, plateCells);

            if (mount.x < 0)
            {
                unmounted++;
                continue;
            }

            p.mountCol = mount.x;
            p.mountRow = mount.y;
            autoMounted++;
        }

        // 판의 자식으로 이미 들어가 있는 모듈. 손으로 붙였든 이전 로드가 붙였든 그 뜻이 이긴다.
        foreach (Transform child in hull)
        {
            if (child == null || child.GetComponent<Armor>() == null)
                continue;

            Vector2Int plateCell = map.ToCell(child.localPosition);

            foreach (Transform module in child)
            {
                Thing thing = DefDatabase.NameOf(module.gameObject);

                if (thing == null || string.IsNullOrEmpty(thing.defName))
                    continue;

                Vector2Int cell = map.ToCell(hull.InverseTransformPoint(module.position));

                def.placements.Add(new Placement
                {
                    def = thing.defName,
                    col = cell.x,
                    row = cell.y,
                    rot = module.eulerAngles.z - hull.eulerAngles.z,
                    mountCol = plateCell.x,
                    mountRow = plateCell.y,
                });
            }
        }

        if (nameless > 0)
            Debug.LogError($"[ShipExporter] defName이 없어 {nameless}개를 뺐다. 배가 그만큼 비어서 나온다.");

        // 왕복 검증은 격자만 비교한다 - 모듈은 칸에 도장을 안 찍으니 이름이 틀려도 통과한다.
        // 그 구멍을 여기서 막는다. 뽑을 때 실패해야 나중에 배를 지을 때 조용히 안 비어 있다.
        var unknown = new HashSet<string>();

        foreach (Placement p in def.placements)
        {
            if (!DefDatabase.Has(p.def))
                unknown.Add(p.def);
        }

        if (unknown.Count > 0)
            Debug.LogError(
                $"[ShipExporter] StreamingAssets/{DefDatabase.DefFolder}에 없는 defName: " +
                $"{string.Join(", ", unknown)}. 씬에는 있지만 def가 아니다 - " +
                "그대로 두면 JSON으로 지은 배에서 그 자리가 빈다.");

        if (autoMounted > 0)
            Debug.LogWarning(
                $"[ShipExporter] 모듈 {autoMounted}개를 가장 가까운 판에 자동으로 붙였다. " +
                "의도한 벽이 아니면 씬에서 옮기고 다시 뽑아라 - 그 벽이 부서지면 모듈이 죽는다.");

        if (unmounted > 0)
            Debug.LogError($"[ShipExporter] 모듈 {unmounted}개가 붙을 판을 못 찾았다.");

        return def;
    }

    /// <summary>파일 이름이 될 수 있게. 공백은 밑줄, 나머지 특수문자는 버린다.</summary>
    private static string Sanitise(string name)
    {
        var buffer = new System.Text.StringBuilder(name.Length);

        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                buffer.Append(char.ToLowerInvariant(c));
            else if (buffer.Length > 0 && buffer[buffer.Length - 1] != '_')
                buffer.Append('_');
        }

        return buffer.ToString().Trim('_');
    }

    /// <summary>
    /// 가장 가까운 판. 거리가 같으면 (row, col)이 작은 쪽이 이긴다 - export를 두 번 돌려
    /// 다른 파일이 나오면 왕복 검증이 의미를 잃는다.
    /// </summary>
    private static Vector2Int Nearest(Vector2Int from, List<Vector2Int> plates)
    {
        var best = new Vector2Int(-1, -1);
        int bestSqr = int.MaxValue;

        foreach (Vector2Int cell in plates)
        {
            int dx = cell.x - from.x;
            int dy = cell.y - from.y;
            int sqr = dx * dx + dy * dy;

            bool better = sqr < bestSqr
                || (sqr == bestSqr && (cell.y < best.y || (cell.y == best.y && cell.x < best.x)));

            if (!better)
                continue;

            bestSqr = sqr;
            best = cell;
        }

        return best;
    }

    /// <summary>
    /// 왕복 검증. 뽑은 JSON을 임시 오브젝트에 다시 심고 격자를 칸 단위로 비교한다.
    /// 이게 통과해야 "씬이 원본"에서 "JSON이 원본"으로 넘어가도 배가 그대로다.
    /// </summary>
    private static void Verify(Transform hull, ShipDef def)
    {
        var armorAt = new Dictionary<Vector2Int, Armor>();
        var doorAt = new Dictionary<Vector2Int, Door>();

        ShipGrid.Map before = ShipBuilder.Stamp(hull, armorAt, doorAt);

        var probe = new GameObject("ship export probe");
        probe.SetActive(false);   // Awake/OnEnable이 TickManager를 깨우지 않게

        try
        {
            ShipBuilder.Spawn(probe.transform, def);

            ShipGrid.Map after = ShipBuilder.Stamp(probe.transform, armorAt, doorAt);

            if (after == null || before.width != after.width || before.height != after.height)
            {
                Debug.LogError(
                    $"[ShipExporter] 왕복 실패: 맵 크기가 {before.width}x{before.height} -> " +
                    $"{(after == null ? "없음" : $"{after.width}x{after.height}")}");
                return;
            }

            for (int row = 0; row < before.height; row++)
            for (int col = 0; col < before.width; col++)
            {
                if (before.cells[col, row] == after.cells[col, row])
                    continue;

                Debug.LogError(
                    $"[ShipExporter] 왕복 실패: ({col},{row})이 " +
                    $"{before.cells[col, row]} -> {after.cells[col, row]}");
                return;
            }

            Debug.Log($"[ShipExporter] 왕복 통과. {before.width}x{before.height} 격자가 칸 단위로 같다.");
        }
        finally
        {
            Object.DestroyImmediate(probe);
        }
    }
}
#endif
