using UnityEngine;
using UnityEngine.InputSystem;

public class CameraSystem : MonoBehaviour
{
    public enum TrackingType
    {
        Following,
        Centering,
        Aim
    }
    public TrackingType myType;
    public Transform A;
    public Transform B;
    public float ratio;
    Camera cam;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        cam = GetComponent<Camera>();
        _instance = this;
    }

    // --- 화면 흔들림 ---
    //
    // 그림이다. 유폭이 화면 밖에서 나도 뭔가 일어났다는 것이 전해져야 하고, 화면 안이면
    // 판이 사라지는 한 틱짜리 사건에 무게가 생긴다.

    private static CameraSystem _instance;

    private float _shake;
    private uint _shakeSeed = 1;

    /// <summary>세기만 받는다. 지속시간은 감쇠율이 정한다 - 손잡이가 둘이면 하나는 안 만진다.</summary>
    public static void Shake(float amount)
    {
        if (_instance != null)
            _instance._shake = Mathf.Max(_instance._shake, amount);
    }

    // Update is called once per frame
    void LateUpdate()
    {
        switch(myType)
        {
            case TrackingType.Centering:
            Centering();
            break;
            case TrackingType.Following:
            Following();
            break;
            case TrackingType.Aim:
            Aim();
            break;
        }
        Vector2 jitter = Vector2.zero;

        if (_shake > 0.001f)
        {
            // UnityEngine.Random 금지. 결정론 불변식은 그림에도 적용된다 - 리플레이가
            // 카메라 때문에 어긋나면 그것도 어긋난 것이다.
            var rng = new DeterministicRng(Ballistics.Hash(0, Core.TickManager.currentTick, (int)_shakeSeed++));

            jitter = new Vector2(rng.Range(-_shake, _shake), rng.Range(-_shake, _shake));

            // 프레임률과 무관하게 약 0.25초에 사그라든다.
            _shake *= Mathf.Pow(0.02f, Time.deltaTime / 0.25f);
        }
        else
        {
            _shake = 0f;
        }

        transform.position = new Vector3(
            transform.position.x + jitter.x, transform.position.y + jitter.y, -10f -cam.orthographicSize / 5f);
    }

    // --- 컷신 프레이밍 ---
    //
    // 컷신이 A/B를 잠깐 갈아끼운다. 원래 값은 여기 두었다가 끝날 때 되돌린다 -
    // 인스펙터에 적힌 것이 진짜 주인이고, 컷신은 빌려 쓰는 쪽이다.

    private bool _cutsceneHeld;
    private TrackingType _savedType;
    private Transform _savedA;
    private Transform _savedB;
    private float _savedMoveSmooth;
    private float _savedZoomSmooth;
    private float _savedRatio;

    /// <summary>
    /// 따라가는 속도. 클수록 느긋하다(SmoothDamp의 시간 상수라 **작을수록 빠르다**).
    /// 연출이 "탁 붙는다"와 "천천히 흘러간다"를 가르는 손잡이가 이것 하나다.
    ///
    /// 0을 주면 SmoothDamp가 그 프레임에 목표로 순간이동한다 - 컷신 시작에서 화면을
    /// 미리 맞춰 두고 싶을 때 쓴다.
    /// </summary>
    public static void CutsceneDamp(float move, float zoom)
    {
        if (_instance == null)
            return;

        _instance.HoldForCutscene();

        _instance.moveSmooth = Mathf.Max(0f, move);
        _instance.zoomSmooth = Mathf.Max(0f, zoom);
    }

    /// <summary>
    /// 컷신이 프레임을 잡는다. b가 있으면 둘을 다 담고(Centering), 없으면 a만 따라간다.
    ///
    /// **원래 값은 처음 한 번만 저장한다.** 컷신 도중에 대상이 여러 번 바뀌는데, 매번
    /// 저장하면 두 번째 호출이 "컷신이 방금 넣은 값"을 원본으로 기억해서 영영 안 돌아온다.
    /// </summary>
    /// <param name="zoom">
    /// 둘을 담을 때 거리에 곱하는 여유. 0이면 씬 값을 그대로 쓴다. **전투 값(1.4쯤)은
    /// 컷신에 너무 크다** - 300 m 떨어진 두 배를 그 값으로 담으면 화면이 840 m 폭이라
    /// 배가 점이 된다.
    /// </param>
    public static void CutsceneFrame(Transform a, Transform b, float zoom = 0f)
    {
        if (_instance == null || a == null)
            return;

        _instance.HoldForCutscene();

        _instance.A = a;
        _instance.B = b;
        _instance.myType = b != null ? TrackingType.Centering : TrackingType.Following;

        if (zoom > 0f)
            _instance.ratio = zoom;
    }

    private void HoldForCutscene()
    {
        if (_cutsceneHeld)
            return;

        _cutsceneHeld = true;
        _savedType = myType;
        _savedA = A;
        _savedB = B;
        _savedMoveSmooth = moveSmooth;
        _savedZoomSmooth = zoomSmooth;
        _savedRatio = ratio;
    }

    /// <summary>
    /// 인스펙터가 적어 둔 프레임으로 되돌린다. **컷신 배를 지우기 전에 불러야 한다** -
    /// 지운 뒤에 남은 참조로 position을 읽으면 그 자리에서 예외가 난다. 안 빌렸으면
    /// 아무 일도 안 일어난다.
    /// </summary>
    public static void ReleaseCutscene()
    {
        if (_instance == null || !_instance._cutsceneHeld)
            return;

        _instance._cutsceneHeld = false;
        _instance.myType = _instance._savedType;
        _instance.A = _instance._savedA;
        _instance.B = _instance._savedB;
        _instance.moveSmooth = _instance._savedMoveSmooth;
        _instance.zoomSmooth = _instance._savedZoomSmooth;
        _instance.ratio = _instance._savedRatio;
    }

    void Following()
    {
        if (A == null)
            return;

        transform.position = Vector2.Lerp((Vector2)transform.position, (Vector2)A.position, 0.1f);
    }

    void Centering()
    {
        // 컷신 배가 먼저 치워지고 카메라가 한 프레임 늦게 도는 창이 있다. 둘 중 하나만
        // 죽어도 남은 쪽을 따라가는 쪽이, 예외로 프레임을 통째로 잃는 것보다 낫다.
        if (A == null || B == null)
        {
            Transform alive = A != null ? A : B;

            if (alive != null)
                transform.position = Vector2.Lerp((Vector2)transform.position, (Vector2)alive.position, 0.1f);

            return;
        }

        Vector2 center = (A.position + B.position) / 2;
        cam.orthographicSize = Mathf.Max(10f, (A.position - B.position).magnitude * ratio);
        transform.position = Vector2.Lerp((Vector2)transform.position, center, 0.1f);
    }

    [SerializeField] float minZoom = 10f;
    [SerializeField] float maxZoom = 18f;

    [SerializeField] float lookAhead = 8f;

    [SerializeField] float moveSmooth = 0.15f;
    [SerializeField] float zoomSmooth = 0.15f;

    private Vector2 moveVelocity;
    private float zoomVelocity;

    void Aim()
    {
        if (A == null)
            return;

        // 0~1
        Vector2 mouseViewport =
            Camera.main.ScreenToViewportPoint(Mouse.current.position.ReadValue());

        // 화면 중앙 기준 -1 ~ +1
        Vector2 aim =
            (mouseViewport - new Vector2(0.5f, 0.5f)) * 2f;

        // 원형 범위로 제한
        aim = Vector2.ClampMagnitude(aim, 1f);

        // 마우스 방향으로 카메라 이동
        Vector2 targetPosition =
            (Vector2)A.position + aim * lookAhead;

        // 마우스가 중앙에서 멀수록 zoom out
        float targetZoom =
            Mathf.Lerp(minZoom, maxZoom, aim.magnitude);

        Vector2 newPosition = Vector2.SmoothDamp(
            transform.position,
            targetPosition,
            ref moveVelocity,
            moveSmooth
        );

        transform.position = new Vector3(
            newPosition.x,
            newPosition.y,
            transform.position.z
        );

        cam.orthographicSize = Mathf.SmoothDamp(
            cam.orthographicSize,
            targetZoom,
            ref zoomVelocity,
            zoomSmooth
        );
    }
}
