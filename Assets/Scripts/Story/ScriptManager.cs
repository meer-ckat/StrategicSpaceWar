using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>대본 한 줄. JsonUtility가 읽으므로 필드는 전부 public이고 이름이 곧 키다.</summary>
[Serializable]
public class DialogueLine
{
    public string message;
    public string author;

    public float duration = 4f;
    public float intensity = 1f;

    /// <summary>presentation profile. radio/control/crew/damage/system/enemy.</summary>
    public string style = "radio";

    /// <summary>0~1. 낮을수록 frame/header 열화가 강해진다. body dropout은 enemy/radio의 극저품질에서만.</summary>
    public float signalQuality = 1f;

    /// <summary>참이면 기존 대사를 밀어내고 이 줄이 난입한다.</summary>
    public bool interrupt;

    /// <summary>다음 줄까지 기다릴 초. 0이면 이 줄의 실제 duration만큼 기다린다.</summary>
    public float wait;

    /// <summary>
    /// 이 줄이 화면에 뜨는 순간 같이 도는 연출. 없으면 아무 일도 안 일어난다.
    ///
    /// **대사와 같은 파일에 적는 것이 요점이다.** "적함 포문 개방"이라고 말하는 줄과 실제로
    /// 포탑이 도는 시점이 두 파일에 나뉘어 있으면 반드시 어긋난다 - 대사 하나를 옮기면
    /// 연출도 같이 옮겨져야 하는데, 옮기는 사람이 그걸 기억할 이유가 없다.
    /// </summary>
    public DialogueCue[] cue;

    /// <summary>
    /// 이 이름의 컷신 배가 **당할 때까지** 다음 줄로 안 넘어간다. 비면 안 기다린다.
    ///
    /// 충각처럼 결과를 시뮬레이션이 내는 연출에 필요하다 - 대본은 "몇 초 뒤에 부딪힌다"를
    /// 알 수가 없다. 거리도 속도도 매번 다르고, 그것이 이 게임에서 충각이 값어치 있는
    /// 이유이기도 하다.
    ///
    /// **상한이 있다.** 창이 빗나가면 그 배는 영영 안 죽고, 그러면 컷신이 거기서 멈춘다.
    /// 연출이 조금 어긋나는 것이 화면이 영원히 안 넘어가는 것보다 낫다.
    /// </summary>
    public string awaitWreck;

    /// <summary>
    /// <see cref="awaitWreck"/>를 들이받는 배. **닿는 순간 표적을 유폭시키고 이 배는
    /// 추적을 놓는다** - 관통해서 지나가는 그림이 그것이다.
    ///
    /// 왜 물리에만 안 맡기나: 창이 452 m/s로 달려들면 한 틱에 15 m를 건너뛴다. 스치는
    /// 각도와 판 배치에 따라 관통이 될 때도, 옆구리를 긁고 지나갈 때도 있다 - 연출은
    /// 그 주사위를 못 받는다. **충각이 일어나는 것은 물리가 정하고, 그 결과가 격침인
    /// 것은 대본이 정한다.** 유폭 자체는 CriticalModule을 통과하는 진짜 경로라 화면에
    /// 나오는 그림은 실전과 같다.
    ///
    /// 비우면 순수하게 기다리기만 한다.
    /// </summary>
    public string awaitRammer;

}

/// <summary>
/// 대본 하나 = <c>StreamingAssets/대사/&lt;이름&gt;.json</c> 파일 하나. def와 같은 규칙이다 -
/// 서로를 이름으로만 알고, 없으면 조용히 아무 일도 안 일어난다.
/// </summary>
[Serializable]
public class DialogueScript
{
    public string defName;

    /// <summary>
    /// 참이면 `lines` 중 **하나만** 고른다. 대본이 아니라 변형 목록이라는 뜻이다.
    ///
    /// 이것 하나로 사건 대사가 살아난다 - 유폭이 스무 번 나는 전투에서 매번 같은 문장이면
    /// 두 번째부터는 글자가 아니라 벽지다. 중첩 배열을 만들지 않아도 되는 이유는 사건
    /// 대사가 원래 한 줄짜리이기 때문이다.
    /// </summary>
    public bool pickOne;

    /// <summary>
    /// 같은 대본이 이 초 안에 다시 안 나온다. 0이면 제한 없음.
    ///
    /// **사건 대사에는 반드시 있어야 한다.** 유폭·선체 절단은 한 틱에 여러 번 날 수 있고,
    /// 그대로 두면 화면이 대사로 덮인다. maxLines가 넘치는 것만 막지 쏟아지는 것은 못 막는다.
    /// </summary>
    public float cooldown;

    public DialogueLine[] lines;
}

/// <summary>
/// 대본을 읽고, 한 줄씩 걸어가며 둘에게 나눠준다. **말은 <see cref="DialogueManager"/>에게,
/// 연출은 <see cref="DramaManager"/>에게.**
///
/// 셋으로 가른 이유가 이것이다 - 예전에는 한 클래스가 JSON을 읽고, 타자기를 돌리고,
/// 컷신 배를 조종하고, RunLog를 구독했다. 그러면 "대사가 안 나온다"는 증상 하나에
/// 용의자가 2500줄이다.
///
/// 여기가 아는 것: 파일이 어디 있고, 어떤 순서로 몇 초씩 기다리는가. **어떻게 보이는지도,
/// 왜 트는지도 모른다.**
/// </summary>
public class ScriptManager : MonoBehaviour
{
    /// <summary>
    /// 대본을 틀고 싶은 쪽이 찾아오는 자리. null이 정상이다 - 대본이 없는 씬에서도
    /// 전투는 돌아야 한다.
    /// </summary>
    public static ScriptManager current;

    [Header("Script")]
    // 시작할 때 재생할 대본. 비우면 아무것도 안 한다.
    public string openingScript = "prologue";

    /// <summary>
    /// 여는 대본을 **런 하나에 한 번만** 튼다. 끄면 씬이 열릴 때마다 나온다(연출을 고치는 중에 쓴다).
    ///
    /// 판정은 <see cref="RunState.Sector"/>다 - 0보다 크면 이미 굴러가던 런이므로
    /// 프롤로그는 지난 이야기다. **<see cref="RunState.Exists"/>를 쓰면 안 된다** -
    /// 그쪽은 배 파일과 진행도 파일이 둘 다 있어야 참인데, 승리 직후 한쪽만 있는 창이
    /// 정상 경로에 항상 열린다(CLAUDE.md "첫 승리의 반쪽은 정상이다"). 그 창에서
    /// 프롤로그가 다시 나오면 원인이 "가끔 다시 나온다"라 재현이 안 된다.
    /// </summary>
    public bool openingOncePerRun = true;

    private const string SystemAuthor = "시스템";
    private const string SystemLineStyle = "system";

    /// <summary>
    /// 씬에 안 붙인다. <see cref="ContactView"/>·<see cref="ShipStatusHud"/>와 같은 자리다 -
    /// 씬을 안 건드리면 "무엇이 대본을 트는가"의 원본이 둘로 갈라질 자리가 없다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Script Manager");
        DontDestroyOnLoad(go);
        go.AddComponent<ScriptManager>();
    }

    /// <summary>
    /// **둘째는 자살한다.** DontDestroyOnLoad라 씬을 다시 열면 Install이 또 돌아 두 번째가
    /// 태어나는데, 그러면 여는 대본이 두 번 나오고 Run 코루틴도 두 벌이 돈다. HUD가
    /// 두 개 뜨는 것과 달리 여기는 화면 순서가 통째로 어긋난다.
    /// </summary>
    private void Awake()
    {
        if (current != null && current != this)
        {
            Destroy(gameObject);
            return;
        }

        current = this;
        ScanChitchat();
    }

    private void OnDestroy()
    {
        if (current == this)
            current = null;
    }

    private void Start() => BeginOpening();

    // 격파 재시작이 씬을 다시 연다. 이 매니저는 DontDestroyOnLoad라 Start가 다시 안
    // 돌고, 그러면 EndAndStartRun을 부르는 사람이 없어져 새 Campaign이
    // waitForCutscene에서 영영 굳는다 - 증상이 "재시작 후 구역이 안 열림"이다.
    // 씬이 열릴 때마다 오프닝 계약을 다시 지킨다. 첫 씬은 sceneLoaded보다 먼저
    // 열려 있어서 Start가 맡는다 - 둘이 겹칠 일은 없다.
    private void OnEnable() =>
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

    private void OnDisable() =>
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;

    private void OnSceneLoaded(
        UnityEngine.SceneManagement.Scene scene,
        UnityEngine.SceneManagement.LoadSceneMode mode) => BeginOpening();

    /// <summary>
    /// **앞서 시작한 대기 코루틴을 먼저 죽인다.** 이 오브젝트는 DontDestroyOnLoad라 코루틴이
    /// 씬 리로드를 살아남는다 - Start가 띄운 대기가 함선 선택 뒤 리로드에서도 살아 있고,
    /// OnSceneLoaded가 하나 더 띄우면 둘이 같은 프레임에 IsOpen이 풀린 것을 보고 각자
    /// Play를 불러 프롤로그가 두 번 나온다. 예전 주석은 "리로드되면 이 코루틴도 함께
    /// 죽는다"였는데 그 전제가 틀렸다.
    /// </summary>
    private Coroutine _opening;

    private void BeginOpening()
    {
        if (_opening != null)
            StopCoroutine(_opening);

        _opening = StartCoroutine(BeginOpeningWhenChosen());
    }

    /// <summary>
    /// **배 선택이 닫힐 때까지 프롤로그를 안 튼다.** 씬 로드 즉시 틀었더니 선택 화면
    /// 위로 대사가 흘렀다 - 대본 코루틴이 WaitForSecondsRealtime이라 timeScale 0도
    /// 무시하고, 다 흐르고 나면 EndCutsceneWhenOpeningDone이 빈 화면을 보고 선택이
    /// 끝나기도 전에 런을 시작했다.
    ///
    /// 선택을 확정하면 씬이 리로드된다. 이 코루틴은 리로드를 살아남으므로(DDOL) BeginOpening이
    /// 새로 띄우기 전에 죽인다 - 안 그러면 둘이 같이 깨어나 프롤로그가 두 번 나온다.
    /// </summary>
    private IEnumerator BeginOpeningWhenChosen()
    {
        bool wasOpen = false;

        while (ShipSelectScreen.IsOpen)
        {
            wasOpen = true;
            yield return null;
        }

        // 선택 화면이 닫히는 것은 확정이고, 확정은 씬을 다시 연다. 리로드 전 마지막 프레임에
        // 여기서 틀면 리로드 뒤 OnSceneLoaded가 또 틀어 프롤로그가 두 번 나온다(로그 0.08초 간격).
        // 이번 판은 리로드 쪽에 맡긴다.
        if (wasOpen)
            yield break;

        // 여는 대본이 없으면 기다릴 것도 없다. **그래도 반드시 한 번은 열어야 한다** -
        // Campaign이 waitForCutscene으로 멈춰 서 있으면, 여기서 안 부르는 순간 그 씬은
        // 영영 전투가 시작되지 않는다. 증상이 "아무 일도 안 일어남"이라 제일 비싸다.
        // 이어하는 런이면 프롤로그는 지난 이야기다. 대본이 비었을 때와 같은 길로 나간다 -
        // 어느 쪽이든 **반드시 한 번은 열어야** Campaign이 waitForCutscene에서 안 굳는다.
        bool alreadyRunning = openingOncePerRun && RunState.Sector > 0;

        if (string.IsNullOrWhiteSpace(openingScript) || alreadyRunning)
        {
            CutSceneManager.EndAndStartRun();
            yield break;
        }

        // 지난 판의 컷신 배를 걷어낸다. 목록이 static이라 씬을 다시 시작해도 살아남는다.
        CutSceneManager.Begin();

        Play(openingScript, PlayerShipName());
        StartCoroutine(EndCutsceneWhenOpeningDone());
    }

    /// <summary>
    /// 여는 대본이 화면에서 다 사라지면 컷신을 걷고 1구역을 연다. 프롤로그가 도는 동안
    /// 이미 전투가 굴러가던 것이 이 기다림이 없어서였다.
    ///
    /// **Texts가 비는 것으로 끝을 안다.** 대본이 여러 줄이면 Run 코루틴이 순서대로 뿌리고,
    /// 마지막 줄은 뿌린 뒤에도 duration만큼 화면에 남는다 - 코루틴이 끝나는 시점을 기다리면
    /// 마지막 대사가 읽히기 전에 전투가 시작된다. 잡담이 Texts가 빌 때까지 기다리는 것과
    /// 같은 규칙이다.
    ///
    /// Play가 실패했으면(대본 파일이 없다) Texts가 처음부터 비어 있어서 즉시 연다. Play는
    /// 첫 줄을 코루틴에 넘기기 **전에** 동기로 Spawn하므로, 성공한 경우 여기 도달할 때는
    /// 이미 차 있다 - 그 순서 덕에 한 프레임 유예를 둘 필요가 없다.
    /// </summary>
    private IEnumerator EndCutsceneWhenOpeningDone()
    {
        // 대본이 아직 돌고 있거나 화면에 대사가 남아 있으면 아직이다. 둘 다 봐야 한다 -
        // 연출 전용 줄에서는 화면이 잠깐 비고(Texts 0), 마지막 줄은 코루틴이 끝난 뒤에도
        // duration만큼 남는다(_running 0). 한쪽만 보면 그 창에서 컷신이 걷힌다.
        while (_running > 0 || LinesOnScreen > 0)
            yield return null;

        CutSceneManager.EndAndStartRun();
    }

    /// <summary>화면에 남아 있는 대사 수. 대사창이 없는 씬이면 0이라 기다림이 즉시 끝난다.</summary>
    private static int LinesOnScreen =>
        DialogueManager.current != null ? DialogueManager.current.Texts.Count : 0;

    /// <summary>
    /// 프롤로그의 <c>{0}</c>에 넣을 이름. <see cref="RunLog"/>가 사건 대사에 쓰는 것과
    /// **같은 규칙**이어야 한다 - 관제가 부르는 함명과 격침 보고의 함명이 다르면 두 대사가
    /// 다른 배 이야기로 읽힌다.
    ///
    /// 못 찾으면 null이 아니라 총칭을 돌려준다. <see cref="Substitute"/>는 arg가 비면
    /// 치환을 아예 안 해서, 화면에 <c>{0}</c>이 글자 그대로 뜬다.
    ///
    /// Start에서 부르는 것이 요점이다. Ship은 Awake에서 목록에 등록되므로 그때는 이미 있다.
    /// </summary>
    private static string PlayerShipName()
    {
        Ship ship = DramaManager.PlayerShip();

        if (ship != null)
            return string.IsNullOrEmpty(ship.shipDefName) ? ship.name : ship.shipDefName;

        return "초계함";
    }

    // =========================================================
    // 대본 파일
    // =========================================================

    private static readonly Dictionary<string, DialogueScript> ScriptCache = new();

    /// <summary>대본 폴더. def와 같은 자리에 산다.</summary>
    public static string ScriptFolder =>
        Path.Combine(Application.streamingAssetsPath, "Dialogue");

    /// <summary>
    /// 대본을 읽는다. **없으면 null이고 그것이 정상이다** - 아직 안 쓴 사건의 대사가 없다고
    /// 게임이 멈추면 대본을 하나 늘릴 때마다 코드를 고쳐야 한다.
    /// </summary>
    public static DialogueScript LoadScript(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (ScriptCache.TryGetValue(name, out DialogueScript cached))
            return cached;

        string path = Path.Combine(ScriptFolder, name + ".json");

        DialogueScript script = null;

        if (File.Exists(path))
        {
            try
            {
                script = JsonUtility.FromJson<DialogueScript>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Story] 대본 '{name}'을 못 읽었다: {e.Message}");
            }
        }

        ScriptCache[name] = script;
        return script;
    }

    /// <summary>에디터에서 JSON을 고친 뒤. def의 Reload와 같은 자리다.</summary>
    public static void ReloadScripts() => ScriptCache.Clear();

    /// <summary>
    /// 잡담 대본 이름. <c>chitchat-*.json</c>을 폴더에서 한 번 긁어 온다.
    ///
    /// 목록 파일을 따로 두지 않는 이유는 def와 같다 - 파일 하나가 곧 대본 하나이고,
    /// 새 잡담은 폴더에 파일을 떨구면 끝이다. 목록을 손으로 들면 파일은 썼는데 목록에
    /// 안 넣는 실수가 반드시 나오고, 그때 증상은 "안 나오는 대사"라 아무 데도 안 걸린다.
    /// </summary>
    private readonly List<string> _chitchat = new();

    /// <summary>무엇을 틀지는 <see cref="DramaManager"/>가 정한다. 여기는 목록만 든다.</summary>
    public IReadOnlyList<string> Chitchat => _chitchat;

    /// <summary>폴더에서 <c>chitchat-*.json</c>을 긁는다. 순서를 정렬해 두는 것이 결정론이다.</summary>
    private void ScanChitchat()
    {
        _chitchat.Clear();

        if (!Directory.Exists(ScriptFolder))
            return;

        string[] files = Directory.GetFiles(ScriptFolder, "chitchat-*.json");

        // GetFiles의 순서는 파일 시스템이 정한다. Pick이 인덱스를 고르므로 그대로 두면
        // 같은 시드가 기계마다 다른 잡담을 낸다.
        Array.Sort(files, StringComparer.Ordinal);

        for (int i = 0; i < files.Length; i++)
            _chitchat.Add(Path.GetFileNameWithoutExtension(files[i]));
    }

    // =========================================================
    // 재생
    // =========================================================

    /// <summary>대본 이름 -> 마지막으로 재생한 시각. 쿨다운이 읽는다.</summary>
    private readonly Dictionary<string, float> _lastPlayed = new();

    /// <summary>변형 고르기의 소금. 같은 틱에 두 번 골라도 같은 문장이 안 나오게 한다.</summary>
    private int _pickSalt;

    /// <summary>재사용 버퍼. 대사는 매 틱 도는 것이 아니지만 할당은 안 하는 편이 낫다.</summary>
    private readonly List<int> _speakable = new();

    /// <summary>
    /// 아직 입이 있는 줄만 모은다. 반환값이 0이면 이 대본은 **통째로** 죽은 자리의 것이다.
    ///
    /// 그때 침묵시키지 않고 시스템이 대신 읽는다. 여러 줄짜리 대본에서 한둘이 빠지는 것은
    /// "저 사람이 없다"로 들리지만, 대본 전체가 사라지면 그냥 버그처럼 들린다 -
    /// 유폭이 났는데 화면이 아무 말도 안 하면 플레이어는 유폭을 못 본 것과 같다.
    ///
    /// 누가 죽었는지는 <see cref="DramaManager"/>가 안다. 여기는 묻기만 한다.
    /// </summary>
    private int CollectSpeakable(DialogueScript script)
    {
        _speakable.Clear();

        for (int i = 0; i < script.lines.Length; i++)
        {
            DialogueLine line = script.lines[i];

            if (line == null || string.IsNullOrEmpty(line.message) || Silenced(line.author))
                continue;

            _speakable.Add(i);
        }

        return _speakable.Count;
    }

    private static bool Silenced(string author) =>
        DramaManager.current != null && DramaManager.current.Silenced(author);

    /// <summary>
    /// 대본을 띄운다. <paramref name="arg"/>는 각 줄의 <c>{0}</c>을 갈아끼운다 -
    /// "{0} 격침 확인" 같은 사건 대사를 위해서다.
    ///
    /// **대본이 있었으면 true다.** 쿨다운에 걸려 실제로 아무것도 안 띄웠어도 true인 것이
    /// 중요하다 - 부르는 쪽이 이걸로 폴백을 정하는데, 쿨다운을 "없음"으로 읽으면 막아둔
    /// 대사가 공용 대본으로 새어 나온다.
    /// </summary>
    public bool Play(string scriptName, string arg = null)
    {
        DialogueScript script = LoadScript(scriptName);

        if (script?.lines == null || script.lines.Length == 0)
            return false;

        if (!OffCooldown(scriptName, script))
            return true;

        // 진단. 같은 대본이 두 번 뜨는 보고가 있는데 코드에서는 원인이 안 보인다 - 누가
        // 언제 부르는지가 콘솔에 남아야 다음 보고가 데이터를 들고 온다. 잡히면 지운다.
        Debug.Log($"[대사] Play '{scriptName}' t={Time.unscaledTime:0.00} running={_running} " +
                  $"from {new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.DeclaringType?.Name}." +
                  $"{new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name}");

        // 죽은 자리의 줄은 후보에서 빠진다. 하나도 안 남으면 시스템이 대신 읽는다.
        bool viaSystem = CollectSpeakable(script) == 0;

        // 변형 목록이면 한 줄만. 코루틴을 안 타므로 기다림도 없다.
        if (script.pickOne)
        {
            DialogueLine one = viaSystem
                ? script.lines[Pick(scriptName, script.lines.Length)]
                : script.lines[_speakable[Pick(scriptName, _speakable.Count)]];

            if (one != null && !string.IsNullOrEmpty(one.message))
                Speak(one, arg, viaSystem);

            return true;
        }

        StartCoroutine(Run(script, arg, viaSystem));
        return true;
    }

    /// <summary>대본이 있으면 재생하고 있었는지 알려준다. 팀별 대본 -> 공용 대본 폴백에 쓴다.</summary>
    public bool PlayIfExists(string scriptName, string arg) => Play(scriptName, arg);

    /// <summary>
    /// 한 줄을 화면으로 넘긴다. **여기가 대사가 이 클래스를 떠나는 유일한 문이다** -
    /// 죽은 자리를 시스템으로 갈아끼우는 규칙이 두 자리(pickOne, Run)에 있었고, 그래서
    /// 한쪽만 고치면 변형 대사만 죽은 사람 목소리로 나갔다.
    /// </summary>
    private static Dialogue Speak(DialogueLine line, string arg, bool viaSystem)
    {
        if (DialogueManager.current == null)
            return null;

        return DialogueManager.current.Spawn(
            Substitute(line.message, arg),
            viaSystem ? SystemAuthor : line.author,
            line.duration,
            line.intensity,
            viaSystem ? SystemLineStyle : line.style,
            line.signalQuality,
            line.interrupt);
    }

    // arg가 없다고 치환을 건너뛰면 화면에 "{0} 응답 없음." 같은 원문 태그가 그대로
    // 샌다 - 플레이어가 볼 값이 아니다. 대신 [검열됨]으로 메운다. {0}이 없는 메시지에는
    // Replace가 아무것도 안 해서 부작용이 없다.
    private static string Substitute(string message, string arg)
        => message.Replace("{0}", string.IsNullOrEmpty(arg) ? "[검열됨]" : arg);

    private bool OffCooldown(string scriptName, DialogueScript script)
    {
        if (script.cooldown <= 0f)
            return true;

        float now = Time.unscaledTime;

        if (_lastPlayed.TryGetValue(scriptName, out float last) && now - last < script.cooldown)
            return false;

        _lastPlayed[scriptName] = now;
        return true;
    }

    /// <summary>
    /// 변형 중 하나를 고른다. <c>UnityEngine.Random</c>을 안 쓰는 것은 이 리포의 규칙이다.
    /// 대사는 시뮬레이션이 아니라 재현성에 걸리진 않지만, 난수 출처가 둘이 되는 순간
    /// "어디서 나온 값인가"를 매번 확인해야 한다.
    ///
    /// <c>_pickSalt</c>가 있어야 같은 틱에 두 번 골라도 다른 값이 나온다 - 유폭 연쇄가
    /// 한 틱에 몰리면 tick만으로는 전부 같은 문장이 된다.
    /// </summary>
    public int Pick(string key, int count)
    {
        var rng = new DeterministicRng(
            Ballistics.Hash(DialogueManager.StableHash(key), Core.TickManager.currentTick, _pickSalt++));

        return (int)(rng.NextUInt() % (uint)count);
    }

    /// <summary>
    /// 지금 도는 <see cref="Run"/>의 수. **화면의 대사만으로는 대본의 끝을 못 안다** -
    /// 연출 전용 줄(대사 없는 줄)에서는 화면이 잠깐 빌 수 있고, 그 순간을 끝으로 읽으면
    /// 컷신이 중간에 걷히고 전투가 시작된다.
    /// </summary>
    /// <summary>대본 하나가 줄을 아직 넘기는 중인가. GameManager가 격파 뒤 재시작을
    /// 미룰지 판단하는 데 쓴다 - battle-lost 같은 유언 대본이 다 읽히기 전에
    /// 씬이 넘어가면 안 된다.</summary>
    public bool IsRunning => _running > 0;

    /// <summary>
    /// 대본이 줄을 넘기는 중이거나 그 줄이 아직 화면에 있는가. **IsRunning보다 늦게 꺼진다** -
    /// 마지막 줄을 넘긴 뒤에도 duration만큼 화면에 남는데, 그 사이에 적이 뜨면 마지막
    /// 문장을 못 읽는다. Campaign이 이걸 보고 소환을 미룬다.
    /// </summary>
    public bool IsBusy => _running > 0 || LinesOnScreen > 0;

    private int _running;

    /// <summary>
    /// 대본을 한 줄씩 걸어간다. 말은 <see cref="DialogueManager"/>로, 연출은
    /// <see cref="DramaManager"/>로 나간다 - 이 코루틴이 아는 것은 순서와 시간뿐이다.
    /// </summary>
    /// <summary>
    /// 줄 사이 대기. **Space가 끊는다** - 다음 줄로 바로 간다. 큐는 줄 머리에서 이미 돌았으므로
    /// 소환·이동 순서는 그대로고, 잘리는 건 읽는 시간뿐이다. awaitWreck(시뮬 결과 대기)은 이걸
    /// 안 타서 못 넘긴다 - 그건 연출이 아니라 사실을 기다리는 것이다.
    /// 누른 프레임을 소비해야 한다: 안 그러면 한 번 누른 Space가 이어지는 줄 전부를 한 프레임에 삼킨다.
    /// </summary>
    private static IEnumerator WaitOrSkip(float seconds)
    {
        float until = Time.unscaledTime + seconds;

        // 이번 프레임의 Space는 이 줄을 띄운 입력일 수 있다 - 한 프레임 건너뛰고 듣는다.
        yield return null;

        while (Time.unscaledTime < until)
        {
            if (UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame)
                yield break;

            yield return null;
        }
    }

    private IEnumerator Run(DialogueScript script, string arg, bool viaSystem)
    {
        _running++;

        try
        {
        for (int i = 0; i < script.lines.Length; i++)
        {
            DialogueLine line = script.lines[i];

            if (line == null)
                continue;

            // **배 선택이 열려 있는 동안은 한 줄도 안 나간다.**
            //
            // 프롤로그는 BeginOpeningWhenChosen이 애초에 안 틀지만, 구역 대사(sector-entered-N)는
            // 그 문을 안 지난다 - 이어하는 런에서 선택 화면과 1구역 진입이 겹치면 대본이 그 위로 흐른다.
            // 대사창은 그 화면에서 안 그리므로(DialogueManager) 줄이 보이지도 않은 채 수명만 흘러,
            // 화면을 닫을 때는 이미 다 지나가 있다. 안 보이는 대사가 소모되는 것이 제일 나쁜 실패다.
            //
            // **여기가 맞는 자리다.** 코루틴이 WaitForSecondsRealtime이라 timeScale 0을 무시하므로
            // 화면 쪽에서 멈출 방법이 없고, Play를 막으면 이미 시작된 대본이 못 멈춘다.
            // 줄 사이에서 멈추면 연출 큐(RunCues)도 같이 멈춰서 적함 소환이 빈 화면에 안 뜬다.
            while (ShipSelectScreen.IsOpen)
                yield return null;

            // **연출은 대사보다 위다.** 말할 사람이 죽어도(Silenced) 세계에서 일어나는 일은
            // 일어난다 - 적함은 함내 누가 살았는지와 무관하게 나타난다. 아래로 내렸더니
            // "전술"이 조용한 판에서 적함 spawn이 통째로 빠지고, 그 뒤의 모든 지시가
            // 대상을 잃어 경고만 쏟아졌다.
            //
            // 말이 없는 줄(message가 빈 줄)도 여기까지 온다. 그것이 **연출 전용 줄**이고,
            // 유폭처럼 대사 없이 시간만 필요한 장면을 그걸로 잡는다.
            if (DramaManager.current != null)
                DramaManager.current.RunCues(line.cue);

            // viaSystem이면 이미 전부 죽은 자리라 거를 것이 없다. 아니면 죽은 줄만 빠지고
            // 나머지는 자기 목소리 그대로 나간다 - 배가 통째로 조용해지는 것이 아니라
            // 한 사람 몫이 사라진다.
            bool speaks = !string.IsNullOrEmpty(line.message)
                       && (viaSystem || !Silenced(line.author));

            if (!speaks)
            {
                // 말은 안 해도 시간은 흐른다. 연출 전용 줄의 wait이 곧 그 장면의 길이다 -
                // 여기서 안 기다리면 유폭 셋이 한 프레임에 몰린다.
                if (line.wait > 0f)
                    yield return WaitOrSkip(line.wait);

                continue;
            }

            Dialogue spawned = Speak(line, arg, viaSystem);

            // **duration이 아니라 타이핑 시간을 기다린다.** duration은 이 줄이 화면에
            // 머무는 시간이라, 그걸 기다리면 앞줄이 사라진 뒤에야 다음이 와서 통신이
            // 절대 안 겹친다. 두 값을 갈라 놓아야 뒤에서 앞줄이 아직 살아 있는 채로
            // 다음 줄이 올라온다 - 이미 있던 스택 연출(stackKick, depthAlpha)이 그제서야
            // 할 일이 생긴다.
            //
            // 대사창이 없는 씬이면 spawned가 null이다. 그때는 대본의 wait만 본다.
            float typed = spawned != null ? spawned.typingDuration + LineGap : 0f;
            float wait = line.wait > 0f ? line.wait : typed;

            SoundManager.AudioShot("Communication", 1, UnityEngine.Random.Range(0.5f, 1.1f));

            yield return WaitOrSkip(Mathf.Max(0.05f, wait));

            // 시뮬레이션이 결과를 낼 때까지. 대사보다 아래인 것이 요점이다 - 줄이 뜨고
            // 읽히는 동안 창이 날아가고, 다 읽은 뒤에 그 결과를 기다린다.
            if (!string.IsNullOrWhiteSpace(line.awaitWreck))
                yield return DramaManager.AwaitWreck(line.awaitWreck, line.awaitRammer);

        }
        }
        finally
        {
            _running--;
        }
    }

    /// <summary>앞줄 타이핑이 끝나고 다음 줄이 오기까지의 사이. 값의 주인은 대사창이다.</summary>
    private static float LineGap =>
        DialogueManager.current != null ? DialogueManager.current.lineGap : 0.6f;

    /// <summary>
    /// 돌고 있는 대본을 끊고 쿨다운을 비운다. 화면을 비우는 것은
    /// <see cref="DialogueManager.Clear"/>가 따로 한다.
    /// </summary>
    public void Clear()
    {
        StopAllCoroutines();
        _running = 0;

        // 쿨다운도 같이 간다. 새 전투인데 지난 전투의 유폭 때문에 첫 유폭이 조용하면
        // 원인이 화면에 안 보인다.
        _lastPlayed.Clear();
    }
}
