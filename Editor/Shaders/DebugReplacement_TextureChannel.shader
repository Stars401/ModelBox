Shader "Hidden/ModelBox/TextureChannel"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _ChannelMask ("Channel Mask", Vector) = (1, 1, 1, 0)
        _DisplayMode ("Display Mode", Int) = 0
        _TexScale ("UV Scale", Vector) = (1, 1, 0, 0)
        _TexOffset ("UV Offset", Vector) = (0, 0, 0, 0)
        _UVChannel ("UV Channel", Int) = 0
        _WorldSpaceUV ("World Space UV", Int) = 0
        _WorldUVScale ("World UV Scale", Float) = 0.01
        _ClampMin ("Clamp Min", Float) = 0
        _ClampMax ("Clamp Max", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 100
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _ChannelMask;
                int _DisplayMode;
                float4 _TexScale;
                float4 _TexOffset;
                int _UVChannel;
                int _WorldSpaceUV;
                float _WorldUVScale;
                float _ClampMin;
                float _ClampMax;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
                float2 uv3 : TEXCOORD3;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                float2 uv;
                if (_WorldSpaceUV)
                {
                    // 世界坐标 XZ 平面采样（适用于模型群噪波等场景）
                    float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                    uv = worldPos.xz * _WorldUVScale;
                }
                else if (_UVChannel == 1) uv = input.uv1;
                else if (_UVChannel == 2) uv = input.uv2;
                else if (_UVChannel == 3) uv = input.uv3;
                else uv = input.uv0;

                output.uv = uv * _TexScale.xy + _TexOffset.xy;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                // [fix] 选中通道标量：RGB 模式用亮度（原通道加权和可 >1，导致钳制范围 0~1 内永远失效）；
                // 单通道（R/G/B/A）用 dot 提取对应分量
                bool isRgbMode = (_ChannelMask.r + _ChannelMask.g + _ChannelMask.b) > 1.5;
                float selectedChannel = isRgbMode
                    ? dot(tex.rgb, half3(0.299, 0.587, 0.114))
                    : dot(tex, _ChannelMask);

                // [fix] 亮度范围钳制：范围内数值重映射到 0~1（窄带拉伸，放大细微差异便于观察），
                // 范围外显示为暗色（保留定位能力）。闭区间 [Min, Max]；Min==Max 时防除零。
                if (_ClampMin > 0.001 || _ClampMax < 0.999)
                {
                    float range = max(_ClampMax - _ClampMin, 1e-4);
                    float normalized = saturate((selectedChannel - _ClampMin) / range);
                    float inRange = step(_ClampMin, selectedChannel) * step(selectedChannel, _ClampMax);
                    float3 band = normalized.xxx * inRange + half3(0.04, 0.04, 0.04) * (1.0 - inRange);
                    return half4(band, 1);
                }

                // [feat] 单色（灰度）显示：隔离通道以灰度呈现，避免红/蓝色调影响数值观察
                if (_DisplayMode == 1)
                    return half4(selectedChannel.xxx, 1);

                // 通道着色显示（原有行为）：R → 红、G → 绿、B → 蓝
                return tex * half4(_ChannelMask.rgb, 1);
            }
            ENDHLSL
        }
    }
}
