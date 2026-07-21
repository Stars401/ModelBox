using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 持久化用户偏好设置（ScriptableObject）。
    /// 保存最后一次使用的模式、参数预设等。
    /// </summary>
    public class ModelBoxSettings : ScriptableObject
    {
        private const string SettingsAssetDir = "Assets/ModelBox";
        private const string SettingsAssetPath = "Assets/ModelBox/ModelBoxSettings.asset";

        [Header("Last Used")]
        public DebugViewMode LastMode = DebugViewMode.None;
        public ModelBoxParameters LastParameters = ModelBoxParameters.Default;

        // [fix C1] 代际计数器替代 bool，防止快速模式切换覆盖 + 不相关 Undo 误消费
        internal static int _pendingModelBoxUndo;

        [Header("Preferences")]
        public bool AutoEnableDepthTexture = true;
        public bool ShowSceneViewOverlay = true;
        public bool ShowPixelInspector = true;
        public bool ShowPixelBar = true;
        public bool PixelBarStandalone = false; // [feat] 独立模式：不开启调试模式也能使用 PixelBar
        public bool ShowPerformanceHUD = true;
        // [feat] HUD 逐项配置
        public bool HudShowFPS = true;
        public bool HudShowFPSBeta = true; // [beta] FPS 显示(BETA — Editor 中帧率计算可能不精确)
        public bool HudShowDC = true;
        public bool HudShowTri = true;
        public bool HudShowVert = true;
        public bool HudShowMem = true;
        public bool HudShowGC = true;
        public bool OverlayDepthTest = true; // 网格叠加深度测试（关闭=透视显示所有点）

        [Header("Split Screen")]
        public bool SplitScreenEnabled = false;
        public float SplitScreenPosition = 0.5f;
        public bool FreezeLeftSnapshot = false;

        [Header("Toolbar")]
        public float ToolbarX = 8f;  // 工具栏 X 位置（可拖拽）
        public float ToolbarY = 8f;  // 工具栏 Y 位置（可拖拽）

        [Header("Selection Debug")]
        public SelectionDebugMode LastSelectionMode = SelectionDebugMode.None;
        public MeshOverlayFlags MeshOverlayState = MeshOverlayFlags.None;

        [Header("Screenshot")]
        public string LastScreenshotDir = "";

        [Header("Pixel Bar")]
        public bool ShowCustomProp = false;
        public string CustomPropName = "_BaseColor";

        private static ModelBoxSettings _instance;
        private static bool _skipAssetCreation; // 静态构造阶段跳过资产创建

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnUndoRedo()
        {
            // [fix C1] 代际计数器：仅当有待处理的 ModelBox Undo 时才恢复模式
            // [fix v0.4] Q3-4: Undo/Redo 边界加固 — 防止下溢
            if (_pendingModelBoxUndo <= 0) return;
            _pendingModelBoxUndo--;
            // [fix v0.4] 二次保护：确保计数器不会因异常变为负数
            if (_pendingModelBoxUndo < 0) _pendingModelBoxUndo = 0;

            var manager = ModelBoxManager.Instance;
            if (manager != null && manager.CurrentMode != LastMode)
            {
                manager.SetDebugModeFromUndo(LastMode, LastParameters);
            }
        }

        /// <summary>
        /// 获取或创建设置实例。对 AssetDatabase 不可用（早期初始化、包首次导入）具有弹性。
        /// 失败时返回纯内存实例（不持久化但功能正常）。
        /// </summary>
        public static ModelBoxSettings GetOrCreate()
        {
            if (_instance != null) return _instance;

            // 尝试从磁盘加载
            try
            {
                _instance = AssetDatabase.LoadAssetAtPath<ModelBoxSettings>(SettingsAssetPath);
            }
            catch
            {
                // AssetDatabase 可能未就绪
            }

            if (_instance == null && !_skipAssetCreation)
            {
                try
                {
                    _instance = CreateInstance<ModelBoxSettings>();

                    if (!AssetDatabase.IsValidFolder(SettingsAssetDir))
                    {
                        if (!AssetDatabase.IsValidFolder("Assets"))
                            throw new System.Exception("Assets folder not available");
                        AssetDatabase.CreateFolder("Assets", "ModelBox");
                    }

                    AssetDatabase.CreateAsset(_instance, SettingsAssetPath);
                    AssetDatabase.SaveAssets();
                }
                catch (System.Exception e)
                {
                    // 首次导入/早期初始化时失败：回退到内存模式
                    // 不设置 _skipAssetCreation，后续调用仍会重试
                    Debug.LogWarning($"[ModelBox] 设置文件创建失败（{e.Message}），使用内存模式。");
                    if (_instance == null)
                        _instance = CreateInstance<ModelBoxSettings>();
                }
            }

            // 兜底：纯内存实例
            if (_instance == null)
                _instance = CreateInstance<ModelBoxSettings>();

            return _instance;
        }

        /// <summary>
        /// 标记跳过资产创建（用于静态构造阶段，避免阻塞初始化）。
        /// 调用此方法后 GetOrCreate 将只返回内存实例，不尝试创建磁盘资产。
        /// </summary>
        public static void SkipAssetCreation()
        {
            _skipAssetCreation = true;
        }

        /// <summary>
        /// 恢复资产创建能力。在 delayCall 阶段 AssetDatabase 就绪后调用。
        /// </summary>
        public static void AllowAssetCreation()
        {
            _skipAssetCreation = false;
        }

        public void Save()
        {
            if (this == null) return;
            try
            {
                EditorUtility.SetDirty(this);
            }
            catch
            {
                // 域重载期间可能失败
            }
        }
    }
}
