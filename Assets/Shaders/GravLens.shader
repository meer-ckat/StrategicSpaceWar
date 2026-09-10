// 중력 렌즈 - 배경 카메라 컬러를 한 번 거른다. 블랙홀 구(투명 큐)가 그려지기 **전**에 돌아서
// 별과 성운만 휘고 블랙홀 자체는 안 휜다. 구 안은 BlackHoleRaymarch가 덮어쓰므로 이 패스는 구 밖의 약장이다.
//
// 각도 공간에서 푼다: 화면 방향 d와 블랙홀 방향 h 사이 각 θ에서 보이는 것은 원래 β = θ - α에 있던 것이다.
// 관측자가 D에 있고 광원이 무한원이면 α = (r_s/D)·cot(θ/2). 카메라에서 출발해 구를 적분하고 나가는
// 레이마치와 같은 양이라 구 테두리에서 별이 안 뛴다. 2차항 15π/16·(r_s/b)²는 비율로 곱한다(b = D sinθ).
// 뷰포트 좌표를 각도로 쓰면 30도에서 편향이 1/3 틀린다 - 그래서 방향 벡터를 세운다.
// 배율 μ = (sinθ/sinβ)/(dβ/dθ)는 β = 0(아인슈타인 링)에서 발산하므로 _Lens.y에서 자른다.
Shader "SUPERRADIANCE/GravLens"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "GravLens"

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float4 _LensCentre;   // xy 블랙홀 자리(뷰포트), z 종횡비(가로/세로)
            float4 _Lens;         // x r_s/D, y 배율 상한, z 앞 물체 컷(m), w 1/(2 tan(fov/2))

            bool Foreground(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams) < _Lens.z;
            }

            // 휜 자리용. 바이리니어 반 텍셀 번짐까지 앞 물체로 친다(BlackHoleRaymarch와 같은 이유).
            bool ForegroundNear(float2 uv)
            {
                float2 t = _CameraDepthTexture_TexelSize.xy * 0.5;
                return Foreground(uv + float2(-t.x, -t.y)) || Foreground(uv + float2(t.x, -t.y))
                    || Foreground(uv + float2(-t.x, t.y)) || Foreground(uv + float2(t.x, t.y));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                // 컷보다 가까운 오파크는 앞 물체다 - 안 휜다. 휜 자리에 있는 앞 물체는 배경이 아니라 검다.
                if (Foreground(i.texcoord))
                    return half4(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb, 1);

                float2 fix = float2(_LensCentre.z, 1.0) / _Lens.w;   // 뷰포트 → tan(각)
                float3 d = normalize(float3((i.texcoord - 0.5) * fix, 1.0));
                float3 h = normalize(float3((_LensCentre.xy - 0.5) * fix, 1.0));

                float cosT = dot(d, h);
                float sinT = max(sqrt(saturate(1.0 - cosT * cosT)), 1e-3);
                float theta = atan2(sinT, cosT);
                float k = _Lens.x;
                float alpha = k * (1.0 + cosT) / sinT * (1.0 + 15.0 * PI / 32.0 * k / sinT);
                float beta = theta - alpha;

                // h 축에서 d 쪽으로 β만큼 돈 방향이 광원이다. β < 0이면 반대편 - 뒤집힌 상.
                float3 e = (d - h * cosT) / sinT;
                float3 src = h * cos(beta) + e * sin(beta);

                if (src.z <= 1e-3)
                    return half4(0, 0, 0, 1);   // 카메라 뒤로 휜 별. 화면에 없다.

                float2 uv2 = saturate(0.5 + src.xy / src.z / fix);
                half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv2).rgb * !ForegroundNear(uv2);

                float mag = sinT / max(abs(sin(beta)), 1e-3) / (1.0 + k / max(1.0 - cosT, 1e-4));
                c *= min(mag, _Lens.y);

                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
