using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 수리. **구역 사이에만 일어나는 일이라 틱 루프에 없다.**
///
/// 고치는 것은 상한 판뿐이고 없어진 판은 못 되살린다. 판이 통째로 사라지는 것은 격자가
/// 바뀌는 일이라 방·구조 장부를 다시 지어야 하고, 그건 수리가 아니라 재건이다.
/// </summary>
public partial class Ship
{
    /// <summary>이 밑이면 손상이다. 1f와 직접 비교하면 float 합의 끝자리가 새 판을 손상으로 읽는다.</summary>
    public const float DamagedBelow = 0.9995f;

    // 구역마다 한 번 도는 일이지만, 판 300장짜리 배에서 매번 리스트를 새로 만들 이유는 없다.
    private static readonly List<Armor> _repairQueue = new();

    /// <summary>
    /// 돈 <paramref name="budget"/>어치로 상한 판을 고친다. 실제로 쓴 장수를 돌려준다.
    ///
    /// **제일 많이 상한 것부터.** 한 장을 어디 쓰든 값이 같으니, 제일 약해진 판을 되살리는
    /// 것이 같은 값으로 제일 많이 사는 길이다. 판 하나에 노획 한 장이고 부분 수리는 없다 -
    /// "0.6까지 고쳤다"는 상태는 화면에도 안 보이고 다음 전투에서 의미도 없다.
    ///
    /// <see cref="Armor.RestoreHealthFraction"/>이 값을 그냥 놓는다는 것이 중요하다. 피해
    /// 경로로 되돌리면 서브셀이 실제로 죽어서, 고치려던 판이 PlateCollapseFraction을 넘겨
    /// 무너진다.
    /// </summary>
    public int RepairPlates(int budget)
    {
        // 정비는 탄 전선도 되돌린다. 전선만 따로 고치는 화면은 M6(야전수리)까지 없다.
        RepairWires();

        if (budget <= 0)
            return 0;

        _repairQueue.Clear();

        foreach (Armor plate in shipArmors)
        {
            // 잔해로 떠난 판은 목록에 그대로 살아 있다. 그걸 고치면 100 m 뒤에 떠 있는
            // 남의 판에 노획을 쓴다 - 엔진과 포탑이 겪은 것과 같은 함정이다.
            if (plate != null && StillAboard(plate, this) && plate.HealthFraction < DamagedBelow)
                _repairQueue.Add(plate);
        }

        if (_repairQueue.Count == 0)
            return 0;

        _repairQueue.Sort((a, b) => a.HealthFraction.CompareTo(b.HealthFraction));

        int used = Mathf.Min(budget, _repairQueue.Count);

        for (int i = 0; i < used; i++)
            _repairQueue[i].RestoreHealthFraction(1f);

        return used;
    }

    /// <summary>고칠 것이 몇 장 남았는가. 노획을 남기지 않으려고 부르는 쪽이 미리 본다.</summary>
    public int DamagedPlateCount()
    {
        int count = 0;

        foreach (Armor plate in shipArmors)
        {
            if (plate != null && StillAboard(plate, this) && plate.HealthFraction < DamagedBelow)
                count++;
        }

        return count;
    }

    // ---- 모듈 자리 ------------------------------------------------------------
    // 판은 안 돌아오지만 모듈은 베이스에서 산다. 값은 콜라이더 넓이(m²당 1 CR) - 모듈에 질량이
    // 없어서 크기가 유일한 "얼마나 큰 물건인가"다. 305mm 포탑 11, 엔진 1, 탄약고 6.
    //
    // **자리는 설계가 정하고 무엇을 다는지는 베이스에서 고른다.** 빈 자리를 채우는 것과 붙어
    // 있는 것을 더 좋은 것으로 바꾸는 것이 같은 경로인 이유는, 둘 다 "이 자리에 이 def를
    // 세운다"라서다. 자리·회전·마운트가 설계 그대로라 배치는 안 바뀐다 - 재배치가 아니다.
    public const float RefitCostPerSquareMetre = 1f;

    public readonly struct ModuleSlot
    {
        public readonly int index;          // 설계도(shipDefName) 배치 인덱스
        public readonly Placement placement;
        public readonly Armor mount;        // 붙을 판. 살아 있는 것만 여기 온다
        public readonly Vector2 local;      // 선체 로컬 자리
        public readonly Thing current;      // 지금 이 자리에 있는 것. null이면 잃은 자리

        public ModuleSlot(int index, Placement placement, Armor mount, Vector2 local, Thing current)
        {
            this.index = index;
            this.placement = placement;
            this.mount = mount;
            this.local = local;
            this.current = current;
        }

        public bool IsEmpty => current == null;
    }

    /// <summary>def 하나를 이 자리에 세우는 값. 배치가 크기를 덮어쓴 자리는 그 크기로 센다.</summary>
    public static int PriceOf(string defName, Placement p)
    {
        ThingDef def = DefDatabase.Get(defName);

        if (def == null)
            return int.MaxValue;

        // 다른 def로 바꾸는 자리에서는 배치의 size를 안 쓴다 - 그 숫자는 옛 def에 맞춰 적은 것이다.
        Vector2 size = (defName == p.def && p.size != Vector2.zero) ? p.size : def.collider.size;

        return Mathf.Max(1, Mathf.RoundToInt(size.x * size.y * RefitCostPerSquareMetre));
    }

    /// <summary>
    /// 설계도의 모듈 자리 전부. 비어 있는 것도 차 있는 것도 같이 온다 - 무손상 승리에도 살 것이
    /// 있어야 전투가 성장이 된다. **붙을 판이 살아 있는 것만** - 판이 없으면 얹을 자리가 없고,
    /// 판은 안 돌아온다는 규칙을 여기서 깨지 않는다.
    ///
    /// 무엇이 그 자리에 있는지는 stableId가 아니라 **자리**로 본다 - 저장본의 인덱스는 설계도와
    /// 다른 번호고, 이제는 설계와 다른 def가 서 있을 수도 있다.
    /// </summary>
    public List<ModuleSlot> ModuleSlots()
    {
        var lost = new List<ModuleSlot>();
        ShipDef design = string.IsNullOrEmpty(shipDefName) ? null : ShipDef.Load(shipDefName);

        if (design == null || DesignMap == null)
            return lost;

        Vector2Int mins = design.Bbox().min;

        var present = new List<(Thing thing, Vector2 local)>();
        foreach (Thing t in GetComponentsInChildren<Thing>())
        {
            if (t.transform == transform || ShipBuilder.IsPlate(t) || string.IsNullOrEmpty(t.defName))
                continue;
            present.Add((t, (Vector2)transform.InverseTransformPoint(t.transform.position)));
        }

        for (int i = 0; i < design.placements.Count; i++)
        {
            Placement p = design.placements[i];
            ThingDef def = DefDatabase.Get(p.def);

            if (def == null || ShipBuilder.StampsGrid(def, out _))
                continue;

            Vector2 local = DesignMap.ToLocal(p.col - mins.x, p.row - mins.y);

            // 자리로만 찾는다. def가 같은지는 안 본다 - 강화로 다른 것이 서 있을 수 있다.
            Thing current = null;
            foreach ((Thing t, Vector2 at) in present)
                if ((at - local).sqrMagnitude < 0.01f) { current = t; break; }

            var mountCell = p.IsMounted
                ? new Vector2Int(p.mountCol - mins.x, p.mountRow - mins.y)
                : new Vector2Int(p.col - mins.x, p.row - mins.y);

            Armor mount = null;
            foreach (Armor a in shipArmors)
                if (a != null && StillAboard(a, this) && DesignMap.ToCell(a.transform.localPosition) == mountCell) { mount = a; break; }
            if (mount == null)
                continue;

            lost.Add(new ModuleSlot(i, p, mount, local, current));
        }

        return lost;
    }

    /// <summary>
    /// 이 자리에 <paramref name="defName"/>을 세운다. null이면 설계 그대로 - 되사기다.
    /// 이미 서 있는 것이 있으면 먼저 치운다(강화). 차감은 부르는 쪽이다.
    ///
    /// ShipBuilder.SpawnOverTime이 한 모듈에 하는 일을 그대로 한 번 한다.
    /// </summary>
    public Thing BuyModule(ModuleSlot m, string defName = null)
    {
        if (m.mount == null || !StillAboard(m.mount, this))
            return null;

        Placement p = m.placement;
        string def = string.IsNullOrEmpty(defName) ? p.def : defName;

        // 같은 def를 그대로 다시 사는 것은 아무 일도 아니다 - 부르는 쪽의 실수를 여기서 막는다.
        if (m.current != null && m.current.defName == def)
            return null;

        // 배치의 size는 옛 def에 맞춰 적은 숫자다. 다른 것을 세울 때는 그 def의 콜라이더를 쓴다.
        Vector2 size = def == p.def ? p.size : Vector2.zero;

        // **떼는 것이 먼저다.** 새로 세운 뒤에 치우면 ModulePlacement가 볼 때 두 개가 겹쳐 있다.
        if (m.current != null)
            Scrap(m.current);

        Thing spawned = DefDatabase.Spawn(def, transform, m.local, p.rot, size, p.offset, p.shape);

        if (spawned == null)
            return null;

        // 저장본 인덱스와 안 겹치게 설계도 길이만큼 띄운다. 저장본은 설계도의 부분집합이라 그 수를 못 넘는다.
        int designCount = ShipDef.Load(shipDefName)?.placements.Count ?? 0;
        foreach (Thing t in spawned.GetComponents<Thing>())
            t.stableId = designCount + m.index;

        spawned.transform.SetParent(m.mount.transform, worldPositionStays: true);
        _powerDirty = true;   // 기기 집합이 바뀌었다. 떼고 새로 세우면 개수가 같아 서명으로는 안 잡힌다

        switch (spawned)
        {
            case Gun gun: shipGuns.Add(gun); _gunnerLost = false; break;
            case Engine engine: shipEngines.Add(engine); _engineerLost = false; break;
            case Tank tank: shipTanks.Add(tank); break;
            case CriticalModule critical: shipCriticals.Add(critical); break;
        }

        return spawned;
    }

    /// <summary>
    /// 자리를 비운다. 목록에서 먼저 빼고 지운다 - 파괴된 오브젝트를 목록에 남기면 그 자리들이
    /// <see cref="StillAboard"/>가 아니라 null 검사에 걸려서, "왜 안 죽었나"와 "왜 목록에 있나"가
    /// 서로 다른 자리에서 따로 터진다.
    /// </summary>
    private void Scrap(Thing thing)
    {
        _powerDirty = true;

        switch (thing)
        {
            case Gun gun: shipGuns.Remove(gun); break;
            case Engine engine: shipEngines.Remove(engine); break;
            case Tank tank: shipTanks.Remove(tank); break;
            case CriticalModule critical: shipCriticals.Remove(critical); break;
        }

        Destroy(thing.gameObject);
    }

    /// <summary>
    /// 이 자리에 세울 수 있는 def들. 같은 <c>thingClass</c>에, 배치 판정(<see
    /// cref="ModulePlacement.Evaluate"/>)을 통과하는 것만. 판정이 페인터·빌더와 같은 함수라
    /// 베이스에서 산 것이 설계도에서 거부당하는 일이 없다.
    ///
    /// 지금 서 있는 것은 목록에서 빠진다 - 같은 것을 다시 사는 줄은 고를 것이 아니다.
    /// </summary>
    public List<string> Candidates(ModuleSlot slot)
    {
        var options = new List<string>();
        ShipDef design = string.IsNullOrEmpty(shipDefName) ? null : ShipDef.Load(shipDefName);
        ThingDef here = DefDatabase.Get(slot.placement.def);

        if (design == null || here == null || DesignMap == null)
            return options;

        Vector2Int mins = design.Bbox().min;
        var origin = new Vector2Int(slot.placement.col - mins.x, slot.placement.row - mins.y);

        var plates = new List<ModulePlacement.Plate>();
        var others = new List<ModulePlacement.Other>();

        foreach (Placement q in design.placements)
        {
            var cell = new Vector2Int(q.col - mins.x, q.row - mins.y);

            if (ShipBuilder.StampsGrid(DefDatabase.Get(q.def), out _))
            {
                Vector2[] poly = ModulePlacement.PlatePolygon(cell, q.def, q.rot, q.size, q.offset, q.shape);
                plates.Add(new ModulePlacement.Plate
                {
                    cell = cell, poly = poly, area = Mathf.Abs(Ballistics.PolygonArea(poly)),
                });
                continue;
            }

            // 자기 자신은 빼야 한다 - 안 빼면 바꾸려는 모듈이 자기와 겹친다고 전부 거부된다.
            if (cell == origin)
                continue;

            others.Add(new ModulePlacement.Other
            {
                origin = cell,
                poly = ModulePlacement.ModulePolygon(cell, q.def, q.rot, q.size, q.offset, out _, out _),
            });
        }

        foreach (ThingDef candidate in DefDatabase.All)
        {
            if (candidate.thingClass != here.thingClass || candidate.defName == slot.placement.def)
                continue;

            if (slot.current != null && candidate.defName == slot.current.defName)
                continue;

            ModulePlacement.Result fit = ModulePlacement.Evaluate(
                candidate.defName, origin, slot.placement.rot, Vector2.zero, slot.placement.offset,
                c => ShipBuilder.OnRear(DesignMap, c), plates, others);

            if (fit.verdict == ModulePlacement.Verdict.Ok)
                options.Add(candidate.defName);
        }

        return options;
    }
}
