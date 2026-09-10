using UnityEngine;

/// <summary>
/// 워프 화면 효과의 값. 연출(<see cref="WarpTransition"/>)이 프레임마다 쓰고
/// <see cref="WarpFxFeature"/>가 그릴 때 읽는다. 전부 0이면 패스 자체가 안 돈다 -
/// 워프 밖에서는 공짜다.
///
/// 세기의 서열은 GPT 검토대로다: 당김이 주역, 링이 악센트, 발광·RGB 갈림은 보조.
/// 전부 100 %로 걸면 워프가 아니라 레이어 47개짜리 인트로가 된다.
/// </summary>
public static class WarpFx
{
    private static Vector2 _centre;
    private static Vector2 _axis = Vector2.right;
    private static float _pull, _stretch, _glow;
    private static float _ring, _ringWidth, _ringPush, _ringSplit;
    private static float _bubbleRadius, _bubbleWall, _bubbleDrive, _bubbleShift;
    private static Vector2 _bubbleCentre;
    private static float _transitDark;

    private static readonly int CentreId = Shader.PropertyToID("_WarpCentre");
    private static readonly int ParamsId = Shader.PropertyToID("_WarpParams");
    private static readonly int RingId = Shader.PropertyToID("_WarpRing");
    private static readonly int BubbleId = Shader.PropertyToID("_WarpBubble");
    private static readonly int BubbleCentreId = Shader.PropertyToID("_WarpBubble2");

    public static bool Active =>
        _pull > 0.0005f || _stretch > 0.0005f || _glow > 0.0005f || _ringPush > 0.0005f || _ringSplit > 0.0005f
        || _bubbleDrive > 0.0005f || _transitDark > 0.0005f;

    /// <summary>배 자리와 축(월드). 당김·늘림·발광은 0~1 정도의 값이다.</summary>
    public static void Set(Vector2 worldCentre, Vector2 worldAxis, float pull, float stretch, float glow)
    {
        _centre = worldCentre;
        _axis = worldAxis.sqrMagnitude > 1e-6f ? worldAxis.normalized : Vector2.right;
        _pull = pull;
        _stretch = stretch;
        _glow = glow;
    }

    /// <summary>충격 링. 반지름·폭은 화면 세로 = 1 단위. push·split도 같은 단위라 0.02면 화면의 2 %다.</summary>
    public static void Ring(float radius, float width, float push, float split)
    {
        _ring = radius;
        _ringWidth = width;
        _ringPush = push;
        _ringSplit = split;
    }

    /// <summary>
    /// 알쿠비에레 거품. radius·벽 폭은 화면 세로 = 1 단위, drive는 벽에서의 변위량(0.02면 화면의 2 %),
    /// shift는 앞뒤 도플러 색 갈림. 안쪽은 평평하다 - 배는 절대 안 일그러진다.
    /// </summary>
    public static void Bubble(Vector2 worldCentre, float radius, float wallWidth, float drive, float shift)
    {
        _bubbleCentre = worldCentre;
        _bubbleRadius = radius;
        _bubbleWall = 1f / Mathf.Max(wallWidth, 1e-3f);
        _bubbleDrive = drive;
        _bubbleShift = shift;
    }

    /// <summary>이동 중. 거품 밖을 이만큼(0~1) 지운다. 1이면 거품 안의 배만 남는다.</summary>
    public static void Transit(float darkness) => _transitDark = Mathf.Clamp01(darkness);

    public static void Off()
    {
        _pull = _stretch = _glow = 0f;
        _ringPush = _ringSplit = 0f;
        _bubbleDrive = _bubbleShift = 0f;
        _transitDark = 0f;
    }

    /// <summary>재질에 붓는다. 월드 → 뷰포트는 여기서 한 번. 직교 카메라라 축은 방향 그대로다.</summary>
    internal static void Apply(Material material, Camera camera)
    {
        Vector3 viewport = camera.WorldToViewportPoint(new Vector3(_centre.x, _centre.y, 0f));
        float aspect = camera.pixelHeight > 0 ? (float)camera.pixelWidth / camera.pixelHeight : 1.78f;

        material.SetVector(CentreId, new Vector4(viewport.x, viewport.y, _axis.x, _axis.y));
        material.SetVector(ParamsId, new Vector4(_pull, _stretch, _glow, aspect));
        material.SetVector(RingId, new Vector4(_ring, _ringWidth, _ringPush, _ringSplit));
        material.SetVector(BubbleId, new Vector4(_bubbleRadius, _bubbleWall, _bubbleDrive, _bubbleShift));
        Vector3 bubble = camera.WorldToViewportPoint(new Vector3(_bubbleCentre.x, _bubbleCentre.y, 0f));
        material.SetVector(BubbleCentreId, new Vector4(bubble.x, bubble.y, _transitDark, 0f));
    }
}
