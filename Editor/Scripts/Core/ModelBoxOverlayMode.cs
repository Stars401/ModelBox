namespace ModelBox
{
    /// <summary>
    /// 选区调试模式（针对选中物体的叠加调试）。
    /// </summary>
    public enum SelectionDebugMode
    {
        None = 0,
        TextureChannel = 1,  // 贴图通道隔离
        Checkerboard = 2,    // UV 棋盘格
        ShaderProperty = 3,  // Shader 属性颜色回读
    }

    /// <summary>
    /// 网格叠加显示标志（Houdini 风格）。
    /// </summary>
    [System.Flags]
    public enum MeshOverlayFlags
    {
        None = 0,
        Wireframe = 1 << 0,
        Vertices = 1 << 1,
        Normals = 1 << 2,
        Tangents = 1 << 3,
        Bounds = 1 << 4,
    }

    /// <summary>
    /// 骨骼权重可视化显示模式。
    /// </summary>
    public enum BoneWeightDisplayMode
    {
        Off = 0,       // 关闭
        ColorMap = 1,  // 顶点颜色模式：所有顶点按权重热力图着色（蓝=0 → 红=1）
        Threshold = 2, // 顶点过滤模式：仅显示权重 > threshold 的顶点
    }
}
