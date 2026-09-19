// 정지 회로도의 바닥. 화면을 덮는 쿼드(카메라 자식) 위에 월드 좌표 1 m 격자를 VOID 바탕으로 그린다.
// 판의 건조 와이어프레임(PlateSkin _BuildFront)과 같은 선이 지나간다 - 플레이어 배 로컬 축(x+y)이
// _Front를 넘은 쪽만 덮는다. 같은 값(PauseControl.Front)을 받으므로 배와 배경이 한 선으로 갈린다.
Shader "SUPERRADIANCE/Blueprint"
{
    Properties
    {
        _Void("Ground", Color) = (0.031, 0.051, 0.078, 1)
        _Steel("Line", Color) = (0.443, 0.522, 0.549, 1)
        _Dim("Cover Alpha", Float) = 1
        _GridAlpha("Grid Alpha", Float) = 0.18
        _Front("Build Front", Float) = 1000000
        _Band("Front Band (m)", Float) = 3
        _Fade("Fade", Float) = 1
        _ShipPos("Ship Pos", Vector) = (0, 0, 0, 0)
        _ShipRot("Ship Rot (cos, sin)", Vector) = (1, 0, 0, 0)
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

        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _Void;
            half4 _Steel;
            float _Dim;
            float _GridAlpha;
            float _Front;
            float _Band;
            float _Fade;
            float4 _ShipPos;
            float4 _ShipRot;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 world      : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 w = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(w);
                o.world = w.xy;
                return o;
            }

            // 격자선 한 겹. 간격 step(m), 픽셀 폭으로 안티에일리어스.
            float Grid(float2 w, float step)
            {
                float2 p = w / step;
                float2 g = abs(frac(p - 0.5) - 0.5) / fwidth(p);
                return 1.0 - saturate(min(g.x, g.y));
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 배 로컬 축. R^T (world - shipPos) 의 x + y.
                float2 d = i.world - _ShipPos.xy;
                float c = _ShipRot.x, s = _ShipRot.y;
                float2 local = float2(c * d.x + s * d.y, -s * d.x + c * d.y);
                float axis = local.x + local.y;

                float mask = saturate((axis - _Front) / max(1e-3, _Band) + 0.5);

                // 'line'은 HLSL 예약어라 못 쓴다.
                float edge = saturate(Grid(i.world, 1.0) * _GridAlpha + Grid(i.world, 10.0) * _GridAlpha * 1.5);
                half3 rgb = lerp(_Void.rgb, _Steel.rgb, edge);

                return half4(rgb, _Dim * mask * _Fade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
