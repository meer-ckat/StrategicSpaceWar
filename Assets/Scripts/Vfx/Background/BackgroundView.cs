using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 3D 배경 카메라를 세계에 묶는다. **배경은 스카이박스가 아니라 카메라다** - 별과 소품을
/// 원근 카메라로 찍어 메인 밑에 깐다(BackgroundRTCompositeFeature). 씬에서는 그 카메라가
/// 루트에 고정이라 플레이어가 어디로 가든 배경이 한 픽셀도 안 움직였고, 점프해도 같은
/// 하늘이 남아서 "옮겨졌다"가 안 읽혔다.
///
/// 세 층이다. 노드마다 정해지는 자세(<see cref="Jump"/>), 메인 카메라 위치에서 나오는
/// 시차(<see cref="LateUpdate"/>), 그리고 **노드 종류가 뿌리는 소품**. 소품을 손으로
/// 놓지 않는 이유는 그게 곧 "소구역 하나마다 배경 하나"라는 저작 비용이라서다 -
/// 시드로 뿌리면 저작이 0이고, 같은 노드는 같은 소품이다.
///
/// 자세는 회전이지 이동이 아니다 - 별 사이에 서 있는 원근 카메라를 밀면 가까운 별이
/// 헤엄친다. 하늘은 도는 것이지 미끄러지는 것이 아니다.
/// </summary>
public sealed class BackgroundView : MonoBehaviour
{
    /// <summary>메인 카메라 x 1 m당 배경 yaw(도). 200 m 비행이 1.2도, 1,500 m 점프가 9도다.</summary>
    [SerializeField] private float yawPerMetre = 0.006f;

    [SerializeField] private float pitchPerMetre = 0.004f;

    /// <summary>드리프트 배율. 들판(60 km)에서는 Campaign이 0.1로 내린다 - 기본값이면 한 바퀴다.</summary>
    public static float DriftScale = 1f;

    private static BackgroundView _instance;

    private Transform _cam;

    /// <summary>씬에 놓인 자세. 프롤로그와 1구역은 이걸 그대로 본다 - 손으로 맞춘 하늘이다.</summary>
    private Quaternion _base = Quaternion.identity;

    /// <summary>점프가 정한 자세. 다음 점프까지 유지된다.</summary>
    private Quaternion _node = Quaternion.identity;

    /// <summary>워프 이동 중 하늘이 흐르는 속도(도/초, yaw). WarpTransition이 켜고 끈다. 점프가 누적을 되돌린다.</summary>
    public static float transitYawPerSecond;
    private float _transitYaw;

    /// <summary>
    /// 소품의 재료. 씬의 별에서 빌린다 - 배경 카메라가 찍는 셰이더가 무엇이든 별이
    /// 보인다면 그 재질로 만든 것도 보인다. 재질 파일을 하나 더 두면 그 파일이 곧
    /// "배경 카메라와 같은 셰이더여야 한다"는 규칙을 두 자리에 적는 것이다.
    /// </summary>
    private Material _material;

    private readonly List<Prop> _props = new();
    private static MaterialPropertyBlock _mpb;

    private struct Prop
    {
        public Transform t;
        public Vector3 spin;   // 도/초
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindFirstObjectByType<BackgroundView>() != null)
            return;

        var go = new GameObject(nameof(BackgroundView));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<BackgroundView>();
    }

    private void Awake() => _instance = this;

    /// <summary>
    /// 배경 카메라를 잡는다. 씬 리로드마다 다시 잡아야 한다 - 이 오브젝트는 살아남고
    /// 카메라는 씬과 함께 죽는다. == null이 Unity의 가짜 null이라 그걸 잡아낸다.
    /// </summary>
    private bool Find()
    {
        if (_cam != null)
            return true;

        GameObject go = GameObject.Find("BackgroundCam");

        if (go == null)
            return false;

        _cam = go.transform;
        _base = _cam.rotation;

        GameObject star = GameObject.Find("Star");

        if (star != null && star.TryGetComponent(out Renderer r))
            _material = r.sharedMaterial;

        return true;
    }

    /// <summary>
    /// 노드에 도착했다. **점프의 암전 한가운데서 부른다** - 그래야 자세와 소품이 바뀌는
    /// 프레임이 검은 화면 밑에 숨는다. 시드가 같으면 같은 하늘·같은 소품이라 재개해도
    /// 그대로다.
    ///
    /// yaw는 한 바퀴 전부, pitch·roll은 조금만. 별이 사방에 흩어져 있어 yaw만으로도
    /// 다른 별자리가 나오고, roll이 살짝 걸리면 "같은 하늘을 돌린 것"이 아니라
    /// "다른 곳"으로 읽힌다.
    /// </summary>
    public static void Jump(uint seed, string kind)
    {
        if (_instance == null || !_instance.Find())
            return;

        var rng = new DeterministicRng(seed);

        // 첫 출력은 버린다. SubSectorGen과 같은 이유 - 이웃 시드가 하위 비트만 다를 때
        // xorshift32의 첫 바퀴가 그걸 상위 비트로 못 올린다.
        rng.NextUInt();

        _instance._transitYaw = 0f;
        _instance._node = Quaternion.Euler(
            rng.Range(-25f, 25f),
            rng.Range(0f, 360f),
            rng.Range(-15f, 15f));

        _instance.Dress(kind, ref rng);
        _instance.MakeBackgroundPerlin(seed);
    }

    // ------------------------------------------------------------
    // 소품
    // ------------------------------------------------------------

    /// <summary>
    /// 지난 노드의 소품을 걷고 이 노드의 것을 뿌린다. 자리는 **카메라가 새 자세로 볼
    /// 앞쪽**이다 - 자세는 LateUpdate에서야 적용되므로 여기서 같은 식으로 앞을 구한다.
    /// </summary>
    private void Dress(string kind, ref DeterministicRng rng)
    {
        Clear();

        if (_material == null)
            return;

        Quaternion look = _base * _node;

        switch (kind)
        {
            case "Wreck":
                // 잔해밭. 부서진 조각이 흩어져 천천히 구른다. 납작한 판, 긴 막대, 덩어리.
                Scatter(ref rng, look, count: 36, near: 350f, far: 1400f, spread: 0.55f,
                    minSize: 8f, maxSize: 55f, squash: true, spin: 6f,
                    new Color(0.42f, 0.40f, 0.38f), cube: true);
                break;

            case "Depot":
                // 보급 부표. 작고 밝은 구 하나가 정면에, 주위에 부품 몇 개.
                Put(look, forward: 520f, side: 0f, up: 40f, size: new Vector3(26f, 26f, 26f),
                    new Color(1.0f, 0.86f, 0.45f), cube: false, spin: Vector3.zero);
                Scatter(ref rng, look, count: 6, near: 420f, far: 700f, spread: 0.22f,
                    minSize: 6f, maxSize: 16f, squash: false, spin: 3f,
                    new Color(0.55f, 0.60f, 0.66f), cube: true);
                break;

            case "Port":
                // 기항지. 판을 쌓은 정거장 - 큰 판 하나, 긴 척추, 작은 모듈 몇 개.
                Put(look, 900f, 0f, 0f, new Vector3(520f, 26f, 90f), new Color(0.58f, 0.62f, 0.70f), true, Vector3.zero);
                Put(look, 900f, 0f, 0f, new Vector3(40f, 260f, 40f), new Color(0.50f, 0.54f, 0.62f), true, Vector3.zero);
                Put(look, 880f, -140f, 80f, new Vector3(90f, 60f, 90f), new Color(0.66f, 0.70f, 0.78f), true, Vector3.zero);
                Put(look, 880f, 150f, -70f, new Vector3(90f, 60f, 90f), new Color(0.66f, 0.70f, 0.78f), true, Vector3.zero);
                Put(look, 860f, 0f, 150f, new Vector3(30f, 30f, 30f), new Color(1.0f, 0.75f, 0.35f), false, Vector3.zero);
                break;

            case "Elite":
                // 정예. 멀리 크고 어두운 덩어리 하나 - 무언가 큰 것이 있다.
                Put(look, 2600f, rng.Range(-600f, 600f), rng.Range(-300f, 300f),
                    new Vector3(700f, 700f, 700f), new Color(0.16f, 0.15f, 0.19f), false, new Vector3(0f, 0.4f, 0f));
                break;

            case "Facility":
                // 초복사 시설. 경계 구 안의 원반과 그림자를 셰이더로 그린다.
                PutBlackHole(look, forward: 2400f, side: 120f, up: 60f, shadow: 120f);
                break;

            // Skirmish와 본구역(빈 문자열)은 소품이 없다. 자세만 바뀐 하늘이면 충분하고,
            // 매 노드에 뭔가를 뿌리면 소품이 있는 노드가 특별하지 않게 된다.
        }
    }

    /// <summary>카메라 앞쪽 원뿔 안에 무작위로 뿌린다.</summary>
    private void Scatter(ref DeterministicRng rng, Quaternion look, int count, float near, float far,
        float spread, float minSize, float maxSize, bool squash, float spin, Color colour, bool cube)
    {
        for (int i = 0; i < count; i++)
        {
            float forward = rng.Range(near, far);
            float side = rng.Range(-1f, 1f) * spread * forward;
            float up = rng.Range(-1f, 1f) * spread * forward * 0.7f;

            float s = rng.Range(minSize, maxSize);
            Vector3 size = squash
                ? new Vector3(s * rng.Range(0.4f, 1.6f), s * rng.Range(0.3f, 1.2f), s * rng.Range(0.4f, 1.6f))
                : new Vector3(s, s, s);

            var spinVec = new Vector3(rng.Range(-spin, spin), rng.Range(-spin, spin), rng.Range(-spin, spin));

            Prop p = Put(look, forward, side, up, size, colour, cube, spinVec);

            if (p.t != null)
                p.t.rotation = Quaternion.Euler(rng.Range(0f, 360f), rng.Range(0f, 360f), rng.Range(0f, 360f));
        }
    }

    private void MakeBackgroundPerlin(uint seed)
    {
        Debug.Log($"[BackgroundView] MakeBackgroundPerlin({seed})");

        var rng = new DeterministicRng(seed);

        // uint 전체를 float offset으로 쓰지 않는다.
        // 너무 큰 float은 정밀도가 박살남.
        float ox = rng.Range(-10000f, 10000f);
        float oy = rng.Range(-10000f, 10000f);
        float oz = rng.Range(500f, 2500f);

        const int attempts = 12000;

        const float worldRadius = 1000f;

        // 큰 구조 = 성군
        const float groupScale = 0.0012f;

        // 작은 구조 = 성단
        const float clusterScale = 0.006f;

        int spawned = 0;
        float densitySum = 0f;

        for (int i = 0; i < attempts; i++)
        {
            float x = rng.Range(-worldRadius, worldRadius);
            float y = rng.Range(-worldRadius, worldRadius);
            float z = rng.Range(-worldRadius, worldRadius);

            // ─────────────────────────────
            // 1. 큰 스케일: "여기에 성군이 있는가?"
            // ─────────────────────────────

            float group = Noise3D(
                x * groupScale + ox,
                y * groupScale + oy,
                z * groupScale + oz
            );

            // 낮은 값은 거의 죽이고 높은 부분만 성군으로 남긴다.
            group = Mathf.InverseLerp(0.42f, 0.72f, group);
            group = Mathf.Clamp01(group);

            // ─────────────────────────────
            // 2. 작은 스케일: 성군 내부의 성단
            // ─────────────────────────────

            float cluster = Noise3D(
                x * clusterScale + ox * 1.73f,
                y * clusterScale + oy * 1.37f,
                z * clusterScale + oz * 1.91f
            );

            cluster = Mathf.InverseLerp(0.38f, 0.72f, cluster);
            cluster = Mathf.Clamp01(cluster);

            // 큰 성군 내부에서도 완전히 똑같이 채우지 않고,
            // 작은 성단이 있는 곳에 더 많이 몰리게 한다.
            float density = group * Mathf.Lerp(0.15f, 1f, cluster);

            // 애매한 밀도 지역을 더 강하게 제거한다.
            // 0.5 -> 0.125
            // 0.8 -> 0.512
            density = Mathf.Pow(density, 1.4f);

            densitySum += density;

            // ─────────────────────────────
            // 3. Perlin이 색이 아니라 "존재 확률"을 결정
            // ─────────────────────────────

            float roll = rng.NextUInt() / (float)uint.MaxValue;

            if (roll > density)
                continue;

            // ─────────────────────────────
            // 4. 별의 밝기 / 크기
            // ─────────────────────────────

            float brightnessRandom = rng.Range(0.65f, 20f);

            // 밀집 지역일수록 평균적으로 조금 더 밝게.
            float brightness =
                Mathf.Lerp(0.35f, 1f, density) *
                brightnessRandom;

            // 드물게 밝은 별 하나.
            if ((rng.NextUInt() % 100u) < 3u)
                brightness *= 2.5f;

            float size = rng.Range(4f, 9f);

            // 드문 큰 별
            if ((rng.NextUInt() % 100u) < 2u)
                size *= rng.Range(1.5f, 2.5f);

            Color color = new Color(
                brightness,
                brightness,
                brightness,
                1f
            );

            Put(
                Quaternion.identity,
                x,
                y,
                z,
                new Vector3(size, size, size),
                color,
                false,
                Vector3.zero
            );

            spawned++;
        }

        Debug.Log(
            $"[BackgroundView] Perlin stars: {spawned}/{attempts}, " +
            $"meanDensity={densitySum / attempts:F3}"
        );
    }


    // Mathf에는 3D Perlin이 없으므로
    // XY / YZ / ZX 세 평면을 섞은 싸구려 pseudo-3D noise.
    // 배경 생성에는 충분히 쓸 만하다.
    private static float Noise3D(float x, float y, float z)
    {
        float xy = Mathf.PerlinNoise(x, y);
        float yz = Mathf.PerlinNoise(y, z);
        float zx = Mathf.PerlinNoise(z, x);

        return (xy + yz + zx) / 3f;
    }

    /// <summary>
    /// 소품 하나. 배경 레이어에 놓고 별의 재질을 쓴다. 색은 MaterialPropertyBlock으로 -
    /// 재질을 복제하면 소품마다 머티리얼이 하나씩 생기고, 셰이더가 무엇이든 _BaseColor와
    /// _Color 둘 중 하나는 읽는다.
    /// </summary>
    private Prop Put(Quaternion look, float forward, float side, float up, Vector3 size,
        Color colour, bool cube, Vector3 spin)
    {
        var go = GameObject.CreatePrimitive(cube ? PrimitiveType.Cube : PrimitiveType.Sphere);

        go.name = "BG Prop";
        go.layer = LayerMask.NameToLayer("Background");

        // 콜라이더는 없앤다. 배경 카메라가 찍기만 하는 것이라 물리 세계에 있을 이유가 없고,
        // 두면 TraceWorld가 계층을 읽다가 이걸 집는다.
        if (go.TryGetComponent(out Collider col))
            Destroy(col);

        Vector3 at = _cam.position + look * new Vector3(side, up, forward);

        go.transform.SetPositionAndRotation(at, look);
        go.transform.localScale = size;

        if (go.TryGetComponent(out Renderer r))
        {
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            _mpb ??= new MaterialPropertyBlock();
            _mpb.Clear();
            _mpb.SetColor("_BaseColor", colour);
            _mpb.SetColor("_Color", colour);
            _mpb.SetColor("_EmissionColor", colour * 0.6f);
            r.SetPropertyBlock(_mpb);
        }

        var prop = new Prop { t = go.transform, spin = spin };
        _props.Add(prop);

        return prop;
    }

    private static Material _blackHole;

    /// <summary>
    /// 블랙홀. 원반보다 25% 큰 경계 구에 카메라부터 적분한 광자 경로를 그린다.
    /// 그림자·광자 고리·먼 쪽이 위로 휘어 오르는 호가 거기서 나온다. 재질은
    /// Resources/Materials/BlackHole.mat - 씬의 Sphere에 끌어다 놓으면 그대로 보인다(Z축이 원반 법선).
    /// 구 안의 별은 원반과 같은 측지선으로 휘고(오파크 텍스처를 읽는다), 구 밖은 GravLens가 약장식으로 잇는다.
    /// </summary>
    /// <param name="shadow">슈바르츠실트 반지름(m). 개체별 _Rs로 전달한다.</param>
    private void PutBlackHole(Quaternion look, float forward, float side, float up, float shadow)
    {
        _blackHole ??= Resources.Load<Material>("Materials/BlackHole");

        if (_blackHole == null)
        {
            Debug.LogError("[BackgroundView] Resources/Materials/BlackHole.mat이 없다.");
            return;
        }

        float outer = _blackHole.GetFloat("_Outer") * shadow;

        Vector3 at = _cam.position + look * new Vector3(side, up, forward);

        // 원반 법선 = 구의 Z축. 시선에서 70도 눕히면 위에서 20도 내려다보는 각이다.
        Quaternion tilt = look * Quaternion.Euler(70f, 0f, 0f);
        Transform hole = Prim(PrimitiveType.Sphere, "BG Black Hole", at, tilt, Vector3.one * (outer * 2.5f), _blackHole);
        _mpb ??= new MaterialPropertyBlock();
        _mpb.Clear();
        _mpb.SetFloat("_Rs", shadow);
        hole.GetComponent<Renderer>().SetPropertyBlock(_mpb);

        if (_cam.TryGetComponent(out Camera bgCam))
            GravLens.Set(bgCam, at, shadow);
    }

    private Transform Prim(PrimitiveType type, string name, Vector3 at, Quaternion rot, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.layer = LayerMask.NameToLayer("Background");

        if (go.TryGetComponent(out Collider col))
            Destroy(col);

        go.transform.SetPositionAndRotation(at, rot);
        go.transform.localScale = scale;

        if (go.TryGetComponent(out Renderer r))
        {
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        _props.Add(new Prop { t = go.transform, spin = Vector3.zero });
        return go.transform;
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Background/Facility Sky (play mode)")]
    private static void PreviewFacility() => Jump(1234u, "Facility");
#endif

    private void Clear()
    {
        GravLens.Off();

        foreach (Prop p in _props)
        {
            if (p.t != null)
                Destroy(p.t.gameObject);
        }

        _props.Clear();
    }

    private void LateUpdate()
    {
        if (!Find())
            return;

        Camera main = Camera.main;

        if (main == null)
            return;

        // 절대 위치에서 나온다. 기준점을 점프마다 되돌리지 않는 이유는 런 전체에 걸쳐
        // 하늘이 한 방향으로 계속 돌아가는 것이 맞아서다 - 11 km를 가면 66도다.
        Vector3 at = main.transform.position;
        Quaternion drift = Quaternion.Euler(-at.y * pitchPerMetre * DriftScale, at.x * yawPerMetre * DriftScale, 0f);

        _transitYaw += transitYawPerSecond * Time.unscaledDeltaTime;
        _cam.rotation = _base * _node * drift * Quaternion.Euler(0f, _transitYaw, 0f);

        // 잔해가 구른다. 정지한 소품은 그림이고 구르는 소품은 장소다.
        float dt = Time.deltaTime;

        for (int i = _props.Count - 1; i >= 0; i--)
        {
            Prop p = _props[i];

            // 씬이 다시 열리면 소품은 씬과 함께 죽는다. 목록에서 걷는다.
            if (p.t == null)
            {
                _props.RemoveAt(i);
                continue;
            }

            if (p.spin != Vector3.zero)
                p.t.Rotate(p.spin * dt, Space.Self);
        }
    }
}
