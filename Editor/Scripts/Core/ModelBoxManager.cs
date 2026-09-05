using System;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 工具核心单例。管理调试模式状态、参数、渲染器生命周期。
    /// 通过 [InitializeOnLoad] 在域重载时自动初始化。
    /// </summary>
    [InitializeOnLoad]
    public sealed class ModelBoxManager
    {
        // ---- Singleton ----
        private static ModelBoxManager _instance;
        public static ModelBoxManager Instance => _instance;

        // ---- 状态 ----
        public DebugViewMode CurrentMode { get; private set; } = DebugViewMode.None;
        public ModelBoxParameters CurrentParameters { get; private set; } = ModelBoxParameters.Default;
        public bool IsEnabled { get; private set; } = false;
        public bool DebugOnlySelected { get; private set; } = false;
        public bool SplitScreenEnabled { get; private set; } = false;
        public float SplitScreenPosition { get; private set; } = 0.5f;
        public bool FreezeLeftSnapshot { get; private set; } = false;

        // [feat] 快照 A/B 对比模式
        // 0=正常分屏, 1=快照A vs 实时, 2=快照A vs 快照B, 3=快照B vs 实时
        public int SnapshotMode { get; private set; } = 0;
        // 触发标记：下一帧捕获当前画面到快照
        public bool CaptureSnapshotA { get; set; } = false;
        public bool CaptureSnapshotB { get; set; } = false;

        // LOGIC-2 修正：保存切换前的模式，Toggle 时恢复
        private DebugViewMode _previousMode = DebugViewMode.None;

        // [fix H8] 保存进入 Wireframe 模式前的 OverlayFlags，切换离开时恢复
        private MeshOverlayFlags _overlayFlagsBeforeWireframe;
        private bool _hasSavedOverlayFlags = false;

        // ---- 事件 ----
        public event Action<DebugViewMode> OnModeChanged;
        public event Action<ModelBoxParameters> OnParametersChanged;
        public event Action<bool> OnEnabledChanged;

        // ---- 管线渲染器 ----
        private IModelBoxRenderer _renderer;

        // ---- 构造 ----
        static ModelBoxManager()
        {
            // 静态构造阶段 AssetDatabase 可能未就绪，跳过设置资产创建
            ModelBoxSettings.SkipAssetCreation();
            _instance = new ModelBoxManager();
            _instance.Initialize();
        }

        private ModelBoxManager() { }

        private void Initialize()
        {
            var pipelineType = PipelineDetector.Detect();
            _renderer = CreateRenderer(pipelineType);

            if (_renderer.IsAvailable)
                _renderer.Initialize();

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            Debug.Log($"[ModelBox] 初始化完成，管线: {pipelineType}，渲染器可用: {_renderer.IsAvailable}");

            // 恢复上次使用的调试模式（域重载前如果是激活状态）
            // 使用 delayCall 避免 AssetDatabase 早期初始化问题
            EditorApplication.delayCall += () =>
            {
                try
                {
                    // AssetDatabase 已就绪，恢复资产创建能力
                    ModelBoxSettings.AllowAssetCreation();
                    var settings = ModelBoxSettings.GetOrCreate();
                    if (settings != null && settings.LastMode != DebugViewMode.None)
                    {
                        _previousMode = settings.LastMode;
                        SetDebugMode(settings.LastMode);
                        SetParameters(settings.LastParameters);
                    }
                    // [fix] 恢复分屏状态（域重载前如果开启过分屏）
                    if (settings != null && settings.SplitScreenEnabled)
                    {
                        SplitScreenEnabled = true;
                        SplitScreenPosition = settings.SplitScreenPosition;
                        FreezeLeftSnapshot = settings.FreezeLeftSnapshot;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ModelBox] 恢复调试模式失败: {e}");
                }
            };
        }

        // ---- 公共 API ----

        /// <summary>
        /// 设置调试模式。触发 OnModeChanged 事件和 SceneView 刷新。
        /// </summary>
        public void SetDebugMode(DebugViewMode mode)
        {
            if (CurrentMode == mode) return;

            // 记录 Undo（用于 Ctrl+Z 回退模式切换）
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null)
            {
                ModelBoxSettings._pendingModelBoxUndo++; // [fix C1] 代际计数器
                Undo.RecordObject(settings, "modelBox Mode");
            }

            CurrentMode = mode;
            IsEnabled = (mode != DebugViewMode.None);

            // S2: 切换模式时，如果新模式不支持 ColorMap，自动重置为 Raw（0）
            if (!DebugParameterControls.ShouldShowColorMapStatic(mode) && CurrentParameters.ColorMapMode != 0)
            {
                var resetParams = CurrentParameters;
                resetParams.ColorMapMode = 0;
                CurrentParameters = resetParams;
                _renderer?.SetDebugMode(mode, CurrentParameters);
                OnParametersChanged?.Invoke(CurrentParameters);
            }

            // Wireframe 模式自动启用选区线框叠加；切换到其他模式时自动清理
            // [fix H8] 进入时保存之前的 OverlayFlags，离开时恢复
            var selManager = ModelBoxSelectionManager.Instance;
            if (selManager != null)
            {
                if (mode == DebugViewMode.Wireframe)
                {
                    // 保存当前 flags（仅首次进入时保存，防止模式内重复切换覆盖原始值）
                    if (!_hasSavedOverlayFlags)
                    {
                        _overlayFlagsBeforeWireframe = selManager.OverlayFlags;
                        _hasSavedOverlayFlags = true;
                    }
                    selManager.SetOverlayFlags(selManager.OverlayFlags | MeshOverlayFlags.Wireframe);
                }
                else
                {
                    // 离开 Wireframe 模式：恢复之前的 flags（仅清除自动添加的 Wireframe 位）
                    if (_hasSavedOverlayFlags)
                    {
                        // [fix v0.6.x] 保留用户在 Wireframe 模式期间手动开启的其他叠加位 —
                        // 旧逻辑整体回滚到进入前快照，会把用户刚开的顶点/法线等叠加静默丢弃；
                        // 仅当 Wireframe 位是本次自动添加（进入前未开启）时才移除它
                        var current = selManager.OverlayFlags;
                        var restored = (_overlayFlagsBeforeWireframe & MeshOverlayFlags.Wireframe) != 0
                            ? current
                            : current & ~MeshOverlayFlags.Wireframe;
                        selManager.SetOverlayFlags(restored);
                        _hasSavedOverlayFlags = false;
                    }
                    else
                    {
                        selManager.SetOverlayFlags(selManager.OverlayFlags & ~MeshOverlayFlags.Wireframe);
                    }
                }
            }

            // OpaqueTexture 模式需要 _CameraOpaqueTexture，自动启用 URP Opaque Texture
            if (mode == DebugViewMode.OpaqueTexture)
                ModelBoxURPSetup.EnsureOpaqueTexture();

            _renderer?.SetDebugMode(mode, CurrentParameters);
            OnModeChanged?.Invoke(mode);

            // 持久化模式记忆
            if (settings != null)
            {
                settings.LastMode = mode;
                settings.LastParameters = CurrentParameters;
                settings.Save();
            }

            // 使用 delayCall 避免 duringSceneGui 递归渲染
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 从 Undo 系统调用的模式恢复。跳过 Undo.RecordObject 和 LastMode 写入，防止递归。
        /// </summary>
        internal void SetDebugModeFromUndo(DebugViewMode mode, ModelBoxParameters parameters)
        {
            CurrentMode = mode;
            IsEnabled = (mode != DebugViewMode.None);
            CurrentParameters = parameters;
            _renderer?.SetDebugMode(mode, parameters);
            OnModeChanged?.Invoke(mode);
            OnParametersChanged?.Invoke(parameters);
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 更新调试参数（Scale/Offset/Gamma/DepthRange）。
        /// </summary>
        public void SetParameters(ModelBoxParameters parameters)
        {
            // A2 修复：记录 Undo
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null)
            {
                ModelBoxSettings._pendingModelBoxUndo++; // [fix C1] 代际计数器
                Undo.RecordObject(settings, "modelBox Parameters");
            }

            CurrentParameters = parameters;
            if (CurrentMode != DebugViewMode.None)
                _renderer?.SetDebugMode(CurrentMode, parameters);
            OnParametersChanged?.Invoke(parameters);

            // 持久化参数变更
            if (settings != null)
            {
                settings.LastParameters = parameters;
                settings.Save();
            }
        }

        /// <summary>
        /// 启用/禁用调试渲染。
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (IsEnabled == enabled) return;
            IsEnabled = enabled;

            if (!enabled)
                SetDebugMode(DebugViewMode.None);

            _renderer?.SetEnabled(enabled);
            OnEnabledChanged?.Invoke(enabled);
        }

        /// <summary>
        /// 切换调试开/关（快捷键用）。
        /// LOGIC-2 修正：关闭时保存当前模式，开启时恢复。
        /// </summary>
        public void ToggleEnabled()
        {
            if (IsEnabled)
            {
                // 关闭：保存当前模式
                _previousMode = CurrentMode;
                SetEnabled(false);
            }
            else
            {
                // 开启：恢复之前的模式
                SetEnabled(true);
                if (_previousMode != DebugViewMode.None)
                    SetDebugMode(_previousMode);
            }
        }

        /// <summary>
        /// 切换仅选中物体调试。
        /// </summary>
        public void ToggleDebugOnlySelected()
        {
            DebugOnlySelected = !DebugOnlySelected;
            // [safety] Layer 31 已被项目占用时发出一次性警告
            if (DebugOnlySelected)
            {
                string layerName = UnityEngine.LayerMask.LayerToName(31);
                if (!string.IsNullOrEmpty(layerName))
                    Debug.LogWarning($"[ModelBox] SEL 模式使用 Layer 31 作为临时过滤层，但该层已被项目定义为 \"{layerName}\"。渲染期间该层会被临时覆盖，结束后恢复。");
            }
            // 使用 delayCall 避免 duringSceneGui 递归渲染
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>切换分屏对比模式（持久化到 Settings）。</summary>
        public void SetSplitScreen(bool enabled)
        {
            if (SplitScreenEnabled == enabled) return;
            SplitScreenEnabled = enabled;
            // [fix] 关闭分屏时同步重置冻结与快照对比状态，避免再次开启时引用脏 RT
            if (!enabled)
            {
                if (FreezeLeftSnapshot)
                    SetFreezeLeftSnapshot(false);
                // [fix v0.6.x] 分屏关闭时快照 RT 由 Feature 释放（ReleaseSnapshotRTs），
                // SnapshotMode 必须同步归零 —— 否则重开分屏后 SA/SB 按钮高亮"有快照"但实际点击走保存流程
                if (SnapshotMode != 0)
                    SnapshotMode = 0;
            }
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null) { settings.SplitScreenEnabled = enabled; settings.Save(); }
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>设置分屏分割位置（0-1，持久化到 Settings）。</summary>
        public void SetSplitPosition(float position)
        {
            SplitScreenPosition = Mathf.Clamp01(position);
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null) { settings.SplitScreenPosition = SplitScreenPosition; settings.Save(); }
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 切换冻结左侧快照。冻结时停止更新正常场景 RT，保留最后一帧内容。
        /// 仅在分屏模式下有效。
        /// </summary>
        public void SetFreezeLeftSnapshot(bool frozen)
        {
            if (FreezeLeftSnapshot == frozen) return;
            FreezeLeftSnapshot = frozen;
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null) { settings.FreezeLeftSnapshot = frozen; settings.Save(); }
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>触发捕获快照 A（下一帧生效）。</summary>
        public void SaveSnapshotA()
        {
            CaptureSnapshotA = true;
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>触发捕获快照 B（下一帧生效）。</summary>
        public void SaveSnapshotB()
        {
            CaptureSnapshotB = true;
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>循环快照对比模式：0→1→2→3→0。</summary>
        public void CycleSnapshotMode()
        {
            SnapshotMode = (SnapshotMode + 1) % 4;
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>重置快照模式。</summary>
        public void ResetSnapshotMode()
        {
            SnapshotMode = 0;
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 获取快照模式描述文本。
        /// </summary>
        public string GetSnapshotModeLabel()
        {
            switch (SnapshotMode)
            {
                case 1: return "A vs 实时";
                case 2: return "A vs B";
                case 3: return "B vs 实时";
                default: return "快照";
            }
        }
        /// ScreenSpace 模式通过全屏 Blit 从深度缓冲重建，不受顶点动画影响。
        /// </summary>
        public DebugViewCategory GetModeCategory(DebugViewMode mode)
        {
            if (mode == DebugViewMode.None)
                return DebugViewCategory.None;

            // 屏幕空间可重建模式：从深度缓冲重建，走全屏 Blit 路径
            if (mode == DebugViewMode.Depth || mode == DebugViewMode.ScreenNormal || mode == DebugViewMode.RayMarch)
                return DebugViewCategory.ScreenSpace;

            // 其余模式使用 Geometry overrideMaterial
            return DebugViewCategory.Geometry;
        }

        /// <summary>
        /// 确保管线相关设置正确（Feature 已安装等）。
        /// </summary>
        public bool EnsurePipelineSetup()
        {
            return _renderer?.EnsureSetup() ?? false;
        }

        // ---- 管线渲染器工厂 ----

        private IModelBoxRenderer CreateRenderer(RenderPipelineType type)
        {
            switch (type)
            {
                case RenderPipelineType.URP:
                    return new URPModelBoxRenderer();
                default:
                    return new UnsupportedModelBoxRenderer();
            }
        }

        // ---- 生命周期 ----

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                // Play Mode 切换后重建渲染器
                if (_renderer != null && _renderer.IsAvailable)
                    _renderer.Initialize();
            }
        }

        private void OnBeforeAssemblyReload()
        {
            // [fix] 取消静态事件订阅，防止域重载后 handler 叠加泄漏
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;

            // 域重载前清理渲染器资源
            _renderer?.Shutdown();
        }

        private void OnAfterAssemblyReload()
        {
            // 域重载后单例由静态构造函数重新创建
        }
    }

    // ---- URP 渲染器实现 ----

    /// <summary>
    /// URP 管线的 IModelBoxRenderer 实现。
    /// 委托给 ModelBoxURPSetup 管理 Feature 安装。
    /// </summary>
    internal class URPModelBoxRenderer : IModelBoxRenderer
    {
        public bool IsAvailable => PipelineDetector.Detect() == RenderPipelineType.URP;

        public void Initialize()
        {
            // 确保 Feature 已安装（域重载后可能丢失，自动修复）
            if (!ModelBoxURPSetup.IsSetupComplete())
            {
                Debug.Log("[ModelBox] URP RendererFeature 丢失，正在自动修复...");
                ModelBoxURPSetup.AddFeatureToActiveRenderer();
            }
        }

        public void Shutdown()
        {
            // Feature 由 ScriptableRendererFeature 自身管理生命周期
        }

        public void SetDebugMode(DebugViewMode mode, ModelBoxParameters parameters)
        {
            // Mode 变更由 ModelBoxRendererFeature.AddRenderPasses() 被动读取
            // 使用 delayCall 避免递归渲染
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        public void SetEnabled(bool enabled)
        {
            if (!enabled)
                EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        public bool EnsureSetup()
        {
            if (ModelBoxURPSetup.IsSetupComplete())
                return true;

            return ModelBoxURPSetup.AddFeatureToActiveRenderer();
        }
    }
}
