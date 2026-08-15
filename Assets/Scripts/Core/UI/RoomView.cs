using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 방 기압을 화면에 그린다. 새 시뮬레이션이 아니다 - <see cref="Ship.rooms"/>는 처음부터 매 틱
/// 돌고 있었고, 볼 수 있는 곳이 에디터 기즈모(그것도 선택한 배만)뿐이었다.
///
/// 이 게임에서 배가 죽는 이유는 기압이다. 함선 HP가 없으니 "얼마나 남았나"를 읽을 곳이
/// 여기밖에 없고, 안 보이면 플레이어는 자기가 이기고 있는지도 모른다.
///
/// **새는 중인 방을 따로 칠한다.** 기압만 그리면 "이미 빈 방"과 "지금 터진 방"이 같은 색이다.
/// 드라마는 후자에 있다 - 지금 막 뚫려서 공기가 빠져나가는 칸.
///
/// SpallTrails와 같은 방식으로 자기를 심는다. 씬에 손으로 붙일 것이 없어야 런타임에
/// 소환되는 함선에도 그대로 붙는다.
/// </summary>
public sealed class RoomView : MonoBehaviour
{
    /// <summary>기밀 상태. 배를 가리면 안 되니 옅게.</summary>
    private static readonly Color Hold = new(0.30f, 0.75f, 1.00f, 0.30f);

    /// <summary>새는 중. 눈에 띄어야 한다.</summary>
    private static readonly Color Vent = new(1.00f, 0.45f, 0.15f, 0.70f);

    /// <summary>초당 이만큼 기압이 빠지면 완전히 Vent 색. 0.2면 5초에 한 방이 빈다.</summary>
    private const float FullVentRate = 0.2f;

    /// <summary>판 위에 그린다. 판은 기본값 0이다.</summary>
    private const int SortingOrder = 100;

    private sealed class Overlay
    {
        public SpriteRenderer renderer;
        public Texture2D texture;
        public Color32[] pixels;
        public ShipGrid.Map map;        // 참조가 바뀌면 배가 다시 지어진 것이다
        public float[] lastPressure;    // 방 번호별. 새는 속도를 여기서 뽑는다
    }

    private readonly Dictionary<Ship, Overlay> _overlays = new();
    private bool _visible = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("Room View");
        DontDestroyOnLoad(go);
        go.AddComponent<RoomView>();
    }

    private void LateUpdate()
    {
        if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            _visible = !_visible;

        // 죽은 배의 오버레이를 걷어낸다. 배는 파괴되지만 사전 키는 남는다.
        _stale.Clear();

        foreach (KeyValuePair<Ship, Overlay> pair in _overlays)
        {
            if (pair.Key == null)
                _stale.Add(pair.Key);
        }

        foreach (Ship dead in _stale)
        {
            if (_overlays.TryGetValue(dead, out Overlay o) && o.renderer != null)
                Destroy(o.renderer.gameObject);

            _overlays.Remove(dead);
        }

        for (int i = 0; i < Ship.All.Count; i++)
            Draw(Ship.All[i]);
    }

    private readonly List<Ship> _stale = new();

    /// <summary>
    /// 감압 분출. 뚫린 판에서 바깥으로 짧은 선을 뿜는다 - 파편 궤적과 같은 링버퍼를 쓰므로
    /// 새 렌더러도 파티클 시스템도 없다.
    ///
    /// 어디서 뿜는지는 이미 알고 있다: 방의 벽 목록 중 AnyBreached인 판이 그 방의 구멍이다.
    /// 방향은 그 판에서 방 중심을 뺀 것 - 안에서 밖으로.
    /// </summary>
    /// <summary>
    /// 분출음을 다시 트리거하기까지의 간격(초). SoundManager의 프레임당 중복 제거는 같은
    /// 프레임만 막는다 - 파공마다 프레임을 어긋나게 뿜으니 어느 하나는 늘 걸려서, 클립이
    /// 초당 60번 처음부터 다시 재생됐다. 그게 "공기 새는 소리가 시끄럽다"의 정체다.
    /// </summary>
    private const float BlowInterval = 0.45f;

    private float _nextBlow;

    private void Blow(Ship ship, Room room, float venting)
    {
        Vector2 centre = Vector2.zero;
        int cells = 0;

        foreach (Vector2Int cell in room.cells)
        {
            centre += (Vector2)ship.transform.TransformPoint(ship.Map.ToLocal(cell.x, cell.y));
            cells++;
        }

        if (cells == 0)
            return;

        centre /= cells;

        float loudest = 0f;
        Vector2 at = centre;

        for (int i = 0; i < room.walls.Count; i++)
        {
            Armor wall = room.walls[i];

            if (wall == null || !wall.AnyBreached)
                continue;

            // 파공마다 매 프레임 뿜으면 궤적 링버퍼를 파편과 나눠 쓰다가 잠식한다.
            // 3프레임에 한 번, 판마다 어긋나게 - 고르게 뿜는 것보다 펄럭이는 게 가스답다.
            if ((Time.frameCount + i) % 3 != 0)
                continue;

            Vector2 from = wall.transform.position;
            Vector2 outward = (from - centre).normalized;

            if (outward.sqrMagnitude < 1e-4f)
                continue;

            // 세기만큼 길게. 다 빠진 방은 더 이상 뿜지 않으므로 저절로 잦아든다.
            float length = Mathf.Lerp(0.6f, 3.5f, venting);


            SpallTrails.Add(from, from + outward * length, SpallTrails.Kind.Miss);

            loudest = Mathf.Max(loudest, venting);
            at = from;
        }

        // 방 하나가 한 번. 파공이 열 개여도 쉬익 소리는 한 겹이고, 간격을 두고 되풀이된다.
        if (loudest > 0f && Time.time >= _nextBlow)
        {
            _nextBlow = Time.time + BlowInterval;
            SoundManager.AudioShot("Blow", at, loudest);
        }
    }

    private void Draw(Ship ship)
    {
        if (ship == null || ship.Map == null || ship.rooms == null || ship.rooms.Count == 0)
            return;

        if (!_overlays.TryGetValue(ship, out Overlay overlay))
            _overlays[ship] = overlay = new Overlay();

        // 배가 갈라지면 BuildRooms가 새 Map을 만든다. 참조 비교 하나로 알아챈다 -
        // Ship 쪽에 알림 코드를 넣지 않아도 되고, 넣으면 언젠가 부르는 걸 잊는다.
        if (overlay.map != ship.Map)
            Rebuild(ship, overlay);

        // 오버레이는 껐다 켰다 하는 디버그 표시지만, 감압 분출은 월드에서 실제로 일어나는
        // 일의 그림이다. Tab으로 끄는 것은 앞엣것뿐이라 Paint는 항상 돈다.
        overlay.renderer.enabled = _visible;

        Paint(ship, overlay, _visible);
    }

    private void Rebuild(Ship ship, Overlay overlay)
    {
        ShipGrid.Map map = ship.Map;

        if (overlay.renderer != null)
            Destroy(overlay.renderer.gameObject);

        var go = new GameObject("rooms");
        go.transform.SetParent(ship.transform, worldPositionStays: false);

        // 칸 (0,0)의 자리에 놓고, 피벗을 그 칸의 픽셀 중심으로 잡는다. ppu가 1이라
        // 픽셀 하나가 1 m고, 그러면 픽셀 (c, h-1-r)이 정확히 칸 (c, r) 위에 온다.
        go.transform.localPosition = map.ToLocal(0, 0);

        overlay.texture = new Texture2D(map.width, map.height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        overlay.pixels = new Color32[map.width * map.height];

        var sprite = Sprite.Create(
            overlay.texture,
            new Rect(0f, 0f, map.width, map.height),
            new Vector2(0.5f / map.width, (map.height - 0.5f) / map.height),
            pixelsPerUnit: 1f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = sprite;
        overlay.renderer.sortingOrder = SortingOrder;

        overlay.map = map;
        overlay.lastPressure = new float[ship.rooms.Count];

        for (int i = 0; i < ship.rooms.Count; i++)
            overlay.lastPressure[i] = ship.rooms[i].Pressure;
    }

    private void Paint(Ship ship, Overlay overlay, bool draw)
    {
        ShipGrid.Map map = overlay.map;
        float dt = Mathf.Max(1e-4f, Time.deltaTime);

        System.Array.Clear(overlay.pixels, 0, overlay.pixels.Length);

        for (int i = 0; i < ship.rooms.Count && i < overlay.lastPressure.Length; i++)
        {
            Room room = ship.rooms[i];
            float pressure = room.Pressure;

            // 떨어지는 쪽만 본다. 문으로 다시 차오르는 방을 빨갛게 칠할 이유는 없다.
            float venting = Mathf.Clamp01((overlay.lastPressure[i] - pressure) / dt / FullVentRate);
            overlay.lastPressure[i] = pressure;

            Color color = Color.Lerp(Hold, Vent, venting);

            // 새는 방은 어딘가로 뿜고 있다. 그 어딘가가 파공이다 - 방의 벽 중 뚫린 판을
            // 찾아 방 반대쪽으로 분출시킨다. 방향은 판에서 방 중심을 뺀 것, 즉 바깥이다.
            if (venting > 0.15f)
                Blow(ship, room, venting);

            // 기압이 낮을수록 옅어진다. 완전히 빈 방은 아무것도 안 그린다 - 거기는
            // 이미 우주고, 볼 것이 없다. 단 새는 중이면 끝까지 보인다.
            color.a *= Mathf.Max(pressure, venting);

            var packed = (Color32)color;

            foreach (Vector2Int cell in room.cells)
            {
                if (!map.Inside(cell))
                    continue;

                // 텍스처는 아래에서 위로, 맵은 위에서 아래로 센다.
                overlay.pixels[(map.height - 1 - cell.y) * map.width + cell.x] = packed;
            }
        }

        if (draw)
        {
            overlay.texture.SetPixels32(overlay.pixels);
            overlay.texture.Apply(false);
        }
    }
}
