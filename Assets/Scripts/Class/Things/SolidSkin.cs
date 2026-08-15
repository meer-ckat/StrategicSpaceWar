using UnityEngine;

/// <summary>
/// 장갑판이 아닌 것들의 그림. 콜라이더 크기만 한 단색 사각형이고, 부서질수록 어두워진다.
///
/// <see cref="ArmorSkin"/>과 달리 픽셀을 굽지 않는다. 판은 서브셀마다 다른 체력을 가져서
/// 구멍이 실제 자리에 뚫려야 하지만, 모듈과 탄은 체력이 하나뿐이라 픽셀을 나눌 이유가 없다.
/// 단색 한 장을 늘려 쓰는 것으로 충분하고, 텍스처도 프로젝트 전체가 하나를 공유한다.
///
/// 스프라이트 자산은 없다. def의 색이 곧 아트다.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public sealed class SolidSkin : MonoBehaviour
{
    [SerializeField] private Color tint = Color.white;

    /// <summary>완전히 망가졌을 때의 색. IDamageable이 아니면 안 쓴다.</summary>
    [SerializeField] private Color dead = new(0.18f, 0.16f, 0.15f, 1f);

    /// <summary>
    /// 콜라이더가 없을 때 쓸 크기. 탄이 이 경우다 - 레이캐스트로 판정해서 콜라이더가 없다.
    /// </summary>
    [SerializeField] private Vector2 skinSize = new(0.3f, 0.3f);

    private static Sprite _shared;

    private SpriteRenderer _renderer;
    private IDamageable _damageable;
    private float _painted = -1f;

    private void Start()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _damageable = GetComponent<IDamageable>();

        // 1x1 흰 픽셀 한 장을 프로젝트가 공유한다. Sliced가 그걸 원하는 크기로 늘려준다 -
        // 단색 사각형에 텍스처를 따로 구울 이유가 없다.
        if (_shared == null)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false);

            // FullRect - 기본값 Tight는 알파로 메시를 깎아서 Sliced가 늘릴 사각형이 없다고
            // 경고한다. ArmorSkin이 같은 이유로 같은 값을 넘긴다.
            _shared = Sprite.Create(
                tex,
                new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: 1f,
                extrude: 0,
                meshType: SpriteMeshType.FullRect);
            _shared.hideFlags = HideFlags.HideAndDontSave;
        }

        Vector2 size = TryGetComponent(out Collider2D col) ? (Vector2)col.bounds.size : skinSize;

        // bounds는 월드 기준이라 회전한 판이면 부풀어 있다. 로컬 크기가 필요하므로 박스면
        // 직접 읽는다 - 45도 경사판이 1.41배로 그려지면 안 된다.
        if (col is BoxCollider2D box)
            size = box.size;

        _renderer.sprite = _shared;
        _renderer.drawMode = SpriteDrawMode.Sliced;
        _renderer.size = size;

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
        _renderer.color = Color.Lerp(dead, tint, health);
    }
}
