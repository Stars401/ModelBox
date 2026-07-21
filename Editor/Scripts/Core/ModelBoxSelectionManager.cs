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

        // 贴图通道参数
        public Vector2 TextureScale { get; set; } = Vector2.one;
        public Vector2 TextureOffset { get; set; } = Vector2.zero;
        public int UVChannel { get; set; } = 0; // 0=UV0, 1=UV1, 2=UV2, 3=UV3
        public bool WorldSpaceUV { get; set; } = false;
        public float WorldUVScale { get; set; } = 0.01f;

        // 贴图亮度钳制参数（Lightness Map / Ramp Mask 调试）
        public float ClampMin { get; set; } = 0f;
        public float ClampMax { get; set; } = 1f;

        // 网格叠加样式设置
        public Color WireframeColor { get; set; } = new Color(0.4f, 0.7f, 1f, 0.8f);
        public Color VertexColor { get; set; } = new Color(1f, 1f, 0.2f, 0.9f);
        public Color NormalColor { get; set; } = new Color(0.3f, 0.6f, 1f, 0.7f);
        public float VertexSize { get; set; } = 0.03f;
        public float NormalLength { get; set; } = 0.1f;
        public float NormalWidth { get; set; } = 2f;
        public bool VertexScaleIndependent { get; set; } = true;

        // GPU 加速开关（默认开启）
        public bool UseGPURendering { get; set; } = true;

        // 切线叠加
        public Color TangentColor { get; set; } = new Color(1f, 1f, 0.2f, 0.8f);
        public float TangentLength { get; set; } = 0.1f;
        public float TangentWidth { get; set; } = 2f;

        // 包围盒叠加
        public Color BoundsColor { get; set; } = new Color(0f, 1f, 0.5f, 0.6f);
        public float BoundsWidth { get; set; } = 2f;

        // [feat] 骨骼权重可视化状态（由 BonePanel 写入）
        public BoneWeightDisplayMode BoneWeightMode { get; set; } = BoneWeightDisplayMode.Off;
        public float[] BoneVertexWeights { get; set; }
        public float BoneWeightThreshold { get; set; } = 0.1f;
        public SkinnedMeshRenderer BoneWeightTargetSMR { get; set; } // 权重来源 SMR

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

            if (_textureChannelMat != null) { Object.DestroyImmediate(_textureChannelMat); _textureChannelMat = null; }
            if (_checkerboardMat != null) { Object.DestroyImmediate(_checkerboardMat); _checkerboardMat = null; }
            if (_uniformColorMat != null) { Object.DestroyImmediate(_uniformColorMat); _uniformColorMat = null; }
            ModelBoxOverlayRenderer.Cleanup();
        }

        public void SetMode(SelectionDebugMode mode)
        {
            if (CurrentMode == mode) return;
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
                                var srcMat = r.sharedMaterial;
                                if (srcMat != null)
                                {
                                    if (srcMat.HasProperty("_MainTex"))
                                        texMat.SetTexture("_MainTex", srcMat.GetTexture("_MainTex"));
                                    else if (srcMat.HasProperty("_BaseMap"))
                                        texMat.SetTexture("_MainTex", srcMat.GetTexture("_BaseMap"));
                                }
                            }
                            texMat.SetVector("_ChannelMask", GetChannelMaskVector());
                            texMat.SetVector("_TexScale", new Vector4(TextureScale.x, TextureScale.y, 0, 0));
                            texMat.SetVector("_TexOffset", new Vector4(TextureOffset.x, TextureOffset.y, 0, 0));
                            texMat.SetInt("_UVChannel", UVChannel);
                            texMat.SetInt("_WorldSpaceUV", WorldSpaceUV ? 1 : 0);
                            texMat.SetFloat("_WorldUVScale", WorldUVScale);
                            texMat.SetFloat("_ClampMin", ClampMin);
                            texMat.SetFloat("_ClampMax", ClampMax);
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
            if (OverlayFlags == MeshOverlayFlags.None && !hasBoneWeight) return;

            var selected = Selection.activeTransform;
            if (selected == null) return;

            var camera = sceneView.camera;

            // [fix] 包含非激活的 LOD 级别 GameObject（SpeedTree 等模型有 LOD 层级）
            var renderers = selected.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                // [fix] 跳过未激活的 Renderer：LOD Group 会禁用非活跃级别的 Renderer.enabled，
                // billboard 等辅助 Renderer 也可能存在但不应该被调试
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;

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
                DrawMeshOverlayGPU(renderer, data, camera);
            else
                DrawMeshOverlayLegacy(renderer, data);
        }

        /// <summary>
        /// GPU 批量渲染路径：GL.LINES + Graphics.DrawMeshInstanced。
        /// 每帧仅 1~4 次 draw call，高面数场景下性能提升数百倍。
        /// </summary>
        private void DrawMeshOverlayGPU(Renderer renderer, CachedMeshData data, Camera camera)
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
                Handles.zTest = zFunc;
                ModelBoxOverlayRenderer.DrawWireframe(data, matrix, WireframeColor, camera, bounds);
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
                            data, matrix, BoneVertexWeights, camera, bounds, depthTest);
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
                Handles.zTest = zFunc;
                ModelBoxOverlayRenderer.DrawNormals(data, matrix, NormalColor, NormalLength, NormalWidth, camera, bounds);
            }

            if ((OverlayFlags & MeshOverlayFlags.Tangents) != 0)
            {
                Handles.zTest = zFunc;
                ModelBoxOverlayRenderer.DrawTangents(data, matrix, TangentColor, TangentLength, TangentWidth, camera, bounds);
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
    }
}
