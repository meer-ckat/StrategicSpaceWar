// Kerr 광선 추적(RimWorld 모드 BlackHoleCore.cginc에서 역이식). 카메라부터 Mino 시간으로 적분한다.
// 원본과 달라진 점 - URP라 HDR·Bloom이 있어 노출·흰색 태우기·가짜 블룸 안개를 뺐고, 구 밖 배경 휨은
// GravLens가 맡으므로 하늘·GrabPass·광행차 경로가 없다. 블랙홀 앞 불투명 물체는 깊이 텍스처로 보존한다.
// 로컬 Z가 원반 법선(= 스핀 축). 구 반지름은 원반 바깥 반지름의 1.25배 이상.
Shader "SUPERRADIANCE/BlackHoleRaymarch"
{
    Properties
    {
        _Rs("Schwarzschild radius (m)", Float) = 120
        _Kerr("Kerr spin a/M (Gargantua ~ 0.99)", Range(0, 0.999)) = 0.999
        _InnerIsco("Disk inner (x ISCO)", Range(1, 4)) = 2.6
        _Outer("Disk outer (x r_s)", Range(3, 12)) = 10.14
        _Thickness("Disk thickness H/r", Range(0, 0.25)) = 0.047
        _DiskDepth("Disk optical depth (face-on)", Range(0.1, 8)) = 0.1
        _Dust("Dust lanes", Range(0, 1)) = 0.7
        _DustColor("Dust colour", Color) = (0.16, 0.075, 0.035, 1)
        _Streak("Orbital streaks (radial fineness)", Range(8, 120)) = 80.5
        _Glow("Glow (HDR)", Float) = 5.5
        _Beam("Frequency colour tint (artistic)", Range(0, 0.9)) = 0.9
        _Speed("Animation rate at 3 r_s (rad/s)", Range(0, 3)) = 3
        _Structure("Moving disk structure", Range(0, 1)) = 1
        _Spin("Spin direction", Range(-1, 1)) = 0.93
        _FilmLook("Interstellar film look (no Doppler beaming / colour shift)", Range(0, 1)) = 0
        _RingAA("Photon ring anti-aliasing (0 off, 1 on)", Range(0, 1)) = 1
        [HDR] _ApproachColor("Approaching colour", Color) = (0.3, 0.6, 1.0, 1)
        [HDR] _RecedeColor("Receding colour", Color) = (1.0, 0.12, 0.025, 1)
        _Steps("March steps", Range(40, 512)) = 250
        _StepLen("Step (x r_s)", Range(0.02, 0.12)) = 0.054
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
            float _Rs, _Kerr, _InnerIsco, _Outer, _Thickness, _DiskDepth, _Dust, _Streak, _Glow, _Beam, _Speed, _Structure, _Spin;
            float _FilmLook, _RingAA, _Steps, _StepLen;
            float4 _DustColor, _HotColor, _CoolColor, _ApproachColor, _RecedeColor;
            CBUFFER_END

            #define TWO_PI 6.2831853
            #define POLE_ZONE 0.0225

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            // ---- 노이즈 ----

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float NoiseP(float2 p, float period)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float ix0 = i.x - period * floor(i.x / period);
                float ix1 = (i.x + 1.0) - period * floor((i.x + 1.0) / period);
                float a = Hash21(float2(ix0, i.y)), b = Hash21(float2(ix1, i.y));
                float c = Hash21(float2(ix0, i.y + 1.0)), d = Hash21(float2(ix1, i.y + 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 궤도 방향으로 늘어진 결. 옥타브마다 둘레 칸도 두 배라 이음매가 없다.
            float Streaks(float phase, float rd, float seed)
            {
                float2 p = float2(phase / TWO_PI * 16.0, log(rd) * _Streak + seed);
                float period = 16.0, s = 0.0, amp = 0.5;
                for (int k = 0; k < 4; k++)
                {
                    s += amp * NoiseP(p, period);
                    p = float2(p.x * 2.0, p.y * 2.1 + 3.7);
                    period *= 2.0;
                    amp *= 0.5;
                }
                return s / 0.9375;
            }

            float2 GasDust(float th, float rd, float age, float layer)
            {
                float phase = th - age * max(_Speed, 0.0) * abs(_Spin) * pow(3.0 / rd, 1.5);
                return float2(Streaks(phase, rd, layer * 0.35), Streaks(phase + 1.3, rd * 1.07, 91.7 + layer * 0.35));
            }

            // rgb = 방출, a = 불투명도. g = 관측/방출 진동수 비, layer = 두께 안의 높이(-1..1).
            float4 DiskSample(float2 q, float g, float inner, float layer)
            {
                float rd = length(q);
                float u = (rd - inner) / max(_Outer - inner, 1e-4);
                if (u < 0.0 || u > 1.0)
                    return 0.0;

                float th = atan2(q.y, q.x);
                float heat = pow(saturate(1.0 - u), 1.6) * (1.0 - 0.35 * abs(layer));
                float3 base = lerp(_CoolColor.rgb, _HotColor.rgb, heat);

                // 두 무늬를 교차 갱신해 전단이 무한히 쌓여 띠로 변하는 것을 피한다.
                float cycle = frac(_Time.y / 12.0);
                float blend = 0.5 - 0.5 * cos(cycle * TWO_PI);
                float2 gd = lerp(GasDust(th, rd, frac(cycle + 0.5) * 12.0, layer), GasDust(th, rd, cycle * 12.0, layer), blend);
                float gas = lerp(0.6, saturate(pow(gd.x, 1.6) * 2.2), _Structure);
                float dust = _Dust * _Structure * smoothstep(0.52, 0.78, gd.y) * (1.0 + 0.6 * abs(layer));

                float edge = smoothstep(0.0, 0.05, u) * (1.0 - smoothstep(0.5, 1.0, u));
                float intensity = (0.35 + 2.6 * heat) * gas * edge;

                float beam = lerp(g * g * g * g, 1.0, _FilmLook);
                float shift = log2(max(g, 0.001)) * _Beam * (1.0 - _FilmLook);
                base = lerp(base, _ApproachColor.rgb, saturate(shift * 1.8));
                base = lerp(base, _RecedeColor.rgb, saturate(-shift * 1.8));

                float3 emit = base * intensity * beam * (1.0 - 0.92 * saturate(dust));
                emit += _DustColor.rgb * saturate(dust) * (0.2 + heat) * edge * 0.6;
                return float4(emit, saturate(0.2 + 1.1 * gas + 1.6 * dust) * edge);
            }

            float Erf(float x)
            {
                float x2 = x * x;
                float t = 0.147 * x2;
                return sign(x) * sqrt(1.0 - exp(-x2 * (1.2732395 + t) / (1.0 + t)));
            }

            // z0 → z1 직선 구간 길이 ds의 ∫ exp(-z²/2σ²) ds.
            float DiskColumn(float z0, float z1, float ds, float sigma)
            {
                float dz = z1 - z0;
                if (abs(dz) < 1e-4 * sigma)
                    return ds * exp(-z0 * z0 / (2.0 * sigma * sigma));
                float k = 0.70710678 / sigma;
                return ds * sigma * 1.2533141 * (Erf(z1 * k) - Erf(z0 * k)) / dz;
            }

            // ---- Kerr (M = 1, E = 1) ----

            float KerrR(float r, float a, float L, float Q)
            {
                float P = r * r + a * a - a * L;
                return P * P - (r * r - 2.0 * r + a * a) * (Q + (L - a) * (L - a));
            }

            float KerrRPrime(float r, float a, float L, float Q)
            {
                return 4.0 * r * (r * r + a * a - a * L) - (2.0 * r - 2.0) * (Q + (L - a) * (L - a));
            }

            float KerrTheta(float mu, float a, float L, float Q)
            {
                float m2 = mu * mu;
                return Q - (Q + L * L - a * a) * m2 - a * a * m2 * m2;
            }

            float KerrThetaPrime(float mu, float a, float L, float Q)
            {
                return -2.0 * (Q + L * L - a * a) * mu - 4.0 * a * a * mu * mu * mu;
            }

            float KerrPhiDot(float r, float mu, float a, float L)
            {
                float delta = max(r * r - 2.0 * r + a * a, 1e-3);
                return a * (r * r + a * a - a * L) / delta - a + L / max(1.0 - mu * mu, 1e-7);
            }

            float KerrDrag(float r, float a, float L)
            {
                return a * (r * r + a * a - a * L) / max(r * r - 2.0 * r + a * a, 1e-3) - a;
            }

            float KerrIsco(float a)
            {
                float z1 = 1.0 + pow(1.0 - a * a, 1.0 / 3.0) * (pow(1.0 + a, 1.0 / 3.0) + pow(1.0 - a, 1.0 / 3.0));
                float z2 = sqrt(3.0 * a * a + z1 * z1);
                return 3.0 + z2 - sqrt(max((3.0 - z1) * (3.0 + z1 + 2.0 * z2), 0.0));
            }

            float KerrRedshift(float r, float a, float lPhoton)
            {
                float r15 = r * sqrt(r);
                float ut = (r15 + a) / (pow(r, 0.75) * sqrt(max(r15 - 3.0 * sqrt(r) + 2.0 * a, 1e-4)));
                float omega = 1.0 / (r15 + a);
                return 1.0 / (ut * max(1.0 - omega * lPhoton, 1e-3));
            }

            struct RayHit { float3 light; float trans; float captured; float order; };

            // 광선 하나. 원반(스핀 축) 좌표계 ax, ay, an과 경계 구 반지름(r_s)을 받는다.
            // 거꾸로 가는 광선은 스핀 -a 시공간의 앞으로 가는 측지선이다. 적색편이만 물리 스핀 fa로 잰다.
            RayHit Trace(float3 positionWS, float3 centre, float rs, float3 ax, float3 ay, float3 an, float bound)
            {
                float fa = clamp(_Kerr, 0.0, 0.999);
                float a = -fa;
                float horizon = 1.0 + sqrt(1.0 - fa * fa);
                float inner = _InnerIsco * KerrIsco(fa) * 0.5;

                float3 vel = normalize(positionWS - _WorldSpaceCameraPos);
                float3 origin = (_WorldSpaceCameraPos - centre) / rs;
                // 카메라부터 적분해야 경계 구 크기에 따라 그림자가 달라지지 않는다. 출구는 구 또는 카메라 거리 중 먼 쪽.
                float startH = max(bound, 1.01 * length(origin));

                RayHit hit;
                hit.light = 0.0;
                hit.trans = 1.0;
                hit.captured = 0.0;
                hit.order = 0.0;

                float3 p = float3(dot(origin, ax), dot(origin, ay), dot(origin, an)) * 2.0;
                float3 v = float3(dot(vel, ax), dot(vel, ay), dot(vel, an));
                float rho2 = dot(p, p) - a * a;
                float r = sqrt(0.5 * rho2 + sqrt(0.25 * rho2 * rho2 + a * a * p.z * p.z));
                float mu = clamp(p.z / max(r, 1e-4), -1.0, 1.0);
                float phi = atan2(p.y, p.x);

                // 출발점 ZAMO 기준으로 보존량과 Mino 속도를 만든다.
                float sTh = sqrt(max(1.0 - mu * mu, 1e-12));
                float cph = cos(phi), sph = sin(phi);
                float nr = dot(v, float3(sTh * cph, sTh * sph, mu));
                float nth = dot(v, float3(mu * cph, mu * sph, -sTh));
                float nph = dot(v, float3(-sph, cph, 0.0));
                float sig0 = r * r + a * a * mu * mu;
                float dlt0 = max(r * r - 2.0 * r + a * a, 1e-6);
                float A0 = (r * r + a * a) * (r * r + a * a) - a * a * dlt0 * sTh * sTh;
                float alpha0 = sqrt(sig0 * dlt0 / A0);
                float omega0 = 2.0 * a * r / A0;
                float varpi0 = sqrt(A0 / sig0) * sTh;
                float ez = 1.0 / (alpha0 + omega0 * varpi0 * nph);
                float L = nph * varpi0 * ez;
                float pTh = sqrt(sig0) * nth * ez;
                float Q = pTh * pTh + mu * mu * (L * L / (sTh * sTh) - a * a);
                float vr = sqrt(sig0 * dlt0) * nr * ez;
                float vmu = -sTh * pTh;
                float vphi = KerrPhiDot(r, mu, a, L);

                float stepLen = clamp(_StepLen, 0.02, 0.12);
                float startM = 2.0 * startH;
                float H = max(_Thickness, 0.0);
                int steps = (int)clamp(_Steps, 40.0, 512.0);
                bool zone = false;
                float3 zn = 0.0, zu = 0.0;

                [loop]
                for (int k = 0; k < steps; k++)
                {
                    if (r < horizon * 1.02) { hit.captured = 1.0; break; }
                    if (r > startM && vr > 0.0) break;

                    float sigmaK = r * r + a * a * mu * mu;
                    float s2 = 1.0 - mu * mu;
                    float J = sqrt(Q + L * L + a * a * mu * mu);
                    float ds = stepLen * clamp(r * 0.25, 0.35, 24.0);
                    float dl = 2.0 * ds / sigmaK;
                    if (zone || s2 < 4.0 * POLE_ZONE)
                        dl = min(dl, 0.02 / J);
                    if (!zone)
                        dl = min(dl, 0.05 * max(s2, POLE_ZONE) / max(abs(L), 1e-9));

                    float ar = 0.5 * KerrRPrime(r, a, L, Q);
                    float am = 0.5 * KerrThetaPrime(mu, a, L, Q);
                    float muN = mu + vmu * dl + 0.5 * am * dl * dl;
                    float phiN = phi;
                    bool inZone = zone || 1.0 - muN * muN < POLE_ZONE;
                    if (inZone)
                    {
                        // 극 지대: (μ, φ)가 특이하므로 단위벡터 대원 회전으로 적분하고 프레임 끌림을 z축 회전으로 더한다.
                        if (!zone)
                        {
                            float rho = sqrt(max(s2, 1e-12));
                            float c = cos(phi), sn = sin(phi);
                            float drho = -mu * vmu / rho;
                            float ps = L / max(s2, 1e-12);
                            float3 nd = float3(drho * c - rho * sn * ps, drho * sn + rho * c * ps, vmu);
                            zn = float3(rho * c, rho * sn, mu);
                            zu = nd / max(length(nd), 1e-12);
                            zone = true;
                        }
                        float ang = J * dl;
                        float3 n2 = zn * cos(ang) + zu * sin(ang);
                        zu = zu * cos(ang) - zn * sin(ang);
                        zn = n2;
                        float w = KerrDrag(r, a, L) * dl;
                        float cw = cos(w), sw = sin(w);
                        zn = float3(zn.x * cw - zn.y * sw, zn.x * sw + zn.y * cw, zn.z);
                        zu = float3(zu.x * cw - zu.y * sw, zu.x * sw + zu.y * cw, zu.z);
                        zn = normalize(zn);
                        zu = normalize(zu - dot(zu, zn) * zn);
                        muN = zn.z;
                        phiN = atan2(zn.y, zn.x);
                        vmu = J * zu.z;
                        if (1.0 - muN * muN >= POLE_ZONE)
                            zone = false;
                    }
                    else
                    {
                        vmu += 0.5 * (am + 0.5 * KerrThetaPrime(muN, a, L, Q)) * dl;
                        float thN = KerrTheta(muN, a, L, Q);
                        if (thN > 1e-2 * (Q + L * L + a * a))
                            vmu = (vmu < 0.0 ? -1.0 : 1.0) * sqrt(thN);
                    }
                    ds = 0.5 * dl * sigmaK;
                    float rN = r + vr * dl + 0.5 * ar * dl * dl;
                    vr += 0.5 * (ar + 0.5 * KerrRPrime(rN, a, L, Q)) * dl;
                    float RN = KerrR(rN, a, L, Q);
                    if (RN > 1e-2 * (rN * rN + a * a) * (rN * rN + a * a))
                        vr = (vr < 0.0 ? -1.0 : 1.0) * sqrt(RN);
                    float vphiN = KerrPhiDot(rN, muN, a, L);
                    if (!inZone)
                        phiN = phi + 0.5 * (vphi + vphiN) * dl;

                    if (mu * muN < 0.0)
                        hit.order += 1.0;

                    // 두꺼운 원반: 수직 가우스 밀도를 걸음마다 erf로 적분하고 Beer-Lambert로 흡수·방출한다.
                    float s0 = sqrt(saturate(1.0 - mu * mu)), s1 = sqrt(saturate(1.0 - muN * muN));
                    float zs0 = r * mu * 0.5, zs1 = rN * muN * 0.5;
                    float sigma = max(H * 0.25 * (r * s0 + rN * s1), 1e-3);
                    float fStar = zs0 * zs1 < 0.0 ? zs0 / (zs0 - zs1) : (abs(zs0) < abs(zs1) ? 0.0 : 1.0);
                    float zStar = lerp(zs0, zs1, fStar);
                    if (abs(zStar) < 3.0 * sigma)
                    {
                        float column = DiskColumn(zs0, zs1, ds, sigma);
                        float rh = lerp(r, rN, fStar);
                        float ph = lerp(phi, phiN, fStar);
                        float2 q = rh * 0.5 * float2(cos(ph), sin(ph));
                        float4 m = DiskSample(q, KerrRedshift(rh, fa, -L), inner, clamp(zStar / (2.0 * sigma), -1.0, 1.0));
                        float absorb = 1.0 - exp(-_DiskDepth * m.a * column / (sigma * 2.5066283));
                        hit.light += m.rgb * max(_Glow, 0.0) * absorb * hit.trans;
                        hit.trans *= 1.0 - absorb;
                    }

                    r = rN;
                    mu = muN;
                    phi = phiN;
                    vphi = vphiN;
                }

                return hit;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uvHere = GetNormalizedScreenSpaceUV(i.positionCS);
                float3 centre = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float holeDepth = -TransformWorldToView(centre).z;
                if (LinearEyeDepth(SampleSceneDepth(uvHere), _ZBufferParams) < holeDepth)
                    discard;

                float3 ax = normalize(unity_ObjectToWorld._m00_m10_m20);
                float3 an = normalize(unity_ObjectToWorld._m02_m12_m22);
                float3 ay = cross(an, ax) * (_Spin < 0.0 ? -1.0 : 1.0);   // 음의 스핀 = 거울 좌표
                float rs = max(abs(_Rs), 0.001);
                float bound = 0.5 * length(unity_ObjectToWorld._m00_m10_m20) / rs;

                RayHit hit = Trace(i.positionWS, centre, rs, ax, ay, an, bound);
                float3 col = hit.light;
                float alpha = hit.captured > 0.5 ? 1.0 : 1.0 - hit.trans;

                // 광자 고리 계단 현상: 포획 경계나 상의 차수가 바뀌는 2x2 쿼드만 회전 격자 4점을 더 쏜다.
                float3 dx = ddx(i.positionWS), dy = ddy(i.positionWS);
                if (_RingAA > 0.5 && fwidth(hit.captured) + fwidth(hit.order) > 0.0)
                {
                    float2 o = float2(0.375, 0.125);
                    [loop]
                    for (int s = 0; s < 4; s++)
                    {
                        RayHit h = Trace(i.positionWS + dx * o.x + dy * o.y, centre, rs, ax, ay, an, bound);
                        col += h.light;
                        alpha += h.captured > 0.5 ? 1.0 : 1.0 - h.trans;
                        o = float2(-o.y, o.x);
                    }
                    col *= 0.2;
                    alpha *= 0.2;
                }

                // 배경 왜곡은 GravLens 패스가 담당한다. 포획된 광선만 완전히 가린다.
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
