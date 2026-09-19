using UnityEngine;

/// <summary>
/// 정지 회로도에서 클릭한 것. 림월드의 선택과 같다 - 하나만, 클릭하면 바꾸고, 빈 곳이면 푼다.
/// 그리는 것은 ShipStatusHud(DrawInspectPanel)이고 여기는 상태와 맞추기(hit test)뿐이다.
/// </summary>
public static class Inspect
{
    /// <summary>전선 한 구간. 배와 (전선, 구간) 번호로 가리킨다 - 참조가 아니라 번호라 재빌드 뒤에도 같은 자리다.</summary>
    public readonly struct WireSel
    {
        public readonly Ship ship;
        public readonly int wire, seg;
        public WireSel(Ship ship, int wire, int seg) { this.ship = ship; this.wire = wire; this.seg = seg; }
    }

    public static Component Thing { get; private set; }
    public static WireSel? Wire { get; private set; }
    public static Crewman Crew { get; private set; }
    public static Ship CrewShip { get; private set; }

    public static bool Any => Thing != null || Wire != null || Crew != null;

    public static void Clear()
    {
        Thing = null;
        Wire = null;
        Crew = null;
        CrewShip = null;
    }

    /// <summary>월드 한 점에서 제일 그럴듯한 것 하나. 승무원 → 전선 → 모듈 → 판 순 - 작은 것이 먼저다.</summary>
    public static void Click(Vector2 world)
    {
        WireSel? was = Wire;
        Clear();

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];
            if (ship == null) continue;

            for (int k = 0; k < ship.crewmen.Count; k++)
            {
                Vector2 at = ship.transform.TransformPoint((Vector2)ship.crewmen[k].anchor * ShipGrid.CellSize);
                if ((at - world).sqrMagnitude <= 0.6f * 0.6f)
                {
                    Crew = ship.crewmen[k];
                    CrewShip = ship;
                    return;
                }
            }
        }

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];
            if (ship == null || !ship.HasWiring) continue;

            Vector2 local = ship.transform.InverseTransformPoint(world);
            if (ship.TryPickWire(local, Ballistics.WireViewWidth * 1.5f, out int wire, out int seg))
            {
                // 트립된 전선을 골라 둔 채 한 번 더 클릭 = 차단기 올리기. 정지 화면의 유일한 명령이다.
                if (was is WireSel w && w.ship == ship && w.wire == wire && ship.BreakerTripped(wire))
                    ship.ResetBreaker(wire);

                Wire = new WireSel(ship, wire, seg);
                return;
            }
        }

        Component best = null;
        int bestRank = 0;

        foreach (Collider2D c in Physics2D.OverlapPointAll(world))
        {
            Thing t = c.GetComponentInParent<Thing>();
            if (t == null) continue;

            int rank = t is Armor ? 1 : 2;   // 판 위에 앉은 모듈이 판을 이긴다
            if (rank > bestRank) { best = t; bestRank = rank; }
        }

        Thing = best;
    }
}
