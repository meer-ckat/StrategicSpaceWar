// 부스터 꼬리 - GPU판. 노즐이 뱉는 불꽃 하나하나가 예전에는 Light2D를 단 GameObject였고,
// 45 ms마다 부스터마다 하나씩 태어났다(destroyer 12개 + lance 16개면 초당 1,300개, 동시
// 생존 수백 개). 여기서는 그 불꽃이 링버퍼의 구조체 한 줄이고, 그리는 것은 절차 드로 하나다.
//
// **C#은 태어난 자리와 시각만 적는다.** 얼마나 퍼지고 얼마나 식었는지는 프레임마다
// 정점 셰이더가 `_Now - born`으로 다시 계산한다 - CPU가 매 프레임 되짚을 것이 없다.
// 감쇠 곡선((1-t)^2)은 Flash.Update의 것을 그대로 옮긴 것이다.
//
// 시간을 `_Time.y`가 아니라 `_Now`로 받는 이유: C#의 Time.time과 셰이더의 _Time.y는
// 기준이 미묘하게 다르고, 어긋나면 증상이 "불꽃이 안 보인다" 하나뿐이라 안 잡힌다.
//
// LightMode 태그가 없는 패스는 SRPDefaultUnlit으로 잡히고, URP의 Renderer2D는 그것을
// 투명 패스에서 그린다. 적열과 같은 이유로 2D 라이팅을 안 탄다 - 스스로 내는 빛은
// albedo 곱셈 밖에 있어야 어두운 데서도 Bloom 문턱을 넘는다.
Shader "SUPERRADIANCE/BoosterTrail"
{
    Properties
    {
        _Now("Now (seconds)", Float) = 0
        _Life("Life", Float) = 0.28
        _StartRadius("Start Radius", Float) = 1.6
        _EndRadius("End Radius", Float) = 7.0
        _Intensity("Intensity (HDR)", Float) = 6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend One One      // 가산. 겹칠수록 밝아지는 것이 불꽃이다
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5   // 정점 셰이더의 StructuredBuffer

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // C#의 BoosterTrail.Puff와 **필드 순서·크기가 같아야 한다.** float 6개 = 24바이트.
            struct Puff
            {
                float2 position;
                float born;
                float3 colour;
            };

            StructuredBuffer<Puff> _Puffs;

            float _Now;
            float _Life;
            float _StartRadius;
            float _EndRadius;
            float _Intensity;

            static const float2 Corners[6] =
            {
                float2(-1, -1), float2(-1,  1), float2( 1,  1),
                float2(-1, -1), float2( 1,  1), float2( 1, -1)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 local      : TEXCOORD0;
                float3 colour     : TEXCOORD1;
            };

            Varyings vert(uint vertexId : SV_VertexID)
            {
                Varyings o;

                Puff p = _Puffs[vertexId / 6];

                float t = (_Now - p.born) / max(1e-3, _Life);

                // 죽었거나 아직 안 태어난 슬롯: 클립 공간 밖으로 보내 삼각형째로 잘라낸다.
                // 링버퍼를 CPU가 청소하지 않아도 되는 것이 이 한 줄 덕이다.
                if (t < 0.0 || t >= 1.0)
                {
                    o.positionCS = float4(2, 2, 2, 1);
                    o.local = 0;
                    o.colour = 0;
                    return o;
                }

                float2 corner = Corners[vertexId % 6];
                float radius = lerp(_StartRadius, _EndRadius, t);

                o.positionCS = TransformWorldToHClip(float3(p.position + corner * radius, 0));
                o.local = corner;

                // 제곱으로 꺼진다. 선형이면 끝이 질질 끌려서 연기처럼 보인다.
                float fade = 1.0 - t;
                o.colour = p.colour * (_Intensity * fade * fade);

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 텍스처 없이 원형 감쇠. 사각형 모서리가 안 보이면 그만이다.
                float falloff = saturate(1.0 - dot(i.local, i.local));

                return half4(i.colour * falloff * falloff, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
