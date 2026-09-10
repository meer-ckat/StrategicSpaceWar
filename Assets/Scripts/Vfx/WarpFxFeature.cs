using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// <see cref="WarpFx"/>를 카메라 컬러에 한 번 거른다. 2D Renderer(2DRender.asset)에 붙는다 -
/// <see cref="BackgroundRTCompositeFeature"/>와 같은 자리, 같은 RenderGraph 방식이다.
///
/// 소스와 목적지가 같은 텍스처일 수 없어서 임시 텍스처에 거른 뒤 되돌려 놓는다(블릿 둘).
/// <see cref="WarpFx.Active"/>가 아니면 패스를 안 넣는다 - 워프 밖에서는 비용이 0이다.
/// </summary>
public sealed class WarpFxFeature : ScriptableRendererFeature
{
    [SerializeField] private Shader shader;   // 직렬화해 둬야 빌드에 들어간다

    private Material _material;
    private WarpPass _pass;

    public override void Create()
    {
        if (shader == null)
            shader = Shader.Find("SUPERRADIANCE/WarpFx");

        if (shader == null)
        {
            Debug.LogWarning("[WarpFxFeature] SUPERRADIANCE/WarpFx 셰이더가 없다. 워프 화면 효과 없이 간다.");
            return;
        }

        _material = CoreUtils.CreateEngineMaterial(shader);
        _pass = new WarpPass(_material) { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null || !WarpFx.Active || !renderingData.cameraData.camera.CompareTag("MainCamera"))
            return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
        _pass = null;
    }

    private sealed class WarpPass : ScriptableRenderPass
    {
        private readonly Material _material;

        public WarpPass(Material material) => _material = material;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            // 백버퍼에 바로 그리는 카메라는 컬러를 읽을 수 없다.
            if (resources.isActiveTargetBackBuffer)
                return;

            TextureHandle colour = resources.activeColorTexture;
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            TextureHandle temp = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_WarpFxTemp", false);

            WarpFx.Apply(_material, cameraData.camera);

            renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(colour, temp, _material, 0), "WarpFx");
            renderGraph.AddBlitPass(temp, colour, Vector2.one, Vector2.zero, passName: "WarpFx Copy");
        }
    }
}
