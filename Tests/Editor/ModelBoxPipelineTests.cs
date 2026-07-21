using NUnit.Framework;

namespace ModelBox.Tests
{
    [TestFixture]
    public class PipelineDetectorTests
    {
        [Test]
        public void Detect_DoesNotReturnUnknown_WhenPipelineIsSet()
        {
            // 在有 URP 的项目中，应该返回 URP
            // 在没有 SRP 的项目中，应该返回 BuiltIn
            var result = PipelineDetector.Detect();
            Assert.AreNotEqual(RenderPipelineType.Unknown, result,
                "PipelineDetector.Detect() should not return Unknown in a valid Unity project.");
        }
    }
}
