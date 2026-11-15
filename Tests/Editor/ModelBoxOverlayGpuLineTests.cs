using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ModelBox.Tests
{
    [TestFixture]
    public class OverlayGpuLineTests
    {
        private const int Resolution = 192;

        [TearDown]
        public void TearDown()
        {
            ModelBoxOverlayRenderer.Cleanup();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StretchRibbon_EncodesFourCornersAndTwoTriangles(bool tangents)
        {
            var data = CreateData();
            var mesh = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents, true);
            Assert.IsNotNull(mesh);
            Assert.AreEqual(data.Vertices.Length * 4, mesh.vertexCount);
            Assert.AreEqual(MeshTopology.Triangles, mesh.GetTopology(0));
            var positions = mesh.vertices;
            var directions = mesh.normals;
            var uvs = mesh.uv;
            var indices = mesh.GetIndices(0);
            Assert.AreEqual(data.Vertices.Length * 6, indices.Length);
            var expectedUVs = new[] { new Vector2(0f, -1f), new Vector2(0f, 1f), new Vector2(1f, -1f), new Vector2(1f, 1f) };

            for (int i = 0; i < data.Vertices.Length; i++)
            {
                var tangent = data.Tangents[i];
                var direction = tangents ? new Vector3(tangent.x, tangent.y, tangent.z) : data.Normals[i];
                for (int corner = 0; corner < 4; corner++)
                {
                    Assert.AreEqual(data.Vertices[i], positions[i * 4 + corner], "base point");
                    Assert.AreEqual(direction, directions[i * 4 + corner], "local direction");
                    Assert.AreEqual(expectedUVs[corner], uvs[i * 4 + corner], "endpoint and side");
                }

                var first = new HashSet<int>();
                var second = new HashSet<int>();
                for (int corner = 0; corner < 6; corner++)
                {
                    int index = indices[i * 6 + corner] - i * 4;
                    Assert.That(index, Is.InRange(0, 3), "A triangle must stay within its source line.");
                    (corner < 3 ? first : second).Add(index);
                }
                Assert.AreEqual(3, first.Count, "first triangle");
                Assert.AreEqual(3, second.Count, "second triangle");
                var shared = new HashSet<int>(first);
                shared.IntersectWith(second);
                Assert.IsTrue(shared.SetEquals(new[] { 0, 3 }) || shared.SetEquals(new[] { 1, 2 }), "The two triangles must share a diagonal.");
                first.UnionWith(second);
                Assert.AreEqual(4, first.Count, "Every ribbon corner must be covered.");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StretchLineMesh_ThinAndThickCachesRemainIndependent(bool tangents)
        {
            var data = CreateData();
            var thin = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents);
            var thick = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents, true);
            Assert.AreNotSame(thin, thick);
            Assert.AreEqual(MeshTopology.Lines, thin.GetTopology(0));
            Assert.AreEqual(data.Vertices.Length * 2, thin.vertexCount);
            Assert.AreEqual(data.Vertices.Length * 2, thin.GetIndices(0).Length);
            Assert.AreEqual(data.Vertices.Length * 4, thick.vertexCount);
            Assert.AreEqual(data.Vertices.Length * 6, thick.GetIndices(0).Length);
            Assert.AreSame(thin, ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents));
            Assert.AreSame(thick, ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents, true));
        }

        [Test]
        public void StretchRibbon_NormalsAndTangentsHaveIndependentCaches()
        {
            var data = CreateData();
            var normals = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false, true);
            var tangents = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, true, true);
            Assert.AreNotSame(normals, tangents);
            Assert.AreEqual(Vector3.up, normals.normals[0]);
            Assert.AreEqual(Vector3.right, tangents.normals[0]);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void StretchLineMesh_MissingDirectionsReturnsNull(bool tangents, bool thick)
        {
            var data = CreateData();
            if (tangents) data.Tangents = null;
            else data.Normals = null;
            Assert.IsNull(ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents, thick));
            if (tangents) data.Tangents = new Vector4[1];
            else data.Normals = new Vector3[1];
            Assert.IsNull(ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, tangents, thick));
        }

        [Test]
        public void StretchRibbon_RendersVisiblePixelsAndRespondsToWidth()
        {
            var window = ScriptableObject.CreateInstance<OverlayRenderTestWindow>();
            try
            {
                window.ShowUtility();
                window.DrawAction = () =>
                {
                    var data = CreateData();
                    data.Vertices = new[] { new Vector3(-1f, -0.5f, 0f), new Vector3(0f, -0.5f, 0f), new Vector3(1f, -0.5f, 0f) };
                    data.Normals = new[] { Vector3.up, Vector3.up, Vector3.up };
                    var mesh = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false, true);
                    int narrowPixels = CountVisiblePixels(RenderLine(mesh, 2f, Matrix4x4.identity));
                    int widePixels = CountVisiblePixels(RenderLine(mesh, 6f, Matrix4x4.identity));
                    Assert.Greater(narrowPixels, 20, "The reference ribbon must actually render.");
                    Assert.Greater(widePixels, narrowPixels * 1.5f, "Increasing width must increase the visible ribbon area.");
                };
                window.SendEvent(new Event { type = EventType.Repaint });
                Assert.IsTrue(window.DidDraw, "The render test must run inside a real GUI Repaint event.");
                if (window.Failure != null) throw window.Failure;
            }
            finally
            {
                window.Close();
            }
        }

        [TestCase(false, 1f)]
        [TestCase(true, 1f)]
        [TestCase(false, 4f)]
        [TestCase(true, 4f)]
        public void StretchLine_WorldMatrixPlacesPixelsAtExpectedEndpoints(bool transformed, float width)
        {
            var window = ScriptableObject.CreateInstance<OverlayRenderTestWindow>();
            try
            {
                window.ShowUtility();
                window.DrawAction = () =>
                {
                    var data = CreateData();
                    data.Vertices = new[] { new Vector3(-1f, -0.5f, 0f), new Vector3(0f, -0.5f, 0f), new Vector3(1f, -0.5f, 0f) };
                    data.Normals = new[] { Vector3.up, Vector3.up, Vector3.up };
                    var matrix = transformed
                        ? Matrix4x4.TRS(new Vector3(1f, 0.5f, 0f), Quaternion.Euler(0f, 0f, 90f), Vector3.one)
                        : Matrix4x4.identity;
                    var worldStarts = transformed
                        ? new[] { new Vector3(1.5f, -0.5f, 0f), new Vector3(1.5f, 0.5f, 0f), new Vector3(1.5f, 1.5f, 0f) }
                        : new[] { new Vector3(-1f, -0.5f, 0f), new Vector3(0f, -0.5f, 0f), new Vector3(1f, -0.5f, 0f) };
                    var worldDirection = transformed ? Vector3.left : Vector3.up;
                    var mesh = ModelBoxOverlayRenderer.GetOrBuildStretchLineMesh(data, false, width > 1f);
                    RenderLine(mesh, width, matrix, (camera, pixels) =>
                    {
                        Assert.Greater(CountVisiblePixels(pixels), 20, "The GPU line must actually render.");
                        for (int line = 0; line < worldStarts.Length; line++)
                        {
                            for (int sample = 0; sample <= 4; sample++)
                            {
                                var worldPoint = worldStarts[line] + worldDirection * (0.75f * sample / 4f);
                                var screenPoint = camera.WorldToScreenPoint(worldPoint);
                                AssertVisibleNear(pixels, screenPoint, "line " + line + ", sample " + sample);
                            }
                        }
                    });
                };
                window.SendEvent(new Event { type = EventType.Repaint });
                Assert.IsTrue(window.DidDraw, "The render test must run inside a real GUI Repaint event.");
                if (window.Failure != null) throw window.Failure;
            }
            finally
            {
                window.Close();
            }
        }

        private static CachedMeshData CreateData()
        {
            return new CachedMeshData
            {
                Vertices = new[] { Vector3.left, Vector3.up, Vector3.right },
                Normals = new[] { Vector3.up, Vector3.forward, Vector3.right },
                Tangents = new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 1f, 0f, -1f), new Vector4(0f, 0f, 1f, 1f) }
            };
        }

        private static int CountVisiblePixels(Color32[] pixels)
        {
            int count = 0;
            foreach (var pixel in pixels)
                if (pixel.r > 32) count++;
            return count;
        }

        private static void AssertVisibleNear(Color32[] pixels, Vector3 screenPoint, string label)
        {
            int centerX = Mathf.RoundToInt(screenPoint.x);
            int centerY = Mathf.RoundToInt(screenPoint.y);
            for (int y = Mathf.Max(0, centerY - 3); y <= Mathf.Min(Resolution - 1, centerY + 3); y++)
            {
                for (int x = Mathf.Max(0, centerX - 3); x <= Mathf.Min(Resolution - 1, centerX + 3); x++)
                    if (pixels[y * Resolution + x].r > 32) return;
            }
            Assert.Fail("No GPU line pixel near the expected world position: " + label + " at " + screenPoint);
        }

        private static Color32[] RenderLine(Mesh mesh, float width, Matrix4x4 localToWorld,
            System.Action<Camera, Color32[]> check = null)
        {
            var savedTarget = RenderTexture.active;
            var savedCamera = Camera.current;
            var cameraObject = new GameObject("OverlayRibbonCamera", typeof(Camera));
            var target = new RenderTexture(Resolution, Resolution, 24);
            var pixels = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false);
            target.Create();
            GL.PushMatrix();
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 2.5f;
                camera.transform.position = new Vector3(0f, 0f, -10f);
                camera.targetTexture = target;
                RenderTexture.active = target;
                Handles.SetCamera(camera);
                GL.Clear(true, true, Color.black);
                ModelBoxOverlayRenderer.DrawOverlayLineMesh(mesh, localToWorld, camera, Color.white, 0.75f, false, width);
                pixels.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                pixels.Apply();
                var result = pixels.GetPixels32();
                check?.Invoke(camera, result);
                return result;
            }
            finally
            {
                GL.PopMatrix();
                Camera.SetupCurrent(savedCamera);
                RenderTexture.active = savedTarget;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(pixels);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }
    }
}
