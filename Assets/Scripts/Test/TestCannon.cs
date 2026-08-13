using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Core;

/// <summary>
/// Firing range trigger. Aims at the cursor, spawns shells while the left button is held.
/// </summary>
public class TestCannon : MonoBehaviour
{
    [SerializeField] private Projectile bulletPrefab;
    [SerializeField] private float muzzleSpeed = 900f;
    [SerializeField] private Camera aimCamera;

    [Header("Auto fire")]
    [SerializeField] private bool automatic = true;
    [SerializeField] private float roundsPerMinute = 600f;

    /// <summary>
    /// Backstop for the overload test itself. 60000 RPM is ~17 shells per tick; a typo of
    /// one more zero would spawn thousands in a frame and hang the editor instead of
    /// showing you where the tick loop actually falls over.
    /// </summary>
    [SerializeField] private int maxShotsPerFrame = 64;

    private long _lastTick;
    private float _pending;

    private void Awake()
    {
        if (aimCamera == null)
            aimCamera = Camera.main;

        if (bulletPrefab == null)
            Debug.LogError($"[TestCannon] {name} has no bullet prefab assigned.", this);

        _lastTick = TickManager.currentTick;
    }

    private void Update()
    {
        if (Mouse.current == null || aimCamera == null)
            return;

        Vector2 toMouse = MouseWorld() - (Vector2)transform.position;

        if (toMouse.sqrMagnitude > 1e-6f)
            transform.up = toMouse.normalized;

        if (bulletPrefab == null)
            return;

        // Rate is counted in ticks, not frames. Frame-timed fire would make the measured
        // load depend on the frame rate, which is the thing under test.
        long tick = TickManager.currentTick;
        long elapsedTicks = tick - _lastTick;
        _lastTick = tick;

        ButtonControl trigger = Mouse.current.leftButton;

        if (!automatic)
        {
            if (trigger.wasPressedThisFrame)
                Fire(toMouse);

            return;
        }

        if (trigger.wasPressedThisFrame)
        {
            _pending = 1f;   // first round leaves on the click, not one interval later
        }
        else if (trigger.isPressed)
        {
            _pending += elapsedTicks * TickManager.TickDeltaTime * (roundsPerMinute / 60f);
        }
        else
        {
            _pending = 0f;
            return;
        }

        int shots = Mathf.FloorToInt(_pending);

        if (shots > maxShotsPerFrame)
        {
            shots = maxShotsPerFrame;
            _pending = 0f;   // a load test has no use for a backlog
        }
        else
        {
            _pending -= shots;
        }

        for (int i = 0; i < shots; i++)
            Fire(toMouse);
    }

    private void Fire(Vector2 direction)
    {
        Projectile bullet = Instantiate(
            bulletPrefab,
            transform.position,
            transform.rotation);

        bullet.Launch(direction, muzzleSpeed);
    }

    private Vector2 MouseWorld()
    {
        Vector3 screen = Mouse.current.position.ReadValue();

        // 2D: ScreenToWorldPoint wants the distance from the camera to the z=0 plane
        screen.z = -aimCamera.transform.position.z;

        return aimCamera.ScreenToWorldPoint(screen);
    }
}
