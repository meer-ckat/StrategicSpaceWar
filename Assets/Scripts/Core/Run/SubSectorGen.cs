using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 소구역 템플릿 하나. **배 목록이 아니라 조합 규칙이다** - 고정 목록으로 두면 한 런에
/// 16개나 뽑는 소구역이 금세 같은 얼굴로 반복된다. 배가 26척 있으니 뽑기가 조합을 만든다.
/// </summary>
[Serializable]
public class SubSectorTemplate
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

    public SubSectorKind Kind => kind switch
    {
        "Elite" => SubSectorKind.Elite,
        "Wreck" => SubSectorKind.Wreck,
        "Depot" => SubSectorKind.Depot,
        "Port" => SubSectorKind.Port,
        _ => SubSectorKind.Skirmish,
    };
}

public enum SubSectorKind { Skirmish, Elite, Wreck, Depot, Port }

[Serializable]
public class SubSectorPool
{
    public string defName;
    public List<SubSectorTemplate> templates = new();

    private static readonly string[] HeaderKeys = { "defName", "templates" };

    public static string Path =>
        System.IO.Path.Combine(Application.streamingAssetsPath, "Run", "subsectors.json");

    public static SubSectorPool Load()
    {
        if (!File.Exists(Path))
        {
            Debug.LogError($"[SubSector] {Path}가 없다.");
            return null;
        }

        string text = File.ReadAllText(Path);

        // 캠페인과 같은 검증을 탄다. JsonUtility가 모르는 키를 조용히 버리는 것은 여기서도
        // 똑같고, 증상은 "왜 이 템플릿만 안 뽑히지"가 된다.
        if (DefKeys.HasUnknown(text, "subsectors.json", HeaderKeys, typeof(SubSectorPool)))
            return null;

        var pool = JsonUtility.FromJson<SubSectorPool>(text);

        if (pool == null || pool.templates == null || pool.templates.Count == 0)
        {
            Debug.LogError("[SubSector] 템플릿이 하나도 없다.");
            return null;
        }

        return pool;
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
public static class SubSectorGen
{
    /// <summary>장 사이 소구역 수(깊이). 갈림길은 각 깊이에서 <see cref="Lanes"/>갈래.</summary>
    public const int LegsPerChapter = 2;

    /// <summary>한 갈림길의 갈래 수.</summary>
    public const int Lanes = 2;

    private static SubSectorPool _pool;

    private static SubSectorPool Pool()
    {
        if (_pool == null)
            _pool = SubSectorPool.Load();

        return _pool;
    }

    /// <summary>
    /// (장, 소구역 순번, 레인)에 해당하는 노드. **위치가 곧 시드다** - 같은 자리를 몇 번
    /// 물어도 같은 답이고, 그래서 저장할 것이 시드와 경로뿐이다.
    /// </summary>
    public static SectorDef Make(int chapter, int leg, int lane)
    {
        SubSectorPool pool = Pool();

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

        SubSectorTemplate first = Pick(pool, chapter, ref rng);

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

            gateX = GateX,
            gateY = 0f,
        };

        // 자리는 뭉친다. 60 km에 자리 다섯을 고르게 뿌리면 들판이 아니라 빈 우주다 - 밀도는
        // 전체 평균이 아니라 **덩어리 안**에서 나온다. 덩어리 안은 자리끼리 센서 거리쯤이라
        // 한 자리에서 옆 자리의 접촉이 보이고, 덩어리 사이는 비어서 그 사이가 항해다.
        int clusters = (int)rng.Range(MinClusters, MaxClusters + 0.999f);
        var centres = new List<Vector2>(clusters);

        for (int c = 0; c < clusters; c++)
        {
            if (PlaceApart(ref rng, centres, ClusterSpacing, ClusterSpacing * 0.5f, FieldHalfWidth - ClusterRadius, out Vector2 at))
                centres.Add(at);
        }

        var placed = new List<Vector2>();
        bool depot = false;
        int sites = 0;

        foreach (Vector2 centre in centres)
            sites += (int)rng.Range(MinSitesPerCluster, MaxSitesPerCluster + 0.999f);

        for (int s = 0; s < sites; s++)
        {
            SubSectorTemplate t = s == 0 ? first : Pick(pool, chapter, ref rng);

            if (t == null)
                continue;

            // 정비 자리는 하나까지. 둘이면 들판이 보급소가 된다. 건너뛰지 않고 다시 뽑는다 -
            // tier 0 셋이 전부 refit이라 건너뛰면 자리 하나짜리 들판이 흔하다.
            for (int k = 0; t != null && t.refit && depot && k < PlaceTries; k++)
                t = Pick(pool, chapter, ref rng);

            if (t == null || (t.refit && depot))
                continue;

            depot |= t.refit;

            Vector2 centre = centres[s % centres.Count];

            if (!PlaceInCluster(ref rng, placed, centre, out Vector2 at))
                continue;

            placed.Add(at);
            Stamp(sector, t, at, ref rng);

            SubSectorTemplate inside = Find(pool, t.hidden);

            if (inside != null && inside != t)
                Stamp(sector, inside, at, ref rng);
        }

        // 덩어리마다 운석. 자리 사이를 채우는 실물이고, 엄폐이고, 충각 사고다. 판 90장짜리라
        // TraceWorld 비용은 배 한 척과 같다 - 수가 곧 비용이라 상수로 뺐다.
        foreach (Vector2 centre in centres)
        {
            int rocks = (int)rng.Range(MinRocksPerCluster, MaxRocksPerCluster + 0.999f);

            for (int r = 0; r < rocks; r++)
            {
                Vector2 at = centre + Polar(ref rng, ClusterRadius * 1.3f);

                sector.spawns.Add(new SpawnDef
                {
                    ship = Rock,
                    team = "Neutral",
                    hulk = true,
                    x = at.x,
                    y = at.y,
                    facing = rng.Next01() < 0.5f ? -1f : 1f,
                });
            }
        }

        return sector;
    }

    /// <summary>
    /// 들판 치수. 전부 오너 손잡이다 - 여기 숫자가 재미를 정하지 코드가 정하지 않는다.
    /// 출구는 +X 끝. 덩어리 사이가 비어 있는 것이 의도다(그 사이를 점프가 접는다).
    /// </summary>
    private const float FieldLength = 60000f;
    private const float FieldHalfWidth = 8000f;
    private const float GateX = FieldLength - 2000f;
    private const int MinClusters = 2;
    private const int MaxClusters = 3;
    private const float ClusterSpacing = 15000f;   // 덩어리 중심 사이
    private const float ClusterRadius = 2500f;     // 덩어리 안 자리가 앉는 반지름
    private const float SiteSpacing = 1200f;       // 덩어리 안 자리 사이. 센서 거리와 같다 - 한 자리에서 옆 자리가 보인다
    private const int MinSitesPerCluster = 3;
    private const int MaxSitesPerCluster = 4;
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
    private static bool PlaceApart(ref DeterministicRng rng, List<Vector2> placed, float spacing, float margin, float halfWidth, out Vector2 at)
    {
        for (int k = 0; k < PlaceTries; k++)
        {
            at = new Vector2(rng.Range(margin, GateX - margin), rng.Range(-halfWidth, halfWidth));

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

    private static void Stamp(SectorDef sector, SubSectorTemplate t, Vector2 at, ref DeterministicRng rng)
    {
        int count = Mathf.Max(1, (int)rng.Range(t.minShips, t.maxShips + 0.999f));

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
    private static SubSectorTemplate Find(SubSectorPool pool, string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        foreach (SubSectorTemplate t in pool.templates)
        {
            if (t.id == id)
                return t;
        }

        Debug.LogWarning($"[SubSector] hidden '{id}'가 템플릿에 없다. 겉만 세운다.");
        return null;
    }

    private static SubSectorTemplate Pick(SubSectorPool pool, int chapter, ref DeterministicRng rng)
    {
        int want = Mathf.Clamp(1 + chapter / 3, 1, 3);

        _candidates.Clear();

        foreach (SubSectorTemplate t in pool.templates)
        {
            if (t.tier == 0 || t.tier == want)
                _candidates.Add(t);
        }

        if (_candidates.Count == 0)
            _candidates.AddRange(pool.templates);

        int total = 0;

        foreach (SubSectorTemplate t in _candidates)
            total += Mathf.Max(1, t.weight);

        // 가중 추첨. 누적합을 훑는 것이 후보 여덟 개짜리에는 충분하다 - 별칭 표 같은
        // 것을 짓기 전에 이 숫자가 커질 이유가 먼저 있어야 한다.
        int roll = (int)rng.Range(0, total - 0.001f);

        foreach (SubSectorTemplate t in _candidates)
        {
            roll -= Mathf.Max(1, t.weight);

            if (roll < 0)
                return t;
        }

        return _candidates[_candidates.Count - 1];
    }

    /// <summary>싸울 것이 없는 노드인가.</summary>
    private static bool Peaceful(SubSectorKind kind) =>
        kind == SubSectorKind.Wreck || kind == SubSectorKind.Depot || kind == SubSectorKind.Port;

    private static readonly List<SubSectorTemplate> _candidates = new();
}
