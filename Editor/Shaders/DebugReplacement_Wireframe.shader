Shader "Hidden/ModelBox/Wireframe"
{
    Properties
    {
        _WireColor ("Wire Color", Color) = (0, 1, 0, 1)
        _FaceColor ("Face Color", Color) = (0.1, 0.1, 0.1, 0.3)
        _WireWidth ("Wire Width", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        LOD 100
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _WireColor;
                float4 _FaceColor;
                float _WireWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct GeomOutput
            {
                float4 positionCS : SV_POSITION;
                float3 barycentric : TEXCOORD0;
            };

            GeomOutput vert(Attributes input)
            {
                GeomOutput output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.barycentric = float3(0, 0, 0);
                return output;
            }

            [maxvertexcount(3)]
            void geom(triangle GeomOutput input[3], inout TriangleStream<GeomOutput> stream)
            {
                input[0].barycentric = float3(1, 0, 0);
                input[1].barycentric = float3(0, 1, 0);
                input[2].barycentric = float3(0, 0, 1);
                stream.Append(input[0]);
                stream.Append(input[1]);
                stream.Append(input[2]);
            }

            half4 frag(GeomOutput input) : SV_Target
            {
                float3 bary = input.barycentric;
                float3 deltas = fwidth(bary);
                float3 smoothing = deltas * _WireWidth;
                float3 thickness = smoothstep(float3(0, 0, 0), smoothing, bary);
                float minThickness = min(thickness.x, min(thickness.y, thickness.z));

                return lerp(_WireColor, _FaceColor, minThickness);
            }
            ENDHLSL
        }
    }
}
