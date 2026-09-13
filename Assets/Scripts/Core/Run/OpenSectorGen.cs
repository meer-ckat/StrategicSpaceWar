using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 소구역 템플릿 하나. **배 목록이 아니라 조합 규칙이다** - 고정 목록으로 두면 한 런에
/// 16개나 뽑는 소구역이 금세 같은 얼굴로 반복된다. 배가 26척 있으니 뽑기가 조합을 만든다.
/// </summary>
[Serializable]
public class OpenSectorTemplate
{
    public string id;

    /// <summary>Skirmish / Elite / Wreck / Depot. 모르는 값이면 Skirmish로 읽는다.</summary>
    public string kind = "Skirmish";

    /// <summary>난이도 등급. 장 번호가 클수록 높은 tier에서 뽑는다.</summary>
    public int tier;

    /// <summary>선택 화면에 뜨는 한 줄. 갈림길이 안 읽히면 그건 동전 던지기다.</summary>
    public string label;

    public int minShips = 2;
    public int maxShips = 3;

    /// <summary>뽑을 후보 설계도 이름.</summary>
    public string[] pool = Array.Empty<string>();

    /// <summary>도착만으로 주는 MTRL. 보급·기항 노드가 쓴다.</summary>
    public int materials;

    /// <summary>이 자리에 닿으면 주는 추진제(kN·s). 워프 한 번이 newship 기준 80만쯤이다.</summary>
    public int propellant;

    /// <summary>이 자리에 닿으면 주는 탄약(발).</summary>
    public int munitions;

    /// <summary>
    /// 신호 크기(m). 기항지 15 km, 보급 8 km, 초계 500 m - 큰 것은 멀리서 좌표가 잡히고 작은 것은 코앞에서만.
    /// 좌표가 잡히기 전엔 부채꼴이고 그 밝기가 signalSize / 거리다. 값이 곧 "얼마나 눈에 띄는가"다.
    /// </summary>
    public float signalSize = 500f;

    /// <summary>여기서 정비 화면이 열리는가. 수리를 살 수 있는 자리를 정하는 값.</summary>
    public bool refit;

    /// <summary>
    /// 속. 다른 템플릿의 id를 적으면 그 자리에 그 템플릿의 배가 **같이** 선다 - 잔해밭 속의
    /// 매복, 보급 부표 옆의 초계. 겉(이 템플릿)이 신호와 첫인상을 정하고 속이 진실이다.
    /// 비면 겉이 전부다.
    /// </summary>
    public string hidden = "";

    /// <summary>
    /// 뽑기 가중치. **이 값이 없으면 균등 추첨이고, 그러면 지도가 보급소 천지가 된다** -
    /// tier 0(잔해·보급·기항)은 언제나 후보인데 셋이고 tier가 맞는 전투는 한둘이라
    /// 정비 노드가 65%로 나왔다. 수리가 흔하면 영구 손상이 규칙이 아니라 장식이다.
    /// </summary>
    public int weight = 10;

    public OpenSectorKind Kind => kind switch
    {
        "Elite" => OpenSectorKind.Elite,
        "Wreck" => OpenSectorKind.Wreck,
        "Depot" => OpenSectorKind.Depot,
        "Port" => OpenSectorKind.Port,
        _ => OpenSectorKind.Skirmish,
    };
}

public enum OpenSectorKind { Skirmish, Elite, Wreck, Depot, Port }

[Serializable]
public class OpenSectorPool
{
    public string defName;
    public List<OpenSectorTemplate> templates = new();

    private static readonly string[] HeaderKeys = { "defName", "templates" };

    public static string Path =>
        System.IO.Path.Combine(Application.streamingAssetsPath, "Run", "opensectors.json");

    public static OpenSectorPool Load()
    {
        if (!File.Exists(Path))
        {
            Debug.LogError($"[OpenSector] {Path}가 없다.");
            return null;
        }

        string text = File.ReadAllText(Path);

        // 캠페인과 같은 검증을 탄다. JsonUtility가 모르는 키를 조용히 버리는 것은 여기서도
        // 똑같고, 증상은 "왜 이 템플릿만 안 뽑히지"가 된다.
        if (DefKeys.HasUnknown(text, "opensectors.json", HeaderKeys, typeof(OpenSectorPool)))
            return null;

        var pool = JsonUtility.FromJson<OpenSectorPool>(text);

        if (pool == null || pool.templates == null || pool.templates.Count == 0)
        {
            Debug.LogError("[OpenSector] 템플릿이 하나도 없다.");
            return null;
        }

        Validate(pool);

        if (pool.templates.Count == 0)
        {
            Debug.LogError("[OpenSector] 쓸 수 있는 템플릿이 하나도 안 남았다.");
            return null;
        }

        return pool;
    }

    /// <summary>
    /// 값이 미친 템플릿을 뺀다. **DefKeys는 오타 난 키를 잡지 빈 값을 못 잡는다** -
    /// <c>"pool": []</c> 하나면 Stamp가 빈 배열을 인덱싱해서 그 자리에서 죽고, 증상은
    /// 들판 생성 전체가 실패하는 것이라 어느 템플릿 탓인지 안 보인다. 고쳐서 싣지 않고
    /// 빼는 이유는 ThingDef.Validate와 같다 - 조용히 고치면 틀린 데이터가 그대로 남는다.
    /// </summary>
    private static void Validate(OpenSectorPool pool)
    {
        var ids = new HashSet<string>();

        for (int i = pool.templates.Count - 1; i >= 0; i--)
        {
            OpenSectorTemplate t = pool.templates[i];
            string bad = null;

            if (t == null)
                bad = "null";
            else if (string.IsNullOrWhiteSpace(t.id))
                bad = "id가 비었다";
            else if (!ids.Add(t.id))
                bad = "id가 중복이다";
            else if (t.pool == null || t.pool.Length == 0)
                bad = "pool이 비었다";
            else if (t.minShips > t.maxShips)
                bad = $"minShips({t.minShips}) > maxShips({t.maxShips})";
            else if (t.maxShips <= 0)
                bad = $"maxShips({t.maxShips})가 0 이하다";

            if (bad == null)
                continue;

            Debug.LogError($"[OpenSector] 템플릿 '{(t != null ? t.id : "?")}'을 뺀다 - {bad}.");
            pool.templates.RemoveAt(i);
        }

        // hidden은 목록이 다 정리된 뒤에 본다 - 자기보다 뒤에 있는 id를 가리킬 수 있다.
        foreach (OpenSectorTemplate t in pool.templates)
        {
            if (string.IsNullOrEmpty(t.hidden) || ids.Contains(t.hidden))
                continue;

            Debug.LogError($"[OpenSector] '{t.id}'의 hidden '{t.hidden}'이 없다. 겉만 세운다.");
            t.hidden = "";
        }
    }
}

/// <summary>
/// 장과 장 사이의 소구역을 만든다. **본구역 8개는 손으로 쓴 것 그대로 남는다** - 이야기가
/// 거기 걸려 있고(sector-01~08 대본, target 구역, 초복사 시설이라는 결말), 절차 생성이
/// 건드릴 자리가 아니다. 생성되는 것은 그 사이를 메우는 잡전투뿐이다.
///
/// **맵을 저장하지 않는다.** 시드(<see cref="RunState.Seed"/>)와 고른 레인
/// (<see cref="RunState.Lanes"/>)만 저장하고, 같은 입력에 같은 노드가 다시 나온다.
/// 노드를 직렬화하면 생성기를 고치는 날 옛 저장과 새 생성기가 다른 맵을 말한다.
///
/// 결과가 <see cref="SectorDef"/>인 것이 요점이다 - Campaign의 Current·_sector++·Stage가
/// 전부 리스트 인덱스 하나에 매달려 있어서, 그래프를 런타임 자료구조로 들면 그 셋이 다
/// 갈린다. 그래프는 선택 화면에만 있고 확정되는 순간 일직선으로 접힌다.
/// </summary>
public static class OpenSectorGen
{
    /// <summary>
    /// 생성기 버전. **시드만 저장하는 것은 세이브 호환이 아니다** - 같은 시드가 같은 맵을
    /// 주는 것은 같은 생성기 안에서뿐이고, 간격 상수 하나나 Density 수식을 고치면 어제
    /// 저장한 런이 오늘 다른 우주로 열린다. 에러도 안 난다.
    ///
    /// 옛 생성기를 남겨 분기하지 않는 이유: 코드가 영원히 늘고, 이 게임의 런은 한 시간짜리다.
    /// 버전이 다르면 그 런을 버리고 새로 시작한다(<see cref="RunState.ValidateOrClear"/>).
    /// **생성 결과를 바꾸는 수정을 하면 이 숫자를 올린다** - 상수·수식·뽑기 순서 전부.
    /// </summary>
    public const int Version = 4;

    /// <summary>장 사이 소구역 수(깊이). 갈림길은 각 깊이에서 <see cref="Lanes"/>갈래.</summary>
    public const int LegsPerChapter = 2;

    /// <summary>한 갈림길의 갈래 수.</summary>
    public const int Lanes = 2;

    private static OpenSectorPool _pool;

    private static OpenSectorPool Pool()
    {
        if (_pool == null)
            _pool = OpenSectorPool.Load();

        return _pool;
    }

    /// <summary>
    /// (장, 소구역 순번, 레인)에 해당하는 노드. **위치가 곧 시드다** - 같은 자리를 몇 번
    /// 물어도 같은 답이고, 그래서 저장할 것이 시드와 경로뿐이다.
    /// </summary>
    public static SectorDef Make(int chapter, int leg, int lane)
    {
        OpenSectorPool pool = Pool();

        if (pool == null)
            return null;

        var rng = new DeterministicRng(
            Ballistics.Hash(RunState.Seed, (chapter << 8) | leg, lane));

        // **첫 출력을 버린다.** leg·lane이 0/1뿐이라 이웃 시드가 하위 비트만 다른데,
        // xorshift32의 첫 한 바퀴는 그걸 상위 비트로 못 올린다 - Next01이 >>8로 상위
        // 24비트만 읽으니 그 뭉침이 그대로 뽑기에 나온다. 재보니 "한 장의 네 노드가
        // 전부 같은 템플릿"이 18.0%였고, 두 번 태우면 13.2%로 독립 추첨의 이론값
        // (13.6%)에 붙는다. DeterministicRng 자체는 안 건드린다 - 탄도·파편 결정론이
        // 거기 매달려 있어서 한 줄만 바꿔도 지금까지의 모든 튜닝이 재현을 잃는다.
        rng.NextUInt();
        rng.NextUInt();

        OpenSectorTemplate first = Pick(pool, chapter, ref rng);

        if (first == null)
            return null;

        // 들판 하나 = 자리 K개. 이름·종류는 첫 자리 것이고 refit·materials는 안 받는다 -
        // 출항이 승리라 "깬 노드"의 보상이 방문 여부와 무관하게 들어오기 때문이다.
        var sector = new SectorDef
        {
            name = first.label,
            kind = first.kind,

            // **대본을 안 건다.** 이야기는 장이 하고 소구역은 조용하다 - 여기서
            // sector-entered를 울리면 "3구역 진입"이 세 번 나온다. 그 조용함이
            // DramaManager의 잡담(idleGap)이 도는 자리다.
            script = "",

            // 출구 둘, +X 끝의 위·아래 모서리. 도착점(-X 변 가운데)에서 보면 좌우로 36 km 벌어진다 -
            // 어느 출구로 갈지가 곧 항해다. 출구 0 = 갈래 0(위), 출구 1 = 갈래 1(아래).
            gateX = GateX,
            gateY = GateY,
            gate1X = GateX,
            gate1Y = -GateY,
            field = FieldLength,
        };

        // 자리는 뭉친다. 60 km에 자리 다섯을 고르게 뿌리면 들판이 아니라 빈 우주다 - 밀도는
        // 전체 평균이 아니라 **덩어리 안**에서 나온다. 덩어리 안은 자리끼리 센서 거리쯤이라
        // 한 자리에서 옆 자리의 접촉이 보이고, 덩어리 사이는 비어서 그 사이가 항해다.
        //
        // 어디에 뭉치는지는 밀도 지도가 정한다. 진한 칸부터 훑어 놓은 자리에서 간격 안이면
        // 건너뛴다 - 정렬된 목록을 걷는 것이라 실패가 없다. 균등 난수 + 거부 20번으로 하면
        // 네제곱 뒤엔 대부분 칸이 0이라 스무 번을 다 버린다.
        int noiseSeed = (int)rng.NextUInt();
        RankCells(noiseSeed, ClusterSpacing * 0.5f, GateX - ClusterSpacing * 0.5f, FieldHalfWidth - ClusterRadius);

        // **두 출구 사이의 가격이 다르다.** 출구 0(위)과 1(아래) 중 한쪽 항로에는 전투 자리가, 다른 쪽에는
        // 잔해·보급·운석이 몰린다. 어느 쪽이 어느 쪽인지는 시드가 정한다 - "위는 언제나 싸움"이면 규칙을 외우고 끝이다.
        // 밀도장은 등방이라 이 편향이 없으면 어느 출구로 가든 같은 들판이고, 그러면 출구 둘은 선택이 아니다.
        bool combatUp = rng.Next01() < 0.5f;

        int clusters = (int)rng.Range(MinClusters, MaxClusters + 0.999f);
        var centres = new List<Vector2>(clusters);
        int cursor = 0;

        for (; cursor < _cells.Count && centres.Count < clusters; cursor++)
        {
            if (Clear(centres, _cells[cursor].at, ClusterSpacing))
                centres.Add(Jitter(_cells[cursor].at, ref rng));
        }


        var placed = new List<Vector2>();
        bool depot = false;
        bool firstSite = true;

        // **덩어리마다 자기 수를 쓴다.** 예전에는 총합을 세고 centres[s % count]로 돌렸는데,
        // 그러면 덩어리가 뽑은 3/4가 통째로 증발하고 균등 분배가 된다 - 분포가 비슷해서
        // 눈으로는 안 들키지만 코드가 말하는 것과 하는 일이 다르다.
        foreach (Vector2 centre in centres)
        {
            int sites = (int)rng.Range(MinSitesPerCluster, MaxSitesPerCluster + 0.999f);

            for (int i = 0; i < sites; i++)
            {
                // 정비 자리는 하나까지. 둘이면 들판이 보급소가 된다. **후보에서 미리 빼는 것이지
                // 다시 뽑는 것이 아니다** - 재시도는 확률이 조금만 치우쳐도 "이유 없이 자리가
                // 없는" 들판을 만들고, 그건 절차 생성에서 제일 찾기 싫은 종류의 버그다.
                bool combatSide = (centre.y > 0f) == combatUp;
                bool biased = rng.Next01() < SideBias;   // 전부 한쪽이면 다른 쪽이 심심하다. 대부분만 치우친다
                bool depotNow = depot;
                System.Predicate<OpenSectorTemplate> allow = !biased
                    ? (depotNow ? NotRefit : null)
                    : t => (!depotNow || !t.refit) && (Combat(t) == combatSide);

                OpenSectorTemplate t = firstSite
                    ? first
                    : Pick(pool, chapter, ref rng, allow);

                firstSite = false;

                if (t == null)
                    continue;

                depot |= t.refit;

                if (!PlaceInCluster(ref rng, placed, centre, out Vector2 at))
                    continue;

                placed.Add(at);
                Stamp(sector, t, at, ref rng);

                OpenSectorTemplate inside = Find(pool, t.hidden);

                if (inside != null && inside != t)
                    Stamp(sector, inside, at, ref rng);
            }
        }

        // 덩어리마다 운석. 자리 사이를 채우는 실물이고, 엄폐이고, 충각 사고다. 판 90장짜리라
        // TraceWorld 비용은 배 한 척과 같다 - 수가 곧 비용이라 상수로 뺐다.
        foreach (Vector2 centre in centres)
            RockField(sector.spawns, centre, ref rng, (centre.y > 0f) == combatUp ? 1f : QuietRockScale);   // 조용한 쪽은 운석이 배 - 숨을 곳

        return sector;
    }

    /// <summary>
    /// 밀도 지도. 펄린 5겹(파장 40 km부터 반씩)을 합쳐 네제곱한다. 네제곱이 중간을 지운다 -
    /// 0.7은 0.24, 0.3은 0.008. 봉우리만 남고 지수가 "얼마나 몰리나"의 손잡이다.
    /// 시드는 펄린 좌표 오프셋이다. 순수 함수라 결정론 안에 있다(UnityEngine.Random이 아니다).
    /// </summary>
    public static float Density(Vector2 at, int seed)
    {
        // 오프셋은 0~655로 잡는다. Mathf.PerlinNoise는 입력이 수천을 넘으면 정밀도가 떨어져 격자가 보인다.
        float ox = (seed & 0xFFFF) * 0.01f;
        float oy = ((seed >> 16) & 0xFFFF) * 0.01f;
        float sum = 0f, norm = 0f, amp = 1f, wave = DensityWavelength;

        for (int i = 0; i < DensityOctaves; i++)
        {
            sum += amp * Mathf.PerlinNoise(ox + at.x / wave, oy + at.y / wave);
            norm += amp;
            amp *= 0.5f;
            wave *= 0.5f;
        }

        float d = Mathf.Clamp01(sum / norm);
        return d * d * d * d;
    }

    /// <summary>들판을 1 km 칸으로 자르고 진한 순으로 정렬한다. 동률은 칸 번호로 가른다 - List.Sort는 불안정이라 그냥 두면 실행마다 순서가 다를 수 있다.</summary>
    private static void RankCells(int seed, float xMin, float xMax, float halfWidth)
    {
        _cells.Clear();

        for (float x = xMin + CellSize * 0.5f; x < xMax; x += CellSize)
        {
            for (float y = -halfWidth + CellSize * 0.5f; y < halfWidth; y += CellSize)
            {
                var at = new Vector2(x, y);
                _cells.Add((Density(at, seed), _cells.Count, at));
            }
        }

        _cells.Sort((a, b) => a.d != b.d ? b.d.CompareTo(a.d) : a.i.CompareTo(b.i));
    }

    /// <summary>칸 가운데에서 반 칸 안으로 흔든다. 안 흔들면 자리가 1 km 격자에 줄을 선다.</summary>
    private static Vector2 Jitter(Vector2 at, ref DeterministicRng rng) =>
        at + new Vector2(rng.Range(-CellSize * 0.5f, CellSize * 0.5f), rng.Range(-CellSize * 0.5f, CellSize * 0.5f));

    /// <summary>
    /// 떠돌이 1척의 설계도 이름. Campaign의 타이머가 부른다 - 자리는 플레이어 앞, 때는 시간.
    /// 이 장의 전투 템플릿에서 뽑아 덩어리의 배와 같은 얼굴을 쓴다.
    /// </summary>
    public static string PickWandererShip(int chapter, ref DeterministicRng rng)
    {
        OpenSectorPool pool = Pool();

        if (pool == null)
            return null;

        OpenSectorTemplate t = PickCombat(pool, chapter, ref rng);

        if (t == null || t.pool.Length == 0)
            return null;

        return t.pool[(int)rng.Range(0, t.pool.Length - 0.001f)];
    }

    /// <summary>싸울 것이 있는 자리만. 재시도가 아니라 후보를 미리 좁힌다.</summary>
    private static OpenSectorTemplate PickCombat(OpenSectorPool pool, int chapter, ref DeterministicRng rng)
        => Pick(pool, chapter, ref rng, Combat);

    private static readonly System.Predicate<OpenSectorTemplate> NotRefit = t => !t.refit;
    private static readonly System.Predicate<OpenSectorTemplate> Combat = t => !t.refit && !Peaceful(t.Kind);

    private static readonly List<(float d, int i, Vector2 at)> _cells = new();

    /// <summary>
    /// 메인 섹터(손대본, 출구 없음)의 운석. 대본 배치와 도착점에서 <see cref="ClusterSpacing"/>의
    /// 절반 이상 떨어진 자리에 밭 몇 개. 적은 안 넣는다 - 메인의 적은 대본이 정하고, 멀리 둔 적은
    /// SeekZone이 전장 중심으로 집어 플레이어를 밀어낸다(OpenSector-Plan §11-1).
    /// def를 바꾸지 않고 새 목록을 돌려준다.
    /// </summary>
    public static List<SpawnDef> Rocks(SectorDef sector, int chapter)
    {
        var rocks = new List<SpawnDef>();
        var rng = new DeterministicRng(Ballistics.Hash(RunState.Seed, chapter, RockSalt));
        rng.NextUInt();
        rng.NextUInt();

        var keep = new List<Vector2> { Vector2.zero };

        foreach (SpawnDef s in sector.spawns)
            keep.Add(new Vector2(s.x, s.y));

        Rect bounds = sector.FieldBounds;
        int fields = (int)rng.Range(MinMainRockFields, MaxMainRockFields + 0.999f);

        for (int f = 0; f < fields; f++)
        {
            if (PlaceApart(ref rng, keep, ClusterSpacing * 0.5f,
                    bounds.xMin + ClusterRadius, bounds.xMax - ClusterRadius,
                    bounds.height * 0.5f - ClusterRadius, out Vector2 at))
            {
                keep.Add(at);
                RockField(rocks, at, ref rng);
            }
        }

        return rocks;
    }

    private static void RockField(List<SpawnDef> into, Vector2 centre, ref DeterministicRng rng, float scale = 1f)
    {
        int rocks = Mathf.RoundToInt(rng.Range(MinRocksPerCluster, MaxRocksPerCluster + 0.999f) * scale);

        for (int r = 0; r < rocks; r++)
        {
            Vector2 at = centre + Polar(ref rng, ClusterRadius * 1.3f);

            into.Add(new SpawnDef
            {
                ship = Rock,
                team = "Neutral",
                hulk = true,
                scenery = true,
                x = at.x,
                y = at.y,
                facing = rng.Next01() < 0.5f ? -1f : 1f,
            });
        }
    }

    /// <summary>
    /// 들판 치수. 전부 오너 손잡이다 - 여기 숫자가 재미를 정하지 코드가 정하지 않는다.
    /// 출구는 +X 끝. 덩어리 사이가 비어 있는 것이 의도다(그 사이를 점프가 접는다).
    /// </summary>
    private const float FieldLength = 60000f;
    private const float FieldHalfWidth = 30000f;   // 60×60. 출구는 +X 끝, 도착점은 -X 변 가운데
    private const float GateX = FieldLength - 2000f;
    private const float GateY = FieldHalfWidth * 0.6f;   // 두 출구의 세로 간격 = 36 km
    // 간격은 시간으로 못 잡는다(2026-09-11 정정). 전속 467 m/s면 6 km가 13초고 100 m/s면 60초라,
    // 같은 거리가 속도에 따라 다섯 배로 달라진다 - "엔카운터 1~2분"은 이 숫자에서 나올 수 없다.
    // 6 km의 실제 뜻: 덩어리 반경 1.5 km를 빼면 빈 구간이 3 km라 센서(1.2 km)의 두 배 - 덩어리를
    // 벗어나면 잠깐 비었다가 다음 것이 잡힌다. 박자(20초)는 Campaign의 떠돌이 타이머가 낸다.
    private const float SideBias = 0.75f;          // 덩어리가 자기 쪽 성격을 따를 확률. 1이면 위/아래가 완전히 갈린다
    private const float QuietRockScale = 2f;       // 조용한 쪽 덩어리의 운석 배수
    private const int MinClusters = 15;
    private const int MaxClusters = 25;
    private const float ClusterSpacing = 6000f;    // 덩어리 중심 사이
    private const float ClusterRadius = 1500f;     // 덩어리 안 자리가 앉는 반지름. 간격의 반보다 작아야 덩어리가 갈린다
    private const float SiteSpacing = 1200f;       // 덩어리 안 자리 사이. 센서 거리와 같다 - 한 자리에서 옆 자리가 보인다
    private const float CellSize = 1000f;          // 밀도 지도 칸. 60×60이면 3,600칸
    private const int DensityOctaves = 5;
    private const float DensityWavelength = 40000f; // 첫 겹 파장. 들판 한 변의 2/3
    private const int MinSitesPerCluster = 3;
    private const int MaxSitesPerCluster = 4;
    private const int MinMainRockFields = 3;       // 메인 100 km. 밭 하나 = 운석 4~8 = TraceWorld에 배 4~8척
    private const int MaxMainRockFields = 4;
    private const int RockSalt = 0x524F434B;       // "ROCK". Make의 시드와 겹치지 않게
    private const int MinRocksPerCluster = 4;
    private const int MaxRocksPerCluster = 8;
    public const string Rock = "asteroid";
    private const int PlaceTries = 20;

    private static Vector2 Polar(ref DeterministicRng rng, float radius)
    {
        float angle = rng.Range(0f, Mathf.PI * 2f);
        float r = radius * Mathf.Sqrt(rng.Next01());   // 넓이 균등. 반지름 균등이면 가운데가 뭉친다
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
    }

    /// <summary>덩어리 중심. 서로 <paramref name="spacing"/> 이상, 출구·도착점에서 그 절반 이상.</summary>
    private static bool PlaceApart(ref DeterministicRng rng, List<Vector2> placed, float spacing, float xMin, float xMax, float halfWidth, out Vector2 at)
    {
        for (int k = 0; k < PlaceTries; k++)
        {
            at = new Vector2(rng.Range(xMin, xMax), rng.Range(-halfWidth, halfWidth));

            if (Clear(placed, at, spacing))
                return true;
        }

        at = default;
        return false;
    }

    private static bool PlaceInCluster(ref DeterministicRng rng, List<Vector2> placed, Vector2 centre, out Vector2 at)
    {
        for (int k = 0; k < PlaceTries; k++)
        {
            at = centre + Polar(ref rng, ClusterRadius);

            if (Clear(placed, at, SiteSpacing))
                return true;
        }

        at = default;
        return false;
    }

    private static bool Clear(List<Vector2> placed, Vector2 at, float spacing)
    {
        foreach (Vector2 other in placed)
        {
            if ((other - at).sqrMagnitude < spacing * spacing)
                return false;
        }

        return true;
    }

    private static void Stamp(SectorDef sector, OpenSectorTemplate t, Vector2 at, ref DeterministicRng rng, int count = 0)
    {
        if (count <= 0)
            count = Mathf.Max(1, (int)rng.Range(t.minShips, t.maxShips + 0.999f));

        for (int i = 0; i < count; i++)
        {
            string ship = t.pool[(int)rng.Range(0, t.pool.Length - 0.001f)];

            sector.spawns.Add(new SpawnDef
            {
                ship = ship,
                team = Peaceful(t.Kind) ? "Neutral" : "Enemy",

                // 잔해·보급·기항은 Hulk로 세운다. 조종도 사격도 안 하고 떠 있으면서 맞는다.
                hulk = Peaceful(t.Kind),
                refit = t.refit,
                materials = i == 0 ? t.materials : 0,     // 자리당 한 번. 첫 배에만 실어 둔다
                propellant = i == 0 ? t.propellant : 0,
                munitions = i == 0 ? t.munitions : 0,
                signalSize = t.signalSize,
                label = t.label,

                x = at.x + rng.Range(-60f, 60f),
                y = at.y + rng.Range(-90f, 90f),
                facing = -1f,
            });
        }
    }

    /// <summary>
    /// 이 장에 어울리는 템플릿. tier가 장 번호를 따라 올라가되, 잔해·보급(tier 0)은
    /// 언제나 후보다 - 회복할 자리가 없으면 영구 손상이 그냥 벌점이 된다.
    /// </summary>
    private static OpenSectorTemplate Find(OpenSectorPool pool, string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        foreach (OpenSectorTemplate t in pool.templates)
        {
            if (t.id == id)
                return t;
        }

        Debug.LogWarning($"[OpenSector] hidden '{id}'가 템플릿에 없다. 겉만 세운다.");
        return null;
    }

    private static OpenSectorTemplate Pick(
        OpenSectorPool pool, int chapter, ref DeterministicRng rng,
        System.Predicate<OpenSectorTemplate> allow = null)
    {
        int want = Mathf.Clamp(1 + chapter / 3, 1, 3);

        _candidates.Clear();

        foreach (OpenSectorTemplate t in pool.templates)
        {
            if ((t.tier == 0 || t.tier == want) && (allow == null || allow(t)))
                _candidates.Add(t);
        }

        // **tier 폴백은 필터를 안 푼다.** 이 장에 맞는 것이 없으면 아무거나 쓰는 것이 낫지만,
        // "정비 자리는 하나" 같은 규칙까지 풀면 그 규칙이 있으나 마나가 된다.
        if (_candidates.Count == 0)
        {
            foreach (OpenSectorTemplate t in pool.templates)
            {
                if (allow == null || allow(t))
                    _candidates.Add(t);
            }
        }

        if (_candidates.Count == 0)
            return null;

        int total = 0;

        foreach (OpenSectorTemplate t in _candidates)
            total += Mathf.Max(1, t.weight);

        // 가중 추첨. 누적합을 훑는 것이 후보 여덟 개짜리에는 충분하다 - 별칭 표 같은
        // 것을 짓기 전에 이 숫자가 커질 이유가 먼저 있어야 한다.
        int roll = (int)rng.Range(0, total - 0.001f);

        foreach (OpenSectorTemplate t in _candidates)
        {
            roll -= Mathf.Max(1, t.weight);

            if (roll < 0)
                return t;
        }

        return _candidates[_candidates.Count - 1];
    }

    /// <summary>싸울 것이 없는 노드인가.</summary>
    private static bool Peaceful(OpenSectorKind kind) =>
        kind == OpenSectorKind.Wreck || kind == OpenSectorKind.Depot || kind == OpenSectorKind.Port;

    private static readonly List<OpenSectorTemplate> _candidates = new();
}
