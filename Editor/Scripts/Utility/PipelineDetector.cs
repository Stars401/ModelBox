using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    public enum RenderPipelineType
    {
        Unknown,
        BuiltIn,
        URP,
        HDRP,
    }

    /// <summary>
    /// 运行时管线检测。通过 GraphicsSettings.currentRenderPipeline 的类型名称判断。
    /// </summary>
    public static class PipelineDetector
    {
        public static RenderPipelineType Detect()
        {
            var currentPipeline = GraphicsSettings.currentRenderPipeline;
            if (currentPipeline == null)
                return RenderPipelineType.BuiltIn;

            var typeName = currentPipeline.GetType().FullName ?? "";
            if (typeName.Contains("Universal"))
                return RenderPipelineType.URP;
            if (typeName.Contains("HighDefinition"))
                return RenderPipelineType.HDRP;

            return RenderPipelineType.Unknown;
        }
    }
}
