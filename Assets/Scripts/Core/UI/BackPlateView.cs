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
        public ShipGrid.Map _designMap; //immutable;    
        public Vector2 localOffset;     // 격자 한가운데의 선체 기준 자리
        public int currentRearCount;
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

        if (overlay._designMap != structure.DesignMap || structure.Rear.Count != overlay.currentRearCount)
        {
            overlay.currentRearCount = structure.Rear.Count;
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

        overlay.texture = new Texture2D(DM.width, DM.height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        overlay.pixels = new Color32[DM.width * DM.height];

        // 피벗은 한가운데. 모서리에 두면 부호나 축을 하나 틀려도 "조금 어긋난 그림"이라
        // 눈에 안 띄는데, 중심이면 대칭으로 틀어져서 바로 보인다. 실제로 처음엔 모서리
        // 피벗이었고 오버레이가 배 옆에 통째로 떠 있었다.
        var sprite = Sprite.Create(
            overlay.texture,
            new Rect(0f, 0f, DM.width, DM.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 1f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);

        overlay.renderer = go.AddComponent<SpriteRenderer>();
        overlay.renderer.sprite = sprite;
        overlay.renderer.sortingOrder = SortingOrder;

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

        for (int row = 0; row < map.height; row++)
        for (int col = 0; col < map.width; col++)
        {
            if (structure.HasRear(new Vector2Int(col, row)))
            {
                Vector2 uv = new Vector2((col + 0.5f) / map.width , 1f - (row + 0.5f) / map.height);
                Color color;

                if(structureTexture != null)
                    color = structureTexture.GetPixelBilinear(uv.x, uv.y);
                else
                {
                    color = Structure;
                }
                float k = 1-Darken;
                Color c = new Color(color.r * k, color.g * k, color.b * k, color.a);

                overlay.pixels[
                    (map.height - 1 - row) * map.width + col
                ] = c;
            }
        }

        if (draw)
        {
            overlay.texture.SetPixels32(overlay.pixels);
            overlay.texture.Apply(false);
        }
    }
}
