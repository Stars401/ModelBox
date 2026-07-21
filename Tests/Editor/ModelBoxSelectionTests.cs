using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace ModelBox.Tests
{
    [TestFixture]
    public class SelectionManagerTests
    {
        [Test]
        public void Instance_IsNotNull()
        {
            Assert.IsNotNull(ModelBoxSelectionManager.Instance);
        }

        [Test]
        public void SetMode_None_ClearsState()
        {
            var manager = ModelBoxSelectionManager.Instance;
            manager.SetMode(SelectionDebugMode.None);
            Assert.AreEqual(SelectionDebugMode.None, manager.CurrentMode);
        }

        [Test]
        public void SetOverlayFlags_TogglesCorrectly()
        {
            var manager = ModelBoxSelectionManager.Instance;
            manager.SetOverlayFlags(MeshOverlayFlags.None);
            Assert.AreEqual(MeshOverlayFlags.None, manager.OverlayFlags);

            manager.SetOverlayFlags(MeshOverlayFlags.Wireframe);
            Assert.AreEqual(MeshOverlayFlags.Wireframe, manager.OverlayFlags);

            manager.SetOverlayFlags(MeshOverlayFlags.Wireframe | MeshOverlayFlags.Normals);
            Assert.IsTrue((manager.OverlayFlags & MeshOverlayFlags.Wireframe) != 0);
            Assert.IsTrue((manager.OverlayFlags & MeshOverlayFlags.Normals) != 0);

            // Cleanup
            manager.SetOverlayFlags(MeshOverlayFlags.None);
        }

        [Test]
        public void TryGetPropertyColor_NoSelection_ReturnsInfo()
        {
            var manager = ModelBoxSelectionManager.Instance;
            Selection.activeTransform = null;
            string result = manager.TryGetPropertyColor("_Color");
            Assert.IsTrue(result.Contains("未选中"));
        }

        [Test]
        public void RestoreOriginalMaterials_DoesNotThrow()
        {
            var manager = ModelBoxSelectionManager.Instance;
            Assert.DoesNotThrow(() => manager.RestoreOriginalMaterials());
        }
    }
}
