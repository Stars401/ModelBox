namespace ModelBox
{
    /// <summary>
    /// 调试可视化模式。
    /// 0-8: 主要调试模式
    /// 9-12: 诊断模式（用于排查问题）
    /// 13: 高级模式（线框叠加）
    /// 14-18: 高级模式（纹理/Overdraw/法线/阴影）
    /// 16, 19-22: PBR 诊断模式
    /// </summary>
    public enum DebugViewMode
    {
        None = 0,
        WorldPosition = 1,
        LocalPosition = 2,
        WorldNormal = 3,
        LocalNormal = 4,
        UV0 = 5,
        UV1 = 6,
        Depth = 7,
        VertexColor = 8,

        // 诊断模式
        DiagScreenUV = 9,       // 屏幕 UV 可视化（检查 ComputeScreenPos）
        DiagRawDepth = 10,      // 原始深度值（检查 _CameraDepthTexture 是否有数据）
        DiagObjectDepth = 11,   // 物体自身深度（不依赖深度纹理）
        DiagPureColor = 12,     // 纯品红色（确认 Shader 是否在执行）

        // 高级模式
        Wireframe = 13,         // 线框叠加（Houdini 风格，选中物体）
        OpaqueTexture = 14,     // Opaque Texture 可视化（采样 _CameraOpaqueTexture）
        Overdraw = 15,          // Overdraw 热力图（双重渲染：计数 shader + 热力图 blit）

        // PBR 诊断模式
        FlatNormal = 16,        // 平面法线（ddx/ddy 交叉积，检查平滑组/硬边）

        ScreenNormal = 17,      // 屏幕空间法线（从深度缓冲重建，不受顶点动画影响）
        ShadowMap = 18,         // 阴影贴图可视化（级联着色 / 阴影衰减）

        // PBR 诊断模式（续）
        NdotL = 19,             // Lambert 漫反射：dot(N, LightDir)，检查法线与光照方向
        NdotV = 20,             // 视角对齐：dot(N, ViewDir)，边缘=亮，正面=暗
        Fresnel = 21,           // Schlick 菲涅尔近似：pow(1-NdotV, 5)，边缘高光
        ObjectID = 22,          // 物体 ID 着色（每物体唯一颜色，快速识别）

        // 性能诊断模式
        TransparencyLayers = 23, // 透明物体层数热力图（仅透明队列，统计 Alpha Blend 层叠）

        // 光照 & 材质分离模式
        Tangent = 24,           // 切线方向 → RGB（检查 UV X 轴方向 / MikkTSpace）
        Bitangent = 25,         // 副切线方向 → RGB（检查 UV Y 轴方向 / 切线手性）
        DiffuseColor = 26,      // 纯漫反射颜色（无高光，仅底色+柔和光照）
        SpecularHighlight = 27, // 仅镜面高光（检查高光贴图、法线精度、粗糙度）
        LightingOnly = 28,      // 仅光照无纹理（检查光照模型、阴影、环境光平衡）
        Roughness = 29,         // 粗糙度影响可视化（Scale 参数控制参考粗糙度）
        Metallic = 30,          // 金属度影响可视化（Scale 参数控制参考金属度）

        // 导数诊断模式（行业高端工具对标）
        MipmapLevel = 31,       // 纹理密度（UV 导数 → Mip 等级估算，检查纹理分辨率分布）
        GeoDensity = 32,        // 几何密度热力图（世界坐标导数 → 每像素三角形密度）
        SkyExposure = 33,       // 天光曝光近似（AO + 法线朝向 → 环境光接收量）

        // 屏幕空间射线步进
        RayMarch = 34,          // 深度缓冲射线步进（步进次数热力图，用于体积渲染/SDF 调试）
    }

    /// <summary>
    /// 调试模式分类。
    /// Geometry: overrideMaterial 路径（逐顶点/逐物体模式）
    /// ScreenSpace: 全屏 Blit 路径（从深度缓冲重建，不受顶点动画影响）
    /// </summary>
    public enum DebugViewCategory
    {
        None,
        Geometry,     // 逐物体模式：overrideMaterial DrawRenderers
        ScreenSpace,  // 屏幕空间模式：从深度缓冲重建的全屏 Blit
    }
}
