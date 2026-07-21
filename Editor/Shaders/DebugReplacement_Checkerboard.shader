Shader "Hidden/ModelBox/Checkerboard"
{
    Properties
    {
        _GridSize ("Grid Size", Float) = 10
        _ColorA ("Color A", Color) = (0.9, 0.9, 0.9, 1)
        _ColorB ("Color B", Color) = (0.2, 0.2, 0.2, 1)
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
                float _GridSize;
                float4 _ColorA;
                float4 _ColorB;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
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
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 grid = floor(input.uv * _GridSize);
                float checker = fmod(grid.x + grid.y, 2.0);
                return lerp(_ColorA, _ColorB, checker);
            }
            ENDHLSL
        }
    }
}
