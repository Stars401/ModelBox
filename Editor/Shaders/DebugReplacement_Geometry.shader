Shader "Hidden/ModelBox/Geometry"
{
    Properties
    {
        _DebugMode("Debug Mode", Int) = 0
        _DebugScale("Scale", Float) = 1.0
        _DebugOffset("Offset", Float) = 0.0
        _DebugGamma("Gamma", Float) = 1.0
        _DebugDepthRange("Depth Range", Float) = 100.0
        _ColorMapMode("Color Map Mode", Int) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 100
        ZWrite Off
        ZTest LEqual
        Cull Off
        Offset -1, -1

        Pass
        {
            Name "ModelBoxGeometry"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ModelBoxColorMapping.hlsl"

            TEXTURE2D_X(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            TEXTURE2D_X(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;    // xyz=tangent, w=bitangent sign
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float3 normalOS   : TEXCOORD3;
                float2 uv0        : TEXCOORD4;
                float2 uv1        : TEXCOORD5;
                float4 color      : TEXCOORD6;
                float4 screenPos  : TEXCOORD7;
                float3 tangentWS  : TEXCOORD8;  // PBR: tangent direction in world space
                float  tangentW   : TEXCOORD9;  // PBR: bitangent sign (handedness)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                int   _DebugMode;
                float _DebugScale;
                float _DebugOffset;
                float _DebugGamma;
                float _DebugDepthRange;
                int   _ColorMapMode;
            CBUFFER_END

            // ObjectID 哈希：从 object-to-world 矩阵对角线+位置生成唯一 HSV 色调
            // 使用矩阵对角线（缩放/旋转信息）+ 平移，确保同位置不同旋转的物体也有不同颜色
            half3 ObjectIDColor(float4x4 objToWorld)
            {
                float3 diag = float3(objToWorld[0][0], objToWorld[1][1], objToWorld[2][2]);
                float3 pos = float3(objToWorld[3][0], objToWorld[3][1], objToWorld[3][2]);
                // 整数位操作哈希，分布质量优于 frac(sin(dot(...)))
                uint3 p = asuint(diag + pos);
                uint h1 = p.x * 73856093u ^ p.y * 19349663u ^ p.z * 83492791u;
                float hash = frac(float(h1) * 0.00000000023283064365386963); // 1/2^32
                uint3 q = asuint(diag * 2.1 + pos * 0.7);
                uint h2 = q.x * 73856093u ^ q.y * 19349663u ^ q.z * 83492791u;
                float hash2 = frac(float(h2) * 0.00000000023283064365386963);
                // HSV → RGB：用 hash 做色调，固定高饱和度+中等亮度确保视觉区分
                float h = hash * 6.0;
                float s = 0.65 + hash2 * 0.2; // 0.65-0.85 饱和度
                float v = 0.75 + hash2 * 0.15; // 0.75-0.9 亮度
                float c = v * s;
                float x = c * (1.0 - abs(fmod(h, 2.0) - 1.0));
                float m = v - c;
                half3 rgb;
                if (h < 1.0)      rgb = half3(c, x, 0);
                else if (h < 2.0) rgb = half3(x, c, 0);
                else if (h < 3.0) rgb = half3(0, c, x);
                else if (h < 4.0) rgb = half3(0, x, c);
                else if (h < 5.0) rgb = half3(x, 0, c);
                else              rgb = half3(c, 0, x);
                return rgb + m;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.positionOS = input.positionOS.xyz;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.normalOS = input.normalOS;
                output.uv0 = input.uv0;
                output.uv1 = input.uv1;
                output.color = input.color;

                // PBR: tangent/bitangent 传递到 fragment
                output.tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                output.tangentW = input.tangentOS.w;

                // 屏幕 UV：ComputeScreenPos 输出 [0,w] 范围，除以 w 得 [0,1]
                output.screenPos = ComputeScreenPos(posInputs.positionCS);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 result = half4(0, 0, 0, 1);

                switch (_DebugMode)
                {
                    case 1: // WorldPosition
                    {
                        float3 ws = input.positionWS;
                        float3 normalized = ws / max(_DebugScale, 0.001) * 0.5 + 0.5;
                        result.rgb = saturate(normalized);
                        // WorldPos/LocalPos: gamma + offset
                        result.rgb = pow(abs(result.rgb), _DebugGamma);
                        result.rgb = saturate(result.rgb + _DebugOffset);
                        break;
                    }
                    case 2: // LocalPosition
                        result.rgb = frac(input.positionOS * _DebugScale);
                        // 仅位置模式应用 gamma/offset
                        result.rgb = pow(abs(result.rgb), _DebugGamma);
                        result.rgb = saturate(result.rgb + _DebugOffset);
                        break;
                    case 3: // WorldNormal
                        result.rgb = normalize(input.normalWS) * 0.5 + 0.5;
                        break;
                    case 4: // LocalNormal
                        result.rgb = normalize(input.normalOS) * 0.5 + 0.5;
                        break;
                    case 5: // UV0
                        result.rgb = half3(input.uv0, 0);
                        break;
                    case 6: // UV1
                        result.rgb = half3(input.uv1, 0);
                        break;
                    case 7: // Depth
                    {
                        float2 screenUV = input.screenPos.xy / input.screenPos.w;
                        float rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, screenUV).r;

                        float linearDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                        float normalizedDepth = saturate(linearDepth / max(_DebugDepthRange, 0.001));
                        result.rgb = half3(normalizedDepth, normalizedDepth, normalizedDepth);
                        // 仅 gamma（不加 offset，会破坏线性深度映射）
                        result.rgb = pow(abs(result.rgb), _DebugGamma);
                        break;
                    }
                    case 9: // 诊断：屏幕 UV 可视化
                    {
                        float2 screenUV = input.screenPos.xy / input.screenPos.w;
                        result.rgb = half3(screenUV.x, screenUV.y, 0);
                        break;
                    }
                    case 10: // 诊断：原始深度值可视化
                    {
                        float2 screenUV = input.screenPos.xy / input.screenPos.w;
                        float rawDepth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, screenUV).r;
                        // 用颜色编码原始深度：红=有深度，黑=无深度，蓝=1.0（远处）
                        result.rgb = half3(rawDepth, rawDepth * 0.5, 1.0 - rawDepth);
                        break;
                    }
                    case 11: // 诊断：物体自身深度（不依赖深度纹理）
                    {
                        // NDC depth: 0=near, 1=far (D3D) → 反转使近处亮、远处暗
                        float objDepth = 1.0 - (input.positionCS.z / input.positionCS.w);
                        result.rgb = half3(objDepth, objDepth, objDepth);
                        break;
                    }
                    case 12: // 诊断：纯色测试（确认 Shader 是否在执行）
                    {
                        result.rgb = half3(1, 0, 1); // 纯品红色 = Shader 正在执行
                        break;
                    }
                    case 8: // VertexColor
                        result = input.color;
                        break;
                    case 13: // Wireframe: 暗色 + 法线着色（真正的线框用 Handles 绘制）
                        result.rgb = normalize(input.normalWS) * 0.15 + 0.05;
                        break;
                    case 14: // Opaque Texture — 由 ModelBoxOpaqueTextureDrawPass 单独处理，此路径不可达
                        discard;
                        break;

                    // ===== PBR 诊断模式 =====

                    case 16: // FlatNormal：平面法线（ddx/ddy 交叉积，检查平滑组/硬边）
                    {
                        float3 dpdx = ddx(input.positionWS);
                        float3 dpdy = ddy(input.positionWS);
                        float3 faceNormal = normalize(cross(dpdx, dpdy));
                        result.rgb = faceNormal * 0.5 + 0.5;
                        break;
                    }
                    case 19: // NdotL：Lambert 漫反射（法线·光照方向）
                    {
                        float3 N = normalize(input.normalWS);
                        float3 L = normalize(_MainLightPosition.xyz); // w=0 for directional
                        float NdotL = saturate(dot(N, L));
                        result.rgb = half3(NdotL, NdotL, NdotL);
                        break;
                    }
                    case 20: // NdotV：视角对齐（边缘亮，正面暗，检查法线朝向）
                    {
                        float3 N = normalize(input.normalWS);
                        float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                        float NdotV = saturate(dot(N, V));
                        result.rgb = half3(NdotV, NdotV, NdotV);
                        break;
                    }
                    case 21: // Fresnel：Schlick 菲涅尔近似（边缘高光强度）
                    {
                        float3 N = normalize(input.normalWS);
                        float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                        float NdotV = saturate(dot(N, V));
                        float fresnel = pow(1.0 - NdotV, 5.0);
                        result.rgb = half3(fresnel, fresnel, fresnel);
                        break;
                    }
                    case 22: // ObjectID：每物体唯一颜色（快速识别选中物体）
                    {
                        result.rgb = ObjectIDColor(unity_ObjectToWorld);
                        break;
                    }

                    // ===== 光照 & 材质分离模式 =====

                    case 24: // Tangent：切线方向 → RGB（检查 UV X 轴方向是否正确）
                    {
                        float3 T = normalize(input.tangentWS);
                        result.rgb = T * 0.5 + 0.5;
                        break;
                    }
                    case 25: // Bitangent：副切线方向 → RGB（检查 UV Y 轴方向 / 切线空间手性）
                    {
                        float3 T = normalize(input.tangentWS);
                        float3 N = normalize(input.normalWS);
                        float3 B = cross(N, T) * input.tangentW; // handedness
                        result.rgb = B * 0.5 + 0.5;
                        break;
                    }
                    case 26: // Soft Diffuse：柔和 Lambert 光照（中性灰 albedo，保留形状感知）
                    {
                        // 柔和漫反射：中性灰底色 + Lambert，无高光，无纹理
                        // 用途：检查法线朝向和光照模型是否正确，不受材质纹理干扰
                        float3 N = normalize(input.normalWS);
                        float3 L = normalize(_MainLightPosition.xyz);
                        float NdotL = saturate(dot(N, L)) * 0.6 + 0.4; // 柔和漫反射，避免全黑阴影
                        result.rgb = half3(NdotL, NdotL, NdotL);
                        break;
                    }
                    case 27: // Specular Highlight：仅镜面高光（检查高光贴图和法线精度）
                    {
                        float3 N = normalize(input.normalWS);
                        float3 L = normalize(_MainLightPosition.xyz);
                        float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                        float3 H = normalize(L + V); // Blinn-Phong half vector
                        float NdotH = saturate(dot(N, H));
                        float spec = pow(NdotH, 64.0); // 紧凑高光（类似 roughness=0.3）
                        result.rgb = half3(spec, spec, spec) * 2.0; // 乘以 2 增强可见度
                        break;
                    }
                    case 28: // Lighting Only：仅光照无纹理（检查光照模型、阴影、环境光）
                    {
                        float3 N = normalize(input.normalWS);
                        float3 L = normalize(_MainLightPosition.xyz);
                        float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                        float NdotL = saturate(dot(N, L));
                        float3 H = normalize(L + V);
                        float NdotH = saturate(dot(N, H));
                        // 漫反射 + 简易高光，无纹理，使用中性灰作为 albedo
                        float3 diffuse = half3(0.5, 0.5, 0.5) * NdotL;
                        float3 spec = half3(1, 1, 1) * pow(NdotH, 32.0) * 0.3;
                        // 简易环境光
                        float3 ambient = half3(0.1, 0.1, 0.12);
                        result.rgb = saturate(diffuse + spec + ambient);
                        break;
                    }
                    case 29: // Roughness：粗糙度可视化（默认 0.5，可通过 scale 参数调整参考值）
                    {
                        // overrideMaterial 无法读取原始材质的 _Smoothness，使用中性灰提示
                        // 用户可通过 Scale 参数模拟不同粗糙度的高光范围
                        float refRoughness = saturate(_DebugScale); // 复用 Scale 参数作为参考值
                        float3 N = normalize(input.normalWS);
                        float3 L = normalize(_MainLightPosition.xyz);
                        float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                        float3 H = normalize(L + V);
                        float NdotH = saturate(dot(N, H));
                        float spec = pow(NdotH, max(1.0, (1.0 - refRoughness) * 128.0));
                        result.rgb = half3(spec, spec, spec);
                        break;
                    }
                    case 30: // Metallic：金属度可视化（默认灰度参考，可通过 scale 调整）
                    {
                        // 金属度影响漫反射/高光比例：金属=高光主导，非金属=漫反射主导
                        float metallic = saturate(_DebugScale); // 复用 Scale 作为参考金属度
                        float3 N = normalize(input.normalWS);
                        float3 L = normalize(_MainLightPosition.xyz);
                        float NdotL = saturate(dot(N, L));
                        // 非金属: 漫反射主导; 金属: 无漫反射，高光主导
                        float3 diffuse = lerp(half3(0.5, 0.5, 0.5), half3(0, 0, 0), metallic) * NdotL;
                        float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                        float3 H = normalize(L + V);
                        float spec = pow(saturate(dot(N, H)), 32.0) * lerp(0.04, 1.0, metallic);
                        result.rgb = saturate(diffuse + spec);
                        break;
                    }

                    // ===== 导数诊断模式（行业高端工具对标） =====

                    case 31: // MipmapLevel：纹理密度可视化（UV 导数 → Mip 等级估算）
                    {
                        // 原理：UV 的屏幕空间导数反映了纹理采样的频率
                        // 导数大 = UV 变化快 = 低 mip 级别（高分辨率）= 绿色
                        // 导数小 = UV 变化慢 = 高 mip 级别（低分辨率）= 红色
                        // Scale 参数作为参考纹理尺寸（像素），默认 512
                        float texSize = max(_DebugScale, 1.0) * 512.0;
                        float2 dxUV = ddx(input.uv0 * texSize);
                        float2 dyUV = ddy(input.uv0 * texSize);
                        float mipLevel = 0.5 * log2(max(dot(dxUV, dxUV), dot(dyUV, dyUV)));
                        // 归一化到 [0,1]，假设 mip 范围 0-8（256px → 1px）
                        float t = saturate(mipLevel / 8.0);
                        // 绿色(高分辨率/mip0) → 黄色(mid) → 红色(低分辨率/mip8+)
                        result.rgb = half3(
                            smoothstep(0.3, 0.8, t),           // R: 高 mip 时亮
                            1.0 - smoothstep(0.5, 1.0, t),     // G: 低 mip 时亮
                            0.0                                  // B: 始终暗
                        );
                        break;
                    }
                    case 32: // GeoDensity：几何密度热力图（每像素三角形密度估算）
                    {
                        // 原理：世界坐标的屏幕空间导数大小反映了每像素覆盖的几何面积
                        // 导数大 = 每像素覆盖大面积几何（远处/低密度）= 绿色
                        // 导数小 = 每像素覆盖小面积几何（近处/高密度/过度细分）= 红色
                        float3 dpdx = ddx(input.positionWS);
                        float3 dpdy = ddy(input.positionWS);
                        float pixelSize = max(length(dpdx), length(dpdy));
                        // Scale 参数作为参考大小（米），默认 0.1（10cm/pixel = 中等密度）
                        float refSize = max(_DebugScale, 0.001) * 0.1;
                        float density = refSize / max(pixelSize, 0.0001);
                        float t = saturate(density);
                        // 绿色(低密度/正常) → 黄色(中等) → 红色(过高密度/过度细分)
                        result.rgb = half3(
                            smoothstep(0.3, 0.8, t),
                            1.0 - smoothstep(0.5, 1.0, t),
                            0.0
                        );
                        break;
                    }
                    case 33: // SkyExposure：天光曝光近似（AO + 法线朝向 → 环境光接收量）
                    {
                        // 原理：朝上的面接收更多天光，朝下的面被遮蔽
                        // 结合法线与上方向的夹角 + 简易阴影遮蔽估算
                        // 输出：白=充分曝光(朝上/受光), 灰=侧面, 黑=严重遮蔽(朝下/背光)
                        float3 N = normalize(input.normalWS);
                        float3 upDir = float3(0, 1, 0);
                        float NdotUp = dot(N, upDir);
                        // 天光因子：朝上=1, 侧面=0.5, 朝下=0.0
                        float skyFactor = saturate(NdotUp * 0.5 + 0.5);
                        // 加入法线与光照方向的遮蔽因子（简易 AO 近似）
                        float3 L = normalize(_MainLightPosition.xyz);
                        float NdotL = saturate(dot(N, L));
                        float ao = lerp(0.3, 1.0, NdotL); // 背光面有遮蔽
                        float exposure = skyFactor * ao;
                        // 输出灰度：白=充分曝光, 黑=严重遮蔽
                        result.rgb = half3(exposure, exposure, exposure);
                        break;
                    }

                // ===== 路由说明 =====
                // 以下模式由此 shader 的 default:discard 处理，因为它们由独立 Pass 路由：
                // case 15 (Overdraw)       → DebugReplacement_Overdraw.shader (Blend One One)
                // case 17 (ScreenNormal)   → ScreenSpaceBlitPass (深度缓冲重建法线)
                // case 18 (ShadowMap)      → ShadowMapPass (深度→世界坐标→阴影采样)
                // case 23 (Transparency)   → DebugReplacement_Overdraw.shader (透明队列过滤)
                default:
                        discard;
                        break;
                }

                // 颜色映射（在 gamma/offset 之后应用，因为 gamma/offset 已在各 case 内处理）
                result.rgb = ApplyColorMap(result.rgb, _ColorMapMode);

                // NaN/Infinity 异常检测：任何通道出现异常值时输出醒目颜色
                if (any(isnan(result.rgb)) || any(isinf(result.rgb)))
                {
                    result.rgb = half3(1, 0, 0); // NaN → 纯红色
                    result.a = 1;
                }

                return result;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
