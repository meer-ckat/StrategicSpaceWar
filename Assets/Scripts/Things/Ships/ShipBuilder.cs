using System.Collections;
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
    /// 모듈이 설 수 있는 칸 = 후면이 있는 칸(판이거나 실내). 모듈은 벽에 붙는 것이 아니라
    /// 후면에 얹히는 것이라 방 한가운데도 된다. 우주에 뜬 자리만 안 된다.
    /// </summary>
    public static bool OnRear(ShipGrid.Map map, Vector2Int cell)
        => map != null && map.Inside(cell) && HullStructure.RearWorthyAt(map, cell.x, cell.y);

    /// <summary>
    /// 우주에 면해도 되는 것 - 포·발사대·엔진, 그리고 격납고(배가 나갈 문이 우주 쪽이다).
    /// 발자국에 우주 칸이 섞여도 되고, 대신 옆에 붙을 판이 있어야 한다. 나머지(원자로·
    /// 탄약고·탱크)는 실내 칸에만 선다.
    /// </summary>
    public static bool IsExteriorModule(string defName)
    {
        System.Type t = DefDatabase.Get(defName)?.MainType;
        return t != null
            && (typeof(Gun).IsAssignableFrom(t) || typeof(Engine).IsAssignableFrom(t) || typeof(Hangar).IsAssignableFrom(t));
    }

    /// <summary>
    /// 배치가 덮어쓴 크기·offset을 def와 합쳐 배 좌표의 상자로. Placement.offset은 칸 좌표라
    /// 그대로, def의 collider.offset은 판 로컬이라 rot으로 돌려 더한다 (ThingDef.Spawn의 역).
    /// </summary>
    public static void ModuleBox(
        string defName, float rot, Vector2 placementSize, Vector2 placementOffset,
        out Vector2 size, out Vector2 offset)
    {
        ThingDef def = DefDatabase.Get(defName);
        Vector2 defSize = def?.collider != null ? def.collider.size : Vector2.one;
        Vector2 defOffset = def?.collider != null ? def.collider.offset : Vector2.zero;

        size = placementSize.x > 0f && placementSize.y > 0f ? placementSize : defSize;
        offset = Ballistics.Rotate(defOffset, rot) + placementOffset;
    }

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

    private static bool StampsGrid(Component child, out ShipGrid.Cell cel)
    {
        // Door를 먼저 본다. Ballistic Door는 thingClass가 BallisticArmor고 Door는 comps라,
        // Armor를 먼저 물으면 문이 전부 벽으로 찍히고 doorAt이 빈 채로 나간다.
        if (child.TryGetComponent<Door>(out _))
        {
            cel = ShipGrid.Cell.Door;
            return true;
        }

        if (child.TryGetComponent(out Armor plate))
        {
            cel = plate.sealsRoom ? ShipGrid.Cell.Wall : ShipGrid.Cell.Vent;
            return true;
        }

        // 발자국이 있는 모듈(격납고 같은 것). **Vent다** - 실물이라 구조로 이어지고
        // 쏘면 끊기지만 방은 안 만든다. 개방형 베이의 정의가 그대로 이 칸 종류다.
        if (child.TryGetComponent(out Thing thing) && HasFootprint(thing.gridSize))
        {
            cel = ShipGrid.Cell.Vent;
            return true;
        }

        // Empty가 아니라 Unset이다. Empty는 "공기 있는 실내"라는 뜻이 이미 있어서,
        // 다음에 누가 bool을 안 보고 cel만 읽으면 조용히 틀린 답을 얻는다.
        cel = ShipGrid.Cell.Unset;
        return false;
    }

    private static bool HasFootprint(Vector2Int size) => size.x > 0 && size.y > 0;

    /// <summary>
    /// 이 자식이 격자에서 차지하는 칸 수. 판·문은 언제나 1칸이다 - 그 규칙이 격자
    /// 전체의 전제라 gridSize를 적어도 무시한다.
    /// </summary>
    private static Vector2Int FootprintOf(Transform child)
        => child.TryGetComponent<Armor>(out _) || child.TryGetComponent<Door>(out _)
            ? Vector2Int.one
            : child.TryGetComponent(out Thing thing) && HasFootprint(thing.gridSize)
                ? thing.gridSize
                : Vector2Int.one;

    private static Vector2Int FootprintOf(ThingDef def)
        => def != null && def.MainType != null
           && !typeof(Armor).IsAssignableFrom(def.MainType)
           && !typeof(Door).IsAssignableFrom(def.MainType)
           && HasFootprint(def.gridSize)
            ? def.gridSize
            : Vector2Int.one;

    /// <summary>
    /// 앵커 칸에서 발자국이 덮는 칸들. **대칭으로 퍼지고 짝수는 +쪽으로 한 칸 치우친다** -
    /// 오브젝트 원점이 그림·콜라이더의 중심이라 발자국도 그 둘레여야 눈과 격자가 맞는다.
    /// 좌상단 기준으로 하면 4칸짜리가 그림에서 두 칸 왼쪽으로 밀려 보인다.
    /// </summary>
    private static void Footprint(Vector2Int anchor, Vector2Int size, System.Action<Vector2Int> each)
    {
        int x0 = anchor.x - (size.x - 1) / 2;
        int y0 = anchor.y - (size.y - 1) / 2;

        for (int dx = 0; dx < size.x; dx++)
        for (int dy = 0; dy < size.y; dy++)
            each(new Vector2Int(x0 + dx, y0 + dy));
    }

    /// <summary>
    /// 오브젝트가 없을 때의 <see cref="StampsGrid"/>. **둘은 같은 규칙이어야 한다** -
    /// 갈리는 순간 "지어진 배의 격자"와 "설계도의 격자"가 다른 물건이 되고, 후면 판이
    /// 전면과 어긋난 자리에 생긴다. 한쪽을 고치면 반드시 다른 쪽도 고쳐라.
    ///
    /// 컴포넌트 대신 <see cref="ThingDef.MainType"/>을 본다. 이름이 아니라 타입인 것이
    /// 중요하다 - `Armor`를 상속한 새 판(`BallisticArmor`)이 생겨도 저절로 따라온다.
    /// </summary>
    public static bool StampsGrid(ThingDef def, out ShipGrid.Cell cel)
    {
        cel = ShipGrid.Cell.Unset;

        if (def?.MainType == null)
            return false;

        if (typeof(Door).IsAssignableFrom(def.MainType))
        {
            cel = ShipGrid.Cell.Door;
            return true;
        }

        if (typeof(Armor).IsAssignableFrom(def.MainType))
        {
            cel = def.sealsRoom ? ShipGrid.Cell.Wall : ShipGrid.Cell.Vent;
            return true;
        }

        // 컴포넌트 경로와 같은 규칙 - 발자국이 있는 모듈은 Vent로 찍는다.
        if (HasFootprint(def.gridSize))
        {
            cel = ShipGrid.Cell.Vent;
            return true;
        }

        return false;
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
            if (!StampsGrid(DefDatabase.Get(p.def), out ShipGrid.Cell cel))
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

            ShipGrid.Map target = authored.map;

            // Stamp와 **같은 발자국 규칙**이어야 한다. 갈리면 후면이 전면과 어긋난
            // 자리에 생긴다 - 이 두 함수가 갈리지 말라는 경고가 위에 이미 있다.
            Footprint(cell, FootprintOf(DefDatabase.Get(p.def)), at =>
            {
                if (target.Inside(at))
                    target.cells[at.x, at.y] = cel;
            });
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

            // 발자국이 큰 모듈은 원점만 재면 맵 밖으로 삐져나간다. 2차 패스가
            // Inside에서 조용히 걸러내서, 증상이 "격납고 절반이 격자에 없다"가 된다.
            Vector2Int size = FootprintOf(child);
            float halfX = (size.x - 1) * 0.5f * ShipGrid.CellSize;
            float halfRow = (size.y - 1) * 0.5f * ShipGrid.CellSize;

            minX = Mathf.Min(minX, p.x - halfX);
            maxX = Mathf.Max(maxX, p.x + halfX);
            minRow = Mathf.Min(minRow, rowAxis - halfRow);
            maxRow = Mathf.Max(maxRow, rowAxis + halfRow);
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
            if (child == null || !StampsGrid(child, out ShipGrid.Cell cel))
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

            Door door = child.GetComponent<Door>();
            Armor armor = child.GetComponent<Armor>();
            Transform owner = child;

            Footprint(cell, FootprintOf(child), at =>
            {
                if (!map.Inside(at))
                    return;

                if (map.cells[at.x, at.y] != ShipGrid.Cell.Unset)
                    Debug.LogWarning(
                        $"[ShipBuilder] {at}에 판이 둘 이상 겹쳐 있다. 뒤에 오는 것이 이긴다 - " +
                        $"'{owner.name}'.", owner);

                map.cells[at.x, at.y] = cel;

                // 판·문만 목록에 담는다. 발자국 모듈(격납고)은 Armor가 없어서 여기 안
                // 들어가고, 그래서 방 벽에도 Neighbours에도 안 낀다 - 개방형이라 맞다.
                if (door != null)
                    doorAt[at] = door;
                else if (armor != null)
                    armorAt[at] = armor;
            });
        }

        ShipGrid.MarkExterior(map);
        WireNeighbours(armorAt, doorAt, map);
        return map;
    }

    /// <summary>
    /// 선체 직속 자식으로 남아 있는 모듈을 자기 발밑 판의 자식으로 내린다.
    ///
    /// **<see cref="Stamp"/> 안에서 부르지 않는다.** Stamp는 순수 질의라 export와
    /// self-test도 부르는데, 거기서 계층을 바꾸면 저작 중인 씬이 조용히 변한다.
    /// 부르는 자리는 런타임 조립 경로 하나뿐이다.
    ///
    /// **JSON 경로에는 이미 있던 규칙이고, 씬 경로에만 없었다.** <see cref="Spawn"/>은
    /// <c>mountCol/mountRow</c>를 보고 판 밑으로 넣는데, 씬에서 손으로 지은 배
    /// (<c>shipDefName</c>이 빈 배 = export 원본)는 그 단계를 안 거쳐서 포탑이 선체 직속
    /// 자식으로 남는다. 그러면 **그 모듈은 불사가 된다** - 판이 죽어도 안 죽고, 조각이
    /// 잔해로 떠나도 안 따라가고, <see cref="Ship.StillAboard"/>는 선체 직속 자식도
    /// "이 배의 것"으로 세므로 계속 쏘고 IsCombatEffective에도 잡혀서 전투가 안 끝난다.
    /// 증상은 "부서진 배에서 멀리 떨어진 포탑 하나가 혼자 쏘고 있다"다.
    ///
    /// CLAUDE.md 불변식이 이미 말하던 것("판이 아닌 것은 선체 직속 자식이 되면 안 된다")을
    /// 씬 경로에서도 강제하는 자리다. 격자를 다 찍은 **뒤에** 도는 것이 요점 - 그 전에
    /// 옮기면 <c>foreach (Transform child in hull)</c> 순회 도중에 계층이 바뀐다.
    ///
    /// 발밑에 판이 없어도 **후면 위(실내)면 산다** - 그 칸의 후면이 죽으면
    /// HullStructure.KillRear가 죽이고 조각으로 떠나면 Breakaway가 데려간다. 판도 후면도
    /// 없는 우주에 뜬 모듈만 파괴한다 - 그건 어디에도 안 매달린 불사 오브젝트다. 예전에는
    /// 판 없는 모듈을 전부 파괴해서 방 한가운데 원자로가 스폰 직후 사라지고 배가 바로 죽었다.
    /// </summary>
    public static void MountLooseModules(
        Transform hull,
        ShipGrid.Map map,
        Dictionary<Vector2Int, Armor> armorAt,
        Dictionary<Vector2Int, Door> doorAt,
        bool firstBuild = true)
    {
        if (hull == null || map == null)
            return;

        _loose.Clear();

        foreach (Transform child in hull)
        {
            // 판과 문은 선체 직속이 맞다 - 격자에 도장을 찍는 것이 그 정의다.
            if (child == null || StampsGrid(child, out _))
                continue;

            // **IDamageable이 곧 모듈이다.** Gun·Engine·CriticalModule·Tank 넷이고,
            // Armor와 Door는 Thing만 상속해서 안 걸린다. 타입 목록을 손으로 적으면
            // 다섯 번째 모듈이 생기는 날 조용히 빠진다.
            if (child.GetComponent<IDamageable>() != null)
                _loose.Add(child);
        }

        for (int i = 0; i < _loose.Count; i++)
        {
            Transform module = _loose[i];
            Vector2Int cell = ModulePlacement.CentreCellOf(module, map);   // 자리 = 상자 중심 칸

            Transform plate = null;

            if (map.Inside(cell))
            {
                if (armorAt.TryGetValue(cell, out Armor armor) && armor != null)
                    plate = armor.transform;
                else if (doorAt.TryGetValue(cell, out Door door) && door != null)
                    plate = door.transform;
            }

            if (plate == null)
            {
                // 실내면 선체 직속으로 두고 후면이 수명을 맡는다. 파단 뒤 다시 지을 때는
                // 방이 우주로 열렸어도 후면(설계도 기준)이 살아 있으면 모듈도 산다 - 그때의
                // 살아 있는 격자 Exterior는 "바닥이 없다"가 아니라 "천장이 뚫렸다"다.
                if (!firstBuild || OnRear(map, cell))
                    continue;

                Debug.LogWarning(
                    $"[ShipBuilder] '{module.name}'의 자리({cell})에 판도 후면도 없다(우주). 어디에도 " +
                    "안 매달린 모듈은 불사가 되므로 파괴한다. 배치를 고칠 것.", hull);

                Object.Destroy(module.gameObject);
                continue;
            }

            module.SetParent(plate, worldPositionStays: true);
        }
    }

    /// <summary>순회 도중 계층을 바꾸면 안 되므로 한 번 모아 두는 버퍼.</summary>
    private static readonly List<Transform> _loose = new();

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
        Dictionary<Vector2Int, Door> doorAt,
        ShipGrid.Map map)
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

            // **우주에 닿은 판만 물리 세계에 남긴다.**
            //
            // 이웃 8칸 중 하나라도 Exterior면 이 판은 껍질이다. 하나도 없으면 방을 두르는
            // 안쪽 격벽이거나 장갑대 속이라 아무것도 안 부딪힌다 - 빼도 잃는 것이 없고,
            // Box2D의 픽스처 추가/제거 값이 그 몸의 픽스처 수에 비례하므로 그 곱셈의
            // 한쪽이 작아진다(Armor.SetBuried 참고).
            //
            // **"8방향이 전부 판이면 파묻힘"으로는 안 된다.** 재봤더니 이사리비 2302장 중
            // 57장(2%)뿐이었다 - 배가 두꺼워서가 아니라 속이 비어서다. Exterior 기준이면
            // 1822장(79%)이다. 큰 배일수록 껍질 비율이 높아서(destroyer 46%, 이사리비 79%)
            // 이 규칙이 정확히 아픈 쪽에 듣는다.
            //
            // 맵 밖은 우주로 센다. 안 그러면 가장자리 판이 통째로 파묻힌다.
            bool skin = false;

            foreach (Vector2Int dir in Around)
            {
                Vector2Int at = pair.Key + dir;

                if (!map.Inside(at) || map.cells[at.x, at.y] == ShipGrid.Cell.Exterior)
                {
                    skin = true;
                    break;
                }
            }

            pair.Value.SetBuried(!skin);

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
    private static readonly Unity.Profiling.ProfilerMarker _mSpawnFrom = new("ShipBuilder.SpawnFrom");

    public static bool SpawnFrom(Transform hull, ShipDef def, Component pourInto)
    {
        if (def == null)
            return false;

        using var _ = _mSpawnFrom.Auto();

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
        // 한 프레임에 끝내는 길. 코루틴 버전과 **같은 단계를 같은 순서로** 밟는다 -
        // 갈리면 격납고에서 나온 배와 캠페인이 소환한 배가 다른 물건이 된다.
        IEnumerator build = SpawnOverTime(hull, def, 0f, null);

        while (build.MoveNext()) { }
    }

    /// <summary>
    /// 배치를 <paramref name="perPlateDelay"/>초 간격으로 하나씩 심는다. 0이면
    /// <see cref="Spawn"/>과 같은 한 프레임 건조다.
    ///
    /// **판은 콜라이더를 끄고 태어난다.** 건조 중인 배는 모함 안에 붙어 있는데, 켜져
    /// 있으면 RamImpact가 매 틱 둘을 갈아서 모함이 자기가 만드는 배를 부순다. 끄면
    /// 솔버와 충각은 못 보고 **탄도(TraceWorld)는 계층에서 읽으므로 여전히 맞는다** -
    /// 골조만 선 배를 쏠 수 있다는 뜻이고, 그게 의도다.
    ///
    /// 격자·구조·후면은 여기서 안 만든다. 부르는 쪽이 완성 뒤에 Stamp -> Build ->
    /// SeedRear를 돌린다 - 그때까지 HullStructure._hasMap이 false라 파단 BFS가 안 돌고
    /// (TrySplitIfBroken의 첫 가드), 방이 없으니 기압도 안 돈다. 반쯤 지어진 배가
    /// 스스로 조각나는 것을 막는 것이 그 순서다.
    /// </summary>
    public static IEnumerator SpawnOverTime(
        Transform hull, ShipDef def, float perPlateDelay, System.Action<Thing> onPlaced)
    {
        var temp = AuthoredMap(def);
        if(temp.map == null)
        {
            Debug.LogAssertion($"Fucking Error. Call Opus. hull:{hull.name}");
            yield break;
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

        for(int i = 0; i < def.placements.Count; i++)
        {
            var p = def.placements[i];
            var cell = new Vector2Int(p.col - minCol, p.row - minRow);

            // 자리와 각도를 스폰에 같이 넘긴다. def로 지은 물건은 전부 붙이고 자리를 잡은
            // 뒤에 활성화돼야 해서, 밖에서 나중에 옮기면 Awake가 이미 지나간 뒤가 된다.
            // 자리는 격자가 정하고, 콜라이더는 배치가 덧쓸 수 있다. **오브젝트 위치에는
            // p.offset을 안 더한다** - localPosition이 곧 칸 번호라 그걸 밀면 Stamp가 다른
            // 칸을 읽는다. 미는 것은 콜라이더 offset뿐이다 (Placement.offset 참고).
            Thing spawned = DefDatabase.Spawn(
                p.def, hull, map.ToLocal(cell.x, cell.y), p.rot, p.size, p.offset, p.shape);

            if (spawned == null)
            {
                Debug.LogError($"[ShipBuilder] '{def.defName}'이 부르는 defName '{p.def}'이 없다.");
                missing++;
                continue;
            }

            // **활성화가 끝난 뒤에 바른다.** ThingDef.Spawn이 오브젝트를 켜면서 Awake가
            // 돌고, Armor.Awake는 서브셀을 전부 만땅으로 초기화한다. 그 전에 넣으면 지워진다.
            Restore(spawned, p.hp);

            // **한 오브젝트의 Thing 전부에 찍는다.** ThingDef.Spawn은 thingClass 하나만
            // 돌려주는데 comps에도 Thing이 올 수 있다 - Ballistic Door가 BallisticArmor에
            // Door를 얹은 것이 그렇다. 돌려받은 것에만 찍으면 나머지가 -1로 남는다.
            // 같은 오브젝트끼리 ID를 공유하는 것은 겹침이 아니다. 물건이 하나니까 맞다.
            foreach (Thing t in spawned.GetComponents<Thing>())
                t.stableId = i;

            if (StampsGrid(spawned, out _))
                plateAt[cell] = spawned.transform;
            else
                modules.Add((p, spawned.transform));

            if (perPlateDelay <= 0f)
                continue;

            // 건조 중에는 물리 세계 밖이다. 완성 뒤 WireNeighbours가 껍질만 다시 켠다.
            if (spawned.TryGetComponent(out Collider2D col))
                col.enabled = false;

            onPlaced?.Invoke(spawned);

            yield return new WaitForSeconds(perPlateDelay);
        }

        // 심은 것을 그대로 들고 온다. 위치로 다시 찾으면 판 위에 올라앉은 모듈이 자기 판을
        // 자기 자신으로 착각하고 스스로의 부모가 된다.
        foreach ((Placement p, Transform module) in modules)
        {
            // **붙을 판을 안 적었으면 발밑 판에 붙는다.** 예전에는 조용히 넘어가서 선체
            // 직속으로 남았는데, 그러면 판이 부서져도 안 죽고 잔해로 떠나도 안 따라가는
            // 고아가 된다 - "판이 아닌 것은 선체 직속 자식이 되면 안 된다"가 깨지는 자리다.
            // 증상이 "부서진 자리에 포탑만 떠 있다"라 배치 실수인지 코드 버그인지 안 갈린다.
            var mount = p.IsMounted
                ? new Vector2Int(p.mountCol - minCol, p.mountRow - minRow)
                : new Vector2Int(p.col - minCol, p.row - minRow);

            // 붙을 판이 없으면 선체 직속이다 - 실내 모듈은 그 칸의 후면이 죽을 때
            // HullStructure.KillRear가 같이 죽이고 조각으로 떠나면 Breakaway가 데려간다.
            // 자리가 틀린 것은 WarnModuleFits가 한 줄로 말한다.
            if (!plateAt.TryGetValue(mount, out Transform plate))
                continue;

            // 판 밑으로 한 겹 내려간다. 판이 죽으면 같이 죽고, 판이 잔해로 떨어져 나가면
            // 같이 날아간다 - 둘 다 별도 코드 없이 부모 자식 관계 하나로 나온다.
            module.SetParent(plate, worldPositionStays: true);
        }

        if (missing > 0)
            Debug.LogError($"[ShipBuilder] '{def.defName}'에서 {missing}개를 심지 못했다.");

        WarnModuleFits(def, map, minCol, minRow, modules);
    }

    /// <summary>
    /// 자리가 안 맞는 모듈을 배마다 한 줄로. 판정은 <see cref="ModulePlacement.Evaluate"/> -
    /// 페인터와 같은 함수다. 아직 거부하지 않는다 - 기존 배가 많이 걸려서 거부하면
    /// 원자로 없는 배로 시작한다. 페인터가 새 배치를 막고, 다 고치면 여기를 거부로 바꾼다.
    /// </summary>
    private static void WarnModuleFits(
        ShipDef def, ShipGrid.Map map, int minCol, int minRow,
        List<(Placement placement, Transform spawned)> modules)
    {
        var plates = new List<ModulePlacement.Plate>();
        var others = new List<ModulePlacement.Other>();

        foreach (Placement p in def.placements)
        {
            var cell = new Vector2Int(p.col - minCol, p.row - minRow);

            if (!StampsGrid(DefDatabase.Get(p.def), out _))
                continue;

            Vector2[] poly = ModulePlacement.PlatePolygon(cell, p.def, p.rot, p.size, p.offset, p.shape);
            plates.Add(new ModulePlacement.Plate { cell = cell, poly = poly, area = Mathf.Abs(Ballistics.PolygonArea(poly)) });
        }

        foreach ((Placement p, Transform _) in modules)
        {
            var origin = new Vector2Int(p.col - minCol, p.row - minRow);
            others.Add(new ModulePlacement.Other
            {
                origin = origin,
                poly = ModulePlacement.ModulePolygon(origin, p.def, p.rot, p.size, p.offset, out _, out _),
            });
        }

        var notes = new List<string>();
        int count = 0, buried = 0;

        foreach ((Placement p, Transform _) in modules)
        {
            var origin = new Vector2Int(p.col - minCol, p.row - minRow);
            ModulePlacement.Result fit = ModulePlacement.Evaluate(
                p.def, origin, p.rot, p.size, p.offset, c => OnRear(map, c), plates, others);

            if (fit.verdict == ModulePlacement.Verdict.Ok)
                continue;

            if (fit.verdict == ModulePlacement.Verdict.Buried)
            {
                buried++;
                continue;
            }

            count++;

            // detail의 좌표는 격자 칸(원점 (0,0) = 판 bbox 왼쪽 위)이라 배치 col/row와 다르다.
            if (notes.Count < 8)
                notes.Add($"{p.def}@({p.col},{p.row}) [격자 ({origin.x},{origin.y})] {fit.detail}");
        }

        if (count > 0)
            Debug.LogWarning(
                $"[ShipBuilder] '{def.defName}' 모듈 {count}개의 자리가 안 맞는다. 페인터에서 옮겨라: " +
                $"{string.Join(" / ", notes)}{(count > notes.Count ? " ..." : "")}" +
                (buried > 0 ? $"  (배 안에 묻힌 포·엔진 {buried}개)" : ""));
    }
}
