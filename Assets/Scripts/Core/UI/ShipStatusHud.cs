using UnityEngine;
using IMGUI;

/// <summary>
/// 내 배 지금 상태 — 연료, 기압, 속도. 새로 재는 것이 없다: 탱크 잔량은 <see cref="Ship.RemainingImpulse"/>,
/// 기압은 <see cref="Room.air"/>/<see cref="Room.Volume"/>, 속도는 <see cref="Ship.velocity"/>가
/// 매 틱 이미 들고 있던 값이다. 여기서는 읽어서 화면에 놓기만 한다.
///
/// RoomView와 같은 자리, 같은 이유로 자기를 심는다 - 씬에 손으로 붙일 것이 없어야 런타임에
/// 소환되는 함선에도 그대로 붙는다.
/// </summary>
public sealed class ShipStatusHud : MonoBehaviour
{
    // RoomView.Hold/Vent와 같은 색조를 쓴다 - "괜찮다"와 "위험하다"가 화면 전체에서
    // 같은 색이어야 플레이어가 새로 배우지 않는다. 저쪽은 반투명 오버레이 틴트라
    // 텍스트로는 안 보여서, 색상만 그대로 가져오고 알파는 불투명으로 새로 둔다.
    private static readonly Color NormalColor = new(0.78f, 0.90f, 1.00f, 1f);
    private static readonly Color WarnColor = new(1.00f, 0.80f, 0.30f, 1f);
    private static readonly Color CriticalColor = new(1.00f, 0.45f, 0.15f, 1f);   // RoomView.Vent와 같은 색
    private static readonly Color PanelBg = new(0.05f, 0.06f, 0.09f, 0.55f);

    private const float PanelWidth = 240f;
    private const float LineHeight = 24f;
    private const float LineGap = 4f;
    private const float Margin = 12f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Ship Status HUD");
        DontDestroyOnLoad(go);
        go.AddComponent<ShipStatusHud>();
    }

    private void Update()
    {
        ImGui.Begin();

        Ship ship = Player();

        if (ship == null)
            return;

        float x = Screen.width - PanelWidth - Margin;
        float y = Margin;

        DrawLine("hud_fuel", new Rect(x, y, PanelWidth, LineHeight), FuelText(ship, out Color fuelColor), fuelColor);
        y += LineHeight + LineGap;

        DrawLine("hud_pressure", new Rect(x, y, PanelWidth, LineHeight), PressureText(ship, out Color pressureColor), pressureColor);
        y += LineHeight + LineGap;

        DrawLine("hud_velocity", new Rect(x, y, PanelWidth, LineHeight), VelocityText(ship), NormalColor);
    }

    /// <summary>
    /// 탱크가 하나도 없으면 Drive()가 무제한으로 도는 배다(배치를 아직 안 끝낸 배) -
    /// "연료 무제한"이라고 정직하게 말한다. 0%로 보이면 죽어가는 것으로 오해한다.
    /// </summary>
    private static string FuelText(Ship ship, out Color color)
    {
        if (ship.shipTanks.Count == 0)
        {
            color = NormalColor;
            return "연료  무제한";
        }

        float remaining = ship.RemainingImpulse();
        float designMax = 0f;

        foreach (Tank tank in ship.shipTanks)
            designMax += tank.impulse;

        float fraction = designMax > 0f ? remaining / designMax : 0f;
        float deltaV = ship.AvailableDeltaV();

        color = ColorFor(fraction);
        return $"연료  {deltaV:0} m/s  ({fraction * 100f:0}%)";
    }

    /// <summary>
    /// 방별 기압(0~1)의 단순 평균이 아니라 부피 가중 평균이다 - 1칸짜리 복도와 40칸짜리
    /// 격납고가 같은 표를 갖던 게 실제 상황과 안 맞았다.
    /// </summary>
    private static string PressureText(Ship ship, out Color color)
    {
        float totalAir = 0f;
        float totalVolume = 0f;

        for (int i = 0; i < ship.rooms.Count; i++)
        {
            Room room = ship.rooms[i];
            totalAir += room.air;
            totalVolume += room.Volume;
        }

        float fraction = totalVolume > 0f ? totalAir / totalVolume : 0f;

        color = ColorFor(fraction);
        return $"기압  {fraction * 100f:0}%";
    }

    private static string VelocityText(Ship ship)
    {
        Vector2 v = ship.velocity;
        float speed = v.magnitude;

        // Gun.Slew·ShipAi.Turn과 같은 -90도 보정 - 0도가 오른쪽이 아니라 위(뱃머리 기준)다.
        float heading = speed > 0.05f
            ? (Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg - 90f + 360f) % 360f
            : 0f;

        return $"속도  {speed:0} m/s  {heading:000}°";
    }

    private static Color ColorFor(float fraction)
    {
        if (fraction < 0.15f) return CriticalColor;
        if (fraction < 0.4f) return WarnColor;
        return NormalColor;
    }

    private static void DrawLine(string id, Rect rect, string text, Color textColor)
    {
        GUIBoxLabel line = ImGui.BoxLabel(id, rect, text,
            GUIStyleMaker.Box(background: PanelBg, text: textColor, fontSize: 15)
                .Padding(8, 0)
                .Align(TextAnchor.MiddleLeft));

        line.Layer = 50;
    }

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
