using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 별도의 3D Background Camera가 RenderTexture에 그린 결과를
/// 2D Main Camera의 컬러 버퍼 맨 뒤에 깐다.
///
/// BackgroundCam : Universal Renderer + Perspective -> RenderTexture
/// MainCam       : 2D Renderer + Orthographic      -> Screen
///
/// 이 Feature는 MainCam이 사용하는 2D Renderer Data에 추가한다.
/// </summary>
public sealed class BackgroundRTCompositeFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public sealed class Settings
    {
        [Tooltip("Background Camera가 Target Texture로 사용하는 RenderTexture")]
        public RenderTexture backgroundTexture;

        [Tooltip("보통 BeforeRenderingOpaques면 2D 오브젝트보다 먼저 깔린다.")]
        public RenderPassEvent injectionPoint =
            RenderPassEvent.BeforeRenderingOpaques;

        [Tooltip("이 태그의 카메라에만 적용. 비워두면 해당 Renderer를 쓰는 모든 카메라에 적용.")]
        public string targetCameraTag = "MainCamera";
    }

    public Settings settings = new();

    private CompositePass _pass;

    public override void Create()
    {
        _pass?.Dispose();

        _pass = new CompositePass(settings.backgroundTexture)
        {
            renderPassEvent = settings.injectionPoint
        };
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (_pass == null || settings.backgroundTexture == null)
            return;

        Camera cam = renderingData.cameraData.camera;

        if (!string.IsNullOrEmpty(settings.targetCameraTag) &&
            !cam.CompareTag(settings.targetCameraTag))
            return;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
        _pass = null;
    }

    private sealed class CompositePass : ScriptableRenderPass
    {
        private readonly RenderTexture _sourceTexture;
        private RTHandle _sourceHandle;

        private sealed class PassData
        {
            public TextureHandle source;
        }

        public CompositePass(RenderTexture source)
        {
            _sourceTexture = source;

            if (_sourceTexture != null)
                _sourceHandle = RTHandles.Alloc(_sourceTexture);
        }

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameData)
        {
            if (_sourceTexture == null || _sourceHandle == null)
                return;

            UniversalResourceData resourceData =
                frameData.Get<UniversalResourceData>();

            TextureHandle destination =
                resourceData.activeColorTexture;

            // BackgroundCam의 외부 RenderTexture를
            // 현재 MainCam RenderGraph 안으로 가져온다.
            TextureHandle source =
                renderGraph.ImportTexture(_sourceHandle);

            using var builder =
                renderGraph.AddRasterRenderPass<PassData>(
                    "Composite 3D Background",
                    out PassData passData);

            passData.source = source;

            builder.UseTexture(
                source,
                AccessFlags.Read);

            // Main Camera의 현재 컬러 버퍼에 직접 작성.
            builder.SetRenderAttachment(
                destination,
                0,
                AccessFlags.Write);

            // 외부 RT를 사용하는 패스라 최적화로 날려버리지 않게 한다.
            builder.AllowPassCulling(false);

            builder.SetRenderFunc(
                (PassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(
                        context.cmd,
                        data.source,
                        new Vector4(1f, 1f, 0f, 0f),
                        0,
                        false);
                });
        }

        public void Dispose()
        {
            _sourceHandle?.Release();
            _sourceHandle = null;
        }
    }
}   