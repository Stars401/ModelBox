using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 参数调节滑条 UI 组件。
    /// 根据当前调试模式动态显示相关参数和中文说明。
    /// </summary>
    public class DebugParameterControls
    {
        // [perf P5] 缓存字符串数组
        private static readonly string[] _colorMapOptions = { "原始 (Raw)", "灰度 (Gray)", "热力图 (HeatMap)", "彩虹 (Rainbow)" };
        public ModelBoxParameters Draw(ModelBoxParameters current, DebugViewMode currentMode)
        {
            EditorGUI.BeginChangeCheck();

            float scale = current.Scale;
            float offset = current.Offset;
            float gamma = current.Gamma;
            float depthRange = current.DepthRange;
            float checkerGridSize = current.CheckerGridSize;
            int colorMapMode = current.ColorMapMode;
            float overdrawMaxCount = current.OverdrawMaxCount;
            float maxSteps = current.MaxSteps;
            float stepSize = current.StepSize;

            // 根据模式显示相关参数
            var category = ModelBoxManager.Instance?.GetModeCategory(currentMode) ?? DebugViewCategory.None;

            if (category == DebugViewCategory.Geometry || category == DebugViewCategory.ScreenSpace)
            {
                switch (currentMode)
                {
                    case DebugViewMode.WorldPosition:
                        EditorGUILayout.HelpBox("世界坐标 → RGB：原点=灰(0.5)，正方向=白，负方向=黑。Scale 控制可视范围（米）", MessageType.None);
                        scale = EditorGUILayout.Slider(
                            new GUIContent("可视范围（米）", "映射到完整颜色范围的坐标跨度"),
                            scale, 0.01f, 100f);
                        offset = EditorGUILayout.Slider(new GUIContent("颜色偏移"), offset, -1f, 1f);
                        gamma = EditorGUILayout.Slider(new GUIContent("Gamma 校正", "pow(color, gamma)：1.0=原始，<1变亮，>1变暗"), gamma, 0.1f, 5f);
                        break;

                    case DebugViewMode.LocalPosition:
                        EditorGUILayout.LabelField("模型坐标 → RGB：frac(pos × Scale) 周期性彩色网格",
                            EditorStyles.wordWrappedMiniLabel);
                        scale = EditorGUILayout.Slider(
                            new GUIContent("网格密度", "值越大网格越密"),
                            scale, 0.01f, 10f);
                        offset = EditorGUILayout.Slider(new GUIContent("颜色偏移"), offset, -1f, 1f);
                        gamma = EditorGUILayout.Slider(new GUIContent("Gamma 校正", "pow(color, gamma)：1.0=原始，<1变亮，>1变暗"), gamma, 0.1f, 5f);
                        break;

                    case DebugViewMode.WorldNormal:
                    case DebugViewMode.LocalNormal:
                        EditorGUILayout.LabelField("法线 → RGB：normalize(dir) × 0.5 + 0.5",
                            EditorStyles.wordWrappedMiniLabel);
                        break;

                    case DebugViewMode.UV0:
                    case DebugViewMode.UV1:
                        EditorGUILayout.HelpBox("UV → RG：直接显示 UV 坐标值（红=X，绿=Y）。\n棋盘格密度仅在选区调试的 UV 棋盘格模式中生效。", MessageType.None);
                        offset = EditorGUILayout.Slider(new GUIContent("颜色偏移"), offset, -1f, 1f);
                        gamma = EditorGUILayout.Slider(new GUIContent("Gamma 校正", "pow(color, gamma)：1.0=原始，<1变亮，>1变暗"), gamma, 0.1f, 5f);
                        break;

                    case DebugViewMode.VertexColor:
                        EditorGUILayout.LabelField("顶点颜色 → RGBA：直接显示顶点颜色",
                            EditorStyles.wordWrappedMiniLabel);
                        break;

                    case DebugViewMode.Depth:
                        // 深度纹理诊断
                        var depthTex = Shader.GetGlobalTexture("_CameraDepthTexture");
                        if (depthTex == null)
                        {
                            EditorGUILayout.HelpBox("⚠ _CameraDepthTexture 不可用！\n" +
                                "请在 URP Asset 中确认：Rendering > Depth Texture = On\n" +
                                "或在设置页面点击「自动修复」。", MessageType.Error);
                        }
                        EditorGUILayout.LabelField("深度 → 灰度：LinearEyeDepth(depth) / Range\n" +
                            "提示：场景较小时降低「深度范围」值以看到明暗变化。", EditorStyles.wordWrappedMiniLabel);
                        depthRange = EditorGUILayout.Slider(
                            new GUIContent("深度范围（米）", "灰度映射的最大深度距离，值越小对比度越高"),
                            depthRange, 0.1f, 1000f);
                        gamma = EditorGUILayout.Slider(new GUIContent("Gamma 校正", "pow(color, gamma)：1.0=原始，<1变亮，>1变暗"), gamma, 0.1f, 5f);
                        break;

                    case DebugViewMode.Wireframe:
                        EditorGUILayout.LabelField("线框叠加（选中物体）", EditorStyles.wordWrappedMiniLabel);
                        break;

                    case DebugViewMode.OpaqueTexture:
                        EditorGUILayout.LabelField("Opaque Texture → 直接显示不透明物体颜色缓冲",
                            EditorStyles.wordWrappedMiniLabel);
                        break;

                    case DebugViewMode.Overdraw:
                        EditorGUILayout.HelpBox("Overdraw 热力图：统计每个像素被不透明物体绘制的次数。\n" +
                            "黑=1x（最优），蓝=2x，绿=4x，黄=6x，红=8x+（需优化）。", MessageType.None);
                        overdrawMaxCount = EditorGUILayout.Slider(
                            new GUIContent("最大 Overdraw", "热力图映射的最大 overdraw 次数"),
                            overdrawMaxCount, 1f, 32f);
                        break;

                    case DebugViewMode.ScreenNormal:
                        EditorGUILayout.LabelField("屏幕空间法线 → RGB：从深度缓冲重建的世界法线，normalize(cross(ddy, ddx))",
                            EditorStyles.wordWrappedMiniLabel);
                        break;

                    case DebugViewMode.ShadowMap:
                        EditorGUILayout.HelpBox("阴影贴图可视化。从深度缓冲重建世界坐标，采样主光源阴影贴图。\n" +
                            "模式 0 = 阴影衰减（白=光照中，黑=阴影），模式 1 = 级联着色（不同颜色=不同级联）", MessageType.None);
                        break;

                    // ===== PBR 诊断模式 =====

                    case DebugViewMode.FlatNormal:
                        EditorGUILayout.HelpBox("平面法线：使用 ddx/ddy 交叉积计算面法线（不受顶点法线影响）。\n" +
                            "用于检查平滑组是否正确、硬边是否缺失。", MessageType.None);
                        break;

                    case DebugViewMode.NdotL:
                        EditorGUILayout.HelpBox("NdotL (Lambert)：法线与主光源方向的点积。\n" +
                            "白 = 正面受光，黑 = 背光/自阴影。用于检查法线朝向和光照模型。", MessageType.None);
                        break;

                    case DebugViewMode.NdotV:
                        EditorGUILayout.HelpBox("NdotV：法线与视角方向的点积。\n" +
                            "白 = 正面朝向相机，黑 = 掠射角/边缘。用于检查法线和菲涅尔效应。", MessageType.None);
                        break;

                    case DebugViewMode.Fresnel:
                        EditorGUILayout.HelpBox("Schlick 菲涅尔近似：pow(1 - NdotV, 5)。\n" +
                            "白 = 边缘高光强，黑 = 正面无高光。用于验证菲涅尔效果和法线精度。", MessageType.None);
                        break;

                    case DebugViewMode.ObjectID:
                        EditorGUILayout.HelpBox("物体 ID：每个物体分配唯一颜色（基于 Transform 矩阵哈希）。\n" +
                            "用于快速识别和区分场景中的不同物体。", MessageType.None);
                        break;

                    case DebugViewMode.TransparencyLayers:
                        EditorGUILayout.HelpBox("透明层数热力图：统计每个像素被透明物体绘制的次数。\n" +
                            "红 = 多层透明叠加（粒子系统/UI/Glass），绿 = 1 层，黑 = 无透明物体。\n" +
                            "透明物体的 Alpha Blend 和排序是移动端头号性能杀手。",
                            MessageType.None);
                        break;

                    // ===== 光照 & 材质分离模式 =====

                    case DebugViewMode.Tangent:
                        EditorGUILayout.HelpBox("切线方向 → RGB：normalize(tangentWS) × 0.5 + 0.5。\n" +
                            "检查 UV X 轴方向和 MikkTSpace 切线空间是否正确。", MessageType.None);
                        break;

                    case DebugViewMode.Bitangent:
                        EditorGUILayout.HelpBox("副切线方向 → RGB：cross(N, T) × tangent.w × 0.5 + 0.5。\n" +
                            "检查 UV Y 轴方向和切线空间手性（handedness）。", MessageType.None);
                        break;

                    case DebugViewMode.DiffuseColor:
                        EditorGUILayout.HelpBox("纯漫反射颜色：柔和 Lambert 光照，无高光。\n" +
                            "用于检查底色纹理分配和基本形状感知。", MessageType.None);
                        break;

                    case DebugViewMode.SpecularHighlight:
                        EditorGUILayout.HelpBox("仅镜面高光：Blinn-Phong（NdotH^64）。\n" +
                            "检查法线精度（高光是否平滑）、粗糙度分布、高光贴图是否正确。", MessageType.None);
                        break;

                    case DebugViewMode.LightingOnly:
                        EditorGUILayout.HelpBox("仅光照无纹理：漫反射 + 高光 + 环境光，中性灰 albedo。\n" +
                            "检查光照模型是否正确、阴影位置、环境光比例。", MessageType.None);
                        break;

                    case DebugViewMode.Roughness:
                        EditorGUILayout.HelpBox("粗糙度影响可视化：使用 Scale 参数控制参考粗糙度（0-1）。\n" +
                            "低粗糙度 = 紧凑高光（光滑），高粗糙度 = 扩散高光（粗糙）。", MessageType.None);
                        scale = EditorGUILayout.Slider(
                            new GUIContent("参考粗糙度", "模拟的粗糙度值（0=镜面，1=完全粗糙）"),
                            scale, 0f, 1f);
                        break;

                    case DebugViewMode.Metallic:
                        EditorGUILayout.HelpBox("金属度影响可视化：使用 Scale 参数控制参考金属度（0-1）。\n" +
                            "0 = 非金属（漫反射+弱高光），1 = 金属（无漫反射+强高光）。", MessageType.None);
                        scale = EditorGUILayout.Slider(
                            new GUIContent("参考金属度", "模拟的金属度值（0=非金属，1=金属）"),
                            scale, 0f, 1f);
                        break;

                    // ===== 导数诊断模式 =====

                    case DebugViewMode.MipmapLevel:
                        EditorGUILayout.HelpBox("纹理密度（Mipmap Level）：基于 UV 导数估算 Mip 等级。\n" +
                            "绿 = 高分辨率（mip 0-2），黄 = 中等（mip 3-5），红 = 低分辨率（mip 6+）。\n" +
                            "用于检查纹理分辨率分布是否合理、是否需要更高分辨率纹理。", MessageType.None);
                        scale = EditorGUILayout.Slider(
                            new GUIContent("参考纹理尺寸", "参考纹理分辨率倍数（×512 像素）"),
                            scale, 0.1f, 16f);
                        break;

                    case DebugViewMode.GeoDensity:
                        EditorGUILayout.HelpBox("几何密度：基于世界坐标导数估算每像素三角形密度。\n" +
                            "绿 = 低密度（正常），黄 = 中等密度，红 = 高密度（过度细分/过近）。\n" +
                            "用于检查模型面数是否合理、LOD 切换距离是否正确。", MessageType.None);
                        scale = EditorGUILayout.Slider(
                            new GUIContent("参考密度", "参考几何密度倍数（×0.1 米/像素）"),
                            scale, 0.01f, 10f);
                        break;

                    case DebugViewMode.SkyExposure:
                        EditorGUILayout.HelpBox("天光曝光（AO 近似）：法线朝向 × 遮蔽因子。\n" +
                            "白 = 充分曝光（朝上/受光），黑 = 严重遮蔽（朝下/背光）。\n" +
                            "快速检查场景 AO 和法线是否正确，无需烘焙。", MessageType.None);
                        break;

                    // ===== 屏幕空间射线步进 =====

                    case DebugViewMode.RayMarch:
                        EditorGUILayout.HelpBox("射线步进（Ray March）：从相机沿视线方向步进，与深度缓冲求交。\n" +
                            "热力图：绿 = 少量步进（近/简单），红 = 大量步进（远/复杂）。\n" +
                            "用途：体积渲染调试、SDF 可视化、场景复杂度分析。", MessageType.None);
                        stepSize = EditorGUILayout.Slider(
                            new GUIContent("步进长度", "每步步进距离（米），越小精度越高但步数越多"),
                            stepSize, 0.01f, 5f);
                        maxSteps = EditorGUILayout.Slider(
                            new GUIContent("最大步数", "射线最大步进次数，越大越远但越慢"),
                            maxSteps, 8f, 256f);
                        gamma = EditorGUILayout.Slider(
                            new GUIContent("Gamma 校正", "输出 Gamma 校正值，调整热力图对比度"),
                            gamma, 0.1f, 5f);
                        break;
                }
            }

            // 诊断模式也显示 gamma/offset（shader 中对所有模式生效）
            if ((currentMode >= DebugViewMode.DiagScreenUV && currentMode <= DebugViewMode.DiagPureColor)
                || currentMode == DebugViewMode.MipmapLevel
                || currentMode == DebugViewMode.GeoDensity
                || currentMode == DebugViewMode.SkyExposure)
            {
                EditorGUILayout.LabelField("诊断模式：观察颜色输出，截图反馈给开发者", EditorStyles.wordWrappedMiniLabel);
            }

            // 颜色映射模式（对标量/归一化模式有效）
            if (ShouldShowColorMapStatic(currentMode))
            {
                EditorGUILayout.Space(2);
                colorMapMode = EditorGUILayout.Popup("颜色映射", colorMapMode, _colorMapOptions);
            }

            EditorGUILayout.Space(4);

            if (GUILayout.Button("恢复默认参数", GUILayout.Height(22)))
            {
                EditorGUI.EndChangeCheck(); // [fix] 防止 GUI 状态栈泄漏
                return ModelBoxParameters.Default;
            }

            if (EditorGUI.EndChangeCheck())
            {
                return new ModelBoxParameters
                {
                    Scale = scale,
                    Offset = offset,
                    Gamma = gamma,
                    DepthRange = depthRange,
                    CheckerGridSize = checkerGridSize,
                    ColorMapMode = colorMapMode,
                    OverdrawMaxCount = overdrawMaxCount,
                    MaxSteps = maxSteps,
                    StepSize = stepSize,
                };
            }

            return current;
        }

        public static bool ShouldShowColorMapStatic(DebugViewMode mode)
        {
            // ColorMap 仅对标量/归一化数据模式有意义
            // 排除：法线(UV编码)、顶点颜色、诊断模式(自带颜色编码)、OpaqueTexture(原始场景颜色)
            switch (mode)
            {
                case DebugViewMode.WorldPosition:
                case DebugViewMode.LocalPosition:
                case DebugViewMode.Depth:
                case DebugViewMode.DiagScreenUV:
                case DebugViewMode.ScreenNormal:
                case DebugViewMode.ShadowMap:
                // PBR 诊断：NdotL/NdotV/Fresnel 输出标量灰度，ColorMap 可增强对比
                case DebugViewMode.NdotL:
                case DebugViewMode.NdotV:
                case DebugViewMode.Fresnel:
                // 光照分离：Specular/LightingOnly 的灰度输出也可用 ColorMap 增强
                case DebugViewMode.SpecularHighlight:
                case DebugViewMode.LightingOnly:
                case DebugViewMode.Roughness:
                case DebugViewMode.Metallic:
                // 导数诊断：Mipmap/GeoDensity/SkyExposure 输出标量灰度
                case DebugViewMode.MipmapLevel:
                case DebugViewMode.GeoDensity:
                case DebugViewMode.SkyExposure:
                // 屏幕空间：RayMarch 输出步进次数热力图
                case DebugViewMode.RayMarch:
                    return true;
                default:
                    return false;
            }
        }
    }
}
