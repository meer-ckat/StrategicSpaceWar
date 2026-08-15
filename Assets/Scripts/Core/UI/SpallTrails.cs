using UnityEngine;

/// <summary>
/// 파편이 지나간 선을 잠깐 남긴다. 전부 그림이다 - 여기서 시뮬레이션 상태는 아무것도
/// 바뀌지 않고, 반대로 이 파일이 통째로 없어도 판정은 똑같이 돌아간다.
///
/// 왜 필요한가: 파편은 한 틱 안에 레이로 끝나서 화면에 아무 흔적이 없다. 그래서 관통한
/// 자리와 상관없어 보이는 판·모듈이 깎이고, 사람은 원인과 결과를 이어 붙일 단서가 없다.
/// 필요한 정보(시작점·끝점·무엇을 맞췄나)는 SpallResolver가 이미 다 계산해 두고 버리던 것이다.
///
/// 구조: 선분을 고정 길이 링버퍼에 쌓고, 매 프레임 그걸 사각형 메시 하나로 만들어 한 번에
/// 그린다. 파편마다 GameObject나 LineRenderer를 만들면 한 발에 수십 개가 생긴다.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class SpallTrails : MonoBehaviour
{
    public enum Kind { Miss = 0, Armor = 1, Module = 2 }

    /// <summary>
    /// 선분 상한. 한 발이 만드는 파편은 SpallMaxCount x MaxSpallDepth로 막혀 있지만,
    /// **유폭은 그 규칙 밖이다** - 판 40장이 한 틱에 무너지면 붕괴 파편만 수천 개다.
    /// 512로는 한 프레임에 링버퍼를 여러 바퀴 덮어써서, 제일 큰 사건이 제일 안 보였다.
    /// </summary>
    private const int Capacity = 4096;

    [Header("Trail")]
    [SerializeField] private float lifetime = 0.3f;   // s
    [SerializeField] private float width = 0.1f;      // m
    [SerializeField] private int sortingOrder = 100;

    [Header("Colour")]
    [SerializeField] private Color armorHit = new(1f, 0.92f, 0.65f, 1f);
    [SerializeField] private Color moduleHit = new(1f, 0.55f, 0.25f, 1f);

    /// <summary>맞은 것만 그리면 부채꼴이 안 보이고 "왜 쟤만 맞았지"가 남는다.</summary>
    [SerializeField] private Color miss = new(0.6f, 0.65f, 0.7f, 0.35f);

    private static readonly Vector2[] _from = new Vector2[Capacity];
    private static readonly Vector2[] _to = new Vector2[Capacity];
    private static readonly Kind[] _kind = new Kind[Capacity];
    private static readonly float[] _born = new float[Capacity];
    private static int _next;

    private static SpallTrails _instance;

    private readonly Vector3[] _vertices = new Vector3[Capacity * 4];
    private readonly Color[] _colours = new Color[Capacity * 4];
    private Mesh _mesh;

    /// <summary>
    /// SpallResolver가 파편 하나를 해결할 때마다 부른다. 구조체 배열에 쓰기만 하므로
    /// 시뮬레이션 쪽에서 불러도 안전하다 - 물리도, RNG도, 할당도 없다.
    /// </summary>
    public static void Add(Vector2 from, Vector2 to, Kind kind)
    {
        _from[_next] = from;
        _to[_next] = to;
        _kind[_next] = kind;
        _born[_next] = Time.time;

        _next = (_next + 1) % Capacity;
    }

    // 씬에 아무것도 안 붙여도 돌아야 한다. 파편을 그리려고 프리팹을 배치하는 걸
    // 잊어버리면, 없는 게 아니라 안 보이는 것이 되어 디버깅이 두 배로 어려워진다.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(SpallTrails));
        DontDestroyOnLoad(go);
        go.AddComponent<SpallTrails>();
    }

    private void Awake()
    {
        _instance = this;

        _mesh = new Mesh { name = "SpallTrails" };
        _mesh.MarkDynamic();

        // 인덱스는 한 번만 만든다. 안 쓰는 사각형은 네 꼭짓점을 한 점에 겹쳐서 지운다 -
        // 매 프레임 인덱스 버퍼를 다시 쓰는 것보다 싸다.
        var triangles = new int[Capacity * 6];

        for (int q = 0; q < Capacity; q++)
        {
            int v = q * 4;
            int t = q * 6;

            triangles[t + 0] = v + 0;
            triangles[t + 1] = v + 1;
            triangles[t + 2] = v + 2;
            triangles[t + 3] = v + 0;
            triangles[t + 4] = v + 2;
            triangles[t + 5] = v + 3;
        }

        _mesh.vertices = _vertices;
        _mesh.triangles = triangles;

        // 죽은 사각형이 원점에 겹쳐 있어도 화면 밖으로 판정되지 않게 넉넉히.
        _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);

        GetComponent<MeshFilter>().sharedMesh = _mesh;

        var renderer = GetComponent<MeshRenderer>();

        // Sprites/Default는 정점 색을 그대로 곱해준다 - 셰이더를 따로 만들 이유가 없다.
        Shader shader = Shader.Find("Sprites/Default");

        if (shader == null)
        {
            Debug.LogWarning("SpallTrails: Sprites/Default 셰이더를 못 찾았다. 파편 선을 끈다.");
            enabled = false;
            return;
        }

        renderer.sharedMaterial = new Material(shader);
        renderer.sortingOrder = sortingOrder;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private void LateUpdate()
    {
        float now = Time.time;
        float life = Mathf.Max(1e-3f, lifetime);
        float half = width * 0.5f;

        for (int i = 0; i < Capacity; i++)
        {
            int v = i * 4;
            float age = now - _born[i];

            Vector2 delta = _to[i] - _from[i];
            float length = delta.magnitude;

            if (age >= life || length < 1e-5f)
            {
                // 죽은 선분: 사각형을 한 점으로 접는다. 면적이 0이라 아무것도 안 그려진다.
                _vertices[v + 0] = _vertices[v + 1] = _vertices[v + 2] = _vertices[v + 3] =
                    Vector3.zero;

                continue;
            }

            Vector2 normal = new Vector2(-delta.y, delta.x) * (half / length);

            _vertices[v + 0] = _from[i] - normal;
            _vertices[v + 1] = _from[i] + normal;
            _vertices[v + 2] = _to[i] + normal;
            _vertices[v + 3] = _to[i] - normal;

            Color c = _kind[i] switch
            {
                Kind.Armor => armorHit,
                Kind.Module => moduleHit,
                _ => miss,
            };

            // 제곱으로 죽여야 '번쩍'으로 읽힌다. 선형이면 흐릿하게 오래 남아 지저분하다.
            float fade = 1f - age / life;
            c.a *= fade * fade;

            _colours[v + 0] = _colours[v + 1] = _colours[v + 2] = _colours[v + 3] = c;
        }

        _mesh.vertices = _vertices;
        _mesh.colors = _colours;
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;

        if (_mesh != null)
            Destroy(_mesh);
    }
}
