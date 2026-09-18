using System.Collections.Generic;
using UnityEngine;

public partial class Ship
{
    private ShipDef _design;

    // 원자로에서 전선으로 닿는 기기 칸. AtmosphereInterval마다 다시 짓는다 - 더티 추적 없이
    // 10틱 지연을 받는 것은 Atmosphere/Crew와 같은 결정이다.
    private readonly HashSet<Vector2Int> _powered = new();
    private bool _hasWiring;

    // 전선 하나 = 폴리라인(배 로컬 m) + 지나는 (판, 서브셀). 그중 하나라도 죽으면 끊긴다.
    // 판이 없는 칸(실내 공기)은 기록이 없어 못 끊긴다 - 파편은 M6.
    private class WireRun
    {
        public Vector2[] local;
        public readonly List<(Armor plate, int sub)> subs = new();
    }

    private readonly List<WireRun> _wires = new();
    private Dictionary<Vector2Int, Armor> _armorAt = new();

    /// <summary>이 모듈까지 전기가 오는가. 전선 없는 설계는 <see cref="HasPower"/> 그대로.</summary>
    public bool Powered(Component module)
    {
        if (!HasPower) return false;
        if (!_hasWiring || _map == null) return true;
        return _powered.Contains(CellOf(module.transform));
    }

    // 모듈은 판의 자식이라 localPosition이 판 기준이다 - CentreCellOf(선체 직속 전제)를 쓰면 전부
    // 판 원점 근처 칸으로 뭉친다. 월드에서 배 로컬로 되돌려 칸을 잡는다.
    private Vector2Int CellOf(Transform module)
    {
        Vector2 local = transform.InverseTransformPoint(module.position);
        if (module.TryGetComponent(out BoxCollider2D box))
            local += Ballistics.Rotate(box.offset, module.eulerAngles.z - transform.eulerAngles.z);
        return _map.ToCell(local);
    }

    /// <summary>설계 점(칸 좌표) → 배 로컬. 판이 서는 자리(AuthoredMap)와 같은 식이라 전선이 판 위에 정확히 얹힌다.</summary>
    private void BuildWires(ShipDef design)
    {
        _wires.Clear();
        if (design?.wires == null || design.wires.Count == 0) return;

        (ShipGrid.Map authored, Vector2Int mins) = ShipBuilder.AuthoredMap(design);
        if (authored == null) return;

        foreach (Wire w in design.wires)
        {
            if (w?.points == null || w.points.Count < 2) continue;
            var run = new WireRun { local = new Vector2[w.points.Count] };
            for (int i = 0; i < w.points.Count; i++)
                run.local[i] = new Vector2(
                    authored.origin.x + (w.points[i].x - mins.x) * ShipGrid.CellSize,
                    authored.origin.y - (w.points[i].y - mins.y) * ShipGrid.CellSize);
            _wires.Add(run);
        }

        RasterizeWires();
    }

    // ponytail: 반 서브셀 간격 샘플링. DDA(Ballistics.March)가 정확하지만 빌드 때 한 번이라 이걸로 충분하다.
    private void RasterizeWires()
    {
        const float step = ShipGrid.CellSize / Ballistics.SubGrid * 0.5f;

        foreach (WireRun run in _wires)
        {
            run.subs.Clear();
            if (_map == null) continue;
            Armor lastPlate = null;
            int lastSub = -1;

            for (int i = 1; i < run.local.Length; i++)
            {
                Vector2 a = run.local[i - 1], b = run.local[i];
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / step));

                for (int k = 0; k <= n; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)k / n);
                    if (!_armorAt.TryGetValue(_map.ToCell(p), out Armor plate) || plate == null) continue;

                    int sub = plate.SubIndexAtLocal(plate.transform.InverseTransformPoint(transform.TransformPoint(p)));
                    if (sub < 0 || sub >= Armor.SubCount || (plate == lastPlate && sub == lastSub)) continue;

                    run.subs.Add((plate, sub));
                    lastPlate = plate;
                    lastSub = sub;
                }
            }
        }
    }

    private bool Intact(WireRun run)
    {
        foreach ((Armor plate, int sub) in run.subs)
            if (plate == null || !StillAboard(plate, this) || plate.IsBreached(sub))
                return false;
        return true;
    }

    private static void Link(Connector a, Connector b)
    {
        a.connectedConnectors.Add(b);
        b.connectedConnectors.Add(a);
    }

    // ponytail: 10틱마다 GetComponentsInChildren + 노드 전부 새로 할당. 배 20척에서 GC가 보이면
    // 판·모듈 죽음에 더티 플래그를 걸고 그때만 짓는다.
    private void RebuildPowerNet()
    {
        _powered.Clear();
        if (!_hasWiring || _map == null) return;

        Connector.connectors.Clear();
        var deviceAt = new Dictionary<Vector2Int, Connector>();
        var sources = new List<Connector>();

        // 기기 = 원자로·포탑. 릴레이·퓨즈가 오면 여기가 늘어난다.
        foreach (Thing t in GetComponentsInChildren<Thing>())
        {
            bool source = t is CriticalModule c && c.providesPower && !c.Neutralized;
            if (!source && t is not Gun) continue;

            Vector2Int cell = CellOf(t.transform);
            if (deviceAt.ContainsKey(cell)) continue;

            var k = new Connector(cell, 1f);
            deviceAt[cell] = k;
            if (source) sources.Add(k);
        }

        // 전선 꼭짓점 = 노드. 같은 자리(1/6 m 눈금)의 꼭짓점은 같은 노드라 전선끼리 거기서 이어진다.
        // 꼭짓점이 기기 칸 위면 그 기기에 붙는다.
        var nodeAt = new Dictionary<Vector2Int, Connector>();

        foreach (WireRun run in _wires)
        {
            if (!Intact(run)) continue;
            Connector prev = null;

            foreach (Vector2 p in run.local)
            {
                var key = Vector2Int.RoundToInt(p * Ballistics.SubGrid);

                if (!nodeAt.TryGetValue(key, out Connector node))
                {
                    nodeAt[key] = node = new Connector(p, 1f);
                    if (deviceAt.TryGetValue(_map.ToCell(p), out Connector dev))
                        Link(node, dev);
                }

                if (prev != null) Link(prev, node);
                prev = node;
            }
        }

        var reached = new HashSet<Connector>();
        foreach (Connector s in sources)
            reached.UnionWith(s.Reach());

        foreach (KeyValuePair<Vector2Int, Connector> pair in deviceAt)
            if (reached.Contains(pair.Value))
                _powered.Add(pair.Key);
    }
}
