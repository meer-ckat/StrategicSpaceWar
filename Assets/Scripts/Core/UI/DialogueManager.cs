using System;
using System.Collections.Generic;
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

    /// <summary>태어난 실시각(Time.unscaledTime). GameManager.ClearBefore가 이걸로
    /// "죽기 전 통신"과 "죽은 직후 새로 뜬 유언"을 가른다 - Tick과 Update의 실행
    /// 순서에 기대지 않는다.</summary>
    public readonly float spawnRealTime = Time.unscaledTime;

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

/// <summary>
/// 화면에 대사 한 줄을 띄우고, 타이핑하고, 밀어 올리고, 지운다. **그것만 한다.**
///
/// 예전에는 이 클래스가 JSON도 읽고, 컷신 배도 조종하고, RunLog도 구독했다. 2586줄이었고
/// "대사가 안 나온다"는 증상 하나에 용의자가 그 전부였다. 지금은 셋이다 -
/// <see cref="ScriptManager"/>가 무엇을 언제, <see cref="DramaManager"/>가 왜와 무슨 일이,
/// 여기가 어떻게 보이는가.
///
/// **여기는 대본을 모른다.** 이 줄이 프롤로그의 것인지 유폭 보고인지 알 방법이 없고,
/// 알 필요도 없다. 주어진 문자열과 style 하나로 그림이 정해진다.
///
/// 인스펙터 값 65개가 여기 있는 것이 그 증거다 - 저 값들은 전부 "어떻게 보이는가"다.
/// </summary>
public class DialogueManager : MonoBehaviour
{
    // 그리기 순서. GUIManager는 Layer로 정렬하고, 동률이면 **등록 순서**로 그린다 -
    // 즉시 모드 캐시에서 등록 순서는 "처음 선언된 프레임"이라 창을 늘려 패턴 행이 새로
    // 생기면 그 행이 대사 위에 올라온다. 명시하면 그런 일이 없다.
    private const int PatternLayer = -100;
    private const int PlateLayer = -2;
    private const int AccentLayer = -1;
    private const int MessageLayer = 0;
    private const int AuthorLayer = 1;

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

    private float _lastLine;

    /// <summary>
    /// 마지막으로 뭔가 말한 시각. <see cref="DramaManager"/>의 잡담이 "얼마나 조용했나"를
    /// 이걸로 잰다 - 말이 끊긴 시간을 아는 것은 말을 띄우는 쪽이다.
    /// </summary>
    public float LastLineTime => _lastLine;

    /// <summary>
    /// 방금 뭔가 말한 것으로 친다. 잡담이 후보를 못 골랐을 때(전부 쿨다운)도 밀어야
    /// 매 프레임 다시 시도하지 않는다.
    /// </summary>
    public void MarkLine() => _lastLine = Time.unscaledTime;

    // =========================================================
    // 수명
    // =========================================================

    /// <summary>
    /// 씬의 대사창. <see cref="Battle"/>·<see cref="Campaign"/>과 같은 규칙으로 둔다 -
    /// 대사를 띄우고 싶은 쪽이 이 오브젝트를 찾아다니지 않아도 되게.
    ///
    /// null이 정상이다. 대사창이 없는 씬에서도 전투는 돌아야 한다.
    /// </summary>
    public static DialogueManager current;

    private void OnEnable() => current = this;

    private void OnDisable()
    {
        if (current == this)
            current = null;
    }

    /// <summary>
    /// FNV-1a. <c>string.GetHashCode</c>는 실행마다 달라질 수 있어서 못 쓴다 - 그러면 같은
    /// 세이브가 실행마다 다른 대사를 낸다.
    ///
    /// **여기 사는 이유는 쓰는 자리가 둘이기 때문이다** - 화면 흔들림의 시드(여기)와
    /// 변형 고르기의 시드(<see cref="ScriptManager.Pick"/>). 두 벌로 두면 언젠가 한쪽만
    /// 고치고, 그러면 같은 대본이 기계마다 다른 문장을 낸다.
    /// </summary>
    public static int StableHash(string s)
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
    // 프레임
    // =========================================================

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        // 대사는 격파 중에도 산다 - battle-lost 유언이 죽음 직후에 뜨는 대본이라,
        // 여기를 GuiHidden(격파 포함)으로 걸면 유언이 뜨자마자 지워진다. 대신 사망
        // 순간의 기존 대사(교전 중 통신)는 GameManager가 Clear()로 한 번에 지운다.
        // 부팅 중(새 씬 로드 직후)에만 숨는다 - 다음 구역 잡담이 부팅 위로 끼어드는 것만 막는다.
        if (GameManager.SceneSeconds < GameManager.GuiBootDelay)
            return;

        Advance(dt);

        _interruptFlash = Mathf.MoveTowards(_interruptFlash, 0f, interruptFlashFade * dt);

        ImGui.Begin();

        if (_interruptFlash > 0.001f)
            DrawInterruptCut();

        // 긴장의 주인은 DramaManager다. 그리는 것만 여기서 한다 - 값을 이쪽으로 옮기면
        // 사건이 올리고 시간이 내리는 그 흐름이 두 파일로 갈라진다.
        DramaManager drama = DramaManager.current;

        if (drama != null && drama.showTension)
        {
            GUILabel gauge = ImGui.Label(
                "tension_debug",
                new Rect(new Vector2(12f, 12f), new Vector2(260f, 22f)),
                $"tension {drama.Tension:0.000}  (잡담 {drama.chitChatMaxTension:0.00} 이하)",
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


    /// <summary>
    /// 화면을 비운다. **대본은 안 건드린다** - 돌고 있는 대본을 멈추고 쿨다운을 비우는
    /// 것은 <see cref="ScriptManager.Clear"/>다. 둘을 한 함수에 두면 "화면만 지우고
    /// 싶다"가 대본까지 끊는다.
    /// </summary>
    public void Clear() => Texts.Clear();

    /// <summary>realTime 이전에 태어난 줄만 지운다. GameManager가 격파 순간 쓴다 -
    /// 죽기 전 통신은 지우고 그 순간 막 뜨기 시작한 유언(battle-lost)은 살린다.</summary>
    public void ClearBefore(float realTime) =>
        Texts.RemoveAll(line => line.spawnRealTime < realTime);

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