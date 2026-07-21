using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 选区叠加 UI 面板。四个子标签：贴图通道、UV 棋盘格、Shader 属性、网格叠加。
    /// 骨骼功能已独立到 ModelBoxBonePanel（侧边栏「骨骼」页）。
    /// </summary>
    public class ModelBoxSelectionInspector
    {
        private int _selectedTab;
        private readonly string[] _tabNames = { "贴图通道", "UV 棋盘格", "Shader 属性", "网格叠加" };

        // [perf P4] 缓存字符串数组，避免每帧分配
        private static readonly string[] _uvChannelOptions = { "UV0", "UV1", "UV2", "UV3" };
        private static readonly string[] _channelOptions = { "RGB", "R", "G", "B", "A" };

        // Texture Channel
        private int _channelMask = 0; // 0=RGB, 1=R, 2=G, 3=B, 4=A
        private Texture _customTexture; // 用户自定义贴图槽
        private int _uvChannel = 0; // 0=UV0, 1=UV1, 2=UV2, 3=UV3
        private Vector2 _texScale = Vector2.one;
        private Vector2 _texOffset = Vector2.zero;
        private float _worldUVScale = 0.01f;
        private float _clampMin = 0f; // 亮度钳制下限
        private float _clampMax = 1f; // 亮度钳制上限
        private bool _textureChannelActive;

        // Checkerboard
        private float _gridSize = 10f;
        private Color _colorA = new Color(0.9f, 0.9f, 0.9f, 1f);
        private Color _colorB = new Color(0.2f, 0.2f, 0.2f, 1f);
        private bool _checkerboardActive;

        // Shader Property
        private string _propertySearchFilter = "";
        private Color _propertyColor;
        private bool _shaderPropertyActive;
        private Vector2 _propertyScrollPos;

        // 缓存属性列表
        private Shader _cachedShader;
        private List<CachedProperty> _cachedProperties = new List<CachedProperty>();

        private struct CachedProperty
        {
            public string Name;
            public string Description;
            public ShaderPropertyType Type;
        }

        public void Draw()
        {
            var manager = ModelBoxSelectionManager.Instance;
            if (manager == null) return;

            // 同步激活状态（窗口重新打开时恢复）
            _textureChannelActive = (manager.CurrentMode == SelectionDebugMode.TextureChannel);
            _checkerboardActive = (manager.CurrentMode == SelectionDebugMode.Checkerboard);
            _shaderPropertyActive = (manager.CurrentMode == SelectionDebugMode.ShaderProperty);

            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames);

            // R6: 子标签页导航与内容分隔线
            var subNavSepRect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(subNavSepRect, ModelBoxStyles.NavSeparatorColor);
            EditorGUILayout.Space(6);

            switch (_selectedTab)
            {
                case 0: DrawTextureChannel(manager); break;
                case 1: DrawCheckerboard(manager); break;
                case 2: DrawShaderProperty(manager); break;
                case 3: DrawMeshOverlaySection(manager); break;
            }

            EditorGUILayout.Space(8);

            // R4: "恢复原始材质"是安全回退操作，使用中性色而非红色
            ModelBoxStyles.BeginNeutralButton();
            if (GUILayout.Button("恢复原始材质", GUILayout.Height(24)))
            {
                _textureChannelActive = false;
                _checkerboardActive = false;
                _shaderPropertyActive = false;
                manager.RestoreOriginalMaterials();
                manager.SetMode(SelectionDebugMode.None);
            }
            ModelBoxStyles.EndNeutralButton();
        }

        private void DrawTextureChannel(ModelBoxSelectionManager manager)
        {
            EditorGUILayout.LabelField("贴图通道隔离", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("选中物体的主贴图，按通道显示。可拖入自定义贴图。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            if (Selection.activeTransform == null)
            {
                EditorGUILayout.HelpBox("请先在 Scene 中选中一个物体。", MessageType.Info);
                return;
            }

            // 贴图槽
            _customTexture = (Texture)EditorGUILayout.ObjectField("自定义贴图（可选）", _customTexture, typeof(Texture), false);

            // UV 通道选择
            _uvChannel = EditorGUILayout.Popup("UV 通道", _uvChannel, _uvChannelOptions);

            // 世界坐标采样
            bool newWorldUV = EditorGUILayout.Toggle("使用世界坐标采样", manager.WorldSpaceUV);
            if (newWorldUV)
            {
                EditorGUI.indentLevel++;
                _worldUVScale = EditorGUILayout.Slider(
                    new GUIContent("世界坐标缩放", "世界坐标 → UV 的缩放倍数。值越小纹理越大，值越大纹理越小。"),
                    _worldUVScale, 0.001f, 10f);
                EditorGUI.indentLevel--;
            }

            // 缩放和偏移
            EditorGUI.BeginChangeCheck();
            _texScale = EditorGUILayout.Vector2Field("UV 缩放 (Tiling)", _texScale);
            _texOffset = EditorGUILayout.Vector2Field("UV 偏移 (Offset)", _texOffset);
            bool transformChanged = EditorGUI.EndChangeCheck();

            EditorGUILayout.Space(2);
            int newChannelMask = GUILayout.SelectionGrid(_channelMask,
                _channelOptions, 5);

            // 亮度范围钳制（Lightness Map / Ramp Mask 调试）
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("亮度范围钳制", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "只显示指定数值范围内的区域。用于 Lightness Map / Ramp Mask 调试。\n" +
                "范围外显示为暗色，精确定位 shader 中 step() 的阈值边界。",
                MessageType.None);
            EditorGUI.BeginChangeCheck();
            _clampMin = EditorGUILayout.Slider("Min", _clampMin, 0f, 1f);
            _clampMax = EditorGUILayout.Slider("Max", _clampMax, 0f, 1f);
            // 确保 Min <= Max
            if (_clampMin > _clampMax) _clampMax = _clampMin;
            bool clampChanged = EditorGUI.EndChangeCheck();

            if (!_textureChannelActive)
            {
                if (GUILayout.Button("应用通道隔离", GUILayout.Height(28)))
                {
                    _textureChannelActive = true;
                    _channelMask = newChannelMask; // [M3-2 fix] 使用最新选择
                    manager.CustomTexture = _customTexture;
                    manager.ChannelMask = _channelMask;
                    manager.UVChannel = _uvChannel;
                    manager.TextureScale = _texScale;
                    manager.TextureOffset = _texOffset;
                    manager.WorldSpaceUV = newWorldUV;
                    manager.WorldUVScale = _worldUVScale;
                    manager.ClampMin = _clampMin;
                    manager.ClampMax = _clampMax;
                    manager.SetMode(SelectionDebugMode.TextureChannel);
                }
            }
            else
            {
                // 实时更新：参数变化时立即推送（包括自定义贴图和 UV 通道变化）
                bool needUpdate = transformChanged || newChannelMask != _channelMask
                    || newWorldUV != manager.WorldSpaceUV || clampChanged
                    || _worldUVScale != manager.WorldUVScale
                    || _customTexture != manager.CustomTexture
                    || _uvChannel != manager.UVChannel;
                if (needUpdate)
                {
                    _channelMask = newChannelMask;
                    manager.CustomTexture = _customTexture;
                    manager.ChannelMask = _channelMask;
                    manager.UVChannel = _uvChannel;
                    manager.TextureScale = _texScale;
                    manager.TextureOffset = _texOffset;
                    manager.WorldSpaceUV = newWorldUV;
                    manager.WorldUVScale = _worldUVScale;
                    manager.ClampMin = _clampMin;
                    manager.ClampMax = _clampMax;
                    manager.UpdateTextureChannelProperties();
                }
                else
                {
                    _channelMask = newChannelMask;
                    manager.WorldSpaceUV = newWorldUV;
                }

                if (GUILayout.Button("关闭通道隔离", GUILayout.Height(28)))
                {
                    _textureChannelActive = false;
                    manager.RestoreOriginalMaterials();
                    manager.SetMode(SelectionDebugMode.None);
                }
            }
        }

        private void DrawCheckerboard(ModelBoxSelectionManager manager)
        {
            EditorGUILayout.LabelField("UV 棋盘格", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("在选中物体上显示 UV 棋盘格，检查 UV 拉伸和接缝。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            if (Selection.activeTransform == null)
            {
                EditorGUILayout.HelpBox("请先在 Scene 中选中一个物体。", MessageType.Info);
                return;
            }

            EditorGUI.BeginChangeCheck();
            _gridSize = EditorGUILayout.Slider("网格密度", _gridSize, 2f, 64f);
            _colorA = EditorGUILayout.ColorField("颜色 A", _colorA);
            _colorB = EditorGUILayout.ColorField("颜色 B", _colorB);
            bool changed = EditorGUI.EndChangeCheck();

            if (!_checkerboardActive)
            {
                if (GUILayout.Button("应用棋盘格", GUILayout.Height(28)))
                {
                    _checkerboardActive = true;
                    manager.CheckerGridSize = _gridSize;
                    manager.CheckerColorA = _colorA;
                    manager.CheckerColorB = _colorB;
                    manager.SetMode(SelectionDebugMode.Checkerboard);
                }
            }
            else
            {
                // 棋盘格已激活，参数变化时实时更新材质
                if (changed)
                {
                    manager.CheckerGridSize = _gridSize;
                    manager.CheckerColorA = _colorA;
                    manager.CheckerColorB = _colorB;
                    manager.UpdateCheckerboardProperties();
                }

                if (GUILayout.Button("关闭棋盘格", GUILayout.Height(28)))
                {
                    _checkerboardActive = false;
                    manager.RestoreOriginalMaterials();
                    manager.SetMode(SelectionDebugMode.None);
                }
            }
        }

        private void DrawShaderProperty(ModelBoxSelectionManager manager)
        {
            EditorGUILayout.LabelField("Shader 属性可视化", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("选中物体后自动列出所有材质属性，点击即可查看颜色值并应用到物体。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            if (Selection.activeTransform == null)
            {
                EditorGUILayout.HelpBox("请先在 Scene 中选中一个物体。", MessageType.Info);
                return;
            }

            var renderer = Selection.activeTransform.GetComponentInChildren<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null)
            {
                EditorGUILayout.HelpBox("选中物体没有 Renderer 或材质。", MessageType.Info);
                return;
            }

            var material = manager.GetOriginalMaterial(renderer) ?? renderer.sharedMaterial;
            var shader = material.shader;

            // 缓存失效检测
            if (shader != _cachedShader)
            {
                _cachedShader = shader;
                _cachedProperties.Clear();
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    var flags = shader.GetPropertyFlags(i);
                    if ((flags & ShaderPropertyFlags.HideInInspector) != 0) continue;
                    // 只列出 Color 和 Float 类型（可可视化为纯色）
                    var type = shader.GetPropertyType(i);
                    if (type != ShaderPropertyType.Color && type != ShaderPropertyType.Float &&
                        type != ShaderPropertyType.Range && type != ShaderPropertyType.Vector)
                        continue;

                    _cachedProperties.Add(new CachedProperty
                    {
                        Name = shader.GetPropertyName(i),
                        Description = shader.GetPropertyDescription(i),
                        Type = type
                    });
                }
            }

            // 搜索框
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("搜索", GUILayout.Width(32));
            _propertySearchFilter = EditorGUILayout.TextField(_propertySearchFilter);
            if (GUILayout.Button("X", GUILayout.Width(20)))
                _propertySearchFilter = "";
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{_cachedProperties.Count} 个可读属性", EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            // 属性列表
            string filterLower = _propertySearchFilter?.ToLower() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(_propertySearchFilter);

            _propertyScrollPos = EditorGUILayout.BeginScrollView(_propertyScrollPos, GUILayout.MinHeight(80), GUILayout.MaxHeight(200));

            for (int i = 0; i < _cachedProperties.Count; i++)
            {
                var prop = _cachedProperties[i];
                if (hasFilter &&
                    !(prop.Name?.ToLower().Contains(filterLower) ?? false) &&
                    !(prop.Description?.ToLower().Contains(filterLower) ?? false))
                    continue;

                DrawPropertyListItem(manager, material, prop);
            }

            EditorGUILayout.EndScrollView();

            // 显示当前读取结果
            if (!string.IsNullOrEmpty(manager.PropertyColorInfo))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(manager.PropertyColorInfo, EditorStyles.helpBox);
            }

            EditorGUILayout.Space(4);

            if (!_shaderPropertyActive)
            {
                if (GUILayout.Button("应用属性颜色到物体", GUILayout.Height(28)))
                {
                    _shaderPropertyActive = true;
                    manager.SetMode(SelectionDebugMode.ShaderProperty);
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                manager.PropertyColor = EditorGUILayout.ColorField("当前颜色", manager.PropertyColor);
                if (EditorGUI.EndChangeCheck())
                    manager.UpdateShaderPropertyColor();

                if (GUILayout.Button("关闭属性颜色", GUILayout.Height(28)))
                {
                    _shaderPropertyActive = false;
                    manager.RestoreOriginalMaterials();
                    manager.SetMode(SelectionDebugMode.None);
                }
            }
        }

        private void DrawPropertyListItem(ModelBoxSelectionManager manager, Material material, CachedProperty prop)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // 属性名
            EditorGUILayout.LabelField(prop.Name, EditorStyles.boldLabel, GUILayout.MinWidth(80));

            // 类型标签
            var prevColor = GUI.contentColor;
            GUI.contentColor = ModelBoxStyles.GetPropertyTypeLabelColor(prop.Type);
            EditorGUILayout.LabelField(prop.Type.ToString(), EditorStyles.miniLabel, GUILayout.Width(45));
            GUI.contentColor = prevColor;

            // 当前值预览（只读色块）
            if (material.HasProperty(prop.Name))
            {
                if (prop.Type == ShaderPropertyType.Color)
                {
                    var c = material.GetColor(prop.Name);
                    var rect = EditorGUILayout.GetControlRect(GUILayout.Width(24), GUILayout.Height(16));
                    EditorGUI.DrawRect(rect, c);
                }
                else if (prop.Type == ShaderPropertyType.Float || prop.Type == ShaderPropertyType.Range)
                {
                    float v = material.GetFloat(prop.Name);
                    EditorGUILayout.LabelField(v.ToString("F2"), EditorStyles.miniLabel, GUILayout.Width(40));
                }
                else if (prop.Type == ShaderPropertyType.Vector)
                {
                    EditorGUILayout.LabelField("vec4", EditorStyles.miniLabel, GUILayout.Width(30));
                }
            }

            // "读取" 按钮
            if (GUILayout.Button("读取", GUILayout.Width(36), GUILayout.Height(20)))
            {
                manager.TryGetPropertyColor(prop.Name);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawMeshOverlaySection(ModelBoxSelectionManager manager)
        {
            EditorGUILayout.LabelField("网格叠加", EditorStyles.boldLabel);

            if (Selection.activeTransform == null)
            {
                EditorGUILayout.LabelField("请先选中物体以启用叠加。", EditorStyles.miniLabel);
                return;
            }

            var flags = manager.OverlayFlags;
            bool wire = (flags & MeshOverlayFlags.Wireframe) != 0;
            bool vert = (flags & MeshOverlayFlags.Vertices) != 0;
            bool norm = (flags & MeshOverlayFlags.Normals) != 0;
            bool tan = (flags & MeshOverlayFlags.Tangents) != 0;
            bool bnd = (flags & MeshOverlayFlags.Bounds) != 0;

            // 开关按钮行（带激活颜色高亮）
            EditorGUILayout.BeginHorizontal();
            DrawOverlayToggle(" 线框 ", ref flags, MeshOverlayFlags.Wireframe, wire);
            DrawOverlayToggle(" 顶点 ", ref flags, MeshOverlayFlags.Vertices, vert);
            DrawOverlayToggle(" 法线 ", ref flags, MeshOverlayFlags.Normals, norm);
            DrawOverlayToggle(" 切线 ", ref flags, MeshOverlayFlags.Tangents, tan);
            DrawOverlayToggle(" AABB ", ref flags, MeshOverlayFlags.Bounds, bnd);
            EditorGUILayout.EndHorizontal();

            if (flags != manager.OverlayFlags)
                manager.SetOverlayFlags(flags);

            // GPU 加速开关 + 网格信息
            EditorGUILayout.Space(2);
            EditorGUI.BeginChangeCheck();
            bool gpuToggle = EditorGUILayout.Toggle("GPU 加速（推荐高面数）", manager.UseGPURendering);
            if (EditorGUI.EndChangeCheck())
            {
                manager.UseGPURendering = gpuToggle;
                EditorApplication.delayCall += () => SceneView.RepaintAll();
            }

            // 显示选中物体的网格面数/顶点数
            DrawMeshStats();
            EditorGUILayout.Space(2);

            // 线框设置
            if (wire)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                manager.WireframeColor = EditorGUILayout.ColorField("线框颜色", manager.WireframeColor);
                if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += () => SceneView.RepaintAll();
                EditorGUI.indentLevel--;
            }

            // 顶点设置
            if (vert)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                manager.VertexColor = EditorGUILayout.ColorField("顶点颜色", manager.VertexColor);
                manager.VertexSize = EditorGUILayout.Slider("顶点大小", manager.VertexSize, 0.005f, 0.3f);
                manager.VertexScaleIndependent = EditorGUILayout.Toggle("大小不受缩放影响", manager.VertexScaleIndependent);
                if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += () => SceneView.RepaintAll();
                EditorGUI.indentLevel--;
            }

            // 法线设置
            if (norm)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                manager.NormalColor = EditorGUILayout.ColorField("法线颜色", manager.NormalColor);
                manager.NormalLength = EditorGUILayout.Slider("法线长度", manager.NormalLength, 0.01f, 0.5f);
                manager.NormalWidth = EditorGUILayout.Slider("法线粗细", manager.NormalWidth, 1f, 8f);
                if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += () => SceneView.RepaintAll();
                EditorGUI.indentLevel--;
            }

            // 切线设置
            if (tan)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                manager.TangentColor = EditorGUILayout.ColorField("切线颜色", manager.TangentColor);
                manager.TangentLength = EditorGUILayout.Slider("切线长度", manager.TangentLength, 0.01f, 0.5f);
                manager.TangentWidth = EditorGUILayout.Slider("切线粗细", manager.TangentWidth, 1f, 8f);
                if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += () => SceneView.RepaintAll();
                EditorGUI.indentLevel--;
            }

            // 包围盒设置
            if (bnd)
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                manager.BoundsColor = EditorGUILayout.ColorField("AABB 颜色", manager.BoundsColor);
                manager.BoundsWidth = EditorGUILayout.Slider("AABB 线宽", manager.BoundsWidth, 1f, 8f);
                if (EditorGUI.EndChangeCheck()) EditorApplication.delayCall += () => SceneView.RepaintAll();
                EditorGUI.indentLevel--;
            }
        }

        private void DrawMeshStats()
        {
            var selected = Selection.activeTransform;
            if (selected == null) return;

            Mesh mesh = null;
            var mr = selected.GetComponentInChildren<MeshRenderer>();
            if (mr != null)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            else
            {
                var smr = selected.GetComponentInChildren<SkinnedMeshRenderer>();
                if (smr != null) mesh = smr.sharedMesh;
            }

            if (mesh == null) return;

            int verts = mesh.vertexCount;
            // [M3-1 fix] mesh.GetIndexCount 不分配内存（mesh.triangles 会分配 int[]）
            int tris = 0;
            for (int si = 0; si < mesh.subMeshCount; si++)
                tris += (int)(mesh.GetIndexCount(si) / 3);
            string label = $"顶点: {verts:N0}  三角面: {tris:N0}";

            // 高面数警告
            if (verts > 50000)
            {
                var prevColor = GUI.color;
                GUI.color = new Color(1f, 0.7f, 0.3f);
                EditorGUILayout.LabelField(label + "  ⚠ 高面数", EditorStyles.miniLabel);
                GUI.color = prevColor;
            }
            else
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            }
        }

        private static void DrawOverlayToggle(string label, ref MeshOverlayFlags flags, MeshOverlayFlags flag, bool isOn)
        {
            var prevBg = GUI.backgroundColor;
            if (isOn) GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();
            if (GUILayout.Toggle(isOn, label, EditorStyles.miniButton))
                flags |= flag;
            else
                flags &= ~flag;
            GUI.backgroundColor = prevBg;
        }

    }
}
