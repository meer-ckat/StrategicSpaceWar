using IMGUI;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 전술 지도(M). 카메라가 배로 당겨지고 그 위에 반투명 패널이 뜬다. 틱은 안 세운다 -
/// 지도를 보려면 안전한 자리로 가야 한다는 것이 이 창의 규칙이다.
///
/// **지도는 계산하지 않는다.** <see cref="ContactView.Known"/> 목록을 찍을 뿐이다 - 자리를 알면
/// 제자리에 점, 모르면 배에서 그쪽으로 벌어지는 부채꼴. 고른 것은 트래커 것이다. 세계에 있는 것과
/// 지도에 좌표가 찍히는 것은 다른 일이다 - 68개를 전부 찍으면 항해가 지도 클릭이 된다.
///
/// **밀도가 기본이다**(2026-09-12). 격자·축척·침로·속도·Δv·판 수·접촉 수·시계 - 이미 있는 값은
/// 전부 올린다. 없는 값은 지어내지 않는다. 회전은 <see cref="GUIImage.Rotation"/>이 한다 -
/// 부채꼴도 침로선도 텍스처 한 장을 돌려 그린다.
///
/// 초안이다. 색·간격·문구는 오너가 고친다.
/// </summary>
public sealed class MapScreen : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    private const float PanelAlpha = 0.72f;
    private const float BridgeSize = 45f;

    private const int Layer = UiLayer.Map;
    private const float Margin = 32f, Pad = 14f, Gap = 10f;
    private const float HeadH = 26f, RowH = 19f, SectionGap = 14f;
    private const float KnownDot = 8f, GateDot = 11f, PlayerDot = 8f;
    private const float WedgeHalfAngle = 12f, WedgeReach = 0.42f;
    private const int WedgePx = 256;
    private const float GridStep = 10000f;   // m

    private static Texture2D _wedgeSteel, _wedgeGold, _pixel;

    // 열기·닫기·선택 애니메이션. 상태는 시각 셋뿐이고 알파는 매 프레임 계산한다 - GUITween을 안 쓰는
    // 이유는 요소 수가 프레임마다 변하는 ImGui 화면이라 핸들러가 붙을 자리가 고정이 아니어서다.
    private const float RiseTime = 0.14f, WedgeStep = 0.06f, SectionStep = 0.08f, RowStep = 0.04f;
    private const float CloseTime = 0.28f, CloseStep = 0.02f;
    private float _openedAt, _closingAt = -1f, _selectedAt;
    private Vector2? _lastSelected;
    private float _alpha = 1f;   // 이번 선언 묶음의 알파. 헬퍼가 곱한다
    private int _order;          // 열기 순서 총수. 닫을 때 역순의 기준

    private Vector2 _mouseFollow;

    /// <summary>마우스 자리를 IMGUI 논리 좌표로. Input System은 Screen px에 y가 아래->위고 GUI는 위->아래다.</summary>
    private static Vector2 LogicalMouse()
    {
        if (Mouse.current == null)
            return Vector2.zero;

        Vector2 mouse = Mouse.current.position.ReadValue();
        mouse.x *= GUIManager.LogicalWidth / Mathf.Max(1f, Screen.width);
        mouse.y *= GUIManager.LogicalHeight / Mathf.Max(1f, Screen.height);
        mouse.y = GUIManager.LogicalHeight - mouse.y;
        return mouse;
    }

    private Vector2 MouseOffset()
        => Mouse.current == null
            ? Vector2.zero
            : LogicalMouse() - new Vector2(GUIManager.LogicalWidth * 0.5f, GUIManager.LogicalHeight * 0.5f);

    /// <summary>
    /// 이번 프레임에 찍은 자리. **고르기를 그리기 루프 안에서 한다** - 점의 화면 좌표(ToMap)와
    /// 마우스 시차(FollowMouse)가 거기서만 정해지므로, 밖에서 다시 계산하면 두 벌이 갈라져
    /// "보이는 자리와 집히는 자리가 다르다"가 된다.
    /// </summary>
    private Vector2? _clickAt;

    /// <summary>집히는 반경(px). 점이 8~11 px이라 그 두 배쯤 - 손이 떨려도 집히고, 옆 자리는 안 집힌다.</summary>
    private const float PickRadius = 22f;

    /// <summary>
    /// 매 프레임 원래 자리에서 새로 만든 GUI Rect에 마우스 오프셋만 더한다.
    /// Rect 자체에는 상태를 남기지 않고 _mouseFollow만 감쇠 추적한다.
    /// </summary>
    private Rect FollowMouse(Rect rect, float ratio = 0.05f)
    {
        rect.position += _mouseFollow * ratio;
        return rect;
    }

    private GUIStyle _panel, _floor, _title, _mono, _dim, _right, _rightDim, _name, _ask, _select, _warn, _kicker, _tick;
    private GUIStyle _boxHull, _boxGate, _boxSupply, _boxEnemy, _boxSteel, _boxSelect;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Map Screen");
        DontDestroyOnLoad(go);
        go.AddComponent<MapScreen>();
    }

    private void Update()
    {
        Keyboard keys = Keyboard.current;

        if (keys != null && keys.mKey.wasPressedThisFrame)
            Toggle();

        if (IsOpen && _closingAt < 0f && keys != null && keys.jKey.wasPressedThisFrame)
            TryJump();

        // 클릭은 담아만 두고 고르기는 그리기 루프가 한다.
        if (IsOpen && _closingAt < 0f && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            _clickAt = LogicalMouse();

        // 오른쪽 패널 굴리기. 지도는 전체 화면이라 이 창에서 휠을 다투는 것이 없다 - 마우스 자리를 안 본다.
        if (IsOpen && _closingAt < 0f && Mouse.current != null)
        {
            float wheel = Mouse.current.scroll.ReadValue().y;

            if (Mathf.Abs(wheel) > 0.01f)
                _sideScroll = Mathf.Clamp(
                    _sideScroll - Mathf.Sign(wheel) * ScrollStep, 0f, Mathf.Max(0f, _sideContent - 1f));
        }

        if (IsOpen && (LogisticsScreen.IsOpen || RefitScreen.IsOpen || GameManager.GuiHidden
            || CutSceneManager.ControlsPlayer || Field() == null))
            Close();

        if (IsOpen && _closingAt >= 0f && Time.unscaledTime - _closingAt >= CloseTime)
        {
            IsOpen = false;   // 페이드가 끝난 뒤에야 선언을 멈춘다
            _closingAt = -1f;
        }

        if (IsOpen)
            Draw();
    }

    private void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        Ship player = Campaign.current?.Player;

        if (Field() == null || player == null || LogisticsScreen.IsOpen || RefitScreen.IsOpen
            || GameManager.GuiHidden || CutSceneManager.ControlsPlayer)
            return;

        IsOpen = true;
        _closingAt = -1f;
        _openedAt = Time.unscaledTime;
        _selectedAt = _openedAt;
        _sideScroll = 0f;   // 열면 본함부터. 지난번에 굴려 둔 자리에서 열리면 "내 배가 어디 갔나"가 된다
        _clickAt = null;    // M을 마우스로 누르고 열었으면 그 클릭이 남아 엉뚱한 자리를 고른다
        _lastSelected = ContactView.Selected;
        CameraSystem.CutsceneDamp(0.25f, 0.25f);
        CameraSystem.CutsceneFrame(player.transform, null, 0f, BridgeSize);
    }

    /// <summary>닫기는 페이드다. IsOpen은 CloseTime 뒤에 Update가 내린다. 카메라는 바로 돌아간다.</summary>
    private void Close()
    {
        if (!IsOpen || _closingAt >= 0f)
            return;

        _closingAt = Time.unscaledTime;
        CameraSystem.ReleaseCutscene(blend: true);
    }

    /// <summary>열기 순서 n번째 요소의 알파. 열 때는 순서대로 켜지고, 닫을 때는 역순으로 꺼지며 전체 페이드가 겹친다.</summary>
    private float Step(int n, float step = SectionStep)
    {
        float now = Time.unscaledTime;
        float rise = Mathf.Clamp01((now - _openedAt - n * step) / RiseTime);

        if (_closingAt < 0f)
            return rise;

        float back = (_order - 1 - n) * CloseStep;   // 마지막에 켜진 것이 먼저 꺼진다
        float fall = 1f - Mathf.Clamp01((now - _closingAt - back) / RiseTime);
        float whole = 1f - Mathf.Clamp01((now - _closingAt) / CloseTime);
        return rise * fall * whole;
    }

    private static SectorDef Field()
    {
        SectorDef sector = Campaign.current?.Current;
        return sector != null && sector.Open ? sector : null;
    }

    // =========================================================

    private void Draw()
    {
        SectorDef sector = Field();
        Campaign campaign = Campaign.current;
        Ship player = campaign?.Player;

        if (sector == null || player == null || !GUIStyleMaker.Initialized)
            return;

        Styles();
        ImGui.Begin();

        // 프레임레이트와 무관한 지수 감쇠 추적.
        // Rect는 매 프레임 원래 좌표에서 다시 만들고 이 값만 상태로 유지한다.
        float followT = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
        _mouseFollow = Vector2.Lerp(_mouseFollow, MouseOffset(), followT);

        if (ContactView.Selected != _lastSelected)
        {
            _lastSelected = ContactView.Selected;
            _selectedAt = Time.unscaledTime;
        }

        // 열기 순서: 판 → 머리띠 → 지도 → 부채꼴·점 하나씩 → 섹션 하나씩. _order는 지난 프레임의 총수다(닫기 역순용).
        int n = 0;
        _alpha = Step(n++);
        var panel = new Rect(Margin, Margin, GUIManager.LogicalWidth - Margin * 2f, GUIManager.LogicalHeight - Margin * 2f);
        panel = FollowMouse(panel, 0.05f);
        Box("map_panel", panel, _panel, 0);

        Rect inn = new(panel.x + Pad, panel.y + Pad, panel.width - Pad * 2f, panel.height - Pad * 2f);

        // 머리띠: 왼쪽 표제, 오른쪽 시계와 키
        _alpha = Step(n++);
        float t = Core.TickManager.currentTick / 60f;
        Text("map_title", new Rect(inn.x, inn.y, inn.width * 0.6f, HeadH), $"TAC  //  {sector.name}", _title, 1);
        Text("map_clock", new Rect(inn.x, inn.y, inn.width - 90f, HeadH), $"T+{(int)(t / 60f):00}:{(int)(t % 60f):00}     점 클릭 고르기 · J 점프", _rightDim, 1);

        // 닫기. 키(M)만 있으면 처음 연 사람은 이 창에서 나가는 길을 모른다.
        var closeRect = new Rect(inn.xMax - 82f, inn.y, 82f, HeadH - 2f);
        bool closeHot = Hot(closeRect);
        float keepAlpha = _alpha;
        _alpha *= closeHot ? 0.35f : 0.18f;
        Box("map_closebg", closeRect, closeHot ? _boxSelect : _boxSteel, 1);
        _alpha = keepAlpha;
        Text("map_close", closeRect, closeHot ? "✕ 닫기  M" : "✕ 닫기  M", closeHot ? _select : _rightDim, 2);

        if (closeHot && _clickAt is Vector2 closeClick && closeRect.Contains(closeClick))
        {
            _clickAt = null;
            Close();
        }
        Rule("map_rule", new Rect(inn.x, inn.y + HeadH + 2f, inn.width, 1f), 0.5f, 1);

        float bodyY = inn.y + HeadH + Gap;
        float bodyH = inn.yMax - bodyY;
        var mapRect = new Rect(inn.x, bodyY, bodyH, bodyH);
        var sideRect = new Rect(mapRect.xMax + Gap, bodyY, inn.width - Gap - bodyH, bodyH);

        mapRect = FollowMouse(mapRect, 0.05f);
        sideRect = FollowMouse(sideRect, 0.05f);
        n = DrawMap(sector, campaign, player, mapRect, n);
        n = DrawSide(sector, campaign, player, sideRect, n);
        _order = n;
    }

    // =========================================================
    // 지도
    // =========================================================

    private int DrawMap(SectorDef sector, Campaign campaign, Ship player, Rect rect, int n)
    {
        _alpha = Step(n++);
        Box("map_view", rect, _floor, 1);

        Rect bounds = sector.FieldBounds;
        float inset = Pad + 12f;   // 좌표 눈금 자리
        Rect canvas = new(rect.x + inset, rect.y + Pad, rect.width - inset - Pad, rect.height - inset - Pad);
        float scale = canvas.width / bounds.width;   // px per m

        Vector2 ToMap(Vector2 world)
        {
            float u = Mathf.InverseLerp(bounds.xMin, bounds.xMax, world.x);
            float v = Mathf.InverseLerp(bounds.yMin, bounds.yMax, world.y);
            return new Vector2(canvas.x + u * canvas.width, canvas.y + (1f - v) * canvas.height);   // GUI y는 아래로
        }

        // 격자 10 km. 테두리와 0 선은 굵게. 눈금 숫자는 km.
        int lines = Mathf.RoundToInt(bounds.width / GridStep);
        for (int i = 0; i <= lines; i++)
        {
            float f = (float)i / lines;
            bool major = i == 0 || i == lines;
            float x = canvas.x + f * canvas.width;
            float y = canvas.y + f * canvas.height;
            Rule("map_gx" + i, new Rect(x, canvas.y, 1f, canvas.height), major ? 0.5f : 0.16f, 2);
            Rule("map_gy" + i, new Rect(canvas.x, y, canvas.width, 1f), major ? 0.5f : 0.16f, 2);
            Text("map_gxl" + i, new Rect(x - 20f, canvas.yMax + 2f, 40f, RowH), $"{(bounds.xMin + f * bounds.width) / 1000f:0}", _tick, 2);
            Text("map_gyl" + i, new Rect(rect.x + 2f, y - RowH * 0.5f, inset - 6f, RowH), $"{(bounds.yMax - f * bounds.height) / 1000f:0}", _tick, 2);
        }

        Vector2 me = ToMap(player.transform.position);
        Vector2? selected = ContactView.Selected;

        // 클릭으로 고르기. 제일 가까운 점 하나만 - 겹친 자리에서 두 개가 같이 집히면 아무것도 안 집힌 것과 같다.
        Vector2? pickHit = null;
        float pickBest = PickRadius;

        // 커서 밑의 점. **클릭하기 전에 무엇이 집힐지 보여준다** - 점 하나가 8 px이라, 표시가
        // 없으면 집을 수 있다는 것도 무엇이 집힐지도 안 보인다. 고르는 규칙과 같은 반경을 쓴다.
        Vector2 cursor = LogicalMouse();
        Vector2? hoverHit = null;
        float hoverBest = PickRadius;

        if (rect.Contains(cursor))
            foreach (ContactView.Contact c in ContactView.Known)
            {
                if (!c.located || c.kind == "소멸")
                    continue;

                float d = Vector2.Distance(ToMap(c.at), cursor);

                if (d < hoverBest)
                {
                    hoverBest = d;
                    hoverHit = c.at;
                }
            }

        for (int i = 0; i < ContactView.Known.Count; i++)
        {
            ContactView.Contact c = ContactView.Known[i];
            bool on = selected == c.at;
            // 점과 부채꼴은 id 접두를 가른다 - Known이 거리순이라 같은 i가 프레임마다 다른 종류가 된다.
            string id = (c.located ? "map_d" : "map_w") + i;
            _alpha = Step(n++, WedgeStep);   // 하나씩 켜진다 - 가까운 것부터(트래커 행 순서)

            if (c.located)
            {
                Vector2 p = ToMap(c.at);
                float size = c.kind == "출구" ? GateDot : KnownDot;

                // **점이 있는 것만 집힌다.** 부채꼴은 방위만 아는 것이라 지도에 찍힌 자리가 진짜 자리가
                // 아니다 - 거기를 집게 하면 안 보이는 좌표를 클릭으로 알아내는 셈이 된다. 그쪽은 [ ]로.
                if (_clickAt is Vector2 click && c.kind != "소멸")
                {
                    float d = Vector2.Distance(p, click);

                    if (d < pickBest)
                    {
                        pickBest = d;
                        pickHit = c.at;
                    }
                }

                if (c.kind == "관측")
                    _alpha *= Mathf.Max(0.25f, c.strength);   // 유령은 시간이 갈수록 흐려진다
                else if (c.kind == "소멸")
                    _alpha *= DeadDim;                        // 치운 자리. 남아 있되 조용하다

                bool hover = hoverHit == c.at;

                if (on)
                    Dot(id + "_halo", p, size + 4f, _boxSelect, 3);   // 테는 얇게 - 두꺼우면 점이 아니라 덩어리다
                else if (hover)
                {
                    float keep = _alpha;
                    _alpha *= 0.6f;
                    Dot(id + "_hov", p, size + 4f, _boxSteel, 3);
                    _alpha = keep;
                    Text(id + "_hovl", new Rect(p.x + size + 6f, p.y - RowH * 0.5f, 240f, RowH), c.label, _dim, 6);
                }

                Dot(id, p, size, BoxFor(c.kind), 4);

                if (on)
                    Text("map_sel", new Rect(p.x + size + 6f, p.y - RowH * 0.5f, 240f, RowH), "◀ " + c.label, _select, 6);

                if (ContactView.ShowDebug)
                    Text(id + "_dbg", new Rect(p.x + size + 6f, p.y + RowH * 0.5f, 300f, RowH), Debug(c), _tick, 6);

                continue;
            }

            // 방향만 아는 것: 꼭짓점이 배인 부채꼴. 밝기 = 크기 ÷ 거리 - 큰 것은 멀리서도 진하고 작은 것은 코앞에서만.
            // 고른 것은 노랗고 길다. 운석 뒤(디버그에서만 남는다)는 거의 안 보인다.
            Vector2 dir = (ToMap(c.at) - me).normalized;
            float reach = canvas.width * WedgeReach * (on ? 1f : 0.6f) * (0.7f + 0.3f * _alpha);   // 켜지며 자란다
            float bright = Mathf.Lerp(0.12f, 1f, c.strength);
            float opacity = c.occluded ? 0.08f : on ? Mathf.Max(0.7f, bright) : bright * 0.8f;
            Image(id, me, reach * 2f, dir, on ? WedgeGold : WedgeSteel, opacity, on ? 3 : 2);

            if (on)
            {
                Vector2 tip = me + dir * reach * 0.8f;
                Text("map_sel", new Rect(tip.x + 6f, tip.y - RowH * 0.5f, 240f, RowH), "◀ " + c.label, _select, 6);
            }

            if (ContactView.ShowDebug)
            {
                Vector2 tip = me + dir * reach * 0.5f;
                Text(id + "_dbg", new Rect(tip.x + 6f, tip.y - RowH * 0.5f, 300f, RowH), Debug(c), _tick, 6);
            }
        }

        // **지도 안을 찍었을 때만 먹는다.** 예전에는 오른쪽 패널을 눌러도 여기가 삼켜서, 패널의
        // 빈 곳을 누르면 선택이 풀리고 패널의 어떤 줄도 클릭을 못 받았다.
        if (_clickAt is Vector2 mapClick && rect.Contains(mapClick))
        {
            // 빈 곳을 찍으면 선택을 놓는다. 안 놓으면 "고른 것 없음"으로 돌아갈 길이 없다.
            ContactView.Selected = pickHit;
            _clickAt = null;
        }

        _alpha = Step(n++);

        // 본함: 침로선 + 점. 선은 픽셀 한 장을 늘려 돌린 것이다.
        Vector2 nose = HeadingGui(player);
        float headLen = canvas.width * 0.06f;
        var line = ImGui.Image("map_head", new Rect(me.x + nose.x * headLen * 0.5f - headLen * 0.5f, me.y + nose.y * headLen * 0.5f - 1f, headLen, 2f), Pixel);
        line.Rotation = Mathf.Atan2(nose.y, nose.x) * Mathf.Rad2Deg;
        line.Layer = Layer + 5;
        line.Opacity = _alpha;
        Dot("map_me", me, PlayerDot, _boxHull, 5);

        // 축척
        float bar = GridStep * scale;
        Rule("map_scale", new Rect(canvas.xMax - bar - 6f, canvas.yMax - 12f, bar, 2f), 1f, 3);
        Text("map_scale_t", new Rect(canvas.xMax - 66f, canvas.yMax - 12f - RowH, 60f, RowH), "10 km", _rightDim, 3);
        return n;
    }

    // =========================================================
    // 오른쪽: 본함 / 선택 / 접촉 / 항로
    // =========================================================

    private int DrawSide(SectorDef sector, Campaign campaign, Ship player, Rect rect, int n)
    {
        _alpha = Step(n++);
        Box("map_side", rect, _floor, 1);
        Rect inn = new(rect.x + Pad, rect.y + Pad, rect.width - Pad * 2f, rect.height - Pad * 2f);

        // 지난 프레임에 잰 내용 높이로 먼저 자른다. 창이 커지거나 줄이 사라지면 한 프레임 뒤에 따라온다 -
        // 즉시 모드에서 총 높이는 다 그려봐야 알고, 한 프레임 지연은 눈에 안 보인다.
        _sideScroll = Mathf.Clamp(_sideScroll, 0f, Mathf.Max(0f, _sideContent - inn.height));

        float top = inn.y - _sideScroll;
        float y = top;
        int row = 0;

        Vector2 pos = player.transform.position;
        int heading = Mathf.RoundToInt(player.transform.eulerAngles.z) % 360;   // 359.6°가 360°로 찍히지 않게
        HullStructure hull = player.GetComponent<HullStructure>();

        _alpha = Step(n++);
        Section(inn, ref y, "본함  //  " + (string.IsNullOrEmpty(player.shipDefName) ? player.name : player.shipDefName), 0);
        Row(inn, ref y, "침로", $"{heading:000}°", row++);
        Row(inn, ref y, "속도", $"{player.velocity.magnitude:0} m/s", row++);
        if (hull != null) Row(inn, ref y, "판", $"{hull.AliveCount}", row++);
        if (player.MaxRounds > 0) Row(inn, ref y, "탄약", $"{100f * player.Rounds / player.MaxRounds:0}%", row++, player.Rounds == 0);
        // 창고. 가득 찬 칸은 보급 자리를 지나칠 이유가 된다 - 그것이 이 줄의 값어치다. 한 줄에 둘을 묶는다.
        bool full = RunState.Munitions >= RunState.MaxMunitions;
        Row(inn, ref y, "잔고", $"{RunState.Credits} CR", row++, false);
        Row(inn, ref y, "탄약", $"{100f * RunState.Munitions / RunState.MaxMunitions:0}%", row++, full);
        Row(inn, ref y, "방출", $"×{player.Emission:0.0}", row++, player.Emission >= campaign.hunterLoud);
        if (player.shipTanks.Count > 0)
        {
            float dv = player.AvailableDeltaV();
            Row(inn, ref y, "Δv", $"{dv:0} / {Ballistics.WarpDeltaV:0} m/s", row++, dv < Ballistics.WarpDeltaV);
        }

        y += SectionGap;
        ContactView.Contact? pick = Selected();
        _alpha = Step(n++);
        Section(inn, ref y, "선택", 1);
        float open = _alpha;
        int pickRow = 0;
        // 타겟이 바뀌면 이 섹션만 다시 켜진다 - 줄이 위에서 아래로 간격을 두고.
        float PickAlpha() => open * Mathf.Clamp01((Time.unscaledTime - _selectedAt - pickRow++ * RowStep) / RiseTime);

        if (pick == null)
        {
            _alpha = PickAlpha();
            Row(inn, ref y, "—", "고른 것 없음", row++);
        }
        else
        {
            ContactView.Contact c = pick.Value;
            Vector2 d = c.at - pos;
            float rel = Mathf.DeltaAngle(player.transform.eulerAngles.z, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            string bearing = Mathf.Abs(rel) < 0.5f ? "정면" : rel > 0f ? $"좌 {rel:0}°" : $"우 {-rel:0}°";

            _alpha = PickAlpha();
            Text("map_pick", new Rect(inn.x, y, inn.width, RowH + 4f), c.located ? c.label : "???", c.located ? _name : _ask, 2);
            y += RowH + 4f;
            _alpha = PickAlpha(); Row(inn, ref y, "방위", bearing, row++);
            _alpha = PickAlpha(); Row(inn, ref y, "거리", c.located ? $"{d.magnitude / 1000f:0.0} km" : "불명", row++);
            _alpha = PickAlpha(); Row(inn, ref y, "정체", c.state >= ContactView.Reveal.Identified ? c.label : "미확인", row++);
            _alpha = PickAlpha(); Row(inn, ref y, "상태", StateName(c.state), row++);

            // 최근접(CPA). 이대로 가면 이 자리와 얼마나 떨어져 지나나 - 좌표를 알 때만. 자리는 안 움직이니 상대속도는 내 속도의 반대다.
            if (c.located)
            {
                Vector2 v = c.velocity - player.velocity;
                float v2 = v.sqrMagnitude;
                string cpa = "정지 중";

                if (v2 > 1f)
                {
                    float t = -Vector2.Dot(d, v) / v2;
                    cpa = t <= 0f ? "멀어지는 중" : $"{(d + v * t).magnitude / 1000f:0.0} km · {t:0} s";
                }

                _alpha = PickAlpha(); Row(inn, ref y, "최근접", cpa, row++);
            }

            // 항로 비용. **항해는 공짜가 아니다** - Drive가 추력마다 탱크에서 뺀다. 그런데 화면에 "남은 Δv"만 있고
            // "저기까지 얼마"가 없어서 항로 선택이 계산이 아니라 감이었다. 모르는 자리(좌표 없음)는 거리를 모르니 안 적는다.
            if (c.located)
            {
                float far = d.magnitude;
                float cost = player.TravelCost(far);
                float cruise = player.CruiseSpeed;
                string when = cruise > 1f ? Clock(far / cruise) : "—";
                bool broke = player.shipTanks.Count > 0 && cost > player.AvailableDeltaV();

                _alpha = PickAlpha();
                Row(inn, ref y, "항로", $"Δv {cost:0} · {when}", row++, broke);
            }

            // 점프. 앵커(보급·잔해·출구, 식별 뒤)로만. 이유가 있으면 이유를 적는다 - 버튼이 왜 죽었는지 모르는 것이 제일 나쁘다.
            if (Anchor(c))
            {
                bool ok = campaign.CanJump(out string why);
                _alpha = PickAlpha();

                if (RowButton(inn, ref y, "점프  (J)", ok ? $"Δv {campaign.jumpDeltaV:0} · 앞 {campaign.jumpStandoff / 1000f:0.0} km" : why, row++, !ok) && ok)
                    TryJump();
            }
        }

        y += SectionGap;
        int hostile = 0, friendly = 0;
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship s = Ship.All[i];
            if (s == null || s == player || s.dormant) continue;
            if (s.IsHostileTo(player)) hostile++; else friendly++;
        }
        int onlyBearing = 0, resolved = 0, identified = 0;
        foreach (ContactView.Contact c in ContactView.Known)
        {
            if (c.occluded) continue;
            if (c.state >= ContactView.Reveal.Identified) identified++;
            else if (c.state >= ContactView.Reveal.Resolved) resolved++;
            else onlyBearing++;
        }

        _alpha = Step(n++);
        Section(inn, ref y, "접촉", 2);
        Row(inn, ref y, "교전 중 적", $"{hostile}", row++, hostile > 0);
        // 방위만 / 위치 / 정체. 세 줄로 두면 아래 섹션이 밀려 잘린다 - 같은 사다리의 눈금이라 한 줄이 맞다.
        Row(inn, ref y, "접촉 단계", $"{onlyBearing} · {resolved} · {identified}", row++);

        int spent = 0;
        foreach (Campaign.Signal s in campaign.Signals) if (s.dead) spent++;
        Row(inn, ref y, "정리한 자리", $"{spent} / {campaign.Signals.Count}", row++);
        Row(inn, ref y, "추적 열기", $"{RunState.Heat} / {RunState.MaxHeat}", row++, RunState.Heat > 0);

        foreach (Campaign.Rover r in campaign.Rovers)
        {
            if (r.ship == null) continue;
            Vector2 h = (Vector2)r.ship.transform.position - pos;
            float rel = Mathf.DeltaAngle(player.transform.eulerAngles.z, Mathf.Atan2(h.y, h.x) * Mathf.Rad2Deg);
            Row(inn, ref y, r.tag, Mathf.Abs(rel) < 0.5f ? "정면" : rel > 0f ? $"좌 {rel:0}°" : $"우 {-rel:0}°", row++, r.hunter);
        }

        // 동료 연료. 출구에서 워프 Δv를 못 내는 동료는 낙오한다 - 그 전에 여기서 보인다.
        float mateDv = float.PositiveInfinity;
        foreach (Ship mate in campaign.Wingmates())
            if (mate.shipTanks.Count > 0) mateDv = Mathf.Min(mateDv, mate.AvailableDeltaV());
        if (!float.IsPositiveInfinity(mateDv))
            Row(inn, ref y, "동료 Δv 최소", $"{mateDv:0} / {Ballistics.WarpDeltaV:0}", row++, mateDv < Ballistics.WarpDeltaV);
        Row(inn, ref y, "들판 전체", $"{campaign.Signals.Count}", row++);

        y += SectionGap;
        _alpha = Step(n++);
        Section(inn, ref y, "항로", 3);

        // 출구마다 한 줄. 방위는 늘(신호라 방위는 안다), 거리는 좌표가 잡힌 뒤, 너머 이름은 센서 안에서.
        for (int k = 0; k < campaign.GateCount; k++)
        {
            Vector2 at = campaign.GateAt(k);
            Vector2 g = at - pos;
            float rel = Mathf.DeltaAngle(player.transform.eulerAngles.z, Mathf.Atan2(g.y, g.x) * Mathf.Rad2Deg);
            string bearing = Mathf.Abs(rel) < 0.5f ? "정면" : rel > 0f ? $"좌 {rel:0}°" : $"우 {-rel:0}°";
            ContactView.Reveal state = ContactView.StateAt(at);
            // 항로 자료를 건졌으면 출구까지 가기 전에 목적지와 상대적 위험을 안다.
            string beyond = state >= ContactView.Reveal.Identified || campaign.RouteIntelUnlocked ? campaign.GateLabel(k) : "";
            string dist = state >= ContactView.Reveal.Resolved ? $"{g.magnitude / 1000f:0.0} km" : "불명";
            // 누르면 그 출구를 고른다 - 고르면 방위·거리·항로 Δv가 위 "선택"에 그대로 뜬다.
            if (RowButton(inn, ref y, "출구 " + (k == 0 ? "A" : "B") + (string.IsNullOrEmpty(beyond) ? "" : " · " + beyond),
                          $"{bearing}  {dist}", row++, campaign.ChosenLane == k, ContactView.Selected == at))
                ContactView.Selected = at;

            if (campaign.RouteIntelUnlocked)
            {
                // 한 줄짜리 군사 보고서가 아니라 결정 카드. 반드시 "다음 구역"이라고 먼저 적어
                // 현재 들판의 접촉 정보와 섞이지 않게 한다.
                Campaign.RouteBriefing plan = campaign.GateBriefing(k);
                _alpha = Step(n++);
                Text("map_route_next" + k, new Rect(inn.x, y, inn.width, RowH), "다음 구역 예측", _kicker, 2);
                y += RowH;
                Row(inn, ref y, "  위험", plan.risk, row++, plan.risk == "높음");
                Row(inn, ref y, "  보급", plan.supplies, row++);
                Row(inn, ref y, "  회피", plan.evasion, row++, plan.evasion == "불리");
                Row(inn, ref y, "  판단", plan.advice, row++, plan.risk == "높음");
            }
        }

        if (!campaign.RouteIntelUnlocked && campaign.RouteIntelAvailable)
            Row(inn, ref y, "다음 구역 정보", "잔해·보급 방문 시 회수", row++);

        Row(inn, ref y, "워프 Δv", $"{Ballistics.WarpDeltaV:0} m/s", row++);

        _sideContent = y - top;

        // 막대는 넘칠 때만. 안 넘치는데 그리면 "굴릴 것이 있다"는 거짓말이 된다.
        if (_sideContent > inn.height)
        {
            float seen = inn.height / _sideContent;
            float barH = Mathf.Max(RowH, inn.height * seen);
            float at = inn.y + (inn.height - barH) * Mathf.Clamp01(_sideScroll / (_sideContent - inn.height));
            // **왼쪽 여백 안이다.** inn 안에 두면 라벨 글자와 겹치고, 겹침을 피하려고 내용을 오른쪽으로
            // 밀면 창이 그만큼 좁아진다. 패널 Pad(14)가 이미 비어 있으니 그 가운데를 쓴다 - 내용은 안 움직인다.
            float barX = rect.x + Pad * 0.5f - ScrollBarWidth * 0.5f;
            Rule("map_sbar_bg", new Rect(barX, inn.y, ScrollBarWidth, inn.height), 0.12f, 6);
            Rule("map_sbar", new Rect(barX, at, ScrollBarWidth, barH), 0.55f, 7);
        }

        return n;
    }

    /// <summary>점프 대상이 되는 것. 고정 앵커(보급·잔해·출구)를 식별한 뒤만 - 움직이는 것·미확인은 안 된다.</summary>
    private static bool Anchor(ContactView.Contact c)
        => c.state >= ContactView.Reveal.Identified && c.velocity == Vector2.zero
           && (c.kind == "보급" || c.kind == "잔해" || c.kind == "출구");

    private void TryJump()
    {
        ContactView.Contact? pick = Selected();
        Campaign campaign = Campaign.current;

        if (pick == null || campaign == null || !Anchor(pick.Value) || !campaign.CanJump(out _))
            return;

        Close();
        campaign.JumpTo(pick.Value.at);
    }

    private static ContactView.Contact? Selected()
    {
        Vector2? sel = ContactView.Selected;

        if (sel == null)
            return null;

        for (int i = 0; i < ContactView.Known.Count; i++)
            if (ContactView.Known[i].at == sel.Value)
                return ContactView.Known[i];

        return null;
    }

    // =========================================================
    // 조각
    // =========================================================

    /// <summary>
    /// 오른쪽 패널의 스크롤(px). **예전에는 넘치는 줄을 조용히 버렸다** - 마지막 섹션인 항로가 통째로
    /// 사라지고 아무도 몰랐다. 잘라내는 대신 굴린다.
    ///
    /// 즉시 모드라 리테인드 스크롤뷰(GUIGroup.Scrollable)를 못 쓴다 - 선언이 곧 그리기라 마스크가
    /// 붙을 자리가 없다. 대신 y 시작점을 밀고, 보이는 띠 밖의 줄은 **선언 자체를 안 한다**(그것이
    /// 즉시 모드에서 지우는 것이다). 줄 높이가 고르므로 클리핑이 한 줄 오차 안에서 정확하다.
    /// </summary>
    private float _sideScroll;

    /// <summary>이번 프레임 오른쪽 패널의 총 내용 높이. 스크롤 상한과 막대 길이가 여기서 나온다.</summary>
    private float _sideContent;

    private const float ScrollStep = 28f, ScrollBarWidth = 3f;

    /// <summary>보이는 띠 안인가. 위아래 어느 쪽으로 벗어나도 안 그린다 - 올려다보는 쪽도 똑같이 잘려야 한다.</summary>
    private static bool Visible(Rect inn, float y) => y + RowH > inn.y && y < inn.yMax;

    private void Section(Rect inn, ref float y, string title, int i)
    {
        if (Visible(inn, y))
        {
            Text("map_s" + i, new Rect(inn.x, y, inn.width, RowH), title, _kicker, 2);
            Rule("map_sr" + i, new Rect(inn.x, y + RowH - 2f, inn.width, 1f), 0.35f, 2);
        }

        y += RowH + 4f;   // 안 그려도 자리는 센다. 안 그러면 스크롤이 내려갈수록 내용이 줄어든다
    }

    /// <summary>마우스가 이 사각형 위인가. 패널 Rect는 시차(FollowMouse)로 같이 움직이므로 클릭과 같은 좌표를 쓴다.</summary>
    private bool Hot(Rect rect) => Mouse.current != null && rect.Contains(LogicalMouse());

    /// <summary>
    /// 누를 수 있는 줄. **이 화면의 행동은 전부 여기로 온다** - 점프도 출구 고르기도 키(J)만
    /// 있었고, 키를 모르면 지도가 읽기 전용 표였다. 마우스가 올라가면 줄이 밝아지는 것이
    /// "여기는 눌린다"의 유일한 표시다.
    /// </summary>
    private bool RowButton(Rect inn, ref float y, string label, string value, int i, bool warn = false, bool on = false)
    {
        bool clicked = false;

        if (Visible(inn, y))
        {
            var row = new Rect(inn.x - 5f, y - 1f, inn.width + 10f, RowH + 2f);
            bool hot = Hot(row);

            if (hot || on)
            {
                float keep = _alpha;
                _alpha *= hot ? 0.30f : 0.16f;
                Box("map_bh" + i, row, on ? _boxSelect : _boxSteel, 1);
                _alpha = keep;
            }

            Text("map_k" + i, new Rect(inn.x, y, inn.width * 0.6f, RowH), (hot ? "▸ " : "  ") + label, hot ? _name : _dim, 2);
            Text("map_v" + i, new Rect(inn.x, y, inn.width, RowH), value, warn ? _warn : _right, 2);

            if (hot && _clickAt is Vector2 click && row.Contains(click))
            {
                clicked = true;
                _clickAt = null;   // 지도 고르기가 이 클릭을 또 먹으면 선택이 풀린다
            }
        }

        y += RowH;
        return clicked;
    }

    private void Row(Rect inn, ref float y, string label, string value, int i, bool warn = false)
    {
        if (Visible(inn, y))
        {
            Text("map_k" + i, new Rect(inn.x, y, inn.width * 0.5f, RowH), label, _dim, 2);
            Text("map_v" + i, new Rect(inn.x, y, inn.width, RowH), value, warn ? _warn : _right, 2);
        }

        y += RowH;
    }

    private void Box(string id, Rect rect, GUIStyle style, int depth)
    {
        GUIBoxLabel b = ImGui.BoxLabel(id, rect, "", style);
        b.Layer = Layer + depth;
        b.Opacity = _alpha;
    }

    private void Text(string id, Rect rect, string text, GUIStyle style, int depth)
    {
        GUILabel l = ImGui.Label(id, rect, text, style);
        l.Layer = Layer + depth;
        l.Opacity = _alpha;
    }

    /// <summary>가는 선. Box 스타일은 최소 두께가 있어 1px을 못 그린다 - 픽셀 텍스처를 늘린다. 밝기는 Opacity.</summary>
    private void Rule(string id, Rect rect, float alpha, int depth)
    {
        GUIImage img = ImGui.Image(id, rect, Pixel);
        img.Opacity = alpha * _alpha;
        img.Layer = Layer + depth;
    }

    private void Dot(string id, Vector2 centre, float size, GUIStyle style, int depth)
        => Box(id, new Rect(centre.x - size * 0.5f, centre.y - size * 0.5f, size, size), style, depth);

    /// <summary>정사각 텍스처를 centre에 놓고 dir(GUI 좌표) 쪽으로 돌린다. 텍스처는 +X를 향해 구워져 있다.</summary>
    private void Image(string id, Vector2 centre, float size, Vector2 dir, Texture2D tex, float opacity, int depth)
    {
        GUIImage img = ImGui.Image(id, new Rect(centre.x - size * 0.5f, centre.y - size * 0.5f, size, size), tex);
        img.Rotation = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;   // GUI는 y 아래, 회전은 시계 방향 - 부호가 맞는다
        img.Opacity = opacity * _alpha;
        img.Layer = Layer + depth;
    }

    /// <summary>뱃머리(+X, 좌현 +Y)를 GUI 방향(y 아래)으로.</summary>
    private static Vector2 HeadingGui(Ship player)
    {
        float a = player.transform.eulerAngles.z * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(a), -Mathf.Sin(a));
    }

    private GUIStyle BoxFor(string kind) => kind switch
    {
        "출구" => _boxGate,
        "보급" => _boxSupply,
        "적" => _boxEnemy,
        _ => _boxSteel,
    };

    /// <summary>치운 자리의 밝기 배수. 0이면 지우는 것과 같아지고, 그러면 "안 가본 곳"과 다시 구별이 안 된다.</summary>
    private const float DeadDim = 0.3f;

    /// <summary>초를 읽을 수 있는 단위로. 6분짜리 항해에 "355초"는 숫자지 시간이 아니다.</summary>
    private static string Clock(float seconds)
        => seconds >= 60f ? $"{seconds / 60f:0.0}분" : $"{seconds:0}초";

    private static string StateName(ContactView.Reveal r) => r switch
    {
        ContactView.Reveal.Visited => "방문",
        ContactView.Reveal.Identified => "식별",
        ContactView.Reveal.Resolved => "위치",
        _ => "탐지",
    };

    /// <summary>F3 디버그 줄. 크기·거리·밝기·상태·차폐 - 안 보이는 이유는 이 다섯 중 하나다.</summary>
    private static string Debug(ContactView.Contact c)
        => $"sz {c.size / 1000f:0.0}k  d {c.distance / 1000f:0.0}k  st {c.strength:0.00}  {c.state}{(c.occluded ? "  OCC" : "")}";

    private static Texture2D WedgeSteel => _wedgeSteel ??= BakeWedge(Palette.Steel);
    private static Texture2D WedgeGold => _wedgeGold ??= BakeWedge(Palette.Radiance);

    private static Texture2D Pixel
    {
        get
        {
            if (_pixel != null) return _pixel;
            _pixel = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = Palette.Hull;
            _pixel.SetPixels32(px);
            _pixel.Apply(false, true);
            return _pixel;
        }
    }

    /// <summary>+X를 향한 부채꼴 한 장. 꼭짓점이 중심이라 배 위에 놓고 돌리면 꼭짓점이 배에 남는다. 멀수록 옅다.</summary>
    private static Texture2D BakeWedge(Color32 tint)
    {
        var px = new Color32[WedgePx * WedgePx];
        float c = (WedgePx - 1) * 0.5f;

        for (int y = 0; y < WedgePx; y++)
        for (int x = 0; x < WedgePx; x++)
        {
            float dx = x - c, dy = y - c;
            float r = Mathf.Sqrt(dx * dx + dy * dy) / c;
            float off = Mathf.Abs(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
            float edge = Mathf.Clamp01((WedgeHalfAngle - off) / 1.5f);
            float fade = Mathf.Clamp01(1f - r);
            float alpha = r < 0.03f ? 0f : edge * fade * fade;
            px[y * WedgePx + x] = new Color32(tint.r, tint.g, tint.b, (byte)(alpha * 255f));
        }

        var tex = new Texture2D(WedgePx, WedgePx, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    private void Styles()
    {
        if (_panel != null)
            return;

        _panel = GUIStyleMaker.Box(Palette.DeepSpace.WithAlpha(PanelAlpha));
        _floor = GUIStyleMaker.Box(Palette.Void.WithAlpha(PanelAlpha));
        _title = GUIStyleMaker.Label(Palette.Hull, 16).Font(16, FontStyle.Bold);
        _mono = GUIStyleMaker.Label(Palette.Hull, 13);
        _dim = GUIStyleMaker.Label(Palette.Steel, 13);
        _tick = GUIStyleMaker.Label(Palette.Steel.WithAlpha(0.8f), 10, TextAnchor.MiddleCenter);
        _right = GUIStyleMaker.Label(Palette.Hull, 13, TextAnchor.MiddleRight);
        _rightDim = GUIStyleMaker.Label(Palette.Steel, 13, TextAnchor.MiddleRight);
        _kicker = GUIStyleMaker.Label(Palette.Telemetry, 12).Font(12, FontStyle.Bold);
        _name = GUIStyleMaker.Label(Palette.Hull, 17).Font(17, FontStyle.Bold);
        _ask = GUIStyleMaker.Label(Palette.Steel, 17).Font(17, FontStyle.Bold);
        _select = GUIStyleMaker.Label(Palette.Radiance, 13).Font(13, FontStyle.Bold);
        _warn = GUIStyleMaker.Label(Palette.Radiance, 13, TextAnchor.MiddleRight).Font(13, FontStyle.Bold);

        _boxHull = GUIStyleMaker.Box(Palette.Hull);
        _boxGate = GUIStyleMaker.Box(Palette.Telemetry);
        _boxSupply = GUIStyleMaker.Box(Palette.Signal);
        _boxEnemy = GUIStyleMaker.Box(Palette.Breach.Muted(0.6f));
        _boxSteel = GUIStyleMaker.Box(Palette.Steel);
        _boxSelect = GUIStyleMaker.Box(Palette.Radiance);
    }
}
