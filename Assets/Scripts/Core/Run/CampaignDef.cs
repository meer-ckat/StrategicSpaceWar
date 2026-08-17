using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 구역 하나가 소환하는 것 하나.
///
/// **여기 있는 것은 전부 소환 인자다.** 어떤 배인지는 <see cref="ship"/>이 가리키는 설계도가
/// 알고 있고, 이 클래스는 그 배를 어느 편으로 어디에 세울지만 말한다. team이 설계가 아닌
/// 이유가 그것이다 - 같은 구축함이 아군일 수도 적군일 수도 있다.
/// </summary>
[Serializable]
public class SpawnDef
{
    /// <summary>StreamingAssets/Ships/&lt;이름&gt;.json.</summary>
    public string ship;

    /// <summary>Neutral / Ally / Enemy. <see cref="hulk"/>면 안 쓴다 - 잔해에 편이 없다.</summary>
    public string team = "Enemy";

    /// <summary>
    /// 함선이 아니라 <see cref="Hulk"/>로 세운다. 운석·폐위성·거울 껍질이 이것이다 -
    /// 조종도 사격도 안 하고 그냥 떠 있으면서 맞는다.
    /// </summary>
    public bool hulk;

    /// <summary>
    /// 이것을 부수는 것이 이 구역의 목표다. 하나라도 있으면 구역의 승리 조건이
    /// "적이 없다"가 아니라 "표적이 다 죽었다"가 된다 - 8구역에서 호위를 다 잡아도
    /// 거울이 남아 있으면 안 끝나는 이유다.
    /// </summary>
    public bool target;

    public float x;
    public float y;

    /// <summary>
    /// -1이면 좌우 반전해서 세운다. **engagementSign이 아니라 localScale.x다** - 반전은
    /// 월드에만 있고, 격자·방·구조 BFS는 전부 로컬 위상이라 손댈 것이 없다.
    /// </summary>
    public float facing = 1f;

    public Ship.Team Side =>
        Enum.TryParse(team, ignoreCase: true, out Ship.Team parsed) ? parsed : Ship.Team.Enemy;
}

/// <summary>구역 하나. 무엇이 뜨는지와, 뜰 때 어떤 대본을 재생하는지.</summary>
[Serializable]
public class SectorDef
{
    public string name;

    /// <summary>구역에 들어설 때 재생할 대본. 비면 아무 일도 안 일어난다.</summary>
    public string script;

    public List<SpawnDef> spawns = new();
}

/// <summary>
/// 캠페인 전체. 구역 목록 하나가 전부다.
///
/// **분기가 없다.** 항로 선택은 구역 def가 "다음 후보 여럿"을 들면 되는데, 일직선이 먼저
/// 돌아가야 그게 뭘 고르는 것인지 알 수 있다.
/// </summary>
[Serializable]
public class CampaignDef
{
    public string defName;
    public List<SectorDef> sectors = new();

    private static readonly string[] HeaderKeys = { "defName", "sectors" };

    public static string Path =>
        System.IO.Path.Combine(Application.streamingAssetsPath, "Run", "campaign.json");

    /// <summary>
    /// 읽고 검증한다. **최상위 키 검증을 여기서도 탄다** - JsonUtility가 모르는 키를 조용히
    /// 버리는 것은 def든 캠페인이든 똑같고, 증상이 "왜 3구역에 적이 안 뜨지"가 된다.
    ///
    /// 다만 검증이 닿는 것은 최상위뿐이다. spawns 안쪽의 오타(<c>shp</c>)는 못 잡는다 -
    /// 대신 소환할 때 그 이름의 설계도가 없으면 <see cref="Campaign"/>이 에러를 낸다.
    /// </summary>
    public static CampaignDef Load()
    {
        if (!File.Exists(Path))
        {
            Debug.LogError($"[Campaign] {Path}가 없다.");
            return null;
        }

        string text = File.ReadAllText(Path);

        if (DefKeys.HasUnknown(text, "campaign.json", HeaderKeys, typeof(CampaignDef)))
            return null;

        var def = JsonUtility.FromJson<CampaignDef>(text);

        if (def == null || def.sectors == null || def.sectors.Count == 0)
        {
            Debug.LogError("[Campaign] 구역이 하나도 없다.");
            return null;
        }

        return def;
    }
}
