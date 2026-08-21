using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IMGUI;
using UnityEngine;

/// <summary>
/// 화면에 쌓이는 대사 한 줄. **순수 데이터다** - GameObject도 GUIItem도 없다.
///
/// 애니메이션 상태를 여기가 들고 최종값만 매 프레임 라벨에 대입하는 것이 중요하다.
/// <see cref="GUITween"/>을 쓰면 핸들러가 GUIItem에 붙는데, 즉시 모드 위젯은 선언을
/// 그만두는 순간 사라지므로 트윈이 완주하지 못하고 트윈 사전에만 남는다.
/// </summary>
public class Dialogue
{
    /// <summary>즉시 모드 위젯 id. 같은 대사가 두 번 나와도 겹치지 않게 일련번호를 쓴다.</summary>
    public readonly string id;

    public readonly string message;
    public readonly string author;

    /// <summary>어느 런타임 요청에서 나온 줄인지. 0은 외부에서 Spawn으로 직접 띄운 줄.</summary>
    public readonly int requestId;

    /// <summary>radio/system/story 같은 논리 채널. 채널은 재생 순서를 통제하고, 화면 스택은 공유한다.</summary>
    public readonly string channel;

    public float duration;
    public float alpha;

    /// <summary>지금 있는 자리와 가야 할 자리. 둘 다 화면 좌표(픽셀, 왼쪽 위 기준).</summary>
    public Vector2 pos;
    public Vector2 targetPos;
    public Vector2 velocity;

    public bool leaving;

    // 연출 상태
    public float age;
    public float intensity;

    /// <summary>
    /// 이 줄이 실제로 차지하는 높이. **0이면 아직 못 쟀다.** GUIStyle.CalcHeight는 GUI
    /// 함수라 OnGUI 안에서만 부를 수 있어서, 재는 자리와 쓰는 자리가 한 프레임 갈린다.
    /// </summary>
    public float height;

    /// <summary>
    /// 글자가 실제로 차지하는 폭. 뒤에 까는 판이 이걸 쓴다.
    /// </summary>
    public float width;

    // 타이핑
    public int visibleCharacters;
    public int revealCharacters;
    public float revealAccumulator;
    public float revealSpeed;

    /// <summary>글자가 다 찍히는 데 걸리는 시간. director의 다음 줄 타이밍이 이 값을 쓴다.</summary>
    public float typingDuration;

    /// <summary>지금 그리고 있는 문자열과 그것이 몇 글자짜리였나. 안 바뀌었으면 안 만든다.</summary>
    public string rendered = string.Empty;
    public int renderedAt = -1;

    public Dialogue(
        string id,
        string message,
        string author,
        float duration,
        Vector2 startPos,
        int visibleCharacters,
        float intensity,
        int requestId = 0,
        string channel = "radio",
        float revealSpeed = 0f)
    {
        this.id = id;
        this.message = message;
        this.author = author;
        this.duration = duration;
        this.visibleCharacters = visibleCharacters;
        this.intensity = intensity;
        this.requestId = requestId;
        this.channel = string.IsNullOrWhiteSpace(channel) ? "radio" : channel;
        this.revealSpeed = revealSpeed;

        pos = startPos;
        targetPos = startPos;
        velocity = Vector2.zero;

        alpha = 0f;
        age = 0f;
        leaving = false;
        revealCharacters = 0;
        revealAccumulator = 0f;
    }
}

/// <summary>
/// 간단한 런타임 조건. JSON이므로 문자열 연산자로 둔다.
/// 지원: exists, missing, eq, neq, contains, gt, gte, lt, lte.
/// 값은 요청 인자 -> 전역 blackboard 순서로 찾는다.
/// </summary>
[Serializable]
public class DialogueCondition
{
    public string key;
    public string op;
    public string value;
}

/// <summary>대본 한 줄. 기존 JSON과 하위 호환된다.</summary>
[Serializable]
public class DialogueLine
{
    /// <summary>직접 쓸 문장. messageKey가 있으면 localization 실패 시 fallback으로 쓴다.</summary>
    public string message;
    public string messageKey;

    public string author;
    public string authorKey;

    public float duration = 4f;
    public float intensity = 1f;

    /// <summary>다음 줄까지 기다릴 초. 0이면 실제 타이핑 시간 + manager.lineGap.</summary>
    public float wait;

    /// <summary>0이면 manager.typeSpeed.</summary>
    public float typingSpeed;

    /// <summary>0이면 manager.minimumHoldTime.</summary>
    public float minimumHold;

    /// <summary>
    /// 실제 오디오를 여기서 로드하지 않는다. ID만 이벤트로 내보낸다.
    /// FMOD/Wwise/AudioSource 어느 쪽이든 바깥 bridge가 받는다.
    /// </summary>
    public string voice;

    /// <summary>
    /// 참이면 voice duration resolver가 아는 길이까지 다음 줄을 미룬다.
    /// 실제 voice 재생은 여전히 외부 bridge 책임이다.
    /// </summary>
    public bool waitForVoice;

    /// <summary>voice 끝난 뒤 다음 줄 전까지 추가 여백.</summary>
    public float voiceTail = 0.08f;

    /// <summary>줄이 시작되는 순간 발행하는 게임플레이/시네마틱 신호 ID.</summary>
    public string signal;

    /// <summary>조건이 하나라도 거짓이면 이 줄은 대기 없이 건너뛴다.</summary>
    public DialogueCondition[] when;
}

/// <summary>
/// 대본 하나 = StreamingAssets/대사/&lt;이름&gt;.json.
/// 기존 pickOne/cooldown/lines는 그대로 읽고, director 메타데이터만 선택적으로 얹는다.
/// </summary>
[Serializable]
public class DialogueScript
{
    public string defName;

    /// <summary>동시에 하나의 시퀀스만 실행되는 논리 채널. 비우면 radio.</summary>
    public string channel;

    /// <summary>높을수록 먼저 재생되고, 현재 대본보다 높으면 lockChannel이 아닌 한 선점한다.</summary>
    public int priority;

    /// <summary>
    /// enqueue(기본), drop, coalesce, replace.
    /// replace도 현재 대본이 lockChannel이면 강제 종료하지 않고 큐 맨 앞에 선다.
    /// </summary>
    public string queueMode;

    /// <summary>참이면 더 높은 우선순위도 이 대본을 중간에 자를 수 없다.</summary>
    public bool lockChannel;

    /// <summary>선점당했을 때 이미 화면에 뜬 이 대본의 줄도 퇴장시킬지.</summary>
    public bool dismissOnInterrupt;

    /// <summary>
    /// 큐에서 이 초보다 오래 기다린 요청은 폐기한다.
    /// 0이면 manager.defaultQueueMaxAge, 음수면 무제한.
    /// </summary>
    public float maxQueueAge;

    /// <summary>한 run 동안 최초 1회만 받아들인다. ResetRunState에서 초기화한다.</summary>
    public bool oncePerRun;

    /// <summary>참이면 lines 중 조건을 통과한 후보 하나만 고른다.</summary>
    public bool pickOne;

    /// <summary>같은 대본 재요청 쿨다운. 큐에 실제로 받아들여졌을 때만 소비한다.</summary>
    public float cooldown;

    /// <summary>대본 자체의 진입 조건.</summary>
    public DialogueCondition[] when;

    public DialogueLine[] lines;
}

/// <summary>UI backlog / 디버그 / 텔레메트리에 그대로 넘길 불변 기록.</summary>
public sealed class DialogueHistoryEntry
{
    public readonly int requestId;
    public readonly string script;
    public readonly string channel;
    public readonly string author;
    public readonly string message;
    public readonly string voice;
    public readonly float time;

    public DialogueHistoryEntry(
        int requestId,
        string script,
        string channel,
        string author,
        string message,
        string voice,
        float time)
    {
        this.requestId = requestId;
        this.script = script;
        this.channel = channel;
        this.author = author;
        this.message = message;
        this.voice = voice;
        this.time = time;
    }
}

/// <summary>
/// 로컬라이제이션을 특정 패키지에 묶지 않기 위한 단 하나의 seam.
/// 프로젝트 시작 때 Resolve만 지정하면 된다.
/// </summary>
public static class DialogueLocalization
{
    public static Func<string, string> Resolve;

    public static string Get(string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
            return fallback ?? string.Empty;

        if (Resolve == null)
            return fallback ?? key;

        try
        {
            string value = Resolve(key);
            return string.IsNullOrEmpty(value) ? (fallback ?? key) : value;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Story] localization '{key}' 실패: {e.Message}");
            return fallback ?? key;
        }
    }
}

/// <summary>
/// VO middleware가 clip 길이를 알고 있다면 이 delegate만 연결한다.
/// null 또는 0 이하를 반환하면 자막 타이밍만 사용한다.
/// </summary>
public static class DialogueVoiceTiming
{
    public static Func<string, float> ResolveDuration;

    public static float DurationOf(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId) || ResolveDuration == null)
            return 0f;

        try
        {
            return Mathf.Max(0f, ResolveDuration(voiceId));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Story] voice duration '{voiceId}' 실패: {e.Message}");
            return 0f;
        }
    }
}


public class StoryScriptManager : MonoBehaviour
{
    // 그리기 순서. GUIManager는 Layer로 정렬하고, 동률이면 **등록 순서**로 그린다 -
    // 즉시 모드 캐시에서 등록 순서는 "처음 선언된 프레임"이라 창을 늘려 패턴 행이 새로
    // 생기면 그 행이 대사 위에 올라온다. 명시하면 그런 일이 없다.
    private const int PatternLayer = -100;
    private const int PlateLayer = -1;
    private const int MessageLayer = 0;
    private const int AuthorLayer = 1;

    /// <summary>author 한 줄의 높이. 판이 author까지 덮으려면 알아야 한다.</summary>
    private const float AuthorHeight = 20f;

    [Header("Script")]
    // 시작할 때 재생할 대본. 비우면 아무것도 안 한다.
    public string openingScript = "prologue";

    /// <summary>전투·격침 같은 사건이 대사를 띄우게 할 것인가.</summary>
    public bool reactToSimulation = true;

    /// <summary>
    /// 앞줄 타이핑이 끝나고 다음 줄이 오기까지의 사이. **이 값이 duration보다 작아서
    /// 통신이 겹친다** - 크게 잡으면 한 번에 한 줄씩 나오는 옛날 동작으로 돌아간다.
    /// 대본의 <see cref="DialogueLine.wait"/>이 0이 아니면 그쪽이 이긴다.
    /// </summary>
    public float lineGap = 0.6f;

    /// <summary>
    /// 화면에 동시에 둘 수 있는 줄 수. 겹치기 시작하면 duration이 길고 사이가 짧은 대본
    /// 하나로 줄이 화면 밖까지 쌓이므로, 넘치면 제일 오래된 줄부터 내보낸다.
    /// </summary>
    public int maxLines = 5;

    [Header("Layout")]
    public Vector2 origin = new(40f, 120f);

    /// <summary>줄 폭과 **최소** 높이. 실제 높이는 스타일이 재고, 이 값이 하한이다.</summary>
    public Vector2 lineSize = new(1000f, 24f);

    /// <summary>줄과 줄 **사이** 여백. 예전에는 줄 높이까지 포함한 간격이라 긴 대사가 겹쳤다.</summary>
    public float spacing = 22f;

    [Header("Movement")]
    public float moveSmoothTime = 0.12f;
    public float spawnOffset = 28f;
    public float leaveOffset = 35f;

    [Header("Fade")]
    public float fadeInSpeed = 6f;
    public float fadeOutSpeed = 4f;

    [Header("Typing")]
    public float typeSpeed = 42f;
    public float minimumHoldTime = 0.5f;

    [Header("Impact")]
    public float enterPunch = 8f;
    public float enterPunchDuration = 0.25f;
    public float shakeAmount = 2f;
    public float stackKick = 5f;

    /// <summary>author 라벨이 등장할 때 부풀었다 돌아오는 배율. 메시지에는 안 쓴다 - 아래 참조.</summary>
    public float authorPunchScale = 0.35f;

    [Header("Screen Shake")]
    // 이 세기 이상인 줄만 화면을 흔든다. 전부 흔들면 아무것도 안 흔든 것과 같다.
    public float screenShakeThreshold = 1.4f;
    public float screenShakeStrength = 7f;
    public float screenShakeDuration = 0.22f;

    [Header("Plate")]
    public bool drawPlate = true;

    /// <summary>
    /// 대사 뒤에 까는 판. **알파는 이 색에 들어 있고, 줄의 페이드가 한 번 더 곱해진다** -
    /// 판이 글자보다 늦게 사라지면 빈 판이 잠깐 떠 있다.
    /// </summary>
    public Color plateColor = new(0.3f, 0.04f, 0.06f, 0.78f);

    public Vector2 platePadding = new(12f, 7f);

    [Header("Pattern Background")]
    public bool drawPattern = true;

    /// <summary>배경이 들고 나는 속도(초당). 대사 페이드보다 느려야 배경이 따라오는 것으로 읽힌다.</summary>
    public float patternFadeSpeed = 3f;

    public string patternText = "ATRIA NAVY";
    public float patternSpeed = 60f;
    public float patternRowHeight = 78f;
    public float patternDiagonalOffset = 70f;
    public float patternOpacity = 0.08f;
    public float patternStartXPadding = 800f;
    public int patternFontSize = 28;

    [Header("Text")]
    public int fontSize = 18;
    public int authorFontSize = 14;

    /// <summary>author를 메시지 **아래** 어디에 놓을지. y는 메시지 높이에 더해진다.</summary>
    public Vector2 authorOffset = new(4f, 2f);

    public List<Dialogue> Texts = new();

    private int _nextId;
    private bool _layoutDirty;

    /// <summary>배경 패턴이 지금 얼마나 나와 있나. 0이면 선언 자체를 안 한다.</summary>
    private float _patternAlpha;

    private GUIStyle _messageStyle;
    private GUIStyle _authorStyle;
    private GUIStyle _patternStyle;

    private string _patternLineCache;

    private static readonly Dictionary<string, DialogueScript> ScriptCache = new();

    /// <summary>대본 이름 -> 마지막으로 재생한 시각. 쿨다운이 읽는다.</summary>
    private readonly Dictionary<string, float> _lastPlayed = new();

    /// <summary>변형 고르기의 소금. 같은 틱에 두 번 골라도 같은 문장이 안 나오게 한다.</summary>
    private int _pickSalt;


    [Header("Director")]
    [Tooltip("채널 하나에 대기시킬 최대 요청 수. 초과하면 우선순위가 가장 낮은 요청부터 희생된다.")]
    public int maxQueuedRequests = 24;

    [Tooltip("script.maxQueueAge가 0일 때 쓰는 기본 대기 수명. 음수면 무제한.")]
    public float defaultQueueMaxAge = 8f;

    [Tooltip("backlog에 남길 최대 줄 수.")]
    public int historyLimit = 200;

    [Header("Accessibility")]
    public bool disableTypewriter;
    public bool reduceMotion;

    /// <summary>voice ID, channel, intensity. 실제 재생기는 구독만 하면 된다.</summary>
    public event Action<string, string, float> onVoiceRequested;

    /// <summary>signal ID, script name, request id.</summary>
    public event Action<string, string, int> onSignal;

    public event Action<string, int> onScriptStarted;
    public event Action<string, int> onScriptEnded;
    public event Action<DialogueHistoryEntry> onHistoryAdded;

    private sealed class DialogueRequest
    {
        public int id;
        public long serial;
        public string scriptName;
        public DialogueScript script;
        public Dictionary<string, string> args;
        public float createdAt;
        public int priority;
        public int lineIndex;
        public int pickedLine = -1;
    }

    private sealed class ChannelState
    {
        public DialogueRequest active;
        public float wait;
        public readonly List<DialogueRequest> queue = new();
    }

    private int _nextRequestId = 1;
    private long _requestSerial;
    private int _directorEpoch;

    private readonly Dictionary<string, ChannelState> _channels =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _playedOnce =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _variables =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<DialogueHistoryEntry> _history = new();

    // event callback이 Play()를 호출해 _channels를 늘려도 foreach가 깨지지 않게 매 프레임 snapshot한다.
    private readonly List<ChannelState> _channelScratch = new();

    public IReadOnlyList<DialogueHistoryEntry> History => _history;

    // =========================================================
    // 수명
    // =========================================================

    /// <summary>
    /// 씬의 대사창. <see cref="Battle"/>·<see cref="Campaign"/>과 같은 규칙으로 둔다 -
    /// 대사를 띄우고 싶은 쪽이 이 오브젝트를 찾아다니지 않아도 되게.
    ///
    /// null이 정상이다. 대사창이 없는 씬에서도 전투는 돌아야 한다.
    /// </summary>
    public static StoryScriptManager current;

    private void OnEnable()
    {
        current = this;

        if (!reactToSimulation)
            return;

        RunLog.onEntry += OnRunEntry;
        Battle.onAnyEnd += OnBattleEnd;
    }

    private void OnDisable()
    {
        RunLog.onEntry -= OnRunEntry;
        Battle.onAnyEnd -= OnBattleEnd;

        if (current == this)
            current = null;
    }

    private void Start()
    {
        if (!string.IsNullOrWhiteSpace(openingScript))
            Play(openingScript, PlayerShipName());
    }

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
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship ship = Ship.All[i];

            if (ship == null || !ship.IsPlayerControlled)
                continue;

            return string.IsNullOrEmpty(ship.shipDefName) ? ship.name : ship.shipDefName;
        }

        return "초계함";
    }

    // =========================================================
    // 대본 / director
    // =========================================================

    /// <summary>대본 폴더. def와 같은 자리에 산다.</summary>
    public static string ScriptFolder =>
        Path.Combine(Application.streamingAssetsPath, "대사");

    /// <summary>
    /// 대본을 읽는다. 없으면 null이고 정상이다.
    /// 읽을 때 한 번만 구조 검증하고, 이후에는 cache를 탄다.
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

                if (script != null)
                {
                    NormalizeScript(script);
                    ValidateScript(name, script);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Story] 대본 '{name}'을 못 읽었다: {e.Message}");
            }
        }

        ScriptCache[name] = script;
        return script;
    }

    public static void Warmup(params string[] scriptNames)
    {
        if (scriptNames == null)
            return;

        for (int i = 0; i < scriptNames.Length; i++)
            LoadScript(scriptNames[i]);
    }

    /// <summary>에디터에서 JSON을 고친 뒤.</summary>
    public static void ReloadScripts() => ScriptCache.Clear();

    private static void NormalizeScript(DialogueScript script)
    {
        if (string.IsNullOrWhiteSpace(script.channel))
            script.channel = "radio";

        if (string.IsNullOrWhiteSpace(script.queueMode))
            script.queueMode = "enqueue";
    }

    private static void ValidateScript(string fileName, DialogueScript script)
    {
        if (script.lines == null || script.lines.Length == 0)
        {
            Debug.LogWarning($"[Story] '{fileName}' lines가 비어 있다.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(script.defName) &&
            !string.Equals(fileName, script.defName, StringComparison.Ordinal))
        {
            Debug.LogWarning(
                $"[Story] 파일명 '{fileName}'과 defName '{script.defName}'이 다르다.");
        }

        string mode = script.queueMode.ToLowerInvariant();

        if (mode != "enqueue" &&
            mode != "drop" &&
            mode != "coalesce" &&
            mode != "replace")
        {
            Debug.LogWarning(
                $"[Story] '{fileName}' queueMode '{script.queueMode}'는 알 수 없다. enqueue로 처리한다.");
            script.queueMode = "enqueue";
        }

        for (int i = 0; i < script.lines.Length; i++)
        {
            DialogueLine line = script.lines[i];

            if (line == null)
                continue;

            if (line.duration < 0f)
                Debug.LogWarning($"[Story] '{fileName}' lines[{i}].duration < 0");

            if (line.wait < 0f)
                Debug.LogWarning($"[Story] '{fileName}' lines[{i}].wait < 0");
        }
    }

    /// <summary>
    /// 기존 API. {0} 치환도 그대로 지원한다.
    /// 반환값은 예전과 동일하게 "대본 파일이 존재한다"의 뜻이다.
    /// 조건/쿨다운/큐 정책 때문에 실제로 재생되지 않아도 true다.
    /// </summary>
    public bool Play(string scriptName, string arg = null)
    {
        Dictionary<string, string> args = null;

        if (!string.IsNullOrEmpty(arg))
        {
            args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["0"] = arg,
            };
        }

        return PlayWithArgs(scriptName, args);
    }

    /// <summary>
    /// named token을 쓰는 새 API. 예: {ship}, {sector}, {weapon}.
    /// priorityOverride는 int.MinValue면 JSON 값을 그대로 쓴다.
    /// 이름을 Play로 오버로드하지 않은 이유는 Play("x", null) 호출의 모호성을 만들지 않기 위해서다.
    /// </summary>
    public bool PlayWithArgs(
        string scriptName,
        IDictionary<string, string> args,
        int priorityOverride = int.MinValue)
    {
        DialogueScript script = LoadScript(scriptName);

        if (script?.lines == null || script.lines.Length == 0)
            return false;

        Dictionary<string, string> copied = CopyArgs(args);

        if (!ConditionsPass(script.when, copied))
            return true;

        if (script.oncePerRun && _playedOnce.Contains(scriptName))
            return true;

        if (!OffCooldown(scriptName, script))
            return true;

        DialogueRequest request = new()
        {
            id = _nextRequestId++,
            serial = _requestSerial++,
            scriptName = scriptName,
            script = script,
            args = copied,
            createdAt = Time.unscaledTime,
            priority = priorityOverride == int.MinValue
                ? script.priority
                : priorityOverride,
        };

        bool accepted = Schedule(request);

        if (accepted)
        {
            if (script.cooldown > 0f)
                _lastPlayed[scriptName] = Time.unscaledTime;

            if (script.oncePerRun)
                _playedOnce.Add(scriptName);
        }

        return true;
    }

    private static Dictionary<string, string> CopyArgs(IDictionary<string, string> args)
    {
        if (args == null || args.Count == 0)
            return null;

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> pair in args)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            copy[pair.Key] = pair.Value ?? string.Empty;
        }

        return copy;
    }

    private static int PriorityOf(DialogueRequest request)
        => request?.priority ?? 0;

    private bool Schedule(DialogueRequest request)
    {
        string channel = request.script.channel;
        ChannelState state = GetChannel(channel);
        string mode = request.script.queueMode?.ToLowerInvariant() ?? "enqueue";

        if (state.active == null)
        {
            BeginRequest(state, request);
            return true;
        }

        if (mode == "drop")
            return false;

        if (mode == "coalesce" && ContainsScript(state, request.scriptName))
            return false;

        int incoming = PriorityOf(request);
        int currentPriority = PriorityOf(state.active);

        bool wantsReplace = mode == "replace";
        bool higherPriority = incoming > currentPriority;

        if ((wantsReplace || higherPriority) && !state.active.script.lockChannel)
        {
            InterruptActive(state);

            // onScriptEnded callback이 같은 채널에 뭔가를 시작했을 수 있다.
            if (state.active == null)
            {
                BeginRequest(state, request);
                return true;
            }

            return Enqueue(state, request, wantsReplace);
        }

        // replace가 lockChannel에 막혔다면 일반 큐보다 먼저 기다린다.
        return Enqueue(state, request, wantsReplace);
    }

    private ChannelState GetChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            channel = "radio";

        if (!_channels.TryGetValue(channel, out ChannelState state))
        {
            state = new ChannelState();
            _channels.Add(channel, state);
        }

        return state;
    }

    private static bool ContainsScript(ChannelState state, string scriptName)
    {
        if (state.active != null &&
            string.Equals(
                state.active.scriptName,
                scriptName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (int i = 0; i < state.queue.Count; i++)
        {
            if (string.Equals(
                state.queue[i].scriptName,
                scriptName,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool Enqueue(ChannelState state, DialogueRequest request, bool forceFront)
    {
        int insert = state.queue.Count;

        if (forceFront)
        {
            insert = 0;
        }
        else
        {
            int priority = PriorityOf(request);

            for (int i = 0; i < state.queue.Count; i++)
            {
                DialogueRequest queued = state.queue[i];
                int queuedPriority = PriorityOf(queued);

                if (priority > queuedPriority ||
                    (priority == queuedPriority && request.serial < queued.serial))
                {
                    insert = i;
                    break;
                }
            }
        }

        state.queue.Insert(insert, request);

        if (maxQueuedRequests <= 0 || state.queue.Count <= maxQueuedRequests)
            return true;

        int worst = 0;

        for (int i = 1; i < state.queue.Count; i++)
        {
            int a = PriorityOf(state.queue[i]);
            int b = PriorityOf(state.queue[worst]);

            if (a < b ||
                (a == b && state.queue[i].serial > state.queue[worst].serial))
            {
                worst = i;
            }
        }

        bool survived = !ReferenceEquals(state.queue[worst], request);
        state.queue.RemoveAt(worst);
        return survived;
    }

    private void BeginRequest(ChannelState state, DialogueRequest request)
    {
        request.lineIndex = 0;
        request.pickedLine = -1;

        if (request.script.pickOne)
            request.pickedLine = PickEligibleLine(request);

        state.active = request;
        state.wait = 0f;

        onScriptStarted?.Invoke(request.scriptName, request.id);
    }

    private void InterruptActive(ChannelState state)
    {
        DialogueRequest active = state.active;

        if (active == null)
            return;

        if (active.script.dismissOnInterrupt)
            DismissRequest(active.id);

        // callback이 Play/Skip을 다시 불러도 "끝난 요청"을 active로 보지 않게 먼저 상태를 닫는다.
        state.active = null;
        state.wait = 0f;

        onScriptEnded?.Invoke(active.scriptName, active.id);
    }

    private void FinishActive(ChannelState state)
    {
        DialogueRequest active = state.active;

        if (active == null)
        {
            StartNextQueued(state);
            return;
        }

        state.active = null;
        state.wait = 0f;

        onScriptEnded?.Invoke(active.scriptName, active.id);

        // callback이 이미 새 요청을 시작했다면 그걸 덮어쓰지 않는다.
        if (state.active == null)
            StartNextQueued(state);
    }

    private void StartNextQueued(ChannelState state)
    {
        DropExpired(state);

        if (state.queue.Count == 0)
            return;

        DialogueRequest next = state.queue[0];
        state.queue.RemoveAt(0);
        BeginRequest(state, next);
    }

    private void DropExpired(ChannelState state)
    {
        float now = Time.unscaledTime;

        for (int i = state.queue.Count - 1; i >= 0; i--)
        {
            DialogueRequest request = state.queue[i];
            float maxAge = request.script.maxQueueAge;

            if (maxAge == 0f)
                maxAge = defaultQueueMaxAge;

            if (maxAge < 0f)
                continue;

            if (now - request.createdAt > maxAge)
                state.queue.RemoveAt(i);
        }
    }

    private int PickEligibleLine(DialogueRequest request)
    {
        int count = 0;

        for (int i = 0; i < request.script.lines.Length; i++)
        {
            DialogueLine line = request.script.lines[i];

            if (line != null &&
                HasRenderableWork(line) &&
                ConditionsPass(line.when, request.args))
            {
                count++;
            }
        }

        if (count == 0)
            return -1;

        int pick = Pick(request.scriptName, count);

        for (int i = 0; i < request.script.lines.Length; i++)
        {
            DialogueLine line = request.script.lines[i];

            if (line == null ||
                !HasRenderableWork(line) ||
                !ConditionsPass(line.when, request.args))
            {
                continue;
            }

            if (pick-- == 0)
                return i;
        }

        return -1;
    }

    private static bool HasRenderableWork(DialogueLine line)
    {
        return line != null &&
               (!string.IsNullOrEmpty(line.message) ||
                !string.IsNullOrEmpty(line.messageKey) ||
                !string.IsNullOrEmpty(line.voice) ||
                !string.IsNullOrEmpty(line.signal));
    }

    private void AdvanceDirector(float dt)
    {
        if (_channels.Count == 0)
            return;

        int epoch = _directorEpoch;

        _channelScratch.Clear();

        foreach (ChannelState state in _channels.Values)
            _channelScratch.Add(state);

        for (int channelIndex = 0; channelIndex < _channelScratch.Count; channelIndex++)
        {
            if (epoch != _directorEpoch)
                return;

            ChannelState state = _channelScratch[channelIndex];

            // event callback에서 channel dictionary가 바뀌어도 snapshot 자체는 안전하다.
            // 다만 Clear()로 소유권이 사라진 state는 건드리지 않는다.
            bool stillOwned = false;

            foreach (ChannelState owned in _channels.Values)
            {
                if (ReferenceEquals(owned, state))
                {
                    stillOwned = true;
                    break;
                }
            }

            if (!stillOwned)
                continue;

            DropExpired(state);

            if (state.active == null)
            {
                StartNextQueued(state);

                if (epoch != _directorEpoch)
                    return;

                if (state.active == null)
                    continue;
            }

            state.wait -= dt;

            int safety = 0;

            while (state.active != null &&
                   state.wait <= 0f &&
                   safety++ < 32)
            {
                if (epoch != _directorEpoch)
                    return;

                DialogueRequest request = state.active;
                float nextWait;

                if (request.script.pickOne)
                {
                    if (request.lineIndex > 0 || request.pickedLine < 0)
                    {
                        FinishActive(state);
                        continue;
                    }

                    request.lineIndex = 1;
                    DialogueLine one = request.script.lines[request.pickedLine];
                    nextWait = ExecuteLine(request, one);
                }
                else
                {
                    DialogueLine line = NextEligibleLine(request);

                    if (line == null)
                    {
                        FinishActive(state);
                        continue;
                    }

                    nextWait = ExecuteLine(request, line);
                }

                if (epoch != _directorEpoch)
                    return;

                // signal/voice/history callback이 이 요청을 선점하거나 skip했으면,
                // 새 active에 이전 줄의 wait를 먹이지 않는다.
                if (ReferenceEquals(state.active, request))
                    state.wait += nextWait;
            }

            if (safety >= 32)
            {
                Debug.LogError(
                    "[Story] director safety limit. wait=0인 빈 줄/신호가 과도하게 연쇄되는지 확인.");
                state.wait = 0.01f;
            }
        }
    }

    private DialogueLine NextEligibleLine(DialogueRequest request)
    {
        while (request.lineIndex < request.script.lines.Length)
        {
            DialogueLine line = request.script.lines[request.lineIndex++];

            if (line == null ||
                !HasRenderableWork(line) ||
                !ConditionsPass(line.when, request.args))
            {
                continue;
            }

            return line;
        }

        return null;
    }

    private float ExecuteLine(DialogueRequest request, DialogueLine source)
    {
        string message = DialogueLocalization.Get(source.messageKey, source.message);
        string author = DialogueLocalization.Get(source.authorKey, source.author);

        message = Substitute(message, request.args);
        author = Substitute(author, request.args);

        if (!string.IsNullOrWhiteSpace(source.signal))
            onSignal?.Invoke(Substitute(source.signal, request.args), request.scriptName, request.id);

        string voiceId = Substitute(source.voice, request.args);

        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            onVoiceRequested?.Invoke(
                voiceId,
                request.script.channel,
                source.intensity);
        }

        Dialogue spawned = null;

        if (!string.IsNullOrEmpty(message))
        {
            spawned = SpawnInternal(
                message,
                author,
                source.duration,
                source.intensity,
                request.id,
                request.script.channel,
                source.typingSpeed,
                source.minimumHold);

            AddHistory(
                request,
                author,
                message,
                voiceId);
        }

        if (source.wait > 0f)
            return Mathf.Max(0.01f, source.wait);

        float subtitleWait = spawned != null
            ? spawned.typingDuration + lineGap
            : (source.duration > 0f ? source.duration : lineGap);

        if (source.waitForVoice)
        {
            float voiceWait = DialogueVoiceTiming.DurationOf(voiceId) +
                              Mathf.Max(0f, source.voiceTail);

            subtitleWait = Mathf.Max(subtitleWait, voiceWait);
        }

        // 자막 없는 signal/voice line도 timeline에서 시간을 차지할 수 있다.
        return Mathf.Max(0.01f, subtitleWait);
    }

    private void AddHistory(
        DialogueRequest request,
        string author,
        string message,
        string voice)
    {
        DialogueHistoryEntry entry = new(
            request.id,
            request.scriptName,
            request.script.channel,
            author,
            message,
            voice,
            Time.unscaledTime);

        _history.Add(entry);

        if (historyLimit > 0 && _history.Count > historyLimit)
            _history.RemoveRange(0, _history.Count - historyLimit);

        onHistoryAdded?.Invoke(entry);
    }

    /// <summary>
    /// 현재 채널의 미래 줄을 중단한다. dismissVisible이면 이미 나온 줄도 자연스럽게 퇴장한다.
    /// </summary>
    public void SkipChannel(string channel = "radio", bool dismissVisible = false)
    {
        if (!_channels.TryGetValue(channel, out ChannelState state))
            return;

        if (state.active != null)
        {
            int requestId = state.active.id;

            if (dismissVisible)
                DismissRequest(requestId);

            FinishActive(state);
        }
    }

    /// <summary>특정 request가 이미 띄운 줄만 자연스럽게 내보낸다.</summary>
    public void DismissRequest(int requestId)
    {
        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];

            if (line.requestId == requestId)
                BeginLeave(line);
        }
    }

    public void SetVariable(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        _variables[key] = value ?? string.Empty;
    }

    public bool TryGetVariable(string key, out string value)
        => _variables.TryGetValue(key, out value);

    public void RemoveVariable(string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
            _variables.Remove(key);
    }

    private bool ConditionsPass(
        DialogueCondition[] conditions,
        Dictionary<string, string> args)
    {
        if (conditions == null || conditions.Length == 0)
            return true;

        for (int i = 0; i < conditions.Length; i++)
        {
            if (!ConditionPasses(conditions[i], args))
                return false;
        }

        return true;
    }

    private bool ConditionPasses(
        DialogueCondition condition,
        Dictionary<string, string> args)
    {
        if (condition == null || string.IsNullOrWhiteSpace(condition.key))
            return true;

        bool found = TryResolveValue(condition.key, args, out string actual);
        string op = string.IsNullOrWhiteSpace(condition.op)
            ? "eq"
            : condition.op.ToLowerInvariant();

        if (op == "exists")
            return found;

        if (op == "missing")
            return !found;

        if (!found)
            return false;

        string expected = condition.value ?? string.Empty;

        switch (op)
        {
            case "eq":
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

            case "neq":
                return !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

            case "contains":
                return actual?.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;

            case "gt":
            case "gte":
            case "lt":
            case "lte":
                if (!float.TryParse(
                        actual,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float a) ||
                    !float.TryParse(
                        expected,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float b))
                {
                    return false;
                }

                return op switch
                {
                    "gt" => a > b,
                    "gte" => a >= b,
                    "lt" => a < b,
                    "lte" => a <= b,
                    _ => false,
                };

            default:
                Debug.LogWarning($"[Story] 알 수 없는 condition op '{condition.op}'");
                return false;
        }
    }

    private bool TryResolveValue(
        string key,
        Dictionary<string, string> args,
        out string value)
    {
        if (args != null && args.TryGetValue(key, out value))
            return true;

        return _variables.TryGetValue(key, out value);
    }

    /// <summary>{0} 및 {name} 토큰. 존재하지 않는 토큰은 그대로 남긴다.</summary>
    private string Substitute(string source, Dictionary<string, string> args)
    {
        if (string.IsNullOrEmpty(source))
            return source ?? string.Empty;

        if ((args == null || args.Count == 0) && _variables.Count == 0)
            return source;

        StringBuilder result = null;
        int copyFrom = 0;

        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '{')
                continue;

            int end = source.IndexOf('}', i + 1);

            if (end < 0)
                break;

            string key = source.Substring(i + 1, end - i - 1);

            if (key.Length == 0 || !TryResolveValue(key, args, out string value))
                continue;

            result ??= new StringBuilder(source.Length + 16);
            result.Append(source, copyFrom, i - copyFrom);
            result.Append(value);

            copyFrom = end + 1;
            i = end;
        }

        if (result == null)
            return source;

        result.Append(source, copyFrom, source.Length - copyFrom);
        return result.ToString();
    }

    private bool OffCooldown(string scriptName, DialogueScript script)
    {
        if (script.cooldown <= 0f)
            return true;

        float now = Time.unscaledTime;

        return !_lastPlayed.TryGetValue(scriptName, out float last) ||
               now - last >= script.cooldown;
    }

    /// <summary>
    /// 변형 중 하나를 고른다. 프로젝트의 deterministic RNG를 그대로 사용한다.
    /// </summary>
    private int Pick(string key, int count)
    {
        var rng = new DeterministicRng(
            Ballistics.Hash(StableHash(key), Core.TickManager.currentTick, _pickSalt++));

        return (int)(rng.NextUInt() % (uint)count);
    }

    /// <summary>FNV-1a. string.GetHashCode의 실행별 salt를 피한다.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;

            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }

            return (int)h;
        }
    }

    // =========================================================
    // 시뮬레이션 반응
    // =========================================================

    /// <summary>
    /// 사건 하나 -> 대본 이름. **팀별 대본이 있으면 그것, 없으면 공용으로 내려간다** -
    /// 적함이 터지는 것과 아군이 터지는 것은 다른 대사여야 하지만, 그렇다고 모든 사건을
    /// 두 벌씩 쓸 이유는 없다. 둘 다 없으면 그 사건엔 대사가 없는 것이고 그것도 정상이다.
    /// </summary>
    private void OnRunEntry(RunLog.Entry entry)
    {
        string key = entry.kind switch
        {
            RunLog.Kind.Finished => "ship-finished",
            RunLog.Kind.Detonated => "detonated",
            RunLog.Kind.HullSplit => "hull-split",
            RunLog.Kind.CrewLost => "crew-lost",
            _ => null,
        };

        if (key == null)
            return;

        string team = entry.team.ToString().ToLowerInvariant();

        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = entry.what ?? string.Empty,
            ["what"] = entry.what ?? string.Empty,
            ["team"] = team,
            ["kind"] = key,
        };

        if (!PlayIfExists($"{key}-{team}", args))
            PlayIfExists(key, args);
    }

    private void OnBattleEnd(Battle battle)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["result"] = battle.Won ? "won" : "lost",
        };

        PlayWithArgs(battle.Won ? "battle-won" : "battle-lost", args);
    }

    private bool PlayIfExists(string scriptName, IDictionary<string, string> args)
        => PlayWithArgs(scriptName, args);

    // =========================================================
    // 프레임
    // =========================================================

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        AdvanceDirector(dt);
        Advance(dt);

        ImGui.Begin();

        // 통신 중일 때만 배경이 깔린다. **떠 있는 대사가 곧 "통신 중"이라는 뜻이다** -
        // 상태 플래그를 따로 두면 켜는 자리와 끄는 자리가 갈려서 언젠가 하나를 잊는다.
        //
        // 알파가 0이 되면 아래 선언 자체를 안 하고, 그러면 다음 Begin이 패턴 행을 전부
        // 걷는다. 즉시 모드에서 "안 부르는 것이 곧 지우는 것"이라 숨기는 코드가 없다.
        float patternTarget = drawPattern && Communicating() ? 1f : 0f;

        _patternAlpha = Mathf.MoveTowards(_patternAlpha, patternTarget, patternFadeSpeed * dt);

        if (_patternAlpha > 0.001f)
            DrawCommunicationPattern(_patternAlpha);

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];

            float enter01 = Mathf.Clamp01(line.age / Mathf.Max(enterPunchDuration, 0.0001f));
            float punch01 = Mathf.Sin(enter01 * Mathf.PI);

            float punch = reduceMotion ? 0f : punch01 * enterPunch * line.intensity;

            float shakeFade = reduceMotion ? 0f : 1f - enter01;
            float seed = i * 31.74f + 17f;

            float noiseX =
                (Mathf.PerlinNoise(seed, Time.unscaledTime * 30f) * 2f - 1f) *
                shakeAmount *
                shakeFade *
                line.intensity;

            float noiseY =
                (Mathf.PerlinNoise(seed + 50f, Time.unscaledTime * 34f) * 2f - 1f) *
                shakeAmount *
                shakeFade *
                line.intensity;

            Vector2 renderPos = line.pos + new Vector2(punch + noiseX, noiseY);

            float height = line.height > 0f ? line.height : lineSize.y;
            bool hasAuthor = !string.IsNullOrWhiteSpace(line.author);

            float recency = Texts.Count <= 1
                ? 1f
                : Mathf.InverseLerp(0, Texts.Count - 1, i);

            float depthAlpha = Mathf.Lerp(0.58f, 1f, recency);

            // 판. **글자보다 먼저 선언하지만 순서가 정하는 것은 없다** - 그리기 순서는
            // Layer가 정한다. 아직 폭을 못 잰 줄은 줄 폭으로 깔고, OnGUI가 재고 나면
            // 다음 프레임에 글자에 맞게 줄어든다.
            if (drawPlate)
            {
                float blockHeight = hasAuthor ? height + authorOffset.y + AuthorHeight : height;
                float blockWidth = line.width > 0f ? line.width : lineSize.x;

                GUIImage plate = ImGui.Image(
                    line.id + "_plate",
                    new Rect(
                        renderPos - platePadding,
                        new Vector2(
                            blockWidth + platePadding.x * 2f,
                            blockHeight + platePadding.y * 2f)
                    ),
                    GUIStyleMaker.Solid(plateColor)
                );

                plate.Layer = PlateLayer;
                plate.Opacity = line.alpha * depthAlpha;
            }

            // 메시지
            GUILabel messageLabel = ImGui.Label(
                line.id + "_msg",
                new Rect(renderPos, new Vector2(lineSize.x, height)),
                RenderedText(line),
                MessageStyle()
            );

            messageLabel.Layer = MessageLayer;
            messageLabel.Opacity = line.alpha * depthAlpha;

            // **메시지에는 RenderScale을 안 쓴다.** DrawRect가 폭까지 같이 키우는데
            // 이 스타일은 wordWrap이라 폭이 흔들리면 줄바꿈 위치가 매 프레임 달라져서
            // 글자가 춤춘다. 등장 충격은 위치(punch)로 주고, 스케일은 wordWrap이 없는
            // author 쪽에서 쓴다.

            if (!hasAuthor)
                continue;

            float authorReveal01 = line.visibleCharacters <= 0
                ? 1f
                : Mathf.Clamp01((float)line.revealCharacters / line.visibleCharacters);

            GUILabel authorLabel = ImGui.Label(
                line.id + "_author",
                new Rect(
                    renderPos + new Vector2(authorOffset.x, height + authorOffset.y),
                    new Vector2(lineSize.x, AuthorHeight)
                ),
                $"- {line.author}",
                AuthorStyle()
            );

            authorLabel.Layer = AuthorLayer;

            authorLabel.Opacity =
                line.alpha *
                depthAlpha *
                Mathf.SmoothStep(0f, 1f, authorReveal01);

            // 중심 기준 스케일이라 자리를 안 옮기고 크기만 튄다.
            authorLabel.RenderScale = reduceMotion
                ? Vector2.one
                : Vector2.one * (1f + punch01 * authorPunchScale * line.intensity);
        }
    }

    /// <summary>
    /// **선언이 아니라 측정만 한다.** ImGui.Begin은 Update에서만 부른다 - OnGUI는 한
    /// 프레임에 여러 번(Layout·Repaint·입력 이벤트마다) 불리기 때문이다. 그런데
    /// GUIStyle.CalcHeight는 GUI 함수라 OnGUI 밖에서 부르면 던진다. 그래서 재는 일만
    /// 여기서 하고, 쓰는 것은 다음 Update다 - 한 프레임 늦지만 등장 프레임에만이다.
    /// </summary>
    private void OnGUI()
    {
        if (Event.current.type != UnityEngine.EventType.Layout)
            return;

        GUIStyle style = MessageStyle();

        if (style == null)
            return;

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];

            if (line.height > 0f)
                continue;

            // 완성된 메시지로 잰다. 타이핑 도중의 길이로 재면 글자가 늘 때마다 아래 줄이
            // 밀려서, 대사 하나가 뜨는 내내 화면 전체가 꿈틀거린다.
            GUIContent content = new(line.message);

            line.height = Mathf.Max(lineSize.y, style.CalcHeight(content, lineSize.x));

            // CalcSize는 줄바꿈을 모르고 잰다. 그 값이 줄 폭을 넘으면 실제로는 접혀서
            // 줄 폭을 꽉 채운다는 뜻이라, 둘 중 작은 쪽이 글자가 차지하는 폭이다.
            line.width = Mathf.Min(lineSize.x, style.CalcSize(content).x);

            if (!string.IsNullOrWhiteSpace(line.author))
            {
                GUIStyle author = AuthorStyle();

                if (author != null)
                {
                    line.width = Mathf.Max(
                        line.width,
                        authorOffset.x + author.CalcSize(new GUIContent($"- {line.author}")).x);
                }
            }

            _layoutDirty = true;
        }

        if (!_layoutDirty)
            return;

        _layoutDirty = false;
        RecalculatePos();
    }

    // =========================================================
    // 대사 생성 / 진행
    // =========================================================

    public Dialogue Spawn(
        string message,
        string author = "",
        float duration = 4f,
        float intensity = 1f)
    {
        return SpawnInternal(
            message,
            author,
            duration,
            intensity,
            0,
            "radio",
            0f,
            0f);
    }

    private Dialogue SpawnInternal(
        string message,
        string author,
        float duration,
        float intensity,
        int requestId,
        string channel,
        float typingSpeedOverride,
        float minimumHoldOverride)
    {
        int visible = CountVisibleCharacters(message);

        float revealSpeed = typingSpeedOverride > 0f
            ? typingSpeedOverride
            : typeSpeed;

        float typing = disableTypewriter
            ? 0f
            : visible / Mathf.Max(revealSpeed, 1f);

        float hold = minimumHoldOverride > 0f
            ? minimumHoldOverride
            : minimumHoldTime;

        duration = Mathf.Max(duration, typing + hold);

        if (!reduceMotion)
        {
            // 기존 줄들 살짝 얻어맞기
            for (int i = 0; i < Texts.Count; i++)
            {
                if (Texts[i].leaving)
                    continue;

                Texts[i].pos += new Vector2(-stackKick * 0.35f, -stackKick);
            }
        }

        Dialogue line = new(
            $"story{_nextId++}",
            message,
            author,
            duration,
            origin,
            visible,
            intensity,
            requestId,
            channel,
            revealSpeed
        );

        line.typingDuration = typing;

        if (disableTypewriter)
            line.revealCharacters = visible;

        Texts.Add(line);
        TrimToMaxLines();
        RecalculatePos();

        line.pos = reduceMotion
            ? line.targetPos
            : line.targetPos + new Vector2(-spawnOffset * 0.35f, spawnOffset);

        if (!reduceMotion && intensity >= screenShakeThreshold)
            GUIManager.Shake(screenShakeStrength * intensity, screenShakeDuration);

        return line;
    }

    private void Advance(float dt)
    {
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];

            line.age += dt;

            if (reduceMotion)
            {
                line.pos = line.targetPos;
                line.velocity = Vector2.zero;
            }
            else
            {
                line.pos = Vector2.SmoothDamp(
                    line.pos,
                    line.targetPos,
                    ref line.velocity,
                    moveSmoothTime,
                    Mathf.Infinity,
                    dt
                );
            }

            if (!line.leaving)
            {
                line.alpha = Mathf.MoveTowards(line.alpha, 1f, fadeInSpeed * dt);

                if (line.revealCharacters < line.visibleCharacters)
                {
                    line.revealAccumulator += Mathf.Max(line.revealSpeed, 1f) * dt;
                    int reveal = Mathf.FloorToInt(line.revealAccumulator);

                    if (reveal > 0)
                    {
                        line.revealCharacters = Mathf.Min(
                            line.visibleCharacters,
                            line.revealCharacters + reveal
                        );

                        line.revealAccumulator -= reveal;
                    }
                }

                line.duration -= dt;

                if (line.duration <= 0f)
                    BeginLeave(line);

                continue;
            }

            line.alpha = Mathf.MoveTowards(line.alpha, 0f, fadeOutSpeed * dt);

            if (line.alpha > 0.01f)
                continue;

            Texts.RemoveAt(i);
            RecalculatePos();
        }
    }

    /// <summary>
    /// 넘치는 줄을 내보낸다. 새 것부터 세고 오래된 것을 버린다.
    /// </summary>
    private void TrimToMaxLines()
    {
        if (maxLines <= 0)
            return;

        int live = 0;

        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            if (Texts[i].leaving)
                continue;

            live++;

            if (live > maxLines)
                BeginLeave(Texts[i]);
        }
    }

    private void BeginLeave(Dialogue line)
    {
        if (line.leaving)
            return;

        line.leaving = true;

        if (reduceMotion)
        {
            line.velocity = Vector2.zero;
            return;
        }

        line.targetPos += new Vector2(-leaveOffset * 0.7f, -leaveOffset);
        line.velocity += new Vector2(-25f, -20f) * line.intensity;
    }

    /// <summary>실제 측정된 높이로 스택을 다시 쌓는다.</summary>
    private void RecalculatePos()
    {
        float y = 0f;

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];

            if (line.leaving)
                continue;

            line.targetPos = origin + Vector2.up * y;

            y += (line.height > 0f ? line.height : lineSize.y) + spacing;
        }
    }

    /// <summary>
    /// 현재 전투의 화면/큐/쿨다운만 비운다.
    /// oncePerRun, blackboard, backlog는 유지한다.
    /// </summary>
    public void Clear()
    {
        _directorEpoch++;

        Texts.Clear();
        _channels.Clear();
        _channelScratch.Clear();
        _lastPlayed.Clear();
        _patternAlpha = 0f;
    }

    /// <summary>새 run 시작 시 호출. 런 단위 조건까지 완전히 초기화한다.</summary>
    public void ResetRunState()
    {
        Clear();
        _playedOnce.Clear();
        _variables.Clear();
        _history.Clear();
    }

    // =========================================================
    // Background Pattern
    // =========================================================

    /// <summary>
    /// 아직 나가는 중이 아닌 대사가 하나라도 있나. <c>Texts.Count &gt; 0</c>이 아닌 이유는
    /// 마지막 줄이 빠지기 시작하면 배경도 같이 빠져야 하기 때문이다 - 대사가 다 사라진 뒤에
    /// 배경만 남아 있는 프레임이 생기면 통신이 끝난 것으로 안 읽힌다.
    /// </summary>
    private bool Communicating()
    {
        for (int i = 0; i < Texts.Count; i++)
        {
            if (!Texts[i].leaving)
                return true;
        }

        return false;
    }

    private void DrawCommunicationPattern(float alpha)
    {
        EnsurePatternLine();

        float width = Screen.width;
        float height = Screen.height;

        float t = reduceMotion ? 0f : Time.unscaledTime * patternSpeed;
        float slide = -(t % 1000f);

        int rowCount = Mathf.CeilToInt(height / patternRowHeight) + 4;

        for (int row = -2; row < rowCount; row++)
        {
            float y = row * patternRowHeight;

            // 회전 없이 사선처럼 보이게 x를 행마다 밀어버림
            float x =
                -patternStartXPadding +
                slide +
                row * patternDiagonalOffset;

            GUILabel bg = ImGui.Label(
                $"comm_pattern_{row}",
                new Rect(
                    new Vector2(x, y),
                    new Vector2(width + patternStartXPadding * 2f, patternRowHeight)
                ),
                _patternLineCache,
                PatternStyle()
            );

            bg.Layer = PatternLayer;
            bg.Opacity = patternOpacity * alpha;
        }
    }

    private void EnsurePatternLine()
    {
        if (!string.IsNullOrWhiteSpace(_patternLineCache))
            return;

        StringBuilder sb = new();

        for (int i = 0; i < 40; i++)
        {
            if (i > 0)
                sb.Append("    ");

            sb.Append(patternText);
        }

        _patternLineCache = sb.ToString();
    }

    // =========================================================
    // Styles
    // =========================================================

    private GUIStyle MessageStyle()
    {
        if (_messageStyle != null || !GUIStyleMaker.Initialized)
            return _messageStyle;

        _messageStyle = GUIStyleMaker.Label(
            fontSize: fontSize,
            alignment: TextAnchor.UpperLeft
        );

        _messageStyle.richText = true;
        _messageStyle.wordWrap = true;

        return _messageStyle;
    }

    private GUIStyle AuthorStyle()
    {
        if (_authorStyle != null || !GUIStyleMaker.Initialized)
            return _authorStyle;

        _authorStyle = GUIStyleMaker.Label(
            fontSize: authorFontSize,
            alignment: TextAnchor.UpperLeft
        );

        _authorStyle.richText = false;
        _authorStyle.wordWrap = false;
        _authorStyle.fontStyle = FontStyle.Italic;

        return _authorStyle;
    }

    private GUIStyle PatternStyle()
    {
        if (_patternStyle != null || !GUIStyleMaker.Initialized)
            return _patternStyle;

        _patternStyle = GUIStyleMaker.Label(
            fontSize: patternFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        _patternStyle.richText = false;
        _patternStyle.wordWrap = false;
        _patternStyle.clipping = TextClipping.Clip;
        _patternStyle.fontStyle = FontStyle.Bold;

        return _patternStyle;
    }

    // =========================================================
    // Rich Text Typewriter
    // =========================================================

    /// <summary>
    /// 지금 몇 글자까지 보이는 문자열. **글자 수가 안 바뀐 프레임에는 안 만든다** -
    /// 타이핑이 끝난 줄이 남은 duration 내내 매 프레임 StringBuilder를 돌리고 있었다.
    /// </summary>
    private static string RenderedText(Dialogue line)
    {
        if (line.renderedAt == line.revealCharacters)
            return line.rendered;

        line.rendered = RevealRichText(line.message, line.revealCharacters);
        line.renderedAt = line.revealCharacters;

        return line.rendered;
    }

    private static int CountVisibleCharacters(string text)
    {
        int count = 0;
        bool insideTag = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '<')
            {
                insideTag = true;
                continue;
            }

            if (c == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
                count++;
        }

        return count;
    }

    private static string RevealRichText(string source, int maxVisibleCharacters)
    {
        if (maxVisibleCharacters <= 0)
            return string.Empty;

        StringBuilder result = new();
        Stack<string> openTags = new();

        int visible = 0;

        for (int i = 0; i < source.Length;)
        {
            if (source[i] == '<')
            {
                int end = source.IndexOf('>', i);
                if (end < 0)
                    break;

                string tag = source.Substring(i, end - i + 1);
                result.Append(tag);

                string tagName = GetTagName(tag);

                if (!string.IsNullOrEmpty(tagName))
                {
                    if (tag.StartsWith("</"))
                    {
                        if (openTags.Count > 0)
                            openTags.Pop();
                    }
                    else if (!tag.EndsWith("/>"))
                    {
                        openTags.Push(tagName);
                    }
                }

                i = end + 1;
                continue;
            }

            if (visible >= maxVisibleCharacters)
                break;

            result.Append(source[i]);
            visible++;
            i++;
        }

        while (openTags.Count > 0)
        {
            string tag = openTags.Pop();
            result.Append("</");
            result.Append(tag);
            result.Append('>');
        }

        return result.ToString();
    }

    private static string GetTagName(string tag)
    {
        if (tag.Length < 3)
            return null;

        int start = tag.StartsWith("</") ? 2 : 1;
        int end = start;

        while (end < tag.Length)
        {
            char c = tag[end];

            if (c == '>' || c == '=' || char.IsWhiteSpace(c))
                break;

            end++;
        }

        if (end <= start)
            return null;

        return tag.Substring(start, end - start);
    }
}