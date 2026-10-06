using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 选区调试管理器。对选中物体应用材质覆盖、网格叠加。
    /// 关键：在 duringSceneGui 中使用 delayCall 调用 RepaintAll，避免递归渲染。
    /// </summary>
    [InitializeOnLoad]
    public sealed class ModelBoxSelectionManager
    {
        private static ModelBoxSelectionManager _instance;
        public static ModelBoxSelectionManager Instance => _instance;

        public SelectionDebugMode CurrentMode { get; private set; } = SelectionDebugMode.None;
        public MeshOverlayFlags OverlayFlags { get; private set; } = MeshOverlayFlags.None;

        private readonly Dictionary<Renderer, Material[]> _originalMaterials = new Dictionary<Renderer, Material[]>();
        private Material _textureChannelMat;
        private Material _checkerboardMat;
        private Material _uniformColorMat;
        private Material _vertexColorMat; // [feat] 顶点颜色可视化材质

        private string _propertyColorInfo = "";
        private Color _propertyColor = Color.magenta;
        public Color PropertyColor
        {
            get => _propertyColor;
            set => _propertyColor = value;
        }

        // 选区调试参数（由 UI 层设置）
        public Texture CustomTexture { get; set; }
        public int ChannelMask { get; set; }
        public float CheckerGridSize { get; set; } = 10f;
        public Color CheckerColorA { get; set; } = new Color(0.9f, 0.9f, 0.9f, 1f);
        public Color CheckerColorB { get; set; } = new Color(0.2f, 0.2f, 0.2f, 1f);

        // [feat] 顶点颜色通道选择：0=RGB, 1=R, 2=G, 3=B, 4=A（灰度显示）
        public int VertexColorChannel { get; set; } = 0;

        // 贴图通道参数
        public Vector2 TextureScale { get; set; } = Vector2.one;
        public Vector2 TextureOffset { get; set; } = Vector2.zero;
        public int UVChannel { get; set; } = 0; // 0=UV0, 1=UV1, 2=UV2, 3=UV3
        public bool WorldSpaceUV { get; set; } = false;
        public float WorldUVScale { get; set; } = 0.01f;

        // [feat] 单色（灰度）显示通道值，避免红/蓝色调影响数值观察
        public bool MonoDisplay { get; set; } = false;

        // 贴图亮度钳制参数（Lightness Map / Ramp Mask 调试）
        public float ClampMin { get; set; } = 0f;
        public float ClampMax { get; set; } = 1f;

        // 网格叠加样式设置
        public Color WireframeColor { get; set; } = new Color(0.4f, 0.7f, 1f, 0.8f);
        public Color VertexColor { get; set; } = new Color(1f, 1f, 0.2f, 0.9f);
        public Color NormalColor { get; set; } = new Color(0.3f, 0.6f, 1f, 0.7f);
        public float VertexSize { get; set; } = 0.03f;
        public float NormalLength { get; set; } = 0.1f;
        public float NormalWidth { get; set; } = 1f; // [perf v0.6] 默认细线走烘焙线网格快速路径；>1 为逐线粗线（高面数明显变慢）
        public bool VertexScaleIndependent { get; set; } = true;

        // [fix] GPU 加速开关默认关闭：烘焙线网格路径在部分环境下输出仍不正确，
        // 默认走 Legacy Handles 路径保证显示正确；用户可按需手动开启
        public bool UseGPURendering { get; set; } = false;

        // 切线叠加
        public Color TangentColor { get; set; } = new Color(1f, 1f, 0.2f, 0.8f);
        public float TangentLength { get; set; } = 0.1f;
        public float TangentWidth { get; set; } = 1f; // [perf v0.6] 同法线宽度

        // 包围盒叠加
        public Color BoundsColor { get; set; } = new Color(0f, 1f, 0.5f, 0.6f);
        public float BoundsWidth { get; set; } = 2f;

        // [feat v0.6] 模型局部坐标轴叠加：轴长系数（实际轴长 = 合并包围盒对角线 × 此系数）
        public float LocalAxesLength { get; set; } = 0.5f;

        // [feat] 骨骼权重可视化状态（由 BonePanel 写入）
        public BoneWeightDisplayMode BoneWeightMode { get; set; } = BoneWeightDisplayMode.Off;
        public float[] BoneVertexWeights { get; set; }
        public float BoneWeightThreshold { get; set; } = 0.1f;
        public float BoneWeightOpacity { get; set; } = 1.0f;
        public SkinnedMeshRenderer BoneWeightTargetSMR { get; set; } // 权重来源 SMR

        // [feat] 骨骼 gizmo 渲染状态（由 BonePanel 写入）
        public int SelectedBoneIndex { get; set; } = -1;
        public bool ShowBoneGizmos { get; set; } = false;

        // [perf] 骨骼 Transform 集合缓存（避免每帧分配）
        private HashSet<Transform> _boneSet;

        // [feat] SceneView 点击选骨骼后的回调（BonePanel 订阅）
        public static event System.Action<int> OnBoneSelectedInScene;

        static ModelBoxSelectionManager()
        {
            _instance = new ModelBoxSelectionManager();
            _instance.Initialize();
        }

        private ModelBoxSelectionManager() { }

        private void Initialize()
        {
            // [fix] 先取消旧订阅再注册（防御性编程，防止异常路径导致 handler 叠加）
            UnsubscribeEvents();
            SubscribeEvents();

            // [fix v0.4] 恢复域重载前的选区调试状态
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var settings = ModelBoxSettings.GetOrCreate();
                    if (settings != null)
                    {
                        // 恢复网格叠加标志（不需要材质替换，直接设置即可）
                        if (settings.MeshOverlayState != MeshOverlayFlags.None)
                            OverlayFlags = settings.MeshOverlayState;

                        // 恢复选区调试模式（需要材质替换，通过 SetMode 触发）
                        if (settings.LastSelectionMode != SelectionDebugMode.None)
                        {
                            var selected = Selection.activeTransform;
                            if (selected != null && selected.GetComponentInChildren<Renderer>() != null)
                                SetMode(settings.LastSelectionMode);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ModelBox] 恢复选区调试状态失败: {e}");
                }
            };
        }

        private void SubscribeEvents()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            Selection.selectionChanged += OnSelectionChanged;
        }

        private void UnsubscribeEvents()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Cleanup;
            Selection.selectionChanged -= OnSelectionChanged;
        }

        // [fix] lambda 改为具名方法，支持 -= 取消订阅
        private void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            RestoreOriginalMaterials();
        }

        // [fix] lambda 改为具名方法，支持 -= 取消订阅
        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                RestoreOriginalMaterials();
        }

        private void OnSelectionChanged()
        {
            // 仅在选区调试模式活跃时执行迁移
            if (CurrentMode == SelectionDebugMode.None) return;

            // 恢复旧选区的原始材质
            RestoreOriginalMaterials();

            // 对新选区应用当前调试模式
            var selected = Selection.activeTransform;
            if (selected != null && selected.GetComponentInChildren<Renderer>() != null)
            {
                ApplyDebugMaterial(CurrentMode);
            }

            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        private void Cleanup()
        {
            // [fix] 域重载前必须先恢复原始材质，否则被替换为调试材质的 Renderer
            // 在重载后会显示 "Missing (Material)"（调试材质已随域重载销毁）
            RestoreOriginalMaterials();

            // [fix] 完整取消所有事件订阅，防止域重载后 handler 叠加
            UnsubscribeEvents();

            // [fix] 清理静态事件委托，防止域重载后悬挂引用
            OnBoneSelectedInScene = null;

            if (_textureChannelMat != null) { Object.DestroyImmediate(_textureChannelMat); _textureChannelMat = null; }
            if (_checkerboardMat != null) { Object.DestroyImmediate(_checkerboardMat); _checkerboardMat = null; }
            if (_uniformColorMat != null) { Object.DestroyImmediate(_uniformColorMat); _uniformColorMat = null; }
            if (_vertexColorMat != null) { Object.DestroyImmediate(_vertexColorMat); _vertexColorMat = null; }
            ModelBoxOverlayRenderer.Cleanup();
        }

        public void SetMode(SelectionDebugMode mode)
        {
            if (CurrentMode == mode) return;
            // [fix v0.6.x] 沙盒互斥守卫：材质沙盒活跃时禁止选区调试 —— 沙盒期间 sharedMaterials 已被
            // 替换为沙盒材质，若此时保存"原始材质"实际存的是沙盒材质，丢弃沙盒后恢复链互相污染导致材质错乱
            if (mode != SelectionDebugMode.None && MaterialDiffPanel.SandboxActive)
            {
                Debug.LogWarning("[ModelBox] 选区调试与材质沙盒互斥：请先「丢弃」沙盒再启用选区调试。");
                return;
            }
            RestoreOriginalMaterials();
            CurrentMode = mode;
            if (mode != SelectionDebugMode.None)
                ApplyDebugMaterial(mode);
            // [fix v0.4] 持久化选区调试模式到 Settings
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null) { settings.LastSelectionMode = mode; settings.Save(); }
            // 使用 delayCall 避免 duringSceneGui 递归渲染
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        public void SetOverlayFlags(MeshOverlayFlags flags)
        {
            OverlayFlags = flags;
            // [fix v0.4] 持久化网格叠加状态到 Settings
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null) { settings.MeshOverlayState = flags; settings.Save(); }
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        public void ToggleOverlayFlag(MeshOverlayFlags flag)
        {
            OverlayFlags ^= flag;
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        public string TryGetPropertyColor(string propertyName)
        {
            var selected = Selection.activeTransform;
            if (selected == null) { _propertyColorInfo = "未选中物体"; return _propertyColorInfo; }

            var renderer = selected.GetComponentInChildren<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null)
            { _propertyColorInfo = "无 Renderer/Material"; return _propertyColorInfo; }

            var mat = renderer.sharedMaterial;
            if (!mat.HasProperty(propertyName))
            { _propertyColorInfo = $"材质无属性 '{propertyName}'"; return _propertyColorInfo; }

            // 尝试读取颜色，如果是 float 属性则用 GetFloat 构造颜色
            try
            {
                _propertyColor = mat.GetColor(propertyName);
            }
            catch
            {
                try
                {
                    float v = mat.GetFloat(propertyName);
                    _propertyColor = new Color(v, v, v, 1f);
                }
                catch
                {
                    _propertyColor = Color.magenta;
                }
            }
            _propertyColorInfo = $"{propertyName} = RGBA({_propertyColor.r:F3}, {_propertyColor.g:F3}, {_propertyColor.b:F3}, {_propertyColor.a:F3})";
            return _propertyColorInfo;
        }

        public string PropertyColorInfo => _propertyColorInfo;

        /// <summary>
        /// 实时更新棋盘格材质属性（滑块拖动时调用）。
        /// </summary>
        public void UpdateCheckerboardProperties()
        {
            if (_checkerboardMat == null) return;
            _checkerboardMat.SetFloat("_GridSize", CheckerGridSize);
            _checkerboardMat.SetColor("_ColorA", CheckerColorA);
            _checkerboardMat.SetColor("_ColorB", CheckerColorB);
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 实时更新贴图通道材质属性（参数变化时调用）。
        /// </summary>
        public void UpdateTextureChannelProperties()
        {
            if (_textureChannelMat == null) return;
            _textureChannelMat.SetVector("_ChannelMask", GetChannelMaskVector());
            _textureChannelMat.SetVector("_TexScale", new Vector4(TextureScale.x, TextureScale.y, 0, 0));
            _textureChannelMat.SetVector("_TexOffset", new Vector4(TextureOffset.x, TextureOffset.y, 0, 0));
            _textureChannelMat.SetInt("_UVChannel", UVChannel);
            _textureChannelMat.SetInt("_WorldSpaceUV", WorldSpaceUV ? 1 : 0);
            _textureChannelMat.SetFloat("_WorldUVScale", WorldUVScale);
            _textureChannelMat.SetFloat("_ClampMin", ClampMin);
            _textureChannelMat.SetFloat("_ClampMax", ClampMax);
            _textureChannelMat.SetInt("_DisplayMode", MonoDisplay ? 1 : 0);
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// [fix] 重申当前选区调试材质：先还原原始材质再重新应用。
        /// 用于激活状态下变更"仅应用时读取"的参数（如自定义贴图——UpdateTextureChannelProperties 不处理贴图，
        /// 贴图只在 ApplyDebugMaterial 中从源材质/自定义槽读取）。
        /// </summary>
        public void RefreshDebugMaterial()
        {
            if (CurrentMode == SelectionDebugMode.None) return;
            RestoreOriginalMaterials();
            ApplyDebugMaterial(CurrentMode);
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 实时更新顶点颜色材质属性（通道切换时调用）。
        /// </summary>
        public void UpdateVertexColorProperties()
        {
            if (_vertexColorMat == null) return;
            _vertexColorMat.SetFloat("_ChannelMode", VertexColorChannel);
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// 实时更新属性颜色材质（颜色变化时调用）。
        /// </summary>
        public void UpdateShaderPropertyColor()
        {
            if (_uniformColorMat == null) return;
            _uniformColorMat.SetColor("_UniformColor", _propertyColor);
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        private Vector4 GetChannelMaskVector()
        {
            switch (ChannelMask)
            {
                case 1: return new Vector4(1, 0, 0, 0);
                case 2: return new Vector4(0, 1, 0, 0);
                case 3: return new Vector4(0, 0, 1, 0);
                case 4: return new Vector4(0, 0, 0, 1);
                default: return new Vector4(1, 1, 1, 0);
            }
        }

        private void ApplyDebugMaterial(SelectionDebugMode mode)
        {
            var selected = Selection.activeTransform;
            if (selected == null) return;

            var renderers = selected.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                // [fix] 跳过未激活的 Renderer（LOD Group 非活跃级别 / billboard）
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!_originalMaterials.ContainsKey(r))
                    _originalMaterials[r] = r.sharedMaterials;

                switch (mode)
                {
                    case SelectionDebugMode.TextureChannel:
                        var texMat = GetOrCreateMaterial(ref _textureChannelMat, "Hidden/ModelBox/TextureChannel");
                        if (texMat != null)
                        {
                            // 优先使用用户自定义贴图，否则从材质读取
                            if (CustomTexture != null)
                            {
                                texMat.SetTexture("_MainTex", CustomTexture);
                            }
                            else
                            {
                                // [fix] FindMaterialMainTexture：兼容自定义 shader（主贴图属性名非 _MainTex/_BaseMap）
                                var srcTex = FindMaterialMainTexture(r.sharedMaterial);
                                if (srcTex != null)
                                    texMat.SetTexture("_MainTex", srcTex);
                            }
                            texMat.SetVector("_ChannelMask", GetChannelMaskVector());
                            texMat.SetVector("_TexScale", new Vector4(TextureScale.x, TextureScale.y, 0, 0));
                            texMat.SetVector("_TexOffset", new Vector4(TextureOffset.x, TextureOffset.y, 0, 0));
                            texMat.SetInt("_UVChannel", UVChannel);
                            texMat.SetInt("_WorldSpaceUV", WorldSpaceUV ? 1 : 0);
                            texMat.SetFloat("_WorldUVScale", WorldUVScale);
                            texMat.SetFloat("_ClampMin", ClampMin);
                            texMat.SetFloat("_ClampMax", ClampMax);
                            texMat.SetInt("_DisplayMode", MonoDisplay ? 1 : 0);
                            ApplyAllSlots(r, texMat); // [M2-1 fix] 应用到所有材质槽
                        }
                        break;

                    case SelectionDebugMode.Checkerboard:
                        var chkMat = GetOrCreateMaterial(ref _checkerboardMat, "Hidden/ModelBox/Checkerboard");
                        if (chkMat != null)
                        {
                            chkMat.SetFloat("_GridSize", CheckerGridSize);
                            chkMat.SetColor("_ColorA", CheckerColorA);
                            chkMat.SetColor("_ColorB", CheckerColorB);
                            ApplyAllSlots(r, chkMat); // [M2-1 fix]
                        }
                        break;

                    case SelectionDebugMode.ShaderProperty:
                        var uniMat = GetOrCreateMaterial(ref _uniformColorMat, "Hidden/ModelBox/UniformColor");
                        if (uniMat != null)
                        {
                            uniMat.SetColor("_UniformColor", _propertyColor);
                            ApplyAllSlots(r, uniMat); // [M2-1 fix]
                        }
                        break;

                    case SelectionDebugMode.VertexColor:
                        // [feat] 顶点颜色可视化：读取 mesh 内置 COLOR 数据
                        var vcMat = GetOrCreateMaterial(ref _vertexColorMat, "Hidden/ModelBox/VertexColor");
                        if (vcMat != null)
                        {
                            vcMat.SetFloat("_ChannelMode", VertexColorChannel);
                            ApplyAllSlots(r, vcMat);
                        }
                        break;
                }
            }
        }

        public void RestoreOriginalMaterials()
        {
            foreach (var kvp in _originalMaterials)
            {
                if (kvp.Key != null && kvp.Value != null)
                    kvp.Key.sharedMaterials = kvp.Value;
            }
            _originalMaterials.Clear();
        }

        /// <summary>
        /// 获取指定 Renderer 的原始材质（即使当前有调试覆盖）。
        /// ShaderInfoPanel 需要此方法来读取原始 Shader 信息。
        /// </summary>
        public Material GetOriginalMaterial(Renderer renderer)
        {
            if (renderer == null) return null;
            if (_originalMaterials.TryGetValue(renderer, out var mats) && mats.Length > 0)
                return mats[0];
            return renderer.sharedMaterial;
        }

        /// <summary>
        /// [feat] 指定 Renderer 当前是否被选区调试材质覆盖（PixelBar 据此采样调试数据而非法线值）。
        /// </summary>
        public bool IsRendererOverridden(Renderer renderer)
        {
            return renderer != null && _originalMaterials.ContainsKey(renderer);
        }

        /// <summary>
        /// [feat v0.6.x] 当前被选区调试材质覆盖的 Renderer 集合（只读，零分配）。
        /// 全局调试最终全量 Pass 据此排除这些 Renderer —— 选区调试独立于全场景调试（见 README 语义），
        /// 本地（更具体）的调试显示优先于全局调试材质。
        /// </summary>
        public IReadOnlyCollection<Renderer> OverriddenRenderers => _originalMaterials.Keys;

        /// <summary>
        /// [feat] 贴图通道模式：获取采样源贴图（自定义贴图优先，否则该 Renderer 原始材质的主贴图）。
        /// 与 ApplyDebugMaterial 中的贴图读取逻辑保持一致。
        /// </summary>
        public Texture GetTextureChannelSource(Renderer renderer)
        {
            if (CustomTexture != null) return CustomTexture;
            return FindMaterialMainTexture(GetOriginalMaterial(renderer));
        }

        /// <summary>
        /// [fix] 查找材质主贴图：_MainTex → _BaseMap → 扫描第一个非空 Texture 属性。
        /// 兼容自定义 shader（如 Custom/G/basic 等主贴图属性名非标准的材质，此前这类材质
        /// 会返回 null 导致屏幕显示默认白、PixelBar 采样 [无贴图] N/A）。
        /// ApplyDebugMaterial（屏幕显示）与 GetTextureChannelSource（PixelBar 采样）共用，保证两者一致。
        /// </summary>
        public static Texture FindMaterialMainTexture(Material mat)
        {
            if (mat == null) return null;
            if (mat.HasProperty("_MainTex"))
            {
                var t = mat.GetTexture("_MainTex");
                if (t != null) return t;
            }
            if (mat.HasProperty("_BaseMap"))
            {
                var t = mat.GetTexture("_BaseMap");
                if (t != null) return t;
            }
            var shader = mat.shader;
            if (shader == null) return null;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                if ((shader.GetPropertyFlags(i) & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0) continue;
                var tex = mat.GetTexture(shader.GetPropertyName(i));
                if (tex != null) return tex;
            }
            return null;
        }

        private Material GetOrCreateMaterial(ref Material field, string shaderName)
        {
            if (field != null) return field;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[ModelBox] Shader '{shaderName}' not found.");
                return null;
            }
            field = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return field;
        }

        /// <summary>[M2-1 fix] 将调试材质应用到 Renderer 的所有材质槽位。</summary>
        private static void ApplyAllSlots(Renderer r, Material debugMat)
        {
            int count = r.sharedMaterials.Length;
            if (count == 1)
            {
                r.sharedMaterial = debugMat;
            }
            else
            {
                var mats = new Material[count];
                for (int j = 0; j < count; j++) mats[j] = debugMat;
                r.sharedMaterials = mats;
            }
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            bool hasBoneWeight = BoneWeightMode != BoneWeightDisplayMode.Off && BoneVertexWeights != null;
            bool hasBoneGizmos = ShowBoneGizmos && BoneWeightTargetSMR != null;
            if (OverlayFlags == MeshOverlayFlags.None && !hasBoneWeight && !hasBoneGizmos) return;

            var selected = Selection.activeTransform;
            if (selected == null) return;

            var camera = sceneView.camera;

            // [perf v0.6] 仅存在需要网格数据的叠加（线框/顶点/法线/切线/AABB）或骨骼权重可视化时
            // 才进入逐 Renderer 叠加路径 — 局部坐标轴叠加不依赖网格数据，
            // 避免其单独开启时无谓构建高面数网格缓存
            bool needsMeshData = (OverlayFlags & (MeshOverlayFlags.Wireframe | MeshOverlayFlags.Vertices |
                                  MeshOverlayFlags.Normals | MeshOverlayFlags.Tangents |
                                  MeshOverlayFlags.Bounds)) != 0
                                 || hasBoneWeight;

            // [feat v0.6] 局部坐标轴长度自适应基准：选中层级所有激活 Renderer 的合并世界包围盒
            var combinedBounds = new Bounds();
            bool hasBounds = false;

            // [fix] 包含非激活的 LOD 级别 GameObject（SpeedTree 等模型有 LOD 层级）
            var renderers = selected.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                // [fix] 跳过未激活的 Renderer：LOD Group 会禁用非活跃级别的 Renderer.enabled，
                // billboard 等辅助 Renderer 也可能存在但不应该被调试
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;

                // [fix v0.6] 轴长基准只统计网格类 Renderer（MeshRenderer/SkinnedMeshRenderer）——
                // 粒子等特效 Renderer 的 bounds 会把轴长撑到失真，导致局部坐标轴信息不可信
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                {
                    if (hasBounds) combinedBounds.Encapsulate(r.bounds);
                    else { combinedBounds = r.bounds; hasBounds = true; }
                }

                // [perf v0.6] 无需网格数据时（如仅局部坐标轴叠加）跳过网格提取与叠加绘制
                if (!needsMeshData) continue;

                Mesh mesh = null;
                bool isSkinned = false;
                SkinnedMeshRenderer smr = null;

                if (r is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                else if (r is SkinnedMeshRenderer skinR)
                {
                    smr = skinR;
                    isSkinned = true;
                    mesh = skinR.sharedMesh;
                }

                if (mesh == null) continue;
                DrawMeshOverlay(r, mesh, camera, isSkinned, smr);
            }

            // [feat v0.6] 模型局部坐标：在选中物体原点（pivot）绘制 X/Y/Z 三向轴
            if ((OverlayFlags & MeshOverlayFlags.LocalAxes) != 0)
                DrawLocalAxesGizmo(selected, combinedBounds, hasBounds);

            // [feat] 骨骼 gizmo 渲染（骨骼位置标记 + 父子连线 + 选中高亮）
            if (hasBoneGizmos)
                DrawBoneGizmos(sceneView);

            // [feat] 权重热力图图例
            if (hasBoneWeight)
                DrawWeightLegend(sceneView);
        }

        /// <summary>
        /// [feat v0.6] 模型局部坐标 gizmo：在选中物体原点绘制三向轴（X红/Y绿/Z蓝 + 箭头 + 标签）。
        /// 轴长 = 选中层级合并包围盒对角线 × LocalAxesLength 系数（无 Renderer 时回退固定长度），
        /// 深度测试遵循与其他叠加一致的 OverlayDepthTest 设置。
        /// </summary>
        private void DrawLocalAxesGizmo(Transform target, Bounds combinedBounds, bool hasBounds)
        {
            if (target == null) return;

            // [fix v0.6] 轴长计算收敛到纯函数（单元测试覆盖），保证信息正确性有测试兜底
            float length = ModelBoxOverlayRenderer.ComputeLocalAxesLength(hasBounds, combinedBounds.extents, LocalAxesLength);

            var settings = ModelBoxSettings.GetOrCreate();
            bool depthTest = settings != null && settings.OverlayDepthTest;

            ModelBoxOverlayRenderer.DrawLocalAxes(target.position, target.rotation, length, depthTest);
        }

        private void DrawMeshOverlay(Renderer renderer, Mesh mesh, Camera camera, bool isSkinned = false, SkinnedMeshRenderer smr = null)
        {
            CachedMeshData data;

            // [fix] 骨骼权重可视化也需要烘焙蒙皮网格，确保顶点位置匹配当前动画姿态
            bool hasBoneWeight = BoneWeightMode != BoneWeightDisplayMode.Off && BoneVertexWeights != null
                && BoneWeightTargetSMR != null && renderer is SkinnedMeshRenderer smrCheck && smrCheck == BoneWeightTargetSMR;

            // 蒙皮网格烘焙：有显式叠加标志 或 骨骼权重可视化激活时执行
            bool needsSkinnedPose = isSkinned && UseGPURendering && smr != null
                && ((OverlayFlags != MeshOverlayFlags.None) || hasBoneWeight);

            if (needsSkinnedPose)
            {
                data = ModelBoxOverlayRenderer.BakeSkinnedMesh(smr);
            }
            else
            {
                data = ModelBoxMeshCache.GetOrBuild(mesh);
            }

            if (data == null) return;

            if (UseGPURendering)
                DrawMeshOverlayGPU(renderer, data, camera, isSkinned);
            else
                DrawMeshOverlayLegacy(renderer, data);
        }

        /// <summary>
        /// GPU 批量渲染路径：GL.LINES + Graphics.DrawMeshInstanced。
        /// 每帧仅 1~4 次 draw call，高面数场景下性能提升数百倍。
        /// </summary>
        private void DrawMeshOverlayGPU(Renderer renderer, CachedMeshData data, Camera camera, bool isSkinned)
        {
            // Unity 2020.2+ BakeMesh 输出在 renderer 本地空间，配合 localToWorldMatrix 正确。
            var matrix = renderer.transform.localToWorldMatrix;
            var bounds = renderer.bounds; // [R4 fix] 世界空间包围盒，LOD 距离计算用

            // [feat] 深度测试设置：影响所有叠加类型
            var settings = ModelBoxSettings.GetOrCreate();
            bool depthTest = settings != null && settings.OverlayDepthTest;
            var zFunc = depthTest ? CompareFunction.LessEqual : CompareFunction.Always;

            if ((OverlayFlags & MeshOverlayFlags.Wireframe) != 0)
            {
                // [perf v0.6] 静态网格优先走烘焙线网格（1 次 DrawMeshNow，顶点变换由 GPU 完成，零逐帧 CPU）；
                // 蒙皮顶点逐帧变化，保持原路径
                var wfMesh = isSkinned ? null : ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(data);
                if (wfMesh != null)
                {
                    ModelBoxOverlayRenderer.DrawOverlayLineMesh(wfMesh, matrix, camera, WireframeColor, 0f, depthTest);
                }
                else
                {
                    Handles.zTest = zFunc;
                    ModelBoxOverlayRenderer.DrawWireframe(data, matrix, WireframeColor, camera, bounds);
                }
            }

            // [feat] 顶点覆盖：显式开启 或 骨骼权重模式激活时自动启用
            bool hasBoneWeight = BoneWeightMode != BoneWeightDisplayMode.Off && BoneVertexWeights != null;
            bool showVertices = (OverlayFlags & MeshOverlayFlags.Vertices) != 0 || hasBoneWeight;
            if (showVertices)
            {
                // [fix] 骨骼权重仅作用于目标 SMR（多 SMR 模型下避免跨 Mesh 权重不匹配）
                bool isTargetSMR = hasBoneWeight && BoneWeightTargetSMR != null
                    && renderer is SkinnedMeshRenderer smrCheck && smrCheck == BoneWeightTargetSMR;

                if (isTargetSMR)
                {
                    // [fix] ColorMap 模式：Maya-style 表面渲染；Threshold 模式：顶点球体
                    if (BoneWeightMode == BoneWeightDisplayMode.ColorMap)
                    {
                        ModelBoxOverlayRenderer.DrawBoneWeightSurface(
                            data, matrix, BoneVertexWeights, camera, bounds, depthTest, BoneWeightOpacity);
                    }
                    else
                    {
                        ModelBoxOverlayRenderer.DrawVerticesWeighted(
                            data, matrix, BoneVertexWeights,
                            BoneWeightMode, BoneWeightThreshold,
                            VertexSize, VertexScaleIndependent, camera, bounds, depthTest);
                    }
                }
                else
                {
                    ModelBoxOverlayRenderer.DrawVertices(data, matrix, VertexColor, VertexSize, VertexScaleIndependent, camera, bounds, depthTest);
                }
            }

            if ((OverlayFlags & MeshOverlayFlags.Normals) != 0)
            {
                // [perf v0.6] 宽度 ≤1 走烘焙拉伸线网格（_Length uniform，滑条零重建）；>1 保留逐线粗线（高面数明显变慢）
                var nMesh = (isSkinned || NormalWidth > 1f) ? null : ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false);
                if (nMesh != null)
                {
                    ModelBoxOverlayRenderer.DrawOverlayLineMesh(nMesh, matrix, camera, NormalColor, NormalLength, depthTest);
                }
                else
                {
                    Handles.zTest = zFunc;
                    ModelBoxOverlayRenderer.DrawNormals(data, matrix, NormalColor, NormalLength, NormalWidth, camera, bounds);
                }
            }

            if ((OverlayFlags & MeshOverlayFlags.Tangents) != 0)
            {
                // [perf v0.6] 同法线：宽度 ≤1 走烘焙拉伸线网格
                var tMesh = (isSkinned || TangentWidth > 1f) ? null : ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, true);
                if (tMesh != null)
                {
                    ModelBoxOverlayRenderer.DrawOverlayLineMesh(tMesh, matrix, camera, TangentColor, TangentLength, depthTest);
                }
                else
                {
                    Handles.zTest = zFunc;
                    ModelBoxOverlayRenderer.DrawTangents(data, matrix, TangentColor, TangentLength, TangentWidth, camera, bounds);
                }
            }

            // AABB 包围盒不需要 mesh data，直接从 Renderer 绘制
            if ((OverlayFlags & MeshOverlayFlags.Bounds) != 0)
            {
                Handles.zTest = zFunc;
                ModelBoxOverlayRenderer.DrawBounds(renderer, BoundsColor, BoundsWidth);
            }

            // [fix] 重置为默认值，避免影响其他 Handles 绘制
            Handles.zTest = CompareFunction.LessEqual;
        }

        /// <summary>
        /// Legacy 回退路径：逐基元 Handles 调用。低面数物体下视觉效果更好（原生 AA）。
        /// </summary>
        private void DrawMeshOverlayLegacy(Renderer renderer, CachedMeshData data)
        {
            var matrix = renderer.transform.localToWorldMatrix;

            // [feat] 深度测试设置
            var settings = ModelBoxSettings.GetOrCreate();
            bool depthTest = settings != null && settings.OverlayDepthTest;
            Handles.zTest = depthTest ? CompareFunction.LessEqual : CompareFunction.Always;

            if ((OverlayFlags & MeshOverlayFlags.Wireframe) != 0)
            {
                Handles.color = WireframeColor;
                Handles.matrix = matrix;
                var verts = data.Vertices;
                var tris = data.TriangleIndices;
                // [fix v0.4.1] 防御性检查：顶点/三角形数组可能为 null 或索引越界
                if (verts != null && tris != null)
                {
                    int vertLimit = verts.Length;
                    for (int i = 0; i < tris.Length; i += 3)
                    {
                        // 跳过越界索引
                        if (tris[i] >= vertLimit || tris[i + 1] >= vertLimit || tris[i + 2] >= vertLimit) continue;
                        Handles.DrawLine(verts[tris[i]], verts[tris[i + 1]]);
                        Handles.DrawLine(verts[tris[i + 1]], verts[tris[i + 2]]);
                        Handles.DrawLine(verts[tris[i + 2]], verts[tris[i]]);
                    }
                }
            }

            if ((OverlayFlags & MeshOverlayFlags.Vertices) != 0)
            {
                // [fix v0.4.1] 防御性 null 检查
                if (data.Vertices == null) goto skipVertices;
                Handles.color = VertexColor;
                if (VertexScaleIndependent)
                {
                    Handles.matrix = Matrix4x4.identity;
                    float capSize = VertexSize;
                    foreach (var v in data.Vertices)
                    {
                        Vector3 wp = matrix.MultiplyPoint3x4(v);
                        Handles.SphereHandleCap(0, wp, Quaternion.identity, capSize, EventType.Repaint);
                    }
                }
                else
                {
                    Handles.matrix = matrix;
                    float capSize = VertexSize;
                    foreach (var v in data.Vertices)
                        Handles.SphereHandleCap(0, v, Quaternion.identity, capSize, EventType.Repaint);
                }
            }
            skipVertices:;

            if ((OverlayFlags & MeshOverlayFlags.Normals) != 0)
            {
                Handles.color = NormalColor;
                Handles.matrix = matrix;
                var verts = data.Vertices;
                var norms = data.Normals;
                // [fix v0.4.1] 防御性 null 检查：网格可能没有法线数据
                if (verts != null && norms != null && norms.Length == verts.Length)
                {
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 end = verts[i] + norms[i] * NormalLength;
                        if (NormalWidth > 1f)
                            Handles.DrawAAPolyLine(NormalWidth, verts[i], end);
                        else
                            Handles.DrawLine(verts[i], end);
                    }
                }
            }

            if ((OverlayFlags & MeshOverlayFlags.Tangents) != 0)
            {
                Handles.color = TangentColor;
                Handles.matrix = matrix;
                var verts = data.Vertices;
                var tans = data.Tangents;
                if (tans != null && tans.Length == verts.Length)
                {
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 tanDir = new Vector3(tans[i].x, tans[i].y, tans[i].z);
                        Vector3 end = verts[i] + tanDir * TangentLength;
                        if (TangentWidth > 1f)
                            Handles.DrawAAPolyLine(TangentWidth, verts[i], end);
                        else
                            Handles.DrawLine(verts[i], end);
                    }
                }
            }

            if ((OverlayFlags & MeshOverlayFlags.Bounds) != 0)
                ModelBoxOverlayRenderer.DrawBounds(renderer, BoundsColor, BoundsWidth);

            // [fix] 重置为默认值
            Handles.zTest = CompareFunction.LessEqual;
        }

        // [feat] 骨骼 gizmo 渲染：骨骼位置标记 + 父子连线 + 选中骨骼高亮 + 坐标轴
        private void DrawBoneGizmos(SceneView sceneView)
        {
            var smr = BoneWeightTargetSMR;
            if (smr == null) return;

            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return;

            // [perf] 复用 HashSet 避免每帧分配
            if (_boneSet == null) _boneSet = new HashSet<Transform>();
            else _boneSet.Clear();
            foreach (var b in bones)
                if (b != null) _boneSet.Add(b);

            // [feat] SceneView 点击选骨骼：鼠标点击时找最近的骨骼
            var ev = Event.current;
            if (ev != null && ev.type == EventType.MouseDown && ev.button == 0 && !ev.alt)
            {
                float bestScreenDist = 20f; // 屏幕空间阈值（像素）
                int bestBone = -1;
                var camera = sceneView.camera;

                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    Vector3 bonePos = bones[i].position;
                    Vector3 screenPos = camera.WorldToScreenPoint(bonePos);
                    if (screenPos.z < 0) continue; // 在相机后面
                    // 转换为 GUI 坐标空间距离
                    Vector2 guiPos = HandleUtility.WorldToGUIPoint(bonePos);
                    float dist = Vector2.Distance(guiPos, ev.mousePosition);
                    if (dist < bestScreenDist)
                    {
                        bestScreenDist = dist;
                        bestBone = i;
                    }
                }

                if (bestBone >= 0)
                {
                    SelectedBoneIndex = bestBone;
                    // 同步到 BonePanel（通过静态回调）
                    OnBoneSelectedInScene?.Invoke(bestBone);
                    ev.Use();
                    EditorApplication.delayCall += () => SceneView.RepaintAll();
                }
            }

            // 保存 Handles 状态
            var prevColor = Handles.color;
            var prevMatrix = Handles.matrix;
            var prevZTest = Handles.zTest;

            var settings = ModelBoxSettings.GetOrCreate();
            bool depthTest = settings != null && settings.OverlayDepthTest;
            Handles.zTest = depthTest ? CompareFunction.LessEqual : CompareFunction.Always;
            Handles.matrix = Matrix4x4.identity;

            // 1. 绘制骨骼连接线（父→子）
            Handles.color = new Color(0.55f, 0.55f, 0.75f, 0.5f);
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                var parent = bone.parent;
                if (parent != null && _boneSet.Contains(parent))
                {
                    Handles.DrawLine(parent.position, bone.position);
                }
            }

            // 2. 绘制骨骼位置标记（大小随模型缩放）
            float boundsExtent = smr.bounds.extents.magnitude;
            float boneSize = Mathf.Max(0.005f, boundsExtent * 0.012f);
            float selectedSize = boneSize * 1.8f;
            float axisLen = boneSize * 3.5f;
            float labelOffset = boneSize * 2.5f;

            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;

                bool isSelected = (i == SelectedBoneIndex);
                var pos = bone.position;

                if (isSelected)
                {
                    // 选中骨骼：橙色大球 + 坐标轴 + 名称标签
                    Handles.color = new Color(1f, 0.5f, 0.1f, 1f);
                    Handles.SphereHandleCap(0, pos, bone.rotation, selectedSize, EventType.Repaint);

                    // 坐标轴
                    var rot = bone.rotation;
                    Handles.color = new Color(1f, 0.2f, 0.2f, 0.9f);
                    Handles.DrawLine(pos, pos + rot * Vector3.right * axisLen);
                    Handles.color = new Color(0.2f, 1f, 0.2f, 0.9f);
                    Handles.DrawLine(pos, pos + rot * Vector3.up * axisLen);
                    Handles.color = new Color(0.2f, 0.5f, 1f, 0.9f);
                    Handles.DrawLine(pos, pos + rot * Vector3.forward * axisLen);

                    // 名称标签
                    var style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
                    style.normal.textColor = new Color(1f, 0.6f, 0.2f);
                    Handles.Label(pos + Vector3.up * labelOffset, $"[{i}] {bone.name}", style);
                }
                else
                {
                    // 普通骨骼：小蓝球
                    Handles.color = new Color(0.4f, 0.7f, 1f, 0.7f);
                    Handles.SphereHandleCap(0, pos, Quaternion.identity, boneSize, EventType.Repaint);
                }
            }

            // 恢复 Handles 状态
            Handles.color = prevColor;
            Handles.matrix = prevMatrix;
            Handles.zTest = prevZTest;
        }

        // [feat] 权重热力图图例：SceneView 左下角渐变条
        private void DrawWeightLegend(SceneView sceneView)
        {
            Handles.BeginGUI();

            float width = 130f;
            float height = 64f;
            float margin = 10f;
            var sceneRect = sceneView.position;
            var legendRect = new Rect(margin, sceneRect.height - height - margin - 24f, width, height);

            // 半透明背景
            EditorGUI.DrawRect(legendRect, new Color(0.12f, 0.12f, 0.12f, 0.88f));

            // 边框
            var borderRect = new Rect(legendRect.x - 1, legendRect.y - 1, legendRect.width + 2, legendRect.height + 2);
            EditorGUI.DrawRect(new Rect(borderRect.x, borderRect.y, borderRect.width, 1), new Color(0.4f, 0.4f, 0.4f, 0.6f));
            EditorGUI.DrawRect(new Rect(borderRect.x, borderRect.yMax - 1, borderRect.width, 1), new Color(0.4f, 0.4f, 0.4f, 0.6f));
            EditorGUI.DrawRect(new Rect(borderRect.x, borderRect.y, 1, borderRect.height), new Color(0.4f, 0.4f, 0.4f, 0.6f));
            EditorGUI.DrawRect(new Rect(borderRect.xMax - 1, borderRect.y, 1, borderRect.height), new Color(0.4f, 0.4f, 0.4f, 0.6f));

            // 标题
            var titleRect = new Rect(legendRect.x + 6, legendRect.y + 3, legendRect.width - 12, 14);
            EditorGUI.LabelField(titleRect, "骨骼权重", EditorStyles.miniBoldLabel);

            // 渐变条
            var barRect = new Rect(legendRect.x + 6, legendRect.y + 20, legendRect.width - 12, 14);
            int segments = 32;
            float segWidth = barRect.width / segments;
            for (int i = 0; i < segments; i++)
            {
                float t = (float)i / (segments - 1);
                float r = Mathf.Clamp01(Mathf.Max(0, (t - 0.25f) * 4f));
                float g = Mathf.Clamp01(t < 0.5f ? t * 4f : (1f - t) * 4f);
                float b = Mathf.Clamp01(Mathf.Max(0, (0.75f - t) * 4f));
                EditorGUI.DrawRect(new Rect(barRect.x + i * segWidth, barRect.y, segWidth + 1, barRect.height),
                    new Color(r, g, b, 1f));
            }

            // 标签
            var labelStyle = EditorStyles.miniLabel;
            EditorGUI.LabelField(new Rect(barRect.x, barRect.yMax + 1, 30, 12), "0.0", labelStyle);
            EditorGUI.LabelField(new Rect(barRect.xMax - 26, barRect.yMax + 1, 30, 12), "1.0", labelStyle);

            // 阈值指示
            if (BoneWeightMode == BoneWeightDisplayMode.Threshold)
            {
                var threshRect = new Rect(legendRect.x + 6, legendRect.y + 48, legendRect.width - 12, 14);
                EditorGUI.LabelField(threshRect, $"阈值: {BoneWeightThreshold:F2}", labelStyle);
            }

            Handles.EndGUI();
        }
    }
}
