using NUnit.Framework;
using UnityEngine;

namespace ModelBox.Tests
{
    [TestFixture]
    public class DebugViewModeTests
    {
        [Test]
        public void ParameterDefaults_AreInValidRanges()
        {
            var defaults = ModelBoxParameters.Default;

            Assert.GreaterOrEqual(defaults.Scale, 0.001f);
            Assert.LessOrEqual(defaults.Scale, 10f);
            Assert.GreaterOrEqual(defaults.Offset, -5f);
            Assert.LessOrEqual(defaults.Offset, 5f);
            Assert.GreaterOrEqual(defaults.Gamma, 0.1f);
            Assert.LessOrEqual(defaults.Gamma, 5f);
            Assert.GreaterOrEqual(defaults.DepthRange, 0.1f);
            Assert.LessOrEqual(defaults.DepthRange, 1000f);
        }

        [Test]
        public void Parameters_Equals_WorksCorrectly()
        {
            var a = ModelBoxParameters.Default;
            var b = ModelBoxParameters.Default;
            Assert.IsTrue(a.Equals(b));

            b.Scale = 5f;
            Assert.IsFalse(a.Equals(b));
        }

        [Test]
        public void ApplyToMaterial_DoesNotThrow_OnNull()
        {
            var p = ModelBoxParameters.Default;
            Assert.DoesNotThrow(() => p.ApplyToMaterial(null));
        }

        [Test]
        public void DebugViewMode_Values_MatchShortcutKeys()
        {
            // 枚举值必须与快捷键数字对应
            Assert.AreEqual(0, (int)DebugViewMode.None);
            Assert.AreEqual(1, (int)DebugViewMode.WorldPosition);
            Assert.AreEqual(2, (int)DebugViewMode.LocalPosition);
            Assert.AreEqual(3, (int)DebugViewMode.WorldNormal);
            Assert.AreEqual(4, (int)DebugViewMode.LocalNormal);
            Assert.AreEqual(5, (int)DebugViewMode.UV0);
            Assert.AreEqual(6, (int)DebugViewMode.UV1);
            Assert.AreEqual(7, (int)DebugViewMode.Depth);
            Assert.AreEqual(8, (int)DebugViewMode.VertexColor);
        }

        [Test]
        public void DebugViewMode_DiagnosticValues_MatchExpected()
        {
            Assert.AreEqual(9, (int)DebugViewMode.DiagScreenUV);
            Assert.AreEqual(10, (int)DebugViewMode.DiagRawDepth);
            Assert.AreEqual(11, (int)DebugViewMode.DiagObjectDepth);
            Assert.AreEqual(12, (int)DebugViewMode.DiagPureColor);
            Assert.AreEqual(13, (int)DebugViewMode.Wireframe);
        }

        [Test]
        public void ModelBoxSettings_CanBeCreated()
        {
            var settings = ModelBoxSettings.GetOrCreate();
            Assert.IsNotNull(settings);
            Assert.AreEqual(DebugViewMode.None, settings.LastMode);
        }
    }
}
