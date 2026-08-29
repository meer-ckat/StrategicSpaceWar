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
/// **후면의 모양은 격자가 아니라 판의 기하에서 나온다.** 설계도당 한 번, 판 폴리곤의
/// 합집합을 16배 해상도로 굽고 바깥에서 flood해서 둘러싸인 안쪽을 실루엣으로 굳힌다 -
/// 칸 단위로 자르면 경사 지붕 칸의 안쪽 절반이 잘려 천장에 구멍이 뚫린다.
/// 정적 마스크 두 장(worthy, 실루엣)은 설계 시점 값이라 불변이고 조각들이 공유한다.
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

        /// <summary>
        /// 칸 해상도 열 마스크. 뜯긴 칸이 물려받은 Armor.Heat를 0..255로. 안 뜨거운
        /// 칸이 대부분이라 <see cref="HullStructure.HotRear"/>가 가리키는 칸만 매 프레임
        /// 갱신한다 - cellMask처럼 구조 변화마다 전부 다시 굽지 않는다.
        /// </summary>
        public Texture2D heatMask;
        public byte[] heatBytes;
        public readonly HashSet<Vector2Int> hotWritten = new();

        public ShipGrid.Map _designMap; //immutable;
        public Vector2 localOffset;
        public int currentRearCount;
        public int currentRearVersion;

    }

    /// <summary>설계도당 정적 마스크. 조각들이 공유하고, 안 쓰는 설계도는 청소 때 파괴한다.</summary>
    private sealed class DesignMasks
    {
        public Texture2D staticMask;   // R8, 칸 해상도: RearWorthy
        public Texture2D footMask;     // R8, 칸당 16px: 판 폴리곤이 둘러싼 안쪽 = 후면 실루엣

        // 모든 판 발자국의 칸 중심 기준 극값. 조각 쿼드도 이만큼 넓혀야 회전·offset·
        // 1m 초과 판이 자기 배치 칸 밖으로 나간 부분을 실제로 그릴 수 있다.
        public float minLocalX;
        public float maxLocalX;
        public float minLocalY;
        public float maxLocalY;

        // footMask가 덮는 설계도 좌표. x는 오른쪽, row는 아래로 증가한다.
        public Vector4 footRect;
    }

    private readonly Dictionary<HullStructure, Overlay> _overlays = new();
    const bool _visible = true;

    private static readonly Dictionary<ShipGrid.Map, DesignMasks> _designCache = new();
    private static readonly HashSet<ShipGrid.Map> _mapsInUse = new();
    private static readonly HashSet<Vector2Int> _footKnown = new();
    private static readonly List<ShipGrid.Map> _staleMaps = new();

    private static Texture2D _sharedWhite;
    private static Material _sharedMaterial;

    private static readonly int HullTexId = Shader.PropertyToID("_HullTex");
    private static readonly int CellMaskId = Shader.PropertyToID("_CellMask");
    private static readonly int HeatMaskId = Shader.PropertyToID("_HeatMask");
    private static readonly int StaticMaskId = Shader.PropertyToID("_StaticMask");
    private static readonly int FootMaskId = Shader.PropertyToID("_FootMask");
    private static readonly int GridId = Shader.PropertyToID("_Grid");
    private static readonly int MapId = Shader.PropertyToID("_Map");
    private static readonly int FootId = Shader.PropertyToID("_Foot");

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

        if (o.heatMask != null)
            Destroy(o.heatMask);

        o.renderer = null;
        o.sprite = null;
        o.cellMask = null;
        o.cellBytes = null;
        o.heatMask = null;
        o.heatBytes = null;
        o.hotWritten.Clear();
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

        UpdateHeat(overlay, structure);

        overlay.renderer.enabled = _visible;

        Follow(overlay.renderer.transform, structure.transform, overlay.localOffset);

        return true;
    }

    private static readonly HashSet<Vector2Int> _stillHot = new();

    /// <summary>
    /// 열 마스크를 매 프레임 갱신한다. cellMask와 달리 구조 변화가 아니라 시간에 따라
    /// 변하므로(감쇠) 매 프레임 봐야 하지만, <see cref="HullStructure.HotRear"/>가
    /// 이미 "뜨거운 칸만" 걸러 주므로 안 뜨거운 배는 아래가 사실상 공짜다.
    /// </summary>
    private static void UpdateHeat(Overlay overlay, HullStructure structure)
    {
        structure.PruneHotRear();
        List<Vector2Int> hot = structure.HotRear;

        if (hot.Count == 0 && overlay.hotWritten.Count == 0)
            return;

        ShipGrid.Map map = overlay._designMap;
        _stillHot.Clear();
        bool changed = false;

        for (int i = 0; i < hot.Count; i++)
        {
            Vector2Int cell = hot[i];

            if (!structure.TryGetRear(cell, out HullStructure.RearCell wall))
                continue;

            byte b = (byte)Mathf.Clamp(
                Mathf.RoundToInt(HullStructure.RearHeatNow(wall) * 255f), 0, 255);

            overlay.heatBytes[(map.height - 1 - cell.y) * map.width + cell.x] = b;
            _stillHot.Add(cell);
            changed = true;
        }

        // 지난 프레임엔 뜨거웠는데 이번엔 식어서 빠진 칸. 안 지우면 텍스처에 옛 값이 남는다.
        foreach (Vector2Int cell in overlay.hotWritten)
        {
            if (_stillHot.Contains(cell))
                continue;

            overlay.heatBytes[(map.height - 1 - cell.y) * map.width + cell.x] = 0;
            changed = true;
        }

        overlay.hotWritten.Clear();

        foreach (Vector2Int cell in _stillHot)
            overlay.hotWritten.Add(cell);

        if (changed)
        {
            overlay.heatMask.SetPixelData(overlay.heatBytes, 0);
            overlay.heatMask.Apply(false);
        }
    }

    /// <summary>
    /// 오버레이는 선체 자식이 아니므로 월드 자세를 직접 따라간다. scale도 매 프레임 읽는다 -
    /// 생성 뒤 localScale.x가 바뀌면 위치는 TransformPoint로 즉시 반전되는데 그림만 이전
    /// scale에 남아, 선체와 RearView가 서로 반대 방향을 보는 반쪽 상태가 되기 때문이다.
    /// </summary>
    private static void Follow(Transform follower, Transform structure, Vector2 localOffset)
    {
        follower.SetPositionAndRotation(
            structure.TransformPoint(localOffset),
            structure.rotation);
        follower.localScale = structure.lossyScale;
    }

#if UNITY_EDITOR
    internal static bool MirroredFollowSelfTest()
    {
        var structure = new GameObject("rear view mirror source");
        var follower = new GameObject("rear view mirror follower");

        try
        {
            structure.transform.SetPositionAndRotation(new Vector3(3f, -2f, 0f),
                Quaternion.Euler(0f, 0f, 27f));

            Vector2 offset = new(2f, 0.5f);
            Follow(follower.transform, structure.transform, offset);
            structure.transform.localScale = new Vector3(-1f, 1f, 1f);
            Follow(follower.transform, structure.transform, offset);

            return follower.transform.localScale.x < 0f
                && (follower.transform.position - structure.transform.TransformPoint(offset)).sqrMagnitude
                    < 1e-6f
                && Quaternion.Angle(follower.transform.rotation, structure.transform.rotation) < 1e-4f;
        }
        finally
        {
            DestroyImmediate(structure);
            DestroyImmediate(follower);
        }
    }
#endif

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

        DesignMasks masks = MasksFor(DM, structure);

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

        // 칸 중심(0.5)에서 실제 발자국 극값까지. row는 아래로 증가하므로 y 부호가 뒤집힌다.
        float minX = minCol + 0.5f + masks.minLocalX;
        float maxX = maxCol + 0.5f + masks.maxLocalX;
        float minRowDown = minRow + 0.5f - masks.maxLocalY;
        float maxRowDown = maxRow + 0.5f - masks.minLocalY;
        float width = Mathf.Max(1f / RearPPU, maxX - minX);
        float height = Mathf.Max(1f / RearPPU, maxRowDown - minRowDown);


        // 쿼드는 공유 흰 텍스처의 사각형이다. 픽셀 눈금은 rect가 256px 안에 들어가는
        // 최대 배율 - 지오메트리용일 뿐 그림 해상도와 무관하다.
        int scale = Mathf.Clamp(
            Mathf.FloorToInt(SharedTexSize / Mathf.Max(width, height)), 1, 16);

        // **rect는 정수 픽셀로 잡고 폭·높이를 거기서 되받는다.** 셰이더가 uv를 설계 좌표로
        // 되돌릴 때 쓰는 배율이 픽셀 격자에서 나오므로, 쿼드의 실제 크기와 셰이더가 믿는
        // 크기가 정확히 같아야 한다. 소수 rect를 그냥 쓰면 그 둘이 반 픽셀씩 어긋난다.
        int wPx = Mathf.Clamp(Mathf.CeilToInt(width * scale), 1, SharedTexSize);
        int hPx = Mathf.Clamp(Mathf.CeilToInt(height * scale), 1, SharedTexSize);

        width = wPx / (float)scale;
        height = hPx / (float)scale;

        overlay.sprite = Sprite.Create(
            _sharedWhite,
            new Rect(0f, 0f, wPx, hPx),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: scale,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        // 소속 판정. 잔해·Hulk는 Ship이 없으니 저절로 외피 쪽으로 떨어진다.
        overlay.mine = false;//structure.TryGetComponent(out Ship ship) && ship.IsPlayerControlled;

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = overlay.sprite;
        overlay.renderer.sharedMaterial = _sharedMaterial;
        overlay.renderer.sortingOrder = overlay.mine ? MineOrder : SkinOrder;

        overlay._designMap = DM;

        // 쿼드 중심의 설계 좌표. 위에서 정수 픽셀로 되받은 폭·높이를 그대로 쓴다 -
        // maxX/maxRowDown으로 다시 재면 반올림한 만큼 그림이 반 픽셀 밀린다.
        Vector2 mapOrigin = DM.ToLocal(0, 0);
        overlay.localOffset = mapOrigin + new Vector2(
            minX + width * 0.5f - 0.5f,
            -(minRowDown + height * 0.5f - 0.5f));

        overlay.cellMask = new Texture2D(DM.width, DM.height, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        overlay.cellBytes = new byte[DM.width * DM.height];

        overlay.heatMask = new Texture2D(DM.width, DM.height, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        overlay.heatBytes = new byte[DM.width * DM.height];
        overlay.hotWritten.Clear();

        // 새로 만든 텍스처는 갱신 전까지 GPU에서 미정의 값이다 - 첫 열이 생기기 전에도
        // 조용히 안 뜨거워야 하므로 0으로 한 번 올려 둔다.
        overlay.heatMask.SetPixelData(overlay.heatBytes, 0);
        overlay.heatMask.Apply(false);

        var props = new MaterialPropertyBlock();
        props.SetTexture(HullTexId, structure.ShipHullPng != null ? structure.ShipHullPng : _sharedWhite);
        props.SetTexture(CellMaskId, overlay.cellMask);
        props.SetTexture(HeatMaskId, overlay.heatMask);
        props.SetTexture(StaticMaskId, masks.staticMask);
        props.SetTexture(FootMaskId, masks.footMask);

        // uv -> 설계 좌표. **uv는 0~1이 아니다** - Sprite.Create의 uv는 텍스처 기준
        // (rect / 256)이라, 칸으로 되돌리는 배율은 폭이 아니라 SharedTexSize/scale이다.
        // 폭을 그냥 넣으면 uv가 0~0.33밖에 안 도는 배에서 설계도 일부가 화면 전체로
        // 늘어난다. w에는 세로 뒤집기용 높이를 넣는다(row는 아래로, uv.y는 위로 증가).
        props.SetVector(GridId, new Vector4(
            minX, minRowDown, (float)SharedTexSize / scale, height));
        props.SetVector(MapId, new Vector4(
            DM.width, DM.height,
            1f - (overlay.mine ? MineDarken : SkinDarken),
            structure.ShipHullPng != null ? 1f : 0f));
        props.SetVector(FootId, masks.footRect);

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

    /// <summary>설계도당 정적 마스크. worthy는 칸 해상도, 실루엣은 칸당 16px - 전부 불변.</summary>
    private static DesignMasks MasksFor(ShipGrid.Map map, HullStructure structure)
    {
        if (_designCache.TryGetValue(map, out DesignMasks masks))
            return masks;

        masks = new DesignMasks();

        // RearWorthy = 뜯김 판정의 "설계엔 후면이 있었다". 예전에는 "우주에 닿은 경계 칸"
        // 채널이 하나 더 있었는데, 실루엣이 기하에서 나오면서 쓸 데가 없어졌다.
        masks.staticMask = new Texture2D(map.width, map.height, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        var worthy = new byte[map.width * map.height];

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            worthy[(map.height - 1 - row) * map.width + col] =
                HullStructure.RearWorthyAt(map, col, row) ? (byte)255 : (byte)0;
        }

        masks.staticMask.SetPixelData(worthy, 0);
        masks.staticMask.Apply(false);

        // 발자국 실루엣. 판마다 자기 1x1 칸만 굽던 옛 방식은 회전·offset·큰 판이 칸 밖으로
        // 나간 순간 잘렸다. 먼저 실제 발자국의 극값을 구하고, 설계도 바깥 돌출까지 담는
        // 하나의 좌표계에 전부 합친다.
        masks.minLocalX = -0.5f;
        masks.maxLocalX = 0.5f;
        masks.minLocalY = -0.5f;
        masks.maxLocalY = 0.5f;

        foreach (KeyValuePair<Vector2Int, HullStructure.RearCell> pair in structure.RearEntries)
        {
            Vector2[] footprint = pair.Value.footprint;

            if (footprint == null)
                continue;

            for (int i = 0; i < footprint.Length; i++)
            {
                masks.minLocalX = Mathf.Min(masks.minLocalX, footprint[i].x);
                masks.maxLocalX = Mathf.Max(masks.maxLocalX, footprint[i].x);
                masks.minLocalY = Mathf.Min(masks.minLocalY, footprint[i].y);
                masks.maxLocalY = Mathf.Max(masks.maxLocalY, footprint[i].y);
            }
        }

        float footMinX = Mathf.Floor((0.5f + masks.minLocalX) * RearPPU) / RearPPU;
        float footMaxX = Mathf.Ceil((map.width - 0.5f + masks.maxLocalX) * RearPPU) / RearPPU;
        float footMinRow = Mathf.Floor((0.5f - masks.maxLocalY) * RearPPU) / RearPPU;
        float footMaxRow = Mathf.Ceil((map.height - 0.5f - masks.minLocalY) * RearPPU) / RearPPU;
        float footWidth = Mathf.Max(1f / RearPPU, footMaxX - footMinX);
        float footHeight = Mathf.Max(1f / RearPPU, footMaxRow - footMinRow);
        int widthPx = Mathf.Max(1, Mathf.RoundToInt(footWidth * RearPPU));
        int heightPx = Mathf.Max(1, Mathf.RoundToInt(footHeight * RearPPU));

        masks.footRect = new Vector4(footMinX, footMinRow, footWidth, footHeight);
        masks.footMask = new Texture2D(widthPx, heightPx, TextureFormat.R8, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };

        var foot = new byte[widthPx * heightPx];
        _footKnown.Clear();

        foreach (KeyValuePair<Vector2Int, HullStructure.RearCell> pair in structure.RearEntries)
        {
            _footKnown.Add(pair.Key);
            RasterFootprint(
                foot, widthPx, heightPx,
                footMinX, footMaxRow, RearPPU,
                pair.Key, pair.Value.footprint);
        }

        // **마스크는 설계도 것인데 발자국은 이 몸이 든 칸에서만 온다.** 캐시를 만든 것이
        // 조각 하나면 나머지 칸은 전부 0으로 남고, 경계 칸의 발자국 검사가 같은 설계도를
        // 쓰는 다른 몸의 외피를 통째로 지운다. 모르는 칸은 옛 기본값(칸 전체 = 불투명)으로
        // 채워서, 알려진 실루엣만 깎이고 나머지는 안전한 쪽으로 남게 한다.
        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            var cell = new Vector2Int(col, row);

            if (!HullStructure.RearWorthyAt(map, col, row) || _footKnown.Contains(cell))
                continue;

            RasterFootprint(foot, widthPx, heightPx, footMinX, footMaxRow, RearPPU, cell, null);
        }

        // **여기서 모양이 격자를 떠난다.** 지금까지 그린 것은 판 폴리곤의 합집합이고,
        // 후면의 진짜 실루엣은 그 폴리곤들이 **둘러싼 안쪽**이다 - 격자의 MarkExterior와
        // 같은 규칙("밀폐 주머니가 실내다")을 16배 해상도로 한 번 더 돌린다. 다른 점은
        // 벽이 1m 칸이 아니라 판의 실제 폴리곤이라는 것뿐이다.
        //
        // 이게 없으면 경사 지붕 칸이 두 가지로 틀린다: 경계 칸이라 폴리곤으로 깎이는데
        // 그 칸의 **방을 향한 안쪽 절반까지 같이 잘려나가** 함교 천장에 구멍이 뚫리고,
        // 반대로 큰 경사판이 자기 칸 밖을 덮으면 그 칸은 격자상 Exterior라 후면이 아예 없다.
        FloodOutside(foot, widthPx, heightPx);

        masks.footMask.SetPixelData(foot, 0);
        masks.footMask.Apply(false);

        _designCache[map] = masks;
        return masks;
    }

    /// <summary>실물이 아니고 아직 안 닿은 픽셀만 큐에 넣는다. 닿은 표시가 곧 방문 표시다.</summary>
    private static void SeedOutside(byte[] mask, int[] queue, ref int tail, int index)
    {
        // 0 = 빈 픽셀, 255 = 판 폴리곤, Outside = 이미 닿음.
        if (mask[index] != 0)
            return;

        mask[index] = Outside;
        queue[tail++] = index;
    }

    private const byte Outside = 1;

    /// <summary>
    /// 마스크 테두리에서 4방향으로 번져 우주를 칠하고, **닿지 않은 곳을 후면으로 굳힌다.**
    /// 판 폴리곤이 벽이다.
    ///
    /// 픽셀당 최대 1회 입큐라 큐는 픽셀 수면 절대 안 넘친다. 설계도당 1회 - destroyer가
    /// 96만 픽셀이고, 여기서 쓰는 int 큐는 함수를 나가면 버려진다.
    ///
    /// **이음매 누수가 유일한 위험이다.** 인접한 두 경사판의 폴리곤이 16px 격자에서 1픽셀
    /// 벌어지면 flood가 실내로 새서 그 주머니의 후면이 통째로 사라진다.
    /// RasterFootprint의 2x2 보수 샘플링이 그 틈을 메우는 쪽으로 굽는 이유가 이것이다.
    /// </summary>
    private static void FloodOutside(byte[] mask, int width, int height)
    {
        var queue = new int[width * height];
        int head = 0, tail = 0;

        for (int x = 0; x < width; x++)
        {
            SeedOutside(mask, queue, ref tail, x);
            SeedOutside(mask, queue, ref tail, (height - 1) * width + x);
        }

        for (int y = 0; y < height; y++)
        {
            SeedOutside(mask, queue, ref tail, y * width);
            SeedOutside(mask, queue, ref tail, y * width + width - 1);
        }

        while (head < tail)
        {
            int at = queue[head++];
            int x = at % width;
            int y = at / width;

            if (x > 0) SeedOutside(mask, queue, ref tail, at - 1);
            if (x < width - 1) SeedOutside(mask, queue, ref tail, at + 1);
            if (y > 0) SeedOutside(mask, queue, ref tail, at - width);
            if (y < height - 1) SeedOutside(mask, queue, ref tail, at + width);
        }

        // 닿은 곳이 우주, 나머지(폴리곤 + 둘러싸인 주머니)가 후면이다.
        for (int i = 0; i < mask.Length; i++)
            mask[i] = mask[i] == Outside ? (byte)0 : (byte)255;
    }

    /// <summary>
    /// 한 판의 실제 발자국을 설계도 전체 마스크에 OR한다. 픽셀 중심 하나만 보면 16px
    /// 사선과 ArmorSkin의 64px 사선 사이로 뼈대가 비치므로, 2x2 지점 중 하나라도 실물이면
    /// 덮는다. 저장 해상도와 GPU 비용은 그대로고 설계도당 최초 1회 CPU만 네 배다.
    /// </summary>
    private static void RasterFootprint(
        byte[] pixels,
        int width,
        int height,
        float minX,
        float maxRowDown,
        int ppu,
        Vector2Int cell,
        Vector2[] footprint)
    {
        float loX = -0.5f, hiX = 0.5f, loY = -0.5f, hiY = 0.5f;

        if (footprint != null && footprint.Length >= 3)
        {
            loX = hiX = footprint[0].x;
            loY = hiY = footprint[0].y;

            for (int i = 1; i < footprint.Length; i++)
            {
                loX = Mathf.Min(loX, footprint[i].x);
                hiX = Mathf.Max(hiX, footprint[i].x);
                loY = Mathf.Min(loY, footprint[i].y);
                hiY = Mathf.Max(hiY, footprint[i].y);
            }
        }

        float worldMinX = cell.x + 0.5f + loX;
        float worldMaxX = cell.x + 0.5f + hiX;
        float minRowDown = cell.y + 0.5f - hiY;
        float maxShapeRowDown = cell.y + 0.5f - loY;
        int px0 = Mathf.Clamp(Mathf.FloorToInt((worldMinX - minX) * ppu), 0, width - 1);
        int px1 = Mathf.Clamp(Mathf.CeilToInt((worldMaxX - minX) * ppu), 1, width);
        int py0 = Mathf.Clamp(Mathf.FloorToInt((maxRowDown - maxShapeRowDown) * ppu), 0, height - 1);
        int py1 = Mathf.Clamp(Mathf.CeilToInt((maxRowDown - minRowDown) * ppu), 1, height);

        for (int py = py0; py < py1; py++)
        for (int px = px0; px < px1; px++)
        {
            bool covered = false;

            // 텍스처 y는 위로, row는 아래로 증가한다. 1/4·3/4 지점을 보수적으로 샘플한다.
            for (int sy = 0; sy < 2 && !covered; sy++)
            for (int sx = 0; sx < 2; sx++)
            {
                float cx = minX + (px + (sx + 0.5f) * 0.5f) / ppu;
                float rowDown = maxRowDown - (py + (sy + 0.5f) * 0.5f) / ppu;
                var local = new Vector2(
                    cx - (cell.x + 0.5f),
                    cell.y + 0.5f - rowDown);

                covered = footprint == null || footprint.Length < 3
                    ? local.x >= -0.5f && local.x <= 0.5f
                        && local.y >= -0.5f && local.y <= 0.5f
                    : Ballistics.PolygonContains(footprint, local);
            }

            if (covered)
                pixels[py * width + px] = 255;
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// uv -> 설계 좌표 왕복. **Sprite.Create의 uv는 0~1이 아니라 rect/텍스처크기다** -
    /// 셰이더에 폭을 그대로 넘겼다가 destroyer의 uv.y가 0~0.33밖에 안 도는 바람에
    /// 설계도 아래 3분의 1이 화면 전체로 늘어난 적이 있다. 셰이더의 두 줄을 그대로
    /// 옮겨 놓고, 쿼드의 네 귀퉁이가 rect의 네 귀퉁이로 돌아오는지 본다.
    /// </summary>
    internal static bool QuadUvSelfTest()
    {
        const float minX = 7.25f;
        const float minRowDown = -1.5f;
        float width = 61f, height = 21f;

        int scale = Mathf.Clamp(
            Mathf.FloorToInt(SharedTexSize / Mathf.Max(width, height)), 1, 16);
        int wPx = Mathf.Clamp(Mathf.CeilToInt(width * scale), 1, SharedTexSize);
        int hPx = Mathf.Clamp(Mathf.CeilToInt(height * scale), 1, SharedTexSize);

        width = wPx / (float)scale;
        height = hPx / (float)scale;

        // Sprite.Create의 uv: rect가 공유 텍스처의 어디를 쓰는지. 0~1이 아니다.
        float uvMaxX = wPx / (float)SharedTexSize;
        float uvMaxY = hPx / (float)SharedTexSize;
        float uvToCell = (float)SharedTexSize / scale;

        // 셰이더 두 줄
        static float Cx(float uvx, float min, float toCell) => min + uvx * toCell;
        static float Row(float uvy, float min, float h, float toCell) => min + h - uvy * toCell;

        bool leftEdge = Mathf.Abs(Cx(0f, minX, uvToCell) - minX) < 1e-3f;
        bool rightEdge = Mathf.Abs(Cx(uvMaxX, minX, uvToCell) - (minX + width)) < 1e-3f;

        // uv.y가 위로 증가하고 row는 아래로 증가한다 - 위 끝이 minRowDown이어야 한다.
        bool topEdge = Mathf.Abs(Row(uvMaxY, minRowDown, height, uvToCell) - minRowDown) < 1e-3f;
        bool bottomEdge = Mathf.Abs(Row(0f, minRowDown, height, uvToCell) - (minRowDown + height)) < 1e-3f;

        return leftEdge && rightEdge && topEdge && bottomEdge;
    }

    /// <summary>
    /// 실루엣 규칙: **판이 둘러싼 안쪽이 후면이다.** 빈 방 한가운데는 판이 하나도 없는데도
    /// 후면이어야 하고(바깥 flood가 못 닿는 주머니라서), 벽에 구멍이 하나라도 나면 그
    /// 주머니가 통째로 우주가 된다 - 이음매 누수가 왜 유일한 진짜 위험인지가 이 두 줄이다.
    /// </summary>
    internal static bool SilhouetteFloodSelfTest()
    {
        const int w = 8, h = 8;

        static byte[] Ring()
        {
            var m = new byte[w * h];

            // (1,1)~(6,6) 테두리만 실물. 바깥 한 겹은 비워 두어 flood가 들어올 문이 된다.
            for (int i = 1; i <= 6; i++)
            {
                m[1 * w + i] = 255;
                m[6 * w + i] = 255;
                m[i * w + 1] = 255;
                m[i * w + 6] = 255;
            }

            return m;
        }

        byte[] closed = Ring();
        FloodOutside(closed, w, h);

        bool pocketIsRear = closed[3 * w + 3] == 255;   // 판 없는 방 한가운데
        bool wallIsRear = closed[1 * w + 1] == 255;     // 판 자신
        bool spaceIsNot = closed[0] == 0;               // 바깥

        byte[] leaky = Ring();
        leaky[3 * w + 1] = 0;                           // 벽에 1픽셀 구멍
        FloodOutside(leaky, w, h);

        bool leakEatsPocket = leaky[3 * w + 3] == 0;

        return pocketIsRear && wallIsRear && spaceIsNot && leakEatsPocket;
    }

    internal static bool FootprintOverflowSelfTest()
    {
        const int ppu = 16;
        const int width = 48;
        const int height = 48;
        var pixels = new byte[width * height];
        var cell = new Vector2Int(1, 1);

        // 45도 큰 판: x=0.70은 자기 칸 오른쪽(+0.5)을 넘어 이웃 칸에 있다.
        Vector2[] diamond =
        {
            new(0f, 0.75f), new(0.75f, 0f), new(0f, -0.75f), new(-0.75f, 0f),
        };

        RasterFootprint(pixels, width, height, 0f, 3f, ppu, cell, diamond);

        int outsideAnchorX = Mathf.FloorToInt((cell.x + 0.5f + 0.65f) * ppu);
        int centreY = Mathf.FloorToInt((3f - (cell.y + 0.5f)) * ppu);
        int definitelyOutsideX = Mathf.FloorToInt((cell.x + 0.5f + 0.9f) * ppu);

        return pixels[centreY * width + outsideAnchorX] != 0
            && pixels[centreY * width + definitelyOutsideX] == 0;
    }
#endif

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
