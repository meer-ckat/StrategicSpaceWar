using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 격파 X-ray의 자료. 죽을 때 한 번, 어느 판이 언제 죽었고 시타델이 어디였는지.
///
/// 워썬더 킬 카메라의 명시 목적이 "데미지 모델 교육"이다. 이 게임의 시뮬은 결정론인데
/// 플레이어 눈엔 운으로 보였다 - 시타델·후면·관통 여부를 모른 채 결과만 봐서. 죽음이
/// 아무것도 안 가르치면 로그라이크 학습 루프의 절반이 비어 있다.
///
/// **배가 사라진 뒤에도 그려야 한다.** 유폭 즉사는 GameObject가 먼저 없어지므로, 설계도와
/// 시타델은 살아 있는 동안 <see cref="Observe"/>가 들고 있고 죽은 판은 <see cref="PlateLost"/>가
/// 쌓는다. 그리는 쪽(ShipStatusHud)은 여기만 읽는다.
/// </summary>
public static class DeathXray
{
    public struct Lost
    {
        public Vector2Int cell;   // 설계도 칸
        public long tick;
    }

    public static ShipGrid.Map Design { get; private set; }

    /// <summary>
    /// 면별 대표 방호력(mm RHA, 수직). 바깥 껍질의 **중앙값**이다 - 평균은 유리창 한 장이나
    /// Lance Armor 한 장이 통째로 끌어내리거나 올린다. 설계 시점 값이라 전투 중에 안 바뀐다:
    /// "이 배가 원래 얼마나 단단한가"를 묻는 값이고, 지금 얼마나 남았나는 X-ray 그림이 답한다.
    /// </summary>
    public static float ArmorBow { get; private set; }
    public static float ArmorSide { get; private set; }
    public static float ArmorStern { get; private set; }
    public static readonly List<Vector2Int> Citadel = new();

    /// <summary>
    /// 판의 **콜라이더 발자국**(격자 좌표 폴리곤)과 그 판의 설계 칸. 격자는 1×1인데 탄은 콜라이더에
    /// 맞는다 - 2×2 Lance Armor·경사판은 칸 밖으로 한 칸 넘게 나와 있어서, 칸만 그리면 탄이 허공에서
    /// 멈추고 파편이 선체 밖에서 터지는 그림이 된다("오프셋"으로 보였던 것, 2026-09-13).
    /// 페인터가 쓰는 ModulePlacement.PlatePolygon 그대로다.
    /// </summary>
    public static readonly List<(Vector2Int cell, Vector2[] poly)> Footprints = new();
    public static readonly List<Lost> LostPlates = new();
    public static long DownTick { get; private set; } = -1;
    public static string Cause { get; private set; } = "";

    /// <summary>
    /// 한 사건 = 같은 순간에 죽은 판 묶음. 유폭 한 방이 판 40장을 같은 틱에 죽이는데, 그걸 40개로
    /// 세면 "무엇이 먼저였나"가 그 40장에 묻힌다 - 관통 한 발이 판 하나를 죽이고, 그 다음 유폭이
    /// 40장을 죽인 것이 두 사건이다.
    /// </summary>
    public struct Group
    {
        public long tick;
        public List<Vector2Int> cells;
        public string tag;   // 그 순간의 RunLog 사건. "유폭" "절단" "관통". 없으면 ""
        public string shell; // 그 순간 맞은 탄의 이름. 없으면 null(유폭 연쇄처럼 탄이 없는 사건)
    }

    /// <summary>마지막 <see cref="MaxGroups"/>개. 0이 제일 오래된 것 - 재생 순서다.</summary>
    public static readonly List<Group> Groups = new();

    public const int MaxGroups = 10;

    /// <summary>이 틱 안이면 같은 사건. 유폭의 붕괴 연쇄가 몇 틱에 걸쳐 흐를 수 있다.</summary>
    private const int GroupTicks = 3;

    private static Ship _watched;

    // ---- 탄과 파편. 워썬더 X-ray의 그 선들 ----
    //
    // 재시뮬이 아니라 **기록**이다. 결정론은 이 기록이 사건과 정확히 같다는 보장이지 다시 돌릴
    // 이유가 아니다. SpallTrails.Add가 모든 선(탄 구간·파편)의 단일 깔때기라 거기서 받고, 배가
    // 움직이므로 받는 순간 배 로컬 → 설계도 격자 좌표로 바꿔 둔다.

    public struct Trail
    {
        public long tick;
        public Vector2 a, b;          // 설계도 격자 좌표(연속). 칸 (c,r)의 중심이 (c,r)
        public SpallTrails.Kind kind;
        public int id;                // 탄이면 ProjectileId. 구간들을 이어 한 발의 궤적이 된다
        public bool frag;             // 실체 파편(generation > 0)의 탄. 궤적은 같은 규칙, 굵기만 다르다
    }

    public struct Hit
    {
        public long tick;
        public Vector2 at;            // 격자 좌표
        public HitOutcome outcome;
        public bool ram;              // 탄이 아니라 충각. outcome은 무시
        public int id;                // 어느 탄의 판정인가
        public string shell;          // 탄의 defName. "105mm". 사건 줄이 "무엇에" 맞았는지를 말한다
        public bool frag;             // 실체 파편(generation > 0)의 판정. 주포 한 발과 같은 무게로 읽히면 안 된다
        public float pen;             // 맞는 순간의 관통력(mm RHA). 우리 장갑과 견주는 유일한 값
    }

    /// <summary>링. 유폭은 파편이 수천이라 작으면 제일 큰 사건이 제일 안 남는다.</summary>
    private const int TrailCapacity = 8192;
    private static readonly Trail[] _trails = new Trail[TrailCapacity];
    private static int _trailNext, _trailCount;

    public static readonly List<Hit> Hits = new();
    private const int HitCapacity = 64;

    /// <summary>
    /// 폭발 하나. 탄약고 유폭도 작약도 충각 유폭도 전부 <see cref="RamImpact.Detonate"/> 한
    /// 문으로 가므로 여기 한 자리에서 받는다 - "무엇이 터졌나"는 안 적는다. 반경은
    /// <see cref="Ballistics.BlastRadiusFor"/>가 세기에서 낸다 - 그릴 때 필요한 것은 자리와 세기뿐이다.
    /// </summary>
    public struct Blast
    {
        public long tick;
        public Vector2 at;      // 격자 좌표
        public float damage;
    }

    public static readonly List<Blast> Blasts = new();
    private const int BlastCapacity = 64;

    /// <summary>폭발 하나. RamImpact.Detonate가 부른다 - 폭심이 내 격자 근처일 때만 남는다.</summary>
    public static void AddBlast(Vector2 world, float damage)
    {
        if (!Frame() || damage <= 0f)
            return;

        Vector2 g = ToGrid(world);

        if (!NearGrid(g, 8f))
            return;

        if (Blasts.Count >= BlastCapacity)
            Blasts.RemoveAt(0);

        Blasts.Add(new Blast { tick = _frameTick, at = g, damage = damage });
    }

    /// <summary>격자 밖 이만큼까지는 남긴다. 파편은 6칸이면 되지만 **탄은 멀리서부터** - 800 m/s면 틱당 13 m라
    /// 6칸 안에는 구간 하나가 채 안 들어와서 어디서 왔는지 안 읽혔다("이상한 위치에서 날아온다").</summary>
    private const float GridMargin = 6f;
    private const float ShellMargin = 45f;

    private static long _frameTick = -1;

    /// <summary>Physics2D.Simulate 직후. 배가 움직였으니 다음 기록은 행렬을 다시 읽는다.</summary>
    public static void PhysicsStepped() => _frameTick = -1;
    private static Matrix4x4 _worldToLocal;
    private static Vector2 _shipPos;
    private static float _shipRadius;

    /// <summary>틱당 한 번만 배의 행렬을 읽는다. 파편 수천 개가 한 틱에 오는데 그때마다 네이티브를 부르면 안 된다.</summary>
    private static bool Frame()
    {
        if (_watched == null || Design == null)
            return false;

        if (_frameTick != Core.TickManager.currentTick)
        {
            _frameTick = Core.TickManager.currentTick;
            _worldToLocal = _watched.transform.worldToLocalMatrix;
            _shipPos = _watched.transform.position;
            _shipRadius = Mathf.Max(Design.width, Design.height) * ShipGrid.CellSize * 0.5f + ShellMargin;
        }

        return true;
    }

    private static Vector2 ToGrid(Vector2 world)
    {
        Vector2 local = _worldToLocal.MultiplyPoint3x4(world);
        return new Vector2((local.x - Design.origin.x) / ShipGrid.CellSize, (Design.origin.y - local.y) / ShipGrid.CellSize);
    }

    private static bool NearGrid(Vector2 g, float margin) =>
        g.x >= -margin && g.y >= -margin && g.x <= Design.width + margin && g.y <= Design.height + margin;

    /// <summary>선 하나. 배 근처가 아니면 버린다 - 5 km 밖 남의 싸움은 기록할 것이 아니다.</summary>
    public static void AddTrail(Vector2 fromWorld, Vector2 toWorld, SpallTrails.Kind kind, int id = 0, int generation = 0)
    {
        if (!Frame())
            return;

        // 실체 파편(generation > 0)도 Projectile이라 틱마다 Shell 구간을 낸다. 예전에는 이것을
        // Vent(공기 새는 김)로 접었는데, 그 탄이 판을 맞히면 **판정 점은 찍히고 탄은 안 보이는**
        // 그림이 됐다 - 회색 가는 선이 알파 0.5라 지난 사건에서는 사실상 사라진다. 파편도 탄이다:
        // 같은 궤적 규칙으로 이어 그리고 굵기와 색만 낮춘다.
        bool frag = kind == SpallTrails.Kind.Shell && generation > 0;

        // 거친 거름망은 월드 거리로. 행렬 곱 전에 대부분이 여기서 빠진다.
        float r = _shipRadius;
        if ((fromWorld - _shipPos).sqrMagnitude > r * r && (toWorld - _shipPos).sqrMagnitude > r * r)
            return;

        // 빗나간 파편은 안 적는다. 최대 사거리까지 날아가는 선 수천 개가 원을 그려 배를 덮었다 -
        // 정보가 아니라 소음이고, 판을 맞힌 파편만이 "왜 이 판이 죽었나"에 답한다.
        if (kind == SpallTrails.Kind.Miss)
            return;

        Vector2 a = ToGrid(fromWorld), b = ToGrid(toWorld);

        // 탄은 멀리서부터, 파편은 **내 격자 안에 닿은 것만** - 격자 밖 판은 남의 배다.
        if (kind == SpallTrails.Kind.Shell ? !NearGrid(a, ShellMargin) && !NearGrid(b, ShellMargin) : !NearGrid(b, 1f))
            return;

        _trails[_trailNext] = new Trail { tick = _frameTick, a = a, b = b, kind = kind, id = id, frag = frag };
        _trailNext = (_trailNext + 1) % TrailCapacity;
        if (_trailCount < TrailCapacity) _trailCount++;
    }

    /// <summary>명중 판정 하나. Projectile.Damage.Apply가 플레이어 배일 때 부른다.</summary>
    public static void AddHit(Vector2 world, HitOutcome outcome, int id = 0, string shell = null, bool frag = false, float pen = 0f)
    {
        if (!Frame())
            return;

        if (Hits.Count >= HitCapacity)
            Hits.RemoveAt(0);

        Hits.Add(new Hit { tick = _frameTick, at = ToGrid(world), outcome = outcome, id = id, shell = shell, frag = frag, pen = pen });
    }

    /// <summary>충각으로 갈린 자리. 매 틱 접촉마다 오므로 같은 틱·같은 칸은 하나로 접는다.</summary>
    public static void AddRam(Vector2 world)
    {
        if (!Frame())
            return;

        Vector2 g = ToGrid(world);

        for (int i = Hits.Count - 1; i >= 0 && Hits[i].tick == _frameTick; i--)
            if (Hits[i].ram && (Hits[i].at - g).sqrMagnitude < 0.25f)
                return;

        if (Hits.Count >= HitCapacity)
            Hits.RemoveAt(0);

        Hits.Add(new Hit { tick = _frameTick, at = g, ram = true });
    }

    /// <summary>오래된 것부터. 그리는 쪽이 틱으로 거른다.</summary>
    public static void ForEachTrail(System.Action<Trail> visit)
    {
        int start = (_trailNext - _trailCount + TrailCapacity) % TrailCapacity;

        for (int i = 0; i < _trailCount; i++)
            visit(_trails[(start + i) % TrailCapacity]);
    }

    /// <summary>매 프레임. 설계도가 바뀐 배(새 런, 새 구역 재건조)만 다시 읽는다 - 나머지 프레임은 참조 비교 하나.</summary>
    public static void Observe(Ship player)
    {
        if (player == null || player.DesignMap == null)
            return;

        if (player == _watched && player.DesignMap == Design)
            return;

        if (player != _watched)
            LostPlates.Clear();   // 다른 배다. 지난 배의 죽음을 물려주지 않는다

        _watched = player;
        Design = player.DesignMap;
        Citadel.Clear();
        BuildFootprints(player);
        BuildFacingArmor(player);

        foreach (CriticalModule m in player.shipCriticals)
        {
            if (m == null || !Ship.StillAboard(m, player))
                continue;

            Citadel.Add(Design.ToCell(player.transform.InverseTransformPoint(m.transform.position)));
        }
    }

    /// <summary>
    /// 바깥 껍질을 면마다 모아 중앙값을 낸다. 기수가 +X(col 증가), 좌현이 +Y(row 감소)라
    /// 전면 = 각 행의 최대 col, 후면 = 최소 col, 측면 = 각 열의 최소·최대 row다.
    /// </summary>
    private static void BuildFacingArmor(Ship player)
    {
        ArmorBow = ArmorSide = ArmorStern = 0f;

        if (player.shipArmors == null || player.shipArmors.Count == 0 || Design == null)
            return;

        var bowOf = new Dictionary<int, (int col, float rha)>();
        var sternOf = new Dictionary<int, (int col, float rha)>();
        var portOf = new Dictionary<int, (int row, float rha)>();
        var starboardOf = new Dictionary<int, (int row, float rha)>();

        foreach (Armor plate in player.shipArmors)
        {
            if (plate == null)
                continue;

            Vector2Int c = Design.ToCell(plate.transform.localPosition);

            if (!Design.Inside(c))
                continue;

            float rha = plate.RHA;

            if (!bowOf.TryGetValue(c.y, out var b) || c.x > b.col) bowOf[c.y] = (c.x, rha);
            if (!sternOf.TryGetValue(c.y, out var s) || c.x < s.col) sternOf[c.y] = (c.x, rha);
            if (!portOf.TryGetValue(c.x, out var p) || c.y < p.row) portOf[c.x] = (c.y, rha);
            if (!starboardOf.TryGetValue(c.x, out var t) || c.y > t.row) starboardOf[c.x] = (c.y, rha);
        }

        var scratch = new List<float>();

        float Median(params Dictionary<int, (int, float)>[] sets)
        {
            scratch.Clear();

            foreach (var set in sets)
                foreach (var kv in set)
                    scratch.Add(kv.Value.Item2);

            if (scratch.Count == 0)
                return 0f;

            scratch.Sort();
            return scratch[scratch.Count / 2];
        }

        ArmorBow = Median(bowOf);
        ArmorStern = Median(sternOf);
        ArmorSide = Median(portOf, starboardOf);
    }

    private static void BuildFootprints(Ship player)
    {
        Footprints.Clear();

        ShipDef def = string.IsNullOrEmpty(player.shipDefName) ? null : ShipDef.Load(player.shipDefName);

        if (def == null || def.placements == null)
            return;

        Vector2Int mins = def.Bbox().min;

        foreach (Placement p in def.placements)
        {
            if (!ShipBuilder.StampsGrid(DefDatabase.Get(p.def), out _))
                continue;

            var cell = new Vector2Int(p.col - mins.x, p.row - mins.y);

            if (!Design.Inside(cell) || !ShipGrid.Solid(Design.cells[cell.x, cell.y]))
                continue;

            // PlatePolygon은 배 좌표(x = col, y = -row)다. 격자 좌표는 y만 뒤집는다.
            Vector2[] poly = ModulePlacement.PlatePolygon(cell, p.def, p.rot, p.size, p.offset, p.shape);
            var grid = new Vector2[poly.Length];

            for (int i = 0; i < poly.Length; i++)
                grid[i] = new Vector2(poly[i].x, -poly[i].y);

            Footprints.Add((cell, grid));
        }
    }

    /// <summary>판이 죽었다. HullStructure.ReportPlateLost가 부른다 - 플레이어 배만 적는다.</summary>
    public static void PlateLost(Ship ship, Vector2Int designCell)
    {
        if (ship == null || ship != _watched)
            return;

        LostPlates.Add(new Lost { cell = designCell, tick = Core.TickManager.currentTick });
    }

    /// <summary>
    /// 격파 확정. 원인 한 줄은 RunLog의 마지막 아군 사건에서 읽고, 없으면 **배 상태에서 직접**
    /// - IsCombatEffective가 거짓이 되는 조건 그대로(승무원·전원·추진제·무장/추진). "전투 불능"
    /// 한 마디는 아무것도 안 가르친다.
    /// </summary>
    public static void Capture(Ship player)
    {
        DownTick = Core.TickManager.currentTick;
        Cause = "전투 불능";

        IReadOnlyList<RunLog.Entry> log = RunLog.Entries;

        BuildGroups(log);


        if (player != null)
        {
            if (!player.CrewAlive) Cause = "승무원 전멸";
            else if (!player.HasPower) Cause = "전원 상실 - 원자로";
            else if (player.shipTanks.Count > 0 && player.AvailableDeltaV() <= 0f) Cause = "추진제 소진";
            else if (!player.HasUsableGun && player.AvailableThrust(true) <= 0f && player.AvailableThrust(false) <= 0f) Cause = "무장·추진 전멸";
            else if (!player.HasUsableGun) Cause = "무장 전멸";
            else Cause = "추진 전멸";
        }

        for (int i = log.Count - 1; i >= 0; i--)
        {
            RunLog.Entry e = log[i];

            if (e.team != Ship.Team.Ally)
                continue;

            switch (e.kind)
            {
                case RunLog.Kind.CrewLost: Cause = "승무원 전멸"; return;
                case RunLog.Kind.Detonated: Cause = e.what + " 유폭"; return;
                case RunLog.Kind.HullSplit: Cause = "선체 절단"; return;
                case RunLog.Kind.RoleLost: Cause = e.what + " 상실"; return;
            }
        }
    }

    private static void BuildGroups(IReadOnlyList<RunLog.Entry> log)
    {
        Groups.Clear();

        // 죽은 순서대로 쌓여 있다. 틱이 가까우면 같은 묶음.
        foreach (Lost l in LostPlates)
        {
            if (Groups.Count > 0 && l.tick - Groups[Groups.Count - 1].tick <= GroupTicks)
            {
                Groups[Groups.Count - 1].cells.Add(l.cell);
                continue;
            }

            Groups.Add(new Group { tick = l.tick, cells = new List<Vector2Int> { l.cell }, tag = "" });
        }

        if (Groups.Count > MaxGroups)
            Groups.RemoveRange(0, Groups.Count - MaxGroups);

        // 그 순간의 RunLog 사건을 묶음에 붙인다. 유폭이 같은 틱에 여러 모듈이어도 태그는 하나.
        for (int g = 0; g < Groups.Count; g++)
        {
            Group group = Groups[g];
            long from = group.tick - GroupTicks;
            long to = (g + 1 < Groups.Count ? Groups[g + 1].tick : long.MaxValue) - 1;

            bool det = false, split = false, pen = false, crew = false, role = false, ram = false;

            foreach (Hit hit in Hits)
            {
                if (hit.tick < from || hit.tick > to)
                    continue;

                if (hit.ram) ram = true;
                else if (group.shell == null && !hit.frag) group.shell = hit.shell;
            }

            for (int i = 0; i < log.Count; i++)
            {
                RunLog.Entry e = log[i];

                if (e.team != Ship.Team.Ally || e.tick < from || e.tick > to)
                    continue;

                switch (e.kind)
                {
                    case RunLog.Kind.Detonated: det = true; break;
                    case RunLog.Kind.HullSplit: split = true; break;
                    case RunLog.Kind.Penetrated: pen = true; break;
                    case RunLog.Kind.CrewLost: crew = true; break;
                    case RunLog.Kind.RoleLost: role = true; break;
                }
            }

            // 인과 순서로 잇는다. 관통이 탄약고를 때려 유폭이 나고 그것이 절단으로 - 한 순간에
            // 셋이 다 일어날 수 있고, "유폭"만 남기면 그 관통 한 발이 사라진다. 그 한 발이 원인이다.
            var tag = new System.Text.StringBuilder();
            if (ram) tag.Append("충각");
            if (pen) tag.Append(tag.Length > 0 ? " → 관통" : "관통");
            if (det) tag.Append(tag.Length > 0 ? " → 유폭" : "유폭");
            if (split) tag.Append(tag.Length > 0 ? " → 절단" : "절단");
            if (crew) tag.Append(tag.Length > 0 ? " → 승무원" : "승무원");
            if (role) tag.Append(tag.Length > 0 ? " → 상실" : "상실");
            group.tag = tag.ToString();
            Groups[g] = group;
        }
    }

    public static void Reset()
    {
        ShipStatusHud.XrayRewindReset();
        Groups.Clear();
        Hits.Clear();
        Blasts.Clear();
        _trailNext = _trailCount = 0;
        _frameTick = -1;
        _watched = null;
        Design = null;
        Citadel.Clear();
        Footprints.Clear();
        LostPlates.Clear();
        DownTick = -1;
        Cause = "";
    }
}
