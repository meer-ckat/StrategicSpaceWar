using System.Collections.Generic;
using TMPro;
using UnityEngine;
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
public sealed class BackPlateView : MonoBehaviour
{
    /// <summary>기본 BackplateColor fallBack</summary>
    
    private static readonly Color Structure = new(1f, 1f, 1f, 1f);
    private float Darken = 0.5f;
    private const int SortingOrder = -10;

    private sealed class Overlay
    {
        public SpriteRenderer renderer;
        public Texture2D texture;
        public Color32[] pixels;
        public Color32[] art;           // 칸별 어두운 색. 생성 때 한 번만 굽는다 - 배 그림은 안 변한다
        public ShipGrid.Map _designMap; //immutable;
        public Vector2 localOffset;     // 텍스처 한가운데의 선체 기준 자리
        public int currentRearCount;

        // 텍스처는 설계도 전체가 아니라 이 조각의 후면 bbox만 하다. 후면은 줄기만 하니
        // 생성 때 bbox면 영원히 충분하고, 판 한 장짜리 잔해가 2,000픽셀 텍스처를 받는
        // 일이 없어진다 - 갈리는 중에는 그런 잔해가 매 틱 태어난다.
        public int minCol;
        public int minRow;
        public int texW;
        public int texH;
    }

    private readonly Dictionary<HullStructure, Overlay> _overlays = new();
    const bool _visible = true;

    /// <summary>
    /// 설계도별 어두운 색. **조각들이 공유한다** - 본체와 잔해가 같은 DesignMap 참조를
    /// 들고 다니므로(불변), 파단으로 조각 수십 개가 생겨도 배 그림 샘플링은 설계도당
    /// 한 번이다.
    ///
    /// 청소는 "안 쓰는 설계도 제거"다. "오버레이가 전멸하면 비운다"로 하면 안 된다 -
    /// 플레이어 함선이 구역 사이에도 살아남아 오버레이가 0이 되는 순간이 런 도중에
    /// 안 오고, 적함은 구역마다 새 Map 인스턴스로 소환되므로 죽은 설계도가 런 끝까지
    /// 쌓인다.
    /// </summary>
    private static readonly Dictionary<ShipGrid.Map, Color32[]> _artCache = new();
    private static readonly HashSet<ShipGrid.Map> _mapsInUse = new();
    private static readonly List<ShipGrid.Map> _staleMaps = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var go = new GameObject("BackPlateView");
        DontDestroyOnLoad(go);
        go.AddComponent<BackPlateView>();
    }

    private void LateUpdate()
    {
        _drawn.Clear();

        for (int i = 0; i < HullStructure.All.Count; i++)
        {
            var ship = HullStructure.All[i];

            if (Draw(ship))
                _drawn.Add(ship);
        }

        _stale.Clear();

        foreach (KeyValuePair<HullStructure, Overlay> pair in _overlays)
        {
            if (!_drawn.Contains(pair.Key))
                _stale.Add(pair.Key);
        }

        foreach (HullStructure gone in _stale)
        {
            if (_overlays.TryGetValue(gone, out Overlay o))
                Discard(o);

            _overlays.Remove(gone);
        }

        // 오버레이가 하나라도 걷힌 프레임에만 돈다. 지금 쓰이는 설계도만 남긴다.
        if (_stale.Count > 0 && _artCache.Count > 0)
        {
            _mapsInUse.Clear();

            foreach (Overlay o in _overlays.Values)
            {
                if (o._designMap != null)
                    _mapsInUse.Add(o._designMap);
            }

            _staleMaps.Clear();

            foreach (ShipGrid.Map key in _artCache.Keys)
            {
                if (!_mapsInUse.Contains(key))
                    _staleMaps.Add(key);
            }

            foreach (ShipGrid.Map map in _staleMaps)
                _artCache.Remove(map);
        }
    }

    private readonly List<HullStructure> _stale = new();
    private readonly HashSet<HullStructure> _drawn = new();

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
        o.art = null;
    }

    /// <summary>
    /// 그렸으면 true. 이 반환값이 곧 오버레이의 수명이다 - false를 돌려주면 호출자가
    /// 그 배의 오버레이를 파괴한다. 조기 리턴은 "못 그린다"가 아니라 "이 배에는 오버레이가
    /// 없다"라는 뜻이고, 조건이 하나 더 붙어도 청소가 저절로 따라온다.
    /// </summary>
    private const int MinRearForOverlay = 6;

    // Ship 여부는 몸이 태어날 때 정해지고 안 변한다. 소형 잔해 수백 개에 매 프레임
    // TryGetComponent(네이티브)를 내지 않으려는 캐시다.
    private static readonly Dictionary<HullStructure, bool> _isShip = new();

    private static bool IsShip(HullStructure structure)
    {
        if (!_isShip.TryGetValue(structure, out bool ship))
        {
            // ponytail: 죽은 몸의 키가 남는다. 넘치면 통째로 비운다 - 재판정이 싸서.
            if (_isShip.Count > 1024)
                _isShip.Clear();

            _isShip[structure] = ship = structure.TryGetComponent(out Ship _);
        }

        return ship;
    }

    private bool Draw(HullStructure structure)
    {
        if (structure.DesignMap == null || structure.Rear.Count == 0)
            return false;

        // LOD: 조각 잔해의 오버레이는 화면에서 어두운 픽셀 몇 개인데, 값은 오브젝트 생성에
        // 매 프레임 추적이다. 방이 없는 작은 조각은 아예 안 만든다 - 본체(Ship)는 아무리
        // 쪼그라들어도 남긴다, 플레이어가 읽는 기압이 거기 있다. false면 기존 수명 규칙이
        // 이미 있던 오버레이도 치워 준다.
        if (structure.Rear.Count < MinRearForOverlay && !IsShip(structure))
            return false;

        if (!_overlays.TryGetValue(structure, out Overlay overlay))
            _overlays[structure] = overlay = new Overlay();

        // 맵이 바뀌었다 = 파단으로 새 덩어리다. 텍스처 크기부터 다르니 통째로 다시 만든다.
        // 칸 수만 변했다 = 후면이 뚫린 것뿐이다. 그림도 크기도 그대로라 칠하기만 다시 한다 -
        // 연사로 뒷벽을 두들길 때 GameObject·텍스처·스프라이트 churn이 없는 이유.
        if (overlay._designMap != structure.DesignMap)
        {
            overlay.currentRearCount = structure.Rear.Count;
            Rebuild(structure, overlay);
        }
        else if (structure.Rear.Count != overlay.currentRearCount)
        {
            overlay.currentRearCount = structure.Rear.Count;
            Paint(overlay, structure);
        }

        overlay.renderer.enabled = _visible;

        // scale은 여기 없다 - 몸의 scale은 태어날 때 정해지고 안 변해서(잔해는 MakeDebris가
        // 본체 lossyScale로 고정, 함선은 소환 시점의 반전뿐) Rebuild에서 한 번만 쓴다.
        overlay.renderer.transform.SetPositionAndRotation(
            structure.transform.TransformPoint(overlay.localOffset),
            structure.transform.rotation);

        return true;
    }

    private void Rebuild(HullStructure structure, Overlay overlay)
    {
        ShipGrid.Map DM = structure.DesignMap; //다이렉트 메시지 아님

        // 텍스처·스프라이트까지 같이 버린다. 여기가 파단마다 도는 자리라, 렌더러만 지우면
        // 배가 갈라질 때마다 텍스처 한 장씩 쌓인다.
        Discard(overlay);
        // **함선의 자식이 아니다.** 선체 직속 자식은 판만이어야 한다 - 격자를 읽는 코드가
        // 직속 자식을 훑기 때문에, 그림 하나가 끼어들면 칸을 차지해서 진짜 판을 밀어낸다.
        // 대신 매 프레임 함선을 따라간다.
        var go = new GameObject("rooms");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localScale = structure.transform.lossyScale;

        // 이 조각이 실제로 든 후면 칸의 bbox. 본체는 사실상 설계도 전체고, 판 한 장짜리
        // 잔해는 1x1이다. Draw의 가드 덕에 여기서 Rear는 비어 있지 않다.
        int minCol = int.MaxValue, minRow = int.MaxValue;
        int maxCol = int.MinValue, maxRow = int.MinValue;

        foreach (Vector2Int cell in structure.Rear)
        {
            minCol = Mathf.Min(minCol, cell.x);
            maxCol = Mathf.Max(maxCol, cell.x);
            minRow = Mathf.Min(minRow, cell.y);
            maxRow = Mathf.Max(maxRow, cell.y);
        }

        overlay.minCol = minCol;
        overlay.minRow = minRow;
        overlay.texW = maxCol - minCol + 1;
        overlay.texH = maxRow - minRow + 1;

        overlay.texture = new Texture2D(overlay.texW, overlay.texH, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        overlay.pixels = new Color32[overlay.texW * overlay.texH];

        // 피벗은 한가운데. 모서리에 두면 부호나 축을 하나 틀려도 "조금 어긋난 그림"이라
        // 눈에 안 띄는데, 중심이면 대칭으로 틀어져서 바로 보인다. 실제로 처음엔 모서리
        // 피벗이었고 오버레이가 배 옆에 통째로 떠 있었다.
        var sprite = Sprite.Create(
            overlay.texture,
            new Rect(0f, 0f, overlay.texW, overlay.texH),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 1f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = sprite;
        overlay.renderer.sortingOrder = SortingOrder;

        overlay._designMap = DM;

        // bbox 한가운데의 선체 기준 좌표. 칸 (minCol,minRow)와 (maxCol,maxRow)의 중점이고,
        // ppu가 1이라 그것이 곧 스프라이트의 중심이다.
        overlay.localOffset = DM.ToLocal(minCol, minRow)
            + new Vector2((overlay.texW - 1) * 0.5f, -(overlay.texH - 1) * 0.5f);

        overlay.art = ArtFor(DM, structure.ShipHullPng);
        Paint(overlay, structure);
    }

    /// <summary>배 그림 샘플링은 설계도당 한 번뿐이다. 칠하기는 이 캐시만 읽는다.</summary>
    private Color32[] ArtFor(ShipGrid.Map map, Texture2D structureTexture)
    {
        if (_artCache.TryGetValue(map, out Color32[] art))
            return art;

        art = new Color32[map.width * map.height];
        float k = 1f - Darken;

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            Color color = structureTexture != null
                ? structureTexture.GetPixelBilinear(
                    (col + 0.5f) / map.width, 1f - (row + 0.5f) / map.height)
                : Structure;

            art[(map.height - 1 - row) * map.width + col] =
                new Color(color.r * k, color.g * k, color.b * k, color.a);
        }

        _artCache[map] = art;
        return art;
    }

    private static void Paint(Overlay overlay, HullStructure structure)
    {
        ShipGrid.Map map = overlay._designMap;

        System.Array.Clear(overlay.pixels, 0, overlay.pixels.Length);

        // 전 칸을 돌지 않는다. 잔해는 칸 세 개짜리일 수 있는데 설계도는 수천 칸이다 -
        // 이 조각이 실제로 든 후면 칸만, bbox 텍스처의 자리에 찍는다. art는 설계도 전체
        // 좌표라 인덱스가 서로 다르다.
        foreach (Vector2Int cell in structure.Rear)
        {
            int p = (overlay.texH - 1 - (cell.y - overlay.minRow)) * overlay.texW
                  + (cell.x - overlay.minCol);
            overlay.pixels[p] = overlay.art[(map.height - 1 - cell.y) * map.width + cell.x];
        }

        overlay.texture.SetPixels32(overlay.pixels);
        overlay.texture.Apply(false);
    }
}
