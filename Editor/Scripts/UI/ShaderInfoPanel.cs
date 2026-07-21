using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// Shader 信息面板。显示选中物体的 Keywords、Passes、Properties。
    /// 三个子标签页：Keywords / Passes / Properties。
    /// 所有 UI 渲染均有 try-catch 保护，确保单个控件异常不会导致整个面板崩溃。
    /// </summary>
    public class ShaderInfoPanel
    {
        private ShaderInfoData _cachedData;
        private Shader _lastShader;
        private Material _lastMaterial;
        private int _subTab;
        private Vector2 _scrollPos;
        private string _searchFilter = "";
        private string _keywordSearchFilter = "";
        private bool _showOnlyEnabledKeywords;
        private readonly string[] _subTabNames = { "Keywords", "Passes", "Properties" };

        public void Draw()
        {
            var selected = Selection.activeTransform;
            if (selected == null)
            {
                EditorGUILayout.HelpBox("请在 Scene 中选择一个物体。", MessageType.Info);
                return;
            }

            var renderer = selected.GetComponentInChildren<Renderer>();
            if (renderer == null)
            {
                EditorGUILayout.HelpBox("选中物体没有 Renderer 组件。", MessageType.Info);
                return;
            }

            var selManager = ModelBoxSelectionManager.Instance;
            var material = selManager != null ? selManager.GetOriginalMaterial(renderer) : renderer.sharedMaterial;
            if (material == null)
            {
                EditorGUILayout.HelpBox("材质为空。", MessageType.Warning);
                return;
            }

            var shader = material.shader;
            if (shader == null)
            {
                EditorGUILayout.HelpBox("Shader 为空。", MessageType.Warning);
                return;
            }

            // 缓存失效检测
            if (shader != _lastShader || material != _lastMaterial)
            {
                try
                {
                    _cachedData = ShaderInfoData.Analyze(shader, material);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ModelBox] ShaderInfoData.Analyze failed: {e.Message}\n{e.StackTrace}");
                    _cachedData = null;
                }
                _lastShader = shader;
                _lastMaterial = material;
            }

            // ---- Shader 信息头部（独立 try-catch，不阻断后续 UI） ----
            try
            {
                DrawShaderHeader(shader, material);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ModelBox] DrawShaderHeader error: {e.Message}");
                EditorGUILayout.HelpBox($"Shader 头部渲染异常: {e.Message}", MessageType.Warning);
            }

            // ---- 即使 _cachedData 为空也显示子标签栏（让用户看到面板结构） ----
            _subTab = GUILayout.Toolbar(_subTab, _subTabNames);

            // R7: 子标签页导航与内容分隔线
            var subNavSepRect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(subNavSepRect, ModelBoxStyles.NavSeparatorColor);
            EditorGUILayout.Space(6);

            if (_cachedData == null)
            {
                EditorGUILayout.HelpBox("Shader 信息解析失败。请尝试重新选中物体或检查 Shader 是否有编译错误。", MessageType.Warning);
                return;
            }

            // ---- 子标签内容（独立 try-catch） ----
            // [fix] 嵌套在外部 ScrollView 中时必须指定 MinHeight，否则塌缩到极小高度
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MinHeight(250));
            try
            {
                switch (_subTab)
                {
                    case 0: DrawKeywords(material); break;
                    case 1: DrawPasses(material); break;
                    case 2: DrawProperties(material); break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ModelBox] Sub-tab {_subTab} render error: {e.Message}\n{e.StackTrace}");
                EditorGUILayout.HelpBox($"渲染异常: {e.Message}", MessageType.Error);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawShaderHeader(Shader shader, Material material)
        {
            // F8: Shader 编译错误检测
            try
            {
                if (!shader.isSupported)
                {
                    EditorGUILayout.HelpBox(
                        $"Shader 编译失败: {shader.name}\n材质显示为粉色（Missing Shader）。",
                        MessageType.Error);
                    if (GUILayout.Button("打开 Shader 源码", GUILayout.Height(22)))
                    {
                        var path = AssetDatabase.GetAssetPath(shader);
                        if (!string.IsNullOrEmpty(path))
                            AssetDatabase.OpenAsset(shader);
                    }
                    EditorGUILayout.Space(4);
                }
            }
            catch
            {
                // isSupported 在极少数情况下可能失败，不影响后续渲染
            }

            // Shader 名称
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Shader", EditorStyles.boldLabel, GUILayout.Width(44));
            EditorGUILayout.SelectableLabel(_cachedData?.ShaderName ?? shader.name, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            // 概要行
            if (_cachedData != null)
            {
                EditorGUILayout.BeginHorizontal();
                var prevColor = GUI.contentColor;
                GUI.contentColor = EditorGUIUtility.isProSkin
                    ? new Color(0.7f, 0.7f, 0.7f)
                    : new Color(0.3f, 0.3f, 0.3f);
                EditorGUILayout.LabelField(
                    $"Passes: {_cachedData.EnabledPassCount}/{_cachedData.Passes.Count}  |  Keywords: {_cachedData.Keywords.Count}  |  Props: {_cachedData.Properties.Count}",
                    EditorStyles.miniLabel);
                GUI.contentColor = prevColor;
                EditorGUILayout.EndHorizontal();
            }

            // 分隔线
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, ModelBoxStyles.SeparatorColor);
            EditorGUILayout.Space(4);
        }

        // ==================== Keywords ====================

        private void DrawKeywords(Material material)
        {
            if (_cachedData.Keywords.Count == 0)
            {
                EditorGUILayout.HelpBox("此 Shader 没有定义任何 Keyword。", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{_cachedData.EnabledKeywordCount} / {_cachedData.Keywords.Count} 启用", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            _showOnlyEnabledKeywords = GUILayout.Toggle(_showOnlyEnabledKeywords, "仅显示已启用", EditorStyles.miniButton);
            EditorGUILayout.EndHorizontal();

            // 搜索框
            ModelBoxStyles.DrawSearchBar(ref _keywordSearchFilter);
            EditorGUILayout.Space(4);

            string filter = _keywordSearchFilter?.Trim().ToLowerInvariant();

            for (int i = 0; i < _cachedData.Keywords.Count; i++)
            {
                var kw = _cachedData.Keywords[i];

                // 搜索过滤
                if (!string.IsNullOrEmpty(filter) && !kw.Name.ToLowerInvariant().Contains(filter))
                    continue;

                // 仅显示已启用过滤
                if (_showOnlyEnabledKeywords && !kw.IsEnabled)
                    continue;

                try
                {
                    DrawKeywordRow(material, kw);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ModelBox] Keyword row error ({kw.Name}): {e.Message}");
                }
            }
        }

        private void DrawKeywordRow(Material material, ShaderInfoData.KeywordInfo kw)
        {
            // Unity 内置 keyword 工具提示（鼠标悬停时显示）
            string tooltip = GetKeywordTooltip(kw.Name);

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = kw.IsEnabled
                ? new Color(0.2f, 0.5f, 0.2f, 0.5f)
                : new Color(0, 0, 0, 0);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            EditorGUI.BeginChangeCheck();
            bool newEnabled = GUILayout.Toggle(kw.IsEnabled, "", GUILayout.Width(20));
            if (EditorGUI.EndChangeCheck())
            {
                _cachedData.ApplyKeywordChange(material, kw.Name, newEnabled);
                EditorApplication.delayCall += () => SceneView.RepaintAll();
            }

            // Keyword 名称（带工具提示）
            var labelContent = new GUIContent(kw.Name, tooltip);
            EditorGUILayout.LabelField(labelContent, EditorStyles.label);

            var prevColor = GUI.contentColor;
            GUI.contentColor = kw.IsOverridable
                ? (EditorGUIUtility.isProSkin ? new Color(0.5f, 0.8f, 1f) : new Color(0.2f, 0.45f, 0.7f))
                : (EditorGUIUtility.isProSkin ? new Color(1f, 0.8f, 0.5f) : new Color(0.65f, 0.45f, 0.15f));
            EditorGUILayout.LabelField(kw.IsOverridable ? "feature" : "compile", EditorStyles.miniLabel, GUILayout.Width(50));
            GUI.contentColor = prevColor;

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 获取 Unity 内置 keyword 的功能描述。用于鼠标悬停工具提示。
        /// </summary>
        private static string GetKeywordTooltip(string keyword)
        {
            switch (keyword)
            {
                // ===== URP 光照 =====
                case "_MAIN_LIGHT_SHADOWS": return "主光源阴影（Cascade 1）";
                case "_MAIN_LIGHT_SHADOWS_CASCADE": return "主光源级联阴影";
                case "_MAIN_LIGHT_SHADOWS_SCREEN": return "主光源屏幕空间阴影";
                case "_ADDITIONAL_LIGHTS": return "额外光源（点光/聚光）";
                case "_ADDITIONAL_LIGHT_SHADOWS": return "额外光源阴影";
                case "_ADDITIONAL_LIGHT_SHADOWS_CASCADE": return "额外光源级联阴影";
                case "_SHADOWS_SOFT": return "软阴影（PCF 滤波）";
                case "_SHADOWS_SOFT_LOW": return "软阴影（低质量）";
                case "_SHADOWS_SOFT_MEDIUM": return "软阴影（中质量）";
                case "_SHADOWS_SOFT_HIGH": return "软阴影（高质量）";
                case "_MIXED_LIGHTING_SUBTRACTIVE": return "Mixed Lighting 减法模式";

                // ===== URP 渲染特性 =====
                case "_ENVIRONMENTREFLECTIONS_OFF": return "关闭环境反射";
                case "_SPECULARHIGHLIGHTS_OFF": return "关闭镜面高光";
                case "_RECEIVE_SHADOWS_OFF": return "不接收阴影";
                case "_CASTING_PUNCTUAL_LIGHT_SHADOW": return "点光源/聚光灯阴影投射";

                // ===== URP 纹理 =====
                case "_NORMALMAP": return "法线贴图";
                case "_PARALLAXMAP": return "视差贴图";
                case "_DETAIL_MULX2": return "细节贴图（Detail Albedo × 2）";
                case "_DETAIL_SCALED": return "细节贴图（Detail Albedo scaled）";
                case "_EMISSION": return "自发光";
                case "_METALLICGLOSSMAP": return "金属度贴图（Metallic Smoothness Map）";
                case "_SPECGLOSSMAP": return "高光贴图（Specular Glossiness Map）";
                case "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A": return "Smoothness 从 Albedo Alpha 通道读取";
                case "_OCCLUSIONMAP": return "遮蔽贴图（AO Map）";
                case "_CLEARCOAT": return "Clear Coat 涂层";
                case "_CLEARCOATMAP": return "Clear Coat 涂层贴图";

                // ===== Surface Type =====
                case "_SURFACE_TYPE_TRANSPARENT": return "透明材质类型";
                case "_ALPHAPREMULTIPLY_ON": return "Alpha 预乘混合";
                case "_ALPHAMODULATE_ON": return "Alpha 调制混合";
                case "_ALPHATEST_ON": return "Alpha Test（裁切）";
                case "_ALPHABLEND_ON": return "Alpha Blend";

                // ===== GPU Instancing =====
                case "INSTANCING_ON": return "GPU 实例化";

                // ===== 常见自定义 =====
                case "_SPECULAR_SETUP": return "Specular 工作流（非 Metallic）";

                default: return "";
            }
        }

        // ==================== Passes ====================

        private void DrawPasses(Material material)
        {
            if (_cachedData.Passes.Count == 0)
            {
                EditorGUILayout.HelpBox("此 Shader 没有可识别的 Pass。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"{_cachedData.EnabledPassCount} / {_cachedData.Passes.Count} passes 启用", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            for (int i = 0; i < _cachedData.Passes.Count; i++)
            {
                var pass = _cachedData.Passes[i];
                try
                {
                    var prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = pass.IsEnabled
                        ? new Color(0.2f, 0.5f, 0.2f, 0.5f)
                        : new Color(0.5f, 0.2f, 0.2f, 0.5f);

                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    GUI.backgroundColor = prevBg;

                    EditorGUI.BeginChangeCheck();
                    bool newEnabled = GUILayout.Toggle(pass.IsEnabled, "", GUILayout.Width(20));
                    if (EditorGUI.EndChangeCheck())
                    {
                        _cachedData.ApplyPassChange(material, pass.Name, newEnabled);
                        EditorApplication.delayCall += () => SceneView.RepaintAll();
                    }

                    EditorGUILayout.LabelField(pass.Name, EditorStyles.label);

                    var prevColor = GUI.contentColor;
                    GUI.contentColor = pass.IsEnabled
                        ? (EditorGUIUtility.isProSkin ? new Color(0.5f, 0.9f, 0.5f) : new Color(0.15f, 0.5f, 0.15f))
                        : (EditorGUIUtility.isProSkin ? new Color(0.9f, 0.4f, 0.4f) : new Color(0.7f, 0.2f, 0.2f));
                    EditorGUILayout.LabelField(pass.IsEnabled ? "ON" : "OFF", EditorStyles.miniLabel, GUILayout.Width(24));
                    GUI.contentColor = prevColor;

                    EditorGUILayout.EndHorizontal();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ModelBox] Pass row error ({pass.Name}): {e.Message}");
                }
            }
        }

        // ==================== Properties ====================

        private void DrawProperties(Material material)
        {
            if (_cachedData.Properties.Count == 0)
            {
                EditorGUILayout.HelpBox("此 Shader 没有可显示的属性。", MessageType.Info);
                return;
            }

            // 搜索栏（I5: 使用集中化组件）
            ModelBoxStyles.DrawSearchBar(ref _searchFilter);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField($"{_cachedData.Properties.Count} properties", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            string filterLower = _searchFilter?.ToLower() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(_searchFilter);

            int drawnCount = 0;
            for (int i = 0; i < _cachedData.Properties.Count; i++)
            {
                var prop = _cachedData.Properties[i];

                // [fix] Description 可能为 null（某些第三方 shader），添加 null 安全访问
                if (hasFilter &&
                    !prop.Name.ToLower().Contains(filterLower) &&
                    !(prop.Description?.ToLower().Contains(filterLower) ?? false))
                    continue;

                try
                {
                    DrawPropertyRow(material, prop);
                    drawnCount++;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ModelBox] Property row error ({prop.Name}): {e.Message}");
                }
            }

            if (drawnCount == 0 && hasFilter)
            {
                EditorGUILayout.HelpBox("搜索无结果。", MessageType.Info);
            }
        }

        private void DrawPropertyRow(Material material, ShaderInfoData.PropertyInfo prop)
        {
            if (!material.HasProperty(prop.Name)) return;

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = ModelBoxStyles.GetPropertyTypeColor(prop.Type);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            // 第一行：名称 + 类型标签
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(prop.Name, EditorStyles.boldLabel);

            var prevColor = GUI.contentColor;
            GUI.contentColor = ModelBoxStyles.GetPropertyTypeLabelColor(prop.Type);
            EditorGUILayout.LabelField(prop.Type.ToString(), EditorStyles.miniLabel, GUILayout.Width(55));
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();

            // 描述
            if (!string.IsNullOrEmpty(prop.Description) && prop.Description != prop.Name)
            {
                EditorGUILayout.LabelField(prop.Description, EditorStyles.wordWrappedMiniLabel);
            }

            // 当前值（可编辑）
            DrawPropertyValueEditable(material, prop);

            // 纹理预览按钮
            if (prop.Type == ShaderPropertyType.Texture)
            {
                var tex = material.GetTexture(prop.Name);
                if (tex != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("查看纹理", GUILayout.Width(100), GUILayout.Height(20)))
                    {
                        TexturePreviewWindow.ShowWindow(tex, prop.Name);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            // NEW-2: Albedo 亮度检查（_BaseColor / _Color）
            if (prop.Type == UnityEngine.Rendering.ShaderPropertyType.Color &&
                (prop.Name == "_BaseColor" || prop.Name == "_Color"))
            {
                var color = material.GetColor(prop.Name);
                float luminance = color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
                string rating;
                Color ratingColor;
                bool proSkin = EditorGUIUtility.isProSkin;
                if (luminance < 0.02f) { rating = "过暗 (接近纯黑)"; ratingColor = proSkin ? Color.red : new Color(0.8f, 0.1f, 0.1f); }
                else if (luminance < 0.05f) { rating = "偏暗"; ratingColor = proSkin ? new Color(1f, 0.7f, 0.3f) : new Color(0.75f, 0.4f, 0.1f); }
                else if (luminance > 0.9f) { rating = "过亮 (接近纯白，可能过曝)"; ratingColor = proSkin ? Color.red : new Color(0.8f, 0.1f, 0.1f); }
                else if (luminance > 0.8f) { rating = "偏亮"; ratingColor = proSkin ? new Color(1f, 0.7f, 0.3f) : new Color(0.75f, 0.4f, 0.1f); }
                else { rating = "正常范围"; ratingColor = EditorGUIUtility.isProSkin ? Color.green : new Color(0.1f, 0.5f, 0.1f); }

                var prevC = GUI.contentColor;
                GUI.contentColor = ratingColor;
                EditorGUILayout.LabelField($"Albedo 亮度: {luminance:F3} — {rating}", EditorStyles.miniLabel);
                GUI.contentColor = prevC;
            }

            // NEW-3: 金属度/光滑度可视化条
            if (prop.Type == UnityEngine.Rendering.ShaderPropertyType.Float ||
                prop.Type == UnityEngine.Rendering.ShaderPropertyType.Range)
            {
                if (prop.Name == "_Metallic" || prop.Name == "_Smoothness" || prop.Name == "_Glossiness")
                {
                    float val = material.GetFloat(prop.Name);
                    var barRect = EditorGUILayout.GetControlRect(false, 8);
                    EditorGUI.DrawRect(barRect, new Color(0.2f, 0.2f, 0.2f, 0.5f));
                    var fillRect = new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(val), barRect.height);
                    EditorGUI.DrawRect(fillRect, prop.Name == "_Metallic"
                        ? new Color(0.8f, 0.8f, 0.2f, 0.8f)
                        : new Color(0.3f, 0.7f, 1f, 0.8f));
                    EditorGUILayout.LabelField($"{val:P0}", EditorStyles.miniLabel);
                }
            }

            // Flags
            DrawFlags(prop.Flags);

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawPropertyValueEditable(Material material, ShaderInfoData.PropertyInfo prop)
        {
            try
            {
                switch (prop.Type)
                {
                    case ShaderPropertyType.Color:
                        EditorGUI.BeginChangeCheck();
                        var newColor = EditorGUILayout.ColorField(prop.Name, material.GetColor(prop.Name));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Edit Color Property");
                            material.SetColor(prop.Name, newColor);
                        }
                        break;

                    case ShaderPropertyType.Vector:
                        EditorGUI.BeginChangeCheck();
                        // [fix] 窄窗口下 Vector4Field 水平溢出。用两行 Vector2Field 代替。
                        var curVec = material.GetVector(prop.Name);
                        var xy = EditorGUILayout.Vector2Field($"{prop.Name} (XY)", new Vector2(curVec.x, curVec.y));
                        var zw = EditorGUILayout.Vector2Field($"{prop.Name} (ZW)", new Vector2(curVec.z, curVec.w));
                        var newVec = new Vector4(xy.x, xy.y, zw.x, zw.y);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Edit Vector Property");
                            material.SetVector(prop.Name, newVec);
                        }
                        break;

                    case ShaderPropertyType.Float:
                        EditorGUI.BeginChangeCheck();
                        var newFloat = EditorGUILayout.FloatField(prop.Name, material.GetFloat(prop.Name));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Edit Float Property");
                            material.SetFloat(prop.Name, newFloat);
                        }
                        break;

                    case ShaderPropertyType.Range:
                        EditorGUI.BeginChangeCheck();
                        var newRange = EditorGUILayout.Slider(
                            prop.Name,
                            material.GetFloat(prop.Name),
                            prop.RangeLimits.x,
                            prop.RangeLimits.y);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Edit Range Property");
                            material.SetFloat(prop.Name, newRange);
                        }
                        break;

                    case ShaderPropertyType.Texture:
                        EditorGUI.BeginChangeCheck();
                        // [fix] 显式标签 + 限制宽度，防止窄窗口溢出
                        var newTex = (Texture)EditorGUILayout.ObjectField(prop.Name, material.GetTexture(prop.Name), typeof(Texture), false);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Edit Texture Property");
                            material.SetTexture(prop.Name, newTex);
                        }
                        break;

                    case ShaderPropertyType.Int:
                        EditorGUI.BeginChangeCheck();
                        var newInt = EditorGUILayout.IntField(prop.Name, material.GetInt(prop.Name));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(material, "Edit Int Property");
                            material.SetInt(prop.Name, newInt);
                        }
                        break;
                }
            }
            catch (System.Exception e)
            {
                // 某些材质属性类型可能不匹配，显示错误而不是崩溃
                EditorGUILayout.LabelField($"[读取失败: {e.Message}]", EditorStyles.miniLabel);
            }
        }

        // ==================== 辅助方法 ====================

        private void DrawFlags(ShaderPropertyFlags flags)
        {
            if (flags == ShaderPropertyFlags.None) return;

            EditorGUILayout.BeginHorizontal();
            var prevColor = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.7f, 0.7f, 0.7f)
                : new Color(0.4f, 0.4f, 0.4f);

            if ((flags & ShaderPropertyFlags.HDR) != 0)
                EditorGUILayout.LabelField("HDR", EditorStyles.miniLabel, GUILayout.Width(30));
            if ((flags & ShaderPropertyFlags.Gamma) != 0)
                EditorGUILayout.LabelField("Gamma", EditorStyles.miniLabel, GUILayout.Width(42));
            if ((flags & ShaderPropertyFlags.PerRendererData) != 0)
                EditorGUILayout.LabelField("PerRenderer", EditorStyles.miniLabel, GUILayout.Width(65));
            if ((flags & ShaderPropertyFlags.NoScaleOffset) != 0)
                EditorGUILayout.LabelField("NoScale", EditorStyles.miniLabel, GUILayout.Width(50));
            if ((flags & ShaderPropertyFlags.Normal) != 0)
                EditorGUILayout.LabelField("Normal", EditorStyles.miniLabel, GUILayout.Width(45));
            if ((flags & ShaderPropertyFlags.HideInInspector) != 0)
                EditorGUILayout.LabelField("Hidden", EditorStyles.miniLabel, GUILayout.Width(45));

            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }
    }
}
