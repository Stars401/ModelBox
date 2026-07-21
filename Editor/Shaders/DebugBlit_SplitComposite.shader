Shader "Hidden/ModelBox/SplitComposite"
{
    Properties
    {
        _SplitPosition("Split Position", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SplitComposite"
            ZWrite Off Cull Off ZTest Always Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_NormalSceneTex);
            SAMPLER(sampler_NormalSceneTex);

            TEXTURE2D_X(_DebugSceneTex);
            SAMPLER(sampler_DebugSceneTex);

            // [feat] 快照 A/B 纹理
            TEXTURE2D_X(_SnapshotATex);
            SAMPLER(sampler_SnapshotATex);

            TEXTURE2D_X(_SnapshotBTex);
            SAMPLER(sampler_SnapshotBTex);

            CBUFFER_START(UnityPerMaterial)
                float _SplitPosition;
                float _SnapshotMode; // 0=正常分屏, 1=快照A vs 实时, 2=快照A vs 快照B, 3=快照B vs 实时
            CBUFFER_END

            struct BlitVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // SV_VertexID 全屏三角形（与其他 Blit shader 同模式）
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

            half4 Frag(BlitVaryings input) : SV_Target
            {
                float2 uv = input.uv;
                half3 color;

                // 分割线：2px 宽抗锯齿白色半透明线
                float pixelWidth = fwidth(uv.x);
                float dividerWidth = max(pixelWidth * 2.0, 0.002);
                float divider = 1.0 - smoothstep(0.0, dividerWidth, abs(uv.x - _SplitPosition));

                if (uv.x < _SplitPosition)
                {
                    // 左半：根据快照模式选择来源
                    if (_SnapshotMode >= 2.0 && _SnapshotMode < 3.0)
                        color = SAMPLE_TEXTURE2D_X(_SnapshotATex, sampler_SnapshotATex, uv).rgb; // A vs B
                    else if (_SnapshotMode >= 3.0)
                        color = SAMPLE_TEXTURE2D_X(_SnapshotBTex, sampler_SnapshotBTex, uv).rgb; // B vs 实时
                    else
                        color = SAMPLE_TEXTURE2D_X(_NormalSceneTex, sampler_NormalSceneTex, uv).rgb; // 默认正常场景
                }
                else
                {
                    // 右半：根据快照模式选择来源
                    if (_SnapshotMode >= 1.0 && _SnapshotMode < 2.0)
                        color = SAMPLE_TEXTURE2D_X(_SnapshotATex, sampler_SnapshotATex, uv).rgb; // 实时 vs A
                    else if (_SnapshotMode >= 2.0 && _SnapshotMode < 3.0)
                        color = SAMPLE_TEXTURE2D_X(_SnapshotBTex, sampler_SnapshotBTex, uv).rgb; // A vs B
                    else
                        color = SAMPLE_TEXTURE2D_X(_DebugSceneTex, sampler_DebugSceneTex, uv).rgb; // 默认调试可视化
                }

                // 叠加分割线（白色半透明）
                color = lerp(color, half3(1, 1, 1), divider * 0.8);

                return half4(color, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
