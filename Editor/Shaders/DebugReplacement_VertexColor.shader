Shader "Hidden/ModelBox/VertexColor"
{
    Properties
    {
        // [feat] 顶点颜色可视化：0=RGB, 1=R, 2=G, 3=B, 4=A(灰度)
        _ChannelMode ("Channel Mode", Float) = 0
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

            CBUFFER_START(UnityPerMaterial)
                float _ChannelMode;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 通道隔离：单通道输出为灰度（R/G/B/A），RGB 输出原色
                if (_ChannelMode > 3.5)
                    return half4(input.color.a.xxx, 1);   // A → 灰度
                if (_ChannelMode > 2.5)
                    return half4(input.color.b.xxx, 1);   // B → 灰度
                if (_ChannelMode > 1.5)
                    return half4(input.color.g.xxx, 1);   // G → 灰度
                if (_ChannelMode > 0.5)
                    return half4(input.color.r.xxx, 1);   // R → 灰度
                return half4(input.color.rgb, 1);         // RGB
            }
            ENDHLSL
        }
    }
}
