using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 材质参数沙盒。自动读取选中物体材质，创建临时副本用于实时对比调试。
    /// 支持：实时预览编辑效果、重置、应用到原始、丢弃。临时材质在丢弃/窗口关闭时清理。
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
                    // 域重载前确保 Renderer 材质恢复
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
                };
            }
        }

        // 域重载安全网用的静态引用（镜像实例字段）
        private static Renderer _sandboxActiveRenderer;
        private static Material _sandboxActiveMaterial;
        private static Material[] _sandboxSavedOriginals;
        // ---- 状态 ----
        private Renderer _targetRenderer;
        private Material _originalMaterial; // 启动沙盒时快照的原始材质引用
        private string _originalMatName;    // 原始材质名称（防御性备份）
        private string _originalShaderName; // 原始 Shader 名称
        private Shader _originalShader;     // 启动时的 Shader 引用（用于检测 Shader 变更）
        private Material _sandboxMaterial;  // 临时副本（HideAndDontSave）
        private Material[] _savedOriginals; // 保存的原始 sharedMaterials 数组
        private int _activeSlotIndex = 0;   // 当前编辑的材质槽索引
        private bool _isPreviewing;

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

            // [fix] 使用 GetComponentInChildren 支持父物体选中（如角色根节点）
            var renderer = selected.GetComponentInChildren<Renderer>();
            if (renderer == null)
            {
                DrawEmptyState("选中物体及其子级没有 Renderer 组件。");
                return;
            }

            var materials = renderer.sharedMaterials;
            if (materials.Length == 0)
            {
                DrawEmptyState("Renderer 没有材质槽。");
                return;
            }

            // 检测物体切换
            if (renderer != _targetRenderer)
            {
                _targetRenderer = renderer;
                _activeSlotIndex = 0;
            }

            DrawHeaderDirect(renderer, materials);
            DrawStartSection(renderer, materials);
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
            int slotCount = _targetRenderer != null ? _targetRenderer.sharedMaterials.Length : 1;
            string slotInfo = slotCount > 1 ? $"  |  槽位 [{_activeSlotIndex}/{slotCount - 1}]" : "";
            EditorGUILayout.LabelField(
                $"物体: {objName}{slotInfo}  |  材质: {_originalMatName}  |  Shader: {_originalShaderName}",
                EditorStyles.miniLabel);
            GUI.contentColor = pc;

            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, ModelBoxStyles.SeparatorColor);
            EditorGUILayout.Space(4);
        }

        /// <summary>沙盒未活跃时的头部（显示材质槽选择器）。</summary>
        private void DrawHeaderDirect(Renderer renderer, Material[] materials)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("材质沙盒", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            var pc = GUI.contentColor;
            GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
            string objName = renderer != null ? renderer.gameObject.name : "-";
            EditorGUILayout.LabelField(
                $"物体: {objName}  |  {materials.Length} 个材质槽",
                EditorStyles.miniLabel);
            GUI.contentColor = pc;

            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, ModelBoxStyles.SeparatorColor);
            EditorGUILayout.Space(4);

            // 材质槽选择器（多槽时显示）
            if (materials.Length > 1)
            {
                EditorGUILayout.LabelField("选择材质槽：", EditorStyles.boldLabel);
                for (int i = 0; i < materials.Length; i++)
                {
                    bool isActive = (i == _activeSlotIndex);
                    var prevBg = GUI.backgroundColor;
                    if (isActive) GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();

                    string matName = materials[i] != null ? materials[i].name : "(空)";
                    string shaderName = materials[i] != null && materials[i].shader != null
                        ? materials[i].shader.name : "Unknown";
                    if (GUILayout.Button($"[{i}] {matName}  ({shaderName})", EditorStyles.miniButton))
                    {
                        _activeSlotIndex = i;
                    }
                    GUI.backgroundColor = prevBg;
                }
                EditorGUILayout.Space(4);
            }
        }

        // ===================== 未开始 =====================

        private void DrawStartSection(Renderer renderer, Material[] materials)
        {
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

            string matInfo = $"{material.name}  ({material.shader.name})";
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
                var localKw = _sandboxMaterial.shader.keywordSpace.FindKeyword(keyword);
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

            // 保存 Renderer 原始材质数组并替换选中槽位
            if (_targetRenderer != null)
            {
                _savedOriginals = _targetRenderer.sharedMaterials;
                var mats = (Material[])_savedOriginals.Clone();
                if (slotIndex < mats.Length)
                    mats[slotIndex] = _sandboxMaterial;
                _targetRenderer.sharedMaterials = mats;
                _isPreviewing = true;
            }

            // 同步静态镜像（域重载安全网）
            _sandboxActiveRenderer = _targetRenderer;
            _sandboxActiveMaterial = _sandboxMaterial;
            _sandboxSavedOriginals = _savedOriginals;
        }

        private void TogglePreview()
        {
            if (_targetRenderer == null) return;

            if (_isPreviewing)
            {
                // 开启预览：将沙盒材质放入对应槽位
                var mats = _targetRenderer.sharedMaterials;
                if (_activeSlotIndex < mats.Length)
                    mats[_activeSlotIndex] = _sandboxMaterial;
                _targetRenderer.sharedMaterials = mats;
            }
            else
            {
                // 关闭预览：恢复原始材质数组
                if (_savedOriginals != null)
                    _targetRenderer.sharedMaterials = _savedOriginals;
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
            // 恢复 Renderer 原始材质
            if (_targetRenderer != null && _savedOriginals != null)
            {
                try { _targetRenderer.sharedMaterials = _savedOriginals; }
                catch { /* Renderer 可能已被销毁 */ }
            }

            // 销毁临时材质
            if (_sandboxMaterial != null)
            {
                Object.DestroyImmediate(_sandboxMaterial);
                _sandboxMaterial = null;
            }

            _savedOriginals = null;
            _isPreviewing = false;
            // 保持 _searchFilter（用户可能想用相同搜索重新开始）
            _diffCount = 0;
            _totalCount = 0;

            // 清理静态镜像
            _sandboxActiveRenderer = null;
            _sandboxActiveMaterial = null;
            _sandboxSavedOriginals = null;
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
