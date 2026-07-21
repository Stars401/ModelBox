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

                // 计算选中通道的标量值
                half selectedChannel = dot(tex, _ChannelMask);

                // 钳制：在 [_ClampMin, _ClampMax] 范围内的区域显示原色，范围外显示暗色
                // 硬边 step() 精确标定阈值边界（模拟 shader 中的 step 采样）
                float inRange = step(_ClampMin, selectedChannel) * (1.0 - step(_ClampMax, selectedChannel));

                half4 result;
                if (_DisplayMode == 1)
                {
                    // 灰度模式：通道值 → 灰度，钳制范围外变暗
                    result = half4(selectedChannel, selectedChannel, selectedChannel, 1);
                }
                else
                {
                    // 通道模式：隔离选中通道，钳制范围外变暗
                    result = tex * half4(_ChannelMask.rgb, 1);
                }

                // 应用钳制蒙版（范围内=原色，范围外=微弱暗灰保留形状感知）
                result.rgb = result.rgb * inRange + half3(0.04, 0.04, 0.04) * (1.0 - inRange);

                return result;
            }
            ENDHLSL
        }
    }
}
