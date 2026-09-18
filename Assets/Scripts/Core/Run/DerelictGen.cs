using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 난파선을 실제 함선 설계도를 부숴서 만든다. 손으로 쓴 derelict.json 한 척이 잔해밭·보급·
/// 기항의 모든 자리에 서 있었고, 64칸짜리 같은 실루엣이 매 런 반복됐다.
///
/// **부수는 것은 로딩 때 한 번이다.** 테두리 폭파가 exterior를 읽어야 하고 exterior는 격자
/// flood에서 나오므로(<see cref="ShipBuilder.StampFromDef"/>), 이 일을 소환 시점에 하면
/// 덩어리 하나가 5 km 안에 들어올 때마다 스파이크다. 결과는 배치 리스트 하나라 def로 구워
/// 캐시에 얹어두면 소환 경로는 평소와 글자 하나 다르지 않다.
///
/// 세 단계 다 **있는 규칙만 쓴다**: 자르기는 배치를 지우고, 분화구도 배치를 지우고, 부식은
/// <see cref="Placement.hp"/>를 낮춘다. 난파선 전용 시뮬레이션이 없다 - 뚫린 구멍에 후면이
/// 안 깔리는 것도, 갈라진 조각이 따로 떠다니는 것도 기존 파단·후면 규칙이 알아서 한다.
/// </summary>
public static class DerelictGen
{
    /// <summary>
    /// 풀에 이 이름이 있으면 생성된 난파선으로 바꿔 끼운다. 못 만들었으면 이 이름이 그대로
    /// 선다 - 손으로 쓴 derelict.json이 폴백이다.
    /// </summary>
    public const string Sentinel = "derelict";

    public const int VariantsPerShip = 3;

    /// <summary>이 밑으로 남으면 배가 아니라 파편 몇 장이다. 그 변종은 버리고 안 등록한다.</summary>
    private const int MinPlates = 12;

    /// <summary>1단계. 원본 폭·높이 중 남기는 비율 - 오른쪽(선수)과 아래(우현)를 잘라낸다.</summary>
    private const float CropMin = 0.55f, CropMax = 0.95f;

    /// <summary>2단계. 테두리를 따라 터지는 원의 개수와 반경(칸).</summary>
    private const int CratersMin = 3, CratersMax = 8;
    private const float CraterRadiusMin = 1.5f, CraterRadiusMax = 4.5f;

    /// <summary>3단계. 칸당 노이즈 좌표 - 작으면 큰 얼룩, 크면 잔 반점.</summary>
    private const float NoiseScaleMin = 0.08f, NoiseScaleMax = 0.30f;

    /// <summary>노이즈를 체력에서 빼는 배율. 1을 넘어야 제일 상한 자리가 구멍이 된다.</summary>
    private const float RotMin = 0.9f, RotMax = 1.6f;

    /// <summary>이 hp 밑은 판이 아니라 구멍이다. 배치에서 뺀다.</summary>
    public const float DropBelow = 0.12f;

    private static List<string> _names;

    /// <summary>만들어 둔 난파선 이름 전부. 순서가 곧 추첨 색인이라 이름 순으로 고정돼 있다.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            Build();
            return _names;
        }
    }

    // 로딩 단계. 씬을 다시 열어도 _names가 살아 있어 한 번만 돈다.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Warm() => Build();

    /// <summary>
    /// 뽑힌 이름이 <see cref="Sentinel"/>이면 난파선으로 바꿔 끼운다. 아니면 그대로 -
    /// 운석은 운석이어야 한다.
    ///
    /// **센티널일 때만 rng를 쓴다.** 항상 한 번 뽑으면 잔해가 아닌 자리의 흐름도 같이
    /// 밀려서, 이 기능을 끄고 켤 때마다 들판 전체가 달라진다.
    /// </summary>
    public static string PickName(string picked, ref DeterministicRng rng)
    {
        if (picked != Sentinel)
            return picked;

        Build();

        return _names.Count == 0
            ? Sentinel
            : _names[(int)rng.Range(0, _names.Count - 0.001f)];
    }

    public static void Build()
    {
        if (_names != null)
            return;

        _names = new List<string>();

        if (!Directory.Exists(ShipDef.DirectoryPath))
            return;

        var sources = new List<string>();

        foreach (string path in Directory.GetFiles(ShipDef.DirectoryPath, "*.json"))
            sources.Add(Path.GetFileNameWithoutExtension(path));

        // **이름 순으로 고정한다.** Directory.GetFiles의 순서는 파일 시스템 것이고 그 순서가
        // 곧 변종 목록의 색인이라, 흔들리면 같은 시드가 다른 난파선을 뽑는다. 배 파일을
        // 더하거나 지우면 색인이 밀리는 것은 남는 대가다 - def 배치를 지우면 stableId가
        // 밀리는 것과 같은 가격표다.
        sources.Sort(System.StringComparer.Ordinal);

        int dropped = 0;

        foreach (string source in sources)
        {
            ShipDef blueprint = ShipDef.Load(source);

            if (blueprint == null)
                continue;

            for (int v = 0; v < VariantsPerShip; v++)
            {
                string name = $"{source}~wreck{v}";
                ShipDef wreck = Crush(blueprint, v, name);

                if (wreck == null)
                {
                    dropped++;
                    continue;
                }

                ShipDef.Register(wreck);
                _names.Add(name);
            }
        }

        Debug.Log($"[DerelictGen] 난파선 {_names.Count}척. 원본 {sources.Count}, 버린 변종 {dropped}.");
    }

    /// <summary>설계도 한 척 + 변종 번호 -> 난파선 def. 남은 판이 너무 적으면 null.</summary>
    public static ShipDef Crush(ShipDef source, int variant, string name)
    {
        if (source?.placements == null || source.placements.Count == 0)
            return null;

        // 시드가 이름과 번호에서만 나오므로 실행마다 같은 난파선이다. string.GetHashCode는
        // 프로세스마다 값이 달라서(해시 무작위화) 쓸 수 없다 - FNV를 직접 돈다.
        var rng = new DeterministicRng(Ballistics.Hash(StableHash(source.defName), variant, 0xDE));

        var plates = new List<Placement>(source.placements.Count);

        // **원본 배치를 복사한다.** ShipDef 캐시의 근거가 "읽은 뒤 변형하지 않는다"라,
        // p.hp를 그 자리에서 낮추면 다음에 그 설계도로 짓는 멀쩡한 배가 상한 채로 나온다.
        foreach (Placement p in source.placements)
            plates.Add(Clone(p));

        Crop(plates, source.Bbox(), ref rng);

        if (plates.Count < MinPlates)
            return null;

        Craters(plates, name, ref rng);

        if (plates.Count < MinPlates)
            return null;

        Rot(plates, ref rng);

        return plates.Count < MinPlates ? null : Bake(source, name, plates);
    }

    /// <summary>
    /// 1단계. 오른쪽과 아래를 잘라낸다. 기수가 +X라 오른쪽은 선수고, row는 아래로 증가하므로
    /// (<see cref="ShipGrid.Map.ToLocal"/>) 아래는 우현이다. 잘린 쪽이 곧 "뭐에 맞았나"다.
    /// </summary>
    private static void Crop(List<Placement> plates, RectInt box, ref DeterministicRng rng)
    {
        int maxCol = box.xMin + Mathf.Max(1, Mathf.RoundToInt(box.width * rng.Range(CropMin, CropMax))) - 1;
        int maxRow = box.yMin + Mathf.Max(1, Mathf.RoundToInt(box.height * rng.Range(CropMin, CropMax))) - 1;

        plates.RemoveAll(p => p.col > maxCol || p.row > maxRow);
    }

    /// <summary>
    /// 2단계. 우주와 맞닿은 테두리 칸에 원을 찍어 판을 지운다.
    ///
    /// **테두리를 한 번만 구한다.** 분화구마다 다시 flood를 돌리면 원이 앞 원이 만든 구멍
    /// 안쪽으로 파고들어 배 속을 파먹는다 - 테두리를 따라 물린 자리가 나와야 "맞아서 벗겨진"
    /// 것으로 읽힌다. 8방향으로 보는 것은 선체 BFS와 같은 이유다(계단식 외판의 대각 이음).
    /// </summary>
    private static void Craters(List<Placement> plates, string name, ref DeterministicRng rng)
    {
        // raw 없는 껍데기로 충분하다 - StampFromDef가 읽는 것은 placements와 basedOn뿐이고,
        // basedOn이 비어 있으면 자기 배치가 곧 authored 격자다.
        var scratch = new ShipDef { defName = name, placements = plates };
        ShipGrid.Map map = ShipBuilder.StampFromDef(scratch);

        if (map == null)
            return;

        Vector2Int mins = scratch.Bbox().min;
        var rim = new List<Vector2Int>();

        for (int x = 0; x < map.width; x++)
        {
            for (int y = 0; y < map.height; y++)
            {
                if (map.cells[x, y] == ShipGrid.Cell.Exterior && TouchesSolid(map, x, y))
                    rim.Add(new Vector2Int(x + mins.x, y + mins.y));
            }
        }

        if (rim.Count == 0)
            return;

        int craters = (int)rng.Range(CratersMin, CratersMax + 0.999f);

        for (int i = 0; i < craters; i++)
        {
            Vector2Int at = rim[(int)rng.Range(0, rim.Count - 0.001f)];
            float radius = rng.Range(CraterRadiusMin, CraterRadiusMax);
            float sqr = radius * radius;

            plates.RemoveAll(p =>
            {
                float dx = p.col - at.x;
                float dy = p.row - at.y;

                return dx * dx + dy * dy <= sqr;
            });
        }
    }

    private static bool TouchesSolid(ShipGrid.Map map, int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                int ax = x + dx, ay = y + dy;

                if (ax >= 0 && ay >= 0 && ax < map.width && ay < map.height
                    && ShipGrid.Solid(map.cells[ax, ay]))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 3단계. 펄린 장 하나로 체력을 깎고, 제일 상한 자리는 구멍으로 떨어뜨린다.
    ///
    /// <c>Mathf.PerlinNoise</c>는 좌표만 보는 순수 함수라 <c>UnityEngine.Random</c> 금지에
    /// 안 걸린다 - 시드는 표본 좌표를 미는 오프셋으로 들어간다.
    ///
    /// 구멍과 얼룩이 같은 장에서 나오는 것이 요점이다. 따로 뽑으면 "구멍 둘레가 멀쩡한"
    /// 그림이 나오고, 그건 부서진 배가 아니라 오려낸 배로 보인다.
    /// </summary>
    private static void Rot(List<Placement> plates, ref DeterministicRng rng)
    {
        float ox = rng.Range(0f, 512f);
        float oy = rng.Range(0f, 512f);
        float scale = rng.Range(NoiseScaleMin, NoiseScaleMax);
        float bite = rng.Range(RotMin, RotMax);

        for (int i = plates.Count - 1; i >= 0; i--)
        {
            Placement p = plates[i];
            float hp = Mathf.Clamp01(1f - Mathf.PerlinNoise(ox + p.col * scale, oy + p.row * scale) * bite);

            if (hp < DropBelow)
                plates.RemoveAt(i);
            else
                p.hp = hp;
        }
    }

    /// <summary>
    /// 원본 원문 위에 배치만 갈아끼운다. <see cref="RunState"/>의 손상 저장과 같은 트릭이고
    /// 같은 이유다 - 통째로 직렬화하면 손으로 튜닝한 배 수치(drag, massPerPlate...)가
    /// 조용히 기본값으로 돌아간다.
    /// </summary>
    private static ShipDef Bake(ShipDef source, string name, List<Placement> plates)
    {
        string array = ShipDef.PlacementsArrayJson(plates);

        if (array == null)
            return null;

        string raw = DefKeys.ReplaceTopLevelValue(source.raw, "placements", array);

        if (raw == null)
            return null;

        ShipDef def = ShipDef.Parse(raw, name);

        if (def == null)
            return null;

        def.defName = name;

        // 그림은 원본 bbox 크기로 뽑힌 PNG다. 잘라낸 배에는 규격이 안 맞는다 - Hulk는
        // 애초에 안 읽지만, 이 def가 Ship으로 서는 날 그때 터진다.
        def.hullSkin = "";

        // basedOn을 비워 둔다. 채우면 AuthoredMap이 원본 크기 격자를 잡아서, 잘라낸 만큼이
        // "구멍 뚫린 실내"로 남고 후면이 그 허공에 깔린다.
        def.basedOn = "";

        return def;
    }

    private static Placement Clone(Placement p) =>
        new()
        {
            def = p.def,
            col = p.col,
            row = p.row,
            rot = p.rot,
            hp = p.hp,
            fuel = p.fuel,
            rounds = p.rounds,
            size = p.size,
            offset = p.offset,
            shape = p.shape,          // 읽기만 하므로 배열은 공유한다
            mountCol = p.mountCol,
            mountRow = p.mountRow,
        };

    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;

            for (int i = 0; i < s.Length; i++)
                h = (h ^ s[i]) * 16777619u;

            return (int)h;
        }
    }
}
