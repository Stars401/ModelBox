Shader "Hidden/ModelBox/OverlayLine"
{
    Properties
    {
        _Color("Color", Color) = (1, 1, 1, 1)
        _Length("Stretch Length", Float) = 0.1
        _ZTest("ZTest", Int) = 4
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }
        LOD 100
        ZWrite Off
        ZTest [_ZTest]
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

        Pass
        {
            Name "ModelBoxOverlayLine"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Length;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;   // 线段基点（局部空间）
                float3 stretchDir : NORMAL;     // 拉伸方向（局部空间；线框网格为 0）
                float2 tipFlag    : TEXCOORD0;  // x: 0=起点(不拉伸) 1=终点(按 _Length 拉伸)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 pos = input.positionOS.xyz + input.stretchDir * (_Length * input.tipFlag.x);
                o.positionCS = TransformObjectToHClip(pos);
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
