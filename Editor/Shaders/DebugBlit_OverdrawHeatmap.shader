Shader "Hidden/ModelBox/OverdrawHeatmap"
{
    Properties
    {
        _OverdrawMaxCount("Max Overdraw Count", Float) = 8.0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "OverdrawHeatmap"
            ZWrite Off Cull Off ZTest Always Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_OverdrawAccumTexture);
            SAMPLER(sampler_OverdrawAccumTexture);

            CBUFFER_START(UnityPerMaterial)
                float _OverdrawMaxCount;
            CBUFFER_END

            struct BlitVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // Fullscreen triangle from SV_VertexID (same pattern as OpaqueTextureBlit)
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

            // Map overdraw count to heatmap color: black -> blue -> cyan -> green -> yellow -> red
            half3 OverdrawHeatmap(float t)
            {
                // t is normalized count (0 = no overdraw, 1 = max)
                half3 c;

                // Black to blue (0.0 - 0.2)
                c.r = 0.0;
                c.g = 0.0;
                c.b = smoothstep(0.0, 0.2, t);

                // Blue to cyan to green (0.2 - 0.5)
                c.g = t > 0.2 ? smoothstep(0.2, 0.5, t) : 0.0;
                c.b = t > 0.5 ? smoothstep(0.5, 0.7, t) * (1.0 - smoothstep(0.5, 0.7, t)) : c.b;

                // Green to yellow (0.5 - 0.7)
                c.r = t > 0.5 ? smoothstep(0.5, 0.7, t) : 0.0;

                // Yellow to red (0.7 - 1.0)
                c.g = t > 0.7 ? 1.0 - smoothstep(0.7, 1.0, t) : c.g;

                return saturate(c);
            }

            half4 Frag(BlitVaryings input) : SV_Target
            {
                half4 accum = SAMPLE_TEXTURE2D_X(_OverdrawAccumTexture, sampler_OverdrawAccumTexture, input.uv);

                // Use the max channel as the overdraw count
                // Each draw adds 1/16, so count = channel * 16
                float count = max(accum.r, max(accum.g, accum.b)) * 16.0;

                // Normalize to [0, 1] based on max count
                float t = saturate(count / max(_OverdrawMaxCount, 1.0));

                half3 color = OverdrawHeatmap(t);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
