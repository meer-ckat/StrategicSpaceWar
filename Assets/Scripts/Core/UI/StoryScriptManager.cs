using System;
using System.Collections;
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

    /// <summary>
    /// 위젯 조각마다의 id. **한 번 만들고 만다** - 즉시 모드는 매 프레임 같은 id로 다시
    /// 선언하는 것이라, `id + "_plate"`를 선언부에 두면 대사 한 줄이 프레임마다 문자열
    /// 여덟 개를 남긴다. 화면에 대사가 떠 있는 내내다.
    /// </summary>
    public readonly string idPlate;
    public readonly string idAccent;
    public readonly string idHeader;
    public readonly string idMessage;
    public readonly string idSystemPlate;
    public readonly string idSystemAccent;
    public readonly string idSystemHeader;
    public readonly string idSystemMessage;

    public readonly string message;
    public readonly string author;

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

    /// <summary>presentation profile id. radio/control/crew/damage/system/enemy.</summary>
    public readonly string style;

    /// <summary>0 = 통신 두절 직전, 1 = 깨끗한 링크.</summary>
    public readonly float signalQuality;

    /// <summary>interrupt로 쫓겨나는 줄. 일반 퇴장보다 훨씬 빠르게 사라진다.</summary>
    public bool interrupted;

    /// <summary>문장부호 뒤 타이핑 호흡.</summary>
    public float revealPause;

    /// <summary>이 줄의 실제 타이핑 속도.</summary>
    public float revealSpeed;

    /// <summary>
    /// 이 줄이 실제로 차지하는 높이. **0이면 아직 못 쟀다.** GUIStyle.CalcHeight는 GUI
    /// 함수라 OnGUI 안에서만 부를 수 있어서, 재는 자리와 쓰는 자리가 한 프레임 갈린다.
    /// </summary>
    public float height;

    /// <summary>
    /// 글자가 실제로 차지하는 폭. 뒤에 까는 판이 이걸 쓴다 - 줄 폭(wordWrap 기준)을 그냥
    /// 쓰면 "No I can't." 뒤에 900픽셀짜리 판이 깔린다. 그리기 rect는 여전히 줄 폭이다,
    /// 그걸 줄이면 줄바꿈 위치가 바뀐다.
    /// </summary>
    public float width;

    // 타이핑
    public int visibleCharacters;
    public int revealCharacters;
    public float revealAccumulator;

    /// <summary>
    /// 글자가 다 찍히는 데 걸리는 시간. **다음 줄이 언제 오는지를 이것이 정한다** -
    /// duration으로 기다리면 앞줄이 사라진 뒤에야 다음이 와서 통신이 절대 안 겹친다.
    /// </summary>
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
        string style = "radio",
        float signalQuality = 1f,
        float revealSpeed = 0f)
    {
        this.id = id;
        idPlate = id + "_plate";
        idAccent = id + "_accent";
        idHeader = id + "_header";
        idMessage = id + "_msg";
        idSystemPlate = id + "_system_plate";
        idSystemAccent = id + "_system_accent";
        idSystemHeader = id + "_system_header";
        idSystemMessage = id + "_system_msg";
        this.message = message;
        this.author = author;
        this.duration = duration;
        this.visibleCharacters = visibleCharacters;
        this.intensity = intensity;
        this.style = string.IsNullOrWhiteSpace(style) ? "radio" : style;
        this.signalQuality = Mathf.Clamp01(signalQuality);
        this.revealSpeed = revealSpeed;

        pos = startPos;
        targetPos = startPos;
        velocity = Vector2.zero;

        alpha = 0f;
        age = 0f;
        leaving = false;
        revealCharacters = 0;
        revealAccumulator = 0f;
        revealPause = 0f;
        interrupted = false;
    }
}

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

public class StoryScriptManager : MonoBehaviour
{
    // 그리기 순서. GUIManager는 Layer로 정렬하고, 동률이면 **등록 순서**로 그린다 -
    // 즉시 모드 캐시에서 등록 순서는 "처음 선언된 프레임"이라 창을 늘려 패턴 행이 새로
    // 생기면 그 행이 대사 위에 올라온다. 명시하면 그런 일이 없다.
    private const int PatternLayer = -100;
    private const int PlateLayer = -2;
    private const int AccentLayer = -1;
    private const int MessageLayer = 0;
    private const int AuthorLayer = 1;

    /// <summary>author 한 줄의 높이. 판이 author까지 덮으려면 알아야 한다.</summary>
    private const float AuthorHeight = 20f;

    /// <summary>
    /// 기능이 아니라 "느낌"을 결정하는 작은 프로필.
    /// author 문자열로 분기하지 않고 JSON의 style이 명시적으로 고른다.
    /// </summary>
    private enum DialogueLane
    {
        External,
        Internal,
        System,
    }

    private readonly struct Presentation
    {
        public readonly string tag;
        public readonly Color plate;
        public readonly Color accent;
        public readonly Vector2 enterDirection;
        public readonly float punch;
        public readonly float shake;
        public readonly float typeSpeed;
        public readonly float punctuationPause;
        public readonly float persistentJitter;
        public readonly float accentScale;

        public Presentation(
            string tag,
            Color plate,
            Color accent,
            Vector2 enterDirection,
            float punch,
            float shake,
            float typeSpeed,
            float punctuationPause,
            float persistentJitter,
            float accentScale = 1f)
        {
            this.tag = tag;
            this.plate = plate;
            this.accent = accent;
            this.enterDirection = enterDirection;
            this.punch = punch;
            this.shake = shake;
            this.typeSpeed = typeSpeed;
            this.punctuationPause = punctuationPause;
            this.persistentJitter = persistentJitter;
            this.accentScale = accentScale;
        }
    }


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
    /// <summary>EXTERNAL COMMS의 기준점. 기존 origin 직렬화 값을 그대로 살린다.</summary>
    public Vector2 origin = new(40f, 96f);

    /// <summary>하위호환용 최소 크기. 실제 폭은 lane별 width가 결정한다.</summary>
    public Vector2 lineSize = new(680f, 30f);

    /// <summary>같은 lane의 카드 사이 간격.</summary>
    public float spacing = 10f;

    [Header("AAA Layout v2")]
    public float externalWidth = 680f;
    public float internalWidth = 560f;
    public float systemWidth = 620f;
    public float internalBottomMargin = 96f;
    public float systemTopMargin = 48f;
    public float headerHeight = 18f;
    public float headerBodyGap = 4f;
    public float headerLeadTime = 0.08f;
    public int maxExternalLines = 3;
    public int maxInternalLines = 3;
    public int maxSystemLines = 1;

    [Header("Movement")]
    public float moveSmoothTime = 0.12f;
    public float spawnOffset = 36f;
    public float leaveOffset = 42f;

    [Header("Fade")]
    public float fadeInSpeed = 7f;
    public float fadeOutSpeed = 4f;

    [Header("Typing")]
    public float typeSpeed = 42f;
    public float minimumHoldTime = 0.5f;

    [Header("Impact")]
    public float enterPunch = 8f;
    public float enterPunchDuration = 0.25f;
    public float shakeAmount = 2f;
    public float stackKick = 5f;

    /// <summary>헤더가 등장할 때만 아주 작게 부풀었다 돌아온다.</summary>
    public float authorPunchScale = 0.16f;

    [Header("Screen Shake")]
    public float screenShakeThreshold = 1.4f;
    public float screenShakeStrength = 7f;
    public float screenShakeDuration = 0.22f;

    [Header("Plate")]
    public bool drawPlate = true;
    public Color plateColor = new(0.3f, 0.04f, 0.06f, 0.78f);
    public Vector2 platePadding = new(12f, 8f);

    [Header("Pattern Background")]
    public bool drawPattern = true;
    public float patternFadeSpeed = 5f;
    public string patternText = "ATRIA NAVY";
    public float patternSpeed = 60f;
    public float patternRowHeight = 78f;
    public float patternDiagonalOffset = 70f;
    public float patternOpacity = 0.055f;
    public float patternStartXPadding = 800f;
    public int patternFontSize = 28;

    [Header("Typography")]
    public int fontSize = 22;
    public int authorFontSize = 13;
    public int systemFontSize = 20;

    [Header("AAA Presentation")]
    public float accentWidth = 4f;
    public float interruptFlashHeight = 3f;
    public float interruptFlashFade = 6f;
    public float interruptKick = 90f;
    public float degradedSignalThreshold = 0.78f;
    public float severeSignalThreshold = 0.20f;
    public float activeAlpha = 1f;
    public float previousAlpha = 0.48f;
    public float historyAlpha = 0.22f;

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

    public List<Dialogue> Texts = new();

    private int _nextId;
    private bool _layoutDirty;

    /// <summary>배경 패턴이 지금 얼마나 나와 있나. 0이면 선언 자체를 안 한다.</summary>
    private float _patternAlpha;

    private float _interruptFlash;
    private float _interruptFlashY;
    private Color _interruptFlashColor = Color.white;

    private GUIStyle _messageStyle;
    private GUIStyle _authorStyle;
    private GUIStyle _systemStyle;
    private GUIStyle _patternStyle;

    private string _patternLineCache;

    private static readonly Dictionary<string, DialogueScript> ScriptCache = new();

    /// <summary>대본 이름 -> 마지막으로 재생한 시각. 쿨다운이 읽는다.</summary>
    private readonly Dictionary<string, float> _lastPlayed = new();

    /// <summary>변형 고르기의 소금. 같은 틱에 두 번 골라도 같은 문장이 안 나오게 한다.</summary>
    private int _pickSalt;

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

    private const string SystemAuthor = "시스템";
    private const string SystemLineStyle = "system";

    /// <summary>이 화자는 이제 없다. 이름이 대응표에 없으면(함장·통신) 언제나 말할 수 있다.</summary>
    private bool Silenced(string author)
        => !string.IsNullOrEmpty(author)
        && AuthorRoles.TryGetValue(author, out Ship.ShipRole role)
        && _lostRoles.Contains(role);

    /// <summary>
    /// 잡담 대본 이름. <c>chitchat-*.json</c>을 폴더에서 한 번 긁어 온다.
    ///
    /// 목록 파일을 따로 두지 않는 이유는 def와 같다 - 파일 하나가 곧 대본 하나이고,
    /// 새 잡담은 폴더에 파일을 떨구면 끝이다. 목록을 손으로 들면 파일은 썼는데 목록에
    /// 안 넣는 실수가 반드시 나오고, 그때 증상은 "안 나오는 대사"라 아무 데도 안 걸린다.
    /// </summary>
    private readonly List<string> _chitchat = new();

    /// <summary>
    /// 승무원 전멸. 죽은 배는 농담을 안 한다.
    ///
    /// <see cref="_lostRoles"/>로는 못 막는다. 전멸하면 두 자리가 다 죽으므로 잡담의
    /// 모든 줄이 걸러지고, 그러면 "하나도 안 남았다"로 떨어져 **시스템이 대신 농담을
    /// 읽는다.** 그 폴백은 사건 대사를 위한 것이지 잡담을 위한 것이 아니다.
    /// </summary>
    private bool _crewLost;

    private float _lastLine;

    /// <summary>
    /// 지금 이 배가 얼마나 위험한가. 0이면 평시, 1이면 최악.
    ///
    /// **사건이 올리고 시간이 내린다. 대사는 안 건드린다.** 대사가 올리고 긴장이 대사를
    /// 막으면 잡담이 자기 자신을 억제하는 되먹임이 생기고, 그건 상수로 못 고친다.
    /// RunLog가 이미 "사건의 단일 깔때기"라 방향이 저절로 한쪽이다.
    ///
    /// 저장하지 않는다. 파생값이 아니라 이번 순간의 분위기이고, 다음 전투는 새로 센다.
    /// </summary>
    private float _tension;

    /// <summary>재사용 버퍼. 대사는 매 틱 도는 것이 아니지만 할당은 안 하는 편이 낫다.</summary>
    private readonly List<int> _speakable = new();

    /// <summary>
    /// 아직 입이 있는 줄만 모은다. 반환값이 0이면 이 대본은 **통째로** 죽은 자리의 것이다.
    ///
    /// 그때 침묵시키지 않고 시스템이 대신 읽는다. 여러 줄짜리 대본에서 한둘이 빠지는 것은
    /// "저 사람이 없다"로 들리지만, 대본 전체가 사라지면 그냥 버그처럼 들린다 -
    /// 유폭이 났는데 화면이 아무 말도 안 하면 플레이어는 유폭을 못 본 것과 같다.
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

        ScanChitchat();

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
    // 대본
    // =========================================================

    /// <summary>대본 폴더. def와 같은 자리에 산다.</summary>
    public static string ScriptFolder =>
        Path.Combine(Application.streamingAssetsPath, "대사");

    /// <summary>
    /// 대본을 읽는다. **없으면 null이고 그것이 정상이다** - 아직 안 쓴 사건의 대사가 없다고
    /// 게임이 멈추면 대본을 하나 늘릴 때마다 코드를 고쳐야 한다.
    /// </summary>
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

        // 죽은 자리의 줄은 후보에서 빠진다. 하나도 안 남으면 시스템이 대신 읽는다.
        bool viaSystem = CollectSpeakable(script) == 0;

        // 변형 목록이면 한 줄만. 코루틴을 안 타므로 기다림도 없다.
        if (script.pickOne)
        {
            DialogueLine one = viaSystem
                ? script.lines[Pick(scriptName, script.lines.Length)]
                : script.lines[_speakable[Pick(scriptName, _speakable.Count)]];

            if (one != null && !string.IsNullOrEmpty(one.message))
            {
                Spawn(
                    Substitute(one.message, arg),
                    viaSystem ? SystemAuthor : one.author,
                    one.duration,
                    one.intensity,
                    viaSystem ? SystemLineStyle : one.style,
                    one.signalQuality,
                    one.interrupt);
            }

            return true;
        }

        StartCoroutine(Run(script, arg, viaSystem));
        return true;
    }

    /// <summary>대본이 있으면 재생하고 있었는지 알려준다. 팀별 대본 -> 공용 대본 폴백에 쓴다.</summary>
    private bool PlayIfExists(string scriptName, string arg) => Play(scriptName, arg);

    private static string Substitute(string message, string arg)
        => string.IsNullOrEmpty(arg) ? message : message.Replace("{0}", arg);

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
    private int Pick(string key, int count)
    {
        var rng = new DeterministicRng(
            Ballistics.Hash(StableHash(key), Core.TickManager.currentTick, _pickSalt++));

        return (int)(rng.NextUInt() % (uint)count);
    }

    /// <summary>
    /// FNV-1a. <c>string.GetHashCode</c>는 실행마다 달라질 수 있어서 못 쓴다 - 그러면 같은
    /// 세이브가 실행마다 다른 대사를 낸다.
    /// </summary>
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

    private IEnumerator Run(DialogueScript script, string arg, bool viaSystem)
    {
        for (int i = 0; i < script.lines.Length; i++)
        {
            DialogueLine line = script.lines[i];

            if (line == null || string.IsNullOrEmpty(line.message))
                continue;

            // viaSystem이면 이미 전부 죽은 자리라 거를 것이 없다. 아니면 죽은 줄만 빠지고
            // 나머지는 자기 목소리 그대로 나간다 - 배가 통째로 조용해지는 것이 아니라
            // 한 사람 몫이 사라진다.
            if (!viaSystem && Silenced(line.author))
                continue;

            Dialogue spawned = Spawn(
                Substitute(line.message, arg),
                viaSystem ? SystemAuthor : line.author,
                line.duration,
                line.intensity,
                viaSystem ? SystemLineStyle : line.style,
                line.signalQuality,
                line.interrupt);

            // **duration이 아니라 타이핑 시간을 기다린다.** duration은 이 줄이 화면에
            // 머무는 시간이라, 그걸 기다리면 앞줄이 사라진 뒤에야 다음이 와서 통신이
            // 절대 안 겹친다. 두 값을 갈라 놓아야 뒤에서 앞줄이 아직 살아 있는 채로
            // 다음 줄이 올라온다 - 이미 있던 스택 연출(stackKick, depthAlpha)이 그제서야
            // 할 일이 생긴다.
            float wait = line.wait > 0f ? line.wait : spawned.typingDuration + lineGap;

            yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, wait));
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

        // **더하지 않고 최댓값을 잡는다.** 유폭 한 번에 판 40장이 같은 틱에 죽으면
        // 더하기는 긴장을 폭발시키고 배가 몇 분 동안 말을 안 한다. 최댓값이면 제일 큰
        // 사건 하나가 분위기를 정하고, 회복 시간이 반감기 하나로 예측 가능해진다.
        _tension = Mathf.Max(_tension, Severity(entry));

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

        if (!PlayIfExists($"{key}-{team}", entry.what))
            PlayIfExists(key, entry.what);
    }

    /// <summary>
    /// 아무 일도 안 일어나는 동안 승무원이 말을 한다. **이것이 tension의 소비자다** -
    /// 지금은 시간만 보지만, 다음에 tension이 들어오면 여기 조건이 하나 는다.
    ///
    /// Texts가 빌 때까지 기다리는 것이 요점이다. 사건 대사와 겹치면 둘 다 안 읽힌다.
    /// </summary>
    private void Chatter()
    {
        if (_crewLost || _chitchat.Count == 0 || Texts.Count > 0)
            return;

        if (Time.unscaledTime - _lastLine < idleGap)
            return;

        // **_lastLine을 밀기 전에 본다.** 그래야 긴장이 임계 밑으로 내려오는 순간
        // 이미 지난 idleGap을 다시 안 기다리고 바로 누가 입을 연다. 긴장이 풀리자마자
        // 말이 나오는 것이 이 시스템이 만들려는 장면이다.
        if (_tension > chitChatMaxTension)
            return;

        // 쿨다운에 걸려도 _lastLine이 안 밀리면 매 프레임 다시 시도한다. 여기서 한 번
        // 밀어 두면 다음 후보를 idleGap 뒤에 고른다 - 대본이 전부 쿨다운이면 그동안
        // 조용한 것이고, 그것도 맞는 출력이다.
        _lastLine = Time.unscaledTime;

        Play(_chitchat[Pick("chitchat", _chitchat.Count)]);
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

    private void OnBattleEnd(Battle battle) => Play(battle.Won ? "battle-won" : "battle-lost");

    // =========================================================
    // 프레임
    // =========================================================

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        Advance(dt);

        // 지수 감쇠. dt에 안 걸리는 것이 요점이다 - 프레임이 튀어도 같은 시간에 같은
        // 값이 되므로, 반감기가 "초"라는 뜻을 계속 유지한다.
        if (_tension > 0f)
            _tension *= Mathf.Pow(0.5f, dt / Mathf.Max(0.01f, tensionHalfLife));

        Chatter();

        _interruptFlash = Mathf.MoveTowards(_interruptFlash, 0f, interruptFlashFade * dt);

        ImGui.Begin();

        if (_interruptFlash > 0.001f)
            DrawInterruptCut();

        if (showTension)
        {
            GUILabel gauge = ImGui.Label(
                "tension_debug",
                new Rect(new Vector2(12f, 12f), new Vector2(260f, 22f)),
                $"tension {_tension:0.000}  (잡담 {chitChatMaxTension:0.00} 이하)",
                AuthorStyle());

            gauge.Layer = AuthorLayer + 1;
            gauge.Opacity = 1f;
        }

        // 전체 화면 패턴은 이제 "통신 중" 표시가 아니다. 중요한 난입 때만 잠깐 쓴다.
        float patternTarget = drawPattern && HasCriticalTransmission() ? 1f : 0f;
        _patternAlpha = Mathf.MoveTowards(_patternAlpha, patternTarget, patternFadeSpeed * dt);

        if (_patternAlpha > 0.001f)
            DrawCommunicationPattern(_patternAlpha);

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];
            Presentation presentation = PresentationFor(line.style);
            DialogueLane lane = LaneForStyle(line.style);

            if (lane == DialogueLane.System)
                DrawSystemLine(line, presentation, i);
            else
                DrawCommsLine(line, presentation, lane, i);
        }
    }

    private void DrawInterruptCut()
    {
        GUIImage cut = ImGui.Image(
            "story_interrupt_cut",
            new Rect(
                new Vector2(0f, Mathf.Clamp(_interruptFlashY, 0f, Screen.height - interruptFlashHeight)),
                new Vector2(Screen.width, interruptFlashHeight)),
            GUIStyleMaker.Solid(_interruptFlashColor)
        );

        cut.Layer = AccentLayer;
        cut.Opacity = _interruptFlash;
    }

    private void DrawCommsLine(Dialogue line, Presentation presentation, DialogueLane lane, int index)
    {
        float enter01 = Mathf.Clamp01(line.age / Mathf.Max(enterPunchDuration, 0.0001f));
        float punch01 = Mathf.Sin(enter01 * Mathf.PI);
        float punch = punch01 * enterPunch * presentation.punch * line.intensity;
        float signalDamage = 1f - line.signalQuality;
        float seed = index * 31.74f + StableHash(line.id) * 0.0001f + 17f;

        // 통신 열화는 frame/header에 보여주고 body는 고정한다. 읽을 정보 자체를 흔들지 않는다.
        float persistent = signalDamage * presentation.persistentJitter;
        Vector2 frameNoise = new(
            (Mathf.PerlinNoise(seed, Time.unscaledTime * 24f) * 2f - 1f) * shakeAmount * persistent,
            (Mathf.PerlinNoise(seed + 50f, Time.unscaledTime * 27f) * 2f - 1f) * shakeAmount * persistent);

        Vector2 framePos = line.pos + frameNoise + new Vector2(punch, 0f);
        Vector2 textPos = line.pos + new Vector2(punch * 0.18f, 0f);

        float bodyHeight = BodyHeight(line);
        float blockHeight = headerHeight + headerBodyGap + bodyHeight;
        float blockWidth = Mathf.Min(WidthForLane(lane), Mathf.Max(line.width, 180f));
        float depthAlpha = LaneDepthAlpha(line);
        float frameFlicker = SignalFrameFlicker(line, seed);
        float bodyLead = Mathf.SmoothStep(
            0f,
            1f,
            Mathf.InverseLerp(headerLeadTime, headerLeadTime + 0.10f, line.age));

        if (drawPlate)
        {
            GUIImage plate = ImGui.Image(
                line.idPlate,
                new Rect(
                    framePos - platePadding,
                    new Vector2(blockWidth + platePadding.x * 2f, blockHeight + platePadding.y * 2f)),
                GUIStyleMaker.Solid(presentation.plate));

            plate.Layer = PlateLayer;
            plate.Opacity = line.alpha * depthAlpha * frameFlicker;

            float barWidth = accentWidth * presentation.accentScale;
            GUIImage accent = ImGui.Image(
                line.idAccent,
                new Rect(
                    new Vector2(framePos.x - platePadding.x, framePos.y - platePadding.y),
                    new Vector2(barWidth, blockHeight + platePadding.y * 2f)),
                GUIStyleMaker.Solid(presentation.accent));

            accent.Layer = AccentLayer;
            accent.Opacity = line.alpha * depthAlpha;
        }

        // Header가 body보다 먼저 뜬다. 누가 말하는지 먼저 읽히는 것이 v2의 핵심이다.
        GUILabel header = ImGui.Label(
            line.idHeader,
            new Rect(textPos, new Vector2(blockWidth, headerHeight)),
            HeaderText(line, presentation),
            AuthorStyle());

        header.Layer = AuthorLayer;
        header.Opacity = line.alpha * depthAlpha * frameFlicker;
        header.RenderScale = Vector2.one * (1f + punch01 * authorPunchScale * line.intensity);

        GUILabel message = ImGui.Label(
            line.idMessage,
            new Rect(
                textPos + new Vector2(0f, headerHeight + headerBodyGap),
                new Vector2(WidthForLane(lane), bodyHeight)),
            BodyText(line, RenderedText(line)),
            MessageStyle());

        message.Layer = MessageLayer;
        message.Opacity = line.alpha * depthAlpha * bodyLead;
    }

    private void DrawSystemLine(Dialogue line, Presentation presentation, int index)
    {
        float enter01 = Mathf.Clamp01(line.age / Mathf.Max(enterPunchDuration, 0.0001f));
        float punch01 = Mathf.Sin(enter01 * Mathf.PI);
        float width = WidthForLane(DialogueLane.System);
        float bodyHeight = BodyHeight(line);
        float blockHeight = headerHeight + headerBodyGap + bodyHeight;
        float seed = index * 41.12f + StableHash(line.id) * 0.0001f;
        float frameFlicker = SignalFrameFlicker(line, seed);
        Vector2 framePos = line.pos + new Vector2(0f, -punch01 * 5f * line.intensity);

        GUIImage plate = ImGui.Image(
            line.idSystemPlate,
            new Rect(
                framePos - platePadding,
                new Vector2(width + platePadding.x * 2f, blockHeight + platePadding.y * 2f)),
            GUIStyleMaker.Solid(presentation.plate));

        plate.Layer = PlateLayer;
        plate.Opacity = line.alpha * frameFlicker;

        GUIImage top = ImGui.Image(
            line.idSystemAccent,
            new Rect(
                new Vector2(framePos.x - platePadding.x, framePos.y - platePadding.y),
                new Vector2(width + platePadding.x * 2f, 2f)),
            GUIStyleMaker.Solid(presentation.accent));

        top.Layer = AccentLayer;
        top.Opacity = line.alpha;

        GUILabel header = ImGui.Label(
            line.idSystemHeader,
            new Rect(framePos, new Vector2(width, headerHeight)),
            "SYSTEM // PRIORITY STATUS",
            AuthorStyle());

        header.Layer = AuthorLayer;
        header.Opacity = line.alpha;

        GUILabel message = ImGui.Label(
            line.idSystemMessage,
            new Rect(
                framePos + new Vector2(0f, headerHeight + headerBodyGap),
                new Vector2(width, bodyHeight)),
            RenderedText(line),
            SystemStyle());

        message.Layer = MessageLayer;
        message.Opacity = line.alpha;
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

        GUIStyle message = MessageStyle();
        GUIStyle system = SystemStyle();
        GUIStyle header = AuthorStyle();

        if (message == null || system == null || header == null)
            return;

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];

            if (line.height > 0f)
                continue;

            DialogueLane lane = LaneForStyle(line.style);
            float laneWidth = WidthForLane(lane);
            GUIStyle bodyStyle = lane == DialogueLane.System ? system : message;
            GUIContent content = new(line.message);

            line.height = Mathf.Max(lineSize.y, bodyStyle.CalcHeight(content, laneWidth));
            line.width = Mathf.Min(laneWidth, bodyStyle.CalcSize(content).x);

            if (lane != DialogueLane.System)
            {
                float headerWidth = header.CalcSize(
                    new GUIContent(HeaderText(line, PresentationFor(line.style)))).x;
                line.width = Mathf.Max(line.width, Mathf.Min(laneWidth, headerWidth));
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

    /// <summary>오디오 시스템이 radio key-on/off, chirp 등을 붙이는 seam.</summary>
    public static event Action<string, string> PresentationCue;

    public Dialogue Spawn(
        string message,
        string author = "",
        float duration = 4f,
        float intensity = 1f,
        string style = "radio",
        float signalQuality = 1f,
        bool interrupt = false)
    {
        Presentation presentation = PresentationFor(style);
        DialogueLane lane = LaneForStyle(style);

        int visible = CountVisibleCharacters(message);
        float revealSpeed = Mathf.Max(1f, typeSpeed * presentation.typeSpeed);
        float typing = EstimateTypingDuration(message, revealSpeed, presentation.punctuationPause);
        duration = Mathf.Max(duration, typing + minimumHoldTime);

        if (interrupt)
            InterruptLane(lane, presentation, intensity);
        else
            KickStack(lane);

        Vector2 start = LaneAnchor(lane);
        Dialogue line = new(
            $"story{_nextId++}",
            message,
            author,
            duration,
            start,
            visible,
            intensity,
            style,
            signalQuality,
            revealSpeed
        );

        line.typingDuration = typing;

        _lastLine = Time.unscaledTime;

        Texts.Add(line);
        TrimToMaxLines();
        RecalculatePos();

        Vector2 direction = presentation.enterDirection.sqrMagnitude > 0.001f
            ? presentation.enterDirection.normalized
            : Vector2.left;

        line.pos = line.targetPos + direction * spawnOffset;

        if (intensity >= screenShakeThreshold)
        {
            GUIManager.Shake(
                screenShakeStrength * intensity * presentation.shake,
                screenShakeDuration);
        }

        PresentationCue?.Invoke(style, interrupt ? "interrupt" : "open");
        return line;
    }


    private void KickStack(DialogueLane lane)
    {
        Vector2 kick = lane switch
        {
            DialogueLane.External => new Vector2(-stackKick * 0.2f, -stackKick),
            DialogueLane.Internal => new Vector2(-stackKick * 0.2f, stackKick),
            _ => new Vector2(0f, -stackKick * 0.5f),
        };

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];
            if (line.leaving || LaneForStyle(line.style) != lane)
                continue;

            line.pos += kick;
        }
    }


    private void InterruptLane(DialogueLane lane, Presentation presentation, float intensity)
    {
        _interruptFlash = 1f;
        _interruptFlashColor = presentation.accent;
        _interruptFlashY = InterruptY(lane);

        Vector2 exit = lane switch
        {
            DialogueLane.External => new Vector2(-interruptKick, -leaveOffset * 0.30f),
            DialogueLane.Internal => new Vector2(-interruptKick, leaveOffset * 0.30f),
            _ => new Vector2(0f, -interruptKick * 0.45f),
        };

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue old = Texts[i];

            if (old.leaving || LaneForStyle(old.style) != lane)
                continue;

            old.interrupted = true;
            old.leaving = true;
            old.targetPos += exit;
            old.velocity += exit * 2.2f * Mathf.Max(0.7f, intensity);
        }

        GUIManager.Shake(
            screenShakeStrength * Mathf.Max(1f, intensity) * presentation.shake,
            screenShakeDuration * 1.15f);
    }


    private void Advance(float dt)
    {
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];

            line.age += dt;

            line.pos = Vector2.SmoothDamp(
                line.pos,
                line.targetPos,
                ref line.velocity,
                moveSmoothTime,
                Mathf.Infinity,
                dt
            );

            if (!line.leaving)
            {
                line.alpha = Mathf.MoveTowards(line.alpha, 1f, fadeInSpeed * dt);

                AdvanceTypewriter(line, dt);

                line.duration -= dt;

                if (line.duration <= 0f)
                    BeginLeave(line);

                continue;
            }

            float leaveSpeed = line.interrupted ? fadeOutSpeed * 3.5f : fadeOutSpeed;
            line.alpha = Mathf.MoveTowards(line.alpha, 0f, leaveSpeed * dt);

            if (line.alpha > 0.01f)
                continue;

            Texts.RemoveAt(i);
            RecalculatePos();
        }
    }

    private void AdvanceTypewriter(Dialogue line, float dt)
    {
        if (line.revealCharacters >= line.visibleCharacters)
            return;

        if (line.revealPause > 0f)
        {
            line.revealPause = Mathf.Max(0f, line.revealPause - dt);
            return;
        }

        Presentation presentation = PresentationFor(line.style);
        line.revealAccumulator += Mathf.Max(1f, line.revealSpeed) * dt;

        while (line.revealAccumulator >= 1f &&
               line.revealCharacters < line.visibleCharacters)
        {
            line.revealAccumulator -= 1f;
            line.revealCharacters++;

            char c = VisibleCharacterAt(line.message, line.revealCharacters - 1);
            float pause = PunctuationPause(c) * presentation.punctuationPause;

            if (pause <= 0f)
                continue;

            line.revealPause = pause;
            break;
        }
    }

    private static float EstimateTypingDuration(
        string message,
        float revealSpeed,
        float punctuationScale)
    {
        int visible = 0;
        float pauses = 0f;
        bool insideTag = false;

        for (int i = 0; i < message.Length; i++)
        {
            char c = message[i];

            if (c == '<') { insideTag = true; continue; }
            if (c == '>') { insideTag = false; continue; }
            if (insideTag) continue;

            visible++;
            pauses += PunctuationPause(c) * punctuationScale;
        }

        return visible / Mathf.Max(1f, revealSpeed) + pauses;
    }

    private static float PunctuationPause(char c)
    {
        return c switch
        {
            '.' or '!' or '?' or '。' or '！' or '？' => 0.14f,
            ',' or ';' or ':' or '，' => 0.055f,
            '—' or '…' => 0.08f,
            _ => 0f,
        };
    }

    private static char VisibleCharacterAt(string text, int visibleIndex)
    {
        if (string.IsNullOrEmpty(text) || visibleIndex < 0)
            return '\0';

        int visible = 0;
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

            if (insideTag)
                continue;

            if (visible == visibleIndex)
                return c;

            visible++;
        }

        return '\0';
    }

    /// <summary>
    /// 넘치는 줄을 내보낸다. **새 것부터 세고 오래된 것을 버린다** - 겹치기가 켜지면
    /// duration이 길고 사이가 짧은 대본 하나로 줄이 화면 밖까지 쌓인다.
    ///
    /// 지우지 않고 <see cref="BeginLeave"/>를 부르는 것이 중요하다. 그냥 빼면 줄이 뚝
    /// 사라져서 "밀려났다"가 아니라 "버그"로 읽힌다.
    /// </summary>
    private void TrimToMaxLines()
    {
        TrimLane(DialogueLane.External, Mathf.Max(1, maxExternalLines));
        TrimLane(DialogueLane.Internal, Mathf.Max(1, maxInternalLines));
        TrimLane(DialogueLane.System, Mathf.Max(1, maxSystemLines));

        // 예전 inspector의 maxLines도 마지막 안전망으로만 유지한다.
        if (maxLines <= 0)
            return;

        int live = 0;
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            if (Texts[i].leaving)
                continue;

            if (++live > maxLines)
                BeginLeave(Texts[i]);
        }
    }

    private void TrimLane(DialogueLane lane, int max)
    {
        int live = 0;
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];
            if (line.leaving || LaneForStyle(line.style) != lane)
                continue;

            if (++live > max)
                BeginLeave(line);
        }
    }


    private void BeginLeave(Dialogue line)
    {
        if (line.leaving)
            return;

        line.leaving = true;
        DialogueLane lane = LaneForStyle(line.style);
        Vector2 exit = lane switch
        {
            DialogueLane.External => new Vector2(-leaveOffset * 0.7f, -leaveOffset * 0.35f),
            DialogueLane.Internal => new Vector2(-leaveOffset * 0.7f, leaveOffset * 0.35f),
            _ => new Vector2(0f, -leaveOffset),
        };

        line.targetPos += exit;
        line.velocity += exit * 0.65f * line.intensity;
        PresentationCue?.Invoke(line.style, "close");
    }


    /// <summary>
    /// 줄을 다시 쌓는다. **높이가 줄마다 다르다** - 고정 간격으로 쌓으면 두 줄짜리 대사가
    /// 다음 대사와 겹친다. 아직 못 잰 줄은 최소 높이로 세고, OnGUI가 재고 나면 여기가
    /// 다시 돌아 자리가 잡힌다.
    /// </summary>
    private void RecalculatePos()
    {
        RecalculateExternal();
        RecalculateInternal();
        RecalculateSystem();
    }

    private void RecalculateExternal()
    {
        float y = origin.y;

        // 새 통신이 가장 위. 아래로 갈수록 echo history다.
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];
            if (line.leaving || LaneForStyle(line.style) != DialogueLane.External)
                continue;

            line.targetPos = new Vector2(origin.x, y);
            y += VisualHeight(line) + spacing;
        }
    }

    private void RecalculateInternal()
    {
        float y = Screen.height - internalBottomMargin;

        // 함내 무전은 아래에서 위로 쌓인다. 최신 보고가 가장 손 가까운 곳에 남는다.
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];
            if (line.leaving || LaneForStyle(line.style) != DialogueLane.Internal)
                continue;

            float visual = VisualHeight(line);
            y -= visual;
            line.targetPos = new Vector2(origin.x, y + platePadding.y);
            y -= spacing;
        }
    }

    private void RecalculateSystem()
    {
        float y = systemTopMargin + platePadding.y;
        float width = WidthForLane(DialogueLane.System);

        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];
            if (line.leaving || LaneForStyle(line.style) != DialogueLane.System)
                continue;

            line.targetPos = new Vector2((Screen.width - width) * 0.5f, y);
            y += VisualHeight(line) + spacing;
        }
    }


    public void Clear()
    {
        StopAllCoroutines();
        Texts.Clear();

        // 쿨다운도 같이 간다. 새 전투인데 지난 전투의 유폭 때문에 첫 유폭이 조용하면
        // 원인이 화면에 안 보인다.
        _lastPlayed.Clear();
    }

    // =========================================================
    // Presentation
    // =========================================================

    public static bool IsKnownStyle(string style)
    {
        switch ((style ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "radio":
            case "control":
            case "crew":
            case "damage":
            case "system":
            case "enemy":
                return true;
            default:
                return false;
        }
    }

    /// <summary>Editor validator가 reflection으로 읽는다. compile-time 참조는 필요 없다.</summary>
    public static string PresentationLaneForStyle(string style)
        => LaneForStyle(style).ToString().ToLowerInvariant();

    private static DialogueLane LaneForStyle(string style)
    {
        switch ((style ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "crew":
            case "damage":
                return DialogueLane.Internal;
            case "system":
                return DialogueLane.System;
            default:
                return DialogueLane.External;
        }
    }

    private Presentation PresentationFor(string style)
    {
        switch ((style ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "control":
                return new Presentation(
                    "CONTROL",
                    new Color(0.025f, 0.08f, 0.11f, 0.90f),
                    new Color(0.28f, 0.86f, 1f, 1f),
                    new Vector2(-1f, 0.08f),
                    0.70f, 0.45f, 0.95f, 1.0f, 0.35f);

            case "crew":
                return new Presentation(
                    "INTERNAL",
                    new Color(0.08f, 0.075f, 0.065f, 0.91f),
                    new Color(1f, 0.78f, 0.34f, 1f),
                    new Vector2(0.15f, 1f),
                    0.90f, 0.72f, 1.08f, 0.78f, 0.55f);

            case "damage":
                return new Presentation(
                    "DAMAGE CONTROL",
                    new Color(0.19f, 0.025f, 0.02f, 0.95f),
                    new Color(1f, 0.22f, 0.12f, 1f),
                    new Vector2(-1f, 0f),
                    1.25f, 1.45f, 1.20f, 0.32f, 0.70f, 1.75f);

            case "system":
                return new Presentation(
                    "SYSTEM",
                    new Color(0.035f, 0.042f, 0.05f, 0.95f),
                    new Color(0.82f, 0.9f, 0.95f, 1f),
                    new Vector2(0f, -1f),
                    0.45f, 0.18f, 0.82f, 0.10f, 0.08f);

            case "enemy":
                return new Presentation(
                    "INTERCEPT",
                    new Color(0.11f, 0.025f, 0.12f, 0.93f),
                    new Color(1f, 0.28f, 0.86f, 1f),
                    new Vector2(1f, 0.08f),
                    0.95f, 0.90f, 0.92f, 1.0f, 1.0f);

            case "radio":
            default:
                return new Presentation(
                    "COMMS",
                    plateColor,
                    new Color(0.95f, 0.24f, 0.28f, 1f),
                    new Vector2(-0.35f, 1f),
                    0.85f, 0.65f, 1f, 0.9f, 0.5f);
        }
    }

    private string HeaderText(Dialogue line, Presentation presentation)
    {
        string who;
        if (string.IsNullOrWhiteSpace(line.author))
            who = presentation.tag;
        else if (line.author.IndexOf(presentation.tag, StringComparison.OrdinalIgnoreCase) >= 0)
            who = line.author;
        else
            who = $"{presentation.tag}  //  {line.author}";

        if (line.signalQuality >= degradedSignalThreshold)
            return who;

        int quality = Mathf.RoundToInt(line.signalQuality * 100f);
        return HeaderGlitch(line, $"{who}  //  LINK {quality}%");
    }

    private static string HeaderGlitch(Dialogue line, string source)
    {
        float damage = 1f - line.signalQuality;
        if (damage < 0.12f || string.IsNullOrEmpty(source))
            return source;

        int bucket = Mathf.FloorToInt(Time.unscaledTime * 10f);
        int salt = StableHash(line.id) ^ bucket * 486187739;
        StringBuilder sb = new(source.Length);

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c) || c == '/')
            {
                sb.Append(c);
                continue;
            }

            unchecked
            {
                uint h = (uint)(salt + i * 374761393);
                h ^= h >> 13;
                h *= 1274126177u;
                h ^= h >> 16;
                float sample = (h & 0xFFFFu) / 65535f;
                sb.Append(sample < damage * damage * 0.18f ? '·' : c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// body dropout은 정말 링크가 죽기 직전인 ambient/enemy에만 허용한다.
    /// control/crew/damage/system의 게임플레이 정보는 끝까지 읽힌다.
    /// </summary>
    private string BodyText(Dialogue line, string source)
    {
        string style = (line.style ?? string.Empty).Trim().ToLowerInvariant();
        bool mayCorrupt = style == "enemy" || style == "radio";

        if (!mayCorrupt || line.signalQuality > severeSignalThreshold || string.IsNullOrEmpty(source))
            return source;

        float severity = 1f - Mathf.Clamp01(line.signalQuality / Mathf.Max(0.01f, severeSignalThreshold));
        int bucket = Mathf.FloorToInt(Time.unscaledTime * 7f);
        int salt = StableHash(line.id) ^ bucket * 486187739;
        StringBuilder sb = new(source.Length);
        bool insideTag = false;
        int visible = 0;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '<') { insideTag = true; sb.Append(c); continue; }
            if (c == '>') { insideTag = false; sb.Append(c); continue; }
            if (insideTag || char.IsWhiteSpace(c)) { sb.Append(c); continue; }

            unchecked
            {
                uint h = (uint)(salt + visible * 374761393);
                h ^= h >> 13;
                h *= 1274126177u;
                h ^= h >> 16;
                float sample = (h & 0xFFFFu) / 65535f;
                float dropout = severity * 0.20f;
                sb.Append(sample < dropout ? '·' : c);
            }

            visible++;
        }

        return sb.ToString();
    }

    private float SignalFrameFlicker(Dialogue line, float seed)
    {
        float damage = 1f - line.signalQuality;
        if (damage < 0.05f)
            return 1f;

        float n = Mathf.PerlinNoise(seed + 100f, Time.unscaledTime * 14f);
        return Mathf.Lerp(1f - damage * 0.34f, 1f, n);
    }

    private float LaneDepthAlpha(Dialogue line)
    {
        DialogueLane lane = LaneForStyle(line.style);
        int newer = 0;
        bool found = false;

        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue candidate = Texts[i];
            if (candidate.leaving || LaneForStyle(candidate.style) != lane)
                continue;

            if (ReferenceEquals(candidate, line))
            {
                found = true;
                break;
            }

            newer++;
        }

        if (!found || newer <= 0) return activeAlpha;
        if (newer == 1) return previousAlpha;
        return historyAlpha;
    }

    private float WidthForLane(DialogueLane lane)
    {
        return lane switch
        {
            DialogueLane.External => Mathf.Max(240f, externalWidth),
            DialogueLane.Internal => Mathf.Max(220f, internalWidth),
            _ => Mathf.Max(260f, systemWidth),
        };
    }

    private float BodyHeight(Dialogue line)
        => line.height > 0f ? line.height : lineSize.y;

    private float VisualHeight(Dialogue line)
        => headerHeight + headerBodyGap + BodyHeight(line) + platePadding.y * 2f;

    private Vector2 LaneAnchor(DialogueLane lane)
    {
        return lane switch
        {
            DialogueLane.External => origin,
            DialogueLane.Internal => new Vector2(origin.x, Screen.height - internalBottomMargin),
            _ => new Vector2((Screen.width - WidthForLane(DialogueLane.System)) * 0.5f, systemTopMargin),
        };
    }

    private float InterruptY(DialogueLane lane)
    {
        return lane switch
        {
            DialogueLane.External => origin.y - 18f,
            DialogueLane.Internal => Screen.height - internalBottomMargin - 18f,
            _ => systemTopMargin - 8f,
        };
    }

    private bool HasCriticalTransmission()
    {
        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];
            if (line.leaving)
                continue;

            string style = (line.style ?? string.Empty).Trim().ToLowerInvariant();
            if (line.interrupted || style == "damage" && line.intensity >= 1.35f && line.age < 0.45f)
                return true;
        }

        return _interruptFlash > 0.05f;
    }

    // =========================================================
    // Background Pattern
    // =========================================================

    /// <summary>
    /// 아직 나가는 중이 아닌 대사가 하나라도 있나. <c>Texts.Count &gt; 0</c>이 아닌 이유는
    /// 마지막 줄이 빠지기 시작하면 배경도 같이 빠져야 하기 때문이다 - 대사가 다 사라진 뒤에
    /// 배경만 남아 있는 프레임이 생기면 통신이 끝난 것으로 안 읽힌다.
    /// </summary>



    /// <summary>
    /// 패턴 행 위젯 id. **미리 만들어 둔다** - 즉시 모드라 이 선언이 매 프레임 도는데,
    /// 보간 문자열을 그 자리에 두면 행 수만큼 문자열이 프레임마다 새로 태어난다.
    /// row는 -2부터 시작하므로 두 칸 밀어 담는다.
    /// </summary>
    private static string[] _patternRowIds = System.Array.Empty<string>();

    private static string PatternRowId(int row)
    {
        int index = row + 2;

        if (index >= _patternRowIds.Length)
        {
            int size = Mathf.Max(16, index + 1);
            System.Array.Resize(ref _patternRowIds, size);
        }

        return _patternRowIds[index] ??= $"comm_pattern_{row}";
    }

    private void DrawCommunicationPattern(float alpha)
    {
        EnsurePatternLine();

        float width = Screen.width;
        float height = Screen.height;

        float t = Time.unscaledTime * patternSpeed;
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
                PatternRowId(row),
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
        _authorStyle.fontStyle = FontStyle.Bold;
        return _authorStyle;
    }


    private GUIStyle SystemStyle()
    {
        if (_systemStyle != null || !GUIStyleMaker.Initialized)
            return _systemStyle;

        _systemStyle = GUIStyleMaker.Label(
            fontSize: systemFontSize,
            alignment: TextAnchor.UpperCenter
        );

        _systemStyle.richText = true;
        _systemStyle.wordWrap = true;
        _systemStyle.fontStyle = FontStyle.Bold;
        return _systemStyle;
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