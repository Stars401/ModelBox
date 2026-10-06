using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ModelBox
{
    /// <summary>
    /// 屏幕空间全屏 Blit Pass：从深度缓冲重建世界坐标，支持 Depth/WorldPos/ScreenNormal/NdotV/Fresnel/FlatNormal 可视化。
    /// 替代原有 Geometry Pass 中的 Depth/ScreenNormal 等模式，不受顶点动画影响。
    /// </summary>
    public class ScreenSpaceBlitPass : ScriptableRenderPass
    {
        private Material _blitMaterial;
        private ModelBoxParameters _currentParams;
        private int _viewMode;

#if UNITY_2022_1_OR_NEWER
        private RTHandle _cameraColorHandle;
        private RTHandle _cameraDepthHandle;

        public void SetTargets(RTHandle colorTarget, RTHandle depthTarget)
        {
            _cameraColorHandle = colorTarget;
            _cameraDepthHandle = depthTarget;
            // [fix] 仅绑定颜色目标，不绑定深度附件：URP 在 MSAA 关闭时 cameraDepthTargetHandle
            // 与 _CameraDepthTexture 是同一资源，而本 Pass 的 shader 会采样 _CameraDepthTexture ——
            // 同一资源同时作为深度附件（DSV）与采样源（SRV）构成反馈环，D3D11 会强制解绑 SRV，
            // 采样返回 0/未定义数据，表现为深度可视化黑屏/花屏。本 Pass ZWrite Off + ZTest Always，
            // 不需要深度缓冲。
            if (colorTarget != null)
                ConfigureTarget(colorTarget);
        }
#else
        private RenderTargetIdentifier _cameraColorTarget;
        private RenderTargetIdentifier _cameraDepthTarget;

        public void SetTargets(RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget)
        {
            _cameraColorTarget = colorTarget;
            _cameraDepthTarget = depthTarget;
            // [fix] 同上：不绑定深度附件，避免与 _CameraDepthTexture 采样构成反馈环
            ConfigureTarget(colorTarget);
        }
#endif

        public ScreenSpaceBlitPass()
        {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void SetMaterial(Material material)
        {
            _blitMaterial = material;
        }

        public void Configure(ModelBoxParameters parameters, int viewMode)
        {
            _currentParams = parameters;
            _viewMode = viewMode;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_blitMaterial == null)
            {
                Debug.LogWarning("[ModelBox] ScreenSpaceBlit Pass: material 为 null，跳过绘制。" +
                    "请检查 Hidden/ModelBox/ScreenSpaceFromDepth shader 是否存在且编译正确。");
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_ScreenSpace_Draw");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_ScreenSpace_Draw")))
            {
                _currentParams.ApplyToMaterial(_blitMaterial);
                _blitMaterial.SetInt("_ViewMode", _viewMode);

                // SV_VertexID 全屏三角形，shader 内部采样 _CameraDepthTexture 重建世界坐标
                cmd.DrawProcedural(Matrix4x4.identity, _blitMaterial, 0,
                    MeshTopology.Triangles, 3, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
