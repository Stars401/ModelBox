using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ModelBox.Tests
{
    [TestFixture]
    public class NormalRenderingTests
    {
        private const int Resolution = 256;
        private const float Length = 0.8f;

        [TearDown]
        public void TearDown()
        {
            ModelBoxOverlayRenderer.Cleanup();
        }

        [TestCase(0, 1f)]
        [TestCase(0, 4f)]
        [TestCase(1, 1f)]
        [TestCase(1, 4f)]
        [TestCase(2, 1f)]
        [TestCase(2, 4f)]
        public void NormalDirection_NonUniformScaleKeepsExistingLength(int path, float width)
        {
            RunInRepaint(() => CheckDirectionPixels(path, width, false));
        }

        [TestCase(0, 4f)]
        public void TangentDirection_NonUniformScaleKeepsExistingBehavior(int path, float width)
        {
            RunInRepaint(() => CheckDirectionPixels(path, width, true));
        }

        [Test]
        public void BoneWeightSurface_DepthOffThenOnRestoresOcclusion()
        {
            RunInRepaint(() =>
            {
                var cameraObject = new GameObject("WeightDepthCamera", typeof(Camera));
                cameraObject.GetComponent<Camera>().enabled = false;
                var data = new CachedMeshData
                {
                    Vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                    TriangleIndices = new[] { 0, 1, 2 }
                };
                var weights = new[] { 1f, 0.5f, 0f };
                try
                {
                    var camera = cameraObject.GetComponent<Camera>();
                    var bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
                    ModelBoxOverlayRenderer.DrawBoneWeightSurface(data, Matrix4x4.identity, weights, camera, bounds, false);
                    var field = typeof(ModelBoxOverlayRenderer).GetField("_boneWeightSurfaceMat",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    var material = (Material)field.GetValue(null);
                    Assert.IsNotNull(material);
                    Assert.AreEqual((int)CompareFunction.Always, material.GetInt("_ZTest"));

                    ModelBoxOverlayRenderer.DrawBoneWeightSurface(data, Matrix4x4.identity, weights, camera, bounds, true);

                    Assert.AreSame(material, field.GetValue(null), "Changing depth state should reuse the surface material.");
                    Assert.AreEqual((int)CompareFunction.LessEqual, material.GetInt("_ZTest"),
                        "Re-enabling overlay depth must restore occlusion after through-surface display.");
                }
                finally
                {
                    Object.DestroyImmediate(cameraObject);
                }
            });
        }

        private static void CheckDirectionPixels(int path, float width, bool tangents)
        {
            var manager = ModelBoxSelectionManager.Instance;
            var settings = ModelBoxSettings.GetOrCreate();
            var savedFlags = manager.OverlayFlags;
            bool savedGPU = manager.UseGPURendering;
            bool savedDepth = settings.OverlayDepthTest;
            var savedNormalColor = manager.NormalColor;
            var savedTangentColor = manager.TangentColor;
            float savedNormalLength = manager.NormalLength;
            float savedTangentLength = manager.TangentLength;
            float savedNormalWidth = manager.NormalWidth;
            float savedTangentWidth = manager.TangentWidth;
            var savedTarget = RenderTexture.active;
            var savedCamera = Camera.current;
            var savedMatrix = Handles.matrix;
            var savedColor = Handles.color;
            var savedZTest = Handles.zTest;
            var cameraObject = new GameObject("NormalDirectionCamera", typeof(Camera));
            var rendererObject = new GameObject("NormalDirectionRenderer", typeof(MeshFilter), typeof(MeshRenderer));
            var target = new RenderTexture(Resolution, Resolution, 24);
            var pixels = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false);
            var direction = new Vector3(1f, 1f, 0f).normalized;
            var vertex = new Vector3(-0.4f, -0.7f, 0f);
            var mesh = new Mesh
            {
                vertices = new[] { vertex },
                normals = new[] { direction },
                tangents = new[] { new Vector4(direction.x, direction.y, 0f, 1f) }
            };
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
                rendererObject.GetComponent<MeshFilter>().sharedMesh = mesh;
                var rotation = Quaternion.Euler(0f, 0f, 20f);
                rendererObject.transform.SetPositionAndRotation(new Vector3(0.3f, -0.2f, 0f), rotation);
                rendererObject.transform.localScale = new Vector3(2f, 1f, 1f);
                var renderer = rendererObject.GetComponent<Renderer>();
                var matrix = rendererObject.transform.localToWorldMatrix;
                settings.OverlayDepthTest = false;
                manager.UseGPURendering = path == 0;
                manager.SetOverlayFlags(tangents ? MeshOverlayFlags.Tangents : MeshOverlayFlags.Normals);
                manager.NormalColor = manager.TangentColor = Color.white;
                manager.NormalLength = manager.TangentLength = Length;
                manager.NormalWidth = manager.TangentWidth = width;
                RenderTexture.active = target;
                Handles.SetCamera(camera);
                Handles.matrix = Matrix4x4.identity;
                Handles.zTest = CompareFunction.Always;
                GL.Clear(true, true, Color.black);

                // 0 = GPU manager path, 1 = Legacy manager path, 2 = public Handles fallback.
                if (path < 2)
                {
                    var draw = typeof(ModelBoxSelectionManager).GetMethod("DrawMeshOverlay",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    draw.Invoke(manager, new object[] { renderer, mesh, camera, false, null });
                }
                else
                {
                    var data = ModelBoxMeshCache.GetOrBuild(mesh, false);
                    if (tangents)
                        ModelBoxOverlayRenderer.DrawTangents(data, matrix, Color.white, Length, width, camera, renderer.bounds);
                    else
                        ModelBoxOverlayRenderer.DrawNormals(data, matrix, Color.white, Length, width, camera, renderer.bounds);
                }

                pixels.ReadPixels(new Rect(0f, 0f, Resolution, Resolution), 0, 0);
                pixels.Apply();
                var colors = pixels.GetPixels32();
                var start = matrix.MultiplyPoint3x4(vertex);
                var worldDirection = rotation * (tangents
                    ? new Vector3(2f, 1f, 0f).normalized : new Vector3(0.5f, 1f, 0f).normalized);
                float worldLength = Mathf.Sqrt(2.5f) * Length;
                foreach (float sample in new[] { 0.25f, 0.5f, 0.75f, 0.96f })
                    Assert.IsTrue(HasVisibleNear(colors, camera.WorldToScreenPoint(start + worldDirection * (worldLength * sample))),
                        "No line pixel at expected direction/length. Path=" + path + ", width=" + width + ", sample=" + sample);
                Assert.IsFalse(HasVisibleNear(colors, camera.WorldToScreenPoint(start + worldDirection * (worldLength * 1.25f))),
                    "The corrected line must retain its previous scaled length.");
                if (!tangents)
                {
                    var oldDirection = rotation * new Vector3(2f, 1f, 0f).normalized;
                    Assert.IsFalse(HasVisibleNear(colors, camera.WorldToScreenPoint(start + oldDirection * (worldLength * 0.9f))),
                        "The ordinary-vector normal direction must no longer be drawn.");
                }
            }
            finally
            {
                GL.PopMatrix();
                Camera.SetupCurrent(savedCamera);
                RenderTexture.active = savedTarget;
                Handles.matrix = savedMatrix;
                Handles.color = savedColor;
                Handles.zTest = savedZTest;
                settings.OverlayDepthTest = savedDepth;
                manager.UseGPURendering = savedGPU;
                manager.NormalColor = savedNormalColor;
                manager.TangentColor = savedTangentColor;
                manager.NormalLength = savedNormalLength;
                manager.TangentLength = savedTangentLength;
                manager.NormalWidth = savedNormalWidth;
                manager.TangentWidth = savedTangentWidth;
                manager.SetOverlayFlags(savedFlags);
                ModelBoxMeshCache.InvalidateMesh(mesh);
                Object.DestroyImmediate(rendererObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(pixels);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static bool HasVisibleNear(Color32[] pixels, Vector3 point)
        {
            int centerX = Mathf.RoundToInt(point.x), centerY = Mathf.RoundToInt(point.y);
            for (int y = Mathf.Max(0, centerY - 2); y <= Mathf.Min(Resolution - 1, centerY + 2); y++)
                for (int x = Mathf.Max(0, centerX - 2); x <= Mathf.Min(Resolution - 1, centerX + 2); x++)
                    if (pixels[y * Resolution + x].r > 32) return true;
            return false;
        }

        private static void RunInRepaint(Action action)
        {
            var window = ScriptableObject.CreateInstance<OverlayRenderTestWindow>();
            try
            {
                window.ShowUtility();
                window.DrawAction = action;
                window.SendEvent(new Event { type = EventType.Repaint });
                Assert.IsTrue(window.DidDraw, "The test must run inside a real Repaint event.");
                if (window.Failure != null) throw window.Failure;
            }
            finally
            {
                window.Close();
            }
        }
    }
}
