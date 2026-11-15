using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace ModelBox.Tests
{
    [TestFixture]
    public class SkinnedOverlayTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            ModelBoxOverlayRenderer.Cleanup();
        }

        [TearDown]
        public void TearDown()
        {
            ModelBoxOverlayRenderer.Cleanup();
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AlternatingRenderers_PreserveIndependentPoseAndTopology(bool shareMesh)
        {
            var firstMesh = CreateMesh(false, true);
            var secondMesh = shareMesh ? firstMesh : CreateMesh(true, true);
            var first = CreateRenderer(firstMesh, out var firstBone);
            var second = CreateRenderer(secondMesh, out var secondBone);
            firstBone.localPosition = new Vector3(0.25f, 0.5f, 0f);
            firstBone.localRotation = Quaternion.Euler(0f, 0f, 20f);
            secondBone.localPosition = new Vector3(-0.5f, 0.25f, 0f);
            secondBone.localRotation = Quaternion.Euler(0f, 0f, -35f);

            var firstData = ModelBoxOverlayRenderer.BakeSkinnedMesh(first);
            ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(firstData);
            var firstVertices = (Vector3[])firstData.Vertices.Clone();
            var firstNormals = (Vector3[])firstData.Normals.Clone();
            var firstTangents = (Vector4[])firstData.Tangents.Clone();
            var firstTriangles = (int[])firstData.TriangleIndices.Clone();
            var firstEdges = (int[])firstData.EdgeIndices.Clone();

            var secondData = ModelBoxOverlayRenderer.BakeSkinnedMesh(second);
            ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(secondData);

            Assert.AreNotSame(firstData, secondData, "Each renderer needs its own pose cache, even when sharing a source mesh.");
            Assert.AreNotSame(firstData.Vertices, secondData.Vertices);
            AssertVectors(firstVertices, firstData.Vertices, "First pose after baking the second renderer");
            AssertVectors(firstNormals, firstData.Normals, "First normals after baking the second renderer");
            Assert.AreEqual(firstTangents, firstData.Tangents);
            Assert.AreEqual(firstTriangles, firstData.TriangleIndices);
            Assert.AreEqual(firstEdges, firstData.EdgeIndices);
            AssertPose(first, firstBone, firstData);
            AssertPose(second, secondBone, secondData);
            Assert.AreEqual(secondMesh.triangles, secondData.TriangleIndices);

            var secondVertices = (Vector3[])secondData.Vertices.Clone();
            var secondTriangles = (int[])secondData.TriangleIndices.Clone();
            var secondEdges = (int[])secondData.EdgeIndices.Clone();
            firstBone.localPosition += Vector3.right;
            Assert.AreSame(firstData, ModelBoxOverlayRenderer.BakeSkinnedMesh(first));
            AssertPose(first, firstBone, firstData);
            AssertVectors(secondVertices, secondData.Vertices, "Second pose after returning to the first renderer");
            Assert.AreEqual(secondTriangles, secondData.TriangleIndices);
            Assert.AreEqual(secondEdges, secondData.EdgeIndices);
        }

        [Test]
        public void RepeatedBake_ReusesPoseAndTopologyArrays()
        {
            var renderer = CreateRenderer(CreateMesh(false, true), out var bone);
            var data = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
            ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(data);
            var vertices = data.Vertices;
            var normals = data.Normals;
            var tangents = data.Tangents;
            var triangles = data.TriangleIndices;
            var edges = data.EdgeIndices;

            for (int i = 1; i <= 3; i++)
            {
                bone.localPosition = Vector3.up * i * 0.25f;
                bone.localRotation = Quaternion.Euler(0f, 0f, i * 15f);
                var next = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
                Assert.AreSame(data, next);
                Assert.AreSame(vertices, next.Vertices);
                Assert.AreSame(normals, next.Normals);
                Assert.AreSame(tangents, next.Tangents);
                Assert.AreSame(triangles, next.TriangleIndices);
                Assert.AreSame(edges, next.EdgeIndices);
                AssertPose(renderer, bone, next);
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void LineMesh_ReusesMeshAndRefreshesAnimatedPose(int kind)
        {
            var renderer = CreateRenderer(CreateMesh(false, true), out var bone);
            var data = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
            var lineMesh = GetLineMesh(data, kind);
            Assert.IsNotNull(lineMesh);
            var initialVertices = lineMesh.vertices;
            var initialIndices = lineMesh.GetIndices(0);
            var initialUV = lineMesh.uv;

            bone.localPosition = new Vector3(0.5f, 0.25f, 0f);
            bone.localRotation = Quaternion.Euler(0f, 0f, 60f);
            var updated = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
            var updatedMesh = GetLineMesh(updated, kind);

            Assert.AreSame(lineMesh, updatedMesh, "Pose changes must update the existing mesh instead of allocating another one.");
            Assert.AreEqual(initialIndices, updatedMesh.GetIndices(0), "Animation must not rebuild or change line topology.");
            Assert.AreEqual(initialUV, updatedMesh.uv);
            Assert.AreEqual(MeshTopology.Lines, updatedMesh.GetTopology(0));
            Assert.Greater(Vector3.Distance(initialVertices[0], updatedMesh.vertices[0]), 0.1f,
                "The line mesh must follow the animated bone.");
            AssertPose(renderer, bone, updated);
            AssertLinePose(updated, updatedMesh, kind);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SourceAttributeChanges_DoNotRetainMissingOrLoseRestoredDirections(bool startWithAttributes)
        {
            var complete = CreateMesh(false, true);
            var incomplete = CreateMesh(false, false);
            var renderer = CreateRenderer(startWithAttributes ? complete : incomplete, out var bone);

            for (int i = 0; i < 3; i++)
            {
                bool hasAttributes = i % 2 == 0 ? startWithAttributes : !startWithAttributes;
                renderer.sharedMesh = hasAttributes ? complete : incomplete;
                bone.localRotation = Quaternion.Euler(0f, 0f, i * 20f);
                var data = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
                Assert.AreEqual(renderer.sharedMesh.vertexCount, data.Vertices.Length);
                var normalMesh = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false);
                var tangentMesh = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, true);
                if (hasAttributes)
                {
                    Assert.IsNotNull(normalMesh, "Normals must be available again after a source change.");
                    Assert.IsNotNull(tangentMesh, "Tangents must be available again after a source change.");
                    AssertLinePose(data, normalMesh, 1);
                    AssertLinePose(data, tangentMesh, 2);
                }
                else
                {
                    Assert.IsTrue(data.Normals == null || data.Normals.Length == 0, "Missing normals must not retain the previous source's data.");
                    Assert.IsTrue(data.Tangents == null || data.Tangents.Length == 0, "Missing tangents must not retain the previous source's data.");
                    Assert.IsNull(normalMesh);
                    Assert.IsNull(tangentMesh);
                }
            }
        }

        [Test]
        public void Cleanup_ReleasesEveryRendererLineMeshAndResetsPoseCache()
        {
            var first = CreateRenderer(CreateMesh(false, true), out var firstBone);
            var second = CreateRenderer(CreateMesh(true, true), out var secondBone);
            firstBone.localPosition = Vector3.right;
            secondBone.localPosition = Vector3.up;
            var firstData = ModelBoxOverlayRenderer.BakeSkinnedMesh(first);
            var lineMeshes = new Mesh[6];
            for (int kind = 0; kind < 3; kind++) lineMeshes[kind] = GetLineMesh(firstData, kind);
            var secondData = ModelBoxOverlayRenderer.BakeSkinnedMesh(second);
            for (int kind = 0; kind < 3; kind++) lineMeshes[kind + 3] = GetLineMesh(secondData, kind);
            foreach (var mesh in lineMeshes) Assert.IsNotNull(mesh);

            ModelBoxOverlayRenderer.Cleanup();

            foreach (var mesh in lineMeshes)
                Assert.IsTrue(mesh == null, "Cleanup must destroy native line meshes from every cached renderer.");
            var rebuilt = ModelBoxOverlayRenderer.BakeSkinnedMesh(first);
            Assert.AreNotSame(firstData, rebuilt);
            Assert.AreNotSame(firstData.Vertices, rebuilt.Vertices);
            AssertPose(first, firstBone, rebuilt);
            for (int kind = 0; kind < 3; kind++) Assert.IsNotNull(GetLineMesh(rebuilt, kind));
        }

        [Test]
        public void MissingRendererOrSource_ReturnsNull()
        {
            Assert.IsNull(ModelBoxOverlayRenderer.BakeSkinnedMesh(null));
            var renderer = CreateRenderer(CreateMesh(false, true), out var bone);
            ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
            renderer.sharedMesh = null;
            Assert.IsNull(ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer));
        }

        [Test]
        public void AlternatingRenderers_BakeBenchmark()
        {
            const int columns = 200;
            const int rows = 150;
            const int rounds = 20;
            var first = CreateRenderer(CreateBenchmarkMesh(columns, rows, 0f), out var firstBone);
            var second = CreateRenderer(CreateBenchmarkMesh(columns, rows, 0.25f), out var secondBone);
            firstBone.localRotation = Quaternion.Euler(0f, 0f, 20f);
            secondBone.localRotation = Quaternion.Euler(0f, 0f, -35f);
            var allocationMethod = typeof(System.GC).GetMethod("GetAllocatedBytesForCurrentThread",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var readAllocated = allocationMethod == null ? null
                : (System.Func<long>)System.Delegate.CreateDelegate(typeof(System.Func<long>), allocationMethod);
            if (readAllocated != null)
            {
                readAllocated();
                long calibrationBefore = readAllocated();
                var calibration = new byte[1024];
                System.GC.KeepAlive(calibration);
                if (readAllocated() <= calibrationBefore) readAllocated = null;
            }
            var timer = new System.Diagnostics.Stopwatch();

            for (int i = 0; i < 3; i++)
            {
                ModelBoxOverlayRenderer.BakeSkinnedMesh(first);
                ModelBoxOverlayRenderer.BakeSkinnedMesh(second);
            }
            if (readAllocated != null) readAllocated();

            long allocatedBefore = readAllocated == null ? 0 : readAllocated();
            timer.Start();
            CachedMeshData firstData = null;
            CachedMeshData secondData = null;
            for (int i = 0; i < rounds; i++)
            {
                firstData = ModelBoxOverlayRenderer.BakeSkinnedMesh(first);
                secondData = ModelBoxOverlayRenderer.BakeSkinnedMesh(second);
            }
            timer.Stop();
            long allocatedAfter = readAllocated == null ? 0 : readAllocated();

            Assert.IsNotNull(firstData);
            Assert.IsNotNull(secondData);
            Assert.AreEqual(columns * rows, firstData.Vertices.Length);
            Assert.AreEqual(columns * rows, secondData.Vertices.Length);
            Debug.Log("MODELBOX_BAKE_BENCH vertices=" + (columns * rows)
                + " renderers=2 rounds=" + rounds + " bakes=" + (rounds * 2)
                + " ms=" + timer.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " allocated=" + (readAllocated == null ? "unsupported" : (allocatedAfter - allocatedBefore).ToString()));
        }

        private Mesh CreateBenchmarkMesh(int columns, int rows, float offset)
        {
            int vertexCount = columns * rows;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector4[vertexCount];
            var weights = new BoneWeight[vertexCount];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    int i = y * columns + x;
                    vertices[i] = new Vector3(x * 0.01f + offset, y * 0.01f, 0f);
                    normals[i] = Vector3.forward;
                    tangents[i] = new Vector4(1f, 0f, 0f, 1f);
                    weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                }
            }
            var triangles = new int[(columns - 1) * (rows - 1) * 6];
            int index = 0;
            for (int y = 0; y < rows - 1; y++)
            {
                for (int x = 0; x < columns - 1; x++)
                {
                    int i = y * columns + x;
                    triangles[index++] = i;
                    triangles[index++] = i + 1;
                    triangles[index++] = i + columns;
                    triangles[index++] = i + 1;
                    triangles[index++] = i + columns + 1;
                    triangles[index++] = i + columns;
                }
            }
            var mesh = new Mesh
            {
                name = "SkinnedOverlayBenchmarkSource",
                vertices = vertices,
                normals = normals,
                tangents = tangents,
                triangles = triangles,
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = weights
            };
            _objects.Add(mesh);
            return mesh;
        }

        private Mesh CreateMesh(bool quad, bool attributes)
        {
            var mesh = new Mesh { name = "SkinnedOverlaySource" };
            _objects.Add(mesh);
            mesh.vertices = quad
                ? new[] { Vector3.left, Vector3.up, Vector3.right, Vector3.down }
                : new[] { Vector3.left, Vector3.up, Vector3.right };
            mesh.triangles = quad ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 1, 2 };
            mesh.bindposes = new[] { Matrix4x4.identity };
            var weights = new BoneWeight[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector4[mesh.vertexCount];
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
            }
            mesh.boneWeights = weights;
            if (attributes)
            {
                mesh.normals = normals;
                mesh.tangents = tangents;
            }
            return mesh;
        }

        private SkinnedMeshRenderer CreateRenderer(Mesh mesh, out Transform bone)
        {
            var root = new GameObject("SkinnedOverlayFixture");
            _objects.Add(root);
            var rendererObject = new GameObject("Renderer", typeof(SkinnedMeshRenderer));
            rendererObject.transform.SetParent(root.transform, false);
            bone = new GameObject("Bone").transform;
            bone.SetParent(root.transform, false);
            var renderer = rendererObject.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;
            return renderer;
        }

        private static Mesh GetLineMesh(CachedMeshData data, int kind)
        {
            return kind == 0 ? ModelBoxOverlayRenderer.GetOrBuildWireframeLineMesh(data)
                : ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, kind == 2);
        }

        private static void AssertPose(SkinnedMeshRenderer renderer, Transform bone, CachedMeshData data)
        {
            var source = renderer.sharedMesh.vertices;
            var sourceNormals = renderer.sharedMesh.normals;
            var sourceTangents = renderer.sharedMesh.tangents;
            var pose = renderer.transform.worldToLocalMatrix * bone.localToWorldMatrix
                * renderer.sharedMesh.bindposes[0];
            Assert.AreEqual(source.Length, data.Vertices.Length);
            for (int i = 0; i < source.Length; i++)
            {
                AssertVector(pose.MultiplyPoint3x4(source[i]), data.Vertices[i], "Pose vertex " + i);
                if (sourceNormals.Length == source.Length)
                    AssertVector(pose.MultiplyVector(sourceNormals[i]).normalized, data.Normals[i].normalized,
                        "Pose normal " + i);
                if (sourceTangents.Length == source.Length)
                {
                    var tangent = sourceTangents[i];
                    var actual = data.Tangents[i];
                    var expectedDirection = pose.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
                    AssertVector(expectedDirection, new Vector3(actual.x, actual.y, actual.z).normalized,
                        "Pose tangent " + i);
                }
            }
        }

        private static void AssertLinePose(CachedMeshData data, Mesh mesh, int kind)
        {
            var positions = mesh.vertices;
            var directions = mesh.normals;
            int copies = kind == 0 ? 1 : 2;
            Assert.AreEqual(data.Vertices.Length * copies, positions.Length, "Every source vertex must remain visible.");
            for (int i = 0; i < data.Vertices.Length; i++)
            {
                for (int copy = 0; copy < copies; copy++)
                    AssertVector(data.Vertices[i], positions[i * copies + copy], "Line base " + i);
                if (kind == 0) continue;
                var tangent = kind == 2 ? data.Tangents[i] : Vector4.zero;
                var direction = kind == 1 ? data.Normals[i]
                    : new Vector3(tangent.x, tangent.y, tangent.z).normalized;
                AssertVector(direction, directions[i * 2], "Line direction " + i);
                AssertVector(direction, directions[i * 2 + 1], "Line tip direction " + i);
            }
            Assert.AreEqual(kind == 0 ? data.EdgeIndices.Length : data.Vertices.Length * 2,
                mesh.GetIndices(0).Length, "Every source edge or direction line must remain visible.");
        }

        private static void AssertVectors(Vector3[] expected, Vector3[] actual, string label)
        {
            Assert.AreEqual(expected.Length, actual.Length, label + " count");
            for (int i = 0; i < expected.Length; i++) AssertVector(expected[i], actual[i], label + " " + i);
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string label)
        {
            Assert.Less(Vector3.Distance(expected, actual), 1e-4f, label);
        }
    }
}
