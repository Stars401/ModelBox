Shader "Hidden/ModelBox/OverdrawCount"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "OverdrawCount"
            Blend One One
            ZWrite Off
            ZTest LEqual  // 只统计可见绘制（被遮挡物体不计数，符合 GPU Early-Z 实际开销）
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Each draw adds 1/16 to the accumulator
                // With 4 channels (RGBA), max visual range = 16 draws per channel
                return half4(1.0 / 16.0, 1.0 / 16.0, 1.0 / 16.0, 1.0 / 16.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
