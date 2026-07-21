Shader "Hidden/ModelBox/ScreenSpaceFromDepth"
{
    Properties
    {
        _ViewMode("View Mode", Int) = 0
        _DebugScale("Debug Scale", Float) = 1.0
        _DebugOffset("Debug Offset", Float) = 0.0
        _DebugGamma("Debug Gamma", Float) = 1.0
        _DebugDepthRange("Depth Range", Float) = 100.0
        _ColorMapMode("Color Map Mode", Int) = 0
        _MaxSteps("Max March Steps", Float) = 64.0
        _StepSize("Step Size", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ScreenSpaceFromDepthBlit"
            ZWrite Off Cull Off ZTest Always Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ModelBoxColorMapping.hlsl"

            TEXTURE2D_X(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            CBUFFER_START(UnityPerMaterial)
                int _ViewMode;
                float _DebugScale;
                float _DebugOffset;
                float _DebugGamma;
                float _DebugDepthRange;
                int _ColorMapMode;
                float _MaxSteps;
                float _StepSize;
            CBUFFER_END

            struct BlitVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // === 全屏三角形顶点着色器（与 ScreenNormalBlit 完全一致） ===
            BlitVaryings Vert(uint vertexID : SV_VertexID)
            {
                float2 pos = float2((vertexID << 1) & 2, vertexID & 2);
                BlitVaryings output;
                output.positionCS = float4(pos * 2.0 - 1.0, 0.0, 1.0);
                output.uv = pos;

                // DirectX 平台 Y 轴翻转（UNITY_UV_STARTS_AT_TOP）
                #if UNITY_UV_STARTS_AT_TOP
                output.uv.y = 1.0 - pos.y;
                #endif

                return output;
            }

            half4 Frag(BlitVaryings input) : SV_Target
            {
                // 采样深度缓冲
                float rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, input.uv).r;

                // 天空区域：输出黑色（平台感知深度判断）
                #ifdef UNITY_REVERSED_Z
                    if (rawDepth <= 0.0001)
                #else
                    if (rawDepth >= 0.9999)
                #endif
                    return half4(0, 0, 0, 1);

                // 从深度缓冲重建世界坐标
                float3 worldPos = ComputeWorldSpacePosition(input.uv, rawDepth, UNITY_MATRIX_I_VP);

                // 用 ddx/ddy 从重建的世界坐标计算法线
                float3 normal = normalize(cross(ddy(worldPos), ddx(worldPos)));

                // 视图方向（相机位置 → 像素世界坐标）
                float3 viewDir = normalize(_WorldSpaceCameraPos - worldPos);

                // LinearEyeDepth
                float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

                half3 color = half3(0, 0, 0);

                switch (_ViewMode)
                {
                    case 0: // Depth 灰度
                    {
                        float t = saturate(eyeDepth / max(_DebugDepthRange, 0.001));
                        t = pow(t, _DebugGamma);
                        color = half3(t, t, t);
                        break;
                    }
                    case 1: // WorldPos (frac 映射)
                    {
                        float3 scaled = worldPos * _DebugScale + _DebugOffset;
                        color = half3(frac(scaled));
                        color = pow(color, _DebugGamma);
                        break;
                    }
                    case 2: // ScreenNormal (法线 * 0.5 + 0.5)
                    {
                        color = half3(normal * 0.5 + 0.5);
                        break;
                    }
                    case 3: // NdotV
                    {
                        float ndotv = saturate(dot(normal, viewDir));
                        ndotv = pow(ndotv, _DebugGamma);
                        color = half3(ndotv, ndotv, ndotv);
                        break;
                    }
                    case 4: // Fresnel (Schlick)
                    {
                        float ndotv = saturate(dot(normal, viewDir));
                        float fresnel = pow(1.0 - ndotv, 5.0);
                        fresnel = pow(fresnel, _DebugGamma);
                        color = half3(fresnel, fresnel, fresnel);
                        break;
                    }
                    case 5: // FlatNormal (面法线，不平滑)
                    {
                        color = half3(normal * 0.5 + 0.5);
                        break;
                    }
                    case 6: // RayMarch — 深度缓冲射线步进
                    {
                        // 从 UV 重建射线方向
                        float2 ndc = input.uv * 2.0 - 1.0;
                        float4 clipPos = float4(ndc, 0, 1);
                        float4 viewPos = mul(UNITY_MATRIX_I_P, clipPos);
                        viewPos.xyz /= viewPos.w;
                        float3 rayDir = normalize(mul((float3x3)UNITY_MATRIX_I_V, viewPos.xyz));

                        // 从相机位置开始步进
                        float3 rayOrigin = _WorldSpaceCameraPos;
                        uint maxIter = (uint)_MaxSteps;
                        float stepLen = _StepSize;
                        int hitSteps = 0;

                        [loop] for (uint i = 1; i <= maxIter; i++)
                        {
                            float3 samplePos = rayOrigin + rayDir * (i * stepLen);
                            // 投影到屏幕空间
                            float4 projClip = mul(UNITY_MATRIX_VP, float4(samplePos, 1));
                            float2 sampleUV = projClip.xy / projClip.w * 0.5 + 0.5;
                            // UNITY_MATRIX_VP 投影已与 _CameraDepthTexture 的 UV 约定对齐，
                            // 顶点 shader 中已做一次 UNITY_UV_STARTS_AT_TOP 翻转，
                            // 此处不可再翻转（会导致画面颠倒）。

                            // 超出屏幕范围则终止
                            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1)
                                break;

                            float sceneDepth = LinearEyeDepth(
                                SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, sampleUV).r,
                                _ZBufferParams);

                            float rayDepth = length(samplePos - _WorldSpaceCameraPos);

                            if (rayDepth > sceneDepth)
                            {
                                hitSteps = (int)i;
                                break;
                            }
                            hitSteps = (int)i;
                        }

                        // 热力图输出：步进次数 → 颜色
                        float t = saturate((float)hitSteps / max((float)maxIter, 1.0));
                        color = half3(
                            smoothstep(0.3, 0.7, t),
                            smoothstep(0.0, 0.3, t) * (1.0 - smoothstep(0.5, 0.8, t)),
                            (1.0 - smoothstep(0.0, 0.5, t)) * 0.5
                        );
                        color = pow(color, _DebugGamma);
                        break;
                    }
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
