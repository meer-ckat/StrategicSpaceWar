using System.Collections.Generic;
using UnityEngine;
/// <summary>
/// 후면(외피) 그림 - GPU판. 시뮬레이션의 후면 장부(<see cref="HullStructure"/>)를 마스크로
/// 바꿔 RearSkin 셰이더에 넘긴다. 마모·뜯김·발자국을 CPU가 칸당 256픽셀씩 굽던 것이,
/// 이제 후면이 변한 이벤트마다 **칸 해상도 마스크 한 장(칸당 1바이트) 다시 채우기**다.
/// 전체 굽기 폴백도, 칸 단위 더티 추적도 필요 없어졌다 - 마스크 전체 갱신이 그만큼 싸다.
///
/// **비대칭 화면: 소속이 곧 렌더 모드다.** 내 배(IsPlayerControlled) = 외피가 어둡게 뒤에
/// 깔린 내부 단면. 적함·중립·잔해 = 외피가 위로 올라와 얼굴이 되고, 상태를 구멍으로 읽는다.
///
/// 설계도당 정적 마스크 두 장(worthy/경계, 발자국 실루엣)은 첫 오버레이 때 한 번 굽고
/// 조각들이 공유한다 - 발자국은 설계 시점 판 모양이라 불변이다.
///
/// SpallTrails와 같은 방식으로 자기를 심는다.
/// </summary>
public sealed class BackPlateView : MonoBehaviour
{
    // hull png가 이미 어두운 회색조라 0.5를 곱하면 우주 배경에 묻힌다.
    private const float MineDarken = 0.35f;
    private const int MineOrder = -10;

    private const float SkinDarken = 0f;
    private const int SkinOrder = 100;

    /// <summary>발자국 실루엣 마스크의 칸당 픽셀. 셰이더의 grain 양자화(16)와 같은 눈금.</summary>
    private const int RearPPU = 16;

    private const int SharedTexSize = 256;

    private sealed class Overlay
    {
        public bool mine;
        public SpriteRenderer renderer;
        public Sprite sprite;

        /// <summary>칸 해상도 동적 마스크. r = 0이면 후면 없음, 아니면 수명 1..255.</summary>
        public Texture2D cellMask;
        public byte[] cellBytes;

        public ShipGrid.Map _designMap; //immutable;
        public Vector2 localOffset;
        public int currentRearCount;
        public int currentRearVersion;

        // 후면 bbox(칸). 쿼드가 이 크기다.
        public int minCol;
        public int minRow;
        public int texW;
        public int texH;
    }

    /// <summary>설계도당 정적 마스크. 조각들이 공유하고, 안 쓰는 설계도는 청소 때 파괴한다.</summary>
    private sealed class DesignMasks
    {
        public Texture2D staticMask;   // RG: r = RearWorthy, g = 우주와 닿은 경계 칸
        public Texture2D footMask;     // R8, 칸당 16px: 판 발자국 실루엣
    }

    private readonly Dictionary<HullStructure, Overlay> _overlays = new();
    const bool _visible = true;

    private static readonly Dictionary<ShipGrid.Map, DesignMasks> _designCache = new();
    private static readonly HashSet<ShipGrid.Map> _mapsInUse = new();
    private static readonly List<ShipGrid.Map> _staleMaps = new();

    private static Texture2D _sharedWhite;
    private static Material _sharedMaterial;

    private static readonly int HullTexId = Shader.PropertyToID("_HullTex");
    private static readonly int CellMaskId = Shader.PropertyToID("_CellMask");
    private static readonly int StaticMaskId = Shader.PropertyToID("_StaticMask");
    private static readonly int FootMaskId = Shader.PropertyToID("_FootMask");
    private static readonly int GridId = Shader.PropertyToID("_Grid");
    private static readonly int MapId = Shader.PropertyToID("_Map");

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

        // 오버레이가 하나라도 걷힌 프레임에만 돈다. 지금 쓰이는 설계도의 마스크만 남긴다.
        if (_stale.Count > 0 && _designCache.Count > 0)
        {
            _mapsInUse.Clear();

            foreach (Overlay o in _overlays.Values)
            {
                if (o._designMap != null)
                    _mapsInUse.Add(o._designMap);
            }

            _staleMaps.Clear();

            foreach (ShipGrid.Map key in _designCache.Keys)
            {
                if (!_mapsInUse.Contains(key))
                    _staleMaps.Add(key);
            }

            foreach (ShipGrid.Map map in _staleMaps)
            {
                DesignMasks masks = _designCache[map];

                if (masks.staticMask != null)
                    Destroy(masks.staticMask);

                if (masks.footMask != null)
                    Destroy(masks.footMask);

                _designCache.Remove(map);
            }
        }
    }

    private readonly List<HullStructure> _stale = new();
    private readonly HashSet<HullStructure> _drawn = new();

    /// <summary>
    /// 오버레이 하나를 통째로 버린다. 스프라이트·마스크는 GameObject의 소유가 아니라
    /// 직접 지운다. 정적 마스크는 설계도 캐시의 소유라 여기서 안 건드린다.
    /// </summary>
    private static void Discard(Overlay o)
    {
        if (o == null)
            return;

        if (o.renderer != null)
            Destroy(o.renderer.gameObject);

        if (o.sprite != null)
            Destroy(o.sprite);

        if (o.cellMask != null)
            Destroy(o.cellMask);

        o.renderer = null;
        o.sprite = null;
        o.cellMask = null;
        o.cellBytes = null;
    }

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

    /// <summary>그렸으면 true. false를 돌려주면 호출자가 그 배의 오버레이를 파괴한다.</summary>
    private bool Draw(HullStructure structure)
    {
        if (structure.DesignMap == null || structure.Rear.Count == 0)
            return false;

        // LOD: 방이 없는 작은 조각은 오버레이를 아예 안 만든다. 본체(Ship)는 항상 남긴다.
        if (structure.Rear.Count < MinRearForOverlay && !IsShip(structure))
            return false;

        if (!_overlays.TryGetValue(structure, out Overlay overlay))
            _overlays[structure] = overlay = new Overlay();

        if (overlay._designMap != structure.DesignMap)
        {
            overlay.currentRearCount = structure.Rear.Count;
            overlay.currentRearVersion = structure.RearVersion;
            Rebuild(structure, overlay);
        }
        else if (structure.Rear.Count != overlay.currentRearCount
              || structure.RearVersion != overlay.currentRearVersion)
        {
            overlay.currentRearCount = structure.Rear.Count;
            overlay.currentRearVersion = structure.RearVersion;
            FillCellMask(overlay, structure);
        }

        overlay.renderer.enabled = _visible;

        // scale은 여기 없다 - 몸의 scale은 태어날 때 정해지고 안 변해서 Rebuild에서 한 번만 쓴다.
        overlay.renderer.transform.SetPositionAndRotation(
            structure.transform.TransformPoint(overlay.localOffset),
            structure.transform.rotation);

        return true;
    }

    private void Rebuild(HullStructure structure, Overlay overlay)
    {
        ShipGrid.Map DM = structure.DesignMap; //다이렉트 메시지 아님

        Discard(overlay);
        EnsureShared();

        if (_sharedMaterial == null)
            return;

        // **함선의 자식이 아니다.** 선체 직속 자식은 판만이어야 한다. 대신 매 프레임 따라간다.
        var go = new GameObject("rooms");
        go.transform.SetParent(transform, worldPositionStays: false);
        go.transform.localScale = structure.transform.lossyScale;

        // 후면 bbox(칸). Draw의 가드 덕에 Rear는 비어 있지 않다.
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

        // 쿼드는 공유 흰 텍스처의 사각형이다. 픽셀 눈금은 bbox가 256px 안에 들어가는
        // 최대 배율 - 지오메트리용일 뿐 그림 해상도와 무관하다.
        int scale = Mathf.Clamp(SharedTexSize / Mathf.Max(overlay.texW, overlay.texH), 1, 16);

        overlay.sprite = Sprite.Create(
            _sharedWhite,
            new Rect(0f, 0f, overlay.texW * scale, overlay.texH * scale),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: scale,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        // 소속 판정. 잔해·Hulk는 Ship이 없으니 저절로 외피 쪽으로 떨어진다.
        overlay.mine = structure.TryGetComponent(out Ship ship) && ship.IsPlayerControlled;

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = overlay.sprite;
        overlay.renderer.sharedMaterial = _sharedMaterial;
        overlay.renderer.sortingOrder = overlay.mine ? MineOrder : SkinOrder;

        overlay._designMap = DM;

        overlay.localOffset = DM.ToLocal(minCol, minRow)
            + new Vector2((overlay.texW - 1) * 0.5f, -(overlay.texH - 1) * 0.5f);

        overlay.cellMask = new Texture2D(DM.width, DM.height, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        overlay.cellBytes = new byte[DM.width * DM.height];

        DesignMasks masks = MasksFor(DM, structure);

        var props = new MaterialPropertyBlock();
        props.SetTexture(HullTexId, structure.ShipHullPng != null ? structure.ShipHullPng : _sharedWhite);
        props.SetTexture(CellMaskId, overlay.cellMask);
        props.SetTexture(StaticMaskId, masks.staticMask);
        props.SetTexture(FootMaskId, masks.footMask);

        // uv -> 칸 좌표: cx = minCol + uv.x * (256/scale), rowDown = (minRow+texH) - uv.y * (256/scale)
        props.SetVector(GridId, new Vector4(minCol, minRow, (float)SharedTexSize / scale, overlay.texH));
        props.SetVector(MapId, new Vector4(
            DM.width, DM.height,
            1f - (overlay.mine ? MineDarken : SkinDarken),
            structure.ShipHullPng != null ? 1f : 0f));

        overlay.renderer.SetPropertyBlock(props);

        FillCellMask(overlay, structure);
    }

    /// <summary>
    /// 후면 장부 -> 칸 마스크. 후면이 변한 이벤트마다 통째로 다시 채운다 - 칸당 1바이트라
    /// 어느 칸이 변했는지 추적하는 것보다 전부 다시 쓰는 쪽이 싸고 단순하다.
    /// </summary>
    private static void FillCellMask(Overlay overlay, HullStructure structure)
    {
        ShipGrid.Map map = overlay._designMap;
        System.Array.Clear(overlay.cellBytes, 0, overlay.cellBytes.Length);

        foreach (KeyValuePair<Vector2Int, HullStructure.RearCell> pair in structure.RearEntries)
        {
            Vector2Int cell = pair.Key;

            if (cell.x < 0 || cell.y < 0 || cell.x >= map.width || cell.y >= map.height)
                continue;

            float life = pair.Value.maxHp > 0f
                ? Mathf.Clamp01(pair.Value.hp / pair.Value.maxHp)
                : 1f;

            // 0은 "없음" 전용. 있는 칸은 1..255.
            overlay.cellBytes[(map.height - 1 - cell.y) * map.width + cell.x] =
                (byte)(1 + Mathf.RoundToInt(life * 254f));
        }

        overlay.cellMask.SetPixelData(overlay.cellBytes, 0);
        overlay.cellMask.Apply(false);
    }

    /// <summary>설계도당 정적 마스크. worthy/경계는 칸 해상도, 발자국은 칸당 16px - 전부 불변.</summary>
    private static DesignMasks MasksFor(ShipGrid.Map map, HullStructure structure)
    {
        if (_designCache.TryGetValue(map, out DesignMasks masks))
            return masks;

        masks = new DesignMasks();

        // r = RearWorthy(뜯김 판정의 "설계엔 있었다"), g = 우주와 닿은 경계 칸(발자국 마스킹 대상)
        masks.staticMask = new Texture2D(map.width, map.height, TextureFormat.RG16, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        var rg = new byte[map.width * map.height * 2];

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            bool boundary = false;

            for (int dy = -1; dy <= 1 && !boundary; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nc = col + dx, nr = row + dy;

                if (nc < 0 || nc >= map.width || nr < 0 || nr >= map.height
                    || map.cells[nc, nr] == ShipGrid.Cell.Exterior)
                {
                    boundary = true;
                    break;
                }
            }

            int i = ((map.height - 1 - row) * map.width + col) * 2;
            rg[i] = HullStructure.RearWorthyAt(map, col, row) ? (byte)255 : (byte)0;
            rg[i + 1] = boundary ? (byte)255 : (byte)0;
        }

        masks.staticMask.SetPixelData(rg, 0);
        masks.staticMask.Apply(false);

        // 발자국 실루엣. 설계 시점 판 모양이라 불변 - 첫 오버레이(본체, 후면 완전)에서 한 번 굽고
        // 그 설계의 모든 조각이 공유한다. 발자국 없는 칸은 불투명(255) = 칸 전체.
        int widthPx = map.width * RearPPU;
        int heightPx = map.height * RearPPU;
        masks.footMask = new Texture2D(widthPx, heightPx, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };

        var foot = new byte[widthPx * heightPx];

        for (int i = 0; i < foot.Length; i++)
            foot[i] = 255;

        foreach (KeyValuePair<Vector2Int, HullStructure.RearCell> pair in structure.RearEntries)
        {
            Vector2[] footprint = pair.Value.footprint;

            if (footprint == null)
                continue;

            Vector2Int cell = pair.Key;

            for (int py = 0; py < RearPPU; py++)
            for (int px = 0; px < RearPPU; px++)
            {
                // 발자국은 칸 중심 기준 배 좌표계(y 위)다. 텍스처 py는 아래로 간다.
                var local = new Vector2(
                    (px + 0.5f) / RearPPU - 0.5f,
                    0.5f - (py + 0.5f) / RearPPU);

                if (Ballistics.PolygonContains(footprint, local))
                    continue;

                int texY = heightPx - 1 - (cell.y * RearPPU + py);
                foot[texY * widthPx + (cell.x * RearPPU + px)] = 0;
            }
        }

        masks.footMask.SetPixelData(foot, 0);
        masks.footMask.Apply(false);

        _designCache[map] = masks;
        return masks;
    }

    private static void EnsureShared()
    {
        if (_sharedWhite == null)
        {
            _sharedWhite = new Texture2D(SharedTexSize, SharedTexSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[SharedTexSize * SharedTexSize];

            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, 255);

            _sharedWhite.SetPixels32(pixels);
            _sharedWhite.Apply(false);
        }

        if (_sharedMaterial == null)
        {
            Shader shader = Shader.Find("SUPERRADIANCE/RearSkin");

            if (shader == null)
            {
                Debug.LogError("[BackPlateView] RearSkin 셰이더를 못 찾았다. 빌드라면 " +
                    "Always Included Shaders에 넣었는지 확인할 것.");
                return;
            }

            _sharedMaterial = new Material(shader);
        }
    }
}
