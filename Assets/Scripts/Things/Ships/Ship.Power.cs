using System.Collections.Generic;
using Core;
using UnityEngine;

/// <summary>
/// 배 한 척의 전기. 설계도의 전선(<see cref="ShipDef.wires"/>)을 배 로컬로 옮겨 (판, 서브셀)로
/// 래스터하고, 기기(원자로·포탑)와 전선 꼭짓점을 <see cref="PowerGraph"/> 노드로 세워 푼다.
/// 소비자는 이 그래프를 직접 안 읽는다 - <see cref="Voltage"/> 하나다. Docs/Electrical-Design.md.
/// </summary>
public partial class Ship
{
    private ShipDef _design;
    private bool _hasWiring;

    private readonly PowerGraph _grid = new();
    private readonly Dictionary<Object, int> _nodeOf = new();        // 기기 → 노드
    private readonly Dictionary<Vector2Int, int> _deviceAt = new();  // 기기 칸 → 노드 (재빌드 스크래치)
    private readonly Dictionary<Vector2Int, int> _nodeAt = new();    // 꼭짓점 키 → 노드 (재빌드 스크래치)

    private bool _powerDirty = true;
    private int _deviceSignature = -1;
    private int _cutSegments = -1;
    private int _gunsFed = -1;

    /// <summary>풀 때마다 오른다. 오버레이·HUD가 이걸로 "다시 읽을 때"를 안다.</summary>
    public int PowerVersion { get; private set; }

    public enum WireState { Cut, Dark, Live }

    /// <summary>끊긴 이유. 검사 패널이 말로 바꾼다.</summary>
    public enum CutCause { None, Burned, Holed, PlateGone, PlateLeft, Breached }

    public struct WireSeg
    {
        public int wire, seg;
        public Vector2 a, b;   // 배 로컬
        public WireState state;
        public CutCause cause;
        public float amps;     // 이 구간 전류
        public float kelvin;   // 주변 대비 온도
        public float ohms;
        public int subs;       // 지나는 (판, 서브셀) 수. 0이면 공기라 못 끊긴다
        public bool burned, holed;
    }

    /// <summary>기기의 풀이 결과. 그래프에 없으면(전선 없는 설계·죽은 기기) false.</summary>
    public bool TryGetReadout(Component module, out float volts, out float amps)
    {
        volts = 0f; amps = 0f;
        if (module == null || !_nodeOf.TryGetValue(module, out int n)) return false;
        volts = _grid.v[n]; amps = _grid.i[n];
        return true;
    }

    /// <summary>배 로컬 한 점에서 radius 안의 제일 가까운 전선 구간. 검사 클릭용.</summary>
    public bool TryPickWire(Vector2 local, float radius, out int wire, out int seg)
    {
        wire = -1; seg = -1;
        float best = radius * radius;

        for (int w = 0; w < _wires.Count; w++)
        {
            WireRun run = _wires[w];
            for (int s = 0; s < run.length.Length; s++)
            {
                Vector2 a = run.local[s], b = run.local[s + 1], ab = b - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(local - a, ab) / ab.sqrMagnitude);
                float d = (local - (a + ab * t)).sqrMagnitude;
                if (d < best) { best = d; wire = w; seg = s; }
            }
        }

        return wire >= 0;
    }

    /// <summary>검사 패널용. 구간 하나의 전부. 그래프 간선이 아니어도(같은 노드로 접힌 0 길이) 기본값을 준다.</summary>
    public WireSeg Segment(int wire, int seg)
    {
        var r = new WireSeg { wire = wire, seg = seg };
        if (wire < 0 || wire >= _wires.Count) return r;
        WireRun run = _wires[wire];
        if (seg < 0 || seg >= run.length.Length) return r;

        r.a = run.local[seg]; r.b = run.local[seg + 1];
        r.ohms = run.length[seg] * Ballistics.WireOhmPerMetre;
        r.kelvin = run.temp[seg];
        r.burned = run.burned[seg];
        r.holed = run.holed[seg];
        r.subs = run.subs[seg].Count;
        r.cause = Cause(run, seg);
        r.state = r.cause != CutCause.None ? WireState.Cut : WireState.Dark;

        for (int e = 0; e < _grid.edgeCount; e++)
        {
            if (_grid.ewire[e] != wire || _grid.eseg[e] != seg) continue;
            r.amps = _grid.ei[e];
            float v = Mathf.Max(_grid.v[_grid.ea[e]], _grid.v[_grid.eb[e]]);
            if (_grid.ealive[e]) r.state = v >= Ballistics.PowerNominal * Ballistics.PowerBrownout ? WireState.Live : WireState.Dark;
            break;
        }

        return r;
    }

    private CutCause Cause(WireRun run, int s)
    {
        if (run.burned[s]) return CutCause.Burned;
        if (run.holed[s]) return CutCause.Holed;

        foreach ((Armor plate, int sub) in run.subs[s])
        {
            if (plate == null) return CutCause.PlateGone;
            if (!StillAboard(plate, this)) return CutCause.PlateLeft;
            if (plate.IsBreached(sub)) return CutCause.Breached;
        }

        return CutCause.None;
    }

    // 전선 하나 = 폴리라인(배 로컬 m) + 구간마다 지나는 (판, 서브셀)·온도·끊김.
    // 구간 단위인 이유: 한 곳이 뚫리면 그 구간만 빠지고 앞쪽 부하는 산다.
    // 래스터는 **지을 때 한 번**이다. 판 참조가 스스로 죽는다(파괴 → null, 잔해로 이탈 → StillAboard).
    // 파단 뒤에 다시 굽으면 죽은 판 자리가 "판 없음 = 공기"로 읽혀 끊긴 전선이 도로 붙는다.
    private class WireRun
    {
        public Vector2[] local;
        public float[] length;
        public List<(Armor plate, int sub)>[] subs;
        public float[] temp;      // 주변 대비 K
        public bool[] burned;     // 열로 탔다. 정비가 되돌린다
        public bool[] holed;      // 설계엔 판이 있는데 지을 때 없었다(손상 저장본의 구멍). 영구
    }

    private readonly List<WireRun> _wires = new();
    private Dictionary<Vector2Int, Armor> _armorAt = new();

    /// <summary>이 모듈에 오는 전압(L-N). 전선 없는 설계는 <see cref="HasPower"/>면 정격.</summary>
    public float Voltage(Component module)
    {
        if (!HasPower) return 0f;
        if (!_hasWiring || _map == null) return Ballistics.PowerNominal;
        return module != null && _nodeOf.TryGetValue(module, out int n) ? _grid.v[n] : 0f;
    }

    public bool Powered(Component module)
        => Voltage(module) >= Ballistics.PowerNominal * Ballistics.PowerBrownout;

    /// <summary>전원 노드 중 제일 높은 전압 - 원자로 단자의 값이지 부하가 보는 값이 아니다. HUD용.</summary>
    public float BusVoltage
    {
        get
        {
            if (!HasPower) return 0f;
            if (!_hasWiring) return Ballistics.PowerNominal;
            float best = 0f;
            for (int k = 0; k < _grid.nodeCount; k++)
                if (_grid.kind[k] == PowerGraph.Kind.Source && _grid.v[k] > best) best = _grid.v[k];
            return best;
        }
    }

    public bool HasWiring => _hasWiring;

    /// <summary>원자로에서 나가는 전류 합(A). HUD용.</summary>
    public float GridAmps
    {
        get
        {
            float sum = 0f;
            for (int k = 0; k < _grid.nodeCount; k++)
                if (_grid.isRoot[k]) sum += _grid.i[k];   // 약한 전원의 i는 뿌리에 이미 들어 있다
            return sum;
        }
    }

    public int WireSegmentsTotal => _grid.edgeCount;

    public int WireSegmentsAlive
    {
        get
        {
            int n = 0;
            for (int e = 0; e < _grid.edgeCount; e++) if (_grid.ealive[e]) n++;
            return n;
        }
    }

    /// <summary>전기가 오는 포탑 수 / 살아 있는 포탑 수.</summary>
    public int GunsFed
    {
        get
        {
            int n = 0;
            for (int k = 0; k < shipGuns.Count; k++)
                if (shipGuns[k] != null && !shipGuns[k].Neutralized && StillAboard(shipGuns[k], this) && Powered(shipGuns[k])) n++;
            return n;
        }
    }

    public int GunsTotal
    {
        get
        {
            int n = 0;
            for (int k = 0; k < shipGuns.Count; k++)
                if (shipGuns[k] != null && !shipGuns[k].Neutralized && StillAboard(shipGuns[k], this)) n++;
            return n;
        }
    }

    /// <summary>오버레이용. 간선마다 배 로컬 두 점과 상태. 끊김 / 살았지만 0 V / 급전. 전류·온도도 같이.</summary>
    public void CollectWireSegments(List<WireSeg> into)
    {
        into.Clear();
        float live = Ballistics.PowerNominal * Ballistics.PowerBrownout;

        for (int e = 0; e < _grid.edgeCount; e++)
        {
            WireRun run = _wires[_grid.ewire[e]];
            int s = _grid.eseg[e];
            float v = Mathf.Max(_grid.v[_grid.ea[e]], _grid.v[_grid.eb[e]]);

            into.Add(new WireSeg
            {
                wire = _grid.ewire[e],
                seg = s,
                a = run.local[s],
                b = run.local[s + 1],
                state = !_grid.ealive[e] ? WireState.Cut : v >= live ? WireState.Live : WireState.Dark,
                amps = _grid.ei[e],
                kelvin = run.temp[s],
                ohms = _grid.er[e],
                subs = run.subs[s].Count,
                burned = run.burned[s],
                holed = run.holed[s],
            });
        }
    }

    public struct DeviceReadout
    {
        public Component device;
        public PowerGraph.Kind kind;
        public float v, i;   // 노드 전압, 들어오는 전류(전원은 나가는 전류)
    }

    /// <summary>속보기 라벨용. 기기마다 전압·전류.</summary>
    public void CollectDeviceReadouts(List<DeviceReadout> into)
    {
        into.Clear();

        foreach (KeyValuePair<Object, int> pair in _nodeOf)
        {
            var c = pair.Key as Component;
            if (c == null) continue;
            int n = pair.Value;
            into.Add(new DeviceReadout { device = c, kind = _grid.kind[n], v = _grid.v[n], i = _grid.i[n] });
        }
    }

    /// <summary>정비. 탄 전선을 되돌린다. 구멍(holed)은 판이 안 돌아오므로 그대로다.</summary>
    public void RepairWires()
    {
        foreach (WireRun run in _wires)
        {
            System.Array.Clear(run.burned, 0, run.burned.Length);
            System.Array.Clear(run.temp, 0, run.temp.Length);
        }

        _powerDirty = true;
    }

    // 기기 칸 = 원점 칸(localPosition). 콜라이더 offset은 더하지 않는다 - 페인터가 전선 끝을
    // 원점 칸 중심에 붙이므로, 중심 칸(KillModulesOn이 보는 것)으로 잡으면 offset 있는 포에 안 붙는다.
    // 모듈은 판의 자식이라 localPosition은 판 기준이고, 그래서 월드에서 배 로컬로 되돌린다.
    private Vector2Int CellOf(Transform module)
        => _map.ToCell(transform.InverseTransformPoint(module.position));

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

            int segs = w.points.Count - 1;
            var run = new WireRun
            {
                local = new Vector2[w.points.Count],
                length = new float[segs],
                subs = new List<(Armor, int)>[segs],
                temp = new float[segs],
                burned = new bool[segs],
                holed = new bool[segs],
            };

            for (int i = 0; i < w.points.Count; i++)
                run.local[i] = new Vector2(
                    authored.origin.x + (w.points[i].x - mins.x) * ShipGrid.CellSize,
                    authored.origin.y - (w.points[i].y - mins.y) * ShipGrid.CellSize);

            for (int s = 0; s < segs; s++)
            {
                run.length[s] = (run.local[s + 1] - run.local[s]).magnitude;
                run.subs[s] = new List<(Armor, int)>();
            }

            _wires.Add(run);
        }

        RasterizeWires();
        _powerDirty = true;
    }

    // ponytail: 반 서브셀 간격 샘플링. DDA(Ballistics.March)가 정확하지만 빌드 때 한 번이라 이걸로 충분하다.
    // "판 없음"이 공기인지 구멍인지는 설계도(DesignMap)가 가른다 - 손상 저장본은 죽은 판이 배치에서 빠져 있다.
    private void RasterizeWires()
    {
        const float step = ShipGrid.CellSize / Ballistics.SubGrid * 0.5f;
        if (_map == null) return;

        foreach (WireRun run in _wires)
        {
            for (int s = 0; s < run.subs.Length; s++)
            {
                List<(Armor plate, int sub)> list = run.subs[s];
                list.Clear();

                Vector2 a = run.local[s], b = run.local[s + 1];
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / step));
                Armor lastPlate = null;
                int lastSub = -1;

                for (int k = 0; k <= n; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, (float)k / n);

                    if (!_armorAt.TryGetValue(_map.ToCell(p), out Armor plate) || plate == null)
                    {
                        if (DesignSaysPlate(p)) run.holed[s] = true;
                        continue;
                    }

                    int sub = plate.SubIndexAtLocal(plate.transform.InverseTransformPoint(transform.TransformPoint(p)));
                    if (sub < 0 || sub >= Armor.SubCount || (plate == lastPlate && sub == lastSub)) continue;

                    list.Add((plate, sub));
                    lastPlate = plate;
                    lastSub = sub;
                }
            }
        }
    }

    private bool DesignSaysPlate(Vector2 local)
    {
        if (DesignMap == null) return false;
        Vector2Int c = DesignMap.ToCell(local);
        return DesignMap.Inside(c) && ShipGrid.Solid(DesignMap.cells[c.x, c.y]);
    }

    private bool SegmentIntact(WireRun run, int s)
    {
        if (run.burned[s] || run.holed[s]) return false;

        foreach ((Armor plate, int sub) in run.subs[s])
            if (plate == null || !StillAboard(plate, this) || plate.IsBreached(sub))
                return false;

        return true;
    }

    /// <summary>
    /// PowerInterval 틱마다. 기기 집합이 바뀌었으면(원자로 생사·포탑 생사·파단·정비) 그래프를 다시 세우고,
    /// 매번 간선 생사를 다시 읽고(관통은 BreachVersion에 첫 파공만 잡힌다), 풀고, 전선을 데운다.
    /// </summary>
    private void SolvePower()
    {
        if (!_hasWiring || _map == null) return;

        int sig = 0;
        for (int k = 0; k < shipCriticals.Count; k++)
        {
            CriticalModule c = shipCriticals[k];
            if (c != null && c.providesPower && !c.Neutralized && StillAboard(c, this)) sig += 1000;
        }
        for (int k = 0; k < shipGuns.Count; k++)
        {
            Gun g = shipGuns[k];
            if (g != null && !g.Neutralized && StillAboard(g, this)) sig += 1;
        }

        if (_powerDirty || sig != _deviceSignature)
        {
            _deviceSignature = sig;
            RebuildPowerNet();
        }

        int cut = 0;
        for (int e = 0; e < _grid.edgeCount; e++)
        {
            _grid.ealive[e] = SegmentIntact(_wires[_grid.ewire[e]], _grid.eseg[e]);
            if (!_grid.ealive[e]) cut++;
        }

        // M3: 포탑 모터. 도는 중이면 (Ra, 역기전력 ke·ω), 서 있으면 열린 회로(전류 0). 기동 순간이 전류 최대다.
        for (int k = 0; k < shipGuns.Count; k++)
        {
            Gun g = shipGuns[k];
            if (g == null || !_nodeOf.TryGetValue(g, out int n)) continue;
            _grid.rInt[n] = g.MotorOn ? g.ArmatureOhms : 0f;
            _grid.emf[n] = g.MotorOn ? g.BackEmf : 0f;
        }

        _grid.Solve();
        HeatWires(TickManager.TickDeltaTime * Ballistics.PowerInterval);
        PowerVersion++;

        // 끊긴 구간이 늘었을 때만 기록한다 - 사건은 전이지 상태가 아니다(WatchForCritical과 같은 이유).
        // 첫 풀이(_cutSegments < 0)는 기준선이라 안 적는다: 손상 저장본의 구멍이 "방금 끊김"이 되면 안 된다.
        int fed = GunsFed;
        if (_cutSegments >= 0 && cut > _cutSegments)
        {
            int lost = Mathf.Max(0, _gunsFed - fed);
            RunLog.WireCut(this, lost);

            // 피탄 판독 줄에 같이 - 관통 글자 옆에 "그래서 포 둘이 죽었다"가 붙어야 원인과 결과가 한 자리다.
            if (IsPlayerControlled)
                HitReadout.Push(lost > 0 ? $"WIRE CUT  -{lost} GUN" : "WIRE CUT", incoming: true, minor: lost == 0);
        }
        _cutSegments = cut;
        _gunsFed = fed;
    }

    private void RebuildPowerNet()
    {
        _powerDirty = false;
        _grid.Clear();
        _nodeOf.Clear();
        _deviceAt.Clear();
        _nodeAt.Clear();

        // 기기 = 원자로(전원)·포탑(부하). 릴레이·퓨즈가 오면 여기가 늘어난다. 죽은 것은 노드가 아니다.
        for (int k = 0; k < shipCriticals.Count; k++)
        {
            CriticalModule c = shipCriticals[k];
            if (c == null || !c.providesPower || c.Neutralized || !StillAboard(c, this)) continue;
            AddDevice(c, PowerGraph.Kind.Source, c.PhaseVoltage, c.sourceResistance);
        }

        for (int k = 0; k < shipGuns.Count; k++)
        {
            Gun g = shipGuns[k];
            if (g == null || g.Neutralized || !StillAboard(g, this)) continue;
            float r = g.powerWatts > 0f ? Ballistics.PowerNominal * Ballistics.PowerNominal / g.powerWatts : 0f;
            AddDevice(g, PowerGraph.Kind.Load, 0f, r);
        }

        // 전선 꼭짓점 = 노드. 같은 자리(1/6 m 눈금)는 같은 노드라 전선끼리 거기서 이어진다.
        // 꼭짓점이 기기 칸 **중심**이면 그 기기 노드 자체다 - 칸 경계에 걸친 꼭짓점을 반올림으로
        // 옆 기기에 붙이지 않으려고 중심 거리(반 서브셀)로 본다. 페인터가 같은 규칙으로 검사한다.
        const float snap = ShipGrid.CellSize / Ballistics.SubGrid * 0.5f;

        for (int w = 0; w < _wires.Count; w++)
        {
            WireRun run = _wires[w];
            int prev = -1;

            for (int p = 0; p < run.local.Length; p++)
            {
                Vector2 at = run.local[p];
                var key = Vector2Int.RoundToInt(at * Ballistics.SubGrid);

                if (!_nodeAt.TryGetValue(key, out int node))
                {
                    Vector2Int cell = _map.ToCell(at);
                    node = _deviceAt.TryGetValue(cell, out int dev)
                           && (at - _map.ToLocal(cell.x, cell.y)).sqrMagnitude <= snap * snap
                        ? dev
                        : _grid.AddNode(PowerGraph.Kind.Junction, 0f, 0f, cell);
                    _nodeAt[key] = node;
                }

                if (p > 0 && prev != node)
                {
                    int s = p - 1;
                    _grid.AddEdge(prev, node, run.length[s] * Ballistics.WireOhmPerMetre, w, s, SegmentIntact(run, s));
                }

                prev = node;
            }
        }
    }

    private void AddDevice(Thing t, PowerGraph.Kind kind, float emf, float r)
    {
        Vector2Int cell = CellOf(t.transform);
        int node = _grid.AddNode(kind, emf, r, cell);
        _nodeOf[t] = node;

        // 한 칸에 둘이면 먼저 온 것이 전선을 받는다. 페인터가 칸당 모듈 하나라 실제로는 안 겹친다.
        if (!_deviceAt.ContainsKey(cell))
            _deviceAt[cell] = node;
    }

    /// <summary>
    /// 구간마다 I²R을 넣고 식힌다. 문턱을 넘으면 그 구간이 타서 끊기고, 판 위였으면 그 서브셀을
    /// 조용히 죽인다(<see cref="Armor.Burn"/> - 관통 소리·파편·기록 없이). 단락이 왜 위험한가가
    /// 여기서 나온다: 6 kA × 0.02 Ω = 720 kW.
    /// </summary>
    private void HeatWires(float dt)
    {
        for (int e = 0; e < _grid.edgeCount; e++)
        {
            WireRun run = _wires[_grid.ewire[e]];
            int s = _grid.eseg[e];
            float len = Mathf.Max(0.01f, run.length[s]);

            float watts = _grid.ei[e] * _grid.ei[e] * _grid.er[e];
            float t = run.temp[s];
            t += watts * dt / (Ballistics.WireHeatCapacity * len);
            t -= t * Ballistics.WireCoolPerSecond * dt;
            run.temp[s] = t;

            if (t < Ballistics.WireBurnKelvin || run.burned[s]) continue;

            run.burned[s] = true;
            run.temp[s] = 0f;

            if (run.subs[s].Count > 0)
            {
                (Armor plate, int sub) = run.subs[s][0];
                if (plate != null && StillAboard(plate, this)) plate.Burn(sub);
            }
        }
    }
}
