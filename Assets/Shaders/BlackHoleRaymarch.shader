// Schwarzschild 궤도의 중심력 표현을 카메라부터 적분한다(r_s = 1).
// 블랙홀 앞의 불투명 물체는 깊이 텍스처로 보존한다. 구 반지름은 원반 바깥 반지름의 1.25배 이상.
// 로컬 Z가 원반 법선. 구의 빈 공간은 투명하며 배경 렌즈는 GravLens가 처리한다.
// 회전은 원반에만 적용하며 Kerr 시공간의 프레임 끌림은 포함하지 않는다.
Shader "SUPERRADIANCE/BlackHoleRaymarch"
{
    Properties
    {
        _Rs("Schwarzschild radius (m)", Float) = 120
        _Inner("Disk inner (x r_s, stable orbit >= 3)", Range(3, 6)) = 3
        _Outer("Disk outer (x r_s)", Range(3, 12)) = 6
        _Glow("Glow (HDR)", Float) = 2.5
        _Beam("Frequency colour tint (artistic)", Range(0, 0.9)) = 0.6
        _Speed("Animation rate at 3 r_s (rad/s)", Range(0, 3)) = 0.75
        _Structure("Moving disk structure", Range(0, 1)) = 0.85
        _Spin("Spin direction", Range(-1, 1)) = 1
        [HDR] _ApproachColor("Approaching colour", Color) = (0.3, 0.6, 1.0, 1)
        [HDR] _RecedeColor("Receding colour", Color) = (1.0, 0.12, 0.025, 1)
        _Steps("March steps", Range(40, 512)) = 300
        _StepLen("Step (x r_s)", Range(0.02, 0.12)) = 0.08
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float _Rs, _Inner, _Outer, _Glow, _Beam, _Speed, _Spin, _Steps, _StepLen;
            float _Structure;
            float4 _HotColor, _CoolColor, _ApproachColor, _RecedeColor;
            CBUFFER_END


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

            float DiskStructure(float th, float rd, float age)
            {
                float phase = th - age * max(_Speed, 0.0) * _Spin * pow(3.0 / rd, 1.5);
                float2 np = float2(phase * ANG_PERIOD / 6.2831853 + rd * 0.6, rd * 2.0);
                float n = Noise(np) * 0.65 + Noise(np * 2.0 + 7.1) * 0.35;
                float arm = pow(0.5 + 0.5 * sin(3.0 * phase + 4.0 * log(rd)), 4.0);
                float knot = pow(0.5 + 0.5 * cos(phase - 1.3), 8.0);
                return 0.18 + 1.1 * n + 1.5 * arm + 1.8 * knot;
            }

            // 원반 평면의 한 점(r_s 단위, 평면 로컬 xy). 빛을 낸다. 도플러는 접선 vs 광선.
            float3 Disk(float2 q, float3 tangentDir, float3 rayDir, float observerLapse)
            {
                float rd = length(q);
                float u = (rd - _Inner) / max(_Outer - _Inner, 1e-4);
                if (u < 0.0 || u > 1.0)
                    return 0.0;

                float th = atan2(q.y, q.x);
                float t = _Time.y;
                float heat = pow(saturate(1.0 - u), 1.6);
                float3 base = lerp(_CoolColor.rgb, _HotColor.rgb, heat);

                // Kepler 각속도 비율. 시간 배율은 재생 속도만 바꾸며 도플러 속도와 분리한다.
                // 두 무늬를 교차 갱신해 전단이 무한히 쌓여 가는 띠로 변하는 것을 피한다.
                float cycle = frac(t / 12.0);
                float blend = 0.5 - 0.5 * cos(cycle * 6.2831853);
                float structure = lerp(DiskStructure(th, rd, frac(cycle + 0.5) * 12.0),
                    DiskStructure(th, rd, cycle * 12.0), blend);
                float bands = lerp(1.0, structure, _Structure);

                float edgeIn = smoothstep(0.0, 0.05, u);
                float edgeOut = 1.0 - smoothstep(0.5, 1.0, u);
                float intensity = (0.35 + 2.6 * heat) * bands * edgeIn * edgeOut;

                // 정지 관측자와 원궤도 물질 사이 주파수 비. rayDir은 방출 지점의 로컬 정규직교 방향.
                float beta = rsqrt(2.0 * max(rd - 1.0, 0.5));
                float speed2 = beta * beta * dot(tangentDir, tangentDir);
                float doppler = sqrt(max(1.0 - speed2, 0.001))
                    / max(1.0 - beta * dot(tangentDir, -rayDir), 0.001);
                float g = sqrt(max(1.0 - 1.0 / rd, 0.001)) / observerLapse * doppler;
                float beam = g * g * g * g; // 주파수 적분 강도 전달. 색 팔레트는 예술적 근사를 유지한다.
                float shift = log2(max(g, 0.001)) * _Beam;
                base = lerp(base, _ApproachColor.rgb, saturate(shift * 1.8));
                base = lerp(base, _RecedeColor.rgb, saturate(-shift * 1.8));
                return base * intensity * beam;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uvHere = GetNormalizedScreenSpaceUV(i.positionCS);
                float3 centre = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float holeDepth = -TransformWorldToView(centre).z;
                if (LinearEyeDepth(SampleSceneDepth(uvHere), _ZBufferParams) < holeDepth)
                    discard;
                float3 ax = normalize(unity_ObjectToWorld._m00_m10_m20);   // 원반 평면 x
                float3 ay = normalize(unity_ObjectToWorld._m01_m11_m21);   // 원반 평면 y
                float3 an = normalize(unity_ObjectToWorld._m02_m12_m22);   // 원반 법선

                // 경계 반지름(r_s 단위). 구 프리미티브 반지름 0.5 x 스케일.
                float rs = max(abs(_Rs), 0.001);
                float bound = 0.5 * length(unity_ObjectToWorld._m00_m10_m20) / rs;

                float3 dir = normalize(i.positionWS - _WorldSpaceCameraPos);
                // 카메라부터 적분해야 경계 구 크기에 따라 그림자가 달라지지 않는다.
                float3 pos = (_WorldSpaceCameraPos - centre) / rs;
                float observerLapse = sqrt(max(1.0 - 1.0 / max(length(pos), 1.001), 0.001));
                float3 observerRadial = normalize(pos);
                float3 radialVelocity = observerRadial * dot(dir, observerRadial);
                float3 vel = radialVelocity + (dir - radialVelocity) / observerLapse;

                float h2 = dot(cross(pos, vel), cross(pos, vel));
                float3 col = 0.0;
                float trans = 1.0;      // 남은 투과율
                float captured = 0.0;
                float prevZ = dot(pos, an);

                int steps = (int)clamp(_Steps, 40.0, 512.0);

                [loop]
                for (int k = 0; k < steps; k++)
                {
                    float r = length(pos);
                    if (r < 1.0) { captured = 1.0; break; }
                    if (r > bound && dot(pos, vel) > 0.0) break;

                    // Velocity Verlet. 속력을 정규화하면 각운동량과 포획 궤도가 깨진다.
                    float dt = clamp(_StepLen, 0.02, 0.12) * clamp(r * 0.5, 0.35, 12.0);
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
                        float3 outward = normalize(hit);
                        float3 vr = outward * dot(vel, outward);
                        float localLapse = sqrt(max(1.0 - 1.0 / length(hit), 0.001));
                        float3 localRay = normalize(vr + (vel - vr) * localLapse);
                        float3 emit = Disk(q, tangent, localRay, observerLapse) * max(_Glow, 0.0);
                        float u = (length(q) - _Inner) / max(_Outer - _Inner, 0.001);
                        float cover = smoothstep(0.0, 0.05, u) * (1.0 - smoothstep(0.5, 1.0, u));
                        col += emit * cover * trans;
                        trans *= 1.0 - cover;
                    }

                    prevZ = z;
                    pos = next;
                }

                // 배경 왜곡은 GravLens 패스가 담당한다. 포획된 광선만 완전히 가린다.
                return half4(col, captured > 0.5 ? 1.0 : 1.0 - trans);
            }
            ENDHLSL
        }
    }
}
