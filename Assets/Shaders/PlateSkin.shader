// 장갑판 스킨 - GPU판. CPU가 판마다 텍스처를 굽던 것을, 배 그림(공유 텍스처) +
// 6x6 손상 마스크 + 절차 노이즈의 조합으로 프래그먼트에서 그린다.
//
// 규칙의 원본은 C#(ArmorSkin)이다. 서브셀 격자·판 사각형·배 그림 대응은 전부 C#이
// 계산해 상수(_Cell, _Rect, _HullA/B)로 넘기고, 여기는 그 숫자를 조합만 한다 -
// 수식이 두 벌이 되지 않게 하는 선이 그것이다.
//
// URP 2D 라이팅(Universal2D 패스)을 쓴다 - 절단면 Light2D가 판을 비추는 경로 유지.
// 노말맵 패스와 포워드 폴백 패스는 뺐다: 판은 노말맵을 쓴 적이 없고, 렌더러는 2D뿐이다.
Shader "SUPERRADIANCE/PlateSkin"
{
    Properties
    {
        _MainTex("Sprite", 2D) = "white" {}               // 공유 흰 텍스처. 지오메트리용
        [HideInInspector] _Color("Tint", Color) = (1,1,1,1)
        _MaskTex("Mask", 2D) = "white" {}                 // URP 2D 라이팅 마스크 규약
        _HullTex("Hull Art", 2D) = "white" {}
        _DamageMask("Damage Mask", 2D) = "white" {}       // 6x6 R8, 서브셀 HP
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
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex CombinedShapeLightVertex
            #pragma fragment CombinedShapeLightFragment

            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_0 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_1 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_2 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_3 __
            #pragma multi_compile _ DEBUG_DISPLAY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                half2 lightingUV : TEXCOORD1;
                #if defined(DEBUG_DISPLAY)
                float3 positionWS : TEXCOORD2;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/LightingUtility.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            TEXTURE2D(_HullTex);
            SAMPLER(sampler_HullTex);
            TEXTURE2D(_DamageMask);
            SAMPLER(sampler_DamageMask);

            half4 _Color;
            half4 _Damaged;
            half4 _Healthy;
            float4 _LocalMin;
            float4 _Rect;
            float4 _Cell;
            float4 _HullA;
            float4 _HullB;
            float4 _GrainSeed;
            float _ErodeBelow;
            float _HasHull;
            float _GrainPpu;

            #if USE_SHAPE_LIGHT_TYPE_0
            SHAPE_LIGHT(0)
            #endif
            #if USE_SHAPE_LIGHT_TYPE_1
            SHAPE_LIGHT(1)
            #endif
            #if USE_SHAPE_LIGHT_TYPE_2
            SHAPE_LIGHT(2)
            #endif
            #if USE_SHAPE_LIGHT_TYPE_3
            SHAPE_LIGHT(3)
            #endif

            Varyings CombinedShapeLightVertex(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = TransformObjectToHClip(v.positionOS);
                #if defined(DEBUG_DISPLAY)
                o.positionWS = TransformObjectToWorld(v.positionOS);
                #endif
                o.uv = v.uv;
                o.lightingUV = half2(ComputeScreenPos(o.positionCS / o.positionCS.w).xy);
                o.color = v.color * _Color * unity_SpriteColor;
                return o;
            }

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/CombinedShapeLightShared.hlsl"

            // CPU판 Repaint의 픽셀 규칙 그대로: 사각형 밖 버림, 서브셀 HP로 erode, 배 그림 색.
            half4 PlateColor(Varyings i)
            {
                float2 local = _LocalMin.xy + i.uv * _LocalMin.zw;

                // 콜라이더 사각형 밖 - 쿼드가 픽셀 반올림만큼 넓어서 여기서 자른다
                if (abs(local.x - _Rect.x) > _Rect.z || abs(local.y - _Rect.y) > _Rect.w)
                    discard;

                // 서브셀. Ballistics.SubIndex와 같은 수식(경계 clamp 포함)
                float2 subUV = saturate((local - _Cell.xy) / _Cell.zw + 0.5);
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

            half4 CombinedShapeLightFragment(Varyings i) : SV_Target
            {
                const half4 main = i.color * PlateColor(i);
                const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv);

                SurfaceData2D surfaceData;
                InputData2D inputData;

                InitializeSurfaceData(main.rgb, main.a, mask, surfaceData);
                InitializeInputData(i.uv, i.lightingUV, inputData);

                return CombinedShapeLightShared(surfaceData, inputData);
            }
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
