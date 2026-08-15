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
        transform.position = new Vector3(transform.position.x, transform.position.y, -10f);
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
