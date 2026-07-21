Shader "Hidden/ModelBox/OpaqueTextureBlit"
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
            Name "OpaqueTextureDebugBlit"
            ZWrite Off Cull Off ZTest Always Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // _CameraOpaqueTexture: URP CopyColorPass 在 opaque 渲染后捕获的全屏纹理
            // 通过 cmd.SetGlobalTexture("_CameraOpaqueTexture", ...) 设置为全局 shader 属性
            // 在 procedural draw 上下文中直接作为全局纹理采样，不依赖 cmd.Blit 的 _MainTex 绑定
            TEXTURE2D_X(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

            CBUFFER_START(UnityPerMaterial)
                int _ColorMapMode;
            CBUFFER_END

            struct BlitVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // === 顶点着色器：使用 SV_VertexID 程序化全屏三角形 ===
            //
            // 这是 URP Blitter API 使用的技术（Unity 2022+ Blitter.BlitCameraTexture 内部实现）。
            // 与 cmd.Blit 的四边形方式不同：
            //   - cmd.Blit 使用内部四边形网格，positionOS 的坐标系统因上下文不同而变化，
            //     在 URP ScriptableRenderPass 中可能导致四边形只覆盖部分屏幕（右下角问题）
            //   - cmd.DrawProcedural + SV_VertexID 从顶点 ID 生成位置，
            //     确定性保证全屏覆盖，与 URP Blitter 一样的结果
            //
            // 全屏三角形（3 个顶点）：
            //   vertexID 0: positionCS = (-1, -1), uv = (0, 0) → 左下
            //   vertexID 1: positionCS = ( 3, -1), uv = (2, 0) → 远右超出视口
            //   vertexID 2: positionCS = (-1,  3), uv = (0, 2) → 远上超出视口
            // GPU 自动裁剪超出视口的部分，结果就是正好覆盖全屏
            BlitVaryings Vert(uint vertexID : SV_VertexID)
            {
                // 全屏三角形：从 vertexID 生成位置和 UV
                float2 pos = float2((vertexID << 1) & 2, vertexID & 2);
                BlitVaryings output;
                output.positionCS = float4(pos * 2.0 - 1.0, 0.0, 1.0);

                // UV 基础映射：pos 范围 (0,0)-(2,0)-(0,2)
                // 视口内可见部分 UV 范围 (0,0)-(1,1)
                output.uv = pos;

                // === 平台 Y 轴翻转处理 ===
                //
                // DirectX (UNITY_UV_STARTS_AT_TOP) 平台上：
                //   渲染纹理存储从上到下（row 0 = 顶部行）
                //   UV (0,0) = 纹理左上角，而非屏幕左下角
                //   我们的全屏三角形 uv.y 从 0（底部）到 1（顶部）
                //   在 DirectX 上直接采样会导致图像上下翻转（反转效果）
                //   修复：翻转 UV.y → uv.y = 1 - pos.y
                //
                // OpenGL/Metal 平台上：
                //   渲染纹理存储从下到上（row 0 = 底部行）
                //   UV (0,0) = 纹理左下角 = 屏幕左下角 → 自然对齐，无需翻转
                //
                // 这与 URP ComputeScreenPos 在 DirectX 上的处理一致：
                //   ComputeScreenPos 在 UNITY_UV_STARTS_AT_TOP 时会翻转 Y
                #if UNITY_UV_STARTS_AT_TOP
                output.uv.y = 1.0 - pos.y;
                #endif

                return output;
            }

            // ---- 颜色映射函数（与 DebugReplacement_Geometry.shader 一致） ----

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

            half4 Frag(BlitVaryings input) : SV_Target
            {
                // 直接使用全屏三角形的 UV 采样 _CameraOpaqueTexture
                // UV 在视口内范围为 (0,0)-(1,1)，对应全屏纹理的完整范围
                half4 color = SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, input.uv);

                // Fallback 检测：
                // 当 _CameraOpaqueTexture 未被 URP CopyColorPass 填充时，
                // Unity 返回小型灰白棋盘格 fallback 纹理（约 4x4 灰色方块交替）。
                // 真实场景颜色应有明显的通道差异（至少一个通道与其他差距 > 0.02），
                // 而 fallback 纹理的 R≈G≈B≈0.5（或 0.25/0.75 交替的小棋盘格）。
                float channelVariation = abs(color.r - color.g)
                                       + abs(color.g - color.b)
                                       + abs(color.r - color.b);

                // 检测条件：三通道几乎相同（差异 < 0.01）且值在 0.3-0.7 范围（排除纯黑/纯白）
                if (channelVariation < 0.01 && color.r > 0.3 && color.r < 0.7)
                {
                    // Fallback 纹理：显示醒目的红色闪烁错误提示
                    // 提示含义：_CameraOpaqueTexture 未被填充，需要在 URP Asset 中启用 Opaque Texture
                    float warningBlink = step(0.5, frac(_Time.y * 2.0));
                    half3 errorColor = half3(0.8, 0.1, 0.1) + half3(0.2, 0.2, 0.0) * warningBlink;
                    return half4(errorColor, 1);
                }

                // 正常情况：显示 Opaque Texture 的原始场景颜色
                // 应用颜色映射（与 Geometry shader 的后处理一致）
                color.rgb = ApplyColorMap(color.rgb, _ColorMapMode);

                return half4(color.rgb, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}