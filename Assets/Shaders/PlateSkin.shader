// 장갑판 스킨 - GPU판. CPU가 판마다 텍스처를 굽던 것을, 배 그림(공유 텍스처) +
// 6x6 손상 마스크 + 실루엣 마스크 + 절차 노이즈의 조합으로 프래그먼트에서 그린다.
//
// 규칙의 원본은 C#(ArmorSkin)이다. 서브셀 격자·판 사각형·실물 모양·배 그림 대응은 전부
// C#이 계산해 상수와 구운 마스크로 넘기고, 여기는 그 숫자를 조합만 한다.
//
// 골격은 URP 17의 Sprite-Lit-Default(Universal2D 패스)를 그대로 따른다 - Core2D/
// Lit2DCommon 매크로 구조가 버전마다 바뀌므로, 다르게 쓰면 내부 재정의와 충돌한다.
// 노말맵 패스는 뺐다: 판은 노말맵을 쓴 적이 없다.
Shader "SUPERRADIANCE/PlateSkin"
{
    Properties
    {
        _MainTex("Sprite", 2D) = "white" {}               // 공유 흰 텍스처. 지오메트리용
        _MaskTex("Mask", 2D) = "white" {}                 // URP 2D 라이팅 마스크 규약
        _HullTex("Hull Art", 2D) = "white" {}
        _DamageMask("Damage Mask", 2D) = "white" {}       // 6x6 R8, 서브셀 HP
        _ShapeMask("Shape Mask", 2D) = "white" {}         // 폴리곤 판 실루엣. InsideShape의 캐시
        _ShapeScale("Shape Scale", Vector) = (1,1,0,0)
        _HasShape("Has Shape", Float) = 0
        _LocalMin("Local Min(xy) / UVToLocal(zw)", Vector) = (0,0,1,1)
        _Rect("Centre(xy) / Half(zw)", Vector) = (0,0,0.5,0.5)
        _Cell("Cell Offset(xy) / Size(zw)", Vector) = (0,0,1,1)
        _HullA("Hull Affine Rows", Vector) = (1,0,0,1)
        _HullB("Hull Affine Offset", Vector) = (0,0,0,0)
        _Damaged("Damaged", Color) = (0.35,0.33,0.32,1)
        _Healthy("Healthy", Color) = (1,1,1,1)
        _ErodeBelow("Erode Below", Float) = 0.6
        _HasHull("Has Hull", Float) = 0
        _GrainSeed("Grain Seed", Vector) = (0,0,0,0)
        _GrainPpu("Grain PPU", Float) = 48
        _Heat("Heat", Float) = 0

        // Sprite-Lit-Default과 같은 레거시 호환 속성
        [HideInInspector] _Color("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _AlphaTex("External Alpha", 2D) = "white" {}
        [HideInInspector] _EnableExternalAlpha("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex LitVertex
            #pragma fragment LitFragment

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/ShapeLightShared.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ DEBUG_DISPLAY

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color        : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_LIT_OUTPUTS
                half4 color        : COLOR;
            };

            // _MainTex/_MaskTex/_NormalMap 선언과 CombinedShapeLightShared 일습이 여기서 온다
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Lit2DCommon.hlsl"

            TEXTURE2D(_HullTex);
            SAMPLER(sampler_HullTex);
            TEXTURE2D(_DamageMask);
            SAMPLER(sampler_DamageMask);
            TEXTURE2D(_ShapeMask);
            SAMPLER(sampler_ShapeMask);

            // NOTE: SRP 배처는 어차피 MaterialPropertyBlock 때문에 판에는 안 붙는다.
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _Damaged;
                half4 _Healthy;
                float4 _LocalMin;
                float4 _Rect;
                float4 _Cell;
                float4 _HullA;
                float4 _HullB;
                float4 _GrainSeed;
                float4 _ShapeScale;
                float _ErodeBelow;
                float _HasHull;
                float _HasShape;
                float _GrainPpu;
                float _Heat;
            CBUFFER_END

            // 적열 램프. **SpriteRenderer.color로는 이 값을 못 보낸다** - 그 경로는 HDR을
            // 못 통과시켜서 1을 넘는 성분이 잘린다(예전 CPU 굽기는 텍스처 픽셀에 직접
            // 써서 됐던 것이다). 그래서 C#은 heat를 float 하나로만 넘기고, 1을 넘는 색은
            // 여기서 만든다. RearSkin의 HeatTint와 같은 값이어야 앞뒤가 같은 열로 보인다.
            half3 HeatTint(float t)
            {
                const half3 c0 = half3(1.00, 1.00, 1.00);
                const half3 c1 = half3(1.30, 0.34, 0.16);
                const half3 c2 = half3(2.40, 0.85, 0.22);
                const half3 c3 = half3(3.20, 1.60, 0.60);
                const half3 c4 = half3(4.20, 3.20, 2.40);

                float tt = saturate(t) * 4.0;
                float lo = floor(min(tt, 3.0));
                float f = tt - lo;

                half3 a = lo < 0.5 ? c0 : (lo < 1.5 ? c1 : (lo < 2.5 ? c2 : c3));
                half3 b = lo < 0.5 ? c1 : (lo < 1.5 ? c2 : (lo < 2.5 ? c3 : c4));
                return lerp(a, b, f);
            }

            Varyings LitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonLitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;

                return o;
            }

            // CPU판 Repaint의 픽셀 규칙 그대로: 사각형·실루엣 밖 버림, 서브셀 HP로 erode,
            // 배 그림 색.
            half4 PlateColor(Varyings i)
            {
                float2 local = _LocalMin.xy + i.uv * _LocalMin.zw;

                // 콜라이더 사각형 밖 - 쿼드가 픽셀 반올림만큼 넓어서 여기서 자른다
                if (abs(local.x - _Rect.x) > _Rect.z || abs(local.y - _Rect.y) > _Rect.w)
                    discard;

                // 판의 실물 모양(폴리곤 판만). Armor.InsideShape를 구운 마스크다.
                if (_HasShape > 0.5)
                {
                    float2 shapeUV = (local - _LocalMin.xy) * _ShapeScale.xy;

                    if (SAMPLE_TEXTURE2D(_ShapeMask, sampler_ShapeMask, shapeUV).r < 0.5)
                        discard;
                }

                // 서브셀. Ballistics.SubIndex와 같은 수식(floor + 경계 clamp) - 6은 SubGrid.
                // texel 중심을 명시해서 샘플한다. 쿼드 픽셀 격자와 서브셀 경계가 겹칠 수
                // 있어서, 경계 위 샘플을 하드웨어 반올림에 맡기면 CPU 수식과 한 칸 어긋난다.
                float2 subCell = min(floor(saturate((local - _Cell.xy) / _Cell.zw + 0.5) * 6.0), 5.0);
                float2 subUV = (subCell + 0.5) / 6.0;
                float f = SAMPLE_TEXTURE2D(_DamageMask, sampler_DamageMask, subUV).r;

                // 죽은 칸은 통째로, 깎인 칸은 가장자리부터. 노이즈는 로컬 좌표를 픽셀
                // 격자로 양자화한 해시 - 판이 움직여도 무늬가 판에 붙어 다닌다.
                float2 grainCell = floor(local * _GrainPpu) + _GrainSeed.xy;
                float g = frac(sin(dot(grainCell, float2(12.9898, 78.233))) * 43758.5453);
                float t = _ErodeBelow > 0 ? saturate(f / _ErodeBelow) : 1;

                if (f <= 0 || g > t)
                    discard;

                float2 hullUV = float2(dot(_HullA.xy, local), dot(_HullA.zw, local)) + _HullB.xy;
                half4 art = SAMPLE_TEXTURE2D(_HullTex, sampler_HullTex, hullUV);
                half4 flat = lerp(_Damaged, _Healthy, f);

                return lerp(flat, art, _HasHull);
            }

            half4 LitFragment(Varyings input) : SV_Target
            {
                const half4 main = input.color * PlateColor(input);
                const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, input.uv);
                const half3 normalTS = half3(0, 0, 1);

                SurfaceData2D surfaceData;
                InputData2D inputData;

                InitializeSurfaceData(main.rgb, main.a, mask, normalTS, surfaceData);
                InitializeInputData(input.uv, input.lightingUV, inputData);

                #if defined(DEBUG_DISPLAY)
                SETUP_DEBUG_TEXTURE_DATA_2D_NO_TS(inputData, input.positionWS, input.positionCS, _MainTex);
                #endif

                half4 lit = CombinedShapeLightShared(surfaceData, inputData);

                // **조명 합성 뒤에 더한다.** CombinedShapeLightShared는 2D 라이트 세기로
                // albedo를 곱하므로, 라이트가 어두운 자리에서는 albedo에 아무리 큰 값을
                // 넣어도 도로 눌린다. 적열은 스스로 내는 빛이라 그 곱셈 밖에 있어야 하고,
                // 그래야 Bloom 문턱을 실제로 넘는다.
                // 배율은 Bloom 문턱을 겨우 넘는 선. 올리면 갓 뜯긴 단면이 화면을 태운다.
                // RearSkin과 같은 값이어야 앞뒤가 같은 열로 보인다.
                if (_Heat > 0.001)
                    lit.rgb += HeatTint(_Heat) * _Heat * 1.0;

                return lit;
            }
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
