using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 배치 하나. 좌표는 격자 칸이지 미터가 아니다 - 격자는 위상만 담당하므로 정수로 충분하고,
/// 정수라서 export/import 왕복이 부동소수점 오차 없이 닫힌다.
///
/// rot과 콜라이더 모양은 격자가 절대 안 본다. 2x2 경사장갑이 이웃 칸을 덮고 있어도 방 구획은
/// 이 col/row 하나만 읽는다. 그게 의도된 거짓말이다.
/// </summary>
[Serializable]
public class Placement
{
    public string def;
    public int col;
    public int row;
    public float rot;          // 도

    /// <summary>
    /// 남은 체력 비율(0~1). 1이면 멀쩡한 새 배다.
    ///
    /// **손상된 배는 새 포맷이 아니다** - 배치가 적은 ShipDef에 이 숫자 하나가 붙은 것이다.
    /// 부서진 판은 배치에서 그냥 빠지고(그게 뚫린 구멍이다), 상한 판만 이 값을 든다.
    ///
    /// 서브셀 36칸 패턴은 안 담는다. 담으면 판당 36개 x 241판이고, 눈에 남는 것은 뚫린
    /// 구멍이지 판 안쪽 1 m 미만의 얼룩이 아니다. 대가는 "어느 모서리가 갈렸나"가 사라지는 것.
    /// </summary>
    public float hp = 1f;

    /// <summary>
    /// 이 자리에서만 쓰는 콜라이더 크기. **0이면 def의 값을 쓴다.**
    ///
    /// 이것이 성립하는 이유는 격자가 콜라이더를 안 보기 때문이다 - 방·선체·파단은 col/row만
    /// 읽고, 콜라이더는 탄도와 그림에만 간다. 그래서 같은 판이 자리마다 다른 크기를 가져도
    /// 위상은 하나도 안 바뀐다.
    ///
    /// 대가로 def 하나가 줄어든다: `Armor mk5`와 `Armor mk5 slope`는 이 숫자 하나만 달랐다
    /// (1x1 대 1x1.414). 두 벌을 두면 rha·hp·색을 고칠 때마다 두 파일을 고쳐야 하고,
    /// 언젠가 한쪽만 고친다.
    ///
    /// 체력도 따라온다 - `hpPerSquareMetre`가 m²당이라 넓은 판이 저절로 튼튼하다.
    /// </summary>
    public Vector2 size;

    /// <summary>
    /// 콜라이더를 칸 중심에서 이만큼(m) 민다. def의 offset에 **더해진다.**
    ///
    /// **오브젝트는 안 옮긴다.** 판의 localPosition이 곧 칸 번호라 그걸 밀면 격자가 통째로
    /// 어긋난다. 미는 것은 콜라이더뿐이고, 서브셀 격자(<see cref="Armor"/>)도 그림
    /// (<see cref="ArmorSkin"/>)도 콜라이더 offset을 이미 읽으므로 셋이 같이 움직인다.
    /// 경사판이 칸 밖으로 걸쳐 나가야 할 때 쓴다.
    ///
    /// **이 값은 칸 좌표계다 - <see cref="rot"/>과 무관하다.** 판을 45도 돌렸다고 "위로 0.3"이
    /// 대각선으로 새면 배치를 쓰는 쪽이 매번 삼각함수를 들고 있어야 한다. 실제 콜라이더
    /// offset은 회전 뒤의 로컬 좌표계라, <see cref="ThingDef.Spawn"/>이 -rot으로 돌려서 넣고
    /// <see cref="ShipExporter"/>가 +rot으로 되돌려 뽑는다.
    /// </summary>
    public Vector2 offset;

    /// <summary>이 모듈이 볼트로 붙은 판의 칸. -1이면 선체 직속(= 판이 죽어도 안 죽는다).</summary>
    public int mountCol = -1;
    public int mountRow = -1;

    public bool IsMounted => mountCol >= 0 && mountRow >= 0;

    public Vector2Int Cell => new(col, row);
}

/// <summary>
/// 배 한 척의 설계도 전부. 배치 리스트 **그리고** 배 자체의 수치.
///
/// ThingDef와 정확히 같은 트릭 위에 있다: 같은 원문을 두 번 읽는다. 한 번은 이 헤더 클래스로
/// (defName, basedOn, placements), 한 번은 <see cref="Apply"/>가 Ship 컴포넌트에
/// FromJsonOverwrite로. drag, angleAccel, FightDistance 같은 것들이 그렇게 들어간다.
///
/// 왜 함선까지 데이터여야 하나: 런이 "적 프리깃 하나 소환"을 하려면 그 배의 수치가 씬
/// 인스펙터가 아니라 파일에 있어야 한다. 클래스를 새로 만들지 않고도 새 함선이 생겨야 한다.
///
/// **team은 여기 없다.** 같은 구축함이 아군일 수도 적군일 수도 있으니 설계가 아니라 소환
/// 인자다. 위치와 engagementSign도 같은 이유로 빠진다.
/// </summary>
[Serializable]
public class ShipDef
{
    public string defName;

    /// <summary>
    /// 원본 설계의 이름. 런 중에 피해를 입은 배는 placements가 줄어든 ShipDef로 저장되는데,
    /// 그러면 원본에서 표류한다. 이 필드가 "런 재시작"을 살려둔다.
    /// </summary>
    public string basedOn;

    public List<Placement> placements = new();

    /// <summary>
    /// 배치가 차지하는 칸 범위. **authored bbox의 유일한 정의다** - `ShipBuilder.AuthoredMap`이
    /// 격자를 잡을 때도, 함선 그림 크기를 잴 때도 여기를 부른다. 두 곳에 적어두면 한쪽만
    /// 고치는 날이 오고, 그때 증상은 "그림은 맞는데 배가 안 실린다"가 된다.
    ///
    /// def가 소유하는 이유: 범위는 placements의 성질이고 placements의 주인이 여기다.
    /// ShipBuilder가 들고 있으면 def가 자기 크기를 물으려고 빌더를 불러야 한다.
    /// </summary>
    public RectInt Bbox()
    {
        if (placements == null || placements.Count == 0)
            return default;

        int minCol = int.MaxValue, maxCol = int.MinValue;
        int minRow = int.MaxValue, maxRow = int.MinValue;

        foreach (Placement p in placements)
        {
            minCol = Mathf.Min(minCol, p.col);
            maxCol = Mathf.Max(maxCol, p.col);
            minRow = Mathf.Min(minRow, p.row);
            maxRow = Mathf.Max(maxRow, p.row);
        }

        return new RectInt(minCol, minRow, maxCol - minCol + 1, maxRow - minRow + 1);
    }

    /// <summary>파일 원문. Ship 컴포넌트에 통째로 붓는다.</summary>
    [NonSerialized] public string raw;

    [NonSerialized] public string source;

    private static readonly string[] HeaderKeys = { "defName", "basedOn", "placements", "hullSkin" };

    /// <summary>
    /// 함선 그림 파일 이름. 배치와 같은 폴더(StreamingAssets/Ships)에 있다. 비어 있으면
    /// 그림이 없는 배다 - 지금 아홉 척이 전부 그렇고, 그건 오류가 아니다.
    ///
    /// **ThingDef가 아니라 여기 있는 이유**: 텍스처는 배 한 척당 한 장이지 판 종류당이
    /// 아니다. `Armor mk5`는 세 척이 같이 쓰는데 그림은 배마다 다르다.
    /// </summary>
    public string hullSkin;

    /// <summary>
    /// 함선 그림의 픽셀/미터. 캔버스 크기가 여기서 나온다 - authored 격자 W×H칸이면
    /// PNG는 정확히 (W*PPU)x(H*PPU)여야 한다.
    ///
    /// **`ArmorSkin`의 48과 정수배로 묶여 있어야 한다.** 판 텍스처가 48/m인데 함선 그림이
    /// 100/m이면 비가 2.083…이라 판마다 리샘플 오차가 남고, 증상은 판 경계마다 생기는
    /// 이음매다. 96은 정확히 두 배라 안전하다. 바꿀 거면 48의 배수로.
    /// </summary>
    private const int PPU = 96;

    /// <summary>
    /// 배의 수치를 Ship(또는 Hulk)에 붓는다. 어느 필드가 넘어오는지는 그 컴포넌트가 정한다 -
    /// Ship에 [SerializeField] 하나를 추가하면 그 순간부터 JSON에서 설정 가능해진다.
    /// </summary>
    public void Apply(Component target)
    {
        if (target != null && !string.IsNullOrEmpty(raw))
            JsonUtility.FromJsonOverwrite(raw, target);
    }

    public static string DirectoryPath => Path.Combine(Application.streamingAssetsPath, "Ships");

    public static string PathOf(string defName) => Path.Combine(DirectoryPath, defName + ".json");

    // ponytail: File 직접 읽기. 데스크톱 전용이다 - 안드로이드나 웹으로 가면 StreamingAssets가
    // 아카이브 안에 들어가서 UnityWebRequest로 바꿔야 한다.
    public static ShipDef Load(string defName)
    {
        string path = PathOf(defName);

        if (!File.Exists(path))
        {
            Debug.LogError($"[ShipDef] '{defName}'을 찾을 수 없다: {path}");
            return null;
        }

        ShipDef def = Parse(File.ReadAllText(path), Path.GetFileName(path));

        // 그림 규격은 설계도를 읽는 이 자리에서만 본다. 이유는 SkinIsValid 주석에.
        return SkinIsValid(def) ? def : null;
    }

    /// <summary>
    /// 원문 -> ShipDef. **읽는 자리가 둘이라 여기 있다** - StreamingAssets의 설계도와
    /// persistentDataPath의 런 상태. 검증을 한쪽에만 두면 나머지 한쪽은 오타를 조용히
    /// 기본값으로 묻는다. 어디서 왔든 같은 문을 통과해야 그 구멍이 안 생긴다.
    /// </summary>
    public static ShipDef Parse(string text, string source)
    {
        var def = JsonUtility.FromJson<ShipDef>(text);

        if (def == null || def.placements == null || def.placements.Count == 0)
        {
            Debug.LogError($"[ShipDef] {source}를 읽었지만 배치가 하나도 없다.");
            return null;
        }

        def.raw = text;
        def.source = source;

        // ThingDef와 같은 이유로 같은 검증을 탄다. 부어넣을 대상이 Ship과 Hulk 둘이라 둘 다
        // 아는 필드면 통과시킨다 - 운석 def에 drag를 적어도 조용히 묻히지 않는다.
        if (DefKeys.HasUnknown(text, source, HeaderKeys, typeof(Ship), typeof(Hulk)))
            return null;

        return def;
    }

    /// <summary>
    /// 함선 그림이 격자와 같은 크기인가. **순수 함수다 - 로그를 안 찍는다.**
    ///
    /// 판정과 보고의 주인이 갈려 있어야 한다. 여기서 <c>Debug.LogError</c>를 부르면
    /// self-test가 실패 케이스를 돌릴 때마다 콘솔이 빨개져서, 테스트가 통과했는지 아닌지가
    /// 로그로는 안 갈린다. 문장만 돌려주고 찍는 것은 부르는 쪽이 한다.
    ///
    /// <paramref name="ppu"/>는 인자다. 상수를 여기서 읽으면 테스트가 축척을 못 바꾸고,
    /// "48만 허용"이라는 **정책이 순수 함수 안으로 새어든다.** 정책은 호출부가 정한다 -
    /// <see cref="Load"/>가 항상 <see cref="PPU"/>를 넘기므로 다른 축척으로 그린 그림은
    /// 픽셀 수가 안 맞아서 거부된다. 선언은 거짓말할 수 있고 픽셀 수는 못 한다.
    /// </summary>
    public static bool CheckTextureSize(
        int bboxW, int bboxH, int ppu, int pngW, int pngH, out string reason)
    {
        if (ppu < 1 || bboxW < 1 || bboxH < 1)
        {
            reason = $"격자나 축척이 비었다. 칸 {bboxW}x{bboxH}, ppu {ppu}.";
            return false;
        }

        int expectedW = bboxW * ppu;
        int expectedH = bboxH * ppu;

        if (pngW != expectedW || pngH != expectedH)
        {
            reason =
                $"그림이 {expectedW}x{expectedH}여야 하는데 {pngW}x{pngH}다 " +
                $"(격자 {bboxW}x{bboxH}칸 x {ppu}px).";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// PNG의 가로세로만 읽는다. 앞 24바이트가 시그니처 + IHDR이고 거기 다 들어 있다.
    ///
    /// **텍스처를 만들지 않는 것이 요점이다.** <c>Texture2D.LoadImage</c>를 쓰면 검사하려고
    /// 5376x1728을 디코드했다가 버려야 하고, 그러면 "이 텍스처의 주인이 누구냐"는 질문이
    /// 여기서 생긴다 - 그건 그림을 실제로 붙이는 쪽의 일이다. 헤더만 읽으면 그 질문이
    /// 존재하지 않는다.
    /// </summary>
    public static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!File.Exists(path))
            return false;

        var head = new byte[24];

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            if (stream.Read(head, 0, head.Length) != head.Length)
                return false;   // 24바이트도 안 되면 PNG가 아니다
        }

        // 시그니처 89 50 4E 47 0D 0A 1A 0A, 그다음 청크가 IHDR이어야 한다.
        if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47 ||
            head[4] != 0x0D || head[5] != 0x0A || head[6] != 0x1A || head[7] != 0x0A ||
            head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R')
            return false;

        // **빅엔디안이다.** BitConverter는 리틀엔디안이라 그냥 쓰면 5376이 아니라
        // 20억쯤 되는 숫자가 나온다.
        width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];

        return width > 0 && height > 0;
    }

    /// <summary>그림 파일은 배치와 같은 폴더에 산다. <see cref="PathOf"/>와 같은 모양이다.</summary>
    public static string SkinPathOf(string texture) => Path.Combine(DirectoryPath, texture);

    /// <summary>
    /// 그림이 선언돼 있으면 격자와 크기가 맞는지 본다. 안 맞으면 배를 안 싣는다 -
    /// <see cref="DefKeys.HasUnknown"/>과 같은 태도다.
    ///
    /// **<see cref="Parse"/>가 아니라 <see cref="Load"/>에서 부른다.** 두 가지 이유다.
    /// 하나: 여기서 authored bbox가 필요한데 <c>ShipBuilder.AuthoredMap</c>은 basedOn을
    /// 파일에서 읽으므로, 설계도의 basedOn이 자기 자신인 이 리포에서는 Parse -> Load ->
    /// Parse로 **무한 재귀**가 된다. 둘: Parse는 손상 저장본도 타는데 저장본의 placements는
    /// 줄어 있어서 bbox가 원본보다 작다 - 거기서 크기를 재면 멀쩡한 그림이 거부된다.
    ///
    /// 설계도를 읽는 자리에서만 검사하므로, 저장본은 이미 검증된 그림을 물려받는다.
    /// 대가: 설계도를 한 번 읽은 뒤 PNG만 갈아치우고 저장본으로 들어가면 안 잡힌다.
    /// </summary>
    private static bool SkinIsValid(ShipDef def)
    {
        if (def == null || string.IsNullOrEmpty(def.hullSkin))
            return true;   // 그림 없는 배. 지금 아홉 척이 전부 이 경우다.

        string path = SkinPathOf(def.hullSkin);

        if (!TryReadPngSize(path, out int pngW, out int pngH))
        {
            Debug.LogError($"[ShipDef] {def.defName}: '{def.hullSkin}'을 PNG로 못 읽는다: {path}");
            return false;
        }

        // AuthoredMap이 아니라 Bbox를 직접 부른다. AuthoredMap은 basedOn을 파일에서 읽어서
        // 위 주석의 재귀에 걸린다. 설계도의 placements가 곧 authored라 답은 같다.
        RectInt box = def.Bbox();

        if (CheckTextureSize(box.width, box.height, PPU, pngW, pngH, out string reason))
            return true;

        Debug.LogError($"[ShipDef] {def.defName}의 '{def.hullSkin}': {reason}");
        return false;
    }

    /// <summary>
    /// 배치만 다시 쓴다. 파일에 이미 있는 배 수치(drag, angleAccel...)는 글자 하나까지
    /// 그대로 둔다 - export는 저작 도구가 씬인 배치의 결과물이지, 손으로 튜닝한 숫자의
    /// 주인이 아니다. 통째로 직렬화하면 그 숫자들이 조용히 사라진다.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);

        string path = PathOf(defName);
        string placementsJson = ExtractPlacements(JsonUtility.ToJson(this, prettyPrint: true));

        if (File.Exists(path) && placementsJson != null)
        {
            string merged = DefKeys.ReplaceTopLevelValue(
                File.ReadAllText(path), "placements", placementsJson);

            if (merged != null)
            {
                File.WriteAllText(path, merged);
                return;
            }
        }

        File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
    }

    private static string ExtractPlacements(string json)
    {
        int start = json.IndexOf('[');
        int end = json.LastIndexOf(']');

        return start >= 0 && end > start ? json.Substring(start, end - start + 1) : null;
    }
}
