using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 수동 조준 주포. 포신은 항상 커서를 따라가고, 좌클릭을 누르는 동안만 쏜다.
/// </summary>
public class M7Cannon : Gun
{
    [Header("조준")]
    [SerializeField] private Camera aimCamera;

    protected override void Awake()
    {
        base.Awake();

        if (aimCamera == null)
            aimCamera = Camera.main;
    }

    protected override bool TryGetTarget(out Vector2 worldPoint)
    {
        worldPoint = default;

        if (aimCamera == null || Mouse.current == null)
            return false;

        Vector3 screen = Mouse.current.position.ReadValue();

        // 2D: ScreenToWorldPoint는 카메라에서 z=0 평면까지의 거리를 원한다
        screen.z = -aimCamera.transform.position.z;

        worldPoint = aimCamera.ScreenToWorldPoint(screen);
        return true;
    }

    protected override bool WantsToFire =>
        Mouse.current != null && Mouse.current.leftButton.isPressed;
}
