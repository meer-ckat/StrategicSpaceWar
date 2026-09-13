using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways, RequireComponent(typeof(Renderer)), DisallowMultipleComponent]
public sealed class BlackHoleLens : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;
    private Renderer _renderer;
    private static readonly List<BlackHoleLens> Sources = new();

    private void OnEnable()
    {
        _renderer = GetComponent<Renderer>();
        if (!Sources.Contains(this)) Sources.Add(this);
    }

    private void OnDisable() => Sources.Remove(this);

    internal static bool TryGet(Camera camera, out Vector3 centre, out float radius)
    {
        foreach (BlackHoleLens source in Sources)
        {
            if (source == null || !source.isActiveAndEnabled || source.targetCamera != camera
                || source._renderer == null || !source._renderer.enabled
                || (camera.cullingMask & (1 << source.gameObject.layer)) == 0)
                continue;

            Material material = source._renderer.sharedMaterial;
            if (material == null || material.shader.name != "SUPERRADIANCE/BlackHoleRaymarch") continue;
            centre = source.transform.position;
            radius = Mathf.Max(0.001f, material.GetFloat("_Rs"));
            return true;
        }
        centre = default;
        radius = 0f;
        return false;
    }
}
