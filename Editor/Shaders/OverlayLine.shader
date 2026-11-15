Shader "Hidden/ModelBox/OverlayLine"
{
    Properties
    {
        _Color("Color", Color) = (1, 1, 1, 1)
        _Length("Stretch Length", Float) = 0.1
        _NormalDirection("Normal Direction", Float) = 0
        _LineWidth("Line Width", Float) = 0
        _ViewportSize("Viewport Size", Vector) = (1, 1, 1, 1)
        _ZTest("ZTest", Int) = 4
    }

    // [fix] 内置管线 CG 实现：本 shader 仅用于 SceneView.duringSceneGui 中
    // GL.LoadProjectionMatrix/GL.modelview + Graphics.DrawMeshNow 的立即模式绘制。
    // 该路径不经过 SRP 相机渲染流程，URP 的 PerCamera/PerDraw 常量缓冲区
    // （unity_ObjectToWorld / unity_MatrixVP）不会按 GL 矩阵绑定 —— 此前使用
    // URP HLSL（TransformObjectToHClip）读到的矩阵为垃圾值，顶点被裁剪导致
    // 线框/法线/切线整批不可见。CG 的 UNITY_MATRIX_MVP 直接来自当前
    // GL.projection × GL.modelview（×DrawMeshNow 矩阵），与管线无关，
    // 在 URP 工程的 SceneView 中同样正常（Handles 内部即采用此机制）。
    SubShader
    {
        Tags
        {
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

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "UnityCG.cginc"

            half4 _Color;
            float _Length;
            float _NormalDirection;
            float _LineWidth;
            float4 _ViewportSize;

            struct appdata
            {
                float4 vertex : POSITION;   // 线段基点（局部空间）
                float3 normal : NORMAL;     // 拉伸方向（局部空间；线框网格无法线语义时为 0）
                float2 uv     : TEXCOORD0;  // x: 0=起点 1=终点；粗线 y: -1/+1 两侧
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 stretchDirection = v.normal;
                if (_NormalDirection > 0.5)
                {
                    stretchDirection = 0;
                    float matrixDeterminant = abs(determinant((float3x3)unity_ObjectToWorld));
                    if (dot(v.normal, v.normal) > 1e-20 && matrixDeterminant > 1e-20)
                    {
                        float3 oldWorldDirection = mul((float3x3)unity_ObjectToWorld, v.normal);
                        float3 normalWS = mul(v.normal, (float3x3)unity_WorldToObject);
                        normalWS *= rsqrt(max(dot(normalWS, normalWS), 1e-20));
                        // 仅修正方向，保留此前随模型缩放的线段长度。
                        stretchDirection = mul((float3x3)unity_WorldToObject,
                            normalWS * length(oldWorldDirection));
                    }
                }
                if (_LineWidth > 0)
                {
                    float4 start = UnityObjectToClipPos(v.vertex.xyz);
                    float4 end = UnityObjectToClipPos(v.vertex.xyz + stretchDirection * _Length);
                    float startW = start.w < 0 ? min(start.w, -1e-5) : max(start.w, 1e-5);
                    float endW = end.w < 0 ? min(end.w, -1e-5) : max(end.w, 1e-5);
                    float2 viewport = max(_ViewportSize.xy, float2(1, 1));
                    float2 direction = (end.xy / endW - start.xy / startW) * viewport;
                    float2 perpendicular = float2(-direction.y, direction.x)
                        * rsqrt(max(dot(direction, direction), 1e-12));
                    o.pos = lerp(start, end, v.uv.x);
                    // NDC 的 2 倍像素换算与半宽抵消；自有 viewport 不依赖 SRP 相机常量。
                    o.pos.xy += perpendicular * (v.uv.y * _LineWidth) / viewport * o.pos.w;
                }
                else
                {
                    float3 pos = v.vertex.xyz + stretchDirection * (_Length * v.uv.x);
                    o.pos = UnityObjectToClipPos(pos);
                }
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
    Fallback Off
}
