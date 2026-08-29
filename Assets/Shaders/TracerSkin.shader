// 탄 스킨 - 조명 밖 + HDR. 트레이서는 스스로 내는 빛이라 2D 라이트 곱셈을 안 받고
// (unlit), Bloom을 물리는 1 초과 색은 여기서 태어난다 - 버텍스 컬러(SpriteRenderer.color)는
// Color32로 패킹돼 1을 못 넘는다. 적열의 HeatTint와 같은 규칙이다.
//
// C#은 _Glow(float 하나)만 MaterialPropertyBlock으로 넣는다. tint·체력 어두워짐은
// 기존 버텍스 컬러 경로 그대로라, glow 1이면 Sprite-Unlit-Default와 같은 그림이다.
Shader "SUPERRADIANCE/TracerSkin"
{
    Properties
    {
        _MainTex("Sprite", 2D) = "white" {}
        _Glow("Glow", Float) = 5

        [HideInInspector] _Color("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor("RendererColor", Color) = (1,1,1,1)
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

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float _Glow;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 colour =
                    SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color;

                // 알파는 안 태운다 - 밝기만 문턱을 넘어야지 반투명이 불투명해지면 안 된다.
                colour.rgb *= _Glow;
                return colour;
            }
            ENDHLSL
        }
    }
}
