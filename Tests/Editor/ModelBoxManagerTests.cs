using NUnit.Framework;
using UnityEngine;

namespace ModelBox.Tests
{
    [TestFixture]
    public class ModelBoxManagerTests
    {
        [Test]
        public void Instance_IsNotNull()
        {
            Assert.IsNotNull(ModelBoxManager.Instance);
        }

        [Test]
        public void SetDebugMode_FiresOnModeChangedEvent()
        {
            var manager = ModelBoxManager.Instance;
            DebugViewMode receivedMode = DebugViewMode.None;
            bool eventFired = false;

            System.Action<DebugViewMode> handler = (mode) =>
            {
                receivedMode = mode;
                eventFired = true;
            };

            manager.OnModeChanged += handler;
            manager.SetDebugMode(DebugViewMode.WorldNormal);

            Assert.IsTrue(eventFired, "OnModeChanged event should fire.");
            Assert.AreEqual(DebugViewMode.WorldNormal, receivedMode);

            manager.OnModeChanged -= handler;
            manager.SetDebugMode(DebugViewMode.None);
        }

        [Test]
        public void SetDebugMode_None_DisablesRendering()
        {
            var manager = ModelBoxManager.Instance;
            manager.SetDebugMode(DebugViewMode.None);

            Assert.AreEqual(DebugViewMode.None, manager.CurrentMode);
            Assert.IsFalse(manager.IsEnabled);
        }

        [Test]
        public void SetParameters_FiresOnParametersChangedEvent()
        {
            var manager = ModelBoxManager.Instance;
            ModelBoxParameters receivedParams = default;
            bool eventFired = false;

            System.Action<ModelBoxParameters> handler = (p) =>
            {
                receivedParams = p;
                eventFired = true;
            };

            manager.OnParametersChanged += handler;

            var testParams = ModelBoxParameters.Default;
            testParams.Scale = 2.5f;
            manager.SetParameters(testParams);

            Assert.IsTrue(eventFired, "OnParametersChanged event should fire.");
            Assert.AreEqual(2.5f, receivedParams.Scale, 0.001f);

            manager.OnParametersChanged -= handler;
        }

        [Test]
        public void GetModeCategory_ReturnsCorrectCategory()
        {
            var manager = ModelBoxManager.Instance;

            Assert.AreEqual(DebugViewCategory.None, manager.GetModeCategory(DebugViewMode.None));
            Assert.AreEqual(DebugViewCategory.Geometry, manager.GetModeCategory(DebugViewMode.WorldPosition));
            Assert.AreEqual(DebugViewCategory.Geometry, manager.GetModeCategory(DebugViewMode.LocalNormal));
            Assert.AreEqual(DebugViewCategory.Geometry, manager.GetModeCategory(DebugViewMode.UV0));
            Assert.AreEqual(DebugViewCategory.Geometry, manager.GetModeCategory(DebugViewMode.VertexColor));
            Assert.AreEqual(DebugViewCategory.Geometry, manager.GetModeCategory(DebugViewMode.Depth));
        }

        [Test]
        public void ToggleEnabled_PreservesPreviousMode()
        {
            var manager = ModelBoxManager.Instance;

            // 设置一个模式
            manager.SetDebugMode(DebugViewMode.WorldNormal);
            Assert.IsTrue(manager.IsEnabled);
            Assert.AreEqual(DebugViewMode.WorldNormal, manager.CurrentMode);

            // Toggle 关闭
            manager.ToggleEnabled();
            Assert.IsFalse(manager.IsEnabled);
            Assert.AreEqual(DebugViewMode.None, manager.CurrentMode);

            // Toggle 开启 → 应恢复之前的模式
            manager.ToggleEnabled();
            Assert.IsTrue(manager.IsEnabled);
            Assert.AreEqual(DebugViewMode.WorldNormal, manager.CurrentMode);

            // 清理
            manager.SetDebugMode(DebugViewMode.None);
        }
    }
}
