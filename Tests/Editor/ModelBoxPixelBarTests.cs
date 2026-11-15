using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace ModelBox.Tests
{
    public class PixelBarTests
    {
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject _object;
        private Camera _camera;
        private ModelBoxParameters _savedParameters;
        private Light _savedSun;

        [SetUp]
        public void SetUp()
        {
            _savedParameters = ModelBoxManager.Instance.CurrentParameters;
            _savedSun = RenderSettings.sun;
            _object = new GameObject("PixelBarTest");
            _camera = _object.AddComponent<Camera>();
            _camera.enabled = false;
            SetParameters(ModelBoxParameters.Default);
        }

        [TearDown]
        public void TearDown()
        {
            SetParameters(_savedParameters);
            RenderSettings.sun = _savedSun;
            ((IList)typeof(ModelBoxPixelBar).GetField("_texPixelCaches", StaticMembers).GetValue(null)).Clear();
            UnityEngine.Object.DestroyImmediate(_object);
        }

        [Test]
        public void WorldPosition_UsesShaderGammaAndOffset()
        {
            var parameters = ModelBoxParameters.Default;
            parameters.Gamma = 2f;
            parameters.Offset = 0.1f;
            SetParameters(parameters);
            AssertColor(Compute(DebugViewMode.WorldPosition, Vector3.zero, Vector3.up, 0),
                new Color(0.35f, 0.35f, 0.35f, 1f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Depth_UsesEyeDepthInsteadOfRayLength(bool orthographic)
        {
            _camera.orthographic = orthographic;
            var parameters = ModelBoxParameters.Default;
            parameters.DepthRange = 10f;
            SetParameters(parameters);
            AssertColor(Compute(DebugViewMode.Depth, new Vector3(6, 0, 8), Vector3.up, 10),
                new Color(0.8f, 0.8f, 0.8f, 1f));
        }

        [Test]
        public void WorldNormal_ColorMapMatchesShaderWithoutPositionAdjustments()
        {
            var parameters = ModelBoxParameters.Default;
            parameters.ColorMapMode = 1;
            parameters.Gamma = 3f;
            parameters.Offset = 0.4f;
            SetParameters(parameters);
            AssertColor(Compute(DebugViewMode.WorldNormal, Vector3.zero, Vector3.right, 0),
                new Color(0.6495f, 0.6495f, 0.6495f, 1));
        }

        [Test]
        public void NdotL_UsesDirectionTowardsSun()
        {
            var sun = _object.AddComponent<Light>();
            sun.type = LightType.Directional;
            RenderSettings.sun = sun;
            AssertColor(Compute(DebugViewMode.NdotL, Vector3.zero, Vector3.back, 0), Color.white);
        }

        [Test]
        public void FlatNormal_DoesNotReportSmoothNormalAsDerivativeResult()
        {
            Assert.Less(Compute(DebugViewMode.FlatNormal, Vector3.zero, Vector3.up, 0).r, 0f);
        }

        [Test]
        public void TextureWrap_RespectsEachAxis()
        {
            var texture = new Texture2D(2, 2);
            try
            {
                texture.wrapModeU = TextureWrapMode.Clamp;
                texture.wrapModeV = TextureWrapMode.Repeat;
                var wrapped = (Vector2)Invoke("ApplyWrapMode", new Vector2(1.25f, 1.25f), texture);
                Assert.AreEqual(1f, wrapped.x, 0.0001f);
                Assert.AreEqual(0.25f, wrapped.y, 0.0001f);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void TextureWrap_MirrorOnceClampsBeyondSingleMirror()
        {
            var texture = new Texture2D(2, 2) { wrapMode = TextureWrapMode.MirrorOnce };
            try
            {
                var wrapped = (Vector2)Invoke("ApplyWrapMode", new Vector2(2.25f, -2.25f), texture);
                Assert.AreEqual(Vector2.one, wrapped);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void TextureSampling_PointDoesNotBlendAdjacentTexels()
        {
            var texture = new Texture2D(2, 1) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            try
            {
                texture.SetPixels(new[] { Color.red, Color.blue });
                texture.Apply();
                Invoke("BuildTexPixelCache", texture);
                var cache = Invoke("GetTexPixelCache", texture);
                Assert.IsNotNull(cache);
                AssertColor((Color)Invoke("SampleTexPixelCache", cache, new Vector2(0.49f, 0.5f)), Color.red, 0.01f);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void TextureReadback_PreservesHdrAndNegativeValues()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            try
            {
                var expected = new Color(4f, 0.125f, -0.5f, 1f);
                texture.SetPixel(0, 0, expected);
                texture.Apply();
                Invoke("BuildTexPixelCache", texture);
                var cache = Invoke("GetTexPixelCache", texture);
                AssertColor((Color)Invoke("SampleTexPixelCache", cache, new Vector2(0.5f, 0.5f)), expected, 0.005f);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void TextureCache_ChangedContentCannotReturnOldPixels()
        {
            var texture = new Texture2D(1, 1);
            try
            {
                texture.SetPixel(0, 0, Color.red);
                texture.Apply();
                Invoke("BuildTexPixelCache", texture);
                Assert.IsNotNull(Invoke("GetTexPixelCache", texture));
                texture.SetPixel(0, 0, Color.blue);
                texture.Apply();
                Assert.IsNull(Invoke("GetTexPixelCache", texture), "Changed content must trigger a fresh readback.");
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        [Test]
        public void VertexColor_PreservesFloatAttributesAndBarycentricWeights()
        {
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 },
                colors = new[] { new Color(-0.5f, 2f, 0, 1), new Color(1, 0, 0, 1), Color.blue }
            };
            try
            {
                var contextType = typeof(ModelBoxPixelBar).GetNestedType("SelDebugHitContext", BindingFlags.NonPublic);
                var context = Activator.CreateInstance(contextType);
                contextType.GetField("mesh", InstanceMembers).SetValue(context, mesh);
                contextType.GetField("bary", InstanceMembers).SetValue(context, new Vector2(0.2f, 0.3f));
                Invoke("SampleVertexColor", null, context);
                AssertColor(ReadChannels(), new Color(-0.05f, 1f, 0.3f, 1f));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void GlobalVertexColor_AppliesColorMapAfterInterpolation()
        {
            var manager = ModelBoxManager.Instance;
            var selectionManager = ModelBoxSelectionManager.Instance;
            var savedMode = manager.CurrentMode;
            var savedSelectionMode = selectionManager.CurrentMode;
            var savedSelection = Selection.activeObject;
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), Vector3.up },
                triangles = new[] { 0, 1, 2 },
                colors = new[] { Color.red, Color.red, Color.red }
            };
            try
            {
                selectionManager.SetMode(SelectionDebugMode.None);
                _object.transform.position = new Vector3(1300, 1700, -20);
                var surface = new GameObject("VertexColorSurface", typeof(MeshFilter), typeof(MeshRenderer));
                surface.transform.SetParent(_object.transform, false);
                surface.transform.localPosition = Vector3.forward * 10;
                surface.GetComponent<MeshFilter>().sharedMesh = mesh;
                Selection.activeGameObject = _object;
                typeof(ModelBoxManager).GetProperty("CurrentMode").GetSetMethod(true)
                    .Invoke(manager, new object[] { DebugViewMode.VertexColor });
                var parameters = ModelBoxParameters.Default;
                parameters.ColorMapMode = 1;
                SetParameters(parameters);

                Invoke("SampleRay", new Ray(_camera.transform.position, _camera.transform.forward), _camera);

                AssertColor(ReadChannels(), new Color(0.299f, 0.299f, 0.299f, 1));
            }
            finally
            {
                typeof(ModelBoxManager).GetProperty("CurrentMode").GetSetMethod(true)
                    .Invoke(manager, new object[] { savedMode });
                Selection.activeObject = savedSelection;
                selectionManager.SetMode(savedSelectionMode);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SelectionDebug_DoesNotEraseGlobalValuesOnOtherRenderers()
        {
            var manager = ModelBoxSelectionManager.Instance;
            var savedSelection = Selection.activeObject;
            var savedMode = manager.CurrentMode;
            try
            {
                Selection.activeObject = null;
                manager.SetMode(SelectionDebugMode.VertexColor);
                var expected = Compute(DebugViewMode.WorldNormal, Vector3.zero, Vector3.up, 0);
                var contextType = typeof(ModelBoxPixelBar).GetNestedType("SelDebugHitContext", BindingFlags.NonPublic);
                bool handled = (bool)Invoke("TrySampleSelectionDebug", null, Vector3.zero,
                    Vector2.zero, Activator.CreateInstance(contextType));
                Assert.IsFalse(handled, "An unrelated renderer must retain global debug values.");
                AssertColor(ReadChannels(), expected);
            }
            finally
            {
                manager.SetMode(SelectionDebugMode.None);
                Selection.activeObject = savedSelection;
                manager.SetMode(savedMode);
            }
        }

        private Color Compute(DebugViewMode mode, Vector3 position, Vector3 normal, float distance)
        {
            var parameters = ModelBoxManager.Instance.CurrentParameters;
            Invoke("ComputeChannelValues", mode, position, normal, Vector2.zero, distance,
                _object.transform, parameters.Scale, parameters.DepthRange, _camera);
            return ReadChannels();
        }

        private static Color ReadChannels()
        {
            return new Color(Read("_chR"), Read("_chG"), Read("_chB"), Read("_chA"));
        }

        private static float Read(string name)
        {
            return (float)typeof(ModelBoxPixelBar).GetField(name, StaticMembers).GetValue(null);
        }

        private static void SetParameters(ModelBoxParameters parameters)
        {
            typeof(ModelBoxManager).GetProperty("CurrentParameters").GetSetMethod(true)
                .Invoke(ModelBoxManager.Instance, new object[] { parameters });
        }

        private static object Invoke(string name, params object[] arguments)
        {
            return typeof(ModelBoxPixelBar).GetMethod(name, StaticMembers).Invoke(null, arguments);
        }

        private static void AssertColor(Color actual, Color expected, float tolerance = 0.0001f)
        {
            Assert.AreEqual(expected.r, actual.r, tolerance, "red");
            Assert.AreEqual(expected.g, actual.g, tolerance, "green");
            Assert.AreEqual(expected.b, actual.b, tolerance, "blue");
            Assert.AreEqual(expected.a, actual.a, tolerance, "alpha");
        }
    }
}
