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

    /// <summary>
    /// 다 빠진 방. **안 그리면 안 된다.**
    ///
    /// 원래는 기압 0인 방을 투명하게 뒀는데, 그러면 "이미 진공인 구획"과 "오버레이가 아예
    /// 안 잡은 구획"이 화면에서 똑같다. 빈 열을 보고 버그를 의심하게 되는데, 사실은 정보가
    /// 전해지고 있어야 할 자리다 - 저 칸이 진공이라는 것이 플레이어가 알아야 할 결과다.
    /// </summary>
    private static readonly Color Vacuum = new(0.16f, 0.18f, 0.24f, 0.34f);

    /// <summary>
    /// 격벽·외판. 방이 아니라서 예전엔 아무것도 안 그렸는데, 그러면 화면에서 "여기는 벽"과
    /// "여기는 데이터가 없음"이 똑같아진다. 진공을 안 그리던 것과 정확히 같은 병이다.
    ///
    /// 그려 넣으면 오버레이가 구멍 뚫린 얼룩이 아니라 **갑판 도면**이 된다. 방 사이의 흰 열이
    /// 격벽이라는 것이 그제야 읽힌다.
    /// </summary>
    private static readonly Color Structure = new(0.62f, 0.68f, 0.80f, 0.22f);

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
        public Vector2 localOffset;     // 격자 한가운데의 선체 기준 자리
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

        // 표시하고 쓸어낸다. 오버레이의 수명을 "이번 프레임에 그렸나" 하나로 정하는 것이
        // 요점이다 - 예전에는 "배가 파괴됐나"만 봤고, 그래서 Draw가 조기 리턴하는 배
        // (방이 하나도 안 남았거나 판이 전멸한 배)의 오버레이가 **켜진 채로 얼어붙었다.**
        // 배는 계속 날아가는데 그림만 제자리에 남으니 offset처럼 보였고, Tab 토글도
        // Draw 안에 있어서 그 유령은 꺼지지도 않았다.
        //
        // 이 규칙 하나가 세 경우를 전부 덮는다: 파괴된 배(All에서 빠짐), 비활성 배
        // (OnDisable이 All에서 뺌), 그릴 게 없는 배(Draw가 리턴해서 표시를 안 남김).
        _drawn.Clear();

        for (int i = 0; i < Ship.All.Count; i++)
        {
            if (Draw(Ship.All[i]))
                _drawn.Add(Ship.All[i]);
        }

        _stale.Clear();

        foreach (KeyValuePair<Ship, Overlay> pair in _overlays)
        {
            if (!_drawn.Contains(pair.Key))
                _stale.Add(pair.Key);
        }

        foreach (Ship gone in _stale)
        {
            if (_overlays.TryGetValue(gone, out Overlay o))
                Discard(o);

            _overlays.Remove(gone);
        }
    }

    private readonly List<Ship> _stale = new();
    private readonly HashSet<Ship> _drawn = new();

    /// <summary>
    /// 오버레이 하나를 통째로 버린다. **텍스처와 스프라이트는 GameObject의 소유가 아니다** -
    /// `new Texture2D`와 `Sprite.Create`로 만든 것이라 렌더러를 지워도 같이 안 죽는다.
    /// 파단마다 오버레이를 다시 굽는 구조라, 안 지우면 한 판을 치를 때마다 조금씩 샌다.
    /// </summary>
    private static void Discard(Overlay o)
    {
        if (o == null)
            return;

        if (o.renderer != null)
        {
            if (o.renderer.sprite != null)
                Destroy(o.renderer.sprite);

            Destroy(o.renderer.gameObject);
        }

        if (o.texture != null)
            Destroy(o.texture);

        o.renderer = null;
        o.texture = null;
        o.pixels = null;
    }

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

    /// <summary>
    /// 그렸으면 true. 이 반환값이 곧 오버레이의 수명이다 - false를 돌려주면 호출자가
    /// 그 배의 오버레이를 파괴한다. 조기 리턴은 "못 그린다"가 아니라 "이 배에는 오버레이가
    /// 없다"라는 뜻이고, 조건이 하나 더 붙어도 청소가 저절로 따라온다.
    /// </summary>
    private bool Draw(Ship ship)
    {
        if (ship == null || ship.Map == null || ship.rooms == null || ship.rooms.Count == 0)
            return false;

        if (!_overlays.TryGetValue(ship, out Overlay overlay))
            _overlays[ship] = overlay = new Overlay();

        // 배가 갈라지면 BuildRooms가 새 Map을 만든다. 참조 비교 하나로 알아챈다 -
        // Ship 쪽에 알림 코드를 넣지 않아도 되고, 넣으면 언젠가 부르는 걸 잊는다.
        if (overlay.map != ship.Map)
            Rebuild(ship, overlay);

        // 오버레이는 껐다 켰다 하는 디버그 표시지만, 감압 분출은 월드에서 실제로 일어나는
        // 일의 그림이다. Tab으로 끄는 것은 앞엣것뿐이라 Paint는 항상 돈다.
        overlay.renderer.enabled = _visible;

        // 부모가 아니라 따라간다. 배가 돌면 오버레이도 같이 돈다.
        overlay.renderer.transform.SetPositionAndRotation(
            ship.transform.TransformPoint(overlay.localOffset), ship.transform.rotation);

        // **scale까지 물려받아야 한다.** 반대쪽에서 오는 배는 localScale.x가 -1이다.
        // TransformPoint는 scale을 적용하므로 오버레이의 *자리*는 이미 뒤집혀 있는데,
        // 렌더러는 함선의 자식이 아니라(직속 자식은 판만) 자기 scale이 1이라 그림만
        // 정방향으로 남는다 - 선체는 왼쪽을 보는데 방 도면은 오른쪽을 보는 그림이 된다.
        //
        // localOffset은 건드리지 않는다. 그건 로컬 값이고 TransformPoint가 이미 반전을
        // 먹였다. 여기서 또 손대면 두 번 뒤집힌다.
        overlay.renderer.transform.localScale = ship.transform.lossyScale;

        Paint(ship, overlay, _visible);
        return true;
    }

    private void Rebuild(Ship ship, Overlay overlay)
    {
        ShipGrid.Map map = ship.Map;

        // 텍스처·스프라이트까지 같이 버린다. 여기가 파단마다 도는 자리라, 렌더러만 지우면
        // 배가 갈라질 때마다 텍스처 한 장씩 쌓인다.
        Discard(overlay);

        // **함선의 자식이 아니다.** 선체 직속 자식은 판만이어야 한다 - 격자를 읽는 코드가
        // 직속 자식을 훑기 때문에, 그림 하나가 끼어들면 칸을 차지해서 진짜 판을 밀어낸다.
        // 대신 매 프레임 함선을 따라간다.
        var go = new GameObject("rooms");
        go.transform.SetParent(transform, worldPositionStays: false);

        overlay.texture = new Texture2D(map.width, map.height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        overlay.pixels = new Color32[map.width * map.height];

        // 피벗은 한가운데. 모서리에 두면 부호나 축을 하나 틀려도 "조금 어긋난 그림"이라
        // 눈에 안 띄는데, 중심이면 대칭으로 틀어져서 바로 보인다. 실제로 처음엔 모서리
        // 피벗이었고 오버레이가 배 옆에 통째로 떠 있었다.
        var sprite = Sprite.Create(
            overlay.texture,
            new Rect(0f, 0f, map.width, map.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 1f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = sprite;
        overlay.renderer.sortingOrder = SortingOrder;

        overlay.map = map;

        // 격자 한가운데의 선체 기준 좌표. 칸 (0,0)과 칸 (w-1,h-1)의 중점이고, ppu가 1이라
        // 그것이 곧 스프라이트의 중심이다.
        overlay.localOffset = map.ToLocal(0, 0)
            + new Vector2((map.width - 1) * 0.5f, -(map.height - 1) * 0.5f);
        overlay.lastPressure = new float[ship.rooms.Count];

        for (int i = 0; i < ship.rooms.Count; i++)
            overlay.lastPressure[i] = ship.rooms[i].Pressure;
    }

    private void Paint(Ship ship, Overlay overlay, bool draw)
    {
        ShipGrid.Map map = overlay.map;
        float dt = Mathf.Max(1e-4f, Time.deltaTime);

        System.Array.Clear(overlay.pixels, 0, overlay.pixels.Length);

        // 구조부터 깔고 그 위에 방을 얹는다. 판이 차지한 칸은 방이 될 수 없으므로 덮어쓸
        // 일이 없고, 순서를 뒤집어도 결과가 같다.
        var structure = (Color32)Structure;

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (ShipGrid.Solid(map.cells[col, row]))
                overlay.pixels[(map.height - 1 - row) * map.width + col] = structure;
        }

        for (int i = 0; i < ship.rooms.Count && i < overlay.lastPressure.Length; i++)
        {
            Room room = ship.rooms[i];
            float pressure = room.Pressure;

            // 떨어지는 쪽만 본다. 문으로 다시 차오르는 방을 빨갛게 칠할 이유는 없다.
            float venting = Mathf.Clamp01((overlay.lastPressure[i] - pressure) / dt / FullVentRate);
            overlay.lastPressure[i] = pressure;

            // 기압이 색을 정하고, 새는 중이면 그 위에 주황이 덮인다. 알파는 안 건드린다 -
            // 진공도 "진공이다"라는 정보라 끝까지 보여야 한다.
            Color color = Color.Lerp(Color.Lerp(Vacuum, Hold, pressure), Vent, venting);

            // 새는 방은 어딘가로 뿜고 있다. 그 어딘가가 파공이다 - 방의 벽 중 뚫린 판을
            // 찾아 방 반대쪽으로 분출시킨다. 방향은 판에서 방 중심을 뺀 것, 즉 바깥이다.
            if (venting > 0.15f)
                Blow(ship, room, venting);

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
