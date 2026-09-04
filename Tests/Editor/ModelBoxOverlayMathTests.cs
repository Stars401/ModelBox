using NUnit.Framework;
using UnityEngine;

namespace ModelBox.Tests
{
    /// <summary>
    /// [perf v0.6] 叠加渲染数学与烘焙线网格构建的正确性契约。
    /// 局部坐标轴端点计算是「信息必须正确」红线的直接兜底 — 手算常量验证旋转映射。
    /// </summary>
    [TestFixture]
    public class OverlayMathTests
    {
        // ===== 局部坐标轴端点（信息正确性红线） =====
        // 注意：Euler→Quaternion→旋转链会携带 ~1e-8 级浮点噪声，Vector3.Equals 的 1e-10 严格容差
        // 会把物理上完全正确的方向判为失败（Expected/Actual 显示相同仍失败）——必须逐分量 + 容差断言。

        private static void AssertVector3Approx(Vector3 expected, Vector3 actual, string label)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-5f, label + ".x");
            Assert.AreEqual(expected.y, actual.y, 1e-5f, label + ".y");
            Assert.AreEqual(expected.z, actual.z, 1e-5f, label + ".z");
        }

        [Test]
        public void LocalAxes_Endpoints_IdentityRotation()
        {
            var tips = new Vector3[3];
            ModelBoxOverlayRenderer.ComputeLocalAxesEndpoints(new Vector3(1, 2, 3), Quaternion.identity, 2f, tips);
            AssertVector3Approx(new Vector3(3, 2, 3), tips[0], "X"); // local +X
            AssertVector3Approx(new Vector3(1, 4, 3), tips[1], "Y"); // local +Y
            AssertVector3Approx(new Vector3(1, 2, 5), tips[2], "Z"); // local +Z
        }

        [Test]
        public void LocalAxes_Endpoints_Yaw90_HandComputed()
        {
            // 手算常量：Yaw +90°（Unity 约定，forward→right）
            // local +X → world -Z (back)，local +Y → world +Y，local +Z → world +X (right)
            var tips = new Vector3[3];
            ModelBoxOverlayRenderer.ComputeLocalAxesEndpoints(Vector3.zero, Quaternion.Euler(0f, 90f, 0f), 1f, tips);
            AssertVector3Approx(new Vector3(0f, 0f, -1f), tips[0], "X");
            AssertVector3Approx(new Vector3(0f, 1f, 0f), tips[1], "Y");
            AssertVector3Approx(new Vector3(1f, 0f, 0f), tips[2], "Z");
        }

        [Test]
        public void LocalAxes_Endpoints_Pitch90_HandComputed()
        {
            // Pitch +90°：X 不变，up→forward，forward→-up：用手算常量验证非对称旋转
            var tips = new Vector3[3];
            ModelBoxOverlayRenderer.ComputeLocalAxesEndpoints(Vector3.zero, Quaternion.Euler(90f, 0f, 0f), 1f, tips);
            AssertVector3Approx(new Vector3(1f, 0f, 0f), tips[0], "X");
            AssertVector3Approx(new Vector3(0f, 0f, 1f), tips[1], "Y");
            AssertVector3Approx(new Vector3(0f, -1f, 0f), tips[2], "Z");
        }

        // ===== 局部坐标轴长度 =====

        [Test]
        public void LocalAxes_Length_Fallback_WhenNoBounds()
        {
            Assert.AreEqual(0.5f, ModelBoxOverlayRenderer.ComputeLocalAxesLength(false, Vector3.zero, 0.5f));
            Assert.AreEqual(0.5f, ModelBoxOverlayRenderer.ComputeLocalAxesLength(false, new Vector3(9, 9, 9), 2f));
        }

        [Test]
        public void LocalAxes_Length_CoefficientScalesExtents()
        {
            // 2×2×2 立方体：extents=(1,1,1)，对角线 √3 ≈ 1.7320508
            float len = ModelBoxOverlayRenderer.ComputeLocalAxesLength(true, new Vector3(1, 1, 1), 0.5f);
            Assert.AreEqual(1.7320508f * 0.5f, len, 1e-4f);
            len = ModelBoxOverlayRenderer.ComputeLocalAxesLength(true, new Vector3(1, 1, 1), 2f);
            Assert.AreEqual(1.7320508f * 2f, len, 1e-4f);
        }

        [Test]
        public void LocalAxes_Length_ClampsToMinimum()
        {
            float len = ModelBoxOverlayRenderer.ComputeLocalAxesLength(true, new Vector3(0.01f, 0f, 0f), 0.1f);
            Assert.AreEqual(0.05f, len);
        }

        // ===== 烘焙线网格构建（GPU 快速路径的正确性契约） =====

        [Test]
        public void WireframeLineMesh_SharesVerticesAndLineIndices()
        {
            var verts = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
            // 三角形 (0,1,2) 的唯一边：(0,1) (1,2) (0,2)
            var edges = new[] { 0, 1, 1, 2, 0, 2 };
            var data = new CachedMeshData { Vertices = verts, EdgeIndices = edges };

            var mesh = ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(data);
            Assert.IsNotNull(mesh);
            Assert.AreEqual(4, mesh.vertexCount);                    // 共享原始顶点，不重复
            Assert.AreEqual(MeshTopology.Lines, mesh.GetTopology(0));
            Assert.AreEqual(6, mesh.GetIndices(0).Length);          // 3 条边 × 2 索引
            for (int i = 0; i < 4; i++)
                Assert.AreEqual(Vector2.zero, mesh.uv[i]);           // uv0.x=0 → shader 不拉伸
            Assert.AreSame(mesh, ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(data)); // 二次调用命中缓存
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void StretchLineMesh_EncodesDirAndTipFlag()
        {
            var verts = new[] { Vector3.zero, new Vector3(1, 0, 0), new Vector3(2, 0, 0) };
            var dirs = new[] { Vector3.up, Vector3.forward, Vector3.right };
            var data = new CachedMeshData { Vertices = verts, Normals = dirs };

            var mesh = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false);
            Assert.IsNotNull(mesh);
            Assert.AreEqual(6, mesh.vertexCount); // 每顶点 2 个端点
            var uvs = mesh.uv;
            var positions = mesh.vertices;
            var normals = mesh.normals;
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(0f, uvs[i * 2].x);      // 起点：不拉伸
                Assert.AreEqual(1f, uvs[i * 2 + 1].x);  // 终点：按 _Length 拉伸
                Assert.AreEqual(verts[i], positions[i * 2]);      // 起点位于基点
                Assert.AreEqual(verts[i], positions[i * 2 + 1]);  // 终点基点相同，由 shader 拉伸
                Assert.AreEqual(dirs[i], normals[i * 2]);        // 方向存于 normal 通道
                Assert.AreEqual(dirs[i], normals[i * 2 + 1]);
            }
            Assert.AreEqual(MeshTopology.Lines, mesh.GetTopology(0));
            var idx = mesh.GetIndices(0);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(i * 2, idx[i * 2]);
                Assert.AreEqual(i * 2 + 1, idx[i * 2 + 1]);
            }
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void StretchLineMesh_MissingData_ReturnsNull()
        {
            var data = new CachedMeshData { Vertices = new[] { Vector3.zero } }; // 无 normals
            Assert.IsNull(ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false));
            Assert.IsNull(ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, true));
            var empty = new CachedMeshData();
            Assert.IsNull(ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(empty));
        }
    }
}
