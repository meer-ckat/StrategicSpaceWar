// 워프 화면 효과 - 풀스크린 한 패스. WarpFxFeature가 카메라 컬러를 이 재질로 한 번 거른다.
//
// 네 가지를 한 샘플링에 합친다. 전부 "배 자리"(_WarpCentre.xy, 뷰포트) 기준이다.
//   1. 당김(_WarpParams.x)   - 배 주변 공간이 배 쪽으로 접힌다. 충전 중 서서히, 점프 직전 최대.
//   2. 별 늘림(_WarpParams.y) - 축(_WarpCentre.zw) 방향으로 화면이 늘어난다. 점프 순간 0.2초.
//   3. 충격 링(_WarpRing)     - 반지름 x, 폭 y에서 바깥으로 밀고(z) RGB가 갈린다(w).
//   4. 국소 발광(_WarpParams.z) - 배 자리가 잠깐 하얗게. 화면 전체 섬광과 달리 "그 자리가 터진다".
//   5. 알쿠비에레 거품(_WarpBubble) - 반지름 R 안은 평평하고(배는 안 일그러진다), 벽에서 앞은 수축·뒤는 팽창.
//      벽 모양은 Alcubierre의 f(r) = (tanh(σ(r+R)) - tanh(σ(r-R))) / 2tanh(σR)의 미분이고, 세기는
//      York time θ = v·(x/r)·f'(r) 그대로다 - 앞뒤 두 잎 모양이 논문의 그 그림이다. 색은 앞이 파랗게 뒤가 붉게(도플러).
//
// 좌표는 세로 기준 정규화(x에 종횡비를 곱한다)라 원이 원으로 나온다. 직교 카메라라 월드
// 방향이 그대로 축이 된다.
Shader "SUPERRADIANCE/WarpFx"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            Name "WarpFx"

            HLSLPROGRAM
            // URP Core가 플랫폼 API와 TEXTURE2D_X 매크로를 가져온다. core의 Common만 넣으면 Blit.hlsl이 못 컴파일된다.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float4 _WarpCentre;   // xy 배 자리(뷰포트), zw 축(단위 벡터)
            float4 _WarpParams;   // x 당김, y 별 늘림, z 발광, w 종횡비(가로/세로)
            float4 _WarpRing;     // x 반지름, y 폭, z 밀기, w RGB 갈림
            float4 _WarpBubble;   // x 반지름 R, y 벽 날카로움 σ(1/폭), z 구동 v(변위량), w 도플러 색 갈림
            float4 _WarpBubble2;  // xy 거품 중심(뷰포트), z 이동 중 어둠(거품 밖을 이만큼 지운다). 링·발광은 출발점에 남고 거품은 배를 따라간다

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float2 stretchFix = float2(_WarpParams.w, 1.0);
                float2 p = (uv - _WarpCentre.xy) * stretchFix;
                float r = length(p);
                float2 radial = r > 1e-4 ? p / r : float2(0, 0);
                float2 axis = _WarpCentre.zw;
                float along = dot(p, axis);

                // 1. 당김. 바깥의 그림을 안으로 끌어와 배 주변이 배 쪽으로 접힌다.
                float2 d = p * (_WarpParams.x * exp(-r * 4.0));

                // 5. 알쿠비에레 거품. 벽 밖에서만 - 안쪽은 평평한 공간이다. (별 늘림보다 먼저 - outside 마스크를 나눠 쓴다)
                float2 pb = (uv - _WarpBubble2.xy) * stretchFix;
                float rb = length(pb);
                float2 radialB = rb > 1e-4 ? pb / rb : float2(0, 0);
                float R = _WarpBubble.x;
                float sigma = max(_WarpBubble.y, 1e-3);
                float wall = 1.0 / cosh(sigma * (rb - R));
                wall *= wall;                                   // sech² = f'(r)의 모양
                float outside = smoothstep(R - 0.5 / sigma, R + 0.25 / sigma, rb);
                float cosA = rb > 1e-4 ? dot(pb, axis) / rb : 0.0;   // x/r
                float york = cosA * wall * outside;              // 부호: 앞 +, 뒤 -
                // 앞(수축): 앞의 그림이 벽으로 끌려온다 = 축 방향으로 더 앞을 샘플. 뒤(팽창): 뒤의 그림이 멀어진다 = 배 쪽을 샘플.
                d += axis * (abs(york) * _WarpBubble.z);
                // 옆구리는 벽을 따라 살짝 미끄러진다 - 두 잎 사이가 죽은 띠로 안 보이게.
                d += radialB * (wall * outside * (1.0 - abs(cosA)) * _WarpBubble.z * 0.25);
                float2 doppler = axis * (york * _WarpBubble.w) / stretchFix;

                // 2. 별 늘림. 축 성분을 줄여 샘플하면 축 방향으로 확대돼 별이 선이 된다.
                // 거품 밖에서만 - 이동 중 배가 같이 늘어나면 안 된다. 거품이 없으면(R = 0) outside는 1이다.
                d -= axis * (along * _WarpParams.y * outside);

                // 3. 충격 링.
                float band = exp(-pow((r - _WarpRing.x) / max(_WarpRing.y, 1e-3), 2.0));
                d += radial * (band * _WarpRing.z);
                float2 split = radial * (band * _WarpRing.w) / stretchFix;

                float2 uv2 = uv + d / stretchFix;
                // 도플러: 앞(york > 0)은 파란 채널이 앞서고 뒤는 붉은 채널이 앞선다.
                half rC = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv2 + split - doppler).r;
                half gC = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv2).g;
                half bC = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv2 - split + doppler).b;
                half3 c = half3(rC, gC, bC);

                // 이동 중: 거품 밖 우주는 인과적으로 끊겨 있다 - 앞은 청색편이로, 뒤는 적색편이로 검다.
                // 별은 늘어난 채 희미하게만 남아 흐르는 빛줄기가 된다.
                c *= 1.0 - _WarpBubble2.z * outside;

                // 거품 벽. 앞은 차갑게 뒤는 뜨겁게 - 값은 작다. 세기는 구동에 비례해서 충전 초반엔 안 보인다.
                half3 wallTint = lerp(half3(1.0, 0.45, 0.25), half3(0.45, 0.75, 1.0), saturate(cosA * 0.5 + 0.5));
                c += wallTint * (wall * outside * _WarpBubble.z * 6.0);

                // 4. 국소 발광.
                c += _WarpParams.z * exp(-r * 6.0);

                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
