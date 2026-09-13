using System.Collections.Generic;
using IMGUI;
using UnityEngine;

/// <summary>
/// 화면에 떠 있는 대사 한 줄. **자기 위젯을 자기가 든다**(2026-09-13).
///
/// 예전에는 줄 전부를 문자열 하나로 이어 붙여 라벨 하나에 그렸다. 그래서 알파를 색 태그의 끝 두 자리로
/// 흉내 내야 했고(줄마다 Opacity가 없으니까), 한 줄이 사라질 때 나머지가 문자열 안에서 순간이동했다.
/// 줄 하나 = 리테인드 위젯 하나로 바꾸면 그 둘이 같이 사라진다 - 알파는 Opacity가, 자리 이동은 트윈이 한다.
///
/// **판과 글이 한 개체다.** <see cref="GUIBoxLabel"/>이 배경과 글을 같이 그리므로 둘로 나눌 이유가 없다.
/// 나누면 자리를 옮길 때마다 둘을 같은 값으로 움직여야 하고, 그 둘이 갈라지는 프레임이 반드시 생긴다.
/// </summary>
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

    /// <summary>이 줄의 위젯. 재 보고 나서 태어난다 - 높이를 모르면 자리를 못 잡는다.</summary>
    public GUIBoxLabel item;

    /// <summary>이 줄이 먹는 세로(px). OnGUI에서 한 번 잰다. 0이면 아직 안 쟀다.</summary>
    public float height;

    /// <summary>지금 놓인 슬롯의 y. 위로 올릴지 말지를 이 값과 비교해서 정한다.</summary>
    public float slotY;

    public Dialogue(string message, string author, string style, float duration, float typingDuration)
    {
        this.message = message;
        this.author = author;
        this.style = style;
        this.duration = duration;
        this.typingDuration = typingDuration;
    }

    /// <summary>
    /// 이 줄을 내보낸다. **여기서 뒤 줄들을 직접 올린다.**
    ///
    /// 순회가 필요한가 - 필요하다. 줄마다 높이가 다르므로(한 줄짜리와 세 줄짜리가 섞인다) "한 칸"이
    /// 고정값이 아니고, 내 뒤에 몇 줄이 남아 있는지도 그때그때 다르다. 나보다 아래 있던 것만 내 높이만큼
    /// 당기면 되는데, 그 판정이 곧 순회다. 매니저가 아니라 여기 두는 이유는 **나가는 것이 미는 사건**이기
    /// 때문이다 - 나가는 줄이 자기가 비운 자리를 아는 유일한 자리다.
    ///
    /// 두 번 불러도 안전하다. 이미 leaving이면 아무것도 안 한다 - 안 그러면 뒤 줄이 두 번 올라간다.
    /// </summary>
    public void Leave()
    {
        if (leaving)
            return;

        leaving = true;

        DialogueManager manager = DialogueManager.current;

        if (manager == null || height <= 0f)
            return;

        float gap = height + DialogueManager.RowGap;

        foreach (Dialogue other in manager.Texts)
        {
            // 나가는 중인 것은 안 민다 - 같이 사라질 것이라 옮겨봐야 안 보인다.
            if (other == this || other.leaving || other.item == null || other.slotY <= slotY)
                continue;

            other.slotY -= gap;
            other.item.MoveTo(
                new Vector2(other.item.Rect.x, other.slotY),
                DialogueManager.SlideTime, 0f, TweenHelper.EaseInOutQuad);
        }
    }
}

/// <summary>
/// 하프라이프 2 자막. **줄 하나가 판 하나.** 화면 위 가운데(에이스 컴뱃 자리) 반투명 판에 화자를 색으로
/// 붙인 줄이 두 줄까지 쌓이고, 다 읽으면 사라진다. 그것이 전부다.
///
/// **리테인드다**(2026-09-13). <see cref="Widget"/>로 한 번 짓고 매 프레임 Opacity만 만진다 -
/// ImGui는 매 프레임 Rect를 되돌리므로(Materialise의 Reset) 줄이 제자리로 스냅해서 트윈이 안 보인다.
/// 대가는 지우는 것을 손으로 해야 한다는 것이다: <see cref="Retire"/>가 트윈과 등록을 같이 걷는다.
/// 안 걷으면 초당 60개가 GUIManager 표에 쌓인다.
///
/// **여기는 대본을 모른다.** <see cref="ScriptManager"/>가 무엇을 언제, <see cref="DramaManager"/>가
/// 왜, 여기가 어떻게 보이는가. 주어진 문자열과 style 하나로 그림이 정해진다.
///
/// **크기는 세 배율을 곱한 값이다** - fontSize × 1.23(Malgun 글리프) × <see cref="GUIManager.UiScale"/>(씬 1.31).
/// 14가 화면 22px이다. 인스펙터 값은 씬이 이기므로 바꾸면 SampleScene.unity도 같이.
/// </summary>
public class DialogueManager : MonoBehaviour
{
    public static DialogueManager current;

    [Header("조판")]
    public int fontSize = 14;
    public float lineWidth = 680f;
    public int maxLines = 2;
    public Vector2 platePadding = new(12f, 8f);

    /// <summary>판의 위 끝. 화면 높이 대비. 에이스 컴뱃처럼 위 중앙이다 - 아래는 배와 계기판(AIRFRAME·WPN)의 자리라 대사가 거기 있으면 늘 뭔가와 겹친다. 줄이 늘면 아래로 자란다.</summary>
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
    private const float EnterDrop = 14f, EnterTime = 0.18f;   // 새 줄은 위에서 살짝 내려앉는다

    /// <summary>앞 줄이 나갈 때 뒤 줄이 올라가는 시간. 들어오는 것(0.18)보다 느리다 - 빈자리가 메워지는 것은 사건이 아니다.</summary>
    public const float SlideTime = 0.36f;

    /// <summary>줄 사이 세로 틈(px).</summary>
    public const float RowGap = 4f;

    private const int PlateLayer = UiLayer.Dialogue;

    public readonly List<Dialogue> Texts = new();
    public float LastLineTime => _lastLine;

    /// <summary>방금 뭔가 말한 것으로 친다. 잡담이 후보를 못 골랐을 때도 밀어야 매 프레임 다시 시도하지 않는다.</summary>
    public void MarkLine() => _lastLine = Time.unscaledTime;

    private float _lastLine;
    private GUIStyle _plate;

    private void OnEnable() => current = this;

    private void OnDisable()
    {
        if (current == this)
            current = null;

        for (int i = Texts.Count - 1; i >= 0; i--)
            Retire(Texts[i]);

        Texts.Clear();
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
        // 뒤에서부터 도는 이유: Leave가 목록을 훑으며 뒤 줄을 올리므로, 앞에서부터 부르면 같은 줄이 여러 번 밀린다.
        if (interrupt)
            for (int i = Texts.Count - 1; i >= 0; i--)
                Texts[i].Leave();

        var line = new Dialogue(message, author, style, duration, typing);
        Texts.Add(line);
        _lastLine = Time.unscaledTime;

        // 상한을 넘긴 만큼 오래된 것부터 내보낸다. Leave가 뒤 줄을 올리므로 자리는 저절로 맞는다.
        int live = 0;
        for (int i = Texts.Count - 1; i >= 0; i--)
            if (!Texts[i].leaving && ++live > Mathf.Max(1, maxLines))
                Texts[i].Leave();

        return line;
    }

    public void Clear()
    {
        for (int i = Texts.Count - 1; i >= 0; i--)
            Retire(Texts[i]);

        Texts.Clear();
    }

    /// <summary>realTime 이전에 태어난 줄만 지운다 - 죽기 전 통신은 지우고 그 순간 뜨는 유언은 살린다.</summary>
    public void ClearBefore(float realTime)
    {
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            if (Texts[i].spawnRealTime >= realTime)
                continue;

            Retire(Texts[i]);
            Texts.RemoveAt(i);
        }
    }

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
        // 격파 화면(X-ray)도 전체 화면이다. 배가 없는데 함내 통신이 계속 뜨면 죽음이 안 끝난
        // 것으로 읽히고, X-ray의 선과 글자 위에 대사판이 겹쳐 앉는다. 숨기기만 하므로
        // 데이터는 남는다 - 여기가 대사가 화면에 나가는 유일한 문이라 한 자리면 된다.
        bool hidden = ShipSelectScreen.IsOpen || LogisticsScreen.IsOpen || RefitScreen.IsOpen
                      || GameManager.PlayerDown;

        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            Dialogue line = Texts[i];
            line.age += dt;

            if (line.age >= line.duration)
                line.Leave();   // 수명이 다한 것도 같은 문으로 나간다 - 뒤 줄 올리기가 한 자리에만 있다

            line.alpha = line.leaving
                ? Mathf.MoveTowards(line.alpha, 0f, FadeOut * dt)
                : Mathf.MoveTowards(line.alpha, 1f, FadeIn * dt);

            if (line.leaving && line.alpha <= 0f)
            {
                Retire(line);
                Texts.RemoveAt(i);
                continue;
            }

            // 전체 화면(정비·항로·선택)에서는 숨기기만. 데이터도 위젯도 남아서 닫히면 그대로 돌아온다.
            if (line.item != null)
            {
                line.item.isVisible = !hidden;
                line.item.Opacity = line.alpha;
            }
        }
    }

    /// <summary>
    /// 높이는 GUI 함수라 OnGUI에서만 잴 수 있다. **재고 나서 위젯을 짓는다** - 높이를 모르면 자리를 못 잡고,
    /// 자리를 모르는 채로 지으면 첫 프레임에 0,0에서 제자리로 튄다.
    /// </summary>
    private void OnGUI()
    {
        if (Event.current.type != EventType.Layout || Texts.Count == 0 || !GUIStyleMaker.Initialized)
            return;

        Styles();
        float width = Mathf.Min(lineWidth, GUIManager.LogicalWidth - 64f);

        foreach (Dialogue line in Texts)
        {
            if (line.item != null)
                continue;

            var content = new GUIContent(Compose(line));
            line.height = _plate.CalcHeight(content, width);
            Build(line, content, width);
        }
    }

    /// <summary>
    /// 줄 하나를 짓는다. 자리는 **살아 있는 앞 줄들의 높이 합**이다 - 인덱스 곱하기 고정 높이가 아니다.
    /// 줄마다 높이가 달라서(한 줄짜리와 세 줄짜리가 섞인다) 고정 간격으로 두면 긴 줄이 다음 줄을 덮는다.
    /// </summary>
    private void Build(Dialogue line, GUIContent content, float width)
    {
        float y = GUIManager.LogicalHeight * topFraction;

        foreach (Dialogue other in Texts)
        {
            if (other == line)
                break;

            if (!other.leaving && other.height > 0f)
                y += other.height + RowGap;
        }

        line.slotY = y;
        line.item = Widget.SetLayer(
            Widget.BoxLabel(content, new Rect((GUIManager.LogicalWidth - width) * 0.5f, y, width, line.height), _plate),
            PlateLayer);

        line.item.isInteractable = false;   // 장식이다. 안 끄면 대사판이 그 밑의 버튼 입력을 통째로 먹는다
        line.item.Opacity = line.alpha;
        line.item.MoveIn(new Vector2(0f, -EnterDrop), EnterTime, 0f, TweenHelper.EaseOutQuad);
    }

    /// <summary>
    /// 위젯을 걷는다. **트윈과 등록을 같이 걷어야 한다** - Unregister는 트윈을 모르므로, 안 죽이면
    /// GUITween의 표에 죽은 아이템이 남아 매 프레임 돈다(LogisticsScreen.ClearCard와 같은 이유).
    /// </summary>
    private static void Retire(Dialogue line)
    {
        if (line?.item == null)
            return;

        GUITween.Kill(line.item);
        GUIManager.Unregister(line.item);
        line.item = null;
    }

    /// <summary>"화자  본문". 화자는 종류 색, 본문은 Hull. **알파는 안 넣는다** - 줄마다 위젯이 있으니 Opacity가 한다.</summary>
    private static string Compose(Dialogue line)
    {
        string who = string.IsNullOrWhiteSpace(line.author) ? Tag(line.style) ?? "COMMS" : line.author;

        return $"<color=#{ColorUtility.ToHtmlStringRGB(Accent(line.style))}>{who}:</color> " +
               $"<color=#{ColorUtility.ToHtmlStringRGB(Palette.Hull)}>{line.message}</color>";
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
        if (_plate != null || !GUIStyleMaker.Initialized)
            return;

        // 판과 글이 한 스타일이다. 배경·글자 크기·여백·리치텍스트·줄바꿈을 전부 여기서 정한다.
        _plate = GUIStyleMaker
            .Box(Palette.DeepSpace.WithAlpha(plateAlpha), Palette.Hull, fontSize)
            .Padding(Mathf.RoundToInt(platePadding.x), Mathf.RoundToInt(platePadding.y))
            .Align(TextAnchor.UpperLeft)
            .RichText()
            .Wrap();
    }
}
