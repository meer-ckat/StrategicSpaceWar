using UnityEngine;

/// <summary>
/// 장갑판의 그림. 고정 스프라이트가 아니라 콜라이더 모양 그대로 런타임에 굽고,
/// 서브셀이 깎이면 그 자리 픽셀을 지운다 - 뚫린 자리가 실제로 뚫려 보인다.
///
/// 판 모양은 콜라이더에게, 격자는 Armor.SubIndexAtLocal에게 물어본다. 둘 다 여기서
/// 다시 계산하지 않으므로, 나중에 서브셀을 콜라이더에서 동적으로 만들어도 이 파일은
/// 그대로다 - Rebuild()만 한 번 부르면 된다.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public sealed class ArmorSkin : MonoBehaviour
{
    [Header("Skin")]
    [SerializeField] private float pixelsPerUnit = 48f;

    /// <summary>텍스처 한 변의 상한. 큰 판이 실수로 4k 텍스처를 굽는 걸 막는다.</summary>
    [SerializeField] private int maxTextureSize = 256;

    [SerializeField] private Color healthy = Color.white;
    [SerializeField] private Color damaged = new(0.35f, 0.33f, 0.32f, 1f);

    /// <summary>
    /// HP가 이 아래로 내려간 서브셀부터 픽셀이 실제로 떨어져 나가기 시작한다. 1.0으로
    /// 두면 멀쩡한 판도 좀먹은 것처럼 보이고, 0으로 두면 구멍이 정확한 사각형이 된다.
    /// </summary>
    [SerializeField, Range(0f, 1f)] private float erodeBelow = 0.6f;

    private Armor _armor;
    private Collider2D _collider;
    private SpriteRenderer _renderer;

    private Texture2D _texture;
    private Sprite _sprite;
    private Color32[] _buffer;

    // 판 모양과 픽셀-서브셀 대응은 한 번만 구한다. OverlapPoint를 피격마다 4천 번씩
    // 부르면 그림 때문에 시뮬레이션이 느려진다.
    private bool[] _inside;
    private int[] _sub;
    private float[] _grain;

    private int _paintedVersion = -1;

    private void Start()
    {
        _armor = GetComponent<Armor>();
        _collider = GetComponent<Collider2D>();
        _renderer = GetComponent<SpriteRenderer>();

        if (_armor == null || _collider == null)
        {
            enabled = false;
            return;
        }

        Rebuild();
    }

    private void LateUpdate()
    {
        if (_armor.DamageVersion != _paintedVersion)
            Repaint();
    }

    /// <summary>
    /// 콜라이더나 서브셀 격자가 바뀌었을 때 부른다. 그 외에는 부를 일이 없다 -
    /// 피해는 Repaint만으로 충분하다.
    /// </summary>
    public void Rebuild()
    {
        LocalRect(out Vector2 centre, out Vector2 size);

        if (size.x <= 0f || size.y <= 0f)
        {
            enabled = false;
            return;
        }

        // 상한에 걸리면 해상도를 낮춘다. 텍스처를 자르면 판이 잘린다.
        float ppu = Mathf.Min(
            pixelsPerUnit,
            maxTextureSize / size.x,
            maxTextureSize / size.y);

        float pixel = 1f / ppu;
        int w = Mathf.Max(1, Mathf.CeilToInt(size.x * ppu));
        int h = Mathf.Max(1, Mathf.CeilToInt(size.y * ppu));

        // 픽셀 정수 개로 딱 떨어지게 넓혀 잡는다. 그래야 Sprite에 넘기는 ppu 하나로
        // 가로세로가 동시에 맞는다.
        Vector2 min = centre - new Vector2(w, h) * (pixel * 0.5f);

        _texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        _buffer = new Color32[w * h];
        _inside = new bool[w * h];
        _sub = new int[w * h];
        _grain = new float[w * h];

        // OverlapPoint는 물리 쪽이 아는 위치를 보는데, TickManager가 simulationMode를
        // Script로 잡아 두어서 Transform이 아직 안 넘어가 있을 수 있다.
        Physics2D.SyncTransforms();

        var rng = new DeterministicRng(Ballistics.Hash(GetInstanceID(), 0, 0));

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;

                Vector2 local = min + new Vector2((x + 0.5f) * pixel, (y + 0.5f) * pixel);

                _inside[i] = _collider.OverlapPoint(transform.TransformPoint(local));
                _sub[i] = _armor.SubIndexAtLocal(local);
                _grain[i] = rng.Next01();
            }
        }

        // FullRect - 기본값인 Tight는 생성 시점의 알파로 메시를 깎아서, 나중에 픽셀을
        // 지우면 잘려나간 자리에 구멍이 안 뚫린다.
        _sprite = Sprite.Create(
            _texture,
            new Rect(0f, 0f, w, h),
            new Vector2(-min.x / (w * pixel), -min.y / (h * pixel)),
            ppu,
            0,
            SpriteMeshType.FullRect);

        _renderer.sprite = _sprite;

        _paintedVersion = -1;
        Repaint();
    }

    private void Repaint()
    {
        _paintedVersion = _armor.DamageVersion;

        for (int i = 0; i < _buffer.Length; i++)
        {
            if (!_inside[i])
            {
                _buffer[i] = default; // (0,0,0,0)
                continue;
            }

            float f = _armor.HpFraction(_sub[i]);

            // 죽은 칸은 통째로, 깎인 칸은 가장자리부터 갉아먹힌 것처럼 사라진다.
            if (f <= 0f || (erodeBelow > 0f && _grain[i] > Mathf.InverseLerp(0f, erodeBelow, f)))
            {
                _buffer[i] = default;
                continue;
            }

            _buffer[i] = Color.Lerp(damaged, healthy, f);
        }

        _texture.SetPixels32(_buffer);
        _texture.Apply(false);
    }

    /// <summary>
    /// 콜라이더가 로컬 공간에서 차지하는 사각형. Box는 정확히, 나머지는 월드 AABB를
    /// 되돌려 넉넉하게 - 넘치는 만큼은 투명 여백이라 손해가 없다.
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

        if (_texture != null)
            Destroy(_texture);
    }
}
