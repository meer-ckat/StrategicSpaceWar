using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using IMGUI;

/// <summary>
/// 화면 밖에 무엇이 있는지. **이 게임에서 결정을 내릴 근거를 처음 만드는 자리다.**
///
/// 시뮬은 판 한 장까지 알고 있는데 플레이어는 화면에 든 것만 봤다. 그래서 구역에 들어설
/// 때마다 적이 "갑자기 나타났고", 부스터로 달리다 급정거하는 그림이 나왔다. 카메라를
/// 넓히는 것은 증상만 덮는다 - 진짜 답은 보이기 전에 아는 것이다.
///
/// **새로 재는 것이 없다.** 위치는 Transform, 편은 <see cref="Ship.team"/>이 이미 들고
/// 있던 값이고 여기서는 거리를 한 번 비교해서 화면에 놓기만 한다.
///
/// <see cref="ShipStatusHud"/>와 같은 자리, 같은 이유로 자기를 심는다.
/// </summary>
public sealed class ContactView : MonoBehaviour
{
    /// <summary>
    /// 이 안에 있으면 접촉이 잡힌다. **v1은 상수다** - def(<see cref="ShipDef"/>)로 옮기는
    /// 것은 배마다 값이 실제로 달라질 때다. 지금은 전부 같으므로 키를 늘려봐야 검증과
    /// 리로드 비용만 붙고 얻는 것이 없다.
    /// </summary>
    public const float SensorRange = 2200f;

    /// <summary>
    /// 이 안에 들어와야 편이 밝혀진다. 밖이면 그냥 "접촉"이다.
    ///
    /// 두 값을 같게 두면 미확인 단계가 통째로 사라져서 처음부터 적/아군이 보인다 -
    /// 다가가서 확인할 이유가 없어지는 것이 그 순간이다.
    /// </summary>
    private const float IdentifyRange = 500f;

    private const float SizeRange = 900f;

    /// <summary>스치는 정찰. 이 안을 이만큼 머물면 식별 거리를 안 밟아도 정체가 잡힌다 - 순항 속도로 지나면 500 m 원이 2초라 자주 빗나간다.</summary>
    private const float PassRange = 700f, PassDwell = 2f;

    /// <summary>운석 차폐 갱신 간격. 행 후보 8개뿐이라 지금은 매 프레임도 공짜지만 접촉이 늘면 아니다 - 처음부터 묶는다.</summary>
    private const float OccludeEvery = 0.25f;

    /// <summary>F3. 신호마다 크기·거리·밝기·상태·차폐를 지도에 찍는다. 안개 낀 화면이라 이게 없으면 "왜 안 보임?"에 답이 없다.</summary>
    public static bool ShowDebug;
    private const int SmallShipPlates = 60;
    private const int LargeShipPlates = 200;

    // ShipStatusHud와 같은 색조를 쓴다. 화면 전체에서 "괜찮다"와 "위험하다"가 같은 색이어야
    // 플레이어가 새로 배우지 않는다.
    // 이름은 Palette의 용도 그대로. 미확인 = 보조 정보, 적대 = 위험, 아군 = 정상, 출구·신호·정비 = 항법.
    // 접촉은 늘 떠 있는 것이라 원색을 안 쓴다. 아군은 위협이 아니니 무채(Hull), 적만 Breach를 채도 절반으로.
    // 위험 원색(Breach 100%)은 상태창의 손상·상실 줄 몫이다 - 같은 빨강이 둘이면 어느 쪽도 안 읽힌다.
    private static readonly Color UnknownColor = Palette.Steel;
    private static readonly Color HostileColor = Palette.Breach.Muted(ContactSaturation);
    private static readonly Color FriendlyColor = Palette.Hull;
    private const float ContactSaturation = 0.5f;   // 오너 손잡이
    private static readonly Color NavColor = Palette.Telemetry;
    private static readonly Color PanelBg = Palette.DeepSpace.WithAlpha(0.55f);

    private const float MarkerWidth = 108f;
    private const float MarkerHeight = 22f;

    /// <summary>화면에 든 배 위의 상태창 너비. 가장자리 표지보다 담는 것이 많다.</summary>
    private const float PanelWidth = 150f;

    /// <summary>
    /// 상태창을 배에서 이만큼 **월드 기준으로** 띄운다. 함선이 35 m쯤 되므로 그 위로 나온다.
    /// 픽셀로 띄우면 줌 배율에 따라 배 한가운데 겹치거나 저 멀리 뜬다.
    /// </summary>
    private const float StatusWorldRise = 26f;

    /// <summary>가장자리에서 이만큼 안쪽에 붙인다. 0이면 표지가 화면 밖으로 반쯤 잘린다.</summary>
    private const float EdgeInset = 16f;

    /// <summary>
    /// **HUD와는 레이어로 못 겨룬다** - ShipStatusHud는 GUIManager 밖의 별도 OnGUI라
    /// 실행 순서가 그리기 순서고, 그쪽이 "화면이 열려 있으면 안 그린다"로 피한다.
    /// 여기 값은 맵·대사·전체 화면과의 순서만 정한다(<see cref="UiLayer"/>).
    /// </summary>
    private const int MarkerLayer = UiLayer.Contact;

    /// <summary>
    /// 색마다 스타일 하나. **매 프레임 만들면 안 된다** - <see cref="GUIStyleMaker.Box"/>는
    /// 부를 때마다 GUIStyle을 복사하므로 접촉 수만큼 프레임마다 태어난다. 색이 셋뿐이라
    /// 처음 한 번 만들어 두면 그 할당이 아예 존재하지 않는다.
    /// </summary>
    private static GUIStyle _unknownStyle, _hostileStyle, _friendlyStyle, _navStyle;

    /// <summary>
    /// 접촉 하나를 읽는 데 필요한, 매 프레임 새로 구하면 아까운 것들.
    ///
    /// <see cref="maxAlive"/>가 **처음 본 판 수**인 것이 요점이다. 선체 비율의 분모인데,
    /// ShipDef의 배치 수를 쓰면 두 군데서 틀린다: 배치에는 판만이 아니라 포탑·엔진·탱크도
    /// 들어 있어서 멀쩡한 배가 87%로 뜨고, def를 매 프레임 읽는 비용이 붙는다. 적함은
    /// 캠페인이 갓 소환한 것이라 처음 본 수가 곧 설계 수다.
    /// </summary>
    private sealed class Tracked
    {
        public string id;
        public HullStructure structure;
        public int maxAlive;
    }

    /// <summary>
    /// 접촉별 캐시. 위젯 id 문자열을 매 프레임 보간하면 그것도 프레임마다 나는 쓰레기고,
    /// GetComponent도 접촉 수만큼 곱해진다. 한 런에서 보는 배가 수십 척이라 그냥 들고 있는다.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<int, Tracked> Seen = new();

    private static GUIStyle StyleFor(Color text, ref GUIStyle cache) =>
        cache ??= GUIStyleMaker.Box(background: PanelBg, text: text, fontSize: 13)
            .Padding(6, 0)
            .Align(TextAnchor.MiddleCenter);

    /// <summary>
    /// 이 배의 캐시. 처음 보는 순간의 판 수를 분모로 굳힌다.
    ///
    /// 수리로 판이 늘 수 있으므로(<see cref="Ship.RepairPlates"/>) 비율이 1을 넘을 수
    /// 있다 - 읽는 쪽에서 자른다.
    /// </summary>
    private static Tracked Track(Ship ship)
    {
        int key = ship.GetInstanceID();

        if (Seen.TryGetValue(key, out Tracked tracked))
        {
            // 건조 중에 처음 봤으면 판 수가 0으로 굳었다. 완성 뒤 첫 프레임에 다시 잡는다.
            if (tracked.maxAlive == 0 && tracked.structure != null)
                tracked.maxAlive = tracked.structure.AliveCount;

            return tracked;
        }

        var structure = ship.GetComponent<HullStructure>();

        tracked = new Tracked
        {
            id = "contact_" + key,
            structure = structure,
            maxAlive = structure != null ? structure.AliveCount : 0,
        };

        Seen[key] = tracked;
        return tracked;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Contact View");
        DontDestroyOnLoad(go);
        go.AddComponent<ContactView>();
    }

    private void Update()
    {
        // 격파 시퀀스 중에는 접촉 마커가 즉시 꺼진다. 선언을 그만두는 것이 곧 지우는 것이다.
        // 스타일 공장은 GUIManager의 첫 OnGUI가 켠다. 그 전에 StyleFor가 돌면 빈 GUIStyle이 캐시에 영영 남는다.
        if (GameManager.GuiHidden || CutSceneManager.ControlsPlayer || !GUIStyleMaker.Initialized)
            return;

        ImGui.Begin();

        Ship player = Player();
        Camera cam = Camera.main;

        if (player == null || cam == null)
            return;

        Vector2 eye = player.transform.position;

        // **두꺼운 것은 하나다.** 포탑이 겨누는 배(NearestHostile)만 이름·선체·상실 줄을 받고
        // 나머지 접촉은 짧은 표만 받는다. 전부 두껍게 쓰면 위계가 없어서 아무것도 안 읽힌다 -
        // 에이스 컴뱃이 선택 표적 하나만 크게 그리는 이유.
        Ship primary = player.NearestHostile();

        // 지도가 열려 있으면 마커는 쉰다 - 0.7 알파 패널 밑에서 비친다. 트래커는 계산까지만 돈다.
        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship other = Ship.All[i];

            if (other == null || other == player)
                continue;

            Observe(eye, other);

            if (!MapScreen.IsOpen)
                Draw(cam, eye, other, other == primary);
        }

        DrawTracker(cam, eye);

        if (!MapScreen.IsOpen)
            DrawRefitPrompt();
    }

    /// <summary>
    /// 적 접촉이 있나. 카메라의 항해 줌이 이걸로 갈린다 - 접촉 마커가 뜨는 조건과 같은 것을
    /// 쓴다(센서 안 + <see cref="Ship.IsHostileTo"/>: 잠든 배·잔해·중립은 접촉이 아니다).
    /// 두 자리가 다른 술어를 쓰면 "마커는 있는데 줌이 안 돌아온다"가 생긴다.
    /// </summary>
    public static bool HasHostileContact(Ship player)
    {
        if (player == null)
            return false;

        Vector2 eye = player.transform.position;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship other = Ship.All[i];

            if (other == null || !player.IsHostileTo(other))
                continue;

            float reach = SensorRange * other.Emission;

            if (((Vector2)other.transform.position - eye).sqrMagnitude <= reach * reach)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 신호 트래커. 출구 + 가까운 신호 몇 개를 한 줄씩 늘어놓고 **하나만 고른다** - 고른 것만
    /// 가장자리 마커(방위 + 거리)를 받는다. 마커를 전부 띄우면 가장자리가 겹쳐서 하나도 안
    /// 읽히고, 리스트가 그 자리를 대신한다. 에이스 컴뱃이 선택 표적 하나에만 화살표를 주는 규칙.
    ///
    /// **행에 거리는 없다.** 거리를 주면 항해가 지도 클릭이 된다. 정체도 식별 거리 안에 한 번
    /// 들어간 뒤에야 적힌다 - 처음부터 이름이 보이면 이건 퀘스트 목록이다. 행 수를 자르는
    /// 기준이 거리인 것은 안 보이는 순서일 뿐이다(센서가 센 신호부터).
    /// 클릭 또는 [ ]로 고른다. 색·간격·행 높이는 오너.
    /// </summary>
    private static void DrawTracker(Camera cam, Vector2 eye)
    {
        Known.Clear();   // 어떤 return보다 먼저. 안 비우면 지난 프레임 목록이 지도에 유령으로 남는다

        Campaign campaign = Campaign.current;
        SectorDef sector = campaign?.Current;

        if (sector == null || !sector.Open)
            return;

        if (_trackerSector != sector)
        {
            _trackerSector = sector;
            _reveal.Clear();
            _dwell.Clear();
            _occluded.Clear();
            _ghosts.Clear();
            _selected = null;

            // 원장 복원. 새 노드면 비어 있고, 재개면 알아낸 좌표가 돌아온다.
            foreach (Vector3 r in RunState.Reveals)
                _reveal[new Vector2(r.x, r.y)] = (Reveal)Mathf.Clamp((int)r.z, 0, (int)Reveal.Visited);
        }

        Keyboard keys = Keyboard.current;

        if (keys != null && keys.f3Key.wasPressedThisFrame)
            ShowDebug = !ShowDebug;

        _rows.Clear();

        // 움직이는 적(추격·순찰)은 원장이 없다 - 늘 방위만. 센서 안에 들면 함선 마커가 따로 뜬다.
        foreach (Campaign.Rover r in campaign.Rovers)
        {
            if (r.ship == null)
                continue;

            Vector2 hat = r.ship.transform.position;
            float hd = Vector2.Distance(eye, hat);
            float size = r.hunter ? HunterSize : PatrolSize;
            bool hidden = RockBetween(eye, hat);

            if (!hidden || ShowDebug)
                Known.Add(new Contact(hat, r.tag, "적", Reveal.Detected, Mathf.Clamp01(size / Mathf.Max(hd, 1f)), hd, size, hidden, r.ship.velocity));
        }

        // 마지막 관측. 센서에서 나간 적은 마지막 자리·속도가 흐려지며 남는다 - 뒤를 못 읽으면 쫓기는 것이 재미가 아니라 억까다.
        float nowT = Time.unscaledTime;

        foreach (KeyValuePair<int, Ghost> kv in _ghosts)
        {
            Ghost g = kv.Value;
            float age = nowT - g.seen;

            if (g.live || age > GhostSeconds)
                continue;

            string label = $"{g.label} · {age:0}s 전";
            _rows.Add((g.at, label));
            Known.Add(new Contact(g.at, label, "관측", Reveal.Resolved, 1f - age / GhostSeconds, Vector2.Distance(eye, g.at), 0f, false, g.velocity));
        }

        _near.Clear();
        float dt = Time.unscaledDeltaTime;

        foreach (Campaign.Signal s in campaign.Signals)
        {
            float d = Vector2.Distance(eye, s.at);
            Reveal was = StateOf(s.at);
            Reveal now = was;

            // 단계는 올라가기만 한다. 15 km에서 잡은 기항지 좌표는 멀어져도 남는다.
            // 출구는 센서 안(2,200 m)이면 식별이다 - 500 m까지 가야 "출구"인 건 너무 늦다.
            if (d <= s.size) now = Max(now, Reveal.Resolved);
            if (d <= (s.gate >= 0 ? SensorRange : IdentifyRange)) now = Max(now, Reveal.Identified);
            if (d <= campaign.refitRadius) now = Max(now, Reveal.Visited);

            if (d <= PassRange)
            {
                _dwell.TryGetValue(s.at, out float t);
                _dwell[s.at] = t += dt;

                if (t >= PassDwell)
                    now = Max(now, Reveal.Identified);
            }
            else
                _dwell.Remove(s.at);

            if (now != was)
            {
                _reveal[s.at] = now;
                RunState.RememberReveal(s.at, (int)now);
            }

            if (s.dead)
            {
                // 소멸한 신호는 행을 안 먹는다 - 살아 있는 여덟 개와 자리를 다투면 정리한 보람이 없다.
                // 지도에는 남는다. 그것이 "지나온 길"이다.
                Known.Add(new Contact(s.at, "소멸", "소멸", Max(now, Reveal.Resolved), 0f, d, s.size, false));
                continue;
            }

            _near.Add((d, s.at, s.size));
        }

        // 제일 잘 보이는 순. 거리순이면 60 km 밖의 출구·기항지가 코앞의 초계 8개에 밀려 지도에서 사라진다.
        _near.Sort((a, b) => (b.size / Mathf.Max(b.d, 1f)).CompareTo(a.size / Mathf.Max(a.d, 1f)));
        Occlude(eye);

        for (int i = 0; i < _near.Count && _rows.Count < TrackerRows; i++)
        {
            (float d, Vector2 at, float size) = _near[i];
            Reveal state = StateOf(at);
            bool occluded = state < Reveal.Resolved && _occluded.Contains(at);

            // 운석 뒤의 신호는 없는 것이다. 돌아 나오면 다시 뜬다. 좌표를 아는 것은 안 사라진다.
            if (occluded && !ShowDebug)
                continue;

            string label = LabelFor(campaign, sector, at, state, out string kind);

            if (!occluded)
                _rows.Add((at, label));

            Known.Add(new Contact(at, label, kind, state, Mathf.Clamp01(size / Mathf.Max(d, 1f)), d, size, occluded));
        }

        // 좌표를 안 자리는 행에서 밀려나도 지도에 남는다 - 알아낸 것은 잊지 않는다.
        // 소멸한 것은 위 루프가 이미 넣었으므로 InKnown이 걸러낸다.
        foreach (KeyValuePair<Vector2, Reveal> kv in _reveal)
        {
            if (kv.Value < Reveal.Resolved || InKnown(kv.Key))
                continue;

            string label = LabelFor(campaign, sector, kv.Key, kv.Value, out string kind);
            Known.Add(new Contact(kv.Key, label, kind, kv.Value, 1f, Vector2.Distance(eye, kv.Key), 0f, false));
        }

        if (_rows.Count == 0)
            return;

        int sel = -1;

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_selected == _rows[i].at)
                sel = i;
        }

        if (sel < 0)
            sel = 0;

        if (keys != null && keys.rightBracketKey.wasPressedThisFrame)
            sel = (sel + 1) % _rows.Count;
        else if (keys != null && keys.leftBracketKey.wasPressedThisFrame)
            sel = (sel + _rows.Count - 1) % _rows.Count;

        // 행 목록은 지도(M)로 갔다(2026-09-12). 여기 남는 것은 [ ] 순환과 고른 것 하나의 가장자리 마커뿐이다.
        _selected = _rows[sel].at;

        if (MapScreen.IsOpen)
            return;   // 지도가 열려도 [ ]는 돈다. 그리기는 지도가 한다

        // 고른 것 하나만 마커. 거리는 좌표를 잡은 뒤에만 - 방위만 아는 것에 거리를 주면 좌표를 준 것이다.
        float distance = Vector2.Distance(eye, _selected.Value);
        string marker = StateOf(_selected.Value) < Reveal.Resolved ? _rows[sel].label
            : distance >= 1000f ? $"{_rows[sel].label}  {distance / 1000f:0.0} km" : $"{_rows[sel].label}  {distance:0} m";

        Ship player = campaign.Player;
        bool isGate = false;

        for (int k = 0; k < campaign.GateCount; k++)
            if (campaign.GateAt(k) == _selected.Value) isGate = true;

        if (isGate && player != null && player.shipTanks.Count > 0
            && player.AvailableDeltaV() < Ballistics.WarpDeltaV)
            marker += $"  Δv 부족 {player.AvailableDeltaV():0}/{Ballistics.WarpDeltaV:0}";

        EdgeMarker(cam, _selected.Value, "track_sel", marker, StyleFor(NavColor, ref _navStyle));
    }

    /// <summary>이 자리의 좌표를 아나. 맵 창이 같은 장부를 읽는다.</summary>
    public static bool Discovered(Vector2 at) => StateOf(at) >= Reveal.Resolved;

    /// <summary>
    /// 신호를 얼마나 아는가. 넷을 하나로 뭉치면 15 km에서 잡은 기항지와 500 m에서 확인한 초계가 같은 상태가 된다 -
    /// 마지막 관측 위치를 붙이는 날 그 둘을 갈라야 하는데 그때는 늦다.
    /// </summary>
    public enum Reveal
    {
        Detected,     // 뭔가 있다. 방위만
        Resolved,     // 좌표가 잡혔다 (d ≤ signalSize)
        Identified,   // 종류를 안다 (d ≤ 500 m, 또는 700 m 안을 2초)
        Visited,      // 실제로 갔다 (d ≤ refitRadius)
    }

    private static Reveal StateOf(Vector2 at) => _reveal.TryGetValue(at, out Reveal r) ? r : Reveal.Detected;

    /// <summary>이 자리를 얼마나 아는가. 지도가 출구 거리를 보여줄지 정할 때 읽는다.</summary>
    public static Reveal StateAt(Vector2 at) => StateOf(at);

    /// <summary>
    /// 이름과 색. 출구는 좌표가 잡히면 "출구", 센서 안이면 "출구 · 너머 이름". 자리는 식별 뒤에 템플릿 label.
    /// </summary>
    private static string LabelFor(Campaign campaign, SectorDef sector, Vector2 at, Reveal state, out string kind)
    {
        for (int k = 0; k < campaign.GateCount; k++)
        {
            if (campaign.GateAt(k) != at)
                continue;

            if (state < Reveal.Resolved)
                return Unknown(out kind);

            kind = "출구";
            string beyond = campaign.GateLabel(k);
            return state >= Reveal.Identified && !string.IsNullOrEmpty(beyond) ? "출구 · " + beyond : "출구";
        }

        if (state >= Reveal.Identified)
            return Identify(sector, at, out kind);

        // 좌표는 잡았지만 아직 500 m를 안 밟았다. 줄 수 있는 정직한 단서는 "움직이는 것이 있나" 하나다 -
        // Hulk는 추력이 없으므로 그 값이 곧 "위험이 스스로 다가올 수 있나"다. 정체는 여전히 안 준다.
        if (state >= Reveal.Resolved && campaign.SignalAt(at, out Campaign.Signal sig))
        {
            kind = "신호";
            return sig.crewed ? "활성" : "표류";
        }

        return Unknown(out kind);
    }
    private static Reveal Max(Reveal a, Reveal b) => a > b ? a : b;
    private static string Unknown(out string kind) { kind = "신호"; return "신호"; }

    /// <summary>운석 뒤의 신호. 행 후보만, 0.25초마다. 좌표를 아는 것은 검사도 안 한다 - 어차피 안 사라진다.</summary>
    private static void Occlude(Vector2 eye)
    {
        if (Time.unscaledTime - _occludedAt < OccludeEvery)
            return;

        _occludedAt = Time.unscaledTime;
        _occluded.Clear();
        int n = Mathf.Min(_near.Count, TrackerRows);

        for (int i = 0; i < n; i++)
        {
            Vector2 at = _near[i].at;

            if (StateOf(at) < Reveal.Resolved && RockBetween(eye, at))
                _occluded.Add(at);
        }
    }

    /// <summary>
    /// 두 점 사이에 운석이 있나. 센서와 사선의 같은 질문이다 - 내가 못 보면 적도 못 본다(잠든 적 깨우기,
    /// 추격의 마지막 관측). 잔해·배는 안 센다 - 운석만이 지형이다.
    /// </summary>
    public static bool RockBetween(Vector2 a, Vector2 b)
    {
        _hits.Clear();
        Physics2D.Linecast(a, b, _noFilter, _hits);

        for (int h = 0; h < _hits.Count; h++)
        {
            Hulk hulk = _hits[h].collider.GetComponentInParent<Hulk>();

            if (hulk != null && hulk.structureDefName == OpenSectorGen.Rock)
                return true;
        }

        return false;
    }

    private const float HunterSize = 3000f;   // 추격 신호의 크기. 초계(500)보다 크고 보급(8000)보다 작다
    private const float PatrolSize = 1500f;
    private const float GhostSeconds = 120f;  // 마지막 관측이 지도에 남는 시간

    private sealed class Ghost
    {
        public Vector2 at, velocity;
        public float seen;
        public string label;
        public bool live;   // 이번 프레임에 센서 안이다 - 유령이 아니라 실체
    }

    private static readonly Dictionary<int, Ghost> _ghosts = new();

    /// <summary>센서 안의 적을 적어 둔다. 나가면 그 값이 유령이 된다.</summary>
    private static void Observe(Vector2 eye, Ship other)
    {
        if (other.team != Ship.Team.Enemy || other.dormant)
            return;

        int key = other.GetInstanceID();
        Vector2 at = other.transform.position;
        float d = Vector2.Distance(eye, at);
        bool inSensor = d <= SensorRange * other.Emission && !RockBetween(eye, at);

        if (!inSensor)
        {
            if (_ghosts.TryGetValue(key, out Ghost old))
                old.live = false;

            return;
        }

        if (!_ghosts.TryGetValue(key, out Ghost g))
            _ghosts[key] = g = new Ghost();

        g.at = at;
        g.velocity = other.velocity;
        g.seen = Time.unscaledTime;
        g.live = true;
        g.label = d <= IdentifyRange ? Name(other) : "접촉";
    }

    /// <summary>트래커가 아는 것 하나. 지도는 이 목록만 찍는다 - 좌표를 알면 제자리에, 모르면 방향만.</summary>
    public readonly struct Contact
    {
        public readonly Vector2 at;
        public readonly string label;    // 식별 전엔 "신호"
        public readonly string kind;     // 출구 / 보급 / 적 / 잔해 / 신호 - 지도 색
        public readonly Reveal state;
        public readonly float strength;  // signalSize / 거리, 0~1. 부채꼴 밝기
        public readonly float distance, size;
        public readonly bool occluded;   // 디버그에서만 목록에 남는다
        public readonly Vector2 velocity; // 자리는 0. 추격만 움직인다

        public bool located => state >= Reveal.Resolved;

        public Contact(Vector2 at, string label, string kind, Reveal state, float strength, float distance, float size, bool occluded,
            Vector2 velocity = default)
        {
            this.at = at;
            this.label = label;
            this.kind = kind;
            this.state = state;
            this.strength = strength;
            this.distance = distance;
            this.size = size;
            this.occluded = occluded;
            this.velocity = velocity;
        }
    }

    /// <summary>이번 프레임에 아는 것 전부: 트래커 행 + 가 본 자리 + 출구. DrawTracker가 매 프레임 다시 채운다.</summary>
    public static readonly List<Contact> Known = new();

    private static bool InKnown(Vector2 at)
    {
        for (int i = 0; i < Known.Count; i++)
            if (Known[i].at == at) return true;
        return false;
    }

    /// <summary>트래커가 고른 자리. 맵 창이 같은 것을 고르고 같은 것을 바꾼다 - 두 벌이면 둘이 다른 말을 한다.</summary>
    public static Vector2? Selected
    {
        get => _selected;
        set => _selected = value;
    }

    /// <summary>
    /// 식별된 자리의 정체. 자리 근처 배치를 읽는다. 이름은 템플릿 label(보급 부표·교전 흔적·초계 접촉), kind는 지도 색.
    /// 적이 하나라도 있으면 적이 정체다 - 잔해밭 속의 매복은 잔해가 아니다.
    /// </summary>
    public static string Identify(SectorDef sector, Vector2 at, out string kind)
    {
        kind = "신호";
        string label = "신호";

        foreach (SpawnDef spawn in sector.spawns)
        {
            if (spawn.scenery
                || (new Vector2(spawn.x, spawn.y) - at).sqrMagnitude > TrackerFold * TrackerFold)
                continue;

            if (!spawn.hulk)
            {
                kind = "적";
                return string.IsNullOrEmpty(spawn.label) ? kind : spawn.label;
            }

            bool supply = spawn.refit || spawn.materials > 0 || spawn.propellant > 0 || spawn.munitions > 0;
            kind = supply ? "보급" : "잔해";
            label = string.IsNullOrEmpty(spawn.label) ? kind : spawn.label;
        }

        return label;
    }

    /// <summary>월드 방향(y 위)의 8방위 글자.</summary>
    private static string Glyph(Vector2 d)
    {
        int sector = Mathf.RoundToInt(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg / 45f);
        return Arrows[((sector % 8) + 8) % 8];
    }

    private const int TrackerRows = 8;
    private const float TrackerFold = 400f;   // Campaign.SiteFold와 같은 값. 자리 하나의 배들이 ±90 m 안이다

    private static GUIStyle _promptStyle;
    private static SectorDef _trackerSector;
    private static Vector2? _selected;
    private static readonly Dictionary<Vector2, Reveal> _reveal = new();   // 자리마다 가장 높이 올라간 단계. 구역이 바뀌면 지운다
    private static readonly Dictionary<Vector2, float> _dwell = new();     // 700 m 안에 머문 초
    private static readonly HashSet<Vector2> _occluded = new();
    private static readonly List<RaycastHit2D> _hits = new(16);
    private static readonly ContactFilter2D _noFilter = new ContactFilter2D().NoFilter();
    private static float _occludedAt = -1f;
    private static readonly List<(Vector2 at, string label)> _rows = new();
    private static readonly List<(float d, Vector2 at, float size)> _near = new();

    /// <summary>정비 잔해 옆. 한 줄이면 된다 - 키 하나를 알리는 것이 전부다.</summary>
    private static void DrawRefitPrompt()
    {
        Campaign campaign = Campaign.current;

        if (campaign == null || RefitScreen.IsOpen || !(campaign.RefitSpotNear || campaign.RefitSpotTooFast))
            return;

        // 너무 빠르면 못 댄다. 지나치면서 R을 눌러봐야 아무 일도 없는 것보다 "감속"이 낫다.
        string text = campaign.RefitSpotNear
            ? "R  정비"
            : $"감속  {campaign.Player.velocity.magnitude:0} → {campaign.dockSpeed:0} m/s";

        const float w = 200f, h = 26f;
        GUIBoxLabel prompt = ImGui.BoxLabel(
            "refit_prompt",
            new Rect((GUIManager.LogicalWidth - w) * 0.5f, GUIManager.LogicalHeight - 120f, w, h),
            text,
            StyleFor(NavColor, ref _promptStyle));

        prompt.Layer = MarkerLayer;
    }

    /// <summary>
    /// 접촉 하나. 범위 밖이거나 화면 안이면 **선언 자체를 안 한다** - 즉시 모드에서는
    /// 그것이 곧 지우는 것이고, 그래서 사라지는 경로에 코드가 없다.
    /// </summary>
    private static void Draw(Camera cam, Vector2 eye, Ship other, bool primary)
    {
        Vector2 at = other.transform.position;
        float distance = Vector2.Distance(eye, at);

        if (distance > SensorRange * other.Emission)
            return;

        // 잠든 배는 식별 거리 밖에서 안 그린다. 잔해밭 속 매복이 1200 m에서 "접촉 ×3"으로
        // 읽히면 겉과 속이 같은 것이다.
        if (other.dormant && distance > IdentifyRange)
            return;

        Vector3 screen = cam.WorldToScreenPoint(at);

        // WorldToScreenPoint는 항상 실제 화면 픽셀이다 - GUIManager의 배율(uiScale)과
        // 무관하다. GUIManager.OnGUI가 그리기 전에 배율을 다시 곱하므로, 여기서 미리
        // 나눠 논리 좌표로 바꿔야 최종 위치가 실제 화면의 같은 자리로 돌아온다. onScreen
        // 판정도 이 나눗셈 뒤에 논리 화면 크기와 비교해야 같은 공간에서 재는 것이다.
        Vector2 logical = (Vector2)screen / GUIManager.UiScale;

        // z < 0이면 카메라 뒤다. 직교 투영이라 전투 중에는 안 생기지만, 컷신이 카메라를
        // 옮기는 동안 생길 수 있고 그때 x/y가 뒤집혀 들어온다.
        bool onScreen = screen.z > 0f
            && logical.x >= 0f && logical.x <= GUIManager.LogicalWidth
            && logical.y >= 0f && logical.y <= GUIManager.LogicalHeight;

        bool identified = distance <= IdentifyRange;
        bool sized = distance <= SizeRange;

        if (onScreen)
        {
            Status(cam, at, other, distance, identified, sized, primary);
            return;
        }

        // 가장자리: 주 표적만 거리를 받는다. 나머지는 무엇인지까지만 - 숫자가 여럿이면 하나도 안 읽힌다.
        string what = identified ? Name(other) : sized ? $"접촉 {SizeClass(other)}" : "접촉";
        string text = primary ? $"{what}  {distance:0} m" : what;

        GUIStyle style = !identified ? StyleFor(UnknownColor, ref _unknownStyle)
            : other.team == Ship.Team.Enemy ? StyleFor(HostileColor, ref _hostileStyle)
            : StyleFor(FriendlyColor, ref _friendlyStyle);

        // id는 GetInstanceID다. Thing.stableId는 def의 배치 인덱스라 배끼리 겹친다 -
        // 그걸 쓰면 두 접촉이 같은 위젯을 두고 매 프레임 싸운다.
        EdgeMarker(cam, at, Track(other).id, text, style);
    }

    /// <summary>
    /// 화면 가장자리의 브래킷 하나. 화면 안 좌표가 들어오면 그 자리에 그대로 그린다.
    ///
    /// 밖이면 **화면 중심에서 표적으로 그은 직선이 테두리와 만나는 자리**다. 예전에는 x·y를
    /// 각각 잘랐는데(클램프), 그러면 대각선 방향 표적이 전부 모서리 한 점에 쌓여서 방향이
    /// 죽는다. 테두리는 HUD 패널 띠 안쪽(<see cref="ShipStatusHud.TopBand"/>) - 가장자리에
    /// 붙이면 네 귀의 패널 밑에 깔리고, Layer를 올려도 글자 위의 꺾쇠는 안 읽힌다.
    /// 방향은 글자 하나로 앞에 붙인다(8방위).
    /// </summary>
    private static void EdgeMarker(Camera cam, Vector2 at, string id, string text, GUIStyle style)
    {
        Vector3 screen = cam.WorldToScreenPoint(at);
        Vector2 logical = (Vector2)screen / GUIManager.UiScale;

        // GUI 좌표는 y가 아래로 증가한다. 스크린 좌표는 위로 증가하므로 여기서 뒤집는다.
        Vector2 point = new(logical.x, GUIManager.LogicalHeight - logical.y);

        if (screen.z < 0f)
            point = new Vector2(GUIManager.LogicalWidth, GUIManager.LogicalHeight) - point;

        var bounds = new Rect(
            EdgeInset, ShipStatusHud.TopBand,
            GUIManager.LogicalWidth - EdgeInset * 2f,
            GUIManager.LogicalHeight - ShipStatusHud.TopBand - ShipStatusHud.BottomBand);

        Vector2 pos = EdgePoint(bounds, point, out string glyph);

        float x = Mathf.Clamp(pos.x - MarkerWidth * 0.5f, bounds.xMin, bounds.xMax - MarkerWidth);
        float y = Mathf.Clamp(pos.y - MarkerHeight * 0.5f, bounds.yMin, bounds.yMax - MarkerHeight);

        GUIBoxLabel marker = ImGui.BoxLabel(
            id, new Rect(x, y, MarkerWidth, MarkerHeight), glyph == null ? text : glyph + " " + text, style);
        marker.Layer = MarkerLayer;
    }

    /// <summary>
    /// 테두리 위의 자리. 안이면 그대로(글자 없음). 밖이면 중심→점 직선이 사각형과 만나는 점과
    /// 8방위 글자. GUI 좌표라 y가 아래로 증가한다 - 글자는 위쪽이 ↑가 되게 y를 뒤집어 잰다.
    /// </summary>
    internal static Vector2 EdgePoint(Rect bounds, Vector2 point, out string glyph)
    {
        glyph = null;

        if (bounds.Contains(point))
            return point;

        Vector2 c = bounds.center;
        Vector2 d = point - c;
        float tx = Mathf.Abs(d.x) > 1e-4f ? bounds.width * 0.5f / Mathf.Abs(d.x) : float.PositiveInfinity;
        float ty = Mathf.Abs(d.y) > 1e-4f ? bounds.height * 0.5f / Mathf.Abs(d.y) : float.PositiveInfinity;
        float t = Mathf.Min(tx, ty);

        glyph = Glyph(new Vector2(d.x, -d.y));

        return c + d * t;
    }

    private static readonly string[] Arrows = { "→", "↗", "↑", "↖", "←", "↙", "↓", "↘" };

    private static string SizeClass(Ship other)
    {
        int plates = Track(other).maxAlive;
        return plates < SmallShipPlates ? "소형" : plates < LargeShipPlates ? "중형" : "대형";
    }

    /// <summary>
    /// 화면에 든 배 위의 상태창. **어디를 쏠지가 결정이 되는 자리다** - 지금까지는 적이
    /// 그냥 "쏘면 되는 것"이었고, 무엇을 부쉈는지 화면에 도달하지 않았다.
    ///
    /// 새로 재는 것이 없다. 넷 다 <see cref="Ship"/>이 이미 매 틱 답하던 파생값이다.
    ///
    /// **죽은 계통만 적는다.** 넷을 늘 적으면 멀쩡한 배마다 "주포 · 기관 · 전력"이 떠서
    /// 읽을 것이 늘기만 한다. 없다가 생기는 것이 곧 "방금 내 사격이 뭔가 했다"는 신호다.
    /// </summary>
    private static void Status(Camera cam, Vector2 at, Ship other, float distance, bool identified, bool sized, bool primary)
    {
        // 배 **위쪽**으로 월드 기준 offset이다. 화면 픽셀로 띄우면 줌아웃했을 때 패널이
        // 배에서 저 멀리 떨어져 뜬다 - 속도 줌이 붙은 뒤로는 그 폭이 크다.
        //
        // WorldToScreenPoint는 실제 화면 픽셀이라 uiScale로 나눠 논리 좌표로 바꾼다 -
        // Draw의 logical과 같은 이유다.
        Vector2 head = (Vector2)cam.WorldToScreenPoint(at + Vector2.up * StatusWorldRise)
            / GUIManager.UiScale;

        float x = head.x - PanelWidth * 0.5f;
        float y = GUIManager.LogicalHeight - head.y - MarkerHeight;

        Tracked tracked = Track(other);

        if (!identified)
        {
            // 화면에 들었어도 아직 먼 배다. 실물이 보이니 무엇인지는 알지만 편은 모른다.
            GUIBoxLabel tag = ImGui.BoxLabel(
                tracked.id,
                new Rect(x, y, PanelWidth, MarkerHeight),
                sized ? $"접촉 {SizeClass(other)}  {distance:0} m" : $"접촉  {distance:0} m",
                StyleFor(UnknownColor, ref _unknownStyle));

            tag.Layer = MarkerLayer;
            return;
        }

        // 주 표적이 아니면 편만. 선체·상실은 겨누는 배 하나의 것이다.
        if (!primary)
        {
            GUIBoxLabel tag = ImGui.BoxLabel(
                tracked.id,
                new Rect(x + (PanelWidth - MarkerWidth) * 0.5f, y, MarkerWidth, MarkerHeight),
                Name(other),
                other.team == Ship.Team.Enemy
                    ? StyleFor(HostileColor, ref _hostileStyle)
                    : StyleFor(FriendlyColor, ref _friendlyStyle));

            tag.Layer = MarkerLayer;
            return;
        }

        float hull = tracked.maxAlive > 0 && tracked.structure != null
            ? Mathf.Clamp01((float)tracked.structure.AliveCount / tracked.maxAlive)
            : 1f;

        GUIStyle nameStyle = other.team == Ship.Team.Enemy
            ? StyleFor(HostileColor, ref _hostileStyle)
            : StyleFor(FriendlyColor, ref _friendlyStyle);

        GUIBoxLabel head1 = ImGui.BoxLabel(
            tracked.id,
            new Rect(x, y, PanelWidth, MarkerHeight),
            $"{Name(other)}  선체 {hull * 100f:0}%",
            nameStyle);

        head1.Layer = MarkerLayer;

        string lost = LostSystems(other);

        if (lost == null)
            return;

        GUIBoxLabel line2 = ImGui.BoxLabel(
            tracked.id + "_lost",
            new Rect(x, y + MarkerHeight + 2f, PanelWidth, MarkerHeight),
            lost + " 상실",
            StyleFor(HostileColor, ref _hostileStyle));

        line2.Layer = MarkerLayer;
    }

    /// <summary>
    /// 죽은 계통을 이어 붙인다. 하나도 없으면 null - 부르는 쪽이 줄 자체를 선언 안 한다.
    ///
    /// 셋이 각각 다른 결정을 만든다: 주포가 죽으면 안전하게 갉을 수 있고, 기관이 죽으면
    /// 도망을 못 가고, 전력이 죽으면 조타와 조준이 **한꺼번에** 멈춘다. 기압을 안 넣는
    /// 것은 승무원이 죽으면 어차피 셋 다 꺼지기 때문이다 - 같은 말이 두 번 나온다.
    /// </summary>
    private static string LostSystems(Ship ship)
    {
        string lost = null;

        if (!ship.HasUsableGun)
            lost = "주포";

        if (ship.AvailableThrust(true) <= 0f && ship.AvailableThrust(false) <= 0f)
            lost = lost == null ? "기관" : lost + "·기관";

        if (!ship.HasPower)
            lost = lost == null ? "전력" : lost + "·전력";

        return lost;
    }

    private static string Name(Ship ship) =>
        ship.team switch
        {
            Ship.Team.Enemy => "적함",
            Ship.Team.Ally => "아군",
            _ => "중립",
        };

    private static Ship Player()
    {
        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Ship.All[i] != null && Ship.All[i].IsPlayerControlled)
                return Ship.All[i];
        }

        return null;
    }
}
