using UnityEngine;
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
    private const float SensorRange = 1200f;

    /// <summary>
    /// 이 안에 들어와야 편이 밝혀진다. 밖이면 그냥 "접촉"이다.
    ///
    /// 두 값을 같게 두면 미확인 단계가 통째로 사라져서 처음부터 적/아군이 보인다 -
    /// 다가가서 확인할 이유가 없어지는 것이 그 순간이다.
    /// </summary>
    private const float IdentifyRange = 500f;

    // ShipStatusHud와 같은 색조를 쓴다. 화면 전체에서 "괜찮다"와 "위험하다"가 같은 색이어야
    // 플레이어가 새로 배우지 않는다.
    private static readonly Color UnknownColor = new(0.70f, 0.72f, 0.78f, 1f);
    private static readonly Color HostileColor = new(1.00f, 0.45f, 0.15f, 1f);   // ShipStatusHud.CriticalColor
    private static readonly Color FriendlyColor = new(0.78f, 0.90f, 1.00f, 1f);  // ShipStatusHud.HudColor
    private static readonly Color PanelBg = new(0.05f, 0.06f, 0.09f, 0.55f);

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
    /// HUD(50)보다 아래. 둘 다 화면 우상단을 쓸 수 있어서 겹치는데, Layer를 안 정하면
    /// <c>GUIManager.BuildDrawRoots</c>의 List.Sort가 불안정 정렬이라 동률의 순서가 아예
    /// 정의되지 않는다 - 실행마다 달라질 수 있다.
    /// </summary>
    private const int MarkerLayer = 40;

    /// <summary>
    /// 색마다 스타일 하나. **매 프레임 만들면 안 된다** - <see cref="GUIStyleMaker.Box"/>는
    /// 부를 때마다 GUIStyle을 복사하므로 접촉 수만큼 프레임마다 태어난다. 색이 셋뿐이라
    /// 처음 한 번 만들어 두면 그 할당이 아예 존재하지 않는다.
    /// </summary>
    private static GUIStyle _unknownStyle, _hostileStyle, _friendlyStyle;

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
            return tracked;

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
        if (GameManager.GuiHidden)
            return;

        ImGui.Begin();

        Ship player = Player();
        Camera cam = Camera.main;

        if (player == null || cam == null)
            return;

        Vector2 eye = player.transform.position;

        for (int i = 0; i < Ship.All.Count; i++)
        {
            Ship other = Ship.All[i];

            if (other == null || other == player)
                continue;

            Draw(cam, eye, other);
        }
    }

    /// <summary>
    /// 접촉 하나. 범위 밖이거나 화면 안이면 **선언 자체를 안 한다** - 즉시 모드에서는
    /// 그것이 곧 지우는 것이고, 그래서 사라지는 경로에 코드가 없다.
    /// </summary>
    private static void Draw(Camera cam, Vector2 eye, Ship other)
    {
        Vector2 at = other.transform.position;
        float distance = Vector2.Distance(eye, at);

        if (distance > SensorRange)
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

        if (onScreen)
        {
            Status(cam, at, other, distance, identified);
            return;
        }

        // GUI 좌표는 y가 아래로 증가한다. 스크린 좌표는 위로 증가하므로 여기서 뒤집는다.
        Vector2 point = new(logical.x, GUIManager.LogicalHeight - logical.y);

        if (screen.z < 0f)
            point = new Vector2(GUIManager.LogicalWidth, GUIManager.LogicalHeight) - point;

        float x = Mathf.Clamp(point.x - MarkerWidth * 0.5f,
            EdgeInset, GUIManager.LogicalWidth - MarkerWidth - EdgeInset);

        float y = Mathf.Clamp(point.y - MarkerHeight * 0.5f,
            EdgeInset, GUIManager.LogicalHeight - MarkerHeight - EdgeInset);

        string text = identified
            ? $"{Name(other)}  {distance:0} m"
            : $"접촉  {distance:0} m";

        GUIStyle style = !identified ? StyleFor(UnknownColor, ref _unknownStyle)
            : other.team == Ship.Team.Enemy ? StyleFor(HostileColor, ref _hostileStyle)
            : StyleFor(FriendlyColor, ref _friendlyStyle);

        // id는 GetInstanceID다. Thing.stableId는 def의 배치 인덱스라 배끼리 겹친다 -
        // 그걸 쓰면 두 접촉이 같은 위젯을 두고 매 프레임 싸운다.
        GUIBoxLabel marker = ImGui.BoxLabel(
            Track(other).id,
            new Rect(x, y, MarkerWidth, MarkerHeight),
            text,
            style);

        marker.Layer = MarkerLayer;
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
    private static void Status(Camera cam, Vector2 at, Ship other, float distance, bool identified)
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
                $"접촉  {distance:0} m",
                StyleFor(UnknownColor, ref _unknownStyle));

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
