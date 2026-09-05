using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// modelBox 主 EditorWindow。
    /// 入口：Tools > modelBox。
    /// 左侧边栏导航布局：检查 / 场景 / 物体 / 沙盒 / 设置。
    /// </summary>
    public class ModelBoxWindow : EditorWindow
    {
        [MenuItem("Tools/modelBox")]
        public static void Open()
        {
            var window = GetWindow<ModelBoxWindow>("modelBox");
            window.minSize = new Vector2(450, 500);
            window.titleContent.image = LoadLogo();
            window.Show();
        }

        // ==================== Version ====================

        private static string _cachedVersion;
        private static Texture2D _logoTexture;

        private static string GetPackageVersion()
        {
            if (_cachedVersion != null) return _cachedVersion;
            var guids = AssetDatabase.FindAssets("com.unity.modelbox t:TextAsset", new[] { "Packages" });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("package.json"))
                {
                    var json = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    if (json != null)
                    {
                        var text = json.text;
                        var marker = "\"version\":";
                        var idx = text.IndexOf(marker);
                        if (idx >= 0)
                        {
                            var start = text.IndexOf('"', idx + marker.Length);
                            var end = text.IndexOf('"', start + 1);
                            if (start >= 0 && end > start)
                            {
                                _cachedVersion = text.Substring(start + 1, end - start - 1);
                                return _cachedVersion;
                            }
                        }
                    }
                }
            }
            _cachedVersion = "unknown";
            return _cachedVersion;
        }

        private static Texture2D LoadLogo()
        {
            if (_logoTexture != null) return _logoTexture;
            var guids = AssetDatabase.FindAssets("modelBox_Logo t:Texture2D");
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            return _logoTexture;
        }

        // ==================== Navigation ====================

        private enum NavPage
        {
            Inspect,
            Scene,
            Object,
            Sandbox,
            Bone,
            Mesh,
            Settings,
        }

        private static readonly (NavPage page, string icon, string label, string tooltip)[] NavItems =
        {
            (NavPage.Inspect,  "In", "检查", "场景模式 + 物体概览"),
            (NavPage.Scene,    "Sc", "场景", "全场景调试模式"),
            (NavPage.Object,   "Ob", "物体", "选区 / Shader 信息"),
            (NavPage.Sandbox,  "Sb", "沙盒", "材质 A/B 测试"),
            (NavPage.Bone,     "Bo", "骨骼", "骨骼层级 + 蒙皮权重"),
            (NavPage.Mesh,     "Me", "网格", "网格详情 + LOD + 子网格材质"),
            (NavPage.Settings, "St", "设置", "偏好 / 诊断 / 快捷键"),
        };

        private const float SidebarWidth = 48f;
        private const float SidebarAccentWidth = 3f;
        private const float StatusDotSize = 6f;
        private const float SidebarButtonHeight = 40f;

        // [R2 fix] 缓存 GUIStyle，避免每帧 GC 分配
        private static GUIStyle _sidebarBrandStyle;
        private static GUIStyle _sidebarIconStyle;
        private static GUIStyle _sidebarLabelStyle;
        private static GUIStyle _sidebarTipStyle; // [perf P2] 缓存版本提示样式
        private static GUIStyle _pageHeaderSubStyle;

        // [perf] 顶点色通道隔离选项（检查页），缓存避免每帧分配
        private static readonly string[] VertexChannelOptions = { "RGB", "R", "G", "B", "A" };

        // ==================== State ====================

        private DebugViewMode _selectedMode;
        private ModelBoxParameters _params;
        private DebugModeSelector _modeSelector;
        private DebugParameterControls _paramControls;
        private ModelBoxSelectionInspector _selectionInspector;
        private ShaderInfoPanel _shaderInfoPanel;
        private MeshInfoPanel _meshInfoPanel;
        private MaterialDiffPanel _diffPanel;
        private ModelBoxBonePanel _bonePanel;
        private bool _needsSetup;
        private Vector2 _contentScrollPos;
        private NavPage _activePage = NavPage.Inspect;

        // ==================== Lifecycle ====================

        private void OnEnable()
        {
            _modeSelector = new DebugModeSelector();
            _paramControls = new DebugParameterControls();
            _selectionInspector = new ModelBoxSelectionInspector();
            _shaderInfoPanel = new ShaderInfoPanel();
            _meshInfoPanel = new MeshInfoPanel();
            _diffPanel = new MaterialDiffPanel();
            _bonePanel = new ModelBoxBonePanel();
            _needsSetup = !ModelBoxURPSetup.IsSetupComplete();

            // Restore persisted PixelBar settings
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null)
            {
                ModelBoxPixelBar.ShowCustomProp = settings.ShowCustomProp;
                ModelBoxPixelBar.CustomPropName = settings.CustomPropName;
            }

            var manager = ModelBoxManager.Instance;
            if (manager != null)
            {
                _selectedMode = manager.CurrentMode;
                _params = manager.CurrentParameters;
                manager.OnModeChanged += OnManagerModeChanged;
                manager.OnParametersChanged += OnManagerParamsChanged;
            }

            titleContent.image = LoadLogo();
        }

        private void OnDisable()
        {
            var manager = ModelBoxManager.Instance;
            if (manager != null)
            {
                manager.OnModeChanged -= OnManagerModeChanged;
                manager.OnParametersChanged -= OnManagerParamsChanged;
            }
            _diffPanel?.Cleanup();
            _bonePanel?.Cleanup();
        }

        // ==================== Main GUI ====================

        private void OnGUI()
        {
            // Pipeline check (global guard)
            var pipeline = PipelineDetector.Detect();
            if (pipeline != RenderPipelineType.URP)
            {
                EditorGUILayout.HelpBox(
                    $"modelBox 目前仅支持 URP。\n当前管线: {pipeline}",
                    MessageType.Warning);
                return;
            }

            // Setup check (global guard)
            _needsSetup = !ModelBoxURPSetup.IsSetupComplete();
            if (_needsSetup)
            {
                DrawSetupPanel();
                return;
            }

            // --- Layout: sidebar | content ---
            var fullRect = new Rect(0, 0, position.width, position.height);

            // Sidebar rect (left)
            var sidebarRect = new Rect(0, 0, SidebarWidth, fullRect.height);
            // Content rect (right)
            var contentRect = new Rect(SidebarWidth, 0, fullRect.width - SidebarWidth, fullRect.height);

            DrawSidebar(sidebarRect);
            DrawContent(contentRect);
        }

        // ==================== Sidebar ====================

        private void DrawSidebar(Rect sidebarRect)
        {
            // Sidebar background
            EditorGUI.DrawRect(sidebarRect, EditorGUIUtility.isProSkin
                ? new Color(0.17f, 0.17f, 0.17f, 1f)
                : new Color(0.76f, 0.76f, 0.76f, 1f));

            // Right edge separator (1px)
            EditorGUI.DrawRect(new Rect(sidebarRect.xMax - 1, 0, 1, sidebarRect.height),
                new Color(0.3f, 0.3f, 0.3f, 0.6f));

            // --- Version header ---
            var versionRect = new Rect(0, 0, SidebarWidth, 28);
            EditorGUI.DrawRect(versionRect, EditorGUIUtility.isProSkin
                ? new Color(0.13f, 0.13f, 0.13f, 1f)
                : new Color(0.68f, 0.68f, 0.68f, 1f));

            // Logo image (fallback: "mB" text)
            var logo = LoadLogo();
            if (logo != null)
            {
                float logoSize = 22f;
                var logoRect = new Rect(
                    (SidebarWidth - logoSize) * 0.5f,
                    (28f - logoSize) * 0.5f,
                    logoSize, logoSize);
                GUI.DrawTexture(logoRect, logo, ScaleMode.ScaleToFit, true);
            }
            else
            {
                if (_sidebarBrandStyle == null)
                {
                    _sidebarBrandStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 10,
                    };
                    _sidebarBrandStyle.normal.textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.7f, 0.85f, 1f, 1f)
                        : new Color(0.15f, 0.35f, 0.55f, 1f);
                }
                GUI.Label(versionRect, "mB", _sidebarBrandStyle);
            }

            // Version tooltip on hover
            if (versionRect.Contains(Event.current.mousePosition))
            {
                if (_sidebarTipStyle == null)
                {
                    _sidebarTipStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 9,
                    };
                    _sidebarTipStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
                }
                GUI.Label(versionRect, new GUIContent("", $"modelBox v{GetPackageVersion()}"), _sidebarTipStyle);
            }

            // --- Navigation buttons ---
            float buttonY = 30f; // below version header

            for (int i = 0; i < NavItems.Length; i++)
            {
                var item = NavItems[i];
                bool isActive = (_activePage == item.page);
                var btnRect = new Rect(0, buttonY, SidebarWidth, SidebarButtonHeight);

                // Active state styling
                if (isActive)
                {
                    // Subtle background highlight
                    EditorGUI.DrawRect(btnRect, EditorGUIUtility.isProSkin
                        ? new Color(0.25f, 0.35f, 0.5f, 0.5f)
                        : new Color(0.55f, 0.7f, 0.85f, 0.4f));

                    // Left accent bar (3px)
                    var accentRect = new Rect(0, buttonY + 4, SidebarAccentWidth, SidebarButtonHeight - 8);
                    EditorGUI.DrawRect(accentRect, EditorGUIUtility.isProSkin
                        ? new Color(0.4f, 0.7f, 1f, 1f)
                        : new Color(0.2f, 0.5f, 0.85f, 1f));
                }

                // Hover effect
                if (!isActive && btnRect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.DrawRect(btnRect, EditorGUIUtility.isProSkin
                        ? new Color(0.28f, 0.28f, 0.28f, 0.6f)
                        : new Color(0.82f, 0.82f, 0.82f, 0.6f));
                }

                // Icon button (abbreviation + label) — [R2 fix] 缓存样式，仅切换颜色
                if (_sidebarIconStyle == null)
                {
                    _sidebarIconStyle = new GUIStyle()
                    {
                        fontSize = 13,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.white } // 必须显式设置，否则 Color.clear 哨兵值导致纯黑文字
                    };
                }
                if (_sidebarLabelStyle == null)
                {
                    _sidebarLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 9,
                        normal = { textColor = Color.white } // 同上，保证 contentColor 乘法可靠
                    };
                }

                var prevContentColor = GUI.contentColor;
                GUI.contentColor = isActive
                    ? (EditorGUIUtility.isProSkin ? new Color(0.95f, 0.97f, 1f) : new Color(0.08f, 0.2f, 0.5f))
                    : (EditorGUIUtility.isProSkin ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.25f, 0.25f, 0.25f));

                // 上半部分：缩写（In/Sc/Ob 等）
                var iconRect = new Rect(btnRect.x, btnRect.y, btnRect.width, btnRect.height * 0.55f);
                // 下半部分：中文标签
                var lblRect = new Rect(btnRect.x, btnRect.y + btnRect.height * 0.5f, btnRect.width, btnRect.height * 0.5f);

                // [fix] 用不可见按钮检测点击，避免默认 button 样式覆盖背景高亮
                if (GUI.Button(btnRect, new GUIContent("", item.tooltip), GUIStyle.none))
                {
                    // [fix H5] 导航离开 Bone 页时重置骨骼权重可视化
                    if (_activePage == NavPage.Bone && item.page != NavPage.Bone)
                        ClearBoneWeightVisualization();
                    _activePage = item.page;
                    _contentScrollPos = Vector2.zero;
                    GUI.FocusControl(null);
                }
                GUI.Label(iconRect, item.icon, _sidebarIconStyle);
                GUI.Label(lblRect, item.label, _sidebarLabelStyle);
                GUI.contentColor = prevContentColor;

                buttonY += SidebarButtonHeight;
            }

            // --- Status indicator dots (bottom of sidebar) ---
            DrawSidebarStatusDots(sidebarRect);
        }

        private void DrawSidebarStatusDots(Rect sidebarRect)
        {
            float dotAreaHeight = 54f;
            float dotY = sidebarRect.height - dotAreaHeight;

            // Top separator for status area
            EditorGUI.DrawRect(new Rect(4, dotY, SidebarWidth - 8, 1),
                new Color(0.3f, 0.3f, 0.3f, 0.4f));
            dotY += 8;

            bool featureInstalled = ModelBoxURPSetup.IsSetupComplete();
            var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture");
            bool depthAvailable = depthTex != null;
            var opaqueTex = Shader.GetGlobalTexture("_CameraOpaqueTexture");
            bool opaqueAvailable = opaqueTex != null;

            float dotX = (SidebarWidth - StatusDotSize) * 0.5f;
            float spacing = 14f;

            // Feature dot
            DrawStatusDot(new Rect(dotX, dotY, StatusDotSize, StatusDotSize), featureInstalled, "RendererFeature");
            dotY += spacing;

            // Depth dot
            DrawStatusDot(new Rect(dotX, dotY, StatusDotSize, StatusDotSize), depthAvailable, "DepthTexture");
            dotY += spacing;

            // Opaque dot
            DrawStatusDot(new Rect(dotX, dotY, StatusDotSize, StatusDotSize), opaqueAvailable, "OpaqueTexture");
        }

        private void DrawStatusDot(Rect rect, bool ok, string tooltip)
        {
            var color = ok
                ? new Color(0.3f, 0.8f, 0.3f, 0.9f)
                : new Color(0.8f, 0.3f, 0.3f, 0.9f);

            EditorGUI.DrawRect(rect, color);

            // Hover tooltip
            if (rect.Contains(Event.current.mousePosition))
            {
                GUI.Label(rect, new GUIContent("", $"{tooltip}: {(ok ? "OK" : "Missing")}"));
            }
        }

        // ==================== Content Area ====================

        private void DrawContent(Rect contentRect)
        {
            GUILayout.BeginArea(contentRect);

            // Sandbox 页面跳过外层 ScrollView — 它内部有自己的 ScrollView
            // 双重嵌套导致内层 MinHeight(200) 无法随窗口拉伸
            bool needsOuterScroll = (_activePage != NavPage.Sandbox);

            if (needsOuterScroll)
                _contentScrollPos = EditorGUILayout.BeginScrollView(_contentScrollPos);

            switch (_activePage)
            {
                case NavPage.Inspect:
                    DrawInspectPage();
                    break;
                case NavPage.Scene:
                    DrawScenePage();
                    break;
                case NavPage.Object:
                    DrawObjectPage();
                    break;
                case NavPage.Sandbox:
                    DrawSandboxPage();
                    break;
                case NavPage.Bone:
                    DrawBonePage();
                    break;
                case NavPage.Mesh:
                    DrawMeshPage();
                    break;
                case NavPage.Settings:
                    DrawSettingsPage();
                    break;
            }

            if (needsOuterScroll)
                EditorGUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        // ==================== Page: Inspect (combined overview) ====================

        private void DrawInspectPage()
        {
            DrawPageHeader("检查", "选中物体总览 + 快速跳转");

            // Status bar
            DrawStatusBar();
            EditorGUILayout.Space(8);

            // Selected object quick summary
            DrawSelectedObjectSummary();
        }

        private void DrawSelectedObjectSummary()
        {
            ModelBoxStyles.DrawSectionHeader("选中物体");

            var selected = Selection.activeTransform;
            if (selected == null)
            {
                EditorGUILayout.HelpBox("在 Scene 中选择一个物体以查看信息。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"名称: {selected.gameObject.name}", EditorStyles.boldLabel);

            // Shader 名：选区调试激活时 sharedMaterial 已被调试材质替换，
            // 经 GetOriginalMaterial 回读原始材质，保证显示的是物体真实 Shader
            var renderer = selected.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                var selMgr = ModelBoxSelectionManager.Instance;
                var displayMat = selMgr != null
                    ? selMgr.GetOriginalMaterial(renderer)
                    : renderer.sharedMaterial;
                if (displayMat != null && displayMat.shader != null)
                    EditorGUILayout.LabelField($"Shader: {displayMat.shader.name}", EditorStyles.miniLabel);
            }

            // Mesh 统计：聚合选中层级所有激活 Renderer（排除未激活物体与 LOD 非活跃级别），
            // 多部件模型显示总量而非首个网格 — 保证统计与实际渲染内容一致
            long totalVerts = 0, totalTris = 0;
            int totalSubMeshes = 0, meshCount = 0;
            foreach (var r in selected.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Mesh m = null;
                if (r is MeshRenderer mr) m = mr.GetComponent<MeshFilter>()?.sharedMesh;
                else if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
                if (m == null) continue;
                meshCount++;
                totalVerts += m.vertexCount;
                totalSubMeshes += m.subMeshCount;
                for (int si = 0; si < m.subMeshCount; si++)
                    totalTris += m.GetIndexCount(si) / 3;
            }

            if (meshCount > 0)
            {
                string meshLabel = meshCount > 1
                    ? $"Mesh: {totalVerts:N0} 顶点, {totalTris:N0} 三角面, {totalSubMeshes} 子网格（{meshCount} 个网格聚合）"
                    : $"Mesh: {totalVerts:N0} 顶点, {totalTris:N0} 三角面, {totalSubMeshes} 子网格";
                EditorGUILayout.LabelField(meshLabel, EditorStyles.miniLabel);

                // 快速叠加开关
                var selManager = ModelBoxSelectionManager.Instance;
                if (selManager != null)
                {
                    EditorGUILayout.Space(2);
                    var flags = selManager.OverlayFlags;
                    EditorGUILayout.BeginHorizontal();
                    ToggleMiniButton("线框", ref flags, MeshOverlayFlags.Wireframe, selManager);
                    ToggleMiniButton("顶点", ref flags, MeshOverlayFlags.Vertices, selManager);
                    ToggleMiniButton("法线", ref flags, MeshOverlayFlags.Normals, selManager);
                    ToggleMiniButton("切线", ref flags, MeshOverlayFlags.Tangents, selManager);
                    ToggleMiniButton("AABB", ref flags, MeshOverlayFlags.Bounds, selManager);
                    // [feat v0.6] 模型局部坐标快捷开关（网格叠加：原点三向轴）
                    ToggleMiniButton("局部坐标", ref flags, MeshOverlayFlags.LocalAxes, selManager);
                    // [feat] 顶点颜色可视化快捷开关（选区材质调试，非网格叠加）
                    {
                        bool vcActive = selManager.CurrentMode == SelectionDebugMode.VertexColor;
                        var prevBg = GUI.backgroundColor;
                        if (vcActive) GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();
                        if (GUILayout.Toggle(vcActive, " 顶点色 ", EditorStyles.miniButton) != vcActive)
                            selManager.SetMode(vcActive ? SelectionDebugMode.None : SelectionDebugMode.VertexColor);
                        GUI.backgroundColor = prevBg;
                    }
                    EditorGUILayout.EndHorizontal();

                    // 叠加样式控件（仅在有叠加激活时显示）
                    if (flags != MeshOverlayFlags.None)
                    {
                        EditorGUI.BeginChangeCheck();
                        if ((flags & MeshOverlayFlags.Wireframe) != 0)
                            selManager.WireframeColor = EditorGUILayout.ColorField("线框颜色", selManager.WireframeColor);
                        if ((flags & MeshOverlayFlags.Vertices) != 0)
                        {
                            selManager.VertexColor = EditorGUILayout.ColorField("顶点颜色", selManager.VertexColor);
                            selManager.VertexSize = EditorGUILayout.Slider("顶点大小", selManager.VertexSize, 0.005f, 0.3f);
                        }
                        if ((flags & MeshOverlayFlags.Normals) != 0)
                        {
                            selManager.NormalColor = EditorGUILayout.ColorField("法线颜色", selManager.NormalColor);
                            selManager.NormalLength = EditorGUILayout.Slider("法线长度", selManager.NormalLength, 0.01f, 0.5f);
                        }
                        if ((flags & MeshOverlayFlags.Tangents) != 0)
                        {
                            selManager.TangentColor = EditorGUILayout.ColorField("切线颜色", selManager.TangentColor);
                            selManager.TangentLength = EditorGUILayout.Slider("切线长度", selManager.TangentLength, 0.01f, 0.5f);
                        }
                        if ((flags & MeshOverlayFlags.Bounds) != 0)
                            selManager.BoundsColor = EditorGUILayout.ColorField("AABB 颜色", selManager.BoundsColor);
                        // [feat v0.6] 局部坐标轴长度（轴长 = 合并包围盒对角线 × 系数）
                        if ((flags & MeshOverlayFlags.LocalAxes) != 0)
                            selManager.LocalAxesLength = EditorGUILayout.Slider(
                                new GUIContent("局部坐标轴长度", "轴长 = 选中层级合并包围盒对角线 × 系数"),
                                selManager.LocalAxesLength, 0.1f, 2f);
                        if (EditorGUI.EndChangeCheck())
                            EditorApplication.delayCall += () => SceneView.RepaintAll();
                    }

                    // [feat v0.6.x] 顶点色激活时在检查页展开通道隔离（RGB/R/G/B/A），免跳转物体页
                    if (selManager.CurrentMode == SelectionDebugMode.VertexColor)
                    {
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField("顶点色通道隔离", EditorStyles.miniBoldLabel);
                        int vcChannel = GUILayout.SelectionGrid(selManager.VertexColorChannel, VertexChannelOptions, 5);
                        if (vcChannel != selManager.VertexColorChannel)
                        {
                            selManager.VertexColorChannel = vcChannel;
                            selManager.UpdateVertexColorProperties();
                        }
                    }
                }

                // [feat] 场景调试快捷控制（补全功能入口：SEL/分屏/冻结）
                EditorGUILayout.Space(6);
                ModelBoxStyles.DrawSectionHeader("场景调试");
                var manager = ModelBoxManager.Instance;
                if (manager != null)
                {
                    bool selOnly = manager.DebugOnlySelected;
                    bool newSelOnly = EditorGUILayout.ToggleLeft(
                        new GUIContent("仅选中物体 (SEL)", "仅对选中物体显示调试效果"),
                        selOnly);
                    if (newSelOnly != selOnly) manager.ToggleDebugOnlySelected();

                    bool split = manager.SplitScreenEnabled;
                    bool newSplit = EditorGUILayout.ToggleLeft(
                        new GUIContent("分屏对比 (Alt+S)", "左侧正常渲染，右侧调试模式"),
                        split);
                    if (newSplit != split) manager.SetSplitScreen(newSplit);

                    if (manager.SplitScreenEnabled)
                    {
                        bool frozen = manager.FreezeLeftSnapshot;
                        bool newFrozen = EditorGUILayout.ToggleLeft("冻结左侧快照", frozen);
                        if (newFrozen != frozen) manager.SetFreezeLeftSnapshot(newFrozen);
                    }
                }
            }

            EditorGUILayout.Space(6);

            // Quick navigation to detailed views
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("→ Shader 详情", EditorStyles.miniButton, GUILayout.Height(22)))
            {
                _activePage = NavPage.Object;
                _contentScrollPos = Vector2.zero;
            }
            if (GUILayout.Button("→ Mesh 详情", EditorStyles.miniButton, GUILayout.Height(22)))
            {
                _activePage = NavPage.Mesh;
                _contentScrollPos = Vector2.zero;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("→ 场景调试模式", EditorStyles.miniButton, GUILayout.Height(22)))
            {
                _activePage = NavPage.Scene;
                _contentScrollPos = Vector2.zero;
            }
            if (GUILayout.Button("→ 材质沙盒", EditorStyles.miniButton, GUILayout.Height(22)))
            {
                _activePage = NavPage.Sandbox;
                _contentScrollPos = Vector2.zero;
            }
            EditorGUILayout.EndHorizontal();

            // 快捷键提示
            EditorGUILayout.Space(4);
            var hintPrevColor = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.55f, 0.55f, 0.55f)
                : new Color(0.45f, 0.45f, 0.45f);
            EditorGUILayout.LabelField("快捷键: Alt+0 关闭 | Alt+1~9 切换模式 | 详见 设置 页面", EditorStyles.miniLabel);
            GUI.contentColor = hintPrevColor;
        }

        // ==================== Page: Scene (full debug modes + parameters) ====================

        private void DrawScenePage()
        {
            DrawPageHeader("场景", "场景调试模式与参数调节");

            // Status bar
            DrawStatusBar();
            EditorGUILayout.Space(6);

            // Mode selection
            ModelBoxStyles.DrawSectionHeader("调试模式");
            var newMode = _modeSelector.Draw(_selectedMode);
            if (newMode != _selectedMode)
            {
                _selectedMode = newMode;
                ModelBoxManager.Instance?.SetDebugMode(_selectedMode);
            }

            EditorGUILayout.Space(4);

            // Parameter controls
            ModelBoxStyles.DrawSectionHeader("参数");
            var newParams = _paramControls.Draw(_params, _selectedMode);
            if (!newParams.Equals(_params))
            {
                _params = newParams;
                ModelBoxManager.Instance?.SetParameters(_params);
            }
        }

        // ==================== Page: Object (merged inspector + shader + mesh) ====================

        private void DrawObjectPage()
        {
            DrawPageHeader("物体", "选区检查、Shader 与 Mesh 信息");

            // --- Section: Selection Inspector ---
            ModelBoxStyles.DrawSectionHeader("选区调试");
            _selectionInspector.Draw();

            EditorGUILayout.Space(8);

            // --- Section: Shader Info ---
            ModelBoxStyles.DrawSectionHeader("Shader 信息");
            _shaderInfoPanel.Draw();

            // Mesh 信息已独立到侧边栏「网格」页
        }

        // ==================== Page: Sandbox ====================

        private void DrawSandboxPage()
        {
            DrawPageHeader("沙盒", "材质 A/B 对比测试");
            _diffPanel.Draw();
        }

        // ==================== Page: Bone (skeleton + skinning) ====================

        // [fix H5] 导航离开 Bone 页时清理骨骼权重可视化状态
        private void ClearBoneWeightVisualization()
        {
            var selManager = ModelBoxSelectionManager.Instance;
            if (selManager != null)
            {
                selManager.BoneWeightMode = BoneWeightDisplayMode.Off;
                selManager.BoneVertexWeights = null;
                selManager.BoneWeightTargetSMR = null;
                selManager.ShowBoneGizmos = false;
                selManager.SelectedBoneIndex = -1;
            }
        }

        private void DrawBonePage()
        {
            DrawPageHeader("骨骼", "骨骼层级 + 蒙皮权重概览");

            // Status bar (show current mode for context)
            DrawStatusBar();
            EditorGUILayout.Space(6);

            _bonePanel.Draw();
        }

        // ==================== Page: Mesh (grid details) ====================

        private void DrawMeshPage()
        {
            DrawPageHeader("网格", "网格详情 + LOD 层级 + 子网格材质");
            _meshInfoPanel.Draw();
        }

        // ==================== Page: Settings ====================

        private void DrawSettingsPage()
        {
            DrawPageHeader("设置", "偏好、快捷键、诊断");

            // --- Installation ---
            ModelBoxStyles.DrawSectionHeader("安装 / 卸载");
            DrawSetupPanel();
            EditorGUILayout.Space(4);
            DrawUninstallPanel();

            EditorGUILayout.Space(8);

            // --- Preferences ---
            DrawPreferences();

            EditorGUILayout.Space(8);

            // --- Shortcut reference ---
            ModelBoxStyles.DrawSectionHeader("快捷键参考");
            DrawShortcutReference();

            EditorGUILayout.Space(8);

            // --- Diagnostics ---
            ModelBoxStyles.DrawSectionHeader("诊断");
            DrawDiagnostics();
        }

        // ==================== Common UI Pieces ====================

        /// <summary>
        /// Page header bar: title + subtitle + 1px separator.
        /// </summary>
        private void DrawPageHeader(string title, string subtitle)
        {
            // Background bar (32px)
            var headerRect = EditorGUILayout.GetControlRect(false, 32);
            EditorGUI.DrawRect(headerRect, ModelBoxStyles.TabHeaderBgColor);

            // Title
            var titleRect = new Rect(headerRect.x + 10, headerRect.y + 2, headerRect.width - 20, 18);
            GUI.Label(titleRect, title, ModelBoxStyles.TabHeaderTitleStyle);

            // Subtitle — [R2 fix] 缓存样式
            var subRect = new Rect(headerRect.x + 10, headerRect.y + 18, headerRect.width - 20, 14);
            if (_pageHeaderSubStyle == null)
            {
                _pageHeaderSubStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.82f, 0.85f, 0.9f, 1f)
                        : new Color(0.2f, 0.22f, 0.28f, 1f) }
                };
            }
            GUI.Label(subRect, subtitle, _pageHeaderSubStyle);

            // Bottom separator
            var sepRect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(sepRect, ModelBoxStyles.SeparatorColor);
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// Status bar showing current active debug mode.
        /// </summary>
        private void DrawStatusBar()
        {
            var manager = ModelBoxManager.Instance;
            if (manager == null) return;

            bool isActive = manager.CurrentMode != DebugViewMode.None;

            // Status bar background
            var statusRect = EditorGUILayout.GetControlRect(false, 28);
            EditorGUI.DrawRect(statusRect, isActive ? ModelBoxStyles.StatusActiveColor : ModelBoxStyles.StatusInactiveColor);

            // Bottom border
            var borderRect = new Rect(statusRect.x, statusRect.yMax - 1, statusRect.width, 1);
            EditorGUI.DrawRect(borderRect, isActive ? ModelBoxStyles.StatusActiveBorder : ModelBoxStyles.StatusInactiveBorder);

            // Status text (with shortcut hint)
            string statusText = isActive
                ? $"  ● {GetModeDisplayName(manager.CurrentMode)}"
                : "  ○ 正常渲染    [Alt+1~9 切换模式]";

            // 当调试模式激活时，右侧显示关闭按钮
            if (isActive)
            {
                float btnW = 60f;
                var labelRect = new Rect(statusRect.x, statusRect.y, statusRect.width - btnW, statusRect.height);
                var btnRect = new Rect(statusRect.xMax - btnW, statusRect.y, btnW, statusRect.height);
                GUI.Label(labelRect, statusText, ModelBoxStyles.GetActiveStatusStyle(true));
                if (GUI.Button(btnRect, "关闭 ✕", EditorStyles.miniButton))
                {
                    manager.SetDebugMode(DebugViewMode.None);
                }
            }
            else
            {
                GUI.Label(statusRect, statusText, ModelBoxStyles.GetActiveStatusStyle(false));
            }
        }

        // ==================== Settings Sub-sections ====================

        private void DrawSetupPanel()
        {
            EditorGUILayout.HelpBox(
                "modelBox 需要将一个 RendererFeature 添加到你的 URP Renderer Asset 中。\n\n" +
                "这是一次性操作，你可以随时卸载。",
                MessageType.Info);

            EditorGUILayout.Space(4);

            ModelBoxStyles.BeginPrimaryButton();
            if (GUILayout.Button("一键安装 URP 集成", GUILayout.Height(28)))
            {
                if (ModelBoxURPSetup.AddFeatureToActiveRenderer())
                {
                    _needsSetup = false;
                }
            }
            ModelBoxStyles.EndPrimaryButton();
        }

        private void DrawUninstallPanel()
        {
            ModelBoxStyles.BeginDangerButton();
            if (GUILayout.Button("卸载 URP 集成", GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog("卸载确认",
                    "确定要卸载 modelBox 的 URP 集成吗？\n\n这将从 URP Renderer 中移除 ModelBoxRendererFeature。",
                    "确定卸载", "取消"))
                {
                    ModelBoxManager.Instance?.SetDebugMode(DebugViewMode.None);
                    if (ModelBoxURPSetup.RemoveFeatureFromActiveRenderer())
                    {
                        _needsSetup = true;
                        Debug.Log("[ModelBox] 卸载完成，URP 集成已移除。");
                    }
                }
            }
            ModelBoxStyles.EndDangerButton();

            EditorGUILayout.LabelField("移除后可随时重新安装", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawPreferences()
        {
            ModelBoxStyles.DrawSectionHeader("偏好设置");

            var settings = ModelBoxSettings.GetOrCreate();
            if (settings == null) return;

            // --- SceneView ---
            EditorGUI.BeginChangeCheck();
            settings.ShowSceneViewOverlay = EditorGUILayout.Toggle("SceneView 工具栏", settings.ShowSceneViewOverlay);
            if (settings.ShowSceneViewOverlay)
            {
                EditorGUI.indentLevel++;
                if (GUILayout.Button("重置工具栏位置", EditorStyles.miniButton, GUILayout.Width(120)))
                {
                    settings.ToolbarX = 8f;
                    settings.ToolbarY = 8f;
                    settings.Save();
                    EditorApplication.delayCall += () => SceneView.RepaintAll();
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);

            // --- Pixel Picking ---
            settings.ShowPixelInspector = EditorGUILayout.Toggle("像素拾取提示", settings.ShowPixelInspector);
            EditorGUILayout.Space(4);

            // --- Pixel Bar ---
            settings.ShowPixelBar = EditorGUILayout.Toggle("像素条 (Pixel Bar)", settings.ShowPixelBar);
            if (settings.ShowPixelBar)
            {
                EditorGUI.indentLevel++;
                settings.PixelBarStandalone = EditorGUILayout.Toggle(
                    new GUIContent("独立模式", "不开启调试模式也能使用 PixelBar（显示物体名/Shader/坐标/自定义属性）"),
                    settings.PixelBarStandalone);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);

            // --- Performance HUD ---
            settings.ShowPerformanceHUD = EditorGUILayout.Toggle(
                new GUIContent("性能统计 HUD", "SceneView 左下角显示性能指标面板"),
                settings.ShowPerformanceHUD);
            if (settings.ShowPerformanceHUD)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("显示项", EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                settings.HudShowDC = GUILayout.Toggle(settings.HudShowDC, "DC", EditorStyles.miniButton);
                settings.HudShowTri = GUILayout.Toggle(settings.HudShowTri, "Tri", EditorStyles.miniButton);
                settings.HudShowVert = GUILayout.Toggle(settings.HudShowVert, "Vert", EditorStyles.miniButton);
                settings.HudShowMem = GUILayout.Toggle(settings.HudShowMem, "总分配", EditorStyles.miniButton);
                settings.HudShowGC = GUILayout.Toggle(settings.HudShowGC, "C#堆", EditorStyles.miniButton);
                EditorGUILayout.EndHorizontal();
                settings.HudShowFPSBeta = EditorGUILayout.ToggleLeft(
                    new GUIContent("显示 FPS [BETA]", "Editor 中 FPS 计算基于时间差近似值，可能与实际帧率有偏差。\n此指标仅作参考，不作为精确性能度量。"),
                    settings.HudShowFPSBeta);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);

            // --- Overlay ---
            settings.OverlayDepthTest = EditorGUILayout.Toggle(
                new GUIContent("叠加深度测试", "网格叠加（线框/顶点/法线等）是否被模型遮挡。\n关闭=透视显示所有叠加点（可看穿模型）"),
                settings.OverlayDepthTest);
            if (EditorGUI.EndChangeCheck())
                settings.Save();

            // --- Pixel Bar Custom Properties ---
            EditorGUILayout.Space(8);
            ModelBoxStyles.DrawSectionHeader("Pixel Bar 自定义属性");
            EditorGUILayout.LabelField("在 Pixel Bar 中显示指定 Shader 属性的值。", EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginChangeCheck();
            ModelBoxPixelBar.ShowCustomProp = EditorGUILayout.Toggle("启用自定义属性", ModelBoxPixelBar.ShowCustomProp);
            ModelBoxPixelBar.CustomPropName = EditorGUILayout.TextField("属性名", ModelBoxPixelBar.CustomPropName);
            if (EditorGUI.EndChangeCheck())
            {
                settings.ShowCustomProp = ModelBoxPixelBar.ShowCustomProp;
                settings.CustomPropName = ModelBoxPixelBar.CustomPropName;
                settings.Save();
                EditorApplication.delayCall += () => SceneView.RepaintAll();
            }

            // Quick presets
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("_BaseColor", EditorStyles.miniButton)) { ModelBoxPixelBar.CustomPropName = "_BaseColor"; settings.CustomPropName = "_BaseColor"; settings.Save(); EditorApplication.delayCall += () => SceneView.RepaintAll(); }
            if (GUILayout.Button("_Color", EditorStyles.miniButton)) { ModelBoxPixelBar.CustomPropName = "_Color"; settings.CustomPropName = "_Color"; settings.Save(); EditorApplication.delayCall += () => SceneView.RepaintAll(); }
            if (GUILayout.Button("_Metallic", EditorStyles.miniButton)) { ModelBoxPixelBar.CustomPropName = "_Metallic"; settings.CustomPropName = "_Metallic"; settings.Save(); EditorApplication.delayCall += () => SceneView.RepaintAll(); }
            if (GUILayout.Button("_Smoothness", EditorStyles.miniButton)) { ModelBoxPixelBar.CustomPropName = "_Smoothness"; settings.CustomPropName = "_Smoothness"; settings.Save(); EditorApplication.delayCall += () => SceneView.RepaintAll(); }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawShortcutReference()
        {
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var miniLabel = EditorStyles.miniLabel;
            DrawShortcutRow("Alt + 0", "开/关调试", miniLabel);
            DrawShortcutRow("Alt + 1~9", "模式 1-9（WP/LP/WN/LN/U0/U1/Dp/VC/SU）", miniLabel);
            DrawShortcutRow("Alt + Shift + 1~5", "模式 10-14（RD/OD/PC/WF/OT）", miniLabel);
            DrawShortcutRow("Alt + Shift + 6~8", "模式 15/17/18（OD/SN/SM；TL=16 无快捷键）", miniLabel);
            DrawShortcutRow("Alt + Shift + 9", "NdotL（PBR 诊断）", miniLabel);
            DrawShortcutRow("Alt + , / .", "上一个 / 下一个模式（循环全部 34 种）", miniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawShortcutRow(string key, string desc, GUIStyle style)
        {
            EditorGUILayout.BeginHorizontal();
            var prevColor = GUI.contentColor;
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.6f, 0.85f, 1f)
                : new Color(0.15f, 0.35f, 0.65f);
            EditorGUILayout.LabelField(key, style, GUILayout.Width(110));
            GUI.contentColor = EditorGUIUtility.isProSkin
                ? new Color(0.85f, 0.85f, 0.85f)
                : new Color(0.25f, 0.25f, 0.25f);
            EditorGUILayout.LabelField(desc, style);
            GUI.contentColor = prevColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDiagnostics()
        {
            EditorGUILayout.Space(2);

            bool featureInstalled = ModelBoxURPSetup.IsSetupComplete();
            var prevCC = GUI.contentColor;
            GUI.contentColor = featureInstalled
                ? new Color(0.4f, 0.9f, 0.4f) : new Color(1f, 0.5f, 0.5f);
            EditorGUILayout.LabelField($"RendererFeature: {(featureInstalled ? "✓ 已安装" : "✗ 未安装")}",
                EditorStyles.miniLabel);
            GUI.contentColor = prevCC;

            var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture");
            bool depthAvailable = depthTex != null;
            GUI.contentColor = depthAvailable
                ? new Color(0.4f, 0.9f, 0.4f) : new Color(1f, 0.5f, 0.5f);
            EditorGUILayout.LabelField($"深度纹理: {(depthAvailable ? "✓ 可用" : "✗ 不可用")}",
                EditorStyles.miniLabel);
            GUI.contentColor = prevCC;

            if (!depthAvailable)
            {
                EditorGUILayout.HelpBox(
                    "_CameraDepthTexture 不可用。请在 URP Asset 中确认：\nRendering > Depth Texture = On",
                    MessageType.Warning);
                if (GUILayout.Button("强制启用 URP 深度纹理", GUILayout.Height(22)))
                    ModelBoxURPSetup.EnsureDepthTexture();
            }

            var opaqueTex = Shader.GetGlobalTexture("_CameraOpaqueTexture");
            bool opaqueAvailable = opaqueTex != null;
            GUI.contentColor = opaqueAvailable
                ? new Color(0.4f, 0.9f, 0.4f) : new Color(1f, 0.5f, 0.5f);
            EditorGUILayout.LabelField($"Opaque Texture: {(opaqueAvailable ? "✓ 可用" : "✗ 不可用")}",
                EditorStyles.miniLabel);
            GUI.contentColor = prevCC;

            if (!opaqueAvailable)
            {
                EditorGUILayout.HelpBox(
                    "_CameraOpaqueTexture 不可用。请在 URP Asset 中确认：\nRendering > Opaque Texture = On",
                    MessageType.Warning);
                if (GUILayout.Button("强制启用 URP Opaque Texture", GUILayout.Height(22)))
                    ModelBoxURPSetup.EnsureOpaqueTexture();
            }

            var pipeline = PipelineDetector.Detect();
            EditorGUILayout.LabelField($"当前管线: {pipeline}", EditorStyles.miniLabel);

            if (GUILayout.Button("重新检查", GUILayout.Height(20)))
                Repaint();
        }

        // ==================== Helpers ====================

        private string GetModeDisplayName(DebugViewMode mode)
        {
            switch (mode)
            {
                case DebugViewMode.WorldPosition: return "世界坐标 (World Position)";
                case DebugViewMode.LocalPosition: return "模型坐标 (Local Position)";
                case DebugViewMode.WorldNormal: return "世界法线 (World Normal)";
                case DebugViewMode.LocalNormal: return "模型法线 (Local Normal)";
                case DebugViewMode.UV0: return "UV0";
                case DebugViewMode.UV1: return "UV1";
                case DebugViewMode.Depth: return "深度 (Depth)";
                case DebugViewMode.VertexColor: return "顶点颜色 (Vertex Color)";
                case DebugViewMode.Wireframe: return "线框 (Wireframe)";
                case DebugViewMode.OpaqueTexture: return "不透明纹理 (Opaque Texture)";
                case DebugViewMode.Overdraw: return "Overdraw 热力图";
                case DebugViewMode.DiagScreenUV: return "屏幕 UV (Screen UV)";
                case DebugViewMode.DiagRawDepth: return "原始深度 (Raw Depth)";
                case DebugViewMode.DiagObjectDepth: return "物体深度 (Object Depth)";
                case DebugViewMode.DiagPureColor: return "纯色测试 (Pure Color)";
                case DebugViewMode.ScreenNormal: return "屏幕法线 (Screen Normal)";
                case DebugViewMode.ShadowMap: return "阴影贴图 (Shadow Map)";
                case DebugViewMode.TransparencyLayers: return "透明层数 (Transparency Layers)";
                case DebugViewMode.FlatNormal: return "平面法线 (Flat Normal)";
                case DebugViewMode.NdotL: return "NdotL (Lambert 漫反射)";
                case DebugViewMode.NdotV: return "NdotV (视角对齐)";
                case DebugViewMode.Fresnel: return "Fresnel (菲涅尔)";
                case DebugViewMode.ObjectID: return "物体 ID (Object ID)";
                case DebugViewMode.Tangent: return "切线 (Tangent)";
                case DebugViewMode.Bitangent: return "副切线 (Bitangent)";
                case DebugViewMode.DiffuseColor: return "漫反射 (Diffuse)";
                case DebugViewMode.SpecularHighlight: return "高光 (Specular)";
                case DebugViewMode.LightingOnly: return "仅光照 (Lighting Only)";
                case DebugViewMode.Roughness: return "粗糙度 (Roughness)";
                case DebugViewMode.Metallic: return "金属度 (Metallic)";
                case DebugViewMode.MipmapLevel: return "Mipmap 级别 (Texture Density)";
                case DebugViewMode.GeoDensity: return "几何密度 (Geo Density)";
                case DebugViewMode.SkyExposure: return "天光曝光 (Sky Exposure)";
                case DebugViewMode.RayMarch: return "射线步进 (Ray March)";
                default: return mode.ToString();
            }
        }

        private void OnManagerModeChanged(DebugViewMode mode)
        {
            _selectedMode = mode;
            Repaint();
        }

        private void OnManagerParamsChanged(ModelBoxParameters p)
        {
            _params = p;
            Repaint();
        }

        /// <summary>快速叠加开关按钮（toggle style mini button）。</summary>
        private static void ToggleMiniButton(string label, ref MeshOverlayFlags flags, MeshOverlayFlags flag, ModelBoxSelectionManager manager)
        {
            bool isOn = (flags & flag) != 0;
            var prevBg = GUI.backgroundColor;
            if (isOn) GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();
            if (GUILayout.Toggle(isOn, $" {label} ", EditorStyles.miniButton))
                flags |= flag;
            else
                flags &= ~flag;
            GUI.backgroundColor = prevBg;
            if (((flags & flag) != 0) != isOn)
                manager.SetOverlayFlags(flags);
        }
    }
}
