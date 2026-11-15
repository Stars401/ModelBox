using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox.Tests
{
    [TestFixture]
    public class OverlayRenderingTests
    {
        private const int Resolution = 192;

        [TestCase(MeshOverlayFlags.Wireframe, 1f)]
        [TestCase(MeshOverlayFlags.Normals, 1f)]
        [TestCase(MeshOverlayFlags.Normals, 3f)]
        [TestCase(MeshOverlayFlags.Tangents, 1f)]
        [TestCase(MeshOverlayFlags.Tangents, 3f)]
        [TestCase(MeshOverlayFlags.Bounds, 1f)]
        [TestCase(MeshOverlayFlags.Bounds, 3f)]
        public void BatchedHandles_IncomingMatrixDoesNotChangePixels(MeshOverlayFlags flag, float width)
        {
            RunInRepaint(() => CheckOverlayPixels(flag, width));
        }

        private static void CheckOverlayPixels(MeshOverlayFlags flag, float width)
        {
            var incoming = Matrix4x4.TRS(new Vector3(2f, -1f, 0f),
                Quaternion.Euler(0f, 0f, 90f), new Vector3(1.5f, 0.75f, 1f));
            var expected = RenderOverlay(flag, width, Matrix4x4.identity);
            var actual = RenderOverlay(flag, width, incoming);
            int visiblePixels = 0;
            int differentPixels = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].r > 32) visiblePixels++;
                if (!expected[i].Equals(actual[i])) differentPixels++;
            }
            Assert.Greater(visiblePixels, 10, "The reference overlay must actually render.");
            Assert.AreEqual(0, differentPixels, "Incoming Handles.matrix transformed the overlay a second time.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectionOverlay_RestoresCallerHandleState(bool useGPU)
        {
            RunInRepaint(() => CheckSelectionHandleState(useGPU));
        }

        private static void CheckSelectionHandleState(bool useGPU)
        {
            var manager = ModelBoxSelectionManager.Instance;
            var savedFlags = manager.OverlayFlags;
            bool savedGPU = manager.UseGPURendering;
            var savedColor = Handles.color;
            var savedMatrix = Handles.matrix;
            var savedZTest = Handles.zTest;
            var go = new GameObject("OverlayStateTest", typeof(MeshFilter), typeof(MeshRenderer));
            var mesh = CreateMesh();
            try
            {
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(90f, 0f, 0f));
                manager.SetOverlayFlags(MeshOverlayFlags.Bounds);
                manager.UseGPURendering = useGPU;
                var incoming = Matrix4x4.Translate(new Vector3(4f, 5f, 6f));
                Handles.matrix = incoming;
                Handles.color = Color.magenta;
                Handles.zTest = CompareFunction.Greater;

                var draw = typeof(ModelBoxSelectionManager).GetMethod("DrawMeshOverlay",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(draw);
                draw.Invoke(manager, new object[] { go.GetComponent<Renderer>(), mesh, null, false, null });

                Assert.AreEqual(incoming, Handles.matrix, "matrix");
                Assert.AreEqual(Color.magenta, Handles.color, "color");
                Assert.AreEqual(CompareFunction.Greater, Handles.zTest, "zTest");
            }
            finally
            {
                manager.UseGPURendering = savedGPU;
                manager.SetOverlayFlags(savedFlags);
                Handles.matrix = savedMatrix;
                Handles.color = savedColor;
                Handles.zTest = savedZTest;
                ModelBoxMeshCache.InvalidateMesh(mesh);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mesh);
                ModelBoxOverlayRenderer.Cleanup();
            }
        }

        [TestCase(1f, 1f, false)]
        [TestCase(2f, 1f, false)]
        [TestCase(0.01f, 1f, false)]
        [TestCase(-1f, 1f, false)]
        [TestCase(1f, 2f, false)]
        [TestCase(2f, 0.5f, true)]
        public void BakedPose_RendererMatrixMatchesAnimatedBone(float scale, float rendererScale, bool nonUniform)
        {
            var root = new GameObject("OverlayPoseTest");
            var mesh = CreateMesh();
            try
            {
                root.transform.SetPositionAndRotation(new Vector3(3f, 2f, -1f), Quaternion.Euler(90f, 30f, 0f));
                root.transform.localScale = (nonUniform ? new Vector3(1f, 0.5f, 1.5f) : Vector3.one) * scale;
                var rendererObject = new GameObject("Renderer", typeof(SkinnedMeshRenderer));
                rendererObject.transform.SetParent(root.transform, false);
                rendererObject.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                rendererObject.transform.localScale = Vector3.one * rendererScale;
                var bone = new GameObject("Bone").transform;
                bone.SetParent(root.transform, false);
                var bindpose = bone.worldToLocalMatrix * rendererObject.transform.localToWorldMatrix;
                mesh.bindposes = new[] { bindpose };
                mesh.boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f }
                };
                var smr = rendererObject.GetComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = new[] { bone };
                smr.rootBone = bone;
                bone.localPosition = new Vector3(0.25f, 0.5f, 0f);
                bone.localRotation = Quaternion.Euler(0f, 0f, 25f);

                var baked = ModelBoxOverlayRenderer.BakeSkinnedMesh(smr);
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    var expected = (bone.localToWorldMatrix * bindpose).MultiplyPoint3x4(mesh.vertices[i]);
                    var actual = smr.transform.localToWorldMatrix.MultiplyPoint3x4(baked.Vertices[i]);
                    Assert.Less(Vector3.Distance(expected, actual), 1e-4f, "vertex " + i);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
                ModelBoxOverlayRenderer.Cleanup();
            }
        }

        [TestCase(EventType.Layout)]
        [TestCase(EventType.MouseMove)]
        public void SelectionOverlay_NonRepaintDoesNotBakePose(EventType eventType)
        {
            RunInRepaint(() =>
            {
                var manager = ModelBoxSelectionManager.Instance;
                var savedFlags = manager.OverlayFlags;
                bool savedGPU = manager.UseGPURendering;
                var go = new GameObject("OverlayEventTest", typeof(SkinnedMeshRenderer));
                var mesh = CreateMesh();
                try
                {
                    var bone = new GameObject("Bone").transform;
                    bone.SetParent(go.transform, false);
                    mesh.bindposes = new[] { Matrix4x4.identity };
                    mesh.boneWeights = new[]
                    {
                        new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                        new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                        new BoneWeight { boneIndex0 = 0, weight0 = 1f }
                    };
                    var renderer = go.GetComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.bones = new[] { bone };
                    renderer.rootBone = bone;
                    var data = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
                    var vertices = (Vector3[])data.Vertices.Clone();
                    bone.localPosition = Vector3.up;
                    manager.SetOverlayFlags(MeshOverlayFlags.Normals);
                    manager.UseGPURendering = true;
                    Event.current.type = eventType;
                    var draw = typeof(ModelBoxSelectionManager).GetMethod("DrawMeshOverlay",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(draw);
                    draw.Invoke(manager, new object[] { renderer, mesh, null, true, renderer });
                    CollectionAssert.AreEqual(vertices, data.Vertices,
                        "Layout/MouseMove must not bake or update the renderer pose.");
                }
                finally
                {
                    manager.UseGPURendering = savedGPU;
                    manager.SetOverlayFlags(savedFlags);
                    ModelBoxOverlayRenderer.Cleanup();
                    ModelBoxMeshCache.InvalidateMesh(mesh);
                    Object.DestroyImmediate(go);
                    Object.DestroyImmediate(mesh);
                }
            });
        }

        private static Mesh CreateMesh()
        {
            return new Mesh
            {
                vertices = new[] { new Vector3(-1f, 0f, 0f), Vector3.up, Vector3.right },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up },
                tangents = new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f) },
                triangles = new[] { 0, 1, 2 }
            };
        }

        private static void RunInRepaint(System.Action action)
        {
            var window = ScriptableObject.CreateInstance<OverlayRenderTestWindow>();
            try
            {
                window.ShowUtility();
                window.DrawAction = action;
                window.SendEvent(new Event { type = EventType.Repaint });
                Assert.IsTrue(window.DidDraw, "The test must run inside a real GUI Repaint event.");
                if (window.Failure != null) throw window.Failure;
            }
            finally
            {
                window.Close();
            }
        }

        private static Color32[] RenderOverlay(MeshOverlayFlags flag, float width, Matrix4x4 incoming)
        {
            var savedTarget = RenderTexture.active;
            var savedCamera = Camera.current;
            var savedColor = Handles.color;
            var savedMatrix = Handles.matrix;
            var savedZTest = Handles.zTest;
            var cameraObject = new GameObject("OverlayCamera", typeof(Camera));
            var target = new RenderTexture(Resolution, Resolution, 24);
            var pixels = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false);
            var meshObject = new GameObject("OverlayMesh", typeof(MeshFilter), typeof(MeshRenderer));
            var mesh = CreateMesh();
            target.Create();
            GL.PushMatrix();
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 5f;
                camera.transform.position = new Vector3(0f, 0f, -10f);
                camera.targetTexture = target;
                meshObject.GetComponent<MeshFilter>().sharedMesh = mesh;
                meshObject.transform.SetPositionAndRotation(new Vector3(1f, 0.5f, 0f), Quaternion.Euler(0f, 0f, 25f));
                meshObject.transform.localScale = new Vector3(1.5f, 0.75f, 1f);
                var matrix = meshObject.transform.localToWorldMatrix;
                var bounds = meshObject.GetComponent<Renderer>().bounds;
                var data = ModelBoxMeshCache.GetOrBuild(mesh);

                RenderTexture.active = target;
                Handles.SetCamera(camera);
                GL.Clear(true, true, Color.black);
                Handles.matrix = incoming;
                Handles.zTest = CompareFunction.Always;

                switch (flag)
                {
                    case MeshOverlayFlags.Wireframe:
                        ModelBoxOverlayRenderer.DrawWireframe(data, matrix, Color.white, camera, bounds);
                        break;
                    case MeshOverlayFlags.Normals:
                        ModelBoxOverlayRenderer.DrawNormals(data, matrix, Color.white, 0.75f, width, camera, bounds);
                        break;
                    case MeshOverlayFlags.Tangents:
                        ModelBoxOverlayRenderer.DrawTangents(data, matrix, Color.white, 0.75f, width, camera, bounds);
                        break;
                    case MeshOverlayFlags.Bounds:
                        ModelBoxOverlayRenderer.DrawBounds(meshObject.GetComponent<Renderer>(), Color.white, width);
                        break;
                }

                pixels.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                pixels.Apply();
                return pixels.GetPixels32();
            }
            finally
            {
                GL.PopMatrix();
                Camera.SetupCurrent(savedCamera);
                RenderTexture.active = savedTarget;
                Handles.matrix = savedMatrix;
                Handles.color = savedColor;
                Handles.zTest = savedZTest;
                ModelBoxMeshCache.InvalidateMesh(mesh);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(meshObject);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(pixels);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }
    }

    public class OverlayRenderTestWindow : EditorWindow
    {
        public System.Action DrawAction;
        public System.Exception Failure;
        public bool DidDraw;

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || DrawAction == null) return;
            var action = DrawAction;
            DrawAction = null;
            try { action(); }
            catch (System.Exception exception) { Failure = exception; }
            finally { DidDraw = true; }
        }
    }
}
