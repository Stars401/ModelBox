using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace ModelBox.Tests
{
    [TestFixture]
    public class ShaderInfoDataTests
    {
        [Test]
        public void Analyze_NullShader_ReturnsNull()
        {
            var result = ShaderInfoData.Analyze(null, null);
            Assert.IsNull(result);
        }

        [Test]
        public void Analyze_NullMaterial_ReturnsNull()
        {
            var shader = Shader.Find("Hidden/ModelBox/Geometry");
            if (shader == null) return; // skip if shader not found in test env
            var result = ShaderInfoData.Analyze(shader, null);
            Assert.IsNull(result);
        }

        [Test]
        public void Analyze_ValidShader_ReturnsData()
        {
            var shader = Shader.Find("Hidden/ModelBox/Geometry");
            if (shader == null) return; // skip if shader not found

            var mat = new Material(shader);
            var data = ShaderInfoData.Analyze(shader, mat);

            Assert.IsNotNull(data);
            Assert.AreEqual(shader, data.SourceShader);
            Assert.GreaterOrEqual(data.Properties.Count, 1);

            Object.DestroyImmediate(mat);
        }

        [Test]
        public void ApplyKeywordChange_TogglesState()
        {
            var shader = Shader.Find("Hidden/ModelBox/Geometry");
            if (shader == null) return;

            var mat = new Material(shader);
            var data = ShaderInfoData.Analyze(shader, mat);

            if (data.Keywords.Count > 0)
            {
                var keyword = data.Keywords[0];
                bool initialState = keyword.IsEnabled;
                data.ApplyKeywordChange(mat, keyword.Name, !initialState);

                // Verify internal state updated
                var updated = data.Keywords[0];
                Assert.AreEqual(!initialState, updated.IsEnabled);
            }

            Object.DestroyImmediate(mat);
        }

        [Test]
        public void Analyze_PropertiesHaveValidTypes()
        {
            var shader = Shader.Find("Hidden/ModelBox/Geometry");
            if (shader == null) return;

            var mat = new Material(shader);
            var data = ShaderInfoData.Analyze(shader, mat);

            foreach (var prop in data.Properties)
            {
                Assert.IsFalse(string.IsNullOrEmpty(prop.Name), "Property name should not be empty");
                Assert.IsTrue(System.Enum.IsDefined(typeof(ShaderPropertyType), prop.Type),
                    $"Property {prop.Name} has invalid type {prop.Type}");
            }

            Object.DestroyImmediate(mat);
        }
    }
}
