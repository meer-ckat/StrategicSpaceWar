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

    /// <summary>파일 원문. Ship 컴포넌트에 통째로 붓는다.</summary>
    [NonSerialized] public string raw;

    [NonSerialized] public string source;

    private static readonly string[] HeaderKeys = { "defName", "basedOn", "placements" };

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

        string text = File.ReadAllText(path);
        var def = JsonUtility.FromJson<ShipDef>(text);

        if (def == null || def.placements == null || def.placements.Count == 0)
        {
            Debug.LogError($"[ShipDef] {path}를 읽었지만 배치가 하나도 없다.");
            return null;
        }

        def.raw = text;
        def.source = Path.GetFileName(path);

        // ThingDef와 같은 이유로 같은 검증을 탄다. 부어넣을 대상이 Ship과 Hulk 둘이라 둘 다
        // 아는 필드면 통과시킨다 - 운석 def에 drag를 적어도 조용히 묻히지 않는다.
        if (DefKeys.HasUnknown(text, def.source, HeaderKeys, typeof(Ship), typeof(Hulk)))
            return null;

        return def;
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
