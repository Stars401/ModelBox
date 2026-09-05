using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// SceneView 浮动工具栏。使用分组下拉按钮替代 18 个独立按钮，
    /// 大幅缩减工具栏宽度。
    /// 使用 SceneView.duringSceneGui（2021.3 稳定 API）。
    /// </summary>
    [InitializeOnLoad]
    public static class ModelBoxSceneOverlay
    {
        // ==================== 分组定义 ====================

        private struct ModeEntry
        {
            public GUIContent Content;
            public DebugViewMode Value;

            public ModeEntry(string abbr, string tooltip, DebugViewMode value)
            {
                Content = new GUIContent(abbr, tooltip);
                Value = value;
            }
        }

        private struct ModeGroup
        {
            public string Label;
            public ModeEntry[] Entries;
        }

        private static readonly ModeGroup[] Groups;
        private static readonly GUIContent _debugLabelContent = new GUIContent();

        // Off 按钮单独定义
        private static readonly GUIContent _offContent = new GUIContent("Off", "模式区 | 恢复正常渲染 (Alt+0)");

        // 功能按钮
        private static readonly GUIContent _selContent = new GUIContent("SEL", "工具区 | 仅显示选中物体的调试效果（切换过滤模式）");
        private static readonly GUIContent _splitContent = new GUIContent("Split", "工具区 | 分屏对比：左侧正常渲染，右侧调试模式 (Alt+S)");
        private static readonly GUIContent _freezeContent = new GUIContent("冻结", "工具区 | 冻结左侧快照（锁定正常场景画面，自由切换右侧调试模式）");
        // [feat] 快照 A/B 对比按钮
        private static readonly GUIContent _snapAContent = new GUIContent("SA", "快照 A：保存当前调试画面。\n已有快照时点击切换对比模式");
        private static readonly GUIContent _snapBContent = new GUIContent("SB", "快照 B：保存当前调试画面。\n已有快照时点击切换对比模式");

        // [feat] 工具栏拖拽状态
        private static int _toolbarDragId = -1;
        private static Vector2 _toolbarDragOffset;
        private static readonly GUIContent _snapContent = new GUIContent("Snap", "操作区 | 截取 SceneView 快照");

        // 最近使用模式记录
        private static readonly List<DebugViewMode> _recentModes = new List<DebugViewMode>();
        private const int MAX_RECENT_MODES = 5;

        static ModelBoxSceneOverlay()
        {
            Groups = new ModeGroup[]
            {
                new ModeGroup
                {
                    Label = "常用",
                    Entries = new ModeEntry[]
                    {
                        new ModeEntry("WP",  "世界坐标",    DebugViewMode.WorldPosition),
                        new ModeEntry("WN",  "世界法线",    DebugViewMode.WorldNormal),
                        new ModeEntry("Dp",  "深度",       DebugViewMode.Depth),
                        new ModeEntry("VC",  "顶点颜色",   DebugViewMode.VertexColor),
                        new ModeEntry("WF",  "线框",       DebugViewMode.Wireframe),
                    }
                },
                new ModeGroup
                {
                    Label = "几何",
                    Entries = new ModeEntry[]
                    {
                        new ModeEntry("LP",  "模型坐标",       DebugViewMode.LocalPosition),
                        new ModeEntry("LN",  "模型法线",       DebugViewMode.LocalNormal),
                        new ModeEntry("U0",  "UV0",           DebugViewMode.UV0),
                        new ModeEntry("U1",  "UV1",           DebugViewMode.UV1),
                    }
                },
                new ModeGroup
                {
                    Label = "诊断",
                    Entries = new ModeEntry[]
                    {
                        new ModeEntry("SU",  "屏幕 UV",       DebugViewMode.DiagScreenUV),
                        new ModeEntry("RD",  "原始深度",      DebugViewMode.DiagRawDepth),
                        new ModeEntry("ObjD","物体深度",      DebugViewMode.DiagObjectDepth),
                        new ModeEntry("PC",  "纯色测试",      DebugViewMode.DiagPureColor),
                        new ModeEntry("Mip",  "Mipmap 级别",  DebugViewMode.MipmapLevel),
                        new ModeEntry("Geo", "几何密度",      DebugViewMode.GeoDensity),
                        new ModeEntry("Sky", "天光曝光",      DebugViewMode.SkyExposure),
                        new ModeEntry("RM",  "射线步进",      DebugViewMode.RayMarch),
                    }
                },
                new ModeGroup
                {
                    Label = "高级",
                    Entries = new ModeEntry[]
                    {
                        new ModeEntry("OT",  "不透明纹理",    DebugViewMode.OpaqueTexture),
                        new ModeEntry("OD",  "Overdraw",      DebugViewMode.Overdraw),
                        new ModeEntry("SN",  "屏幕法线",      DebugViewMode.ScreenNormal),
                        new ModeEntry("SM",  "阴影贴图",      DebugViewMode.ShadowMap),
                        new ModeEntry("TL",  "透明层数",      DebugViewMode.TransparencyLayers),
                    }
                },
                new ModeGroup
                {
                    Label = "PBR",
                    Entries = new ModeEntry[]
                    {
                        new ModeEntry("FN",  "平面法线",       DebugViewMode.FlatNormal),
                        new ModeEntry("NL",  "NdotL",        DebugViewMode.NdotL),
                        new ModeEntry("NV",  "NdotV",        DebugViewMode.NdotV),
                        new ModeEntry("Fr",  "Fresnel",      DebugViewMode.Fresnel),
                        new ModeEntry("ID",  "物体 ID",       DebugViewMode.ObjectID),
                    }
                },
                new ModeGroup
                {
                    Label = "光照",
                    Entries = new ModeEntry[]
                    {
                        new ModeEntry("T",   "切线方向",       DebugViewMode.Tangent),
                        new ModeEntry("B",   "副切线方向",     DebugViewMode.Bitangent),
                        new ModeEntry("Df",  "漫反射",       DebugViewMode.DiffuseColor),
                        new ModeEntry("Sp",  "高光",         DebugViewMode.SpecularHighlight),
                        new ModeEntry("LO",  "仅光照",       DebugViewMode.LightingOnly),
                        new ModeEntry("Rg",  "粗糙度",       DebugViewMode.Roughness),
                        new ModeEntry("Mt",  "金属度",       DebugViewMode.Metallic),
                    }
                },
            };

            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        // [fix H2] 域重载前取消事件订阅，防止 handler 叠加
        private static void Cleanup()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            var manager = ModelBoxManager.Instance;
            if (manager != null) manager.OnModeChanged -= OnModeChanged;
            _subscribedModeChange = false;
        }

        // 延迟订阅模式变更（避免静态构造时 ModelBoxManager 未初始化）
        private static bool _subscribedModeChange;

        private static void EnsureModeSubscription()
        {
            if (_subscribedModeChange) return;
            _subscribedModeChange = true;
            var manager = ModelBoxManager.Instance;
            if (manager != null)
                manager.OnModeChanged += OnModeChanged;
        }

        private static void OnModeChanged(DebugViewMode mode)
        {
            if (mode == DebugViewMode.None) return;
            _recentModes.Remove(mode);
            _recentModes.Insert(0, mode);
            if (_recentModes.Count > MAX_RECENT_MODES)
                _recentModes.RemoveAt(_recentModes.Count - 1);
        }

        // ==================== 查表辅助 ====================

        /// <summary>
        /// 在所有分组中查找当前模式所属的分组索引，返回 -1 表示 Off。
        /// </summary>
        private static int FindGroupIndex(DebugViewMode mode)
        {
            for (int g = 0; g < Groups.Length; g++)
            {
                for (int e = 0; e < Groups[g].Entries.Length; e++)
                {
                    if (Groups[g].Entries[e].Value == mode)
                        return g;
                }
            }
            return -1;
        }

        /// <summary>格式化大数字为可读形式（如 128K, 1.2M）。</summary>
        private static string FormatLargeNumber(int n)
        {
            if (n >= 1_000_000) return $"{n / 1_000_000f:F1}M";
            if (n >= 1_000) return $"{n / 1_000f:F0}K";
            return n.ToString();
        }

        // [fix] 渲染统计反射缓存（避免每 0.5s 重新查找类型和成员）
        private static System.Type _cachedStatsType;
        private static System.Reflection.MemberInfo _cachedDcMember;
        private static System.Reflection.MemberInfo _cachedTriMember;
        private static System.Reflection.MemberInfo _cachedVertMember;
        private static bool _statsReflectionCached;

        /// <summary>通过反射获取 UnityStats 渲染统计（DrawCalls/Triangles/Vertices）。</summary>
        private static void UpdateRenderStats()
        {
            try
            {
                if (!_statsReflectionCached)
                {
                    _cachedStatsType = System.Type.GetType("UnityEditorInternal.UnityStats, UnityEditor")
                                    ?? System.Type.GetType("UnityEditor.UnityStats, UnityEditor");
                    if (_cachedStatsType != null)
                    {
                        _cachedDcMember = (System.Reflection.MemberInfo)_cachedStatsType.GetProperty("drawCalls")
                                       ?? _cachedStatsType.GetField("drawCalls");
                        _cachedTriMember = (System.Reflection.MemberInfo)_cachedStatsType.GetProperty("triangles")
                                        ?? _cachedStatsType.GetField("triangles");
                        _cachedVertMember = (System.Reflection.MemberInfo)_cachedStatsType.GetProperty("vertices")
                                         ?? _cachedStatsType.GetField("vertices");
                    }
                    _statsReflectionCached = true;
                }

                if (_cachedStatsType != null)
                {
                    if (_cachedDcMember != null)
                    {
                        object val = _cachedDcMember is System.Reflection.PropertyInfo dp
                            ? dp.GetValue(null) : ((System.Reflection.FieldInfo)_cachedDcMember).GetValue(null);
                        _hudDrawCalls = System.Convert.ToInt32(val);
                    }
                    if (_cachedTriMember != null)
                    {
                        object val = _cachedTriMember is System.Reflection.PropertyInfo tp
                            ? tp.GetValue(null) : ((System.Reflection.FieldInfo)_cachedTriMember).GetValue(null);
                        _hudTriCount = System.Convert.ToInt32(val);
                    }
                    if (_cachedVertMember != null)
                    {
                        object val = _cachedVertMember is System.Reflection.PropertyInfo vp
                            ? vp.GetValue(null) : ((System.Reflection.FieldInfo)_cachedVertMember).GetValue(null);
                        _hudVertCount = System.Convert.ToInt32(val);
                    }
                }
                else
                {
                    _hudPerfDiag = "UnityStats not available";
                }
            }
            catch (System.Exception ex)
            {
                _hudPerfDiag = $"Stats error: {ex.GetType().Name}";
            }
        }

        /// <summary>
        /// 获取当前激活模式在所属分组中的缩写名。若为 Off 返回 null。
        /// </summary>
        private static string GetActiveAbbr(DebugViewMode mode)
        {
            for (int g = 0; g < Groups.Length; g++)
            {
                for (int e = 0; e < Groups[g].Entries.Length; e++)
                {
                    if (Groups[g].Entries[e].Value == mode)
                        return Groups[g].Entries[e].Content.text;
                }
            }
            return null;
        }

        /// <summary>获取当前激活模式的中文 tooltip（如 "世界坐标"）。若为 Off 返回 null。</summary>
        private static string GetActiveTooltip(DebugViewMode mode)
        {
            for (int g = 0; g < Groups.Length; g++)
            {
                for (int e = 0; e < Groups[g].Entries.Length; e++)
                {
                    if (Groups[g].Entries[e].Value == mode)
                        return Groups[g].Entries[e].Content.tooltip;
                }
            }
            return null;
        }

        // ==================== GUI 绘制 ====================

        private static void OnSceneGUI(SceneView sceneView)
        {
            EnsureModeSubscription(); // 延迟订阅模式变更
            var manager = ModelBoxManager.Instance;
            if (manager == null) return;

            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowSceneViewOverlay) return;

            Handles.BeginGUI();
            try
            {
                var currentMode = manager.CurrentMode;
                int activeGroupIdx = FindGroupIndex(currentMode);

                // ===== 双行工具栏布局 =====
                float row1Width = 16f + 36f + 68f + 8f + 36f + 4f; // ≡ + Off + 模式▾ + gap + R + pad
                float row2Width = 36f + 42f + 36f + 24f + 24f + 4f + 42f + 4f; // SEL + Split + 冻结 + SA + SB + gap + Snap + pad
                float toolbarWidth = Mathf.Max(row1Width, row2Width) + 8f;
                float toolbarHeight = 54f; // 两行(24px each) + 间距(6px)

                // [feat] 工具栏可拖拽位置（持久化到 Settings）
                float tbX = settings.ToolbarX;
                float tbY = settings.ToolbarY;

                // 拖拽手柄区域（工具栏最左侧 16px 区域，跨两行）
                var gripRect = new Rect(tbX, tbY, 16f, toolbarHeight);
                EditorGUIUtility.AddCursorRect(gripRect, MouseCursor.MoveArrow);

                var e = Event.current;
                if (e.type == EventType.MouseDown && gripRect.Contains(e.mousePosition))
                {
                    _toolbarDragId = GUIUtility.GetControlID(FocusType.Passive);
                    GUIUtility.hotControl = _toolbarDragId;
                    _toolbarDragOffset = e.mousePosition - new Vector2(tbX, tbY);
                    e.Use();
                }
                if (e.type == EventType.MouseDrag && GUIUtility.hotControl == _toolbarDragId)
                {
                    tbX = e.mousePosition.x - _toolbarDragOffset.x;
                    tbY = e.mousePosition.y - _toolbarDragOffset.y;
                    settings.ToolbarX = tbX;
                    settings.ToolbarY = tbY;
                    settings.Save();
                    e.Use();
                }
                if (e.type == EventType.MouseUp && GUIUtility.hotControl == _toolbarDragId)
                {
                    GUIUtility.hotControl = 0;
                _toolbarDragId = -1;
                e.Use();
            }

            var toolbarBg = EditorGUIUtility.isProSkin
                ? new Color(0.19f, 0.19f, 0.19f, 0.95f)
                : new Color(0.78f, 0.78f, 0.78f, 0.95f);
            EditorGUI.DrawRect(new Rect(tbX, tbY, toolbarWidth, toolbarHeight), toolbarBg);

            // ===== Row 1: 模式控制 =====
            float rowY = tbY + 1f;
            GUILayout.BeginArea(new Rect(tbX, rowY, toolbarWidth, 24));
            GUILayout.BeginHorizontal();

            // 拖拽手柄
            GUILayout.Label("≡", GUILayout.Width(14));

            // Off 按钮
            {
                var oldBg = GUI.backgroundColor;
                if (currentMode == DebugViewMode.None)
                    GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();
                if (GUILayout.Button(_offContent, EditorStyles.toolbarButton, GUILayout.Width(36)))
                {
                    // [fix v0.6.x] 消费鼠标事件，防止点击穿透到 SceneView 触发相机操作
                    e.Use();
                    manager.SetDebugMode(DebugViewMode.None);
                }
                GUI.backgroundColor = oldBg;
            }

            // 模式下拉
            {
                string modeLabel = currentMode == DebugViewMode.None ? "模式" : GetActiveTooltip(currentMode) ?? "模式";
                string modeTooltip = currentMode == DebugViewMode.None
                    ? "选择调试模式（按分类浏览）"
                    : $"当前: {GetActiveTooltip(currentMode)}\n点击切换其他模式";
                var modeContent = new GUIContent(modeLabel, modeTooltip);
                bool modeClicked = EditorGUI.DropdownButton(
                    GUILayoutUtility.GetRect(modeContent, EditorStyles.toolbarButton, GUILayout.Width(68)),
                    modeContent, FocusType.Passive, EditorStyles.toolbarButton);
                if (modeClicked)
                {
                    // [fix] 延迟显示菜单，避免在 Handles.BeginGUI/EndGUI 之间调用导致递归 OnGUI
                    var capturedMode = currentMode;
                    EditorApplication.delayCall += () =>
                    {
                        var modeMenu = new GenericMenu();
                        for (int g = 0; g < Groups.Length; g++)
                        {
                            var group = Groups[g];
                            for (int ei = 0; ei < group.Entries.Length; ei++)
                            {
                                var entry = group.Entries[ei];
                                bool isOn = (entry.Value == capturedMode);
                                int cg = g, ce = ei;
                                modeMenu.AddItem(
                                    new GUIContent($"{group.Label}/{entry.Content.tooltip}"),
                                    isOn, () => manager.SetDebugMode(Groups[cg].Entries[ce].Value));
                            }
                        }
                        modeMenu.ShowAsContext();
                    };
                }
            }

            // 间隔
            GUILayout.Space(6);

            // 最近模式下拉
            if (_recentModes.Count > 0)
            {
                var recentContent = new GUIContent("R", "最近使用的调试模式");
                bool recentClicked = EditorGUI.DropdownButton(
                    GUILayoutUtility.GetRect(recentContent, EditorStyles.toolbarButton, GUILayout.Width(28)),
                    recentContent, FocusType.Passive, EditorStyles.toolbarButton);
                if (recentClicked)
                {
                    // [fix] 延迟显示菜单，避免在 Handles.BeginGUI/EndGUI 之间调用导致递归 OnGUI
                    var capturedMode2 = currentMode;
                    EditorApplication.delayCall += () =>
                    {
                        var recentMenu = new GenericMenu();
                        foreach (var mode in _recentModes)
                        {
                            var cm = mode;
                            recentMenu.AddItem(
                                new GUIContent(GetActiveTooltip(mode) ?? mode.ToString()),
                                mode == capturedMode2, () => manager.SetDebugMode(cm));
                        }
                        recentMenu.ShowAsContext();
                    };
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ===== Row 2: 视图工具 =====
            rowY += 26f;
            GUILayout.BeginArea(new Rect(tbX, rowY, toolbarWidth, 24));
            GUILayout.BeginHorizontal();

            GUILayout.Label(" ", GUILayout.Width(14)); // 对齐拖拽手柄

            // SEL 按钮
            {
                var oldBg = GUI.backgroundColor;
                if (manager.DebugOnlySelected)
                    GUI.backgroundColor = ModelBoxStyles.SelButtonActiveColor;
                if (GUILayout.Button(_selContent, EditorStyles.toolbarButton, GUILayout.Width(36)))
                {
                    // [fix v0.6.x] 消费鼠标事件，防止事件穿透到 SceneView
                    e.Use();
                    manager.ToggleDebugOnlySelected();
                }
                GUI.backgroundColor = oldBg;
            }

            // Split 按钮
            {
                var oldBg = GUI.backgroundColor;
                if (manager.SplitScreenEnabled)
                    GUI.backgroundColor = new Color(0.40f, 0.58f, 0.85f, 0.8f);
                if (GUILayout.Button(_splitContent, EditorStyles.toolbarButton, GUILayout.Width(42)))
                {
                    e.Use();
                    manager.SetSplitScreen(!manager.SplitScreenEnabled);
                }
                GUI.backgroundColor = oldBg;
            }

            // 冻结按钮（禁用灰显而非隐藏，避免布局跳动）
            {
                var oldBg = GUI.backgroundColor;
                if (!manager.SplitScreenEnabled)
                    GUI.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                else if (manager.FreezeLeftSnapshot)
                    GUI.backgroundColor = new Color(0.85f, 0.62f, 0.25f, 0.8f);
                bool freezeClicked = GUILayout.Button(_freezeContent, EditorStyles.toolbarButton, GUILayout.Width(36));
                GUI.backgroundColor = oldBg;
                if (freezeClicked && manager.SplitScreenEnabled)
                {
                    e.Use();
                    manager.SetFreezeLeftSnapshot(!manager.FreezeLeftSnapshot);
                }
            }

            // 快照 A/B（禁用灰显而非隐藏）
            {
                var snapBg = GUI.backgroundColor;
                bool hasSnapA = manager.SnapshotMode == 1 || manager.SnapshotMode == 2;
                bool hasSnapB = manager.SnapshotMode == 2 || manager.SnapshotMode == 3;
                if (!manager.SplitScreenEnabled)
                    GUI.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                else if (hasSnapA)
                    GUI.backgroundColor = new Color(0.30f, 0.65f, 0.40f, 0.7f);
                if (GUILayout.Button(_snapAContent, EditorStyles.toolbarButton, GUILayout.Width(24)))
                {
                    e.Use();
                    if (manager.SplitScreenEnabled)
                    {
                        if (manager.SnapshotMode == 0) manager.SaveSnapshotA();
                        else manager.CycleSnapshotMode();
                    }
                }
                GUI.backgroundColor = snapBg;
                if (!manager.SplitScreenEnabled)
                    GUI.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                else if (hasSnapB)
                    GUI.backgroundColor = new Color(0.45f, 0.55f, 0.80f, 0.7f);
                if (GUILayout.Button(_snapBContent, EditorStyles.toolbarButton, GUILayout.Width(24)))
                {
                    e.Use();
                    if (manager.SplitScreenEnabled)
                    {
                        if (manager.SnapshotMode <= 1) manager.SaveSnapshotB();
                        else manager.CycleSnapshotMode();
                    }
                }
                GUI.backgroundColor = snapBg;
            }

            // 间隔
            GUILayout.Space(4);

            // Snap 截图按钮
            if (GUILayout.Button(_snapContent, EditorStyles.toolbarButton, GUILayout.Width(42)))
            {
                e.Use();
                ScreenshotCapture.CaptureSceneView();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ===== 右上角调试指示器 =====
            if (currentMode != DebugViewMode.None)
            {
                string selTag = manager.DebugOnlySelected ? " [SEL]" : "";
                _debugLabelContent.text = $"[modelBox: {currentMode}{selTag}]";
                var labelStyle = EditorStyles.boldLabel;
                var labelSize = labelStyle.CalcSize(_debugLabelContent);
                var labelRect = new Rect(
                    sceneView.cameraViewport.width - labelSize.x - 20,
                    8,
                    labelSize.x + 10,
                    labelSize.y + 4
                );
                EditorGUI.DrawRect(labelRect, ModelBoxStyles.DebugIndicatorColor);
                GUI.Label(labelRect, _debugLabelContent, labelStyle);
            }

            // ===== 分屏分割线（可拖拽） =====
            if (manager.SplitScreenEnabled && currentMode != DebugViewMode.None)
            {
                DrawSplitDivider(sceneView, manager);
            }

            // ===== 左下角统计 HUD =====
            DrawPerformanceStatsHUD();
            DrawMeshStatsHUD();
            }
            finally
            {
                // [fix] 确保 EndGUI 始终被调用，防止渲染状态泄漏导致递归渲染错误
                Handles.EndGUI();
            }
        }

        // ===== 分屏分割线 =====

        private static int _splitDragControlId = -1;

        /// <summary>
        /// 绘制可拖拽的分屏分割线。使用 GUIUtility.hotControl 消耗鼠标事件，
        /// 防止拖拽时触发 SceneView 相机旋转/平移。
        /// </summary>
        private static void DrawSplitDivider(SceneView sceneView, ModelBoxManager manager)
        {
            var viewRect = sceneView.cameraViewport;
            float dividerX = viewRect.width * manager.SplitScreenPosition;

            // 分割线手柄区域（8px 宽，上下留边距）
            var handleRect = new Rect(dividerX - 4, 40, 8, viewRect.height - 80);

            // [feat] 拖拽时显示水平调整光标
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeHorizontal);

            // 绘制半透明手柄
            EditorGUI.DrawRect(handleRect, new Color(1, 1, 1, 0.25f));

            // 手柄中间的抓取条纹（3 条短横线）
            float centerY = handleRect.y + handleRect.height * 0.5f;
            for (int i = -1; i <= 1; i++)
            {
                var stripeRect = new Rect(dividerX - 3, centerY + i * 6 - 1, 6, 2);
                EditorGUI.DrawRect(stripeRect, new Color(1, 1, 1, 0.6f));
            }

            // 鼠标拖拽处理
            var controlId = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (handleRect.Contains(e.mousePosition))
                    {
                        // [feat] 双击重置到 50%
                        if (e.clickCount == 2)
                        {
                            manager.SetSplitPosition(0.5f);
                            e.Use();
                        }
                        else
                        {
                            _splitDragControlId = controlId;
                            GUIUtility.hotControl = controlId;
                            e.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (_splitDragControlId == controlId)
                    {
                        // [fix] 限制拖拽范围 5%~95%（防止分割线完全消失）
                        float newPos = Mathf.Clamp(e.mousePosition.x / viewRect.width, 0.05f, 0.95f);
                        // [feat] 三段吸附：边缘(5%/95%) + 中心(50%)
                        if (Mathf.Abs(newPos - 0.5f) < 0.03f)
                            newPos = 0.5f;
                        else if (newPos < 0.08f)
                            newPos = 0.05f; // 吸附到左边缘（全屏调试模式）
                        else if (newPos > 0.92f)
                            newPos = 0.95f; // 吸附到右边缘（全屏正常场景）
                        manager.SetSplitPosition(newPos);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (_splitDragControlId == controlId)
                    {
                        _splitDragControlId = -1;
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }

            // 左右标签
            var labelStyle = EditorStyles.miniLabel;
            GUI.Label(new Rect(8, viewRect.height - 50, 60, 16), "正常渲染", labelStyle);
            GUI.Label(new Rect(viewRect.width - 80, viewRect.height - 50, 72, 16), "调试模式", labelStyle);
        }

        private static float _hudFps;
        private static float _hudFpsAccum;
        private static int _hudFpsFrameCount;
        private static double _hudFpsNextUpdate;
        private static double _hudLastFrameTime; // [fix] 使用 EditorApplication.timeSinceStartup 替代 Time.unscaledDeltaTime
        private static int _hudDrawCalls, _hudTriCount, _hudVertCount; // [feat] 渲染统计缓存
        private static string _hudPerfDiag = ""; // 反射诊断信息
        private static readonly Color _perfAccentColor = new Color(0.35f, 0.70f, 0.45f, 1f);
        private static GUIStyle _perfStyle;
        private static readonly GUIContent _perfContent = new GUIContent();
        private static Rect _cachedPerfHudRect; // [fix] 缓存 Performance HUD 位置，供 Mesh Stats 锚定
        private static System.Text.StringBuilder _hudStringBuilder; // [perf P1] 缓存 HUD 字符串构建

        /// <summary>
        /// 左下角性能统计 HUD：显示 FPS、Draw Calls、Triangles、Vertices、总分配内存、Mono 堆内存。
        /// 数据每 0.5 秒更新一次（避免刷新过快看不清）。
        /// </summary>
        private static void DrawPerformanceStatsHUD()
        {
            // 读取设置开关（默认开启）
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowPerformanceHUD) return;

            // [fix v0.4.1] currentDrawingSceneView 可能为 null（非 duringSceneGui 上下文调用时）
            var sv = SceneView.currentDrawingSceneView;
            if (sv == null) return;

            // [fix] FPS 计算：使用 EditorApplication.timeSinceStartup 替代 Time.unscaledDeltaTime
            // Time.unscaledDeltaTime 在 Editor/SceneView 中不可靠（游戏循环时钟，非编辑器刷新时钟）
            double now = EditorApplication.timeSinceStartup;
            if (_hudLastFrameTime > 0)
            {
                float dt = (float)(now - _hudLastFrameTime);
                // [fix] 钳制 dt 到合理范围，忽略 >200ms 的卡顿尖刺（如切换窗口、编译暂停）
                if (dt > 0f && dt < 0.2f)
                {
                    _hudFpsAccum += dt;
                    _hudFpsFrameCount++;
                }
            }
            _hudLastFrameTime = now;

            if (now > _hudFpsNextUpdate)
            {
                _hudFps = _hudFpsAccum > 0.001f ? _hudFpsFrameCount / _hudFpsAccum : 0;
                _hudFpsAccum = 0;
                _hudFpsFrameCount = 0;

                // 渲染统计必须在 FPS 更新块内（共享同一时钟）
                UpdateRenderStats();

                _hudFpsNextUpdate = now + 0.5;
            }

            // 渲染统计（使用 Unity 内置 Profiler API，兼容所有 Unity 版本）
            long memMB = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            long gcMemMB = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024);

            if (_perfStyle == null)
            {
                _perfStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = _hudTextColor },
                    fontSize = 10,
                    padding = new RectOffset(8, 8, 3, 3)
                };
            }

            string fpsStr = _hudFps > 0 ? $"{_hudFps:F0}" : "--";
            string dcStr = _hudDrawCalls > 0 ? $"{_hudDrawCalls}" : (string.IsNullOrEmpty(_hudPerfDiag) ? "N/A" : "ERR");
            string triStr = _hudTriCount > 0 ? FormatLargeNumber(_hudTriCount) : "--";
            string vertStr = _hudVertCount > 0 ? FormatLargeNumber(_hudVertCount) : "--";

            // [perf P1] HUD 逐项配置：使用 StringBuilder 替代 List<string> + string.Join，消除堆分配
            var sb = _hudStringBuilder;
            if (sb == null) _hudStringBuilder = sb = new System.Text.StringBuilder(128);
            sb.Clear();
            if (settings.HudShowFPSBeta) { if (sb.Length > 0) sb.Append("  |  "); sb.Append("FPS: ").Append(fpsStr).Append(" [β]"); }
            if (settings.HudShowDC)     { if (sb.Length > 0) sb.Append("  |  "); sb.Append("DC: ").Append(dcStr); }
            if (settings.HudShowTri)    { if (sb.Length > 0) sb.Append("  |  "); sb.Append("Tri: ").Append(triStr); }
            if (settings.HudShowVert)   { if (sb.Length > 0) sb.Append("  |  "); sb.Append("Vert: ").Append(vertStr); }
            if (settings.HudShowMem)    { if (sb.Length > 0) sb.Append("  |  "); sb.Append("总分配: ").Append(memMB).Append(" MB"); }
            if (settings.HudShowGC)     { if (sb.Length > 0) sb.Append("  |  "); sb.Append("C#堆: ").Append(gcMemMB).Append(" MB"); }
            _perfContent.text = sb.Length > 0 ? sb.Insert(0, "  ").Append("  ").ToString() : "  (HUD 指标已全部隐藏)  ";
            // 反射失败时附加诊断信息
            if (_hudDrawCalls == 0 && !string.IsNullOrEmpty(_hudPerfDiag))
                _perfContent.text += $"  [{_hudPerfDiag}]";
            var size = _perfStyle.CalcSize(_perfContent);
            float hudWidth = Mathf.Max(size.x + 16, 360);
            float hudHeight = size.y + 6;

            var viewRect = sv.cameraViewport;

            // [fix] 锚定在 PixelBar 上方（PixelBar 高 38px + 2px 底部偏移 = 40px）
            float pixelBarZone = 42f; // PixelBar 占用的底部空间 + 4px 间距
            float y = viewRect.height - hudHeight - pixelBarZone;

            var hudRect = new Rect(8, y, hudWidth, hudHeight);
            _cachedPerfHudRect = hudRect; // 缓存供 Mesh Stats 锚定

            EditorGUI.DrawRect(hudRect, _hudBgColor);

            // 左侧色条（绿色 = 性能）
            var accentRect = new Rect(hudRect.x, hudRect.y, 3, hudHeight);
            EditorGUI.DrawRect(accentRect, _perfAccentColor);

            GUI.Label(hudRect, _perfContent, _perfStyle);
        }

        // ===== Mesh 统计 HUD（左下角）=====

        private static readonly Color _hudBgColor = new Color(0.1f, 0.1f, 0.1f, 0.82f);
        private static readonly Color _hudTextColor = new Color(0.85f, 0.9f, 0.95f, 1f);
        private static readonly Color _hudAccentColor = new Color(0.45f, 0.62f, 0.82f, 1f);
        private static GUIStyle _hudStyle;
        private static readonly GUIContent _hudContent = new GUIContent(); // [R6 fix] 缓存复用
        // [perf v0.6.x] Mesh 统计 HUD 文本缓存：切换选中立即重建，同一选中 0.5s 刷新一次
        private static string _hudMeshCachedText;
        private static Transform _hudMeshLastSelection;
        private static double _hudMeshNextUpdate;

        /// <summary>
        /// 左下角 Mesh 统计 HUD：显示选中物体的顶点/面数/材质数/子网格数。
        /// [perf v0.6.x] 文本缓存：切换选中立即重建，同一选中 0.5s 刷新一次 ——
        /// 高面数模型 BuildHudText 的 GetIndexCount 遍历不便宜，不应每帧全量执行。
        /// </summary>
        private static void DrawMeshStatsHUD()
        {
            var selected = Selection.activeTransform;
            if (selected == null)
            {
                // 取消选择时清缓存
                _hudMeshCachedText = null;
                _hudMeshLastSelection = null;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_hudMeshCachedText == null || !ReferenceEquals(selected, _hudMeshLastSelection) || now >= _hudMeshNextUpdate)
            {
                _hudMeshCachedText = BuildHudText(selected);
                _hudMeshLastSelection = selected;
                _hudMeshNextUpdate = now + 0.5;
            }
            string text = _hudMeshCachedText;
            if (text == null) return;

            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = _hudTextColor },
                    fontSize = 11,
                    padding = new RectOffset(8, 8, 4, 4)
                };
            }

            _hudContent.text = text; // [R6 fix] 缓存复用，不 new
            var size = _hudStyle.CalcSize(_hudContent);
            float hudWidth = Mathf.Max(size.x + 16, 180);
            float hudHeight = size.y + 8;

            // [fix] 左下角定位，锚定在 Performance HUD 上方
            // [fix v0.4.1] currentDrawingSceneView 可能为 null
            var currentSv = SceneView.currentDrawingSceneView;
            if (currentSv == null) return;
            var viewRect = currentSv.cameraViewport;
            float anchorTop = _cachedPerfHudRect.height > 0
                ? _cachedPerfHudRect.y      // Performance HUD 顶部
                : viewRect.height - 42f;    // 无 Performance HUD 时的回退位置
            float y = Mathf.Max(8, anchorTop - hudHeight - 4);
            var hudRect = new Rect(8, y, hudWidth, hudHeight);

            EditorGUI.DrawRect(hudRect, _hudBgColor);

            // 左侧色条
            var accentRect = new Rect(hudRect.x, hudRect.y, 3, hudHeight);
            EditorGUI.DrawRect(accentRect, _hudAccentColor);

            GUI.Label(hudRect, _hudContent, _hudStyle);
        }

        private static string BuildHudText(Transform selected)
        {
            int verts = 0, tris = 0, mats = 0, subMeshes = 0;
            string meshName = null;
            bool isSkinned = false;

            // 统计所有子 Renderer
            var renderers = selected.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return null;

            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                // [fix v0.6.x] 与检查页统计口径一致：排除 LOD 非活跃级别与未激活物体
                mats += r.sharedMaterials.Length;

                if (r is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        var m = mf.sharedMesh;
                        verts += m.vertexCount;
                        for (int si = 0; si < m.subMeshCount; si++)
                            tris += (int)(m.GetIndexCount(si) / 3);
                        subMeshes += m.subMeshCount;
                        meshName ??= m.name;
                    }
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    isSkinned = true;
                    if (smr.sharedMesh != null)
                    {
                        var m = smr.sharedMesh;
                        verts += m.vertexCount;
                        for (int si = 0; si < m.subMeshCount; si++)
                            tris += (int)(m.GetIndexCount(si) / 3);
                        subMeshes += m.subMeshCount;
                        meshName ??= m.name;
                    }
                }
            }

            if (verts == 0) return null;

            string meshType = isSkinned ? "Skinned" : "Mesh";
            string name = selected.name;
            if (meshName != null && meshName != name)
                name = $"{name} ({meshName})";

            return $"  {name}\n  {meshType}  ·  {verts:N0} vtx  ·  {tris:N0} tri\n  {mats} mat  ·  {subMeshes} sub";
        }
    }
}
