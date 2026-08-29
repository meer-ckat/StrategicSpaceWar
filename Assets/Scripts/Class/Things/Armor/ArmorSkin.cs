using UnityEngine;

/// <summary>
/// 장갑판의 그림 - GPU판. 판은 자기 텍스처를 굽지 않는다. 배 그림(함선 공유 텍스처)과
/// 6x6 손상 마스크(서브셀 HP)를 PlateSkin 셰이더가 프래그먼트에서 조합한다.
/// CPU가 픽셀을 만지는 곳은 피해 시 마스크 36바이트 갱신과, 폴리곤 판의 실루엣 마스크
/// 1회 굽기뿐이다.
///
/// **내 배의 판만 선체 그림을 입는다.** 비대칭 화면의 연장이다 - 내 배는 외피가 어둡게
/// 뒤로 빠지므로 판이 그림을 입어도 충돌할 상대가 없다. 적함 판은 절차 색 뼈대로 남는다.
///
/// 규칙의 원본은 여전히 C#이다: 서브셀 격자(Armor.CellOffset/CellSize), 판 사각형
/// (콜라이더), 실물 모양(Armor.InsideShape), 판->배 그림 대응(로컬 아핀)을 여기서
/// 계산해 상수·마스크로 셰이더에 넘긴다. 셰이더는 숫자를 조합만 한다.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public sealed class ArmorSkin : MonoBehaviour
{
    [Header("Skin")]
    // 이제 그림 해상도가 아니라 erode 노이즈의 눈금이다. def JSON이 이 키를 쓰므로 이름 유지.
    [SerializeField] private float pixelsPerUnit = 48f;

    /// <summary>안 쓴다. def JSON이 이 키를 적어 두어서 지우면 검증이 def를 통째로 거부한다.</summary>
    [SerializeField] private int maxTextureSize = 256;

    [SerializeField] private Color healthy = Color.white;
    [SerializeField] private Color damaged = new(0.35f, 0.33f, 0.32f, 1f);
    [SerializeField, Range(0f, 1f)] private float erodeBelow = 0.6f;

    /// <summary>쿼드 픽셀 눈금. 그림 해상도가 아니라 스프라이트 사각형의 반올림 단위다.</summary>
    private const float QuadPpu = 32f;
    private const int SharedTexSize = 256;   // 최대 판 8m. 콜라이더 상한 2x2의 대각도 여유

    /// <summary>폴리곤 실루엣 마스크 한 변. 2m 판 기준 32px/m - 눈에 보이는 톱니가 없는 선.</summary>
    private const int ShapeMaskSize = 64;

    private static Texture2D _sharedWhite;
    private static Material _sharedMaterial;

    private static readonly int HullTexId = Shader.PropertyToID("_HullTex");
    private static readonly int DamageMaskId = Shader.PropertyToID("_DamageMask");
    private static readonly int ShapeMaskId = Shader.PropertyToID("_ShapeMask");
    private static readonly int ShapeScaleId = Shader.PropertyToID("_ShapeScale");
    private static readonly int HasShapeId = Shader.PropertyToID("_HasShape");
    private static readonly int LocalMinId = Shader.PropertyToID("_LocalMin");
    private static readonly int RectId = Shader.PropertyToID("_Rect");
    private static readonly int CellId = Shader.PropertyToID("_Cell");
    private static readonly int HullAId = Shader.PropertyToID("_HullA");
    private static readonly int HullBId = Shader.PropertyToID("_HullB");
    private static readonly int DamagedId = Shader.PropertyToID("_Damaged");
    private static readonly int HealthyId = Shader.PropertyToID("_Healthy");
    private static readonly int ErodeBelowId = Shader.PropertyToID("_ErodeBelow");
    private static readonly int HasHullId = Shader.PropertyToID("_HasHull");
    private static readonly int GrainSeedId = Shader.PropertyToID("_GrainSeed");
    private static readonly int GrainPpuId = Shader.PropertyToID("_GrainPpu");
    private static readonly int HeatId = Shader.PropertyToID("_Heat");

    private Armor _armor;
    private Collider2D _collider;
    private SpriteRenderer _renderer;
    private ShipGrid.Map _map;
    private Texture2D _shipHullTexture;

    private Sprite _sprite;
    private Texture2D _mask;
    private byte[] _maskBytes;
    private Texture2D _shapeMask;
    private MaterialPropertyBlock _props;

    private int _paintedVersion = -1;
    private bool _built;

    /// <summary>
    /// 마지막으로 셰이더에 넘긴 열. 이 값이 안 변하면 SetPropertyBlock을 안 부른다 -
    /// 안 뜨거운 판이 절대 다수라 그 판들은 float 비교 하나로 끝난다.
    /// </summary>
    private float _sentHeat = -1f;

    private void Start()
    {
        _armor = GetComponent<Armor>();
        _collider = GetComponent<Collider2D>();
        _renderer = GetComponent<SpriteRenderer>();

        // **내 배의 판만 선체 그림을 입는다** (비대칭 화면). 적함 판은 절차 색 뼈대.
        // 타이밍은 안전하다: 여기는 Start라 Ship.Awake(Map 생성)가 이미 끝나 있다.
        Ship ship = GetComponentInParent<Ship>();

        if (ship != null && ship.IsPlayerControlled && ship.ShipHullPng != null && ship.Map != null)
        {
            _shipHullTexture = ship.ShipHullPng;
            _map = ship.Map;
        }

        if (_armor == null || _collider == null)
        {
            enabled = false;
            return;
        }

        Rebuild();
    }

    private void LateUpdate()
    {
        if (!_built)
            return;

        if (_armor.DamageVersion != _paintedVersion)
            UpdateMask();

        // **열은 SpriteRenderer.color로 안 보낸다.** 그 경로는 HDR을 못 통과시켜서 1을
        // 넘는 성분이 잘리고, 잘리면 Bloom 문턱을 못 넘어 발광이 통째로 사라진다. 예전
        // CPU 굽기가 됐던 것은 텍스처 픽셀에 HDR을 직접 썼기 때문이다. 지금은 값 하나만
        // 넘기고 1을 넘는 색은 PlateSkin이 만든다 - 후면(RearSkin)도 같은 방식이다.
        float heat = Mathf.Clamp01(_armor.Heat);

        if (Mathf.Abs(heat - _sentHeat) < 0.004f)
            return;

        _sentHeat = heat;
        _props.SetFloat(HeatId, heat);
        _renderer.SetPropertyBlock(_props);
    }

    /// <summary>
    /// 콜라이더나 서브셀 격자가 바뀌었을 때 부른다. 픽셀 루프가 폴리곤 실루엣 마스크
    /// 하나뿐이라(그것도 폴리곤 판만) 소환 프레임에 판 수백 장을 지어도 스파이크가 없다.
    /// </summary>
    public void Rebuild()
    {
        LocalRect(out Vector2 centre, out Vector2 size);

        if (size.x <= 0f || size.y <= 0f)
        {
            enabled = false;
            return;
        }

        EnsureShared();

        if (_sharedMaterial == null)
        {
            enabled = false;
            return;
        }

        // 픽셀 정수 개로 딱 떨어지게 넓혀 잡고, 넘치는 만큼은 셰이더의 사각형 검사가
        // 잘라낸다 - CPU판의 투명 여백과 같은 역할이다.
        float pixel = 1f / QuadPpu;
        int w = Mathf.Clamp(Mathf.CeilToInt(size.x * QuadPpu), 1, SharedTexSize);
        int h = Mathf.Clamp(Mathf.CeilToInt(size.y * QuadPpu), 1, SharedTexSize);
        Vector2 min = centre - new Vector2(w, h) * (pixel * 0.5f);
        var quadSize = new Vector2(w * pixel, h * pixel);

        if (_sprite != null)
            Destroy(_sprite);

        // 텍스처는 전 판이 공유하는 흰 판때기다. 스프라이트는 지오메트리(사각형 + 피벗)만 준다.
        _sprite = Sprite.Create(
            _sharedWhite,
            new Rect(0f, 0f, w, h),
            new Vector2(-min.x / quadSize.x, -min.y / quadSize.y),
            QuadPpu,
            0,
            SpriteMeshType.FullRect);

        _renderer.sprite = _sprite;
        _renderer.sharedMaterial = _sharedMaterial;

        if (_mask == null)
        {
            _mask = new Texture2D(Ballistics.SubGrid, Ballistics.SubGrid, TextureFormat.R8, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _maskBytes = new byte[Armor.SubCount];
        }

        _props ??= new MaterialPropertyBlock();

        // uv(0..w/256) -> 판 로컬 미터. 사각형이 (0,0)에서 시작하므로 배율 하나로 끝난다.
        _props.SetVector(LocalMinId, new Vector4(min.x, min.y, SharedTexSize / QuadPpu, SharedTexSize / QuadPpu));
        _props.SetVector(RectId, new Vector4(centre.x, centre.y, size.x * 0.5f, size.y * 0.5f));
        _props.SetVector(CellId, new Vector4(
            _armor.CellOffset.x, _armor.CellOffset.y,
            _armor.CellSize.x, _armor.CellSize.y));
        _props.SetColor(DamagedId, damaged);
        _props.SetColor(HealthyId, healthy);
        _props.SetFloat(ErodeBelowId, erodeBelow);
        _props.SetFloat(GrainPpuId, pixelsPerUnit);
        _props.SetTexture(DamageMaskId, _mask);

        // erode 무늬 씨앗 - 결정론 규약 그대로 stableId, 씬 저작 배만 인스턴스 ID 폴백.
        Debug.Assert(_armor.stableId >= 0,
            $"[ArmorSkin] '{_armor.defName}'에 stableId가 없다. def로 안 지어진 배다.", this);
        var rng = new DeterministicRng(
            Ballistics.Hash(_armor.stableId < 0 ? _armor.GetInstanceID() : _armor.stableId, 0, 0));
        _props.SetVector(GrainSeedId, new Vector4(rng.Range(0f, 4096f), rng.Range(0f, 4096f), 0f, 0f));

        // 판의 실물 모양(폴리곤). 셰이더가 매 픽셀 다각형을 풀 수는 없으니 실루엣을
        // 마스크 한 장으로 여기서 한 번 굽는다 - InsideShape가 원본이고, 이 마스크는
        // 그 수식의 캐시다. 사각형 판은 마스크 자체가 없다.
        bool hasShape = _armor.Shape != null && _armor.Shape.Length >= 3;

        if (hasShape)
        {
            if (_shapeMask == null)
            {
                _shapeMask = new Texture2D(ShapeMaskSize, ShapeMaskSize, TextureFormat.R8, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            var bytes = new byte[ShapeMaskSize * ShapeMaskSize];

            for (int y = 0; y < ShapeMaskSize; y++)
            for (int x = 0; x < ShapeMaskSize; x++)
            {
                var local = new Vector2(
                    min.x + (x + 0.5f) / ShapeMaskSize * quadSize.x,
                    min.y + (y + 0.5f) / ShapeMaskSize * quadSize.y);

                bytes[y * ShapeMaskSize + x] = _armor.InsideShape(local) ? (byte)255 : (byte)0;
            }

            _shapeMask.SetPixelData(bytes, 0);
            _shapeMask.Apply(false);
            _props.SetTexture(ShapeMaskId, _shapeMask);
            _props.SetVector(ShapeScaleId, new Vector4(1f / quadSize.x, 1f / quadSize.y, 0f, 0f));
        }
        else
        {
            _props.SetTexture(ShapeMaskId, _sharedWhite);
        }

        _props.SetFloat(HasShapeId, hasShape ? 1f : 0f);

        // 판 로컬 -> 배 그림 uv 아핀. 판은 선체 직속 자식이라 localRotation/localPosition이
        // 곧 배 좌표이고, 잔해로 재부모화돼도 이 둘은 안 변한다(칸 좌표 불변식) - 갱신 불필요.
        bool hasHull = _shipHullTexture != null && _map != null;

        if (hasHull)
        {
            Vector2 rx = transform.localRotation * Vector2.right;
            Vector2 ry = transform.localRotation * Vector2.up;
            Vector2 lp = transform.localPosition;
            float mw = _map.width;
            float mh = _map.height;

            _props.SetTexture(HullTexId, _shipHullTexture);
            _props.SetVector(HullAId, new Vector4(rx.x / mw, ry.x / mw, rx.y / mh, ry.y / mh));
            _props.SetVector(HullBId, new Vector4((lp.x + mw * 0.5f) / mw, (lp.y + mh * 0.5f) / mh, 0f, 0f));
        }
        else
        {
            _props.SetTexture(HullTexId, _sharedWhite);
        }

        _props.SetFloat(HasHullId, hasHull ? 1f : 0f);

        _renderer.SetPropertyBlock(_props);

        _built = true;
        _paintedVersion = -1;
        UpdateMask();
    }

    /// <summary>서브셀 HP -> 마스크 36바이트. 피해가 있을 때만 불리고, 이게 CPU 몫의 전부다.</summary>
    private void UpdateMask()
    {
        _paintedVersion = _armor.DamageVersion;
        _armor.ConsumeDirtySubs();

        for (int i = 0; i < Armor.SubCount; i++)
            _maskBytes[i] = (byte)(Mathf.Clamp01(_armor.HpFraction(i)) * 255f);

        _mask.SetPixelData(_maskBytes, 0);
        _mask.Apply(false);
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
            Shader shader = Shader.Find("SUPERRADIANCE/PlateSkin");

            if (shader == null)
            {
                Debug.LogError("[ArmorSkin] PlateSkin 셰이더를 못 찾았다. 빌드라면 " +
                    "Always Included Shaders에 넣었는지 확인할 것.");
                return;
            }

            _sharedMaterial = new Material(shader);
        }
    }

    /// <summary>
    /// 콜라이더가 로컬 공간에서 차지하는 사각형. Box는 정확히, 나머지는 월드 AABB를
    /// 되돌려 넉넉하게 - 넘치는 만큼은 셰이더의 사각형·실루엣 검사가 잘라낸다.
    /// </summary>
    private void LocalRect(out Vector2 centre, out Vector2 size)
    {
        if (_collider is BoxCollider2D box)
        {
            centre = box.offset;
            size = box.size;
            return;
        }

        Bounds b = _collider.bounds;
        Vector2 min = new(float.MaxValue, float.MaxValue);
        Vector2 max = new(float.MinValue, float.MinValue);

        for (int i = 0; i < 4; i++)
        {
            Vector3 corner = new(
                (i & 1) == 0 ? b.min.x : b.max.x,
                (i & 2) == 0 ? b.min.y : b.max.y,
                0f);

            Vector2 local = transform.InverseTransformPoint(corner);

            min = Vector2.Min(min, local);
            max = Vector2.Max(max, local);
        }

        centre = (min + max) * 0.5f;
        size = max - min;
    }

    private void OnDestroy()
    {
        if (_sprite != null)
            Destroy(_sprite);

        if (_mask != null)
            Destroy(_mask);

        if (_shapeMask != null)
            Destroy(_shapeMask);
    }
}
