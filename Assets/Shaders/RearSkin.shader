// 후면(외피) 스킨 - GPU판. CPU가 칸당 256픽셀을 굽던 것을(마모·뜯김·발자국) 마스크
// 세 장의 조합으로 프래그먼트에서 그린다:
//   _CellMask   칸 해상도, 동적. r = 0이면 그 칸 후면 없음, 아니면 남은 수명(1..255)
//   _StaticMask 칸 해상도, 설계도당 1회. r = 후면이 깔릴 칸(RearWorthy)
//   _FootMask   칸당 16px, 설계도당 1회. **판 폴리곤이 둘러싼 안쪽** = 후면 실루엣.
//               모양은 격자가 아니라 기하에서 온다 - 경사 지붕도 판을 따라 잘린다
// 뜯긴 가장자리는 이웃 4칸을 셰이더가 직접 탭해서 얻는다(설계엔 있는데 지금 없는 칸 = 찢긴 곳).
//
// 골격은 PlateSkin과 같은 URP 17 Sprite-Lit(Universal2D) 원본을 따른다.
Shader "SUPERRADIANCE/RearSkin"
{
    Properties
    {
        _MainTex("Sprite", 2D) = "white" {}               // 공유 흰 텍스처. 지오메트리용
        _MaskTex("Mask", 2D) = "white" {}                 // URP 2D 라이팅 마스크 규약
        _HullTex("Hull Art", 2D) = "white" {}
        _CellMask("Cell Mask", 2D) = "black" {}
        _StaticMask("Static Mask", 2D) = "white" {}
        _FootMask("Footprint Mask", 2D) = "white" {}
        _Grid("Quad MinX/MinRow/UVToCell/Height", Vector) = (0,0,1,1)
        _Map("MapW/MapH/Shade/HasHull", Vector) = (1,1,1,0)
        _Foot("Foot MinX/MinRow/Width/Height", Vector) = (0,0,1,1)

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

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Lit2DCommon.hlsl"

            TEXTURE2D(_HullTex);
            SAMPLER(sampler_HullTex);
            TEXTURE2D(_CellMask);
            SAMPLER(sampler_CellMask);
            TEXTURE2D(_StaticMask);
            SAMPLER(sampler_StaticMask);
            TEXTURE2D(_FootMask);
            SAMPLER(sampler_FootMask);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _Grid;
                float4 _Map;
                float4 _Foot;
            CBUFFER_END

            Varyings LitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonLitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;

                return o;
            }

            // 칸 마스크는 아랫줄이 마지막 row가 되게 뒤집혀 저장된다(텍스처 y는 위로 증가).
            float2 CellUV(float col, float row)
            {
                return float2((col + 0.5) / _Map.x, (_Map.y - row - 0.5) / _Map.y);
            }

            // 설계엔 후면이 깔릴 칸인데 지금 없다 = 찢긴 곳. 맵 밖은 찢긴 게 아니다.
            bool Torn(float col, float row)
            {
                if (col < 0 || row < 0 || col >= _Map.x || row >= _Map.y)
                    return false;

                float2 uvN = CellUV(col, row);
                float present = SAMPLE_TEXTURE2D(_CellMask, sampler_CellMask, uvN).r;
                float worthy = SAMPLE_TEXTURE2D(_StaticMask, sampler_StaticMask, uvN).r;

                return worthy > 0.5 && present * 255.0 < 0.5;
            }

            half4 RearColor(Varyings i)
            {
                // uv -> 설계도 칸 좌표. **uv는 0~1이 아니다** - 스프라이트가 공유 텍스처의
                // 한 조각이라 uv는 rect/256이고, 칸으로 되돌리는 배율(_Grid.z)이 그 몫을
                // 이미 담고 있다. row는 아래로 증가하고 쿼드 y는 위로 증가하므로 뒤집는다.
                float cx = _Grid.x + i.uv.x * _Grid.z;
                float rowDown = _Grid.y + _Grid.w - i.uv.y * _Grid.z;
                float col = floor(cx);
                float row = floor(rowDown);
                float fx = cx - col;
                float fy = rowDown - row;

                float2 cuv = CellUV(col, row);
                float v = SAMPLE_TEXTURE2D(_CellMask, sampler_CellMask, cuv).r * 255.0;

                float2 huv = float2(
                    (cx - _Foot.x) / _Foot.z,
                    1.0 - (rowDown - _Foot.y) / _Foot.w);
                float footprint = SAMPLE_TEXTURE2D(_FootMask, sampler_FootMask, huv).r;

                // 실루엣 안인데 격자상 후면 칸이 아니다 = 판이 자기 칸 밖으로 돌출됐거나
                // 폴리곤이 Exterior 칸을 삼킨 자리. 가장 가까운 살아 있는 후면 수명을 쓴다.
                // 설계상 후면 칸(worthy)이 파괴돼 0인 경우에는 이 폴백을 절대 쓰지 않는다:
                // 그 구멍을 이웃 후면이 도로 메우면 파괴 표현이 사라진다.
                bool inMap = col >= 0 && row >= 0 && col < _Map.x && row < _Map.y;
                float worthyHere = inMap
                    ? SAMPLE_TEXTURE2D(_StaticMask, sampler_StaticMask, cuv).r
                    : 0.0;

                if (v < 0.5 && footprint >= 0.5 && (!inMap || worthyHere < 0.5))
                {
                    for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        float nc = clamp(col + ox, 0.0, _Map.x - 1.0);
                        float nr = clamp(row + oy, 0.0, _Map.y - 1.0);
                        v = max(v,
                            SAMPLE_TEXTURE2D(_CellMask, sampler_CellMask, CellUV(nc, nr)).r * 255.0);
                    }
                }

                if (v < 0.5)
                    discard;

                float life = saturate((v - 1.0) / 254.0);

                // 픽셀 위치 해시 노이즈. CPU판은 칸별 rng 수열이었다 - 무늬는 다르고 규칙은 같다.
                float grain = frac(sin(dot(floor(float2(cx, rowDown) * 16.0),
                    float2(12.9898, 78.233))) * 43758.5453);

                // 세월: 반 넘게 상하면 가장자리부터 갉아먹힌다
                if (life < 0.5 && grain > life * 2.0)
                    discard;

                // 찢긴 이웃 쪽 가장자리일수록 살아남기 어렵다
                const float Bite = 0.35;
                float open = 1.0;

                if (Torn(col - 1, row)) open = min(open, fx / Bite);
                if (Torn(col + 1, row)) open = min(open, (1.0 - fx) / Bite);
                if (Torn(col, row - 1)) open = min(open, fy / Bite);
                if (Torn(col, row + 1)) open = min(open, (1.0 - fy) / Bite);

                if (open < 1.0 && grain > open)
                    discard;

                // **실루엣은 무조건 건다.** 예전에는 "우주에 닿은 경계 칸에만" 걸었는데,
                // 경사 지붕 칸은 경계 칸이면서 아래가 방이라 그 규칙이 안쪽 절반까지
                // 잘라내 함교 천장에 구멍을 냈다. 실루엣이 이미 "둘러싸인 안쪽"이라
                // 방을 향한 면은 저절로 남고 우주를 향한 면만 판 모양으로 깎인다.
                if (footprint < 0.5)
                    discard;

                float2 artUv = float2(cx / _Map.x, 1.0 - rowDown / _Map.y);
                half4 art = lerp(half4(1, 1, 1, 1),
                    SAMPLE_TEXTURE2D(_HullTex, sampler_HullTex, artUv), _Map.w);

                float wear = lerp(0.30, 1.0, life);

                return half4(art.rgb * (_Map.z * wear), art.a);
            }

            half4 LitFragment(Varyings input) : SV_Target
            {
                const half4 main = input.color * RearColor(input);
                const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, input.uv);
                const half3 normalTS = half3(0, 0, 1);

                SurfaceData2D surfaceData;
                InputData2D inputData;

                InitializeSurfaceData(main.rgb, main.a, mask, normalTS, surfaceData);
                InitializeInputData(input.uv, input.lightingUV, inputData);

                #if defined(DEBUG_DISPLAY)
                SETUP_DEBUG_TEXTURE_DATA_2D_NO_TS(inputData, input.positionWS, input.positionCS, _MainTex);
                #endif

                return CombinedShapeLightShared(surfaceData, inputData);
            }
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
