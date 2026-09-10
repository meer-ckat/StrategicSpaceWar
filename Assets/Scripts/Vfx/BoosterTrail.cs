using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 부스터가 뱉는 불꽃 전부. 전부 그림이다 - 이 파일이 통째로 없어져도 판정은 똑같이 돌아간다.
///
/// 왜 생겼나: 예전에는 불꽃 하나가 <see cref="Flash"/> + URP <c>Light2D</c>를 단 GameObject였고,
/// 부스터마다 45 ms에 하나씩 태어났다. destroyer가 부스터 12개, lance가 16개라 아군 편대가
/// 한 번 밟으면 초당 1,300개가 생성·파괴되고 동시 생존 2D 라이트가 수백 개다. 2D 라이트는
/// 개당 라이트 텍스처에 그려지므로 그 수가 곧 프레임이었다.
///
/// 구조는 <see cref="SpallTrails"/>와 같은 규칙 - **링버퍼 하나에 쌓고 한 번에 그린다.**
/// 다른 점은 CPU가 매 프레임 되짚지 않는다는 것이다: 불꽃이 얼마나 퍼졌고 얼마나 식었는지는
/// 정점 셰이더가 <c>_Now - born</c>으로 계산하고, C#은 새로 뱉은 것을 적을 때만 버퍼를
/// 올린다. 죽은 슬롯은 셰이더가 클립 밖으로 보내 지우므로 청소 루프도 없다.
///
/// 불빛은 사라지지 않았다 - 노즐 자체의 <c>Light2D</c>는 부스터 모듈에 붙은 채로 남는다
/// (<see cref="BoosterComp.Glow"/>). 없어진 것은 **불꽃마다 하나씩 따라 태어나던** 빛이다.
/// </summary>
public sealed class BoosterTrail : MonoBehaviour
{
    /// <summary>
    /// 불꽃 상한. 넘치면 제일 오래된 것부터 덮어써서 일찍 꺼진다 - 에러가 아니라 열화다.
    /// 아군 8척이 전부 부스터를 밟아도 동시 생존은 400 남짓이라 넉넉하다.
    /// </summary>
    private const int Capacity = 2048;

    /// <summary>BoosterTrail.shader의 Puff와 **필드 순서·크기가 같아야 한다.** float 6개.</summary>
    private struct Puff
    {
        public Vector2 position;
        public float born;
        public Vector3 colour;
    }

    private static readonly Puff[] _cpu = new Puff[Capacity];
    private static int _next;
    private static bool _dirty;
    private static BoosterTrail _instance;

    [Header("불꽃 하나")]
    [SerializeField] private float life = 0.28f;
    [SerializeField] private float startRadius = 1.6f;
    [SerializeField] private float endRadius = 7f;

    /// <summary>1을 넘어야 Bloom이 문다. SpriteRenderer.color와 달리 여기는 잘리지 않는다.</summary>
    [SerializeField] private float intensity = 6f;

    private GraphicsBuffer _buffer;
    private Material _material;
    private RenderParams _params;

    /// <summary>
    /// 불꽃 하나를 그 자리에 놓는다. 배열에 쓰기만 하므로 어디서 불러도 안전하다 -
    /// 할당도, 물리도, RNG도 없다.
    /// </summary>
    public static void Add(Vector2 position, Color colour)
    {
        _cpu[_next] = new Puff
        {
            position = position,
            born = Time.time,
            colour = new Vector3(colour.r, colour.g, colour.b),
        };

        _next = (_next + 1) % Capacity;
        _dirty = true;
    }

    // 씬에 아무것도 안 붙여도 돌아야 한다. SpallTrails와 같은 이유 - 그리는 것을 잊으면
    // 없는 게 아니라 안 보이는 것이 되어 디버깅이 두 배로 어려워진다.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(BoosterTrail));
        DontDestroyOnLoad(go);
        go.AddComponent<BoosterTrail>();
    }

    private static readonly int PuffsId = Shader.PropertyToID("_Puffs");
    private static readonly int NowId = Shader.PropertyToID("_Now");
    private static readonly int LifeId = Shader.PropertyToID("_Life");
    private static readonly int StartRadiusId = Shader.PropertyToID("_StartRadius");
    private static readonly int EndRadiusId = Shader.PropertyToID("_EndRadius");
    private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

    private void Awake()
    {
        _instance = this;

        Shader shader = Shader.Find("SUPERRADIANCE/BoosterTrail");

        if (shader == null)
        {
            Debug.LogWarning("BoosterTrail: SUPERRADIANCE/BoosterTrail 셰이더를 못 찾았다. 부스터 꼬리를 끈다.");
            enabled = false;
            return;
        }

        // 태어나기 전의 슬롯. born이 0이면 게임 시작 직후 한 프레임 동안 2,048개가
        // 한꺼번에 뜬다 - 기본값이 유효한 값으로 읽히는 자리는 반드시 한 번 데인다.
        for (int i = 0; i < Capacity; i++)
            _cpu[i].born = float.NegativeInfinity;

        _buffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured, Capacity, sizeof(float) * 6);

        _material = new Material(shader);
        _material.SetBuffer(PuffsId, _buffer);
        _material.SetFloat(LifeId, life);
        _material.SetFloat(StartRadiusId, startRadius);
        _material.SetFloat(EndRadiusId, endRadius);
        _material.SetFloat(IntensityId, intensity);

        _params = new RenderParams(_material)
        {
            // 링버퍼는 월드 어디에나 있으므로 컬링에 걸리면 안 된다.
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 1e5f),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };

        _dirty = true;
    }

    private void LateUpdate()
    {
        if (_buffer == null)
            return;

        // 뱉은 프레임에만 올린다. 48 KB 한 번이라 부분 업로드로 쪼갤 이유가 없다.
        if (_dirty)
        {
            _buffer.SetData(_cpu);
            _dirty = false;
        }

        _material.SetFloat(NowId, Time.time);

        Graphics.RenderPrimitives(_params, MeshTopology.Triangles, Capacity * 6);
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;

        _buffer?.Dispose();
        _buffer = null;

        if (_material != null)
            Destroy(_material);
    }
}
