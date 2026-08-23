using System.Collections.Generic;
using UnityEngine;
/// <summary>
/// 후면(외피) 그림. 시뮬레이션의 후면 장부(<see cref="HullStructure"/>)를 화면에 옮긴다.
///
/// **비대칭 화면: 소속이 곧 렌더 모드다.** 내 배(IsPlayerControlled) = 외피가 어둡게 뒤에
/// 깔린 내부 단면. 적함·중립·잔해 = 외피가 위로 올라와 얼굴이 되고, 상태를 구멍으로 읽는다.
///
/// **굽기는 칸 단위 더티다.** 총알 한 발이 후면 칸 하나를 깎을 때 맵 전체(칸 수 x 256픽셀)를
/// 다시 굽던 것이 40만 픽셀짜리 프레임 스파이크였다 - HullStructure.RearDirty가 변한 칸을
/// 적어 주고, 여기는 그 칸의 16x16 블록만 다시 굽는다. 전체 굽기는 생성·파단·대폭발 폴백뿐이다.
///
/// SpallTrails와 같은 방식으로 자기를 심는다. 씬에 손으로 붙일 것이 없어야 런타임에
/// 소환되는 함선에도 그대로 붙는다.
/// </summary>
public sealed class BackPlateView : MonoBehaviour
{
    /// <summary>배 그림이 없을 때의 폴백 색.</summary>
    private static readonly Color Structure = new(1f, 1f, 1f, 1f);

    // hull png가 이미 어두운 회색조라 0.5를 곱하면 우주 배경에 묻힌다.
    // 0.35에서 시작 - 더 밝거나 어둡게는 이 숫자 하나다.
    private const float MineDarken = 0.35f;
    private const int MineOrder = -10;

    private const float SkinDarken = 0f;
    private const int SkinOrder = 100;

    /// <summary>
    /// 후면 텍스처의 칸당 픽셀. **1이면 마스킹 단위가 통째로 1 m 칸이다** - 선체 그림을
    /// 칸 중심에서 한 번만 찍으니 경사 실루엣 밖으로 사각 블록이 삐져나온다. 16이면
    /// 그림의 알파를 픽셀마다 읽어서 후면이 실루엣을 따라 잘린다.
    /// </summary>
    private const int RearPPU = 16;

    private sealed class Overlay
    {
        /// <summary>이 오버레이가 내 배 것인가. Rebuild가 정하고 Paint가 읽는다.</summary>
        public bool mine;

        public SpriteRenderer renderer;
        public Texture2D texture;
        public Color32[] pixels;        // bbox 크기(픽셀). 텍스처와 같은 배열
        public Color32[] art;           // 설계도 전체 크기의 원본 색. _artCache 공유
        public ShipGrid.Map _designMap; //immutable;
        public Vector2 localOffset;     // 텍스처 한가운데의 선체 기준 자리
        public int currentRearCount;
        public int currentRearVersion;

        /// <summary>더티 링에서 어디까지 봤나. 생산자는 기록만 하고 소비자가 커서를 든다.</summary>
        public long dirtyCursor;

        // 텍스처는 설계도 전체가 아니라 이 조각의 후면 bbox만 하다(칸 단위). 후면은 줄기만
        // 하니 생성 때 bbox면 영원히 충분하고, 잔해가 설계도 전체 크기 텍스처를 받는 일이
        // 없어진다.
        public int minCol;
        public int minRow;
        public int texW;    // 칸 수
        public int texH;
    }

    private readonly Dictionary<HullStructure, Overlay> _overlays = new();
    const bool _visible = true;

    /// <summary>
    /// 설계도별 원본 색(칸당 16x16 픽셀로 미리 샘플한 배 그림). **조각들이 공유한다** -
    /// 본체와 잔해가 같은 DesignMap 참조를 들고 다니므로, 파단으로 조각 수십 개가 생겨도
    /// 배 그림 샘플링(GetPixelBilinear x 40만)은 설계도당 한 번이다. 어둡기(mine/skin)는
    /// 칠할 때 곱한다 - 캐시는 원본이다.
    ///
    /// 청소는 "안 쓰는 설계도 제거"다. "오버레이가 전멸하면 비운다"로 하면 안 된다 -
    /// 플레이어 함선이 구역 사이에도 살아남아 오버레이가 0이 되는 순간이 런 도중에 안 온다.
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
        o.art = null;   // 공유 캐시 참조만 놓는다 - 배열은 _artCache가 주인
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

    /// <summary>
    /// 그렸으면 true. 이 반환값이 곧 오버레이의 수명이다 - false를 돌려주면 호출자가
    /// 그 배의 오버레이를 파괴한다.
    /// </summary>
    private bool Draw(HullStructure structure)
    {
        if (structure.DesignMap == null || structure.Rear.Count == 0)
            return false;

        // LOD: 조각 잔해의 오버레이는 화면에서 어두운 픽셀 몇 개인데, 값은 오브젝트 생성에
        // 매 프레임 추적이다. 방이 없는 작은 조각은 아예 안 만든다 - 본체(Ship)는 아무리
        // 쪼그라들어도 남긴다.
        if (structure.Rear.Count < MinRearForOverlay && !IsShip(structure))
            return false;

        if (!_overlays.TryGetValue(structure, out Overlay overlay))
            _overlays[structure] = overlay = new Overlay();

        if (overlay._designMap != structure.DesignMap)
        {
            // 맵이 바뀌었다 = 파단으로 새 덩어리다. 텍스처 크기부터 다르니 통째로.
            overlay.currentRearCount = structure.Rear.Count;
            overlay.currentRearVersion = structure.RearVersion;
            overlay.dirtyCursor = structure.RearDirtyTotal;
            Rebuild(structure, overlay);
        }
        else if (structure.Rear.Count != overlay.currentRearCount
              || structure.RearVersion != overlay.currentRearVersion)
        {
            overlay.currentRearCount = structure.Rear.Count;
            overlay.currentRearVersion = structure.RearVersion;

            long total = structure.RearDirtyTotal;
            long from = overlay.dirtyCursor;
            overlay.dirtyCursor = total;

            // 커서가 링 용량보다 밀렸거나(대폭발·긴 공백) 기록 없이 변했으면 전체 폴백.
            if (total > from && total - from <= HullStructure.RearDirtyRingSize)
                PaintCells(overlay, structure, from, total);
            else
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

        // 이 조각이 실제로 든 후면 칸의 bbox. 본체는 사실상 설계도 전체고, 작은 잔해는
        // 칸 몇 개다. Draw의 가드 덕에 여기서 Rear는 비어 있지 않다.
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

        overlay.texture = new Texture2D(
            overlay.texW * RearPPU, overlay.texH * RearPPU, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
        };

        overlay.pixels = new Color32[overlay.texW * RearPPU * overlay.texH * RearPPU];

        // 피벗은 한가운데. 모서리에 두면 부호나 축을 하나 틀려도 "조금 어긋난 그림"이라
        // 눈에 안 띄는데, 중심이면 대칭으로 틀어져서 바로 보인다.
        var sprite = Sprite.Create(
            overlay.texture,
            new Rect(0f, 0f, overlay.texW * RearPPU, overlay.texH * RearPPU),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: RearPPU,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        // 소속 판정. Ship은 HullStructure와 같은 GameObject다(RequireComponent).
        // 잔해·Hulk는 Ship이 없으니 저절로 외피 쪽으로 떨어진다 - 내 배에서 떨어진
        // 조각이 그 순간 외피를 입는 것은 받아들인 결정이다.
        overlay.mine = structure.TryGetComponent(out Ship ship) && ship.IsPlayerControlled;

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = sprite;
        overlay.renderer.sortingOrder = overlay.mine ? MineOrder : SkinOrder;

        overlay._designMap = DM;

        // bbox 한가운데의 선체 기준 좌표. 칸 (minCol,minRow)와 (maxCol,maxRow)의 중점.
        overlay.localOffset = DM.ToLocal(minCol, minRow)
            + new Vector2((overlay.texW - 1) * 0.5f, -(overlay.texH - 1) * 0.5f);

        overlay.art = ArtFor(DM, structure.ShipHullPng);
        Paint(overlay, structure);
    }

    /// <summary>
    /// 배 그림을 칸당 16x16 픽셀로 미리 샘플한 원본 색. 설계도당 한 번 굽고 조각들이
    /// 공유한다 - GetPixelBilinear 40만 회가 재굽기마다에서 설계도당 1회로 준다.
    /// </summary>
    private static Color32[] ArtFor(ShipGrid.Map map, Texture2D structureTexture)
    {
        if (_artCache.TryGetValue(map, out Color32[] art))
            return art;

        int widthPx = map.width * RearPPU;
        int heightPx = map.height * RearPPU;
        art = new Color32[widthPx * heightPx];

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        for (int py = 0; py < RearPPU; py++)
        for (int px = 0; px < RearPPU; px++)
        {
            Color color = structureTexture != null
                ? structureTexture.GetPixelBilinear(
                    (col + (px + 0.5f) / RearPPU) / map.width,
                    1f - (row + (py + 0.5f) / RearPPU) / map.height)
                : Structure;

            int texY = heightPx - 1 - (row * RearPPU + py);
            art[texY * widthPx + (col * RearPPU + px)] = color;
        }

        _artCache[map] = art;
        return art;
    }

    /// <summary>전체 굽기. 생성·파단, 그리고 더티 목록이 넘친 폴백에서만 돈다.</summary>
    private static void Paint(Overlay overlay, HullStructure structure)
    {
        System.Array.Clear(overlay.pixels, 0, overlay.pixels.Length);

        for (int row = overlay.minRow; row < overlay.minRow + overlay.texH; row++)
        for (int col = overlay.minCol; col < overlay.minCol + overlay.texW; col++)
            PaintCellInto(overlay, structure, col, row);

        overlay.texture.SetPixels32(overlay.pixels);
        overlay.texture.Apply(false);
    }

    private static readonly Color32[] _block = new Color32[RearPPU * RearPPU];
    private static readonly HashSet<Vector2Int> _dirtySeen = new();

    /// <summary>변한 칸만. 칸의 16x16 블록을 다시 굽고 그 블록만 올린다.</summary>
    private static void PaintCells(Overlay overlay, HullStructure structure, long from, long to)
    {
        int strideX = overlay.texW * RearPPU;
        int heightPx = overlay.texH * RearPPU;
        _dirtySeen.Clear();

        for (long i = from; i < to; i++)
        {
            Vector2Int cell = structure.RearDirtyAt(i);

            if (!_dirtySeen.Add(cell))
                continue;

            int localCol = cell.x - overlay.minCol;
            int localRow = cell.y - overlay.minRow;

            if (localCol < 0 || localRow < 0 || localCol >= overlay.texW || localRow >= overlay.texH)
                continue;

            // 블록을 지우고 다시 굽는다. 칸이 빠졌으면 지운 채로 남는 것이 곧 구멍이다.
            int blockYTop = heightPx - 1 - localRow * RearPPU;

            for (int py = 0; py < RearPPU; py++)
            {
                int texY = blockYTop - py;

                System.Array.Clear(overlay.pixels, texY * strideX + localCol * RearPPU, RearPPU);
            }

            PaintCellInto(overlay, structure, cell.x, cell.y);

            // 텍스처에는 블록만 올린다. SetPixels32 블록은 아랫줄부터라 y를 뒤집어 담는다.
            int blockYBottom = heightPx - (localRow + 1) * RearPPU;

            for (int sy = 0; sy < RearPPU; sy++)
            for (int sx = 0; sx < RearPPU; sx++)
                _block[sy * RearPPU + sx] =
                    overlay.pixels[(blockYBottom + sy) * strideX + localCol * RearPPU + sx];

            overlay.texture.SetPixels32(
                localCol * RearPPU, blockYBottom, RearPPU, RearPPU, _block);
        }

        overlay.texture.Apply(false);
    }

    /// <summary>
    /// 칸 하나(16x16)를 overlay.pixels에 굽는다. 마모(hp/maxHp)·뜯긴 가장자리(RearTorn)·
    /// 경계 칸의 판 발자국 마스킹 - 시뮬레이션은 칸 단위고, 여기는 그림뿐이다.
    /// </summary>
    private static void PaintCellInto(Overlay overlay, HullStructure structure, int col, int row)
    {
        ShipGrid.Map map = overlay._designMap;

        if (!structure.TryGetRear(new Vector2Int(col, row), out HullStructure.RearCell wall))
            return;

        // **발자국 마스킹은 실루엣용이다 - 우주와 닿은 경계 칸에만 건다.** 안쪽 벽까지
        // 판 모양으로 깎으면 얇은 패널 뒤가 투명해져 외피 한가운데에 구멍이 뚫린다.
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

        // 세월. hp/maxHp가 곧 시간이다 - 상한 만큼 어두워지고, 반 넘게 상하면
        // 판과 같은 문법으로 가장자리부터 픽셀이 갉아먹힌다.
        float life = wall.maxHp > 0f ? Mathf.Clamp01(wall.hp / wall.maxHp) : 1f;
        float wear = Mathf.Lerp(0.30f, 1f, life);
        float k = 1 - (overlay.mine ? MineDarken : SkinDarken);
        float shade = k * wear;

        // 칸마다 고정된 씨앗. 프레임마다 다르면 갉힌 자리가 지글거린다.
        var grainRng = new DeterministicRng(Ballistics.Hash(col, row, 77));

        // 구멍 옆이면 그 방향 가장자리를 물어뜯는다.
        bool tornL = structure.RearTorn(new Vector2Int(col - 1, row));
        bool tornR = structure.RearTorn(new Vector2Int(col + 1, row));
        bool tornU = structure.RearTorn(new Vector2Int(col, row - 1));
        bool tornD = structure.RearTorn(new Vector2Int(col, row + 1));

        int strideX = overlay.texW * RearPPU;
        int heightPx = overlay.texH * RearPPU;
        int mapWidthPx = map.width * RearPPU;
        int mapHeightPx = map.height * RearPPU;
        int localCol = col - overlay.minCol;
        int localRow = row - overlay.minRow;

        for (int py = 0; py < RearPPU; py++)
        for (int px = 0; px < RearPPU; px++)
        {
            // grain은 마스킹 여부와 무관하게 픽셀마다 하나씩 뽑아야 한다 - 조건
            // 안에서 뽑으면 발자국이 있는 칸과 없는 칸의 무늬가 달라진다.
            float grain = grainRng.Next01();

            if (life < 0.5f && grain > life * 2f)
                continue;

            // 찢긴 이웃 쪽 가장자리일수록 살아남기 어렵다.
            const float Bite = 0.35f;
            float open = 1f;

            if (tornL) open = Mathf.Min(open, (px + 0.5f) / RearPPU / Bite);
            if (tornR) open = Mathf.Min(open, (RearPPU - px - 0.5f) / RearPPU / Bite);
            if (tornU) open = Mathf.Min(open, (py + 0.5f) / RearPPU / Bite);
            if (tornD) open = Mathf.Min(open, (RearPPU - py - 0.5f) / RearPPU / Bite);

            if (open < 1f && grain > open)
                continue;

            if (boundary && wall.footprint != null)
            {
                // 발자국은 칸 중심 기준 배 좌표계(y 위)다. 텍스처 py는 아래로
                // 가므로 y를 뒤집어 넣는다.
                var local = new Vector2(
                    (px + 0.5f) / RearPPU - 0.5f,
                    0.5f - (py + 0.5f) / RearPPU);

                if (!Ballistics.PolygonContains(wall.footprint, local))
                    continue;
            }

            // 원본 색은 설계도 좌표의 공유 캐시에서, 어둡기·마모는 여기서 곱한다.
            int artY = mapHeightPx - 1 - (row * RearPPU + py);
            Color32 raw = overlay.art[artY * mapWidthPx + (col * RearPPU + px)];

            int texY = heightPx - 1 - (localRow * RearPPU + py);

            overlay.pixels[texY * strideX + (localCol * RearPPU + px)] = new Color32(
                (byte)(raw.r * shade), (byte)(raw.g * shade), (byte)(raw.b * shade), raw.a);
        }
    }
}
