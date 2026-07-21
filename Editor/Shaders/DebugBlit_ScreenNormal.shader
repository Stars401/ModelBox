Shader "Hidden/ModelBox/ScreenNormalBlit"
{
    Properties
    {
        _ColorMapMode("Color Map Mode", Int) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ScreenNormalDebugBlit"
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
                int _ColorMapMode;
            CBUFFER_END

            struct BlitVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // === 全屏三角形顶点着色器（与 OpaqueTextureBlit 完全一致） ===
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

            // [fix v0.4] 颜色映射函数已提取到 ModelBoxColorMapping.hlsl，此处不再重复

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
                // cross(ddy, ddx) 给出正确朝向的法线（cross(ddx, ddy) 会反转）
                float3 normal = normalize(cross(ddy(worldPos), ddx(worldPos)));

                // 法线 [-1,1] 映射到 [0,1] 用于 RGB 显示
                half3 color = half3(normal * 0.5 + 0.5);

                // 应用颜色映射
                color = ApplyColorMap(color, _ColorMapMode);

                return half4(color, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
