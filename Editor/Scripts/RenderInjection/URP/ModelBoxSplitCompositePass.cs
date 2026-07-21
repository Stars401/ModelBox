using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if !UNITY_6000_0_OR_NEWER
using UnityEngine.Experimental.Rendering;
#endif

namespace ModelBox
{
    /// <summary>
    /// 分屏合成 Pass：在 AfterRenderingTransparents 绘制左右分屏对比。
    /// 左半采样正常场景 RT，右半采样调试输出 RT。
    /// 支持快照 A/B 对比模式。
    /// </summary>
    public class ModelBoxSplitCompositePass : ScriptableRenderPass
    {
        private Material _compositeMaterial;
        private float _splitPosition = 0.5f;

        // [feat] 快照 A/B 支持
        private int _snapshotMode = 0;
        private RenderTexture _snapshotART;
        private RenderTexture _snapshotBRT;

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

        public void SetMaterial(Material material)
        {
            _compositeMaterial = material;
        }

        public void Configure(float splitPosition)
        {
            _splitPosition = Mathf.Clamp01(splitPosition);
        }

        /// <summary>设置快照模式和纹理引用。</summary>
        public void ConfigureSnapshot(int mode, RenderTexture snapshotA, RenderTexture snapshotB)
        {
            _snapshotMode = mode;
            _snapshotART = snapshotA;
            _snapshotBRT = snapshotB;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_compositeMaterial == null) return;

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_SplitComposite");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_SplitComposite")))
            {
                _compositeMaterial.SetFloat("_SplitPosition", _splitPosition);
                _compositeMaterial.SetFloat("_SnapshotMode", _snapshotMode);

                // [feat] 快照捕获：从相机颜色缓冲复制到快照 RT
                var manager = ModelBoxManager.Instance;
                if (manager != null)
                {
#if UNITY_2022_1_OR_NEWER
                    var camTarget = _cameraColorHandle;
#else
                    var camTarget = _cameraColorTarget; // [fix] 使用颜色缓冲而非深度缓冲
#endif
                    if (manager.CaptureSnapshotA && _snapshotART != null)
                    {
                        cmd.Blit(camTarget, _snapshotART);
                        manager.CaptureSnapshotA = false;
                    }
                    if (manager.CaptureSnapshotB && _snapshotBRT != null)
                    {
                        cmd.Blit(camTarget, _snapshotBRT);
                        manager.CaptureSnapshotB = false;
                    }
                }

                // [feat] 绑定快照纹理（不存在时绑定黑色占位避免 shader 报错）
                var black = Texture2D.blackTexture;
                cmd.SetGlobalTexture("_SnapshotATex", _snapshotART != null ? (RenderTargetIdentifier)_snapshotART : black);
                cmd.SetGlobalTexture("_SnapshotBTex", _snapshotBRT != null ? (RenderTargetIdentifier)_snapshotBRT : black);

                cmd.DrawProcedural(Matrix4x4.identity, _compositeMaterial, 0,
                    MeshTopology.Triangles, 3, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
