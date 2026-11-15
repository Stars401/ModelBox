Shader "Hidden/ModelBox/Tests/MaterialKeywordUndo"
{
    SubShader
    {
        Pass
        {
            Name "MaterialKeywordUndo"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _ _MODELBOX_TEST_KEYWORD

            float4 Vert(float4 position : POSITION) : SV_POSITION
            {
                return position;
            }

            float4 Frag() : SV_Target
            {
                #if defined(_MODELBOX_TEST_KEYWORD)
                    return float4(1, 0, 0, 1);
                #else
                    return float4(0, 0, 0, 1);
                #endif
            }
            ENDHLSL
        }
    }
}
