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
    /// 격자에 도장을 찍는 직속 자식인가. Armor와 Door만이다 - 모듈은 격자에 안 나온다.
    ///
    /// **격자를 읽는 모든 자리가 이걸 통과해야 한다.** "직속 자식이면 판"이라고 가정한 코드는
    /// 그림 오브젝트가 하나 끼어드는 순간 그걸 판으로 세고, 증상은 방 오버레이가 잔해에 실려
    /// 우주로 날아가는 것이었다.
    ///
    /// **직속 자식만 본다.** localPosition은 부모 기준이라, 판이 선체 직속이 아니면 칸 좌표가
    /// 통째로 틀린다. 모듈은 판의 자식으로 한 겹 아래 들어가고, 그래서 여기 안 걸린다 -
    /// 판이 잔해로 넘어갈 때 모듈이 딸려가는 것도 같은 이유로 공짜다.
    /// </summary>
    public static bool IsPlate(Component child) => child != null && StampsGrid(child, out _);

    /// <summary>
    /// 판이 자기 칸 중심에서 이만큼(m) 넘게 벗어나면 경고한다. 0.5면 반올림이 이웃 칸으로
    /// 넘어가는 지점이라, 그 절반 아래로 잡아 실제로 칸을 잘못 먹기 전에 잡는다.
    /// 에디터에서 드래그하다 붙는 미세한 흔들림은 이보다 훨씬 작다.
    /// </summary>
    private const float GridResidualEpsilon = 0.1f;

    /// <summary>
    /// 저장된 손상을 되돌려 놓는다. 1이면 새 배라 할 일이 없다.
    ///
    /// 판은 값을 그냥 놓는다(RestoreHealthFraction) - 피해를 다시 넣으면 서브셀이 실제로
    /// 죽어서, 전투를 살아남은 판이 로드하자마자 무너진다. 모듈은 서브셀이 없어 TakeDamage로
    /// 깎아도 같은 결과다.
    /// </summary>
    private static void Restore(Thing spawned, float hp)
    {
        if (spawned == null || hp >= 1f)
            return;

        if (spawned.TryGetComponent(out Armor plate))
        {
            plate.RestoreHealthFraction(hp);
            return;
        }

        // 0까지 내리지 않는다. 탄약고는 0에서 유폭하므로 로드하자마자 배가 터진다 -
        // 이미 터진 모듈은 애초에 배치에 없다.
        if (spawned.TryGetComponent(out IDamageable module))
            module.RestoreHealth01(Mathf.Max(0.01f, hp));
    }

    private static bool StampsGrid(Component child, out bool isDoor)
    {
        isDoor = child.GetComponent<Door>() != null;
        return isDoor || child.GetComponent<Armor>() != null;
    }

    /// <summary>
    /// 오브젝트가 없을 때의 <see cref="StampsGrid"/>. **둘은 같은 규칙이어야 한다** -
    /// 갈리는 순간 "지어진 배의 격자"와 "설계도의 격자"가 다른 물건이 되고, 후면 판이
    /// 전면과 어긋난 자리에 생긴다. 한쪽을 고치면 반드시 다른 쪽도 고쳐라.
    ///
    /// 컴포넌트 대신 <see cref="ThingDef.MainType"/>을 본다. 이름이 아니라 타입인 것이
    /// 중요하다 - `Armor`를 상속한 새 판(`BallisticArmor`)이 생겨도 저절로 따라온다.
    /// </summary>
    private static bool StampsGrid(ThingDef def, out bool isDoor)
    {
        isDoor = false;

        if (def?.MainType == null)
            return false;

        isDoor = typeof(Door).IsAssignableFrom(def.MainType);
        return isDoor || typeof(Armor).IsAssignableFrom(def.MainType);
    }

    /// <summary>
    /// 오브젝트를 하나도 만들지 않고 배치 리스트만으로 격자를 찍는다.
    ///
    /// **<see cref="Stamp"/>가 답할 수 없는 질문에 답한다.** Stamp는 살아 있는 자식
    /// 트랜스폼을 읽으므로 부서진 판이 있던 자리는 격자에서 그냥 사라진다. 후면 판은
    /// 뚫린 구멍 뒤에도 있어야 하니 "설계가 여기 뭘 뒀나"를 아는 격자가 따로 필요하다.
    ///
    /// **틀과 내용이 다른 데서 온다.** 크기·원점은 <see cref="AuthoredMap"/>이 주는
    /// authored bbox(= basedOn)이고, 칸 내용은 넘긴 def의 placements다. 그래서 손상
    /// 저장본을 넣으면 **원본 크기 격자에 구멍이 뚫린 채로** 나오고, 설계도를 넣으면
    /// 온전한 격자가 나온다. 부르는 쪽이 어느 쪽을 원하는지 def로 말한다.
    ///
    /// armorAt/doorAt은 안 준다 - 가리킬 오브젝트가 없다. 판 참조가 필요한 일
    /// (WireNeighbours, 방 경계)은 여전히 Stamp의 몫이다.
    /// </summary>
    public static ShipGrid.Map StampFromDef(ShipDef def)
    {
        (ShipGrid.Map map, Vector2Int mins) authored = AuthoredMap(def);

        if (authored.map == null)
            return null;

        foreach (Placement p in def.placements)
        {
            if (!StampsGrid(DefDatabase.Get(p.def), out bool isDoor))
                continue;

            var cell = new Vector2Int(p.col - authored.mins.x, p.row - authored.mins.y);

            // authored bbox 밖은 손상 저장본에서도 안 생긴다. 생겼다면 배치가 원본과
            // 다른 좌표계로 적힌 것이라, 조용히 건너뛰면 #1이 깨진 걸 여기서 묻는다.
            if (!authored.map.Inside(cell))
            {
                Debug.LogError(
                    $"[ShipBuilder] {def.defName}: '{p.def}'가 설계도 격자 밖 ({p.col},{p.row})에 있다.");
                continue;
            }

            authored.map.cells[cell.x, cell.y] =
                isDoor ? ShipGrid.Cell.Door : ShipGrid.Cell.Wall;
        }

        ShipGrid.MarkExterior(authored.map);
        return authored.map;
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

            // **hull.localScale을 곱하지 않는다.** localPosition은 부모의 scale과 무관하므로
            // 좌우 반전된 배도 여기서는 정방향 배와 글자 그대로 같은 값을 낸다. 격자는
            // 로컬 위상이고 반전은 월드에 그릴 때의 일이다 - 이 함수가 scale을 알면
            // 두 배가 서로 다른 방 구획을 갖게 되어 같은 설계도가 두 벌이 된다.
            Vector2 p = child.localPosition;
            float rowAxis = -p.y;   // 맵 첫 줄이 위쪽

            minX = Mathf.Min(minX, p.x); //가장 낮은 x값을 찾는 코드
            maxX = Mathf.Max(maxX, p.x); //가장 높은 x값을 찾는 코드
            minRow = Mathf.Min(minRow, rowAxis);
            maxRow = Mathf.Max(maxRow, rowAxis);
            any = true;
        }

        if (!any) //판이 하나도 없으면 격자가 없다. 주인(Ship.BuildRooms)이 로그를 남긴다.
            return null;

        var map = new ShipGrid.Map(
            Mathf.RoundToInt((maxX - minX) / ShipGrid.CellSize) + 1,
            Mathf.RoundToInt((maxRow - minRow) / ShipGrid.CellSize) + 1,
            new Vector2(minX, -minRow));

        foreach (Transform child in hull)
        {
            // 판이 아닌 직속 자식은 조용히 건너뛴다. 오류가 아니다 - IsPlate가 격자를 읽는
            // 모든 자리의 단일 관문이고, 여기가 그 관문이다.
            if (child == null || !StampsGrid(child, out bool isDoor))
                continue;

            // 1차 패스와 **같은 공간**이어야 한다. 원점을 뒤집힌 좌표로 잡고 여기서 안 뒤집힌
            // 좌표를 빼면 뺄셈 자체가 뜻을 잃는다. 둘 다 그냥 localPosition이다.
            Vector2Int cell = map.ToCell(child.localPosition);

            if (!map.Inside(cell))
                continue;   // 위 패스가 극값을 잡았으므로 여기 오면 안 된다

            // 격자의 진짜 불변식은 "원점이 정수"가 아니라 **"판끼리의 간격이 CellSize의
            // 정수배"**다. ToCell이 원점을 빼고 나눠서 반올림하므로 원점의 절대값은
            // 상쇄된다 - 배 전체가 x.5에 놓여 있어도 잘 돈다. 어긋나는 것은 잔차다.
            //
            // 반 칸 어긋난 판 하나는 RoundToInt에서 이웃 칸으로 넘어가고, 그러면 증상이
            // "판이 둘 이상 겹쳐 있다"로 나온다 - 원인과 다른 말이라 한참 헤맨다. 밀려난
            // 판은 armorAt에서 빠져 방 경계에도 Neighbours에도 안 들어간다. 조용히
            // 시뮬레이션 밖으로 나가는 것이 제일 나쁘다.
            //
            // JSON 경로는 정수만 쓰므로 안전하다. 이 검사는 씬에서 손으로 놓고 export 하는
            // 저작 경로를 위한 것이다.
            Vector2 residual = (Vector2)child.localPosition - map.ToLocal(cell.x, cell.y);

            if (residual.sqrMagnitude > GridResidualEpsilon * GridResidualEpsilon)
                Debug.LogWarning(
                    $"[ShipBuilder] '{child.name}'이 격자에서 {residual.magnitude:0.00} m " +
                    $"벗어나 있다({cell}로 반올림됨). 판 간격은 {ShipGrid.CellSize} m의 " +
                    "정수배여야 한다.", child);

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

    public static (ShipGrid.Map map, Vector2Int mins) AuthoredMap(ShipDef def, Transform hull = null)
    {
        if (def == null || def.placements == null || def.placements.Count == 0)
        {
            Debug.LogError("You bastard");
            return (null, default);
        }

        if(hull != null) //좆까 내맘대로 할거임
        {
            if(hull.childCount <= 0)
                return (null, default); //개같은
        }

        // basedOn이 비는 것은 사고가 아니라 Hulk다(운석·거울·폐위성). 손상 이력이 없으니
        // 자기 placements가 곧 authored고, 폴백이 곧 정답이다.
        ShipDef authored = string.IsNullOrEmpty(def.basedOn) ? def : ShipDef.Load(def.basedOn) ?? def;

        RectInt box = authored.Bbox();

        return (ShipGrid.Map.Centred(box.width, box.height), box.min);
    }

    /// <summary>
    /// 이미 읽어 둔 def로 짓는다. 저장된 런처럼 파일 이름으로 못 찾는 def가 있어서 갈랐다.
    /// </summary>
    public static bool SpawnFrom(Transform hull, ShipDef def, Component pourInto)
    {
        if (def == null)
            return false;

        if (pourInto != null)
            def.Apply(pourInto);

        Spawn(hull, def);
        return true;
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

        var temp = AuthoredMap(def);
        if(temp.map == null)
        {
            Debug.LogAssertion($"Fucking Error. Call Opus. hull:{hull.name}");
            return;
        }
        // Destroy는 프레임 끝까지 미뤄진다. 그 사이에 Stamp가 돌면 옛 자식과 새 자식을
        // 함께 읽어 칸이 겹친다. 여기서는 즉시 지워야 한다.
        for (int i = hull.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(hull.GetChild(i).gameObject);

        ShipGrid.Map map = temp.map;
        int minCol = temp.mins.x;
        int minRow = temp.mins.y;

        var plateAt = new Dictionary<Vector2Int, Transform>();
        var modules = new List<(Placement placement, Transform spawned)>();
        int missing = 0;

        foreach (Placement p in def.placements)
        {
            var cell = new Vector2Int(p.col - minCol, p.row - minRow);

            // 자리와 각도를 스폰에 같이 넘긴다. def로 지은 물건은 전부 붙이고 자리를 잡은
            // 뒤에 활성화돼야 해서, 밖에서 나중에 옮기면 Awake가 이미 지나간 뒤가 된다.
            // 자리는 격자가 정하고, 콜라이더는 배치가 덧쓸 수 있다. **오브젝트 위치에는
            // p.offset을 안 더한다** - localPosition이 곧 칸 번호라 그걸 밀면 Stamp가 다른
            // 칸을 읽는다. 미는 것은 콜라이더 offset뿐이다 (Placement.offset 참고).
            Thing spawned = DefDatabase.Spawn(
                p.def, hull, map.ToLocal(cell.x, cell.y), p.rot, p.size, p.offset);

            if (spawned == null)
            {
                Debug.LogError($"[ShipBuilder] '{def.defName}'이 부르는 defName '{p.def}'이 없다.");
                missing++;
                continue;
            }

            // **활성화가 끝난 뒤에 바른다.** ThingDef.Spawn이 오브젝트를 켜면서 Awake가
            // 돌고, Armor.Awake는 서브셀을 전부 만땅으로 초기화한다. 그 전에 넣으면 지워진다.
            Restore(spawned, p.hp);

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
