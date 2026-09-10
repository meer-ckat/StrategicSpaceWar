using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 대사 한 줄에 붙는 연출 지시. **컷신 배에만 닿는다** - 캠페인이 소환한 배는 이 길로
/// 조종되지 않는다.
///
/// JsonUtility라 사전도 다형성도 못 쓴다. 그래서 필드를 다 펴 놓고 <see cref="act"/>가
/// 어느 필드를 읽을지 정하는 꼴이다 - 안 쓰는 필드는 그냥 기본값으로 남는다.
/// </summary>
[Serializable]
public class DialogueCue
{
    /// <summary>
    /// 지시 이름. 모르는 값이면 경고를 찍고 넘어간다 - 오타 하나가 컷신을 통째로 멈추는
    /// 것보다 낫다. 목록은 대사 폴더의 README에 있다.
    /// </summary>
    public string act;

    /// <summary>대상 컷신 배의 이름. spawn이면 새로 붙일 이름이다.</summary>
    public string name;

    /// <summary>spawn 전용. 함선 설계도 이름(StreamingAssets/Ships).</summary>
    public string ship;

    /// <summary>spawn 전용. Ally / Enemy / Neutral. 비면 Enemy.</summary>
    public string team;

    /// <summary>spawn 전용. 음수면 180도 돌려 왼쪽을 본다.</summary>
    public float facing = 1f;

    public float x;
    public float y;

    /// <summary>
    /// 좌표의 **기준점**을 이름으로 준다. <c>player</c>는 예약어로 지금 플레이어가 모는
    /// 배다 - 그 배가 씬 어디에 서 있는지는 대본을 쓰는 시점에 알 수가 없다.
    ///
    /// **있으면 x·y가 그 기준점으로부터의 오프셋이 된다.** 둘 다 0이면 그 자리 그대로다.
    /// 절대 좌표로 적으면 플레이어를 옮기는 순간 대본이 통째로 어긋난다 - 연출은 "플레이어
    /// 앞 300 m"라고 말하지 "월드 640"이라고 말하지 않는다.
    /// </summary>
    public string at;

    /// <summary>
    /// look 전용. 둘을 담을 때의 여유(거리에 곱해 화면 크기가 된다). 0이면 씬의 값을
    /// 그대로 쓴다.
    ///
    /// **작을수록 바짝 붙는다.** 두 배가 300 m 떨어져 있는데 1.4를 쓰면 화면이 840 m
    /// 폭이라 배가 점이 된다 - 컷신은 얼굴이 보여야 하므로 전투 값보다 훨씬 작다.
    /// </summary>
    public float zoom;
    // look 전용. 화면 세로 절반의 월드 크기; 0이면 기존 줌 규칙을 쓴다.
    public float size;
}

/// <summary>
/// 게임 세계와 대본 사이의 다리. **두 방향으로 흐른다.**
///
/// 들어오는 쪽 - <see cref="RunLog"/>와 <see cref="Battle"/>이 낸 사건을 받아 어떤 대본을
/// 틀지 정하고, 조용한 동안에는 잡담을 시작한다.
///
/// 나가는 쪽 - 대본의 <see cref="DialogueCue"/>를 집행한다. 배를 소환하고, 움직이고,
/// 겨누고, 쏘고, 카메라를 잡는다.
///
/// 둘이 같은 클래스인 이유는 둘 다 **"세계와 대본이 서로를 만지는 자리"**여서다. 여기가
/// 아는 것은 무슨 일이 일어났는가와 무엇을 일으킬 것인가뿐이고, 그 말이 화면에 어떻게
/// 그려지는지는 <see cref="DialogueManager"/>만 안다.
/// </summary>
public class DramaManager : MonoBehaviour
{
    public static DramaManager current;

    /// <summary>전투·격침 같은 사건이 대사를 띄우게 할 것인가.</summary>
    public bool reactToSimulation = true;

    /// <summary>
    /// 화면이 이만큼 비어 있으면 승무원이 잡담을 시작한다.
    ///
    /// 사건 대사가 하나라도 살아 있는 동안은 안 센다. "말이 끊긴 시간"이지 "조용한
    /// 시간"이 아니다 - 유폭 대사가 화면에 떠 있는데 그 옆에서 농담이 올라오면
    /// 두 줄 다 안 읽힌다.
    /// </summary>
    public float idleGap = 14f;

    /// <summary>
    /// 긴장이 절반으로 식는 데 걸리는 초. **곱수가 아니라 반감기로 적는 이유는 이것이
    /// 귀로 잴 수 있는 유일한 단위이기 때문이다** - "0.97을 곱한다"는 아무것도 안
    /// 말해주지만 "20초면 절반"은 세어볼 수 있다.
    ///
    /// 20초면 선체 절단(0.6) 뒤 약 25초, 유폭(0.8) 뒤 약 34초에 잡담이 돌아온다.
    /// </summary>
    public float tensionHalfLife = 20f;

    /// <summary>이 값보다 긴장이 높으면 잡담이 안 나온다.</summary>
    public float chitChatMaxTension = 0.25f;

    /// <summary>디버그 표시. 반감기는 이거 없이는 못 맞춘다 - 소리로만 드러나는 값이다.</summary>
    public bool showTension;

    /// <summary>
    /// 지금 이 배가 얼마나 위험한가. 0이면 평시, 1이면 최악.
    ///
    /// **사건이 올리고 시간이 내린다. 대사는 안 건드린다.** 대사가 올리고 긴장이 대사를
    /// 막으면 잡담이 자기 자신을 억제하는 되먹임이 생기고, 그건 상수로 못 고친다.
    /// RunLog가 이미 "사건의 단일 깔때기"라 방향이 저절로 한쪽이다.
    ///
    /// 저장하지 않는다. 파생값이 아니라 이번 순간의 분위기이고, 다음 전투는 새로 센다.
    /// </summary>
    public float Tension { get; private set; }

    /// <summary>
    /// 이 승무원이 잃은 자리. <see cref="RunLog.Kind.RoleLost"/>가 채우고 **아무것도
    /// 비우지 않는다** - 기관실이 재가압돼도 죽은 기관사는 안 돌아온다.
    ///
    /// ponytail: 런을 새로 시작할 때 비우는 자리가 없다. 지금은 런마다 씬이 새로 떠서
    /// 이 매니저도 새로 나므로 문제가 안 된다. 씬을 유지한 채 런을 다시 시작하는 길이
    /// 생기면 그때 여기를 비워야 하고, 안 비우면 새 배의 기관사가 처음부터 말이 없다.
    /// </summary>
    private readonly HashSet<Ship.ShipRole> _lostRoles = new();

    /// <summary>
    /// 화자 이름 -> 자리. **이 대응이 있는 자리에만 대사가 있다** - Ship은 "기관"이라는
    /// 낱말을 모르고, 대본은 <c>ShipRole</c>을 모른다. 둘을 아는 곳이 여기 하나다.
    /// </summary>
    private static readonly Dictionary<string, Ship.ShipRole> AuthorRoles = new()
    {
        ["기관"] = Ship.ShipRole.Engineer,
        ["전술"] = Ship.ShipRole.Gunner,
    };

    /// <summary>
    /// 승무원 전멸. 죽은 배는 농담을 안 한다.
    ///
    /// <see cref="_lostRoles"/>로는 못 막는다. 전멸하면 두 자리가 다 죽으므로 잡담의
    /// 모든 줄이 걸러지고, 그러면 "하나도 안 남았다"로 떨어져 **시스템이 대신 농담을
    /// 읽는다.** 그 폴백은 사건 대사를 위한 것이지 잡담을 위한 것이 아니다.
    /// </summary>
    private bool _crewLost;

    /// <summary>이 화자는 이제 없다. 이름이 대응표에 없으면(함장·통신) 언제나 말할 수 있다.</summary>
    public bool Silenced(string author)
        => !string.IsNullOrEmpty(author)
        && AuthorRoles.TryGetValue(author, out Ship.ShipRole role)
        && _lostRoles.Contains(role);

    /// <summary>
    /// 씬에 안 붙인다. <see cref="ScriptManager"/>와 같은 규칙이다 - 씬을 안 건드리면
    /// 어긋날 두 번째 원본이 안 생긴다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Drama Manager");
        DontDestroyOnLoad(go);
        go.AddComponent<DramaManager>();
    }

    /// <summary>
    /// **둘째는 자살한다.** 안 그러면 RunLog 구독이 두 벌이 되어 사건 하나에 대사가
    /// 두 번 뜬다. ScriptManager와 같은 이유다.
    /// </summary>
    private void Awake()
    {
        if (current != null && current != this)
        {
            Destroy(gameObject);
            return;
        }

        current = this;
    }

    private void OnEnable()
    {
        if (!reactToSimulation)
            return;

        RunLog.onEntry += OnRunEntry;
        Battle.onAnyEnd += OnBattleEnd;
    }

    private void OnDisable()
    {
        RunLog.onEntry -= OnRunEntry;
        Battle.onAnyEnd -= OnBattleEnd;
    }

    private void OnDestroy()
    {
        if (current == this)
            current = null;
    }

    private void Update()
    {
        // 함선 선택 중에는 입을 다문다. 잡담이 unscaled 시계라 timeScale 0으로 안 멎고,
        // 화면이 조용하니(idleGap) 오히려 더 잘 나온다 - 시계가 아니라 입을 막아야 한다.
        if (ShipSelectScreen.IsOpen || LogisticsScreen.IsOpen || RefitScreen.IsOpen)
            return;

        float dt = Time.unscaledDeltaTime;

        // 지수 감쇠. dt에 안 걸리는 것이 요점이다 - 프레임이 튀어도 같은 시간에 같은
        // 값이 되므로, 반감기가 "초"라는 뜻을 계속 유지한다.
        if (Tension > 0f)
            Tension *= Mathf.Pow(0.5f, dt / Mathf.Max(0.01f, tensionHalfLife));

        Chatter();
    }

    // =========================================================
    // 들어오는 것 - 시뮬레이션 반응
    // =========================================================

    /// <summary>
    /// 사건 하나 -> 대본 이름. **팀별 대본이 있으면 그것, 없으면 공용으로 내려간다** -
    /// 적함이 터지는 것과 아군이 터지는 것은 다른 대사여야 하지만, 그렇다고 모든 사건을
    /// 두 벌씩 쓸 이유는 없다. 둘 다 없으면 그 사건엔 대사가 없는 것이고 그것도 정상이다.
    /// </summary>
    private void OnRunEntry(RunLog.Entry entry)
    {
        // 대사가 아니라 걸쇠다. 아래 switch에 맡기면 `_ => null`이 조용히 삼킨다.
        //
        // 아군만 본다. 적함 기관사가 죽는 것은 우리 무전에 아무 영향이 없다 - 적함
        // 대본도 말하는 사람은 우리 승무원이다.
        if (entry.kind == RunLog.Kind.RoleLost)
        {
            if (entry.team == Ship.Team.Ally
                && Enum.TryParse(entry.what, out Ship.ShipRole lost))
                _lostRoles.Add(lost);

            return;
        }

        if (entry.kind == RunLog.Kind.CrewLost && entry.team == Ship.Team.Ally)
            _crewLost = true;

        // 관통·파공 표시는 HitReadout(구석 로그)과 ShipStatusHud의 AIRFRAME 하이라이트가
        // 맡는다. 여기서는 긴장만 올린다 - 대사 레인은 이야기 전용이다.
        if (entry.kind is RunLog.Kind.Penetrated or RunLog.Kind.RoomBreached)
        {
            Tension = Mathf.Max(Tension, Severity(entry));
            return;
        }

        // **더하지 않고 최댓값을 잡는다.** 유폭 한 번에 판 40장이 같은 틱에 죽으면
        // 더하기는 긴장을 폭발시키고 배가 몇 분 동안 말을 안 한다. 최댓값이면 제일 큰
        // 사건 하나가 분위기를 정하고, 회복 시간이 반감기 하나로 예측 가능해진다.
        Tension = Mathf.Max(Tension, Severity(entry));

        string key = entry.kind switch
        {
            RunLog.Kind.Finished => "ship-finished",
            RunLog.Kind.Detonated => "detonated",
            RunLog.Kind.HullSplit => "hull-split",
            RunLog.Kind.CrewLost => "crew-lost",
            RunLog.Kind.SectorEntered => "sector-entered",
            RunLog.Kind.SectorCleared => "sector-cleared",
            _ => null,
        };

        ScriptManager scripts = ScriptManager.current;

        if (key == null || scripts == null)
            return;

        // 구역 사건은 팀이 아니라 번호로 가른다. sector-entered-3 = 3구역 진입 대사.
        if (entry.kind is RunLog.Kind.SectorEntered or RunLog.Kind.SectorCleared)
        {
            if (!scripts.PlayIfExists($"{key}-{entry.what}", entry.what))
                scripts.PlayIfExists(key, entry.what);

            return;
        }

        // 전투 사건은 구체적인 것부터 넓은 것 순서로 찾는다:
        //   키-팀-횟수 -> 키-팀 -> 키
        // 횟수는 이 런에서 같은 (종류, 팀)이 몇 번째인가다(방금 것 포함, 1부터).
        // ship-finished-enemy-1 = 이 런의 첫 적함 격파 대사 - 파일이 있으면 그때만 나온다.
        string team = entry.team.ToString().ToLowerInvariant();
        int count = RunLog.Count(entry.kind, entry.team);

        if (!scripts.PlayIfExists($"{key}-{team}-{count}", entry.what)
            && !scripts.PlayIfExists($"{key}-{team}", entry.what))
            scripts.PlayIfExists(key, entry.what);
    }

    /// <summary>
    /// 아무 일도 안 일어나는 동안 승무원이 말을 한다. **이것이 tension의 소비자다** -
    /// 지금은 시간만 보지만, 다음에 tension이 들어오면 여기 조건이 하나 는다.
    ///
    /// 화면이 빌 때까지 기다리는 것이 요점이다. 사건 대사와 겹치면 둘 다 안 읽힌다.
    /// </summary>
    private void Chatter()
    {
        ScriptManager scripts = ScriptManager.current;

        if (_crewLost || scripts == null || scripts.Chitchat.Count == 0)
            return;

        DialogueManager screen = DialogueManager.current;

        if (screen == null || screen.Texts.Count > 0)
            return;

        if (Time.unscaledTime - screen.LastLineTime < idleGap)
            return;

        // **LastLineTime을 밀기 전에 본다.** 그래야 긴장이 임계 밑으로 내려오는 순간
        // 이미 지난 idleGap을 다시 안 기다리고 바로 누가 입을 연다. 긴장이 풀리자마자
        // 말이 나오는 것이 이 시스템이 만들려는 장면이다.
        if (Tension > chitChatMaxTension)
            return;

        // 쿨다운에 걸려도 시각이 안 밀리면 매 프레임 다시 시도한다. 여기서 한 번 밀어
        // 두면 다음 후보를 idleGap 뒤에 고른다 - 대본이 전부 쿨다운이면 그동안 조용한
        // 것이고, 그것도 맞는 출력이다.
        screen.MarkLine();

        scripts.Play(scripts.Chitchat[scripts.Pick("chitchat", scripts.Chitchat.Count)]);
    }

    /// <summary>
    /// 이 사건이 얼마나 무거운가. 0~1.
    ///
    /// 팀별로 행을 열 개 쓰지 않는다. **사건의 무게 × 누구 일인가**로 갈라야 왜 그
    /// 숫자인지 설명이 되고, 새 사건이 생겨도 배수는 안 건드린다.
    ///
    /// 적함이 0이 아니라 0.3인 이유: 위협은 아니지만 방금 눈앞에서 큰일이 났다.
    /// 0이면 적 탄약고가 터지는 순간 바로 커피 얘기가 나온다.
    ///
    /// **적함 격침이 긴장을 내리는 규칙은 없다.** 적이 죽으면 새 사건이 안 들어오고,
    /// 그러면 감쇠가 알아서 데려간다 - "숨통이 트인다"가 규칙 없이 나온다.
    /// </summary>
    private static float Severity(RunLog.Entry entry)
    {
        float weight = entry.kind switch
        {
            RunLog.Kind.CrewLost => 1.0f,
            RunLog.Kind.Finished => 1.0f,
            RunLog.Kind.Detonated => 0.8f,
            RunLog.Kind.RoleLost => 0.8f,
            RunLog.Kind.HullSplit => 0.6f,
            RunLog.Kind.RoomBreached => 0.5f,
            RunLog.Kind.Penetrated => 0.25f,
            _ => 0f,
        };

        float whose = entry.team switch
        {
            Ship.Team.Ally => 1.0f,
            Ship.Team.Enemy => 0.3f,
            _ => 0f,
        };

        return weight * whose;
    }

    private void OnBattleEnd(Battle battle)
    {
        // 정비 노드 도착은 승리가 아니다. 전승 보고가 잔해밭에서 나오면 거짓말이다.
        if (battle.Won && battle.peaceful)
            return;

        ScriptManager.current?.Play(battle.Won ? "battle-won" : "battle-lost");
    }

    // =========================================================
    // 나가는 것 - 연출 (컷신)
    // =========================================================

    /// <summary>
    /// 충각이 이 안에 안 끝나면 포기하고 대본을 이어 간다. **넉넉해야 한다** - 창이
    /// 700 m 밖에서 출발하고 가속에도 시간이 걸린다. 짧게 잡으면 아직 날아오는 중에
    /// 대본이 다음 장면으로 넘어가서, 유폭이 엉뚱한 대사 위에서 난다.
    /// </summary>
    private const float AwaitWreckTimeout = 45f;

    /// <summary>
    /// 들이받았다고 볼 거리(m). 판정이 아니라 **연출의 문턱이다** - 창이 한 틱에 15 m를
    /// 건너뛰므로 정확한 접촉을 기다리면 그 틱을 통째로 넘길 수 있다. 구축함 반길이쯤.
    /// </summary>
    private const float RamContactRange = 40f;

    /// <summary>
    /// 그 배가 전투불능이 될 때까지 기다린다. **판정은 IsCombatEffective 하나다** -
    /// 컷신 전용 "죽음"을 따로 정의하면 화면에 부서진 것으로 보이는 배와 시뮬레이션이
    /// 죽었다고 보는 배가 어긋난다.
    ///
    /// 배가 통째로 사라지는 경우(오브젝트 파괴)도 끝으로 친다.
    /// </summary>
    public static IEnumerator AwaitWreck(string name, string rammer)
    {
        float spent = 0f;

        while (spent < AwaitWreckTimeout)
        {
            if (!CutSceneManager.TryGet(name, out CutSceneManager.CutScene_ShipObj target)
                || target.ship == null
                || !target.ship.IsCombatEffective)
                yield break;

            // 들이받는 배가 닿았으면 그 자리에서 결말을 낸다. 유폭은 CriticalModule을
            // 통과하는 진짜 경로라 그림이 실전과 같고, 창은 추적을 놓아 관성으로 지나간다 -
            // 안 놓으면 시체 자리에서 맴돈다.
            if (!string.IsNullOrWhiteSpace(rammer)
                && CutSceneManager.TryGet(rammer, out CutSceneManager.CutScene_ShipObj hitter)
                && hitter.Root != null && target.Root != null
                && Vector2.Distance(hitter.Root.transform.position, target.Root.transform.position)
                   <= RamContactRange)
            {
                hitter.Chase(null);
                target.Detonate();
                yield break;
            }

            spent += Time.unscaledDeltaTime;
            yield return null;
        }

        Debug.LogWarning(
            $"[대사] '{name}'이 {AwaitWreckTimeout:0}초 안에 안 부서졌다. 창이 빗나갔을 수 있다 - " +
            "연출을 이어 간다.");
    }

    /// <summary>
    /// 한 줄에 붙은 연출을 순서대로 집행한다. **모르는 지시는 경고만 찍고 넘어간다** -
    /// 오타 하나로 컷신이 통째로 멈추면 대본을 고치는 사람이 원인을 못 찾는다.
    /// </summary>
    public void RunCues(DialogueCue[] cues)
    {
        if (cues == null)
            return;

        for (int i = 0; i < cues.Length; i++)
            RunCue(cues[i]);
    }

    private void RunCue(DialogueCue cue)
    {
        if (cue == null || string.IsNullOrWhiteSpace(cue.act))
            return;

        // look은 대상이 컷신 배가 아닐 수도 있다(player). 그래서 아래 TryGet 관문 위에 둔다.
        // name이 비면 카메라를 놓는다 - 인스펙터가 적어 둔 프레임으로 돌아간다.
        if (cue.act == "look")
        {
            if (string.IsNullOrWhiteSpace(cue.name))
            {
                CameraSystem.ReleaseCutscene();
                return;
            }

            CameraSystem.CutsceneFrame(TransformOf(cue.name), TransformOf(cue.at), cue.zoom, cue.size);
            return;
        }

        // spawn만 대상이 아직 없어도 된다. 나머지는 이미 서 있는 배에게 거는 지시다.
        if (cue.act == "spawn")
        {
            // at이 있으면 그 배 기준 오프셋이다. 프롤로그는 거의 전부 "플레이어 앞/뒤
            // 몇 m"라, 절대 좌표로 적으면 배를 옮길 때마다 대본을 다시 계산해야 한다.
            if (!TryResolvePoint(cue, out Vector2 where))
                return;

            CutSceneManager.SpawnCutSceneShips(
                cue.name, where, cue.facing, cue.ship, TeamOf(cue.team));

            return;
        }

        // **join은 컷신 배가 아니라 런에 건다.** 컷신 배는 release 때 사라지지만 동료는
        // 남은 구역을 전부 따라가야 하므로, 명단이 RunState에 있고 구역마다 Campaign이
        // 다시 소환한다. ship이 설계도, x·y가 편대 자리(플레이어 기준)다.
        if (cue.act == "join")
        {
            RunState.Join(cue.ship, new Vector2(cue.x, cue.y));
            Debug.Log($"[대사] '{cue.ship}' 합류. 편대 자리 ({cue.x}, {cue.y}).");
            return;
        }

        // camera는 배가 없어도 된다. 화면 자체를 만지는 지시다.
        if (cue.act == "camera")
        {
            CameraSystem.CutsceneDamp(cue.x, cue.y);
            return;
        }

        // **전투 중 대본은 스스로 끝내야 한다.** Clear를 부르는 자리가 여는 대본의
        // EndAndStartRun뿐이라, 구역 대본이 player를 만지면 Borrow가 PlayerInput을 끄고
        // _detatchBrain을 켠 채로 아무도 안 돌려준다 - 증상은 "연출 뒤로 배가 안 움직인다"고,
        // 대본이 아니라 조종을 의심하게 된다. 마지막 줄에 이걸 달면 조종간이 돌아온다.
        if (cue.act == "endCut")
        {
            CutSceneManager.Clear();
            return;
        }

        // 구역 대본이 "지금부터 적을 띄워라"를 말한다. 없으면 대본이 끝나거나 45초 상한이
        // 지나야 소환이 시작돼서, 긴 대본은 농담 도중에 적이 뜬다.
        if (cue.act == "engage")
        {
            Campaign.current?.ReleaseSpawns();
            return;
        }

        CutSceneManager.CutScene_ShipObj target = TargetOf(cue.name);

        if (target == null)
        {
            Debug.LogWarning($"[대사] 연출 '{cue.act}'의 대상 '{cue.name}'이 없다. 먼저 spawn해야 한다.");
            return;
        }

        switch (cue.act)
        {
            case "moveTo":
                // moveTo는 **지금 좌표를 고정한다.** 움직이는 표적에는 chase를 쓴다.
                if (TryResolvePoint(cue, out Vector2 to))
                {
                    // 쫓기도 편대도 매 틱 _targetPos를 다시 쓴다 - 안 풀면 이 좌표가
                    // 다음 틱에 덮어써져서 moveTo가 아무 일도 안 한 것처럼 보인다.
                    target.Chase(null);
                    target.Formation(null, Vector2.zero);
                    target.MoveTo(to);
                }
                break;

            case "chase":
                target.Formation(null, Vector2.zero);
                target.Chase(TransformOf(cue.at));
                break;

            case "unchase":
                target.Chase(null);
                break;

            // at을 편대장으로, x·y를 **그 배 기준** 자리로 읽는다. moveTo와 달리 매 틱
            // 다시 계산하므로 편대장이 움직이고 돌아도 간격이 그대로 남는다.
            case "formation":
                target.Chase(null);
                target.Formation(TransformOf(cue.at), new Vector2(cue.x, cue.y));
                break;

            case "unformation":
                target.Formation(null, Vector2.zero);
                break;

            case "aimAt":
                // 좌표든 대상이든 못 찾으면 null을 준다 - 그러면 포탑이 평소대로 가장
                // 가까운 적을 잡는다. 컷신이 조준을 놓는 정식 길이기도 하다.
                target.AimAt(TryResolvePoint(cue, out Vector2 aim) ? aim : (Vector2?)null);
                break;

            case "face":
                // x를 각도로 읽는다. at이 있으면 그 대상을 향하는 각도로 푼다.
                target.Face(FaceAngleFor(cue, target));
                break;

            case "unface":
                target.Face(null);
                break;

            case "hold":
                target.HoldFire(true);
                break;

            case "release":
                target.HoldFire(false);
                break;

            case "fire":
                target.ForceFire(true);
                break;

            case "ceasefire":
                target.ForceFire(false);
                break;

            case "boost":
                target.Boost(true);
                break;

            case "unboost":
                target.Boost(false);
                break;

            case "detonate":
                target.Detonate();
                break;

            case "despawn":
                CutSceneManager.Remove(cue.name);
                break;

            default:
                Debug.LogWarning(
                    $"[대사] 모르는 연출 '{cue.act}'. spawn/despawn/moveTo/chase/unchase/aimAt/" +
                    "face/unface/hold/release/fire/ceasefire/boost/unboost/detonate/look/camera/engage/endCut " +
                    "중 하나여야 한다.");
                break;
        }
    }

    /// <summary>
    /// 지시를 받을 배. **컷신 배가 아니면 그 자리에서 빌린다** - 플레이어 배는 씬의 것이라
    /// spawn된 적이 없고, 대본에서 <c>player</c>라고만 부른다. 빌린 배는 컷신이 끝날 때
    /// 지워지지 않고 조종간만 돌아간다.
    /// </summary>
    private static CutSceneManager.CutScene_ShipObj TargetOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (CutSceneManager.TryGet(name, out CutSceneManager.CutScene_ShipObj had))
            return had;

        return name == "player" ? CutSceneManager.Borrow(name, PlayerShip()) : null;
    }

    /// <summary>
    /// face가 향할 각도. <c>at</c>이 있으면 그 대상을 바라보는 각도를 풀고, 없으면 x를
    /// 각도로 그대로 쓴다 - "저 배 쪽으로 뱃머리를 튼다"가 좌표를 세는 것보다 훨씬 자주 쓴다.
    /// </summary>
    private static float FaceAngleFor(DialogueCue cue, CutSceneManager.CutScene_ShipObj target)
    {
        if (string.IsNullOrWhiteSpace(cue.at) || target.Root == null)
            return cue.x;

        Transform at = TransformOf(cue.at);

        if (at == null)
            return cue.x;

        Vector2 to = (Vector2)at.position - (Vector2)target.Root.transform.position;

        // ShipAi.Turn이 hullAngle과 비교하는 각도와 같은 규약이어야 한다.
        return Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// 연출이 가리키는 점. <see cref="DialogueCue.at"/>이 있으면 **그 대상 위치 + (x,y)**,
    /// 없으면 x·y 그대로다.
    ///
    /// 오프셋으로 두는 것이 요점이다 - "플레이어 앞 300 m"를 절대 좌표로 적으면 배를
    /// 한 번 옮길 때마다 대본의 모든 숫자를 다시 계산해야 한다. x·y가 0이면 예전처럼
    /// 그 대상의 자리 그대로라 이미 쓰인 대본도 안 깨진다.
    /// </summary>
    private static bool TryResolvePoint(DialogueCue cue, out Vector2 point)
    {
        point = new Vector2(cue.x, cue.y);

        if (string.IsNullOrWhiteSpace(cue.at))
            return true;

        Transform at = TransformOf(cue.at);

        if (at == null)
        {
            Debug.LogWarning($"[대사] 연출 대상 '{cue.at}'을 못 찾았다.");
            return false;
        }

        point += (Vector2)at.position;
        return true;
    }

    /// <summary>
    /// 이름 하나를 Transform으로 푼다. <c>player</c>는 예약어이고, 나머지는 컷신 배의
    /// 이름이다. 못 찾으면 null - 부르는 쪽이 그때 무엇을 할지 정한다.
    ///
    /// 좌표(<see cref="TryResolvePoint"/>)와 카메라(<c>look</c>)가 같은 이름 규칙을 써야
    /// 대본에서 "저 배"를 가리키는 말이 하나로 남는다.
    /// </summary>
    private static Transform TransformOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // 플레이어 배의 좌표는 대본을 쓰는 시점에 알 수 없다. 이름으로만 가리킬 수 있다.
        if (name == "player")
        {
            Ship player = PlayerShip();
            return player != null ? player.transform : null;
        }

        return CutSceneManager.TryGet(name, out CutSceneManager.CutScene_ShipObj ship) && ship.Root != null
            ? ship.Root.transform
            : null;
    }

    /// <summary>지금 사람이 모는 배. <see cref="ScriptManager"/>도 함명을 뽑을 때 쓴다.</summary>
    public static Ship PlayerShip()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];

            if (ship != null && ship.IsPlayerControlled)
                return ship;
        }

        return null;
    }

    private static Ship.Team TeamOf(string team) => team switch
    {
        "Ally" => Ship.Team.Ally,
        "Neutral" => Ship.Team.Neutral,
        _ => Ship.Team.Enemy,
    };
}
