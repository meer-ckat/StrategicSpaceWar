using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 블랙홀의 중력 렌즈 값. <see cref="BackgroundView"/>가 블랙홀을 놓을 때 켜고 걷을 때 끈다.
/// 값이 없으면 패스 자체가 안 돈다 - 블랙홀 없는 하늘에서는 공짜다.
/// </summary>
public static class GravLens
{
    private static Camera _camera;
    private static Vector3 _centre;
    private static float _rs;

    private static readonly int CentreId = Shader.PropertyToID("_LensCentre");
    private static readonly int LensId = Shader.PropertyToID("_Lens");

    /// <summary>렌즈를 거를 카메라. 배경 카메라다 - 메인 카메라의 배는 안 휜다.</summary>
    public static Camera Target => _camera;

    public static bool Active => _camera != null && _rs > 0f;

    /// <param name="rs">슈바르츠실트 반지름(m). 세기는 r_s/카메라 거리라 Apply가 매 프레임 낸다.</param>
    public static void Set(Camera camera, Vector3 worldCentre, float rs)
    {
        _camera = camera;
        _centre = worldCentre;
        _rs = rs;
    }

    public static void Off()
    {
        _camera = null;
        _rs = 0f;
    }

    /// <summary>재질에 붓는다. 카메라 뒤면 false.</summary>
    internal static bool Apply(Material material, Camera camera)
    {
        bool sceneLens = BlackHoleLens.TryGet(camera, out Vector3 centre, out float rs);
        if (!sceneLens)
        {
            if (!Active || camera != Target) return false;
            centre = _centre;
            rs = _rs;
        }
        Vector3 c = camera.WorldToViewportPoint(centre);

        if (c.z <= 0f)
            return false;

        float aspect = camera.pixelHeight > 0 ? (float)camera.pixelWidth / camera.pixelHeight : 1.78f;
        float focal = 0.5f / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float distance = (centre - camera.transform.position).magnitude;

        material.SetVector(CentreId, new Vector4(c.x, c.y, aspect, 0f));
        material.SetVector(LensId, new Vector4(rs / Mathf.Max(distance, rs), MaxMagnification, c.z, focal));
        return true;
    }

    /// <summary>고리에서 배율이 발산한다. 여기서 자른다 - 초안, 오너가 고친다.</summary>
    public const float MaxMagnification = 4f;

}

/// <summary>
/// <see cref="GravLens"/>를 배경 카메라 컬러에 한 번 거른다. **투명 큐 전**이다 - 별(불투명)은 휘고
/// 블랙홀 쿼드(투명)는 그 위에 그대로 그려진다. 배경 카메라의 Universal Renderer에 붙는다.
/// 구조는 <see cref="WarpFxFeature"/>와 같다.
/// </summary>
public sealed class GravLensFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader shader;   // 직렬화해 둬야 빌드에 들어간다

    private Material _material;
    private LensPass _pass;

    public override void Create()
    {
        CoreUtils.Destroy(_material);

        if (shader == null)
            shader = Shader.Find("SUPERRADIANCE/GravLens");

        if (shader == null)
        {
            Debug.LogWarning("[GravLensFeature] SUPERRADIANCE/GravLens 셰이더가 없다. 중력 렌즈 없이 간다.");
            return;
        }

        _material = CoreUtils.CreateEngineMaterial(shader);
        _pass = new LensPass(_material) { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        Camera camera = renderingData.cameraData.camera;
        bool sceneLens = BlackHoleLens.TryGet(camera, out _, out _);
        if (_pass == null || (!sceneLens && (!GravLens.Active || camera != GravLens.Target)))
            return;

        _pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
        _pass = null;
    }

    private sealed class LensPass : ScriptableRenderPass
    {
        private readonly Material _material;

        private sealed class PassData
        {
            internal TextureHandle source;
            internal TextureHandle depth;
            internal Material material;
        }

        public LensPass(Material material) => _material = material;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            if (resources.isActiveTargetBackBuffer)
                return;

            if (!GravLens.Apply(_material, cameraData.camera))
                return;

            TextureHandle colour = resources.activeColorTexture;
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            TextureHandle temp = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_GravLensTemp", false);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("GravLens", out var data))
            {
                data.source = colour;
                data.depth = resources.cameraDepthTexture;
                data.material = _material;
                builder.UseTexture(colour, AccessFlags.Read);
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(temp, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData pass, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalTexture("_CameraDepthTexture", pass.depth);
                    Blitter.BlitTexture(context.cmd, pass.source, new Vector4(1, 1, 0, 0), pass.material, 0);
                });
            }
            renderGraph.AddBlitPass(temp, colour, Vector2.one, Vector2.zero, passName: "GravLens Copy");
        }
    }
}
