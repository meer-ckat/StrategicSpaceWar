using System.Collections.Generic;
using System.Text;
using IMGUI;
using UnityEngine;

/// <summary>화면에 떠 있는 대사 한 줄. 순수 데이터다 - GameObject도 GUIItem도 없다.</summary>
public class Dialogue
{
    public readonly string message;
    public readonly string author;
    public readonly string style;
    public readonly float duration;

    /// <summary>다 읽는 데 걸리는 시간. ScriptManager가 다음 줄을 언제 낼지 이걸로 정한다.</summary>
    public readonly float typingDuration;

    /// <summary>태어난 실시각(Time.unscaledTime). GameManager.ClearBefore가 "죽기 전 통신"과 "유언"을 이걸로 가른다.</summary>
    public readonly float spawnRealTime = Time.unscaledTime;

    public float age;
    public float alpha;
    public bool leaving;

    public Dialogue(string message, string author, string style, float duration, float typingDuration)
    {
        this.message = message;
        this.author = author;
        this.style = style;
        this.duration = duration;
        this.typingDuration = typingDuration;
    }
}

/// <summary>
/// 하프라이프 2 자막(2026-09-12). **상자 하나, 글 한 덩어리.** 화면 위 가운데(에이스 컴뱃 자리) 반투명 판에
/// 화자를 색으로 붙인 줄이 두 줄까지 쌓이고, 다 읽으면 사라진다. 그것이 전부다.
///
/// 예전 1,450줄 - 레인 셋, 줄마다 판, 타이핑, 펀치, 흔들림, 지터, 난입 섬광, 배경 무늬 -
/// 을 버렸다. 오너 판정: "대사 비중이 높은 게임 치곤 대사 UI가 개떡 같다." 읽히는 것이
/// 연출보다 먼저고, 하프라이프는 연출 없이 20년을 읽혔다.
///
/// **여기는 대본을 모른다.** <see cref="ScriptManager"/>가 무엇을 언제, <see cref="DramaManager"/>가
/// 왜, 여기가 어떻게 보이는가. 주어진 문자열과 style 하나로 그림이 정해진다.
///
/// **크기는 세 배율을 곱한 값이다** - fontSize × 1.23(Malgun 글리프) × <see cref="GUIManager.UiScale"/>(씬 1.31).
/// 14가 화면 22px이다. 인스펙터 값은 씬이 이기므로 바꾸면 SampleScene.unity도 같이.
/// </summary>
/// <remarks>
/// **GUIManager보다 먼저 돈다**(DefaultExecutionOrder). ImGui 선언은 매 프레임 Rect·Opacity를 되돌리고(Reset),
/// GUITween은 GUIManager.Update의 Tick에서 값을 쓴다. 선언 → 틱 순이어야 트윈이 이긴다.
/// 반대면 트윈이 쓴 값을 다음 선언이 지워서 아무것도 안 움직인다.
/// </remarks>
[DefaultExecutionOrder(-100)]
public class DialogueManager : MonoBehaviour
{
    public static DialogueManager current;

    [Header("조판")]
    public int fontSize = 14;
    public float lineWidth = 680f;
    public int maxLines = 2;
    public Vector2 platePadding = new(12f, 8f);

    /// <summary>판의 위 끝. 화면 높이 대비. 에이스 컴뱃처럼 위 중앙이다(2026-09-12) - 아래는 배와 계기판(AIRFRAME·WPN)의 자리라 대사가 거기 있으면 늘 뭔가와 겹친다. 줄이 늘면 아래로 자란다.</summary>
    public float topFraction = 0.01f;

    /// <summary>판 불투명도. 하프라이프는 0.5, 여기는 별이 많아 더 덮는다. 접근성 옵션으로 1까지 올릴 자리.</summary>
    public float plateAlpha = 0.78f;

    [Header("박자")]
    /// <summary>초당 읽는 글자 수. 타이핑 연출은 없고 다음 줄까지의 간격만 이걸로 잰다.</summary>
    public float typeSpeed = 42f;
    public float minimumHoldTime = 0.5f;

    /// <summary>대사 사이 간격(초). ScriptManager가 읽는다.</summary>
    public float lineGap = 0.6f;

    private const float FadeIn = 6f, FadeOut = 4f;
    private const float EnterDrop = 14f, EnterTime = 0.18f;   // 새 줄이 오면 판이 위에서 살짝 내려앉는다
    private const int PlateLayer = UiLayer.Dialogue, TextLayer = UiLayer.Dialogue + 1;

    public readonly List<Dialogue> Texts = new();
    public float LastLineTime => _lastLine;

    /// <summary>방금 뭔가 말한 것으로 친다. 잡담이 후보를 못 골랐을 때도 밀어야 매 프레임 다시 시도하지 않는다.</summary>
    public void MarkLine() => _lastLine = Time.unscaledTime;

    private float _lastLine;
    private float _blockHeight;
    private string _measured;   // 이 문자열로 잰 높이다. 글이 바뀌면 다시 잰다
    private GUIStyle _style, _plate;
    private readonly StringBuilder _sb = new();
    private GUIItem _plateItem, _textItem;   // 트윈을 죽일 때 필요하다 - 선언을 멈추면 ImGui가 걷지만 트윈 사전엔 남는다
    private bool _enter;

    private void OnEnable() => current = this;

    private void OnDisable()
    {
        if (current == this)
            current = null;
    }

    // =========================================================
    // 밖에서 부르는 것
    // =========================================================

    /// <summary>signalQuality는 API 호환으로 받기만 한다 - 신호 열화 연출은 뺐다.</summary>
    public Dialogue Spawn(
        string message,
        string author = "",
        float duration = 4f,
        float intensity = 1f,
        string style = "radio",
        float signalQuality = 1f,
        bool interrupt = false)
    {
        float typing = VisibleCharacters(message) / Mathf.Max(1f, typeSpeed);
        duration = Mathf.Max(duration, typing + minimumHoldTime);

        // 난입은 앞줄을 즉시 내보낸다. 섬광도 흔들림도 없다 - 새 줄이 곧 사건이다.
        if (interrupt)
            foreach (Dialogue old in Texts)
                old.leaving = true;

        var line = new Dialogue(message, author, style, duration, typing);
        Texts.Add(line);
        _enter = true;
        _lastLine = Time.unscaledTime;

        int live = 0;
        for (int i = Texts.Count - 1; i >= 0; i--)
            if (!Texts[i].leaving && ++live > Mathf.Max(1, maxLines))
                Texts[i].leaving = true;

        return line;
    }

    public void Clear() => Texts.Clear();

    /// <summary>realTime 이전에 태어난 줄만 지운다 - 죽기 전 통신은 지우고 그 순간 뜨는 유언은 살린다.</summary>
    public void ClearBefore(float realTime) => Texts.RemoveAll(l => l.spawnRealTime < realTime);

    public static bool IsKnownStyle(string style) => Tag(style) != null;

    /// <summary>에디터 검증기가 리플렉션으로 읽는다 - 줄 수 추정용. 배치는 레인을 안 쓴다.</summary>
    public static string PresentationLaneForStyle(string style) => Norm(style) switch
    {
        "crew" or "damage" => "internal",
        "system" => "system",
        _ => "external",
    };

    public static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return (int)h;
        }
    }

    // =========================================================
    // 프레임
    // =========================================================

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];
            line.age += dt;

            if (line.age >= line.duration)
                line.leaving = true;

            line.alpha = line.leaving
                ? Mathf.MoveTowards(line.alpha, 0f, FadeOut * dt)
                : Mathf.MoveTowards(line.alpha, 1f, FadeIn * dt);

            if (line.leaving && line.alpha <= 0f)
                Texts.RemoveAt(i);
        }

        // 전체 화면(정비·항로·선택)에서는 안 그린다. 데이터는 남아서 닫히면 돌아온다.
        if (Texts.Count == 0 || ShipSelectScreen.IsOpen || LogisticsScreen.IsOpen || RefitScreen.IsOpen)
        {
            KillTweens();
            return;
        }

        Styles();
        ImGui.Begin();

        float width = Mathf.Min(lineWidth, GUIManager.LogicalWidth - 64f);

        // 높이는 알파와 무관하다. 측정 키에 알파를 넣으면 페이드 중 매 프레임 키가 바뀌어 한 번도 안 그린다.
        if (_blockHeight <= 0f || _measured != Compose(false))
            return;

        string text = Compose(true);   // OnGUI가 이 글의 높이를 재고 나면 다음 프레임에 그린다

        float h = _blockHeight + platePadding.y * 2f;
        // 자리는 비율 하나가 정한다. 계기판 띠로 아래를 막던 Max는 뺐다(2026-09-12) - 그게 있으면 0.01을 적어도 178px에서 시작해 값이 죽은 것처럼 보인다.
        float top = GUIManager.LogicalHeight * topFraction;
        var box = new Rect((GUIManager.LogicalWidth - width) * 0.5f, top, width, h);

        GUIBoxLabel plate = ImGui.BoxLabel("dlg_plate", box, "", _plate);
        plate.Layer = PlateLayer;
        plate.Opacity = Peak();

        GUILabel label = ImGui.Label("dlg_text",
            new Rect(box.x + platePadding.x, box.y + platePadding.y, width - platePadding.x * 2f, _blockHeight), text, _style);
        label.Layer = TextLayer;

        _plateItem = plate;
        _textItem = label;

        // 새 줄의 첫 프레임에만. MoveIn은 자리를 밀어 두고 원래 자리로 돌아온다 - 매 프레임 선언이
        // Rect를 되돌려도 GUIManager의 틱이 그 뒤에 와서 트윈 값이 그려진다(실행 순서 -100).
        if (_enter)
        {
            _enter = false;
            var drop = new Vector2(0f, -EnterDrop);
            plate.MoveIn(drop, EnterTime, 0f, TweenHelper.EaseOutQuad);
            label.MoveIn(drop, EnterTime, 0f, TweenHelper.EaseOutQuad);
        }
    }

    /// <summary>높이는 GUI 함수라 OnGUI에서만 잴 수 있다. 글이 바뀐 프레임에만 잰다.</summary>
    private void OnGUI()
    {
        if (Event.current.type != EventType.Layout || Texts.Count == 0 || !GUIStyleMaker.Initialized)
            return;

        Styles();
        string text = Compose(false);

        if (_measured == text)
            return;

        float width = Mathf.Min(lineWidth, GUIManager.LogicalWidth - 64f) - platePadding.x * 2f;
        _blockHeight = _style.CalcHeight(new GUIContent(text), width);
        _measured = text;
    }

    /// <summary>줄마다 "화자  본문". 화자는 종류 색, 본문은 Hull. 알파는 색 태그의 끝 두 자리로 - 라벨 하나라 줄마다 Opacity가 없다.</summary>
    private string Compose(bool withAlpha)
    {
        _sb.Clear();

        for (int i = 0; i < Texts.Count; i++)
        {
            Dialogue line = Texts[i];
            byte a = withAlpha ? (byte)Mathf.RoundToInt(Mathf.Clamp01(line.alpha) * 255f) : (byte)255;
            string who = string.IsNullOrWhiteSpace(line.author) ? Tag(line.style) ?? "COMMS" : line.author;

            if (i > 0)
                _sb.Append('\n');

            _sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Accent(line.style))).Append(a.ToString("X2")).Append('>')
               .Append(who).Append(":</color> <color=#").Append(ColorUtility.ToHtmlStringRGB(Palette.Hull)).Append(a.ToString("X2")).Append('>')
               .Append(line.message).Append("</color>");
        }

        return _sb.ToString();
    }

    private void KillTweens()
    {
        if (_plateItem != null) GUITween.Kill(_plateItem);
        if (_textItem != null) GUITween.Kill(_textItem);
        _plateItem = _textItem = null;
    }

    /// <summary>판은 제일 밝은 줄만큼 보인다 - 마지막 줄이 사라질 때 판도 같이 꺼진다.</summary>
    private float Peak()
    {
        float peak = 0f;
        foreach (Dialogue line in Texts)
            peak = Mathf.Max(peak, line.alpha);
        return peak;
    }

    private static string Norm(string style) => (style ?? string.Empty).Trim().ToLowerInvariant();

    private static string Tag(string style) => Norm(style) switch
    {
        "control" => "CONTROL",
        "crew" => "INTERNAL",
        "damage" => "DAMAGE CONTROL",
        "system" => "SYSTEM",
        "enemy" => "INTERCEPT",
        "radio" or "" => "COMMS",
        _ => null,
    };

    private static Color Accent(string style) => Norm(style) switch
    {
        "control" => Palette.Telemetry,
        "crew" => Palette.Radiance,
        "damage" => Palette.Breach,
        "system" => Palette.Steel,
        "enemy" => Palette.Heat,
        _ => Palette.Signal,
    };

    private static int VisibleCharacters(string s)
    {
        int n = 0;
        bool tag = false;
        foreach (char c in s ?? string.Empty)
        {
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (!tag && !char.IsWhiteSpace(c)) n++;
        }
        return n;
    }

    private void Styles()
    {
        if (_style != null || !GUIStyleMaker.Initialized)
            return;

        _style = GUIStyleMaker.Label(Palette.Hull, fontSize, TextAnchor.UpperLeft).RichText().Wrap();
        _plate = GUIStyleMaker.Box(Palette.DeepSpace.WithAlpha(plateAlpha));
    }
}
