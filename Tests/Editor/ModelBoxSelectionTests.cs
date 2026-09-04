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

        // [feat] 顶点颜色模式契约：模式切换 + 通道参数属性
        [Test]
        public void SetMode_VertexColor_SwitchesCorrectly()
        {
            var manager = ModelBoxSelectionManager.Instance;
            manager.SetMode(SelectionDebugMode.VertexColor);
            Assert.AreEqual(SelectionDebugMode.VertexColor, manager.CurrentMode);

            // 通道参数可设置且不抛异常
            manager.VertexColorChannel = 4;
            Assert.DoesNotThrow(() => manager.UpdateVertexColorProperties());
            Assert.AreEqual(4, manager.VertexColorChannel);

            // Cleanup
            manager.VertexColorChannel = 0;
            manager.SetMode(SelectionDebugMode.None);
            Assert.AreEqual(SelectionDebugMode.None, manager.CurrentMode);
        }

        // [feat] 贴图通道调试参数契约：单色显示 + 材质刷新
        [Test]
        public void TextureChannel_MonoDisplay_AndRefresh_DoesNotThrow()
        {
            var manager = ModelBoxSelectionManager.Instance;
            manager.MonoDisplay = true;
            Assert.IsTrue(manager.MonoDisplay);
            Assert.DoesNotThrow(() => manager.UpdateTextureChannelProperties());
            Assert.DoesNotThrow(() => manager.RefreshDebugMaterial());

            // Cleanup
            manager.MonoDisplay = false;
            manager.SetMode(SelectionDebugMode.None);
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

        // [feat v0.6] 模型局部坐标叠加契约：标志位值 + 开关切换 + 长度参数属性
        [Test]
        public void SetOverlayFlags_LocalAxes_TogglesCorrectly()
        {
            // 枚举值契约（MeshOverlayState 序列化兼容性）
            Assert.AreEqual(1 << 5, (int)MeshOverlayFlags.LocalAxes);

            var manager = ModelBoxSelectionManager.Instance;
            manager.SetOverlayFlags(MeshOverlayFlags.None);
            Assert.AreEqual(MeshOverlayFlags.None, manager.OverlayFlags);

            manager.SetOverlayFlags(MeshOverlayFlags.LocalAxes);
            Assert.IsTrue((manager.OverlayFlags & MeshOverlayFlags.LocalAxes) != 0);

            // 可与其他叠加组合
            manager.SetOverlayFlags(MeshOverlayFlags.LocalAxes | MeshOverlayFlags.Wireframe);
            Assert.IsTrue((manager.OverlayFlags & MeshOverlayFlags.LocalAxes) != 0);
            Assert.IsTrue((manager.OverlayFlags & MeshOverlayFlags.Wireframe) != 0);

            // 轴长系数参数可设置
            manager.LocalAxesLength = 1.5f;
            Assert.AreEqual(1.5f, manager.LocalAxesLength);

            // Cleanup
            manager.LocalAxesLength = 0.5f;
            manager.SetOverlayFlags(MeshOverlayFlags.None);
            Assert.AreEqual(MeshOverlayFlags.None, manager.OverlayFlags);
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
