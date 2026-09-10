// Schwarzschild 궤도의 중심력 표현을 카메라부터 적분한다(r_s = 1).
// 배경 전용: 깊이를 무시한다. 구 반지름은 원반 바깥 반지름의 1.25배 이상, 로컬 Z가 원반 법선.
// 회전은 원반에만 적용하며 Kerr 시공간의 프레임 끌림은 포함하지 않는다.
Shader "SUPERRADIANCE/BlackHoleRaymarch"
{
    Properties
    {
        _Rs("Schwarzschild radius (m)", Float) = 120
        _Inner("Disk inner (x r_s)", Range(1.5, 6)) = 3
        _Outer("Disk outer (x r_s)", Range(3, 12)) = 6
        _Glow("Glow (HDR)", Float) = 2.5
        _Beam("Doppler colour / beaming", Range(0, 0.9)) = 0.6
        _Speed("Swirl speed", Float) = 0.35
        _Spin("Spin direction", Range(-1, 1)) = 1
        [HDR] _ApproachColor("Approaching colour", Color) = (0.3, 0.6, 1.0, 1)
        [HDR] _RecedeColor("Receding colour", Color) = (1.0, 0.12, 0.025, 1)
        _Steps("March steps", Range(40, 300)) = 160
        _StepLen("Step (x r_s)", Range(0.05, 0.5)) = 0.16
        _HotColor("Inner colour", Color) = (1.0, 0.92, 0.75, 1)
        _CoolColor("Outer colour", Color) = (1.0, 0.32, 0.08, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Blend One OneMinusSrcAlpha
        Cull Front
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "BlackHole"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float _Rs, _Inner, _Outer, _Glow, _Beam, _Speed, _Spin, _Steps, _StepLen;
            float4 _HotColor, _CoolColor, _ApproachColor, _RecedeColor;
            CBUFFER_END

            // m. 이보다 가까운 오파크는 배경이 아니라 앞 물체다 - 블랙홀을 가리고, 렌즈는 안 받는다.
            // 값은 GravLensFeature.Create가 넣는다(GravLens.ForegroundCut). 0이면 컷 없음.
            float _LensForeground;

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            #define ANG_PERIOD 24.0

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float ix0 = i.x - ANG_PERIOD * floor(i.x / ANG_PERIOD);
                float ix1 = (i.x + 1.0) - ANG_PERIOD * floor((i.x + 1.0) / ANG_PERIOD);
                float a = Hash(float2(ix0, i.y)), b = Hash(float2(ix1, i.y));
                float c = Hash(float2(ix0, i.y + 1.0)), d = Hash(float2(ix1, i.y + 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 원반 평면의 한 점(r_s 단위, 평면 로컬 xy). 빛을 낸다. 도플러는 접선 vs 광선.
            float3 Disk(float2 q, float3 tangentDir, float3 rayDir)
            {
                float rd = length(q);
                float u = (rd - _Inner) / max(_Outer - _Inner, 1e-4);
                if (u < 0.0 || u > 1.0)
                    return 0.0;

                float th = atan2(q.y, q.x);
                float t = _Time.y;
                float heat = pow(saturate(1.0 - u), 1.6);
                float3 base = lerp(_CoolColor.rgb, _HotColor.rgb, heat);

                float spin = t * _Speed * _Spin / max(rd * 0.3, 0.05);
                float2 np = float2((th + spin) * ANG_PERIOD / 6.2831853 + rd * 1.5, log(max(rd, 1e-3)) * 9.0);
                float n = Noise(np) * 0.6 + Noise(np * 2.0 + 7.1) * 0.3 + Noise(np * 4.0 + 3.3) * 0.1;
                float bands = 0.3 + 1.3 * n;

                float edgeIn = smoothstep(0.0, 0.05, u);
                float edgeOut = 1.0 - smoothstep(0.5, 1.0, u);
                float intensity = (0.35 + 2.6 * heat) * bands * edgeIn * edgeOut;

                // 저비용 색 이동 근사. 회전 속도·방향·반지름·광선 방향을 함께 반영한다.
                float shift = clamp(_Beam * (_Speed / 0.35) * sqrt(3.0 / max(rd, 1.5))
                    * dot(tangentDir, -rayDir), -0.9, 0.9);
                float beam = pow(1.0 + shift, 2.5);
                float near = 1.0 - smoothstep(0.0, 0.25, u);
                base *= lerp(1.0, float3(1.0, 0.45, 0.3) * 0.6, near * near);
                base = lerp(base, _ApproachColor.rgb, saturate(shift * 1.8));
                base = lerp(base, _RecedeColor.rgb, saturate(-shift * 1.8));
                return base * intensity * beam;
            }

            bool Foreground(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams) < _LensForeground;
            }

            // 휜 자리용. 컬러는 바이리니어라 앞 물체 가장자리 반 텍셀이 배경 픽셀에 번지는데, 그 별이
            // HDR 2000배라 5%만 섞여도 시안 조각이 된다. 네 귀퉁이 중 하나라도 앞 물체면 앞 물체다.
            bool ForegroundNear(float2 uv)
            {
                float2 t = _CameraDepthTexture_TexelSize.xy * 0.5;
                return Foreground(uv + float2(-t.x, -t.y)) || Foreground(uv + float2(t.x, -t.y))
                    || Foreground(uv + float2(-t.x, t.y)) || Foreground(uv + float2(t.x, t.y));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uvHere = GetNormalizedScreenSpaceUV(i.positionCS);
                if (Foreground(uvHere))
                    return half4(SampleSceneColor(uvHere), 1.0);

                float3 centre = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float3 ax = normalize(unity_ObjectToWorld._m00_m10_m20);   // 원반 평면 x
                float3 ay = normalize(unity_ObjectToWorld._m01_m11_m21);   // 원반 평면 y
                float3 an = normalize(unity_ObjectToWorld._m02_m12_m22);   // 원반 법선

                // 경계 반지름(r_s 단위). 구 프리미티브 반지름 0.5 x 스케일.
                float rs = max(abs(_Rs), 0.001);
                float bound = 0.5 * length(unity_ObjectToWorld._m00_m10_m20) / rs;

                float3 dir = normalize(i.positionWS - _WorldSpaceCameraPos);
                // 카메라부터 적분해야 경계 구 크기에 따라 그림자가 달라지지 않는다.
                float3 pos = (_WorldSpaceCameraPos - centre) / rs;
                float3 vel = dir;

                float h2 = dot(cross(pos, vel), cross(pos, vel));
                float3 col = 0.0;
                float trans = 1.0;      // 남은 투과율
                float captured = 0.0;
                float prevZ = dot(pos, an);

                int steps = (int)clamp(_Steps, 40.0, 300.0);

                [loop]
                for (int k = 0; k < steps; k++)
                {
                    float r = length(pos);
                    if (r < 1.0) { captured = 1.0; break; }
                    if (r > bound && dot(pos, vel) > 0.0) break;

                    // Velocity Verlet. 속력을 정규화하면 각운동량과 포획 궤도가 깨진다.
                    float dt = clamp(_StepLen, 0.05, 0.5) * clamp(r * 0.5, 0.35, 12.0);
                    float r2 = r * r;
                    float3 acc = -1.5 * h2 * pos / (r2 * r2 * r);
                    float3 next = pos + vel * dt + 0.5 * acc * dt * dt;
                    float nr = max(length(next), 0.5);
                    float3 nextAcc = -1.5 * h2 * next / (nr * nr * nr * nr * nr);
                    vel += 0.5 * (acc + nextAcc) * dt;

                    // 원반 평면(z = 0) 교차. 이번 스텝 안에서 부호가 바뀌면 그 점의 빛을 쌓는다.
                    float z = dot(next, an);
                    if (prevZ * z < 0.0)
                    {
                        float f = prevZ / (prevZ - z);
                        float3 hit = lerp(pos, next, f);
                        float2 q = float2(dot(hit, ax), dot(hit, ay));
                        float3 radial = normalize(hit - an * dot(hit, an));
                        float3 tangent = cross(an, radial) * _Spin;
                        float3 emit = Disk(q, tangent, normalize(vel)) * max(_Glow, 0.0);
                        float u = (length(q) - _Inner) / max(_Outer - _Inner, 0.001);
                        float cover = smoothstep(0.0, 0.05, u) * (1.0 - smoothstep(0.5, 1.0, u));
                        col += emit * cover * trans;
                        trans *= 1.0 - cover;
                    }

                    prevZ = z;
                    pos = next;
                }

                // 빠져나간 광선은 그 방향의 별을 읽는다 - 별의 렌즈가 원반과 같은 측지선에서 나온다.
                // 경계 밖에 남은 약장 편향 (1/b)(1 - z/r)를 해석적으로 더한다. GravLens와 같은 약장식이라
                // 구 테두리에서 별이 안 뛴다.
                if (captured < 0.5)
                {
                    float3 v = normalize(vel);
                    float r = length(pos);
                    float z = dot(pos, v);
                    float b = max(length(cross(pos, v)), 1e-3);
                    float3 n = (v * z - pos) / b;
                    v = normalize(v + n * (1.0 - z / r) / b);

                    float4 clip = mul(UNITY_MATRIX_VP, float4(_WorldSpaceCameraPos + v * 1e4, 1.0));
                    float4 sp = ComputeScreenPos(clip);
                    float2 uv = sp.xy / max(sp.w, 1e-4);
                    // ponytail: 화면 밖(카메라 뒤로 휜 별)은 검다. 큐브맵 하늘이 생기면 거기서 읽는다.
                    // 휜 자리에 앞 물체가 있으면 그 뒤는 모른다 - 역시 검다.
                    float onScreen = clip.w > 0.0 && all(uv > 0.0) && all(uv < 1.0) && !ForegroundNear(saturate(uv));
                    col += SampleSceneColor(uv) * onScreen * trans;
                }

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
