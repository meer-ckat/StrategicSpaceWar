using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using IMGUI;

/// <summary>
/// 조작법을 화면 구석에서 하나씩 알려준다. **대사가 아니라 툴팁이다.**
///
/// 대사로 하면 안 되는 이유: 승무원이 함장에게 "WASD를 누르십시오"라고 말하는 순간 그건
/// 4의 벽이 아니라 그냥 깨진 것이다. 무전은 함내에서 일어나는 일만 말해야 하고, 키보드는
/// 함내에 없다.
///
/// 키 목록을 통째로 띄우는 것도 안 된다 - 아무도 안 읽는다. 그래서 **한 번에 하나**만
/// 보여주고, 플레이어가 실제로 해야 다음으로 넘어간다. 안 하면 계속 떠 있는다.
///
/// 배운 것은 저장한다(PlayerPrefs). 두 번째 런에서 다시 가르치면 그건 방해다.
/// </summary>
public sealed class ControlHints : MonoBehaviour
{
    /// <summary>가르칠 것 하나. 키 캡 몇 개 + 한 줄 + "다 했나".</summary>
    private readonly struct Step
    {
        public readonly string[] caps;
        public readonly string text;
        public readonly System.Func<bool> done;

        public Step(string[] caps, string text, System.Func<bool> done)
        {
            this.caps = caps;
            this.text = text;
            this.done = done;
        }
    }

    private const string SeenKey = "controlHints.seen";

    [Header("배치")]
    /// <summary>캡 **최소** 폭. 글자가 더 넓으면 잰 값이 이긴다(L-CLICK 같은 것).</summary>
    [SerializeField] private float capMinWidth = 34f;
    [SerializeField] private float capHeight = 30f;
    [SerializeField] private float capGap = 4f;

    /// <summary>캡 글자 좌우에 더하는 여백. 잰 폭에 이만큼 붙여야 글자가 상자에 안 닿는다.</summary>
    [SerializeField] private float capPadding = 14f;

    /// <summary>패널 안쪽 여백(좌우, 상하).</summary>
    [SerializeField] private Vector2 panelPadding = new(16f, 10f);

    /// <summary>캡 묶음과 설명 글 사이.</summary>
    [SerializeField] private float capTextGap = 16f;

    /// <summary>화면 아래에서 띄우는 거리. AIRFRAME·함내 통신과 안 겹치는 자리.</summary>
    [SerializeField] private float bottomMargin = 40f;

    // **900이었다 - ShipSelectScreen과 정확히 동률이라 순서가 정의되지 않았다.**
    // 힌트는 전투 중에 뜨는 것이라 HUD 위, 맵 아래가 맞다.
    [SerializeField] private int layer = UiLayer.Hint;

    [Header("글")]
    [SerializeField] private int capFontSize = 15;
    [SerializeField] private int lineFontSize = 15;
    [SerializeField] private Color capTextColour = new(0.85f, 0.93f, 1f);
    [SerializeField] private Color capColour = new(0.16f, 0.22f, 0.30f, 0.95f);
    [SerializeField] private Color panelColour = new(0.04f, 0.06f, 0.09f, 0.82f);
    [SerializeField] private Color lineColour = new(0.72f, 0.82f, 0.92f);

    [Header("등장")]
    /// <summary>이 초에 걸쳐 밝아진다. 처음부터 최대로 띄우면 소리를 지르는 꼴이다.</summary>
    [SerializeField] private float fadeIn = 0.6f;

    /// <summary>숨쉬듯 밝기가 오르내리는 세기·속도. 0이면 가만히 있는다.</summary>
    [SerializeField] private float pulse = 0.25f;
    [SerializeField] private float pulseSpeed = 2.2f;

    /// <summary>마우스가 이만큼(px) 움직여야 "조준을 해봤다"로 친다. 커서는 가만히 둬도 떨린다.</summary>
    [SerializeField] private float mouseThreshold = 200f;

    private static Vector2 _mouseAt;
    private static bool _mouseSeeded;

    /// <summary>
    /// <see cref="mouseThreshold"/>의 정적 사본. 판정이 Step의 delegate라 정적이고, 인스펙터
    /// 값은 인스턴스에 있어서 Start가 한 번 옮겨 놓는다.
    /// </summary>
    private static float _threshold = 200f;

    /// <summary>
    /// 이번 단계 글줄의 실제 폭. **0이면 아직 못 쟀다.**
    ///
    /// GUIStyle.CalcSize는 GUI 함수라 OnGUI 밖에서 부르면 던진다. 그래서 재는 일만
    /// OnGUI에서 하고 쓰는 것은 다음 Update다 - DialogueManager가 같은 문제를 푸는 방식과
    /// 같다. 한 프레임 늦지만 단계가 바뀌는 프레임에만이다.
    ///
    /// 글자 수로 추정하면 안 된다. 한글은 글자당 15px쯤이고 영문은 8px쯤이라, 하나로
    /// 잡으면 한쪽이 반드시 잘린다. 실제로 그랬다.
    /// </summary>
    private Vector2 _lineSize;

    /// <summary>캡마다 실제로 필요한 폭. <c>L-CLICK</c>은 34px 캡에 안 들어간다.</summary>
    private float[] _capWidths = System.Array.Empty<float>();

    private int _measuredStep = -1;

    private Step[] _steps;
    private int _at;
    private float _shownAt;

    private GUIStyle _cap;
    private GUIStyle _capLabel;
    private GUIStyle _line;
    private GUIStyle _panel;

    private static ControlHints _instance;

    /// <summary>
    /// 씬에 없으면 스스로 심는다. **있으면 그쪽이 이긴다** - 위 인스펙터 값을 쓰려면
    /// 씬에 놓인 컴포넌트여야 하고, 코드가 만든 것은 전부 기본값이다.
    /// SpallTrails와 같은 모양이되 씬 우선인 것만 다르다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(ControlHints));
        DontDestroyOnLoad(go);
        go.AddComponent<ControlHints>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
    }

    private void Start()
    {
        // **마우스는 "누른다"가 없어서 움직인 거리로 본다.** 커서는 가만히 둬도 1px씩
        // 떨리므로 문턱이 필요하다 - 화면을 가로질러 봐야 배운 것이다.
        _steps = new[]
        {
            new Step(new[] { "W", "A", "S", "D" },
                "추력. 화면 기준이라 기수가 어딜 보든 W는 위다.",
                () => Pressed(k => k.wKey, k => k.aKey, k => k.sKey, k => k.dKey)),

            new Step(new[] { "MOUSE" },
                "기수와 포탑이 커서를 따라간다.",
                MouseMoved),

            new Step(new[] { "L-CLICK" },
                "사격. 겨눈 곳이 곧 사선이다.",
                () => Mouse.current != null && Mouse.current.leftButton.isPressed),

            new Step(new[] { "SHIFT" },
                "부스터. 연료를 태운다.",
                () => Keyboard.current != null && Keyboard.current.shiftKey.isPressed),

            new Step(new[] { "SPACE" },
                "정지. 회로도로 배 속을 읽는다. 휠 줌, 끌어서 이동.",
                () => Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame),

            new Step(new[] { "TAB" },
                "정지 중 층 전환 - 전부 · 전기 · 기압.",
                () => Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame),
        };

        _threshold = mouseThreshold;
        _at = PlayerPrefs.GetInt(SeenKey, 0);
        _shownAt = Time.unscaledTime;
    }

    private static bool Pressed(params System.Func<Keyboard, KeyControl>[] keys)
    {
        Keyboard k = Keyboard.current;

        if (k == null)
            return false;

        for (int i = 0; i < keys.Length; i++)
            if (keys[i](k).isPressed)
                return true;

        return false;
    }

    private static bool MouseMoved()
    {
        if (Mouse.current == null)
            return true;

        Vector2 now = Mouse.current.position.ReadValue();

        if (!_mouseSeeded)
        {
            _mouseAt = now;
            _mouseSeeded = true;
            return false;
        }

        return (now - _mouseAt).sqrMagnitude > _threshold * _threshold;
    }

    private void Update()
    {
        if (_steps == null || _at >= _steps.Length)
            return;

        // 함선 선택 화면이 떠 있으면 조작할 배가 없다. 맵은 자기 키를 제목줄에 적는다.
        if (ShipSelectScreen.IsOpen || LogisticsScreen.IsOpen || RefitScreen.IsOpen || MapScreen.IsOpen)
            return;

        if (!MakeStyles())
            return;

        Step step = _steps[_at];

        // 아직 못 쟀으면 그리지 않는다. 폭이 0인 채로 한 번 그리면 그 프레임에 글자가
        // 통째로 잘린 채 나타난다.
        if (_measuredStep != _at || _lineSize.x <= 0f)
            return;

        if (step.done())
        {
            _at++;
            _mouseSeeded = false;
            _shownAt = Time.unscaledTime;
            PlayerPrefs.SetInt(SeenKey, _at);
            return;
        }

        Draw(step);
    }

    private bool MakeStyles()
    {
        if (_cap != null)
            return true;

        if (!GUIStyleMaker.Initialized)
            return false;

        _panel = GUIStyleMaker.Box(panelColour);
        _cap = GUIStyleMaker.Box(capColour);
        _capLabel = GUIStyleMaker.Label(Palette.Hull, 10, TextAnchor.MiddleCenter);
        _line = GUIStyleMaker.Label(lineColour, lineFontSize, TextAnchor.MiddleLeft);

        return true;
    }

    /// <summary>
    /// 화면 아래 가운데. AIRFRAME(좌하단)과 함내 통신(그 위)을 피한 자리다 - 겹치면
    /// Layer로 순서를 정해야 하는데 GUIManager의 Sort가 불안정 정렬이라 실행마다 달라진다.
    /// </summary>
    private void Draw(Step step)
    {
        ImGui.Begin();

        // 캡은 잰 폭. 고정 34px에 L-CLICK을 넣으면 캡 안에서 줄바꿈된다.
        float capsWidth = (step.caps.Length - 1) * capGap;

        for (int i = 0; i < _capWidths.Length; i++)
            capsWidth += _capWidths[i];

        float lineH = Mathf.Max(capHeight, _lineSize.y);
        float want = panelPadding.x * 2f + capsWidth + capTextGap + _lineSize.x;
        float width = Mathf.Min(want, GUIManager.LogicalWidth - 32f);
        float height = lineH + panelPadding.y * 2f;

        float x = (GUIManager.LogicalWidth - width) * 0.5f;
        float y = GUIManager.LogicalHeight - height - bottomMargin;

        // 오래 안 하고 있으면 천천히 밝아진다. 처음부터 최대로 띄우면 소리를 지르는 꼴이다.
        float age = Time.unscaledTime - _shownAt;
        float alpha = Mathf.Clamp01(age / Mathf.Max(0.01f, fadeIn))
            * (1f - pulse + pulse * Mathf.Sin(age * pulseSpeed));

        GUIItem panel = ImGui.BoxLabel(
            "hint:panel", new Rect(x, y, width, height), "", _panel);

        panel.Layer = layer;
        panel.Opacity = alpha;

        float cx = x + panelPadding.x;

        for (int i = 0; i < step.caps.Length; i++)
        {
            float capW = i < _capWidths.Length ? _capWidths[i] : capMinWidth;
            var rect = new Rect(cx, y + (height - capHeight) * 0.5f, capW, capHeight);

            // 캡은 상자 하나 + 글자 하나다. 키보드 이미지를 자산으로 두면 키를 바꿀 때마다
            // 그림을 다시 그려야 한다 - def가 데이터인 이 프로젝트에서 그것만 자산이 된다.
            GUIItem box = ImGui.BoxLabel($"hint:cap{i}", rect, "", _cap);
            box.Layer = layer + 1;
            box.Opacity = alpha;

            GUIItem text = ImGui.Label($"hint:capText{i}", rect, step.caps[i], _capLabel);
            text.Layer = layer + 2;
            text.Opacity = alpha;

            cx += capW + capGap;
        }

        GUIItem line = ImGui.Label(
            "hint:line",
            new Rect(cx + capTextGap - capGap, y + (height - lineH) * 0.5f, _lineSize.x + 4f, lineH),
            step.text,
            _line);

        line.Layer = layer + 1;
        line.Opacity = alpha;
    }

    /// <summary>
    /// **선언이 아니라 측정만 한다.** OnGUI는 한 프레임에 여러 번(Layout·Repaint·입력마다)
    /// 불리므로 여기서 ImGui를 선언하면 같은 위젯이 여러 번 태어난다. Layout에서 재기만 한다.
    /// </summary>
    private void OnGUI()
    {
        if (Event.current.type != EventType.Layout)
            return;

        if (_steps == null || _at >= _steps.Length || _measuredStep == _at)
            return;

        if (!MakeStyles())
            return;

        Step step = _steps[_at];

        _lineSize = _line.CalcSize(new GUIContent(step.text));

        if (_capWidths.Length != step.caps.Length)
            _capWidths = new float[step.caps.Length];

        for (int i = 0; i < step.caps.Length; i++)
        {
            _capWidths[i] = Mathf.Max(
                capMinWidth, _capLabel.CalcSize(new GUIContent(step.caps[i])).x + capPadding);
        }

        _measuredStep = _at;
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/GUI/조작 힌트 다시 보기")]
    private static void ResetHints()
    {
        PlayerPrefs.DeleteKey(SeenKey);
        Debug.Log("[힌트] 처음부터 다시 나온다. 플레이 재시작.");
    }
#endif
}
