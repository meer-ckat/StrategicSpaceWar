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
        float intensity)
    {
        this.id = id;
        this.message = message;
        this.author = author;
        this.duration = duration;
        this.visibleCharacters = visibleCharacters;
        this.intensity = intensity;

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

/// <summary>대본 한 줄. JsonUtility가 읽으므로 필드는 전부 public이고 이름이 곧 키다.</summary>
[Serializable]
public class DialogueLine
{
    public string message;
    public string author;

    public float duration = 4f;
    public float intensity = 1f;

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
            Play(openingScript);
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

        // 변형 목록이면 한 줄만. 코루틴을 안 타므로 기다림도 없다.
        if (script.pickOne)
        {
            DialogueLine one = script.lines[Pick(scriptName, script.lines.Length)];

            if (one != null && !string.IsNullOrEmpty(one.message))
                Spawn(Substitute(one.message, arg), one.author, one.duration, one.intensity);

            return true;
        }

        StartCoroutine(Run(script, arg));
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

    private IEnumerator Run(DialogueScript script, string arg)
    {
        for (int i = 0; i < script.lines.Length; i++)
        {
            DialogueLine line = script.lines[i];

            if (line == null || string.IsNullOrEmpty(line.message))
                continue;

            Dialogue spawned = Spawn(
                Substitute(line.message, arg), line.author, line.duration, line.intensity);

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

    private void OnBattleEnd(Battle battle) => Play(battle.Won ? "battle-won" : "battle-lost");

    // =========================================================
    // 프레임
    // =========================================================

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

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

            float punch = punch01 * enterPunch * line.intensity;

            float shakeFade = 1f - enter01;
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
            authorLabel.RenderScale =
                Vector2.one * (1f + punch01 * authorPunchScale * line.intensity);
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
        int visible = CountVisibleCharacters(message);

        float typing = visible / Mathf.Max(typeSpeed, 1f);
        duration = Mathf.Max(duration, typing + minimumHoldTime);

        // 기존 줄들 살짝 얻어맞기
        for (int i = 0; i < Texts.Count; i++)
        {
            if (Texts[i].leaving)
                continue;

            Texts[i].pos += new Vector2(-stackKick * 0.35f, -stackKick);
        }

        Dialogue line = new(
            $"story{_nextId++}",
            message,
            author,
            duration,
            origin,
            visible,
            intensity
        );

        line.typingDuration = typing;

        Texts.Add(line);
        TrimToMaxLines();
        RecalculatePos();

        line.pos = line.targetPos + new Vector2(-spawnOffset * 0.35f, spawnOffset);

        // 화면 흔들림은 GUIManager가 GUI.matrix를 한 번 미는 것이라 **이 캔버스 전체가**
        // 같이 흔들린다. 줄 하나를 흔드는 위의 Perlin과 다른 층이고, 그래서 둘을 같이 쓴다.
        if (intensity >= screenShakeThreshold)
            GUIManager.Shake(screenShakeStrength * intensity, screenShakeDuration);

        return line;
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

                if (line.revealCharacters < line.visibleCharacters)
                {
                    line.revealAccumulator += typeSpeed * dt;
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
    /// 넘치는 줄을 내보낸다. **새 것부터 세고 오래된 것을 버린다** - 겹치기가 켜지면
    /// duration이 길고 사이가 짧은 대본 하나로 줄이 화면 밖까지 쌓인다.
    ///
    /// 지우지 않고 <see cref="BeginLeave"/>를 부르는 것이 중요하다. 그냥 빼면 줄이 뚝
    /// 사라져서 "밀려났다"가 아니라 "버그"로 읽힌다.
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

        line.targetPos += new Vector2(-leaveOffset * 0.7f, -leaveOffset);
        line.velocity += new Vector2(-25f, -20f) * line.intensity;
    }

    /// <summary>
    /// 줄을 다시 쌓는다. **높이가 줄마다 다르다** - 고정 간격으로 쌓으면 두 줄짜리 대사가
    /// 다음 대사와 겹친다. 아직 못 잰 줄은 최소 높이로 세고, OnGUI가 재고 나면 여기가
    /// 다시 돌아 자리가 잡힌다.
    /// </summary>
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

    public void Clear()
    {
        StopAllCoroutines();
        Texts.Clear();

        // 쿨다운도 같이 간다. 새 전투인데 지난 전투의 유폭 때문에 첫 유폭이 조용하면
        // 원인이 화면에 안 보인다.
        _lastPlayed.Clear();
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
