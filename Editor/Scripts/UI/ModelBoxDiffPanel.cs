using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 材质参数沙盒。自动读取选中物体材质，创建临时副本用于实时对比调试。
    /// 支持：实时预览编辑效果、重置、应用到原始、丢弃。临时材质在丢弃/窗口关闭时清理。
    /// [fix v0.4] 修复材质替换失败：添加 SceneView 重绘、TogglePreview 从 _savedOriginals 克隆、LODGroup 多 Renderer 支持。
    /// </summary>
    public class MaterialDiffPanel
    {
        // ---- 静态域重载安全网 ----
        [InitializeOnLoad]
        private static class DomainReloadHandler
        {
            static DomainReloadHandler()
            {
                AssemblyReloadEvents.beforeAssemblyReload += () =>
                {
                    // [fix v0.4] 域重载前确保所有 Renderer 材质恢复（含 LODGroup 各级别）
                    // [fix v0.4.1] 防御性 null 检查：_sandboxAllSavedOriginals 可能为 null
                    if (_sandboxAllRenderers != null && _sandboxAllSavedOriginals != null)
                    {
                        for (int i = 0; i < _sandboxAllRenderers.Count; i++)
                        {
                            var rend = _sandboxAllRenderers[i];
                            var originals = i < _sandboxAllSavedOriginals.Count ? _sandboxAllSavedOriginals[i] : null;
                            if (rend != null && originals != null)
                            {
                                try { rend.sharedMaterials = originals; }
                                catch { }
                            }
                        }
                    }
                    // 回退：如果多 Renderer 列表为空，尝试单 Renderer 恢复
                    if (_sandboxActiveRenderer != null && _sandboxSavedOriginals != null)
                    {
                        try { _sandboxActiveRenderer.sharedMaterials = _sandboxSavedOriginals; }
                        catch { }
                    }
                    if (_sandboxActiveMaterial != null)
                        Object.DestroyImmediate(_sandboxActiveMaterial);
                    _sandboxActiveRenderer = null;
                    _sandboxActiveMaterial = null;
                    _sandboxSavedOriginals = null;
                    _sandboxAllRenderers = null;
                    _sandboxAllSavedOriginals = null;
                };
            }
        }

        // 域重载安全网用的静态引用（镜像实例字段）
        private static Renderer _sandboxActiveRenderer;
        private static Material _sandboxActiveMaterial;
        private static Material[] _sandboxSavedOriginals;
        // [fix v0.4] 多 Renderer 静态镜像
        private static List<Renderer> _sandboxAllRenderers;
        private static List<Material[]> _sandboxAllSavedOriginals;
        // [fix v0.6.x] 沙盒活跃标志：与选区调试互斥守卫（两系统都替换 sharedMaterials，无互斥会互相污染恢复链）
        private static bool s_sandboxActive;
        /// <summary>材质沙盒是否活跃（SelectionManager.SetMode 据此拒绝选区调试，保护恢复链）。</summary>
        public static bool SandboxActive => s_sandboxActive;
        /// <summary>[fix v0.6.x] 沙盒正在预览替换材质的 Renderer 列表（静态镜像；全局调试最终全量 Pass 据此排除）。</summary>
        public static List<Renderer> SandboxPreviewRenderers => _sandboxAllRenderers;
        // ---- 状态 ----
        private Renderer _targetRenderer;
        // [fix v0.4] LODGroup 多 Renderer 支持：沙盒需要替换所有 LOD 级别的对应材质槽
        private List<Renderer> _allTargetRenderers = new List<Renderer>();
        private List<Material[]> _allSavedOriginals = new List<Material[]>();
        private Material _originalMaterial; // 启动沙盒时快照的原始材质引用
        private string _originalMatName;    // 原始材质名称（防御性备份）
        private string _originalShaderName; // 原始 Shader 名称
        private Shader _originalShader;     // 启动时的 Shader 引用（用于检测 Shader 变更）
        private Material _sandboxMaterial;  // 临时副本（HideAndDontSave）
        private Material[] _savedOriginals; // 保存的原始 sharedMaterials 数组（主 Renderer）
        private int _activeSlotIndex = 0;   // 当前编辑的材质槽索引
        private bool _isPreviewing;

        // [fix v0.6.x] 材质候选：跨 Renderer 聚合 + 按材质实例去重
        private struct MaterialCandidate
        {
            public Renderer Renderer;
            public int Slot;
            public Material Material;
            public int RefCount;       // 该材质实例被引用的槽位总数
            public string FirstSource; // 首个来源描述（物体名[槽i]）
        }
        private readonly List<MaterialCandidate> _candidates = new List<MaterialCandidate>();
        private readonly Dictionary<Material, int> _candidateIndexByMat = new Dictionary<Material, int>();

        /// <summary>
        /// [fix v0.6.x] 遍历选中层级所有激活 Renderer 的全部材质槽，
        /// 按材质实例去重聚合为候选（同一材质占多槽位/被多 Renderer 引用只列一次并计数）。
        /// 旧逻辑只取 GetComponentInChildren 第一个 Renderer 的槽位 —— 多部件模型只能看到
        /// 首个 Renderer 的材质，且同实例多槽位按槽位重复列出（用户实测的重复候选 bug）。
        /// </summary>
        private void RebuildCandidates(Transform selected)
        {
            _candidates.Clear();
            _candidateIndexByMat.Clear();

            foreach (var rend in selected.GetComponentsInChildren<Renderer>())
            {
                if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
                Material[] mats;
                try { mats = rend.sharedMaterials; }
                catch { continue; /* Renderer 可能已被销毁 */ }

                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (_candidateIndexByMat.TryGetValue(m, out int idx))
                    {
                        var c = _candidates[idx];
                        c.RefCount++;
                        _candidates[idx] = c;
                    }
                    else
                    {
                        _candidateIndexByMat[m] = _candidates.Count;
                        _candidates.Add(new MaterialCandidate
                        {
                            Renderer = rend,
                            Slot = i,
                            Material = m,
                            RefCount = 1,
                            FirstSource = $"{rend.gameObject.name}[{i}]"
                        });
                    }
                }
            }
        }

        private int _diffCount;
        private int _totalCount;
        private bool _showOnlyDifferences = false;
        private bool _showAllProperties = false; // 显示所有属性（包括被 keyword 隐藏的）
        private Vector2 _scrollPos;
        private string _searchFilter = "";

        // ===================== 主入口 =====================

        public void Draw()
        {
            // 沙盒活跃时：跳过自动检测，直接使用已存储的状态
            if (_sandboxMaterial != null && _targetRenderer != null)
            {
                // 验证 Renderer 仍然有效（未被销毁）
                if (_targetRenderer.gameObject == null)
                {
                    DiscardSandbox();
                    DrawEmptyState("目标物体已被销毁。");
                    return;
                }

                // 检测 Shader 变更
                if (_originalMaterial != null && _originalMaterial.shader != _originalShader)
                {
                    EditorGUILayout.HelpBox(
                        $"材质的 Shader 已变更（{_originalShaderName} → {_originalMaterial.shader.name}）。\n" +
                        "请丢弃沙盒后重新开始。",
                        MessageType.Warning);
                    DrawHeader();
                    DrawBottomActions();
                    return;
                }

                // 选择锁定提示
                var currentSelection = Selection.activeTransform;
                if (currentSelection != null && _targetRenderer != null &&
                    currentSelection != _targetRenderer.transform &&
                    currentSelection.GetComponentInChildren<Renderer>() != _targetRenderer)
                {
                    EditorGUILayout.HelpBox(
                        $"沙盒已锁定到 [{_targetRenderer.gameObject.name}]。丢弃后可选择新物体。",
                        MessageType.Info);
                }

                DrawHeader();
                DrawDiffActiveSection();
                return;
            }

            // ---- 沙盒未活跃：自动检测选中物体 ----
            var selected = Selection.activeTransform;
            if (selected == null)
            {
                DrawEmptyState("请在 Scene 中选择一个物体。");
                return;
            }

            // [fix v0.6.x] 遍历选中层级所有激活 Renderer 聚合材质候选（按材质实例去重），
            // 不再只取第一个 Renderer 的槽位
            RebuildCandidates(selected);
            if (_candidates.Count == 0)
            {
                DrawEmptyState("选中物体及其子级没有可用材质。");
                return;
            }

            // 校验当前候选仍有效（选中变化 / 槽位材质变化后回退到首个候选）
            bool currentValid = false;
            foreach (var cand in _candidates)
            {
                if (cand.Renderer == _targetRenderer && cand.Slot == _activeSlotIndex)
                {
                    currentValid = true;
                    break;
                }
            }
            if (!currentValid)
            {
                _targetRenderer = _candidates[0].Renderer;
                _activeSlotIndex = _candidates[0].Slot;
            }

            DrawCandidateSelector(_candidates);
            DrawStartSection();
        }

        // ===================== 头部 =====================

        /// <summary>沙盒活跃时的头部（使用存储状态，不访问 renderer.sharedMaterial）。</summary>
        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("材质沙盒", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_isPreviewing)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(0.3f, 1f, 0.3f)
                    : new Color(0.1f, 0.55f, 0.1f);
                EditorGUILayout.LabelField("PREVIEW", EditorStyles.boldLabel, GUILayout.Width(60));
                GUI.contentColor = prevColor;
            }
            EditorGUILayout.EndHorizontal();

            // 信息行（使用缓存名称，不访问可能已替换的材质对象）
            var pc = GUI.contentColor;
            GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
            string objName = _targetRenderer != null ? _targetRenderer.gameObject.name : "-";
            // [fix v0.4.1] 防御性检查：Renderer 可能已销毁导致 sharedMaterials 抛异常
            int slotCount = 1;
            try
            {
                if (_targetRenderer != null)
                {
                    var sm = _targetRenderer.sharedMaterials;
                    if (sm != null) slotCount = sm.Length;
                }
            }
            catch { /* Renderer 可能已被销毁 */ }
            string slotInfo = slotCount > 1 ? $"  |  槽位 [{_activeSlotIndex}/{slotCount - 1}]" : "";
            EditorGUILayout.LabelField(
                $"物体: {objName}{slotInfo}  |  材质: {_originalMatName}  |  Shader: {_originalShaderName}",
                EditorStyles.miniLabel);
            GUI.contentColor = pc;

            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, ModelBoxStyles.SeparatorColor);
            EditorGUILayout.Space(4);
        }

        /// <summary>沙盒未活跃时的头部（显示跨 Renderer 聚合去重后的材质候选）。</summary>
        private void DrawCandidateSelector(List<MaterialCandidate> candidates)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("材质沙盒", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            var pc = GUI.contentColor;
            GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
            EditorGUILayout.LabelField(
                $"选中层级: {candidates.Count} 个材质（{SlotRefCount(candidates)} 个槽位引用，已按实例去重）",
                EditorStyles.miniLabel);
            GUI.contentColor = pc;

            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, ModelBoxStyles.SeparatorColor);
            EditorGUILayout.Space(4);

            // 材质候选列表：按材质实例去重，多槽/多 Renderer 引用同一材质只出现一次并标注引用数
            EditorGUILayout.LabelField("选择材质：", EditorStyles.boldLabel);
            foreach (var cand in candidates)
            {
                bool isActive = (cand.Renderer == _targetRenderer && cand.Slot == _activeSlotIndex);
                var prevBg = GUI.backgroundColor;
                if (isActive) GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();

                string shaderName = cand.Material.shader != null ? cand.Material.shader.name : "Unknown";
                string refInfo = cand.RefCount > 1 ? $"  ×{cand.RefCount}引用" : "";
                if (GUILayout.Button($"{cand.Material.name}  ({shaderName}){refInfo}  — {cand.FirstSource}", EditorStyles.miniButton))
                {
                    _targetRenderer = cand.Renderer;
                    _activeSlotIndex = cand.Slot;
                }
                GUI.backgroundColor = prevBg;
            }
            EditorGUILayout.Space(4);
        }

        /// <summary>统计候选中材质被引用的槽位总数。</summary>
        private static int SlotRefCount(List<MaterialCandidate> candidates)
        {
            int total = 0;
            foreach (var c in candidates) total += c.RefCount;
            return total;
        }

        // ===================== 未开始 =====================

        private void DrawStartSection()
        {
            if (_targetRenderer == null)
            {
                DrawEmptyState("请先选择材质。");
                return;
            }

            Material[] materials;
            try { materials = _targetRenderer.sharedMaterials; }
            catch { DrawEmptyState("Renderer 已不可用。"); return; }

            // 获取选中槽位的材质
            if (_activeSlotIndex >= materials.Length) _activeSlotIndex = 0;
            var material = materials[_activeSlotIndex];

            if (material == null)
            {
                EditorGUILayout.HelpBox($"材质槽 [{_activeSlotIndex}] 为空。", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"即将为材质槽 [{_activeSlotIndex}] 创建副本。\n" +
                "可自由调节参数并实时预览效果。满意后「应用到原始」，不满意「丢弃」恢复。",
                MessageType.Info);
            EditorGUILayout.Space(4);

            // [fix v0.4.1] shader 可能为 null（材质引用了已删除的 shader）
            string shaderDisplayName = material.shader != null ? material.shader.name : "(Missing Shader)";
            string matInfo = $"{material.name}  ({shaderDisplayName})";
            EditorGUILayout.LabelField($"目标材质：{matInfo}", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            ModelBoxStyles.BeginPrimaryButton();
            if (GUILayout.Button($"开始 Diff（创建副本并预览）", GUILayout.Height(32)))
            {
                StartSandbox(material, _activeSlotIndex);
            }
            ModelBoxStyles.EndPrimaryButton();
        }

        // ===================== Diff 活跃 =====================

        private void DrawDiffActiveSection()
        {
            // ---- 操作栏 ----
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();
            _isPreviewing = GUILayout.Toggle(_isPreviewing,
                _isPreviewing ? "预览 ON (Scene中可见)" : "预览 OFF",
                EditorStyles.toolbarButton, GUILayout.Height(24));
            if (EditorGUI.EndChangeCheck())
            {
                TogglePreview();
            }

            if (GUILayout.Button("重置", GUILayout.Width(50), GUILayout.Height(24)))
            {
                ResetSandbox();
            }

            _showOnlyDifferences = GUILayout.Toggle(_showOnlyDifferences, "仅差异", EditorStyles.toolbarButton, GUILayout.Width(54));
            _showAllProperties = GUILayout.Toggle(_showAllProperties, new GUIContent("显示隐藏", "显示被 keyword 隐藏的属性（如 Normal Map、Emission 等）"), EditorStyles.toolbarButton, GUILayout.Width(64));

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField($"差异: {_diffCount} / 共: {_totalCount}", EditorStyles.miniLabel, GUILayout.Width(90));

            EditorGUILayout.EndHorizontal();

            // 关键 keyword 快速启用（当有隐藏属性或"显示隐藏"开启时显示）
            if (_sandboxMaterial != null && HasHiddenProperties())
            {
                if (!_showAllProperties)
                {
                    // 提示用户有隐藏属性
                    int hiddenCount = CountHiddenProperties();
                    EditorGUILayout.HelpBox(
                        $"{hiddenCount} 个属性被 keyword 隐藏。点击「显示隐藏」或使用下方 keyword 按钮解锁。",
                        MessageType.Info);
                }
                DrawKeywordToggles();
            }

            EditorGUILayout.Space(4);

            // ---- 搜索栏 ----
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("搜索", GUILayout.Width(32));
            _searchFilter = EditorGUILayout.TextField(_searchFilter);
            if (GUILayout.Button("X", GUILayout.Width(20)))
                _searchFilter = "";
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // ---- 属性编辑列表 ----
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));
            DrawPropertyList();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(8);

            // ---- 底部操作按钮 ----
            DrawBottomActions();
        }

        private void DrawPropertyList()
        {
            _diffCount = 0;
            _totalCount = 0;
            int drawnCount = 0;

            if (_originalMaterial == null || _sandboxMaterial == null) return;
            var shader = _originalMaterial.shader;
            if (shader == null) return;

            int count = shader.GetPropertyCount();
            string filterLower = _searchFilter?.ToLower() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(_searchFilter);

            for (int i = 0; i < count; i++)
            {
                var flags = shader.GetPropertyFlags(i);
                bool isHidden = (flags & ShaderPropertyFlags.HideInInspector) != 0;

                // "全部" 开关：显示所有属性（包括被 keyword 隐藏的）
                if (isHidden && !_showAllProperties) continue;

                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);

                if (hasFilter && !name.ToLower().Contains(filterLower)) continue;

                bool differs = ValuesDiffer(name, type);
                _totalCount++;
                if (differs) _diffCount++;

                if (_showOnlyDifferences && !differs) continue;

                DrawPropertyRow(i, name, type, differs);
                drawnCount++;
            }

            // 空列表提示
            if (drawnCount == 0)
            {
                if (_totalCount == 0)
                    EditorGUILayout.HelpBox("此 Shader 没有可编辑的属性。", MessageType.Info);
                else if (_showOnlyDifferences)
                    EditorGUILayout.HelpBox("所有属性值相同，没有差异。修改上方参数后再切回「仅差异」查看。", MessageType.Info);
                else if (hasFilter)
                    EditorGUILayout.HelpBox("搜索无结果。", MessageType.Info);
            }
        }

        private void DrawPropertyRow(int index, string name, ShaderPropertyType type, bool differs)
        {
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = differs ? ModelBoxStyles.DiffMismatchColor : ModelBoxStyles.DiffMatchColor;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(name, EditorStyles.boldLabel);

            var prevColor = GUI.contentColor;
            GUI.contentColor = ModelBoxStyles.GetPropertyTypeLabelColor(type);
            EditorGUILayout.LabelField(type.ToString(), EditorStyles.miniLabel, GUILayout.Width(55));
            GUI.contentColor = prevColor;

            // 标记被 keyword 控制的隐藏属性（显示控制 keyword 名称）
            if (_showAllProperties && _originalMaterial != null)
            {
                var flags = _originalMaterial.shader.GetPropertyFlags(index);
                if ((flags & ShaderPropertyFlags.HideInInspector) != 0)
                {
                    string gateKeyword = FindGatingKeyword(name);
                    var pc2 = GUI.contentColor;
                    GUI.contentColor = new Color(1f, 0.6f, 0.2f);
                    string label = !string.IsNullOrEmpty(gateKeyword) ? $"🔒{gateKeyword}" : "HIDDEN";
                    EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(100));
                    GUI.contentColor = pc2;
                }
            }

            if (differs)
            {
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 0.7f, 0.3f)
                    : new Color(0.75f, 0.4f, 0.1f);
                EditorGUILayout.LabelField("DIFF", EditorStyles.miniLabel, GUILayout.Width(35));
                GUI.contentColor = prevColor;
            }

            // 单属性重置按钮（仅在差异时显示）
            if (differs && _originalMaterial != null)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("↺", GUILayout.Width(22), GUILayout.Height(16)))
                {
                    CopyPropertyValue(_originalMaterial, _sandboxMaterial, name, type);
                }
            }

            EditorGUILayout.EndHorizontal();

            switch (type)
            {
                case ShaderPropertyType.Color: DrawEditableColor(name); break;
                case ShaderPropertyType.Vector: DrawEditableVector(name); break;
                case ShaderPropertyType.Float: DrawEditableFloat(name); break;
                case ShaderPropertyType.Range: DrawEditableRange(name, index); break;
                case ShaderPropertyType.Texture: DrawEditableTexture(name); break;
                case ShaderPropertyType.Int: DrawEditableInt(name); break;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawEditableColor(string name)
        {
            EditorGUI.BeginChangeCheck();
            var newVal = EditorGUILayout.ColorField(_sandboxMaterial.GetColor(name));
            if (EditorGUI.EndChangeCheck()) _sandboxMaterial.SetColor(name, newVal);
        }

        private void DrawEditableVector(string name)
        {
            EditorGUI.BeginChangeCheck();
            // [fix] Vector4Field 水平溢出，改用两行 Vector2Field
            var curVec = _sandboxMaterial.GetVector(name);
            var xy = EditorGUILayout.Vector2Field($"{name} (XY)", new Vector2(curVec.x, curVec.y));
            var zw = EditorGUILayout.Vector2Field($"{name} (ZW)", new Vector2(curVec.z, curVec.w));
            var newVal = new Vector4(xy.x, xy.y, zw.x, zw.y);
            if (EditorGUI.EndChangeCheck()) _sandboxMaterial.SetVector(name, newVal);
        }

        private void DrawEditableFloat(string name)
        {
            EditorGUI.BeginChangeCheck();
            float newVal = EditorGUILayout.FloatField(_sandboxMaterial.GetFloat(name));
            if (EditorGUI.EndChangeCheck()) _sandboxMaterial.SetFloat(name, newVal);
        }

        private void DrawEditableRange(string name, int propIndex)
        {
            var shader = _originalMaterial.shader;
            Vector2 limits = new Vector2(0, 1);
            if (shader != null && shader.GetPropertyType(propIndex) == ShaderPropertyType.Range)
                limits = shader.GetPropertyRangeLimits(propIndex);

            EditorGUI.BeginChangeCheck();
            float newVal = EditorGUILayout.Slider(_sandboxMaterial.GetFloat(name), limits.x, limits.y);
            if (EditorGUI.EndChangeCheck()) _sandboxMaterial.SetFloat(name, newVal);
        }

        private void DrawEditableTexture(string name)
        {
            EditorGUI.BeginChangeCheck();
            var newVal = (Texture)EditorGUILayout.ObjectField(_sandboxMaterial.GetTexture(name), typeof(Texture), false);
            if (EditorGUI.EndChangeCheck()) _sandboxMaterial.SetTexture(name, newVal);
        }

        private void DrawEditableInt(string name)
        {
            EditorGUI.BeginChangeCheck();
            int newVal = EditorGUILayout.IntField(_sandboxMaterial.GetInt(name));
            if (EditorGUI.EndChangeCheck()) _sandboxMaterial.SetInt(name, newVal);
        }

        // ===================== Keyword 快速启用 =====================

        /// <summary>
        /// 显示常见 URP keyword 快速切换按钮。
        /// 这些 keyword 控制材质属性的可见性（如 _METALLICGLOSSMAP 启用后 Metallic Map 才可见）。
        /// </summary>
        private void DrawKeywordToggles()
        {
            if (_sandboxMaterial == null) return;
            // [fix v0.4.1] shader 可能为 null
            var sandboxShader = _sandboxMaterial.shader;
            if (sandboxShader == null) return;

            // 常见 URP keyword 及其控制的属性
            var keywordGroups = new (string keyword, string label, string tooltip)[]
            {
                ("_METALLICGLOSSMAP",    "Metallic Map",   "启用 Metallic/Smoothness 贴图通道"),
                ("_NORMALMAP",           "Normal Map",     "启用法线贴图通道"),
                ("_EMISSION",            "Emission",       "启用自发光贴图和颜色"),
                ("_OCCLUSIONMAP",        "Occlusion Map",  "启用遮蔽贴图（AO）"),
                ("_PARALLAXMAP",         "Height Map",     "启用视差贴图（Height）"),
                ("_DETAIL_MULX2",        "Detail Map",     "启用细节贴图通道"),
                ("_SPECGLOSSMAP",        "Specular Map",   "切换到 Specular 工作流贴图"),
                ("_CLEARCOAT",           "Clear Coat",     "启用 Clear Coat 涂层"),
            };

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("解锁属性：", EditorStyles.miniLabel, GUILayout.Width(64));

            foreach (var (keyword, label, tooltip) in keywordGroups)
            {
                // 检查 shader 是否定义了此 keyword（没有定义的 keyword 按钮无意义）
                var localKw = sandboxShader.keywordSpace.FindKeyword(keyword);
                if (!localKw.isValid)
                    continue;

                bool isEnabled = _sandboxMaterial.IsKeywordEnabled(keyword);
                var prevBg = GUI.backgroundColor;
                if (isEnabled) GUI.backgroundColor = new Color(0.3f, 0.7f, 0.3f, 0.7f);

                if (GUILayout.Button(new GUIContent(label, tooltip), EditorStyles.miniButton, GUILayout.Height(18)))
                {
                    if (isEnabled)
                        _sandboxMaterial.DisableKeyword(keyword);
                    else
                        _sandboxMaterial.EnableKeyword(keyword);
                }
                GUI.backgroundColor = prevBg;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        // ===================== 底部按钮 =====================

        private void DrawBottomActions()
        {
            EditorGUILayout.BeginHorizontal();

            if (_diffCount > 0)
            {
                ModelBoxStyles.BeginPrimaryButton();
                if (GUILayout.Button($"应用到原始（{_diffCount} 项差异）", GUILayout.Height(30)))
                {
                    if (EditorUtility.DisplayDialog("应用确认",
                        $"将 {_diffCount} 个差异属性写入原始材质？\n可通过 Ctrl+Z 撤销。",
                        "确定", "取消"))
                    {
                        ApplyToOriginal();
                    }
                }
                ModelBoxStyles.EndPrimaryButton();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                GUILayout.Button("无差异", GUILayout.Height(30));
                EditorGUI.EndDisabledGroup();
            }

            ModelBoxStyles.BeginDangerButton();
            if (GUILayout.Button("丢弃", GUILayout.Width(80), GUILayout.Height(30)))
            {
                if (_diffCount == 0 || EditorUtility.DisplayDialog("丢弃确认",
                    $"丢弃 {_diffCount} 项差异并恢复原始材质？",
                    "确定丢弃", "取消"))
                {
                    DiscardSandbox();
                }
            }
            ModelBoxStyles.EndDangerButton();

            EditorGUILayout.EndHorizontal();
        }

        // ===================== 核心逻辑 =====================

        private void StartSandbox(Material original, int slotIndex)
        {
            // [fix v0.6.x] 互斥保护：先关闭选区调试（其 RestoreOriginalMaterials 会恢复原始材质），
            // 再做沙盒快照 — 否则选区调试会把沙盒材质存为"原始材质"，恢复链互相污染
            var selMgr = ModelBoxSelectionManager.Instance;
            if (selMgr != null && selMgr.CurrentMode != SelectionDebugMode.None)
            {
                Debug.Log("[ModelBox] 材质沙盒启动：已自动关闭选区调试（两者互斥，保护材质恢复链）。");
                selMgr.SetMode(SelectionDebugMode.None);
            }

            // 创建临时副本
            _sandboxMaterial = new Material(original)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = original.name + "_Sandbox"
            };

            // 快照原始状态
            _originalMaterial = original;
            _originalMatName = original.name;
            _originalShaderName = original.shader != null ? original.shader.name : "Unknown";
            _originalShader = original.shader;
            _activeSlotIndex = slotIndex;

            // [fix v0.4] 收集所有需要替换的 Renderer（主 Renderer + LODGroup 各级别 Renderer）
            _allTargetRenderers.Clear();
            _allSavedOriginals.Clear();
            CollectAllRenderers(_targetRenderer, _allTargetRenderers);

            // 保存每个 Renderer 的原始材质数组并替换【持有同实例材质】的槽位
            // [fix v0.6.x] 不再盲目按 slotIndex 替换所有收集到的 Renderer —— LOD 各级别/多部件的
            // 材质布局可能不同，盲目替换会把 A 材质的克隆错误覆盖到持有 B 材质的槽位；
            // 按 ReferenceEquals 匹配所有引用该材质实例的槽位（材质级 Diff 语义：该材质的所有
            // 引用处同步预览），完全不持有该材质的 Renderer 保持原样
            foreach (var rend in _allTargetRenderers)
            {
                var originals = rend.sharedMaterials;
                _allSavedOriginals.Add(originals);

                var mats = (Material[])originals.Clone();
                bool replaced = false;
                for (int i = 0; i < originals.Length; i++)
                {
                    if (ReferenceEquals(originals[i], original))
                    {
                        mats[i] = _sandboxMaterial;
                        replaced = true;
                    }
                }
                if (!replaced && rend == _targetRenderer && slotIndex < mats.Length)
                    mats[slotIndex] = _sandboxMaterial; // 防御：主 Renderer 至少替换选中槽位
                rend.sharedMaterials = mats;
            }

            // [fix v0.4.1] _savedOriginals 必须引用原始材质数组，而非替换后的
            // _allSavedOriginals[0] 是主 Renderer 的原始材质（在替换前保存的）
            _savedOriginals = _allSavedOriginals.Count > 0 ? _allSavedOriginals[0] : _targetRenderer.sharedMaterials;
            _isPreviewing = true;

            // 同步静态镜像（域重载安全网）
            _sandboxActiveRenderer = _targetRenderer;
            _sandboxActiveMaterial = _sandboxMaterial;
            _sandboxSavedOriginals = _allSavedOriginals.Count > 0 ? _allSavedOriginals[0] : null;
            _sandboxAllRenderers = new List<Renderer>(_allTargetRenderers);
            _sandboxAllSavedOriginals = new List<Material[]>(_allSavedOriginals);
            s_sandboxActive = true; // [fix v0.6.x] 互斥标志：沙盒已接管这些 Renderer 的材质

            // [fix v0.4] 启动后立即触发 SceneView 重绘，否则用户看不到材质替换效果
            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        /// <summary>
        /// [fix v0.4] 收集主 Renderer 及 LODGroup 下所有 LOD 级别的 Renderer。
        /// 确保沙盒预览在所有 LOD 级别上生效。
        /// </summary>
        private static void CollectAllRenderers(Renderer primary, List<Renderer> results)
        {
            if (primary == null) return;
            results.Add(primary);

            // 检查是否有 LODGroup（可能在同一物体或父级）
            var lodGroup = primary.GetComponentInParent<LODGroup>();
            if (lodGroup == null) return;

            var lods = lodGroup.GetLODs();
            foreach (var lod in lods)
            {
                foreach (var rend in lod.renderers)
                {
                    if (rend != null && !results.Contains(rend))
                        results.Add(rend);
                }
            }
        }

        private void TogglePreview()
        {
            if (_targetRenderer == null) return;

            if (_isPreviewing)
            {
                // [fix v0.4] 开启预览：从 _savedOriginals 克隆（而非 sharedMaterials），
                // 避免 sharedMaterials getter 返回已替换的数组导致引用混乱
                // [fix v0.6.x] 按材质实例匹配替换（与 StartSandbox 一致），不再按 slotIndex 盲替
                for (int i = 0; i < _allTargetRenderers.Count; i++)
                {
                    var rend = _allTargetRenderers[i];
                    if (rend == null) continue;
                    var originals = i < _allSavedOriginals.Count ? _allSavedOriginals[i] : null;
                    if (originals == null) continue;

                    var mats = (Material[])originals.Clone();
                    bool replaced = false;
                    for (int s = 0; s < originals.Length; s++)
                    {
                        if (ReferenceEquals(originals[s], _originalMaterial))
                        {
                            mats[s] = _sandboxMaterial;
                            replaced = true;
                        }
                    }
                    if (!replaced && rend == _targetRenderer && _activeSlotIndex < mats.Length)
                        mats[_activeSlotIndex] = _sandboxMaterial;
                    rend.sharedMaterials = mats;
                }
                EditorApplication.delayCall += () => SceneView.RepaintAll();
            }
            else
            {
                // 关闭预览：恢复所有 Renderer 的原始材质数组
                for (int i = 0; i < _allTargetRenderers.Count; i++)
                {
                    var rend = _allTargetRenderers[i];
                    if (rend == null) continue;
                    var originals = i < _allSavedOriginals.Count ? _allSavedOriginals[i] : null;
                    if (originals != null)
                        rend.sharedMaterials = originals;
                }
                EditorApplication.delayCall += () => SceneView.RepaintAll();
            }
        }

        private void ResetSandbox()
        {
            if (_originalMaterial == null || _sandboxMaterial == null) return;
            CopyAllProperties(_originalMaterial, _sandboxMaterial);
            // 同步 keyword 状态
            CopyKeywords(_originalMaterial, _sandboxMaterial);
        }

        private void ApplyToOriginal()
        {
            if (_originalMaterial == null || _sandboxMaterial == null) return;

            Undo.RecordObject(_originalMaterial, "Material Sandbox Apply");

            var shader = _originalMaterial.shader;
            // [fix v0.4.1] shader 可能为 null
            if (shader == null)
            {
                Debug.LogWarning("[ModelBox] 无法应用到原始：材质 Shader 为空。");
                return;
            }
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                if (ValuesDiffer(name, type))
                    CopyPropertyValue(_sandboxMaterial, _originalMaterial, name, type);
            }

            // 传递 keyword 状态（修复：之前 keyword 变更会丢失）
            CopyKeywords(_sandboxMaterial, _originalMaterial);

            EditorUtility.SetDirty(_originalMaterial);
            ResetSandbox();
        }

        /// <summary>丢弃沙盒：恢复 Renderer、销毁临时材质、重置状态。</summary>
        public void DiscardSandbox()
        {
            // [fix v0.4] 恢复所有 Renderer 的原始材质（含 LODGroup 各级别）
            for (int i = 0; i < _allTargetRenderers.Count; i++)
            {
                var rend = _allTargetRenderers[i];
                if (rend == null) continue;
                var originals = i < _allSavedOriginals.Count ? _allSavedOriginals[i] : null;
                if (originals != null)
                {
                    try { rend.sharedMaterials = originals; }
                    catch { /* Renderer 可能已被销毁 */ }
                }
            }

            // 销毁临时材质
            if (_sandboxMaterial != null)
            {
                Object.DestroyImmediate(_sandboxMaterial);
                _sandboxMaterial = null;
            }

            _savedOriginals = null;
            _allTargetRenderers.Clear();
            _allSavedOriginals.Clear();
            _isPreviewing = false;
            _diffCount = 0;
            _totalCount = 0;

            // 清理静态镜像
            _sandboxActiveRenderer = null;
            _sandboxActiveMaterial = null;
            _sandboxSavedOriginals = null;
            _sandboxAllRenderers = null; // [fix v0.6.x] 丢弃时同步清空多 Renderer 镜像（原仅域重载时清理）
            _sandboxAllSavedOriginals = null;
            s_sandboxActive = false; // [fix v0.6.x] 解除互斥

            EditorApplication.delayCall += () => SceneView.RepaintAll();
        }

        public void Cleanup()
        {
            DiscardSandbox();
            _targetRenderer = null;
            _originalMaterial = null;
            _originalMatName = null;
            _originalShaderName = null;
            _originalShader = null;
        }

        // ===================== 辅助方法 =====================

        private void DrawEmptyState(string message)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(message, MessageType.Info);
        }

        private bool ValuesDiffer(string name, ShaderPropertyType type)
        {
            if (_originalMaterial == null || _sandboxMaterial == null) return false;
            if (!_originalMaterial.HasProperty(name) || !_sandboxMaterial.HasProperty(name)) return false;

            switch (type)
            {
                case ShaderPropertyType.Color: return _originalMaterial.GetColor(name) != _sandboxMaterial.GetColor(name);
                case ShaderPropertyType.Vector: return _originalMaterial.GetVector(name) != _sandboxMaterial.GetVector(name);
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: return Mathf.Abs(_originalMaterial.GetFloat(name) - _sandboxMaterial.GetFloat(name)) > 0.0001f;
                case ShaderPropertyType.Texture: return _originalMaterial.GetTexture(name) != _sandboxMaterial.GetTexture(name);
                case ShaderPropertyType.Int: return _originalMaterial.GetInt(name) != _sandboxMaterial.GetInt(name);
                default: return false;
            }
        }

        private void CopyAllProperties(Material src, Material dst)
        {
            var shader = src.shader;
            // [fix v0.4.1] shader 可能为 null
            if (shader == null) return;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                CopyPropertyValue(src, dst, name, shader.GetPropertyType(i));
            }
        }

        private void CopyPropertyValue(Material src, Material dst, string name, ShaderPropertyType type)
        {
            if (!src.HasProperty(name) || !dst.HasProperty(name)) return;
            switch (type)
            {
                case ShaderPropertyType.Color: dst.SetColor(name, src.GetColor(name)); break;
                case ShaderPropertyType.Vector: dst.SetVector(name, src.GetVector(name)); break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range: dst.SetFloat(name, src.GetFloat(name)); break;
                case ShaderPropertyType.Texture: dst.SetTexture(name, src.GetTexture(name)); break;
                case ShaderPropertyType.Int: dst.SetInt(name, src.GetInt(name)); break;
            }
        }

        /// <summary>查找控制指定属性的 keyword（基于 URP Lit shader 常见映射）。</summary>
        private static string FindGatingKeyword(string propertyName)
        {
            switch (propertyName)
            {
                case "_BumpMap":
                case "_BumpScale": return "_NORMALMAP";
                case "_MetallicGlossMap":
                case "_Smoothness": return "_METALLICGLOSSMAP";
                case "_EmissionMap":
                case "_EmissionColor": return "_EMISSION";
                case "_OcclusionMap":
                case "_OcclusionStrength": return "_OCCLUSIONMAP";
                case "_ParallaxMap":
                case "_Parallax": return "_PARALLAXMAP";
                case "_DetailAlbedoMap":
                case "_DetailNormalMap":
                case "_DetailAlbedoMapScale": return "_DETAIL_MULX2";
                case "_SpecGlossMap":
                case "_SpecColor": return "_SPECGLOSSMAP";
                case "_ClearCoatMap":
                case "_ClearCoatMask":
                case "_ClearCoatSmoothness": return "_CLEARCOAT";
                default: return null;
            }
        }

        private bool HasHiddenProperties()
        {
            if (_sandboxMaterial == null) return false;
            var shader = _sandboxMaterial.shader;
            if (shader == null) return false;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0)
                    return true;
            }
            return false;
        }

        private int CountHiddenProperties()
        {
            if (_sandboxMaterial == null) return 0;
            var shader = _sandboxMaterial.shader;
            if (shader == null) return 0;
            int result = 0;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0)
                    result++;
            }
            return result;
        }

        /// <summary>复制 keyword 状态（EnableKeyword / DisableKeyword）。</summary>
        private static void CopyKeywords(Material src, Material dst)
        {
            if (src == null || dst == null) return;
            var shader = src.shader;
            if (shader == null) return;

            foreach (var keyword in shader.keywordSpace.keywordNames)
            {
                if (src.IsKeywordEnabled(keyword))
                    dst.EnableKeyword(keyword);
                else
                    dst.DisableKeyword(keyword);
            }
        }

    }
}
