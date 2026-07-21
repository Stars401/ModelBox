using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 调试模式选择 UI 组件。双列网格布局，分类标题 + 2 列模式按钮。
    /// </summary>
    public class DebugModeSelector
    {
        private static readonly (DebugViewMode mode, string label, string tooltip, string category)[] ModeEntries =
        {
            (DebugViewMode.None,          "Off",              "恢复正常渲染",                  "Off"),
            (DebugViewMode.WorldPosition, "World Position",   "世界空间坐标 → 彩色网格",       "Geometry"),
            (DebugViewMode.LocalPosition, "Local Position",   "模型空间坐标 → 彩色网格",       "Geometry"),
            (DebugViewMode.WorldNormal,   "World Normal",     "世界法线方向 → RGB 颜色",       "Geometry"),
            (DebugViewMode.LocalNormal,   "Local Normal",     "模型法线方向 → RGB 颜色",       "Geometry"),
            (DebugViewMode.UV0,           "UV0",              "第一套 UV 坐标 → RG 颜色",      "Geometry"),
            (DebugViewMode.UV1,           "UV1",              "第二套 UV 坐标 → RG 颜色",      "Geometry"),
            (DebugViewMode.VertexColor,   "Vertex Color",     "顶点颜色 → RGBA",               "Geometry"),
            (DebugViewMode.Depth,         "Depth",            "线性深度 → 灰度（需要深度纹理）","Geometry"),
            (DebugViewMode.Wireframe,     "Wireframe",        "线框叠加（选中物体 + 暗色底）",   "Geometry"),

            // 诊断模式
            (DebugViewMode.DiagScreenUV,  "Diag: Screen UV",  "屏幕 UV → RG（检查屏幕坐标）",  "Diagnostics"),
            (DebugViewMode.DiagRawDepth,  "Diag: Raw Depth",  "原始深度纹理 → 彩色（检查深度纹理绑定）","Diagnostics"),
            (DebugViewMode.DiagObjectDepth,"Diag: Obj Depth", "物体深度 → 灰度（不依赖深度纹理）","Diagnostics"),
            (DebugViewMode.DiagPureColor, "Diag: Pure Color", "纯品红色（确认 Shader 是否在执行）","Diagnostics"),

            // 导数诊断模式
            (DebugViewMode.MipmapLevel,   "Mipmap Level",     "纹理密度：UV 导数 → Mip 等级估算（检查纹理分辨率分布）","Diagnostics"),
            (DebugViewMode.GeoDensity,    "Geo Density",      "几何密度：世界坐标导数 → 每像素三角形密度（检查过度细分）","Diagnostics"),
            (DebugViewMode.SkyExposure,   "Sky Exposure",     "天光曝光：AO + 法线朝向 → 环境光接收量（快速 AO 检查）","Diagnostics"),
            (DebugViewMode.RayMarch,     "Ray March",        "射线步进：从相机沿视线步进与深度缓冲求交，热力图可视化","Diagnostics"),

            // 高级模式
            (DebugViewMode.OpaqueTexture, "Opaque Texture",   "采样 _CameraOpaqueTexture 可视化","Advanced"),
            (DebugViewMode.Overdraw,      "Overdraw",         "Overdraw 热力图（双重渲染：计数 + 热力图 blit）","Advanced"),
            (DebugViewMode.ScreenNormal,  "Screen Normal",    "屏幕空间法线（从深度缓冲重建，不受顶点动画影响）","Advanced"),
            (DebugViewMode.ShadowMap,     "Shadow Map",       "阴影贴图可视化（级联着色 / 阴影衰减）",          "Advanced"),
            (DebugViewMode.TransparencyLayers,"Transparency",   "透明物体层数热力图（Alpha Blend 层叠计数）",     "Advanced"),

            // PBR 诊断模式
            (DebugViewMode.FlatNormal,    "Flat Normal",      "平面法线（ddx/ddy 交叉积，检查平滑组/硬边）",   "PBR"),
            (DebugViewMode.NdotL,         "NdotL",            "Lambert 漫反射：dot(N, LightDir)，检查法线与光照方向","PBR"),
            (DebugViewMode.NdotV,         "NdotV",            "视角对齐：dot(N, ViewDir)，边缘亮/正面暗",       "PBR"),
            (DebugViewMode.Fresnel,       "Fresnel",          "Schlick 菲涅尔近似：pow(1-NdotV, 5)，边缘高光",  "PBR"),
            (DebugViewMode.ObjectID,      "Object ID",        "每物体唯一颜色，快速识别选中物体",                "PBR"),

            // 光照 & 材质分离模式
            (DebugViewMode.Tangent,           "Tangent",         "切线方向 → RGB（检查 UV X 轴方向 / MikkTSpace）","Lighting"),
            (DebugViewMode.Bitangent,         "Bitangent",       "副切线方向 → RGB（检查 UV Y 轴方向 / 切线手性）","Lighting"),
            (DebugViewMode.DiffuseColor,      "Diffuse Color",   "纯漫反射颜色（无高光，仅底色 + 柔和光照）",      "Lighting"),
            (DebugViewMode.SpecularHighlight, "Specular",        "仅镜面高光（检查法线精度、粗糙度、高光贴图）",   "Lighting"),
            (DebugViewMode.LightingOnly,      "Lighting Only",   "仅光照无纹理（检查光照模型、阴影、环境光）",      "Lighting"),
            (DebugViewMode.Roughness,         "Roughness",       "粗糙度影响可视化（Scale 控制参考粗糙度）",        "Lighting"),
            (DebugViewMode.Metallic,          "Metallic",        "金属度影响可视化（Scale 控制参考金属度）",         "Lighting"),
        };

        private static readonly (string id, string title, string desc, Color bg)[] _categories = new[]
        {
            ("Off", "关闭", "恢复正常渲染", ModelBoxStyles.CategoryOffBgColor),
            ("Geometry", "几何数据", "坐标、法线、UV、深度、顶点色", ModelBoxStyles.CategoryGeometryBgColor),
            ("Diagnostics", "诊断模式", "排查管线和纹理绑定问题", ModelBoxStyles.CategoryDiagBgColor),
            ("Advanced", "高级模式", "Opaque Texture、Overdraw、阴影、屏幕法线", ModelBoxStyles.CategoryAdvancedBgColor),
            ("PBR", "PBR 诊断", "NdotL、NdotV、Fresnel、平面法线、物体 ID", ModelBoxStyles.CategoryPbrBgColor),
            ("Lighting", "光照 & 材质", "光照分离、切线、粗糙度、金属度可视化", ModelBoxStyles.CategoryLightingBgColor),
        };

        // [perf P3] 缓存 GUIContent，避免每帧分配 ~35 个对象
        private static GUIContent[] _cachedContents;

        private static GUIContent GetOrCreateContent(int index, string label, string tooltip)
        {
            if (_cachedContents == null)
            {
                _cachedContents = new GUIContent[ModeEntries.Length];
                for (int i = 0; i < ModeEntries.Length; i++)
                    _cachedContents[i] = new GUIContent(ModeEntries[i].label, ModeEntries[i].tooltip);
            }
            return _cachedContents[index];
        }

        // 按分类分组的索引缓存（避免每帧重新分组）
        private static List<int>[] _categoryBuckets;

        private static List<int>[] GetCategoryBuckets()
        {
            if (_categoryBuckets != null) return _categoryBuckets;

            var catIds = new List<string>();
            for (int c = 0; c < _categories.Length; c++)
                catIds.Add(_categories[c].id);

            _categoryBuckets = new List<int>[_categories.Length];
            for (int c = 0; c < _categories.Length; c++)
                _categoryBuckets[c] = new List<int>();

            for (int i = 0; i < ModeEntries.Length; i++)
            {
                int catIdx = catIds.IndexOf(ModeEntries[i].category);
                if (catIdx >= 0) _categoryBuckets[catIdx].Add(i);
            }
            return _categoryBuckets;
        }

        /// <summary>
        /// 绘制双列网格模式选择器。返回用户选中的模式。
        /// </summary>
        public DebugViewMode Draw(DebugViewMode currentMode)
        {
            DebugViewMode newMode = currentMode;
            bool narrowWindow = EditorGUIUtility.currentViewWidth < 280f;
            int columns = narrowWindow ? 1 : 2;

            var buckets = GetCategoryBuckets();

            // 收集激活模式的描述信息（延迟绘制到分类末尾）
            string activeTooltip = null;

            for (int c = 0; c < _categories.Length; c++)
            {
                var cat = _categories[c];
                var entries = buckets[c];
                if (entries.Count == 0) continue;

                // 分类标题栏（24px 高，分类背景色）
                var catHeaderRect = EditorGUILayout.GetControlRect(false, 24);
                EditorGUI.DrawRect(catHeaderRect, cat.bg);
                GUI.Label(new Rect(catHeaderRect.x + 6, catHeaderRect.y + 2, catHeaderRect.width - 12, catHeaderRect.height),
                    new GUIContent(cat.title, cat.desc), ModelBoxStyles.CategoryHeaderStyle);

                // 分类内容区域（双列网格）
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                int col = 0;

                for (int e = 0; e < entries.Count; e++)
                {
                    int entryIndex = entries[e];
                    var entry = ModeEntries[entryIndex];
                    bool isActive = (currentMode == entry.mode);

                    if (col == 0)
                    {
                        EditorGUILayout.BeginHorizontal();
                    }

                    // 按钮颜色
                    var oldColor = GUI.backgroundColor;
                    if (isActive) GUI.backgroundColor = ModelBoxStyles.GetActiveButtonColor();

                    var content = GetOrCreateContent(entryIndex, entry.label, entry.tooltip);
                    bool clicked = GUILayout.Button(content, ModelBoxStyles.ModeButtonStyle, GUILayout.ExpandWidth(true));
                    if (clicked) newMode = entry.mode;

                    GUI.backgroundColor = oldColor;

                    // 收集激活模式的 tooltip
                    if (isActive && !string.IsNullOrEmpty(entry.tooltip))
                        activeTooltip = entry.tooltip;

                    col++;
                    if (col >= columns || e == entries.Count - 1)
                    {
                        // [fix] 奇数行最后一个按钮自动扩展至全宽（ExpandWidth(true) 已设置）
                        EditorGUILayout.EndHorizontal();
                        col = 0;
                    }
                }

                // 激活模式描述（在分类末尾绘制，不打断网格）
                if (!string.IsNullOrEmpty(activeTooltip))
                {
                    var descRect = EditorGUILayout.GetControlRect(false, 18);
                    EditorGUI.DrawRect(descRect, ModelBoxStyles.ActiveModeDescBgColor);
                    var prevCC = GUI.contentColor;
                    GUI.contentColor = EditorGUIUtility.isProSkin
                        ? new Color(0.75f, 0.85f, 0.75f)
                        : new Color(0.2f, 0.3f, 0.2f);
                    GUI.Label(new Rect(descRect.x + 12, descRect.y, descRect.width - 12, descRect.height),
                        activeTooltip, EditorStyles.wordWrappedMiniLabel);
                    GUI.contentColor = prevCC;
                    activeTooltip = null; // 重置，仅在激活模式所在分类显示
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            return newMode;
        }
    }
}
