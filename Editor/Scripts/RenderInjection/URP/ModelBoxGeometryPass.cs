using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
// Blitter 在 URP 12-14 (Unity 2021-2023) 位于 Experimental.Rendering，
// URP 17+ (Unity 6) 移至 UnityEngine.Rendering
#if !UNITY_6000_0_OR_NEWER
using UnityEngine.Experimental.Rendering;
#endif

namespace ModelBox
{
    /// <summary>
    /// 调试 Pass：
    ///   - 非 OpaqueTexture/Overdraw 模式：通过 overrideMaterial DrawRenderers 实现场景调试可视化。
    ///   - OpaqueTexture 模式：Capture Phase — 在 AfterRenderingOpaques+1 捕获
    ///     相机颜色（仅 opaque 物体）到临时纹理，供 Draw Pass 采样。
    ///   - Overdraw 模式：Capture Phase — 在 AfterRenderingOpaques+1 用计数 shader
    ///     累加绘制到临时纹理，供 OverdrawHeatmap Draw Pass 采样。
    /// [fix v0.6.2] 最终全量绘制 Pass：drawTransparentQueue=true 的实例过滤 RenderQueueRange(0, 5000)
    ///   （API 允许的全部渲染队列 0..5000，上限由引擎强制；>5000 的自定义队列物体超出
    ///   RenderQueueRange 表达范围，不在最终全量覆盖内），由 Feature 在 AfterRenderingTransparents+2 注入 ——
    ///   位于所有正常几何绘制（不透明 + 天空盒 + 透明）之后、分屏捕获之前，
    ///   是渲染队列的最后一个几何绘制 Pass：任何物体的调试效果都不会被后续正常渲染覆盖。
    ///   调试 shader ZWrite Off，最终全量绘制不污染深度缓冲；
    ///   遮挡关系由 ZTest LEqual 对正常渲染写入的深度判定，排序沿用 CommonTransparent。
    ///   drawTransparentQueue=false 的实例仍为 opaque 过滤（供 OpaqueTexture/Overdraw/透明层数
    ///   Capture 模式复用），几何调试路径只注入最终全量实例。
    /// </summary>
    public class ModelBoxGeometryPass : ScriptableRenderPass
    {
        private readonly Material _overrideMaterial;
        private readonly Material _overdrawCountMaterial;
        private FilteringSettings _filterSettings;
        private readonly List<ShaderTagId> _shaderTags;
        private DebugViewMode _currentMode;
        private ModelBoxParameters _currentParams;
        private bool _passEnabled;
        private bool _debugOnlySelected; // [feat] SEL 模式：仅对选中物体应用调试效果
        private readonly bool _finalSweep; // [fix v0.6.2] true=最终全量绘制实例（0..int.MaxValue 全队列）；false=opaque 过滤实例（Capture 模式复用）

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

        /// <summary>
        /// OpaqueTexture Capture Phase 的输出纹理。
        /// Draw Pass 通过此属性读取捕获结果进行采样。
        /// </summary>
        public RTHandle OpaqueCaptureTexture => _opaqueCaptureHandle;
        public RTHandle OverdrawCaptureTexture => _overdrawCaptureHandle;

        private RTHandle _opaqueCaptureHandle;
        private RTHandle _overdrawCaptureHandle;
        private RTHandle _normalSceneCaptureHandle;

        /// <summary>分屏模式：正常场景捕获纹理。</summary>
        public RTHandle NormalSceneCaptureTexture => _normalSceneCaptureHandle;

        public ModelBoxGeometryPass(Material overrideMaterial, Material overdrawCountMaterial = null, bool finalSweep = false)
        {
            _overrideMaterial = overrideMaterial;
            _overdrawCountMaterial = overdrawCountMaterial;
            _finalSweep = finalSweep;
            // [fix v0.6.2] 最终全量实例覆盖全部渲染队列（0..5000；RenderQueueRange API 上限为 5000，
            // 传 int.MaxValue 会抛 ArgumentOutOfRangeException 导致整条 URP 管线创建失败、编辑器每帧刷异常）；
            // 普通实例保持 opaque 过滤（供 Capture 模式复用）
            _filterSettings = new FilteringSettings(
                finalSweep ? new RenderQueueRange(0, 5000) : RenderQueueRange.opaque);

            _shaderTags = new List<ShaderTagId>
            {
                // URP 标准
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("LightweightForward"),
                new ShaderTagId("Universal2D"),

                // Built-in 管线兼容（第三方 shader 或内置 shader 可能使用）
                new ShaderTagId("ForwardBase"),
                new ShaderTagId("ForwardAdd"),

                // 通用标记（部分第三方 shader 使用 Always 确保始终渲染）
                new ShaderTagId("Always"),
            };
            _passEnabled = false;
        }

        public void ConfigureForMode(DebugViewMode mode, ModelBoxParameters parameters)
        {
            _currentMode = mode;
            _currentParams = parameters;
            _passEnabled = (mode != DebugViewMode.None);
        }

        /// <summary>设置 SEL 模式（仅对选中物体应用调试效果）。</summary>
        public void SetDebugOnlySelected(bool enabled)
        {
            _debugOnlySelected = enabled;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!_passEnabled) return;

            // OpaqueTexture 模式：执行 Capture Phase
            if (_currentMode == DebugViewMode.OpaqueTexture)
            {
                ExecuteOpaqueCapture(context, renderingData);
                return;
            }

            // Overdraw 模式：执行 Overdraw Capture Phase
            if (_currentMode == DebugViewMode.Overdraw)
            {
                ExecuteOverdrawCapture(context, renderingData);
                return;
            }

            // 透明层数模式：执行透明物体 Capture Phase
            if (_currentMode == DebugViewMode.TransparencyLayers)
            {
                ExecuteTransparencyCapture(context, renderingData);
                return;
            }

            // 非 OpaqueTexture 模式：overrideMaterial DrawRenderers
            if (_overrideMaterial == null) return;

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox");

            // [feat] SEL 模式：临时将选中物体分配到专用 Layer，渲染后恢复
            // [fix v0.6.2] SEL + 最终全量实例：过滤队列与所属实例保持一致
            // [fix v0.6.x] 本地调试互斥：被选区调试/材质沙盒替换了 sharedMaterials 的 Renderer
            // 不参与全局调试绘制（本地更具体的调试优先，README 语义：选区调试独立于全场景调试）
            var localDebugTransforms = _finalSweep ? CollectLocalDebugTransforms() : null;
            var localDebugOriginalLayers = localDebugTransforms != null
                ? SaveAndSetLayers(localDebugTransforms, SelLayer)
                : null;

            var selectedTransforms = _debugOnlySelected ? GetSelectedHierarchy() : null;
            // SEL 模式：从选中层级中剔除本地调试接管的物体（其显示由选区调试/沙盒负责）
            if (_debugOnlySelected && selectedTransforms != null && localDebugTransforms != null)
                RemoveAllByTransform(selectedTransforms, localDebugTransforms);
            var originalLayers = _debugOnlySelected ? SaveAndSetLayers(selectedTransforms, SelLayer) : null;
            var filterSettings = _debugOnlySelected
                ? new FilteringSettings(_finalSweep ? new RenderQueueRange(0, 5000) : RenderQueueRange.opaque, SelLayerMask)
                : (localDebugOriginalLayers != null
                    ? new FilteringSettings(new RenderQueueRange(0, 5000), ~SelLayerMask)
                    : _filterSettings);

            try
            {
                using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox")))
                {
                    _currentParams.ApplyToMaterial(_overrideMaterial);
                    _overrideMaterial.SetInt("_DebugMode", (int)_currentMode);

                    var drawSettings = CreateDrawingSettings(
                        _shaderTags,
                        ref renderingData,
                        // [fix v0.6.2] 最终全量绘制使用透明排序（从后往前）；遮挡正确性由 ZTest 保证
                        _finalSweep
                            ? SortingCriteria.CommonTransparent
                            : renderingData.cameraData.defaultOpaqueSortFlags
                    );
                    drawSettings.overrideMaterial = _overrideMaterial;
                    drawSettings.overrideMaterialPassIndex = 0;

                    context.DrawRenderers(
                        renderingData.cullResults,
                        ref drawSettings,
                        ref filterSettings
                    );
                }
            }
            finally
            {
                // [fix] 确保 Layer 始终恢复（即使 DrawRenderers 抛异常）
                if (originalLayers != null)
                    RestoreLayers(selectedTransforms, originalLayers);
                // [fix v0.6.x] 恢复本地调试对象的 Layer
                if (localDebugOriginalLayers != null)
                    RestoreLayers(localDebugTransforms, localDebugOriginalLayers);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        // ===== SEL 模式辅助方法 =====

        // [fix v0.4] SEL 模式 Layer 选择：优先使用空闲 Layer，避免与项目冲突
        // 从 31 向下查找第一个未被项目定义的 Layer
        private static int _selLayer = -1;
        private static int SelLayer
        {
            get
            {
                if (_selLayer >= 0) return _selLayer;
                // 从 31 向下查找未使用的 Layer（Layer 名称为空 = 未被项目定义）
                for (int i = 31; i >= 28; i--)
                {
                    if (string.IsNullOrEmpty(UnityEngine.LayerMask.LayerToName(i)))
                    {
                        _selLayer = i;
                        return _selLayer;
                    }
                }
                // 所有备选 Layer 都被占用，回退到 31（会触发警告）
                _selLayer = 31;
                return _selLayer;
            }
        }
        private static int SelLayerMask => 1 << SelLayer;

        /// <summary>获取选中物体及其所有子物体的 Transform 列表。</summary>
        private static List<Transform> GetSelectedHierarchy()
        {
            var selected = UnityEditor.Selection.activeTransform;
            if (selected == null) return null;
            var list = new List<Transform>();
            foreach (var t in selected.GetComponentsInChildren<Transform>(true))
                list.Add(t);
            return list;
        }

        /// <summary>保存原始 Layer 并设置为指定 Layer。返回原始 Layer 值数组。</summary>
        private static int[] SaveAndSetLayers(List<Transform> transforms, int newLayer)
        {
            if (transforms == null || transforms.Count == 0) return null;
            var originals = new int[transforms.Count];
            for (int i = 0; i < transforms.Count; i++)
            {
                if (transforms[i] == null) continue;
                originals[i] = transforms[i].gameObject.layer;
                transforms[i].gameObject.layer = newLayer;
            }
            return originals;
        }

        /// <summary>恢复物体的原始 Layer。</summary>
        private static void RestoreLayers(List<Transform> transforms, int[] originalLayers)
        {
            if (transforms == null || originalLayers == null) return;
            for (int i = 0; i < transforms.Count; i++)
            {
                if (transforms[i] == null) continue;
                transforms[i].gameObject.layer = originalLayers[i];
            }
        }

        /// <summary>
        /// [fix v0.6.x] 收集被本地调试系统（选区调试材质覆盖 ∪ 材质沙盒预览）接管材质的 Renderer 的 Transform。
        /// 全局调试最终全量 Pass 将其临时移入 SelLayer 并从过滤中排除（本地调试优先）。无则返回 null。
        /// </summary>
        private static List<Transform> CollectLocalDebugTransforms()
        {
            List<Renderer> localRenderers = null;

            var selMgr = ModelBoxSelectionManager.Instance;
            if (selMgr != null && selMgr.CurrentMode != SelectionDebugMode.None)
            {
                var overridden = selMgr.OverriddenRenderers;
                if (overridden.Count > 0)
                    localRenderers = new List<Renderer>(overridden);
            }

            if (MaterialDiffPanel.SandboxActive && MaterialDiffPanel.SandboxPreviewRenderers != null)
            {
                if (localRenderers == null) localRenderers = new List<Renderer>();
                foreach (var r in MaterialDiffPanel.SandboxPreviewRenderers)
                    if (r != null && !localRenderers.Contains(r))
                        localRenderers.Add(r);
            }

            if (localRenderers == null) return null;
            var transforms = new List<Transform>(localRenderers.Count);
            foreach (var r in localRenderers)
                if (r != null) transforms.Add(r.transform);
            return transforms;
        }

        /// <summary>从 list 中剔除 excludeList 包含的 Transform（引用相等）。</summary>
        private static void RemoveAllByTransform(List<Transform> list, List<Transform> excludeList)
        {
            if (list == null || excludeList == null || excludeList.Count == 0) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t == null) continue;
                foreach (var ex in excludeList)
                {
                    if (ex != null && ReferenceEquals(ex, t))
                    {
                        list.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// OpaqueTexture Capture Phase:
        /// 在 AfterRenderingOpaques+1 时机，将当前相机颜色（仅包含 opaque 物体渲染结果，
        /// 不包含 skybox 和 transparent 物体）拷贝到持久 RTHandle。
        ///
        /// 使用 URP Blitter.BlitCameraTexture 替代 cmd.Blit：
        ///   - Blitter 是 URP 标准 API（URP 内部 post-processing 使用同样的方法）
        ///   - 自动处理 DirectX/OpenGL/Metal 的坐标差异
        ///   - 使用 URP 内置全屏 Blit shader，确保正确覆盖整个纹理
        ///
        /// RTHandle 跨帧复用，尺寸变化时自动重新分配。
        /// </summary>
        private void ExecuteOpaqueCapture(ScriptableRenderContext context, RenderingData renderingData)
        {
#if UNITY_2022_1_OR_NEWER
            if (_cameraColorHandle == null) return;

            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;

            if (width <= 0 || height <= 0) return;

            // [fix v0.4] HDR 兼容性：根据相机 HDR 设置选择 RT 格式
            bool isHdr = renderingData.cameraData.isHdrEnabled;
            var hdrFormat = GraphicsFormat.R16G16B16A16_SFloat;
            var ldrFormat = GraphicsFormat.R8G8B8A8_SRGB;
            var captureFormat = isHdr ? hdrFormat : ldrFormat;

            // 按需分配/重新分配持久 RTHandle
            if (_opaqueCaptureHandle == null ||
                _opaqueCaptureHandle.rt == null ||
                _opaqueCaptureHandle.rt.width != width ||
                _opaqueCaptureHandle.rt.height != height)
            {
                _opaqueCaptureHandle?.Release();
                _opaqueCaptureHandle = RTHandles.Alloc(width, height,
                    colorFormat: captureFormat,
                    filterMode: FilterMode.Bilinear,
                    name: "_ModelBoxOpaqueCapture");
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_OpaqueCapture");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_OpaqueCapture")))
            {
                // 清除捕获 RT 为黑色，避免上一帧残留数据导致拖影
                CoreUtils.SetRenderTarget(cmd, _opaqueCaptureHandle, ClearFlag.Color, Color.black);

                // 使用 URP Blitter 进行全屏拷贝（SV_VertexID 三角形 + 平台感知 UV）
                Blitter.BlitCameraTexture(cmd, _cameraColorHandle, _opaqueCaptureHandle);

                // 将捕获的纹理设置为全局 shader 属性
                cmd.SetGlobalTexture("_CameraOpaqueTexture", _opaqueCaptureHandle);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
#endif
        }

        /// <summary>
        /// 分屏模式正常场景捕获：在 Debug Pass 之前复制相机颜色缓冲。
        /// 与 ExecuteOpaqueCapture 使用完全相同的模式（Blitter.BlitCameraTexture）。
        /// 此方法由 RendererFeature 在分屏模式启用时调用。
        /// </summary>
        public void ExecuteNormalSceneCapture(ScriptableRenderContext context, RenderingData renderingData)
        {
#if UNITY_2022_1_OR_NEWER
            if (_cameraColorHandle == null) return;

            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;

            if (width <= 0 || height <= 0) return;

            // [fix v0.4] HDR 兼容性
            bool isHdr = renderingData.cameraData.isHdrEnabled;
            var captureFormat = isHdr ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R8G8B8A8_SRGB;

            // 按需分配/重新分配
            if (_normalSceneCaptureHandle == null ||
                _normalSceneCaptureHandle.rt == null ||
                _normalSceneCaptureHandle.rt.width != width ||
                _normalSceneCaptureHandle.rt.height != height)
            {
                _normalSceneCaptureHandle?.Release();
                _normalSceneCaptureHandle = RTHandles.Alloc(width, height,
                    colorFormat: captureFormat,
                    filterMode: FilterMode.Bilinear,
                    name: "_ModelBoxNormalSceneCapture");
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_NormalCapture");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_NormalCapture")))
            {
                CoreUtils.SetRenderTarget(cmd, _normalSceneCaptureHandle, ClearFlag.Color, Color.black);
                Blitter.BlitCameraTexture(cmd, _cameraColorHandle, _normalSceneCaptureHandle);
                cmd.SetGlobalTexture("_NormalSceneTex", _normalSceneCaptureHandle);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
#endif
        }

        /// <summary>
        /// Overdraw Capture Phase:
        /// 在 AfterRenderingOpaques+1 时机，使用计数 shader (Additive Blend) 绘制所有 opaque 物体
        /// 到临时 RT。每次绘制叠加 1/16，最终纹理记录了每个像素被绘制的次数。
        ///
        /// 计数 shader 使用 overrideMaterial + Blend One One 实现累加：
        ///   - 每个 opaque 物体的每个像素被绘制时，输出 1/16
        ///   - 重叠区域的值会叠加，从而记录 overdraw 次数
        ///
        /// RTHandle 跨帧复用，尺寸变化时自动重新分配。
        /// </summary>
        private void ExecuteOverdrawCapture(ScriptableRenderContext context, RenderingData renderingData)
        {
#if UNITY_2022_1_OR_NEWER
            if (_overdrawCountMaterial == null) return;

            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;

            if (width <= 0 || height <= 0) return;

            // 按需分配/重新分配持久 RTHandle (UNorm 格式用于累加)
            if (_overdrawCaptureHandle == null ||
                _overdrawCaptureHandle.rt == null ||
                _overdrawCaptureHandle.rt.width != width ||
                _overdrawCaptureHandle.rt.height != height)
            {
                _overdrawCaptureHandle?.Release();
                _overdrawCaptureHandle = RTHandles.Alloc(width, height,
                    colorFormat: GraphicsFormat.R8G8B8A8_UNorm,
                    filterMode: FilterMode.Bilinear,
                    name: "_ModelBoxOverdrawCapture");
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_OverdrawCapture");

            // 第一段：设置渲染目标并清除
            // 必须在 DrawRenderers 之前 flush，因为 DrawRenderers 直接作用于 context，
            // 不会自动执行 cmd 中未 flush 的命令
            cmd.SetRenderTarget(_overdrawCaptureHandle);
            cmd.ClearRenderTarget(false, true, Color.black);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // DrawRenderers 现在正确渲染到 _overdrawCaptureHandle（而非相机颜色缓冲）
            var drawSettings = CreateDrawingSettings(
                _shaderTags,
                ref renderingData,
                renderingData.cameraData.defaultOpaqueSortFlags
            );
            drawSettings.overrideMaterial = _overdrawCountMaterial;
            drawSettings.overrideMaterialPassIndex = 0;

            context.DrawRenderers(
                renderingData.cullResults,
                ref drawSettings,
                ref _filterSettings
            );

            // 第二段：设置全局纹理供 Draw Pass 采样
            cmd.SetGlobalTexture("_OverdrawAccumTexture", _overdrawCaptureHandle);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
#endif
        }

        /// <summary>
        /// 清理持久 RTHandle（由 Feature 在 Dispose 时调用）。
        /// </summary>
        public void ReleaseCaptureTexture()
        {
            _opaqueCaptureHandle?.Release();
            _opaqueCaptureHandle = null;
            _overdrawCaptureHandle?.Release();
            _overdrawCaptureHandle = null;
            _normalSceneCaptureHandle?.Release();
            _normalSceneCaptureHandle = null;
        }

        /// <summary>
        /// Transparency Layers Capture Phase:
        /// 在 AfterRenderingTransparents+1 时机，使用计数 shader (Additive Blend) 绘制所有 transparent 物体
        /// 到临时 RT。每次绘制叠加 1/16，最终纹理记录了每个像素被透明物体绘制的次数。
        ///
        /// 与 ExecuteOverdrawCapture 结构相同，关键区别：
        /// - 使用 RenderQueueRange.transparent（只统计透明物体）
        /// - 渲染时机在 AfterRenderingTransparents（透明物体已全部渲染完毕）
        /// - 输出含义：透明物体层叠层数（Alpha Blend 叠加层数）
        ///
        /// 复用 _overdrawCountMaterial（同一个 Blend One One + 输出 1/16 的计数 shader）。
        /// </summary>
        private void ExecuteTransparencyCapture(ScriptableRenderContext context, RenderingData renderingData)
        {
#if UNITY_2022_1_OR_NEWER
            if (_overdrawCountMaterial == null) return;

            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;

            if (width <= 0 || height <= 0) return;

            // 复用 Overdraw 的 RTHandle（同一时刻只有一种模式活跃）
            if (_overdrawCaptureHandle == null ||
                _overdrawCaptureHandle.rt == null ||
                _overdrawCaptureHandle.rt.width != width ||
                _overdrawCaptureHandle.rt.height != height)
            {
                _overdrawCaptureHandle?.Release();
                _overdrawCaptureHandle = RTHandles.Alloc(width, height,
                    colorFormat: GraphicsFormat.R8G8B8A8_UNorm,
                    filterMode: FilterMode.Bilinear,
                    name: "_ModelBoxTransparencyCapture");
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_TransparencyCapture");

            // 第一段：设置渲染目标并清除
            cmd.SetRenderTarget(_overdrawCaptureHandle);
            cmd.ClearRenderTarget(false, true, Color.black);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // DrawRenderers：只绘制透明队列的物体（使用透明排序：从后往前）
            var transparentFilter = new FilteringSettings(RenderQueueRange.transparent);
            var drawSettings = CreateDrawingSettings(
                _shaderTags,
                ref renderingData,
                SortingCriteria.CommonTransparent
            );
            drawSettings.overrideMaterial = _overdrawCountMaterial;
            drawSettings.overrideMaterialPassIndex = 0;

            context.DrawRenderers(
                renderingData.cullResults,
                ref drawSettings,
                ref transparentFilter
            );

            // 第二段：设置全局纹理供 Draw Pass 采样
            cmd.SetGlobalTexture("_OverdrawAccumTexture", _overdrawCaptureHandle);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
#endif
        }
    }

    /// <summary>
    /// 调试场景捕获 Pass（分屏模式专用）。
    /// 在 Debug Pass 渲染到相机颜色缓冲后，复制调试输出到持久 RT，
    /// 作为合成 Pass 的右半部分（_DebugSceneTex）。
    /// </summary>
    public class ModelBoxDebugSceneCapturePass : ScriptableRenderPass
    {
        private RTHandle _debugSceneCaptureHandle;

        public RTHandle DebugSceneCaptureTexture => _debugSceneCaptureHandle;

#if UNITY_2022_1_OR_NEWER
        private RTHandle _cameraColorHandle;

        public void SetCameraColor(RTHandle colorTarget)
        {
            _cameraColorHandle = colorTarget;
        }
#endif

        public void ExecuteCapture(ScriptableRenderContext context, RenderingData renderingData)
        {
#if UNITY_2022_1_OR_NEWER
            if (_cameraColorHandle == null) return;

            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;

            if (width <= 0 || height <= 0) return;

            // [fix v0.4] HDR 兼容性
            bool isHdr = renderingData.cameraData.isHdrEnabled;
            var captureFormat = isHdr ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R8G8B8A8_SRGB;

            if (_debugSceneCaptureHandle == null ||
                _debugSceneCaptureHandle.rt == null ||
                _debugSceneCaptureHandle.rt.width != width ||
                _debugSceneCaptureHandle.rt.height != height)
            {
                _debugSceneCaptureHandle?.Release();
                _debugSceneCaptureHandle = RTHandles.Alloc(width, height,
                    colorFormat: captureFormat,
                    filterMode: FilterMode.Bilinear,
                    name: "_ModelBoxDebugSceneCapture");
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_DebugCapture");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_DebugCapture")))
            {
                CoreUtils.SetRenderTarget(cmd, _debugSceneCaptureHandle, ClearFlag.Color, Color.black);
                Blitter.BlitCameraTexture(cmd, _cameraColorHandle, _debugSceneCaptureHandle);
                cmd.SetGlobalTexture("_DebugSceneTex", _debugSceneCaptureHandle);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
#endif
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            ExecuteCapture(context, renderingData);
        }

        public void ReleaseCaptureTexture()
        {
            _debugSceneCaptureHandle?.Release();
            _debugSceneCaptureHandle = null;
        }
    }

    /// <summary>
    /// 正常场景捕获 Pass（分屏模式专用）。
    /// 在 Debug Pass 之前运行，复制当前相机颜色缓冲到持久 RT。
    /// 使用委托回调调用 ModelBoxGeometryPass.ExecuteNormalSceneCapture。
    /// </summary>
    public class ModelBoxNormalSceneCapturePass : ScriptableRenderPass
    {
        private System.Action<ScriptableRenderContext, RenderingData> _captureAction;

        public void SetCaptureAction(System.Action<ScriptableRenderContext, RenderingData> action)
        {
            _captureAction = action;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            _captureAction?.Invoke(context, renderingData);
        }
    }

    /// <summary>
    /// OpaqueTexture Draw Pass：在 AfterRenderingTransparents 绘制全屏可视化。
    /// 采样 Capture Phase 捕获的临时纹理（仅 opaque 物体），通过 DrawProcedural 全屏三角形输出。
    /// 必须在 FinalBlit 之前执行（AfterRenderingTransparents），
    /// 因为 AfterRendering 在 FinalBlit 之后，DrawProcedural 的输出不会出现在最终画面中。
    /// </summary>
    public class ModelBoxOpaqueTextureDrawPass : ScriptableRenderPass
    {
        private readonly Material _opaqueBlitMaterial;
        private ModelBoxParameters _currentParams;

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

        public ModelBoxOpaqueTextureDrawPass(Material opaqueBlitMaterial)
        {
            _opaqueBlitMaterial = opaqueBlitMaterial;
        }

        public void Configure(ModelBoxParameters parameters)
        {
            _currentParams = parameters;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_opaqueBlitMaterial == null)
            {
                Debug.LogWarning("[ModelBox] OpaqueTexture Draw Pass: material 为 null，跳过绘制。" +
                    "请检查 Hidden/ModelBox/OpaqueTextureBlit shader 是否存在且编译正确。");
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_OpaqueTexture_Draw");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_OpaqueTexture_Draw")))
            {
                _currentParams.ApplyToMaterial(_opaqueBlitMaterial);

                // SV_VertexID 全屏三角形，采样 Capture Phase 设置的全局纹理 _CameraOpaqueTexture
                cmd.DrawProcedural(Matrix4x4.identity, _opaqueBlitMaterial, 0,
                    MeshTopology.Triangles, 3, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    /// <summary>
    /// Overdraw Draw Pass：在 AfterRenderingTransparents 绘制全屏 Overdraw 热力图。
    /// 采样 Capture Phase 计数的 _OverdrawAccumTexture，通过 DrawProcedural 全屏三角形输出热力图。
    /// 与 OpaqueTexture Draw Pass 一样，必须在 FinalBlit 之前执行。
    /// </summary>
    public class ModelBoxOverdrawDrawPass : ScriptableRenderPass
    {
        private readonly Material _overdrawHeatmapMaterial;
        private float _overdrawMaxCount = 8.0f;

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

        public ModelBoxOverdrawDrawPass(Material overdrawHeatmapMaterial)
        {
            _overdrawHeatmapMaterial = overdrawHeatmapMaterial;
        }

        public void Configure(float maxCount)
        {
            _overdrawMaxCount = maxCount;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_overdrawHeatmapMaterial == null)
            {
                Debug.LogWarning("[ModelBox] Overdraw Draw Pass: material 为 null，跳过绘制。" +
                    "请检查 Hidden/ModelBox/OverdrawHeatmap shader 是否存在且编译正确。");
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_Overdraw_Draw");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_Overdraw_Draw")))
            {
                _overdrawHeatmapMaterial.SetFloat("_OverdrawMaxCount", _overdrawMaxCount);

                // SV_VertexID 全屏三角形，采样 Capture Phase 设置的全局纹理 _OverdrawAccumTexture
                cmd.DrawProcedural(Matrix4x4.identity, _overdrawHeatmapMaterial, 0,
                    MeshTopology.Triangles, 3, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    /// <summary>
    /// ShadowMap Pass：在 AfterRenderingTransparents 绘制全屏阴影可视化。
    /// 从 _CameraDepthTexture 重建世界坐标，通过级联阴影矩阵采样 _MainLightShadowmapTexture，
    /// 输出阴影衰减或级联着色。与 ScreenNormal Pass 结构完全一致。
    /// </summary>
    public class ModelBoxShadowMapPass : ScriptableRenderPass
    {
        private Material _shadowMapBlitMaterial;
        private ModelBoxParameters _currentParams;
        private int _viewMode; // 0=attenuation, 1=cascade coloring

#if UNITY_2022_1_OR_NEWER
        private RTHandle _cameraColorHandle;
        private RTHandle _cameraDepthHandle;

        public void SetTargets(RTHandle colorTarget, RTHandle depthTarget)
        {
            _cameraColorHandle = colorTarget;
            _cameraDepthHandle = depthTarget;
            // [fix] 仅绑定颜色目标：本 Pass 采样 _CameraDepthTexture，MSAA 关闭时它与
            // cameraDepthTargetHandle 为同一资源，同时绑定为深度附件构成 DSV/SRV 反馈环
            //（D3D11 强制解绑 SRV → 采样返回 0/未定义）。ZWrite Off + ZTest Always 无需深度。
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

        public ModelBoxShadowMapPass()
        {
            // 深度纹理用于重建世界坐标；阴影数据（_MainLightShadowmapTexture 等）由 URP
            // 主光源 ShadowCaster Pass 自动设置为全局属性，无需通过 ConfigureInput 请求
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void SetMaterial(Material material)
        {
            _shadowMapBlitMaterial = material;
        }

        public void Configure(ModelBoxParameters parameters, int viewMode = 0)
        {
            _currentParams = parameters;
            _viewMode = viewMode;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (_shadowMapBlitMaterial == null)
            {
                Debug.LogWarning("[ModelBox] ShadowMap Draw Pass: material 为 null，跳过绘制。" +
                    "请检查 Hidden/ModelBox/ShadowMapBlit shader 是否存在且编译正确。");
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("ModelBox_ShadowMap_Draw");

            using (new ProfilingScope(cmd, new ProfilingSampler("ModelBox_ShadowMap_Draw")))
            {
                _currentParams.ApplyToMaterial(_shadowMapBlitMaterial);
                _shadowMapBlitMaterial.SetInt("_ViewMode", _viewMode);

                // SV_VertexID 全屏三角形，shader 内部采样 _CameraDepthTexture 和 _MainLightShadowmapTexture
                cmd.DrawProcedural(Matrix4x4.identity, _shadowMapBlitMaterial, 0,
                    MeshTopology.Triangles, 3, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
