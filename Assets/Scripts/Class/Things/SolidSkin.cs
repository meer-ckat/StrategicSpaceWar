using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 장갑판이 아닌 것들의 그림. def가 지목한 PNG 한 장이고, 없으면 예전처럼 단색 사각형이다.
///
/// **그림의 주인은 def지 배가 아니다.** 판은 <see cref="ArmorSkin"/>이 선체 그림(hullSkin)에서
/// 자기 조각을 굽는데, 그것이 맞는 이유는 판이 곧 선체이기 때문이다. 모듈은 선체가 아니라
/// 선체에 볼트로 붙은 남이다 - 엔진 하나를 배 그림에 그려 넣으면 같은 엔진의 그림이 배 파일
/// 열두 개에 흩어지고(lance는 엔진이 서른 개다), 배치를 한 칸 옮길 때마다 그림이 틀어지고,
/// <c>Placement.size</c>로 크기를 덮어쓰면 엉뚱한 자리 픽셀이 튀어나온다. "물건 한 종류가
/// 파일 하나"라는 이 리포의 뼈대가 그림에도 그대로 간다.
///
/// 피해는 <see cref="SpriteRenderer.color"/>다. 판은 서브셀마다 체력이 달라 구멍이 실제
/// 자리에 뚫려야 하지만, 모듈과 탄은 체력이 하나뿐이라 어두워지는 것으로 충분하다.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public sealed class SolidSkin : MonoBehaviour
{
    /// <summary>
    /// def 폴더에 있는 PNG 파일 이름. 비어 있으면 단색으로 내려간다.
    ///
    /// **가로는 콜라이더와 정확히 같아야 하고, 세로는 그 이상이면 된다.** 축척은
    /// <see cref="ShipDef.PPU"/>로 고정이라 1 m = 96 px다. 남는 세로는 위로 뻗는다 -
    /// 그림의 아래 끝이 콜라이더의 아래 끝이고, 포신은 그 위로 자란다. 이 규칙 하나가
    /// pivot을 정하므로 def에 pivot을 적을 필요가 없다.
    /// </summary>
    [SerializeField] private string skinTexture;

    /// <summary>단색일 때의 색. PNG를 쓰면 이 값은 안 읽는다 - 그림이 이미 색을 들고 있다.</summary>
    [SerializeField] private Color tint = Color.white;

    /// <summary>
    /// 콜라이더가 없을 때 쓸 크기. 탄이 이 경우다 - 레이캐스트로 판정해서 콜라이더가 없다.
    /// </summary>
    [SerializeField] private Vector2 skinSize = new(0.3f, 0.3f);

    /// <summary>
    /// 그리기 순서. **판(0)과 모듈이 같은 값이면 순서가 정의되지 않는다** - Unity는 동률을
    /// 카메라 거리와 인스턴스 ID로 가르므로 실행마다 달라질 수 있고, 증상은 "가끔 포탑이
    /// 판 밑으로 들어간다"라 재현이 안 된다. GUIManager의 불안정 정렬과 같은 병이다.
    ///
    /// 쓰는 값: 후면 -10 / 판 0 / 포대 10 / 터렛 20 / 적함 외피 100. 간격이 10인 것은
    /// 나중에 사이에 뭘 끼울 자리를 남긴 것이다.
    /// </summary>
    [SerializeField] private int sortingOrder = 10;

    private static Sprite _box;
    private static readonly Dictionary<string, Texture2D> _pngs = new();
    private static readonly Dictionary<string, Sprite> _sprites = new();

    private SpriteRenderer _renderer;
    private IDamageable _damageable;
    private Color _base = Color.white;
    private float _painted = -1f;

    /// <summary>
    /// 런타임에 만든 자식(포탑의 터렛 같은 것)을 def 없이 설정한다. **켜기 전에 부른다** -
    /// Start가 이 값들을 읽으므로, 활성 상태에서 붙이면 빈 값으로 한 번 그려진다.
    /// ThingDef.Spawn이 "붙이는 순서가 아니라 켜는 순서"를 지키는 것과 같은 이유다.
    /// </summary>
    public void Configure(string texture, Vector2 size, Color colour, int order)
    {
        skinTexture = texture;
        skinSize = size;
        tint = colour;
        sortingOrder = order;
    }

    private void Start()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _renderer.sortingOrder = sortingOrder;

        // 자기에게 없으면 부모에게 묻는다. 터렛은 체력을 따로 안 들지만(피해는 포대가
        // 받는다) 포대가 상하면 같이 어두워져야 한 덩어리로 읽힌다.
        _damageable = GetComponent<IDamageable>() ?? GetComponentInParent<IDamageable>();

        // 콜라이더의 bounds가 아니라 size다. bounds는 월드 기준이라 회전한 판이 1.41배로
        // 부풀어 그려진다. ThingDef가 붙이는 것은 박스뿐이므로 다른 종류는 볼 일이 없다.
        Vector2 size = TryGetComponent(out BoxCollider2D box) ? box.size : skinSize;

        Sprite drawn = FromPng(size);

        if (drawn != null)
        {
            // Simple이어야 한다. Sliced는 renderer.size로 늘리는데, 구운 그림은 세계 크기가
            // 이미 (픽셀 / ppu)로 정해져 있어서 또 늘리면 두 번 스케일된다.
            _renderer.drawMode = SpriteDrawMode.Simple;
            _renderer.sprite = drawn;

            // 그림이 색을 들고 있으므로 def의 tint를 곱하면 두 번 칠하는 것이 된다.
            // 흰색을 기준으로 삼아야 체력 어두워지기만 남는다.
            _base = Color.white;
        }
        else
        {
            _renderer.sprite = SharedBox();
            _renderer.drawMode = SpriteDrawMode.Sliced;
            _renderer.size = size;
            _base = tint;
        }

        Repaint();
    }

    private void LateUpdate()
    {
        if (_damageable != null && !Mathf.Approximately(_damageable.Health01, _painted))
            Repaint();
    }

    private void Repaint()
    {
        float health = _damageable?.Health01 ?? 1f;

        _painted = health;
        // 죽은 색을 따로 받지 않는다. 같은 색을 태운 것이라 끝까지 같은 재료로 읽힌다.
        _renderer.color = Color.Lerp(_base * 0.2f, _base, health);
    }

    /// <summary>
    /// def가 지목한 PNG. 크기가 안 맞으면 **에러를 찍고 null을 준다** - JsonUtility가 모르는
    /// 키를 조용히 버리는 것을 <see cref="ThingDef.Validate"/>가 막는 것과 같은 태도다.
    /// 조용히 늘려 쓰면 증상이 "포신이 좀 짧은 것 같은데"가 되고, 그건 못 찾는다.
    /// </summary>
    private Sprite FromPng(Vector2 size)
    {
        if (string.IsNullOrEmpty(skinTexture))
            return null;

        // pivot이 콜라이더 높이에 달려 있어서, 같은 PNG라도 크기가 다르면 다른 스프라이트다.
        string key = $"{skinTexture}:{size.x:F2}x{size.y:F2}";

        // 플레이 모드를 나가면 런타임 스프라이트가 파괴돼 항목이 가짜 null이 된다.
        if (_sprites.TryGetValue(key, out Sprite cached) && cached != null)
            return cached;

        Texture2D texture = Png(skinTexture);

        if (texture == null)
            return null;

        if (!Fits(size, texture.width, texture.height, out string reason))
        {
            Debug.LogError($"[SolidSkin] {name}의 '{skinTexture}': {reason}", this);
            return null;
        }

        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, PivotY(size.y, texture.height)),
            ShipDef.PPU,
            0,
            SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;

        _sprites[key] = sprite;

        return sprite;
    }

    /// <summary>
    /// 그림의 아래 끝이 콜라이더의 아래 끝이라는 규칙에서 나오는 pivot. 회전축(오브젝트 원점)은
    /// 그 아래 끝에서 콜라이더 높이의 절반만큼 위에 있다. PNG가 콜라이더와 같은 높이면 0.5다.
    /// </summary>
    internal static float PivotY(float sizeY, int pngHeight)
        => pngHeight > 0 ? sizeY * 0.5f / (pngHeight / (float)ShipDef.PPU) : 0.5f;

    /// <summary>
    /// 이 콜라이더(와 포신)가 요구하는 PNG 픽셀 크기.
    ///
    /// **템플릿 생성기와 검사가 이 한 함수를 같이 쓴다.** 두 벌로 두면 "템플릿대로 그렸는데
    /// 거부당한다"가 생기고, 그때 누가 틀렸는지 알 방법이 없다.
    /// </summary>
    internal static Vector2Int WantedPixels(Vector2 size, float barrel) => new(
        Mathf.RoundToInt(size.x * ShipDef.PPU),
        Mathf.RoundToInt((size.y + Mathf.Max(0f, barrel)) * ShipDef.PPU));

    /// <summary>
    /// 가로는 딱 맞아야 하고 세로는 남아도 된다. 남는 세로가 포신이다.
    /// 반 픽셀은 봐준다 - 0.4 m 짜리 판처럼 정수 픽셀로 안 떨어지는 크기가 있다.
    /// </summary>
    internal static bool Fits(Vector2 size, int pngWidth, int pngHeight, out string reason)
    {
        Vector2Int want = WantedPixels(size, 0f);
        int wantW = want.x;
        int wantH = want.y;

        if (Mathf.Abs(pngWidth - wantW) > 1)
        {
            reason = $"가로가 {pngWidth} px인데 콜라이더({size.x} m)는 {wantW} px를 원한다.";
            return false;
        }

        if (pngHeight < wantH - 1)
        {
            reason = $"세로가 {pngHeight} px인데 콜라이더({size.y} m)만 해도 {wantH} px다.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>def 폴더의 PNG 한 장. 같은 파일을 여러 def가 써도 한 번만 읽는다.</summary>
    private static Texture2D Png(string file)
    {
        if (_pngs.TryGetValue(file, out Texture2D cached) && cached != null)
            return cached;

        string path = Path.Combine(DefDatabase.DefDirectory, file);

        if (!File.Exists(path))
        {
            Debug.LogError($"[SolidSkin] '{file}'이 없다: {path}");
            _pngs[file] = null;
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        if (!texture.LoadImage(File.ReadAllBytes(path)))
        {
            Debug.LogError($"[SolidSkin] '{file}'을 PNG로 못 읽는다: {path}");
            Destroy(texture);
            _pngs[file] = null;
            return null;
        }

        _pngs[file] = texture;

        return texture;
    }

    /// <summary>1x1 흰 픽셀 한 장. 단색으로 내려온 것 전부가 이것을 공유한다.</summary>
    private static Sprite SharedBox()
    {
        if (_box != null)
            return _box;

        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        tex.SetPixel(0, 0, Color.white);
        tex.Apply(false);

        // FullRect - 기본값 Tight는 알파로 메시를 깎아서 Sliced가 늘릴 사각형이 없다고
        // 경고한다. ArmorSkin이 같은 이유로 같은 값을 넘긴다.
        _box = Sprite.Create(
            tex,
            new Rect(0f, 0f, 1f, 1f),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 1f,
            extrude: 0,
            meshType: SpriteMeshType.FullRect);
        _box.hideFlags = HideFlags.HideAndDontSave;

        return _box;
    }
}
