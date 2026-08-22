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
    // **비대칭 화면: 소속이 곧 렌더 모드다** (설계도: 비대칭 화면 - 내 배는 안, 적함은 밖).
    // 내 배(IsPlayerControlled) = 외피가 어둡게 뒤에 깔린 내부 단면. 적함·중립·잔해 =
    // 외피가 위로 올라와 얼굴이 되고, 상태를 구멍으로 읽는다. 전환 버튼은 없다 -
    // 파괴가 적함을 열고, Tab(RoomView)이 내 배 진단을 얹는다.
    private const float MineDarken = 0.5f;
    private const int MineOrder = -10;

    private const float SkinDarken = 0f;
    private const int SkinOrder = 100;

    /// <summary>
    /// 후면 텍스처의 칸당 픽셀. **1이면 마스킹 단위가 통째로 1 m 칸이다** - 선체 그림을
    /// 칸 중심에서 한 번만 찍으니 경사 실루엣 밖으로 사각 블록이 삐져나온다. 16이면
    /// 그림의 알파를 픽셀마다 읽어서 후면이 실루엣을 따라 잘린다.
    /// </summary>
    // 48로 올려봤지만 판과의 괴리는 해상도가 아니었다 - 후면은 어떻게 손상되든
    // 모든 픽셀이 온전해서(침식·그을음·적열이 없다) 재질이 달라 보이는 것이다.
    // 16이면 실루엣 마스킹에 충분하고 메모리도 9분의 1이다.
    private const int RearPPU = 16;

    private sealed class Overlay
    {
        /// <summary>이 오버레이가 내 배 것인가. Rebuild가 정하고 Paint가 읽는다.</summary>
        public bool mine;

        public SpriteRenderer renderer;
        public Texture2D texture;
        public Color32[] pixels;
        public ShipGrid.Map _designMap; //immutable;    
        public Vector2 localOffset;     // 격자 한가운데의 선체 기준 자리
        public int currentRearCount;
        public int currentRearVersion;
    }

    private readonly Dictionary<HullStructure, Overlay> _overlays = new();
    const bool _visible = true;

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
    }

    /// <summary>
    /// 그렸으면 true. 이 반환값이 곧 오버레이의 수명이다 - false를 돌려주면 호출자가
    /// 그 배의 오버레이를 파괴한다. 조기 리턴은 "못 그린다"가 아니라 "이 배에는 오버레이가
    /// 없다"라는 뜻이고, 조건이 하나 더 붙어도 청소가 저절로 따라온다.
    /// </summary>
    private bool Draw(HullStructure structure)
    {
        if (structure.DesignMap == null)
            return false;

        if (!_overlays.TryGetValue(structure, out Overlay overlay))
            _overlays[structure] = overlay = new Overlay();

        if (overlay._designMap != structure.DesignMap
            || structure.Rear.Count != overlay.currentRearCount
            || structure.RearVersion != overlay.currentRearVersion)
        {
            overlay.currentRearCount = structure.Rear.Count;
            overlay.currentRearVersion = structure.RearVersion;
            Rebuild(structure, overlay);
        }

        overlay.renderer.enabled = _visible;

        overlay.renderer.transform.SetPositionAndRotation(
            structure.transform.TransformPoint(overlay.localOffset),
            structure.transform.rotation);

        overlay.renderer.transform.localScale = structure.transform.lossyScale;

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

        overlay.texture = new Texture2D(DM.width * RearPPU, DM.height * RearPPU, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
        };

        overlay.pixels = new Color32[DM.width * RearPPU * DM.height * RearPPU];

        // 피벗은 한가운데. 모서리에 두면 부호나 축을 하나 틀려도 "조금 어긋난 그림"이라
        // 눈에 안 띄는데, 중심이면 대칭으로 틀어져서 바로 보인다. 실제로 처음엔 모서리
        // 피벗이었고 오버레이가 배 옆에 통째로 떠 있었다.
        var sprite = Sprite.Create(
            overlay.texture,
            new Rect(0f, 0f, DM.width * RearPPU, DM.height * RearPPU),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: RearPPU,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        // 소속 판정. Ship은 HullStructure와 같은 GameObject다(RequireComponent).
        // 잔해·Hulk는 Ship이 없으니 저절로 외피 쪽으로 떨어진다 - 내 배에서 떨어진
        // 조각이 그 순간 외피를 입는 것은 받아들인 결정이다(진단 밖: 선이 끊겼으니
        // 센서도 죽었다).
        overlay.mine = structure.TryGetComponent(out Ship ship) && ship.IsPlayerControlled;

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = sprite;
        overlay.renderer.sortingOrder = overlay.mine ? MineOrder : SkinOrder;

        overlay._designMap = DM;

        // 격자 한가운데의 선체 기준 좌표. 칸 (0,0)과 칸 (w-1,h-1)의 중점이고, ppu가 1이라
        // 그것이 곧 스프라이트의 중심이다.
        overlay.localOffset = DM.ToLocal(0, 0)
            + new Vector2((DM.width - 1) * 0.5f, -(DM.height - 1) * 0.5f);

        Paint(overlay, _visible, structure.ShipHullPng, structure);
    }

    private void Paint(Overlay overlay, bool draw, Texture2D structureTexture, HullStructure structure)
    {
        ShipGrid.Map map = overlay._designMap;

        System.Array.Clear(overlay.pixels, 0, overlay.pixels.Length);

        int widthPx = map.width * RearPPU;
        int heightPx = map.height * RearPPU;
        float k = 1 - (overlay.mine ? MineDarken : SkinDarken);

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (!structure.TryGetRear(new Vector2Int(col, row), out HullStructure.RearCell wall))
                continue;

            // **발자국 마스킹은 실루엣용이다 - 우주와 닿은 경계 칸에만 건다.**
            //
            // 안쪽 벽까지 판 모양으로 깎으면, 모듈 자리의 얇은 패널(콜라이더가 칸보다
            // 작은 판) 뒤가 투명해져서 외피 한가운데에 구멍이 뚫리고 내부 모듈이
            // 비친다. 안쪽 벽의 후면은 칸 전체가 맞다 - 시뮬레이션의 후면이 칸
            // 단위인 것과 같은 해상도다. 실루엣을 다듬어야 하는 곳은 우주에 보이는
            // 가장자리뿐이다.
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
            // 판과 같은 문법으로 가장자리부터 픽셀이 갉아먹힌다. 이게 없으면 후면은
            // 죽는 순간까지 새것이라 온전 -> 즉시 구멍으로 건너뛰고, 판만 삭아서
            // 그림에 시간이 안 흐른다.
            float life = wall.maxHp > 0f ? Mathf.Clamp01(wall.hp / wall.maxHp) : 1f;
            float wear = Mathf.Lerp(0.30f, 1f, life);

            // 칸마다 고정된 씨앗. 프레임마다 다르면 갉힌 자리가 지글거린다.
            var grainRng = new DeterministicRng(Ballistics.Hash(col, row, 77));

            // 구멍 옆이면 그 방향 가장자리를 물어뜯는다. 시뮬레이션은 칸 단위로 죽는 게
            // 맞지만(후면에 서브셀은 없다), 그림까지 1 m 정사각형으로 뚝 떨어지면 종이
            // 오리기처럼 보인다 - 찢긴 자리는 너덜너덜해야 한다.
            bool tornL = structure.RearTorn(new Vector2Int(col - 1, row));
            bool tornR = structure.RearTorn(new Vector2Int(col + 1, row));
            bool tornU = structure.RearTorn(new Vector2Int(col, row - 1));
            bool tornD = structure.RearTorn(new Vector2Int(col, row + 1));

            // 칸 안을 픽셀 단위로. **마스크는 그림이 아니라 판의 발자국이다** - 그림
            // 알파에 맡기면 텍스처가 없거나 못 읽어서 폴백(흰색, 알파 1)으로 넘어가는
            // 순간 칸 전체가 네모로 나온다. 발자국은 그 자리에 서 있던 판의 기하라
            // 텍스처와 무관하게 실루엣을 따라 잘린다. 시뮬레이션의 후면은 여전히
            // 칸 단위다 - 여기는 그림뿐이다.
            for (int py = 0; py < RearPPU; py++)
            for (int px = 0; px < RearPPU; px++)
            {
                // grain은 마스킹 여부와 무관하게 픽셀마다 하나씩 뽑아야 한다 - 조건
                // 안에서 뽑으면 발자국이 있는 칸과 없는 칸의 무늬가 달라진다.
                float grain = grainRng.Next01();

                if (life < 0.5f && grain > life * 2f)
                    continue;

                // 찢긴 이웃 쪽 가장자리일수록 살아남기 어렵다. Bite 폭 안에서 거리에
                // 비례해 확률이 떨어지므로 경계가 직선이 아니라 뜯긴 단면이 된다.
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

                Color color = structureTexture != null
                    ? structureTexture.GetPixelBilinear(
                        (col + (px + 0.5f) / RearPPU) / map.width,
                        1f - (row + (py + 0.5f) / RearPPU) / map.height)
                    : Structure;

                float shade = k * wear;

                // row도 칸 안의 py도 아래로 증가하고 텍스처 y는 위로 증가한다.
                int texY = heightPx - 1 - (row * RearPPU + py);

                overlay.pixels[texY * widthPx + (col * RearPPU + px)] =
                    new Color(color.r * shade, color.g * shade, color.b * shade, color.a);
            }
        }

        if (draw)
        {
            overlay.texture.SetPixels32(overlay.pixels);
            overlay.texture.Apply(false);
        }
    }
}
