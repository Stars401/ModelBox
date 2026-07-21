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
            if (colorTarget != null)
                ConfigureTarget(colorTarget, depthTarget);
        }
#else
        private RenderTargetIdentifier _cameraColorTarget;
        private RenderTargetIdentifier _cameraDepthTarget;

        public void SetTargets(RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget)
        {
            _cameraColorTarget = colorTarget;
            _cameraDepthTarget = depthTarget;
            ConfigureTarget(colorTarget, depthTarget);
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
