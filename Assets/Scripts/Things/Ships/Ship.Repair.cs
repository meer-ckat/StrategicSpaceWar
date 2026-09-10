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
    /// 노획 <paramref name="budget"/>장어치로 상한 판을 고친다. 실제로 쓴 장수를 돌려준다.
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

    // ---- 잃은 모듈 되사기 ------------------------------------------------------
    // 판은 안 돌아오지만 모듈은 자재로 다시 산다. 값은 콜라이더 넓이(m²당 판 1장) - 모듈에 질량이
    // 없어서 크기가 유일한 "얼마나 큰 물건인가"다. 305mm 포탑 11, 엔진 1, 탄약고 6.
    public const float RefitCostPerSquareMetre = 1f;

    public readonly struct LostModule
    {
        public readonly int index;          // 설계도(shipDefName) 배치 인덱스
        public readonly Placement placement;
        public readonly Armor mount;        // 붙을 판. 살아 있는 것만 여기 온다
        public readonly Vector2 local;      // 선체 로컬 자리
        public readonly int cost;

        public LostModule(int index, Placement placement, Armor mount, Vector2 local, int cost)
        {
            this.index = index;
            this.placement = placement;
            this.mount = mount;
            this.local = local;
            this.cost = cost;
        }
    }

    /// <summary>
    /// 설계도에는 있는데 지금 배에는 없는 모듈. **붙을 판이 살아 있는 것만** - 판이 없으면 얹을
    /// 자리가 없고, 판은 안 돌아온다는 규칙을 여기서 깨지 않는다. 있고 없고는 stableId가 아니라
    /// (def, 자리)로 본다 - 저장본의 인덱스는 설계도와 다른 번호다.
    /// </summary>
    public List<LostModule> LostModules()
    {
        var lost = new List<LostModule>();
        ShipDef design = string.IsNullOrEmpty(shipDefName) ? null : ShipDef.Load(shipDefName);

        if (design == null || DesignMap == null)
            return lost;

        Vector2Int mins = design.Bbox().min;

        var present = new List<(string def, Vector2 local)>();
        foreach (Thing t in GetComponentsInChildren<Thing>())
        {
            if (t.transform == transform || ShipBuilder.IsPlate(t) || string.IsNullOrEmpty(t.defName))
                continue;
            present.Add((t.defName, (Vector2)transform.InverseTransformPoint(t.transform.position)));
        }

        for (int i = 0; i < design.placements.Count; i++)
        {
            Placement p = design.placements[i];
            ThingDef def = DefDatabase.Get(p.def);

            if (def == null || ShipBuilder.StampsGrid(def, out _))
                continue;

            Vector2 local = DesignMap.ToLocal(p.col - mins.x, p.row - mins.y);
            bool here = false;
            foreach ((string d, Vector2 at) in present)
                if (d == p.def && (at - local).sqrMagnitude < 0.01f) { here = true; break; }
            if (here)
                continue;

            var mountCell = p.IsMounted
                ? new Vector2Int(p.mountCol - mins.x, p.mountRow - mins.y)
                : new Vector2Int(p.col - mins.x, p.row - mins.y);

            Armor mount = null;
            foreach (Armor a in shipArmors)
                if (a != null && StillAboard(a, this) && DesignMap.ToCell(a.transform.localPosition) == mountCell) { mount = a; break; }
            if (mount == null)
                continue;

            Vector2 size = p.size != Vector2.zero ? p.size : def.collider.size;
            int cost = Mathf.Max(1, Mathf.RoundToInt(size.x * size.y * RefitCostPerSquareMetre));
            lost.Add(new LostModule(i, p, mount, local, cost));
        }

        return lost;
    }

    /// <summary>ShipBuilder.SpawnOverTime이 한 모듈에 하는 일을 그대로 한 번. 자재 차감은 부르는 쪽이다.</summary>
    public Thing BuyModule(LostModule m)
    {
        if (m.mount == null || !StillAboard(m.mount, this))
            return null;

        Placement p = m.placement;
        Thing spawned = DefDatabase.Spawn(p.def, transform, m.local, p.rot, p.size, p.offset, p.shape);

        if (spawned == null)
            return null;

        // 저장본 인덱스와 안 겹치게 설계도 길이만큼 띄운다. 저장본은 설계도의 부분집합이라 그 수를 못 넘는다.
        int designCount = ShipDef.Load(shipDefName)?.placements.Count ?? 0;
        foreach (Thing t in spawned.GetComponents<Thing>())
            t.stableId = designCount + m.index;

        spawned.transform.SetParent(m.mount.transform, worldPositionStays: true);

        switch (spawned)
        {
            case Gun gun: shipGuns.Add(gun); _gunnerLost = false; break;
            case Engine engine: shipEngines.Add(engine); _engineerLost = false; break;
            case Tank tank: shipTanks.Add(tank); break;
            case CriticalModule critical: shipCriticals.Add(critical); break;
        }

        return spawned;
    }
}
