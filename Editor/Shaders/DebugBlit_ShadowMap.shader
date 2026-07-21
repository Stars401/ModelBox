Shader "Hidden/ModelBox/ShadowMapBlit"
{
    Properties
    {
        _ColorMapMode("Color Map Mode", Int) = 0
        _ViewMode("View Mode: 0=Attenuation 1=Cascade", Int) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ShadowMapDebugBlit"
            ZWrite Off Cull Off ZTest Always Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // 注意：不直接 include Shadows.hlsl（依赖 EntityLighting.hlsl 中的 LerpWhiteTo，
            // 且我们未使用其中的函数）。阴影全局属性手动声明。

            // 深度缓冲（用于重建世界坐标）
            TEXTURE2D_X(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            // URP 主光源阴影贴图（使用硬件比较采样器，避免手动 depth compare 的平台差异）
            TEXTURE2D_SHADOW(_MainLightShadowmapTexture);
            SAMPLER_CMP(sampler_MainLightShadowmapTexture);

            // 级联阴影数据（URP MainLightShadowCasterPass 设置的全局属性）
            float4x4 _MainLightWorldToShadow[5]; // 4 cascade + 1 no-op fallback
            float4 _MainLightShadowParams;        // x=strength, y=softShadowQuality, z=fadeScale, w=fadeBias
            float4 _CascadeShadowSplitSpheres0;   // xyz=球心, w=线性半径
            float4 _CascadeShadowSplitSpheres1;
            float4 _CascadeShadowSplitSpheres2;
            float4 _CascadeShadowSplitSpheres3;
            float4 _CascadeShadowSplitSphereRadii; // xyzw=各级联的平方半径（R²）

            CBUFFER_START(UnityPerMaterial)
                int _ColorMapMode;
                int _ViewMode;
            CBUFFER_END

            struct BlitVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // === 全屏三角形顶点着色器（与其他 Blit shader 一致） ===
            BlitVaryings Vert(uint vertexID : SV_VertexID)
            {
                float2 pos = float2((vertexID << 1) & 2, vertexID & 2);
                BlitVaryings output;
                output.positionCS = float4(pos * 2.0 - 1.0, 0.0, 1.0);
                output.uv = pos;

                #if UNITY_UV_STARTS_AT_TOP
                output.uv.y = 1.0 - pos.y;
                #endif

                return output;
            }

            // ---- 颜色映射函数（与 ScreenNormalBlit 一致） ----

            half3 HeatMap(float t)
            {
                half3 c = half3(0, 0, 0);
                c.r = smoothstep(0.5, 0.8, t);
                c.g = t < 0.5 ? smoothstep(0.0, 0.5, t) : smoothstep(1.0, 0.5, t);
                c.b = smoothstep(0.0, 0.3, t) * (1.0 - smoothstep(0.3, 0.6, t));
                return c;
            }

            half3 Rainbow(float t)
            {
                half3 c;
                c.r = smoothstep(0.0, 0.25, t) * (1.0 - smoothstep(0.75, 1.0, t));
                c.g = smoothstep(0.0, 0.5, t) * (1.0 - smoothstep(0.5, 1.0, t));
                c.b = smoothstep(0.25, 0.75, t) * (1.0 - smoothstep(0.75, 1.0, t));
                return saturate(c);
            }

            half3 ApplyColorMap(half3 input, int mode)
            {
                if (mode == 1) // Gray
                {
                    float g = dot(input, half3(0.299, 0.587, 0.114));
                    return half3(g, g, g);
                }
                else if (mode == 2) // HeatMap
                {
                    float v = dot(input, half3(0.299, 0.587, 0.114));
                    return HeatMap(saturate(v));
                }
                else if (mode == 3) // Rainbow
                {
                    float v = dot(input, half3(0.299, 0.587, 0.114));
                    return Rainbow(saturate(v));
                }
                return input;
            }

            // ---- 级联索引计算 ----
            // URP 源码：_CascadeShadowSplitSpheresN.w = 线性半径，_CascadeShadowSplitSphereRadii = 平方半径
            // 比较：平方距离 vs 平方半径（dot(d,d) <= R²）
            int GetCascadeIndex(float3 worldPos)
            {
                float3 d0 = worldPos - _CascadeShadowSplitSpheres0.xyz;
                float3 d1 = worldPos - _CascadeShadowSplitSpheres1.xyz;
                float3 d2 = worldPos - _CascadeShadowSplitSpheres2.xyz;
                float3 d3 = worldPos - _CascadeShadowSplitSpheres3.xyz;

                // 使用预计算的平方半径（与 URP ComputeCascadeIndex 一致）
                float4 radii2 = _CascadeShadowSplitSphereRadii;

                if (radii2.x > 0 && dot(d0, d0) <= radii2.x) return 0;
                if (radii2.y > 0 && dot(d1, d1) <= radii2.y) return 1;
                if (radii2.z > 0 && dot(d2, d2) <= radii2.z) return 2;
                if (radii2.w > 0 && dot(d3, d3) <= radii2.w) return 3;

                return -1; // 不在任何级联范围内
            }

            // ---- 级联颜色 ----
            half3 GetCascadeColor(int cascadeIndex)
            {
                // 每个级联一个独特的颜色
                if (cascadeIndex == 0) return half3(0.2, 0.8, 0.2);  // 绿色：最近
                if (cascadeIndex == 1) return half3(0.2, 0.6, 0.9);  // 蓝色
                if (cascadeIndex == 2) return half3(0.9, 0.7, 0.1);  // 黄色
                if (cascadeIndex == 3) return half3(0.9, 0.3, 0.1);  // 红色：最远
                return half3(0.3, 0.3, 0.3);                          // 灰色：超出范围
            }

            // ---- 阴影采样（使用硬件比较采样器） ----
            // 与 URP Shadows.hlsl SampleShadowmap 对齐：
            // - shadowCoord.xy = 阴影图 UV（[0,1]）
            // - shadowCoord.z  = 深度比较值（[0,1]）
            // - BEYOND_SHADOW_FAR: z <= 0 || z >= 1 视为超出阴影范围，返回 1.0
            float SampleShadowAttenuation(float3 worldPos)
            {
                int cascade = GetCascadeIndex(worldPos);
                if (cascade < 0) return 1.0;

                float4 shadowPos = mul(_MainLightWorldToShadow[cascade], float4(worldPos, 1.0));
                float3 shadowCoord = float4(shadowPos.xyz, 0).xyz; // .w = 0, 与 URP 一致

                // BEYOND_SHADOW_FAR 检测（URP Shadows.hlsl:105）
                if (shadowCoord.z <= 0.0 || shadowCoord.z >= 1.0)
                    return 1.0;

                // 边界检测
                if (shadowCoord.x < 0 || shadowCoord.x > 1 || shadowCoord.y < 0 || shadowCoord.y > 1)
                    return 1.0;

                // 硬件比较采样（使用 LinearClamp comparison sampler）
                float shadow = SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture,
                    sampler_MainLightShadowmapTexture, shadowCoord);

                // 应用阴影强度
                shadow = lerp(1.0, shadow, _MainLightShadowParams.x);

                return shadow;
            }

            half4 Frag(BlitVaryings input) : SV_Target
            {
                // 采样深度缓冲
                float rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, input.uv).r;

                // 天空区域：输出深灰色（平台感知深度判断）
                #ifdef UNITY_REVERSED_Z
                    if (rawDepth <= 0.0001)
                #else
                    if (rawDepth >= 0.9999)
                #endif
                    return half4(0.05, 0.05, 0.05, 1);

                // 阴影未启用时（_MainLightShadowParams.x == 0），显示提示色
                if (_MainLightShadowParams.x <= 0.0)
                    return half4(0.1, 0.1, 0.2, 1); // 深蓝：阴影未启用

                // 从深度缓冲重建世界坐标
                float3 worldPos = ComputeWorldSpacePosition(input.uv, rawDepth, UNITY_MATRIX_I_VP);

                half3 color;

                if (_ViewMode == 0)
                {
                    // 模式 0：阴影衰减（白色=光照中，黑色=阴影中）
                    float atten = SampleShadowAttenuation(worldPos);
                    color = half3(atten, atten, atten);
                }
                else
                {
                    // 模式 1：级联着色
                    int cascade = GetCascadeIndex(worldPos);
                    float atten = SampleShadowAttenuation(worldPos);
                    half3 cascadeColor = GetCascadeColor(cascade);

                    // 阴影中的区域变暗
                    color = cascadeColor * lerp(0.3, 1.0, atten);
                }

                // 应用颜色映射
                color = ApplyColorMap(color, _ColorMapMode);

                return half4(color, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
