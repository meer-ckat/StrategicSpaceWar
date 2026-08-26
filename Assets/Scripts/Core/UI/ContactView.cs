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
    private static readonly Color FriendlyColor = new(0.78f, 0.90f, 1.00f, 1f);  // ShipStatusHud.NormalColor
    private static readonly Color PanelBg = new(0.05f, 0.06f, 0.09f, 0.55f);

    private const float MarkerWidth = 108f;
    private const float MarkerHeight = 22f;

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
    /// 접촉별 위젯 id. 문자열 보간을 매 프레임 돌리면 그것도 프레임마다 나는 쓰레기다.
    /// 한 런에서 보는 배가 수십 척이라 그냥 들고 있는다.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<int, string> Ids = new();

    private static GUIStyle StyleFor(Color text, ref GUIStyle cache) =>
        cache ??= GUIStyleMaker.Box(background: PanelBg, text: text, fontSize: 13)
            .Padding(6, 0)
            .Align(TextAnchor.MiddleCenter);

    private static string IdFor(int instanceId)
    {
        if (!Ids.TryGetValue(instanceId, out string id))
            Ids[instanceId] = id = "contact_" + instanceId;

        return id;
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

        // z < 0이면 카메라 뒤다. 직교 투영이라 전투 중에는 안 생기지만, 컷신이 카메라를
        // 옮기는 동안 생길 수 있고 그때 x/y가 뒤집혀 들어온다.
        bool onScreen = screen.z > 0f
            && screen.x >= 0f && screen.x <= Screen.width
            && screen.y >= 0f && screen.y <= Screen.height;

        if (onScreen)
            return;   // 실물이 이미 보인다

        // GUI 좌표는 y가 아래로 증가한다. 스크린 좌표는 위로 증가하므로 여기서 뒤집는다.
        Vector2 point = new(screen.x, Screen.height - screen.y);

        if (screen.z < 0f)
            point = new Vector2(Screen.width, Screen.height) - point;

        float x = Mathf.Clamp(point.x - MarkerWidth * 0.5f,
            EdgeInset, Screen.width - MarkerWidth - EdgeInset);

        float y = Mathf.Clamp(point.y - MarkerHeight * 0.5f,
            EdgeInset, Screen.height - MarkerHeight - EdgeInset);

        bool identified = distance <= IdentifyRange;

        string text = identified
            ? $"{Name(other)}  {distance:0} m"
            : $"접촉  {distance:0} m";

        GUIStyle style = !identified ? StyleFor(UnknownColor, ref _unknownStyle)
            : other.team == Ship.Team.Enemy ? StyleFor(HostileColor, ref _hostileStyle)
            : StyleFor(FriendlyColor, ref _friendlyStyle);

        // id는 GetInstanceID다. Thing.stableId는 def의 배치 인덱스라 배끼리 겹친다 -
        // 그걸 쓰면 두 접촉이 같은 위젯을 두고 매 프레임 싸운다.
        GUIBoxLabel marker = ImGui.BoxLabel(
            IdFor(other.GetInstanceID()),
            new Rect(x, y, MarkerWidth, MarkerHeight),
            text,
            style);

        marker.Layer = MarkerLayer;
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
