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
    void Update()
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
            transform.position.x + jitter.x, transform.position.y + jitter.y, -10f -cam.orthographicSize);
    }

    void Following()
    {
        transform.position = Vector2.Lerp((Vector2)transform.position, (Vector2)A.position, 0.1f);
    }

    void Centering()
    {
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
