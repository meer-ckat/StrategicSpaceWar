using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

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
#if UNITY_EDITOR
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
#endif

    /// <summary>
    /// 남은 체력 비율. **부서진 것은 애초에 여기 안 온다** - 판은 죽으면서 GameObject째
    /// 사라지고, 그 빈 칸이 곧 뚫린 구멍이다. 여기서 재는 것은 살아남았지만 상한 것뿐이다.
    /// </summary>
    private static float HealthOf(Transform child)
    {
        if (child.TryGetComponent(out Armor plate))
            return plate.HealthFraction;

        if (child.TryGetComponent(out IDamageable module))
            return module.Health01;

        return 1f;
    }

    /// <summary>
    /// 씬의 콜라이더가 def의 기본값과 다르면 그 차이를 배치에 적는다. **같으면 0으로 둬서
    /// 배치가 조용하다** - 판 241개가 전부 자기 크기를 적고 있으면 진짜로 특별한 자리가
    /// 어디인지 안 보인다.
    ///
    /// 이것이 있어야 `Armor mk5` 하나로 경사판까지 뽑을 수 있다. 없으면 씬에서 콜라이더를
    /// 키워도 export가 그 사실을 버려서, 다시 지은 배는 전부 정사각형이 된다.
    /// </summary>
    private static void CaptureCollider(Transform child, Placement placement)
    {
        if (!child.TryGetComponent(out BoxCollider2D box))
            return;

        ThingDef def = DefDatabase.Get(placement.def);

        if (def == null)
            return;

        if (box.size != def.collider.size)
            placement.size = box.size;

        // Spawn이 칸 좌표계 -> 로컬로 돌려서 넣는다. 뽑을 때 되돌려야 왕복이 닫힌다.
        if (box.offset != def.collider.offset)
            placement.offset = Ballistics.Rotate(box.offset - def.collider.offset, placement.rot);
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

        // 선체 직속인데 판이 아닌 것 = 어느 벽에도 안 붙은 모듈. 벽이 부서져도 안 죽으므로
        // 거의 항상 저작 실수다.
        int unmounted = 0;

        foreach (Transform child in hull)
        {
            if (child == null)
                continue;

            var thing = child.GetComponent<Thing>();

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

            if (child.GetComponent<Armor>() == null && child.GetComponent<Door>() == null)
                unmounted++;

            var placement = new Placement
            {
                def = thing.defName,
                col = cell.x,
                row = cell.y,
                rot = child.localEulerAngles.z,
                hp = HealthOf(child),
            };

            CaptureCollider(child, placement);
            def.placements.Add(placement);
        }

        // 모듈은 자기가 붙을 판의 자식으로 들어가 있어야 한다. 씬에서 그렇게 놓는 것이
        // 곧 "이 벽이 부서지면 이 모듈이 죽는다"는 선언이고, 추측할 여지가 없다.
        foreach (Transform child in hull)
        {
            if (child == null || child.GetComponent<Armor>() == null)
                continue;

            Vector2Int plateCell = map.ToCell(child.localPosition);

            foreach (Transform module in child)
            {
                var thing = module.GetComponent<Thing>();

                if (thing == null || string.IsNullOrEmpty(thing.defName))
                    continue;

                Vector2Int cell = map.ToCell(hull.InverseTransformPoint(module.position));

                var placement = new Placement
                {
                    def = thing.defName,
                    col = cell.x,
                    row = cell.y,
                    rot = module.eulerAngles.z - hull.eulerAngles.z,
                    hp = HealthOf(module),
                    mountCol = plateCell.x,
                    mountRow = plateCell.y,
                };

                CaptureCollider(module, placement);
                def.placements.Add(placement);
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

        if (unmounted > 0)
            Debug.LogError(
                $"[ShipExporter] 어느 판에도 안 붙은 모듈 {unmounted}개가 선체 직속으로 있다. " +
                "붙일 판의 자식으로 끌어다 놓고 다시 뽑아라 - 지금 상태로는 벽이 부서져도 안 죽는다.");

        return def;
    }

    // 여기부터 아래는 에디터 전용이다. **클래스를 닫는 중괄호는 이 안에 두지 않는다** -
    // 클래스 몸통은 위에서 가드 밖으로 열렸으므로, 그 중괄호가 #if 안에 있으면 빌드에서
    // 클래스가 안 닫혀 CS1513이 난다. 에디터에서는 UNITY_EDITOR가 늘 켜져 있어 영영 안
    // 보이는 종류의 실패다. RunState.Save가 Export를 런타임에 부르므로 이 파일은 빌드에서도
    // 컴파일돼야 한다.
#if UNITY_EDITOR

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

#endif
}
