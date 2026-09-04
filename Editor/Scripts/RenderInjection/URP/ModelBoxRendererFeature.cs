using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ModelBox
{
    /// <summary>
    /// URP ScriptableRendererFeature。
    ///
    /// 路由逻辑：
    /// - Geometry Pass (overrideMaterial DrawRenderers)：大部分调试模式（含 WorldPosition/NdotV/Fresnel/FlatNormal 等）
    /// - ScreenSpace Pass (全屏 Blit 从深度缓冲重建)：仅 Depth(7) 和 ScreenNormal(17)
    /// - OpaqueTexture Pass：双 Pass 架构（Capture + Draw）
    /// - Overdraw Pass：双 Pass 架构（Count + Heatmap）
    /// - ShadowMap Pass：从深度重建世界坐标 + 采样阴影贴图
    /// </summary>
    public class ModelBoxRendererFeature : ScriptableRendererFeature
    {
        [System.NonSerialized] public ModelBoxGeometryPass debugPass;
        // [fix v0.6] 透明队列调试 Pass：几何模式下补齐透明物体（水面/玻璃/粒子/相机空间 UI）的调试覆盖
        [System.NonSerialized] public ModelBoxGeometryPass debugPassTransparent;
        [System.NonSerialized] public ModelBoxOpaqueTextureDrawPass opaqueDrawPass;
        [System.NonSerialized] public ScreenSpaceBlitPass screenSpaceBlitPass;
        [System.NonSerialized] public ModelBoxOverdrawDrawPass overdrawDrawPass;
        [System.NonSerialized] public ModelBoxShadowMapPass shadowMapPass;
        [System.NonSerialized] public ModelBoxSplitCompositePass splitCompositePass;
        [System.NonSerialized] public ModelBoxNormalSceneCapturePass normalSceneCapturePass;
        [System.NonSerialized] public ModelBoxDebugSceneCapturePass debugSceneCapturePass;
        [System.NonSerialized] public bool isInitialized;
        private int _initRetryCount;

        // [feat] 快照 A/B 持久化 RT
        private RenderTexture _snapshotART;
        private RenderTexture _snapshotBRT;

        private Material _debugMaterial;
        private Material _opaqueBlitMaterial;
        private Material _screenSpaceBlitMaterial;
        private Material _overdrawCountMaterial;
        private Material _overdrawHeatmapMaterial;
        private Material _shadowMapBlitMaterial;
        private Material _splitCompositeMaterial;

        public override void Create()
        {
            var shader = Shader.Find("Hidden/ModelBox/Geometry");
            if (shader == null)
            {
                isInitialized = false;
                return;
            }

            CoreUtils.Destroy(_debugMaterial);
            _debugMaterial = CoreUtils.CreateEngineMaterial(shader);
            _debugMaterial.hideFlags = HideFlags.HideAndDontSave;

            // 创建 OpaqueTexture 专用 Blit 材质
            var blitShader = Shader.Find("Hidden/ModelBox/OpaqueTextureBlit");
            CoreUtils.Destroy(_opaqueBlitMaterial);
            if (blitShader != null)
            {
                _opaqueBlitMaterial = CoreUtils.CreateEngineMaterial(blitShader);
                _opaqueBlitMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                Debug.LogWarning("[ModelBox] 无法加载 OpaqueTextureBlit shader，OpaqueTexture 模式将不可用。");
            }

            // 创建 ScreenSpace Blit 材质（从深度缓冲重建世界坐标，支持多种可视化模式）
            var screenSpaceShader = Shader.Find("Hidden/ModelBox/ScreenSpaceFromDepth");
            CoreUtils.Destroy(_screenSpaceBlitMaterial);
            if (screenSpaceShader != null)
            {
                _screenSpaceBlitMaterial = CoreUtils.CreateEngineMaterial(screenSpaceShader);
                _screenSpaceBlitMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                Debug.LogWarning("[ModelBox] 无法加载 ScreenSpaceFromDepth shader，ScreenSpace 模式将不可用。");
            }

            // 创建 Overdraw 计数材质（Blend One One 累加 shader）
            var overdrawCountShader = Shader.Find("Hidden/ModelBox/OverdrawCount");
            CoreUtils.Destroy(_overdrawCountMaterial);
            if (overdrawCountShader != null)
            {
                _overdrawCountMaterial = CoreUtils.CreateEngineMaterial(overdrawCountShader);
                _overdrawCountMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                Debug.LogWarning("[ModelBox] 无法加载 OverdrawCount shader，Overdraw 模式将不可用。");
            }

            // 创建 Overdraw 热力图 Blit 材质
            var overdrawHeatmapShader = Shader.Find("Hidden/ModelBox/OverdrawHeatmap");
            CoreUtils.Destroy(_overdrawHeatmapMaterial);
            if (overdrawHeatmapShader != null)
            {
                _overdrawHeatmapMaterial = CoreUtils.CreateEngineMaterial(overdrawHeatmapShader);
                _overdrawHeatmapMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                Debug.LogWarning("[ModelBox] 无法加载 OverdrawHeatmap shader，Overdraw 模式将不可用。");
            }

            // 创建 Shadow Map Blit 材质
            var shadowMapShader = Shader.Find("Hidden/ModelBox/ShadowMapBlit");
            CoreUtils.Destroy(_shadowMapBlitMaterial);
            if (shadowMapShader != null)
            {
                _shadowMapBlitMaterial = CoreUtils.CreateEngineMaterial(shadowMapShader);
                _shadowMapBlitMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                Debug.LogWarning("[ModelBox] 无法加载 ShadowMapBlit shader，ShadowMap 模式将不可用。");
            }

            // Geometry Pass / Capture Pass
            debugPass = new ModelBoxGeometryPass(_debugMaterial, _overdrawCountMaterial)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };

            // [fix v0.6] 透明队列 Geometry Pass：在真实透明物体渲染后（AfterRenderingTransparents+2）
            // 用 overrideMaterial 覆盖绘制透明队列，确保几何调试模式全场景覆盖。
            // 时机依据：
            //   - 晚于 AfterRenderingTransparents(300)：真实透明 Pass 已画完，调试色完全覆盖原透明外观
            //   - 晚于分屏模式下的不透明调试 Pass(301)：左右两侧捕获内容互不污染
            //   - 早于分屏调试场景捕获 Pass(+5) 与合成 Pass(+10)：右侧捕获包含透明调试结果
            debugPassTransparent = new ModelBoxGeometryPass(_debugMaterial, _overdrawCountMaterial, true)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 2
            };

            // OpaqueTexture Draw Pass: 在 AfterRenderingTransparents 绘制全屏可视化
            // 必须在 FinalBlit 之前执行，否则 DrawProcedural 的输出不会出现在最终画面中
            // AfterRendering 在 FinalBlit 之后，太晚了
            opaqueDrawPass = new ModelBoxOpaqueTextureDrawPass(_opaqueBlitMaterial)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };

            // ScreenSpace Pass: 在 AfterRenderingTransparents 绘制全屏深度重建可视化
            screenSpaceBlitPass = new ScreenSpaceBlitPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
            screenSpaceBlitPass.SetMaterial(_screenSpaceBlitMaterial);

            // Overdraw Draw Pass: 在 AfterRenderingTransparents 绘制全屏热力图
            overdrawDrawPass = new ModelBoxOverdrawDrawPass(_overdrawHeatmapMaterial)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };

            // ShadowMap Pass: 在 AfterRenderingTransparents 绘制全屏阴影可视化
            shadowMapPass = new ModelBoxShadowMapPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
            shadowMapPass.SetMaterial(_shadowMapBlitMaterial);

            // 创建分屏合成材质
            var splitShader = Shader.Find("Hidden/ModelBox/SplitComposite");
            CoreUtils.Destroy(_splitCompositeMaterial);
            if (splitShader != null)
            {
                _splitCompositeMaterial = CoreUtils.CreateEngineMaterial(splitShader);
                _splitCompositeMaterial.hideFlags = HideFlags.HideAndDontSave;
            }

            // Split Composite Pass: 在 AfterRenderingTransparents 绘制分屏合成
            splitCompositePass = new ModelBoxSplitCompositePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
            };
            splitCompositePass.SetMaterial(_splitCompositeMaterial);

            // Normal Scene Capture Pass: 分屏模式时在 Debug Pass 之前捕获正常场景
            normalSceneCapturePass = new ModelBoxNormalSceneCapturePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques - 1
            };
            normalSceneCapturePass.SetCaptureAction(debugPass.ExecuteNormalSceneCapture);

            // [fix] 分屏调试场景捕获 Pass：在 Debug Pass 之后复制调试输出到独立 RT
            debugSceneCapturePass = new ModelBoxDebugSceneCapturePass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 2
            };

            isInitialized = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!isInitialized)
            {
                if (_initRetryCount < 3) { _initRetryCount++; Create(); }
                if (!isInitialized) return;
            }

            if (renderingData.cameraData.cameraType != CameraType.Game &&
                renderingData.cameraData.cameraType != CameraType.SceneView)
                return;

            var manager = ModelBoxManager.Instance;
            if (manager == null || manager.CurrentMode == DebugViewMode.None)
                return;

            // [fix v0.4] VR/XR 兼容性：立体渲染相机跳过全屏 Blit 模式
            // 全屏三角形 Blit 在 stereo rendering 下只覆盖单眼，会导致显示异常
            // Geometry overrideMaterial 模式（DrawRenderers）不受影响，仍可使用
            bool isStereo = renderingData.cameraData.camera.stereoEnabled;

            // Depth(7) 和 ScreenNormal(17) 已迁移到 ScreenSpace 路径
            bool needsDepth = (manager.CurrentMode == DebugViewMode.DiagRawDepth);

            bool needsOpaqueTex = (manager.CurrentMode == DebugViewMode.OpaqueTexture);

            bool needsOverdraw = (manager.CurrentMode == DebugViewMode.Overdraw);

            bool needsShadowMap = (manager.CurrentMode == DebugViewMode.ShadowMap);

            bool needsTransparency = (manager.CurrentMode == DebugViewMode.TransparencyLayers);

            // ScreenSpace 路径：只有 Depth(7) 和 ScreenNormal(17) 从深度缓冲重建
            // WorldPosition(1), NdotV(20), Fresnel(21), FlatNormal(16) 走 Geometry overrideMaterial 路径
            bool needsScreenSpace = (manager.CurrentMode == DebugViewMode.Depth ||
                                     manager.CurrentMode == DebugViewMode.ScreenNormal ||
                                     manager.CurrentMode == DebugViewMode.RayMarch);

            // [fix v0.4] VR/XR: 立体渲染相机跳过全屏 Blit 模式（变量已在上文声明，此处仅做检测）
            if (isStereo && (needsScreenSpace || needsOpaqueTex || needsOverdraw || needsShadowMap || needsTransparency || manager.SplitScreenEnabled))
            {
                // VR 模式下全屏 Blit 不可用，仅保留 Geometry overrideMaterial 模式
                var geometryModes = !needsScreenSpace && !needsOpaqueTex && !needsOverdraw && !needsShadowMap && !needsTransparency;
                if (!geometryModes)
                {
                    // 当前模式在 VR 下不可用，跳过本帧
                    return;
                }
            }

            bool needsSplitScreen = manager.SplitScreenEnabled;

            // [fix] 分屏关闭时释放快照 RT，避免 GPU 内存泄漏
            if (!needsSplitScreen && (_snapshotART != null || _snapshotBRT != null))
                ReleaseSnapshotRTs();

            // [feat] SEL 模式：传递仅选中物体标志到 Geometry Pass
            debugPass.SetDebugOnlySelected(manager.DebugOnlySelected);
            debugPassTransparent.SetDebugOnlySelected(manager.DebugOnlySelected);

            // === 分屏模式：在透明物体渲染后捕获正常场景 ===
            // 此时相机颜色缓冲 = 不透明物体 + 天空盒 + 透明物体（水面等），不含 Debug 覆写
            if (needsSplitScreen && !manager.FreezeLeftSnapshot)
            {
                // [fix] AfterRenderingTransparents：在透明物体渲染后捕获，确保水面等透明物体出现在左侧正常场景中
                normalSceneCapturePass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                renderer.EnqueuePass(normalSceneCapturePass);
            }

            // ScreenSpace 模式的 ViewMode 映射（只含 Depth 和 ScreenNormal）
            int screenSpaceViewMode = -1;
            if (needsScreenSpace)
            {
                switch (manager.CurrentMode)
                {
                    case DebugViewMode.Depth:        screenSpaceViewMode = 0; break;
                    case DebugViewMode.ScreenNormal:  screenSpaceViewMode = 2; break;
                    case DebugViewMode.RayMarch:      screenSpaceViewMode = 6; break;
                }
            }

            // === 默认 Geometry Pass：非专用路径的模式走 overrideMaterial ===
            if (!needsOpaqueTex && !needsOverdraw && !needsShadowMap && !needsScreenSpace && !needsTransparency)
            {
                debugPass.ConfigureForMode(manager.CurrentMode, manager.CurrentParameters);
                // [fix v0.6] 透明队列 Pass 同步模式/参数（同一 overrideMaterial，_DebugMode 一致）
                debugPassTransparent.ConfigureForMode(manager.CurrentMode, manager.CurrentParameters);

                if (needsDepth)
                {
                    debugPass.ConfigureInput(ScriptableRenderPassInput.Depth);
                    debugPassTransparent.ConfigureInput(ScriptableRenderPassInput.Depth);
                }

                // [fix] 分屏模式下 Debug Pass 移到透明物体之后，确保正常场景先被捕获（含水面等透明物体）
                debugPass.renderPassEvent = needsSplitScreen
                    ? RenderPassEvent.AfterRenderingTransparents + 1
                    : RenderPassEvent.AfterRenderingOpaques;
                renderer.EnqueuePass(debugPass);
                // [fix v0.6] 透明队列调试绘制：事件固定 AfterRenderingTransparents+2
                // （非分屏/分屏两种情形均晚于本 Pass 的不透明调试绘制与真实透明 Pass）
                renderer.EnqueuePass(debugPassTransparent);
            }

            // === OpaqueTexture 模式：双 Pass 架构 ===
            if (needsOpaqueTex)
            {
                // Capture Pass (debugPass): 在 AfterRenderingOpaques+1 捕获 opaque-only 相机颜色
                debugPass.ConfigureForMode(manager.CurrentMode, manager.CurrentParameters);
                debugPass.ConfigureInput(ScriptableRenderPassInput.Color);
                debugPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;
                renderer.EnqueuePass(debugPass);

                // Draw Pass (opaqueDrawPass): 在 AfterRenderingTransparents 绘制全屏可视化（FinalBlit 前）
                opaqueDrawPass.Configure(manager.CurrentParameters);
                renderer.EnqueuePass(opaqueDrawPass);
            }

            // === Overdraw 模式：双 Pass 架构 ===
            if (needsOverdraw)
            {
                // Capture Pass (debugPass): 在 AfterRenderingOpaques+1 用计数 shader 累加绘制
                debugPass.ConfigureForMode(manager.CurrentMode, manager.CurrentParameters);
                debugPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;
                renderer.EnqueuePass(debugPass);

                // Draw Pass (overdrawDrawPass): 在 AfterRenderingTransparents 绘制热力图
                overdrawDrawPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                overdrawDrawPass.Configure(manager.CurrentParameters.OverdrawMaxCount);
                renderer.EnqueuePass(overdrawDrawPass);
            }

            // === 透明层数模式：双 Pass 架构（与 Overdraw 对称，仅透明队列） ===
            if (needsTransparency)
            {
                // Capture Pass: 在 AfterRenderingOpaques+1 绘制透明物体到 capture RT
                // 与 Overdraw 共用相同时机 — DrawRenderers 使用预计算 cull results，
                // 不依赖 URP 是否已绘制透明物体。此时深度缓冲已有 opaque 数据，
                // 提供正确的遮挡关系。
                debugPass.ConfigureForMode(manager.CurrentMode, manager.CurrentParameters);
                debugPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;
                renderer.EnqueuePass(debugPass);

                // Draw Pass: 在 AfterRenderingTransparents 绘制热力图
                overdrawDrawPass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                overdrawDrawPass.Configure(manager.CurrentParameters.OverdrawMaxCount);
                renderer.EnqueuePass(overdrawDrawPass);
            }

            // === ShadowMap 模式：单一 Draw Pass（从深度缓冲重建世界坐标 + 采样阴影贴图） ===
            if (needsShadowMap)
            {
                shadowMapPass.Configure(manager.CurrentParameters);
                renderer.EnqueuePass(shadowMapPass);
            }

            // === ScreenSpace 模式：单一 Draw Pass（从深度缓冲重建世界坐标，多种可视化） ===
            if (needsScreenSpace)
            {
                screenSpaceBlitPass.Configure(manager.CurrentParameters, screenSpaceViewMode);
                renderer.EnqueuePass(screenSpaceBlitPass);
            }

            // === 分屏模式：最后绘制合成 Pass（左=正常，右=调试） ===
            if (needsSplitScreen)
            {
                // [fix] 在合成之前捕获调试输出到独立 RT（_DebugSceneTex）
                debugSceneCapturePass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 5;
                renderer.EnqueuePass(debugSceneCapturePass);

                // [feat] 快照 RT 生命周期管理
                EnsureSnapshotRTs(renderingData.cameraData.cameraTargetDescriptor);

                splitCompositePass.Configure(manager.SplitScreenPosition);
                splitCompositePass.ConfigureSnapshot(manager.SnapshotMode, _snapshotART, _snapshotBRT);
                splitCompositePass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents + 10;
                renderer.EnqueuePass(splitCompositePass);
            }
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (!isInitialized) return;

#if UNITY_2022_1_OR_NEWER
            debugPass?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            debugPassTransparent?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            opaqueDrawPass?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            screenSpaceBlitPass?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            overdrawDrawPass?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            shadowMapPass?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            splitCompositePass?.SetTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
            debugSceneCapturePass?.SetCameraColor(renderer.cameraColorTargetHandle);
#else
            debugPass?.SetTargets(renderer.cameraColorTarget, renderer.cameraDepthTarget);
            debugPassTransparent?.SetTargets(renderer.cameraColorTarget, renderer.cameraDepthTarget);
            opaqueDrawPass?.SetTargets(renderer.cameraColorTarget, renderer.cameraDepthTarget);
            screenSpaceBlitPass?.SetTargets(renderer.cameraColorTarget, renderer.cameraDepthTarget);
            overdrawDrawPass?.SetTargets(renderer.cameraColorTarget, renderer.cameraDepthTarget);
            shadowMapPass?.SetTargets(renderer.cameraColorTarget, renderer.cameraDepthTarget);
#endif
        }

        /// <summary>确保快照 RT 存在且尺寸匹配。首次调用时分配。</summary>
        private void EnsureSnapshotRTs(RenderTextureDescriptor desc)
        {
            if (_snapshotART == null || _snapshotART.width != desc.width || _snapshotART.height != desc.height)
            {
                ReleaseSnapshotRTs();
                _snapshotART = new RenderTexture(desc) { name = "ModelBox_SnapshotA", hideFlags = HideFlags.HideAndDontSave };
                _snapshotART.Create();
                _snapshotBRT = new RenderTexture(desc) { name = "ModelBox_SnapshotB", hideFlags = HideFlags.HideAndDontSave };
                _snapshotBRT.Create();
            }
        }

        private void ReleaseSnapshotRTs()
        {
            if (_snapshotART != null) { _snapshotART.Release(); CoreUtils.Destroy(_snapshotART); _snapshotART = null; }
            if (_snapshotBRT != null) { _snapshotBRT.Release(); CoreUtils.Destroy(_snapshotBRT); _snapshotBRT = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CoreUtils.Destroy(_debugMaterial);
                CoreUtils.Destroy(_opaqueBlitMaterial);
                CoreUtils.Destroy(_screenSpaceBlitMaterial);
                CoreUtils.Destroy(_overdrawCountMaterial);
                CoreUtils.Destroy(_overdrawHeatmapMaterial);
                CoreUtils.Destroy(_shadowMapBlitMaterial);
                CoreUtils.Destroy(_splitCompositeMaterial);
                debugPass?.ReleaseCaptureTexture();
                debugPassTransparent?.ReleaseCaptureTexture();
                debugSceneCapturePass?.ReleaseCaptureTexture();
                ReleaseSnapshotRTs();

                // [fix] 清理工具设置的全局 shader 属性，避免卸载后残留指向已释放纹理的引用
                Shader.SetGlobalTexture("_NormalSceneTex", null);
                Shader.SetGlobalTexture("_DebugSceneTex", null);
                Shader.SetGlobalTexture("_OverdrawAccumTexture", null);
                Shader.SetGlobalTexture("_SnapshotATex", null);
                Shader.SetGlobalTexture("_SnapshotBTex", null);
            }
            debugPass = null;
            debugPassTransparent = null;
            opaqueDrawPass = null;
            screenSpaceBlitPass = null;
            overdrawDrawPass = null;
            shadowMapPass = null;
            splitCompositePass = null;
            normalSceneCapturePass = null;
            debugSceneCapturePass = null;
            isInitialized = false;
        }
    }
}