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

    /// <summary>들판의 정비 자리. 이 잔해 옆에서 R을 누르면 정비 화면이 열린다. 생성기만 채운다.</summary>
    public bool refit;

    /// <summary>들판의 자리에 닿으면 한 번 주는 물자. 생성기가 템플릿에서 옮긴다.</summary>
    public int credits, propellant, munitions;

    /// <summary>신호 크기(m). 이 거리 안이면 좌표가 잡히고, 지도 부채꼴 밝기가 signalSize / 거리다. 생성기가 템플릿에서 옮긴다.</summary>
    public float signalSize;

    /// <summary>식별됐을 때 지도에 뜨는 이름(템플릿 label). 비면 적/잔해/보급으로 뭉뚱그린다.</summary>
    public string label = "";

    /// <summary>
    /// 이것을 부수는 것이 이 구역의 목표다. 하나라도 있으면 구역의 승리 조건이
    /// "적이 없다"가 아니라 "표적이 다 죽었다"가 된다 - 8구역에서 호위를 다 잡아도
    /// 거울이 남아 있으면 안 끝나는 이유다.
    /// </summary>
    public bool target;

    /// <summary>
    /// 배경으로 깔린 것. 신호도 자리도 아니고 엄폐·충각 사고만 한다.
    ///
    /// **이 값이 없을 때는 def 이름(asteroid)이 그 질문에 답했다.** 그런데 템플릿의 pool에도
    /// asteroid가 있어서(driftfield·trap이 [derelict, asteroid]다) 자리가 통째로 운석으로
    /// 태어나는 일이 흔했고, 그러면 정비 자리인데 신호에 안 뜨고 트래커에도 안 나온다 -
    /// 플레이어가 우연히 지나가야 R이 뜬다. 증상이 "가끔 보급이 없는 들판"이라 안 보인다.
    /// </summary>
    public bool scenery;

    public float x;
    public float y;

    public bool isWingman;
    public bool autoFormation;

    /// <summary>
    /// -1이면 180도 돌려 세운다(왼쪽을 본다). 회전은 월드에만 있고, 격자·방·구조 BFS는
    /// 전부 로컬 위상이라 손댈 것이 없다.
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
    public string gameWinCondition;

    /// <summary>
    /// 이 구역을 끝낸 뒤 정비 화면이 열리는가. **기본이 false인 것이 규칙이다** -
    /// 예전에는 구역을 깨면 언제나 수리할 수 있었고, 그래서 영구 손상이라는 이 게임의
    /// 유일한 주장이 매 구역 리셋됐다. 수리는 항로에서 얻는 것이지 승리의 부록이 아니다.
    ///
    /// 손으로 쓴 본구역 8개는 이 값을 안 적으므로 전부 false다 - 정비는 소구역의
    /// 보급·표류·기항 노드에서만 일어난다.
    /// </summary>
    public bool refit;

    /// <summary>도착만으로 주는 크레딧. 기항지 지원금이다 - 격파 급여와 다른 축이다.</summary>
    public int credits;

    /// <summary>
    /// 소구역 종류(Skirmish/Elite/Wreck/Depot/Port). 배경 소품이 이걸로 갈린다. 손으로 쓴
    /// 본구역은 비어 있고, 그러면 배경은 씬에 놓인 것만 보인다.
    /// </summary>
    public string kind = "";
    /// <summary>구역에 들어설 때 재생할 대본. 비면 아무 일도 안 일어난다.</summary>
    public string script;

    public List<SpawnDef> spawns = new();

    /// <summary>
    /// 들판 노드의 출구. (0,0)이면 지금까지의 노드다 - 적 전멸이 승리. 값이 있으면 여기
    /// 닿는 것이 승리고, 적은 흩어진 자리마다 잠들어 있다가 가까이 가야 깬다.
    /// </summary>
    public float gateX, gateY;

    /// <summary>둘째 출구. (0,0)이면 출구는 하나다(옛 저장·손대본). 닿은 출구가 곧 갈래(lane)다.</summary>
    public float gate1X, gate1Y;

    public int GateCount => !Open ? 0 : (gate1X != 0f || gate1Y != 0f) ? 2 : 1;
    public Vector2 GateAt(int k) => k == 0 ? new Vector2(gateX, gateY) : new Vector2(gate1X, gate1Y);

    public bool Open => gateX != 0f || gateY != 0f;

    /// <summary>
    /// 들판 한 변(m). 오픈섹터 60 km, 메인 100 km. 0이면 예전 구역(씬에 놓인 것만). 출구가
    /// 없어도 들판이다 - 메인은 대본이 승리를 정하고 들판은 그 둘레에 깔리는 운석뿐이다.
    /// </summary>
    public float field;

    public bool Field => field > 0f || Open;

    /// <summary>
    /// 들판 범위(배 좌표). **원점의 뜻이 둘이라 여기서 한 번에 갈린다** - 오픈섹터는
    /// 도착점이 -X 변 가운데라 x가 0부터 시작하고, 손대본 구역은 도착점이 한가운데라
    /// 원점 중심이다(워프 도착점 불변식). 범위를 읽는 쪽이 이 차이를 각자 알면 폭을
    /// 바꾸는 날 한쪽만 따라오고, 증상은 "운석이 들판 밖에 있다"뿐이다.
    /// </summary>
    public Rect FieldBounds => Open
        ? new Rect(0f, -field * 0.5f, field, field)
        : new Rect(-field * 0.5f, -field * 0.5f, field, field);
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
