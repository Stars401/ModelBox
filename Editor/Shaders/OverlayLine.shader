Shader "Hidden/ModelBox/OverlayLine"
{
    Properties
    {
        _Color("Color", Color) = (1, 1, 1, 1)
        _Length("Stretch Length", Float) = 0.1
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

            struct appdata
            {
                float4 vertex : POSITION;   // 线段基点（局部空间）
                float3 normal : NORMAL;     // 拉伸方向（局部空间；线框网格无法线语义时为 0）
                float2 uv     : TEXCOORD0;  // x: 0=起点(不拉伸) 1=终点(按 _Length 拉伸)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 pos = v.vertex.xyz + v.normal * (_Length * v.uv.x);
                o.pos = UnityObjectToClipPos(pos);
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
