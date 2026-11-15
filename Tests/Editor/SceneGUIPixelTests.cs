using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ModelBox.Tests
{
    [TestFixture]
    public class SceneGUIPixelTests
    {
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static float PixelBarHeight => (float)typeof(ModelBoxPixelBar).GetField("BarHeight", StaticMembers)
            .GetRawConstantValue();
        private ModelBoxSettings _settings;
        private string _savedSettings;
        private DebugViewMode _savedMode;
        private ModelBoxParameters _savedParameters;
        private PixelInfo _savedPixel;
        private string _savedDecoded;

        [SetUp]
        public void SetUp()
        {
            AsyncGPUReadback.WaitAllRequests();
            _settings = ModelBoxSettings.GetOrCreate();
            _savedSettings = EditorJsonUtility.ToJson(_settings);
            _savedMode = ModelBoxManager.Instance.CurrentMode;
            _savedParameters = ModelBoxManager.Instance.CurrentParameters;
            _savedPixel = PixelInspector.LastPixelInfo;
            _savedDecoded = (string)typeof(PixelInspector).GetField("_decodedInfo", StaticMembers).GetValue(null);
            SetManagerState(DebugViewMode.None, ModelBoxParameters.Default);
            SetLastPixel(default);
            _settings.ShowSceneViewOverlay = true;
            _settings.ShowPerformanceHUD = false;
            _settings.ShowPixelInspector = false;
            _settings.ShowPixelBar = false;
            _settings.ToolbarX = 20f;
            _settings.ToolbarY = 20f;
        }

        [TearDown]
        public void TearDown()
        {
            AsyncGPUReadback.WaitAllRequests();
            SetManagerState(_savedMode, _savedParameters);
            SetLastPixel(_savedPixel);
            typeof(PixelInspector).GetField("_decodedInfo", StaticMembers).SetValue(null, _savedDecoded);
            EditorJsonUtility.FromJsonOverwrite(_savedSettings, _settings);
        }

        [Test]
        public void SceneGUI_HasOnePluginSubscriber()
        {
            foreach (var type in new[] { typeof(ModelBoxSelectionManager), typeof(ModelBoxSceneOverlay),
                typeof(PixelInspector), typeof(ModelBoxPixelBar) })
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);

            var field = typeof(SceneView).GetField("duringSceneGui", StaticMembers);
            Assert.IsNotNull(field, "The SceneView callback backing field is required for this integration check.");
            var subscribers = ((Delegate)field.GetValue(null)).GetInvocationList()
                .Where(callback => callback.Method.DeclaringType.Assembly == typeof(ModelBoxManager).Assembly).ToArray();

            Assert.AreEqual(1, subscribers.Length, "Plugin drawing must have one deterministic SceneView entry point.");
            Assert.AreEqual("ModelBoxSceneGUI", subscribers[0].Method.DeclaringType.Name);
        }

        [Test]
        public void Toolbar_BlankBackgroundConsumesMouseDown()
        {
            WithToolbar((window, sceneView) =>
            {
                EventType after = EventType.Ignore;
                window.DrawAction = () =>
                {
                    DrawToolbar(sceneView);
                    after = Event.current.type;
                };
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0,
                    mousePosition = new Vector2(100f, 73f) });
                Assert.AreEqual(EventType.Used, after, "Toolbar padding must not pass clicks to scene picking.");
            });
        }

        [Test]
        public void Toolbar_GripDragSurvivesLayoutRepaintAndReleasesOutside()
        {
            WithToolbar((window, sceneView) =>
            {
                bool released = false;
                window.DrawAction = () =>
                {
                    var type = Event.current.type;
                    DrawToolbar(sceneView);
                    if (type == EventType.MouseUp) released = GUIUtility.hotControl == 0;
                };
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0,
                    mousePosition = new Vector2(25f, 30f) });
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                window.SendEvent(new Event { type = EventType.MouseDrag, button = 0,
                    mousePosition = new Vector2(95f, 110f) });
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0,
                    mousePosition = new Vector2(95f, 110f) });
                Assert.AreEqual(90f, _settings.ToolbarX);
                Assert.AreEqual(100f, _settings.ToolbarY);
                Assert.IsTrue(released, "The toolbar must release mouse capture outside its original bounds.");
            });
        }

        [Test]
        public void Coordinator_PixelBarBlocksOverlappingToolbarGrip()
        {
            _settings.ShowPixelBar = true;
            WithToolbar((window, sceneView) =>
            {
                float barTop = sceneView.cameraViewport.height - PixelBarHeight - 2f;
                _settings.ToolbarY = barTop + 4f;
                AssertPixelBarOwnsMouseDown(window, sceneView, new Vector2(_settings.ToolbarX + 5f, barTop + 12f));
            });
        }

        [Test]
        public void Coordinator_PixelBarBlocksIntersectingSplitHandle()
        {
            var manager = ModelBoxManager.Instance;
            bool savedSplit = manager.SplitScreenEnabled;
            float savedPosition = manager.SplitScreenPosition;
            _settings.ShowPixelBar = true;
            SetManagerState(DebugViewMode.WorldNormal, ModelBoxParameters.Default);
            try
            {
                SetManagerProperty("SplitScreenEnabled", true);
                SetManagerProperty("SplitScreenPosition", 0.5f);
                WithToolbar((window, sceneView) =>
                {
                    var viewport = sceneView.cameraViewport;
                    Assert.Greater(viewport.height, 100f);
                    AssertPixelBarOwnsMouseDown(window, sceneView,
                        new Vector2(viewport.width * 0.5f, viewport.height - PixelBarHeight + 3f));
                });
            }
            finally
            {
                SetManagerProperty("SplitScreenEnabled", savedSplit);
                SetManagerProperty("SplitScreenPosition", savedPosition);
            }
        }

        [Test]
        public void Readback_RGBA32PublishesTheActualPixel()
        {
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Async GPU readback is unavailable.");
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                var expected = new Color32(64, 128, 192, 255);
                texture.SetPixel(0, 0, expected);
                texture.Apply();
                var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();
                Assert.IsFalse(request.hasError);
                InvokePixel("OnReadbackComplete", request);

                Assert.IsTrue(PixelInspector.LastPixelInfo.IsValid);
                Assert.AreEqual(expected.r / 255f, PixelInspector.LastPixelInfo.Color.r, 0.001f);
                Assert.AreEqual(expected.g / 255f, PixelInspector.LastPixelInfo.Color.g, 0.001f);
                Assert.AreEqual(expected.b / 255f, PixelInspector.LastPixelInfo.Color.b, 0.001f);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [TestCase(800, 400, 600, 299)]
        [TestCase(200, 100, 150, 74)]
        public void Readback_MapsGuiPointsToTargetPixels(int width, int height, int expectedX, int expectedY)
        {
            var method = typeof(PixelInspector).GetMethod("GetReadbackPixel", StaticMembers);
            Assert.IsNotNull(method, "Readback needs viewport-to-target scaling for high DPI and render scale.");
            var pixel = (Vector2Int)method.Invoke(null, new object[] { new Vector2(300f, 50f),
                new Vector2(400f, 200f), new Vector2Int(width, height) });
            Assert.AreEqual(new Vector2Int(expectedX, expectedY), pixel);
        }

        [Test]
        public void Decode_ColorMappedNormalIsReportedAsDisplayColor()
        {
            var parameters = ModelBoxParameters.Default;
            parameters.ColorMapMode = 2;
            string decoded = Decode(new Color(0.25f, 0.5f, 0.75f), DebugViewMode.WorldNormal, parameters);
            StringAssert.StartsWith("Screen RGBA:", decoded);
            StringAssert.Contains("ColorMap", decoded);
        }

        [Test]
        public void Decode_PostProcessedDepthIsReportedAsDisplayColor()
        {
            var parameters = ModelBoxParameters.Default;
            parameters.DepthRange = 40f;
            parameters.Gamma = 2f;
            string decoded = Decode(new Color(0.25f, 0.25f, 0.25f), DebugViewMode.Depth, parameters);
            StringAssert.StartsWith("Screen RGBA:", decoded);
            StringAssert.Contains("Depth", decoded);
            StringAssert.DoesNotContain("20.000", decoded);
        }

        [Test]
        public void Readback_KeepsRequestModeWhenManagerChangesBeforeCompletion()
        {
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("Async GPU readback is unavailable.");
            var sceneView = ScriptableObject.CreateInstance<SceneView>();
            var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32);
            var previousTarget = RenderTexture.active;
            try
            {
                ShowSceneView(sceneView);
                target.Create();
                RenderTexture.active = target;
                GL.Clear(false, true, new Color(0.25f, 0.5f, 0.75f, 1f));
                sceneView.camera.targetTexture = target;
                SetManagerState(DebugViewMode.WorldNormal, ModelBoxParameters.Default);
                InvokePixel("TryCapturePixel", sceneView, Vector2.zero);
                var changed = ModelBoxParameters.Default;
                changed.ColorMapMode = 2;
                SetManagerState(DebugViewMode.UV0, changed);
                AsyncGPUReadback.WaitAllRequests();

                Assert.IsTrue(PixelInspector.LastPixelInfo.IsValid);
                StringAssert.Contains("WorldNormal", PixelInspector.LastPixelInfo.DecodedString,
                    "Completion must identify the mode recorded when the request was created.");
                StringAssert.DoesNotContain("ColorMap", PixelInspector.LastPixelInfo.DecodedString,
                    "Completion must use the captured parameters rather than the new ColorMap setting.");
            }
            finally
            {
                AsyncGPUReadback.WaitAllRequests();
                RenderTexture.active = previousTarget;
                if (sceneView.camera != null) sceneView.camera.targetTexture = null;
                sceneView.Close();
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static void WithToolbar(Action<SelectionInteractionTestWindow, SceneView> action)
        {
            var window = ScriptableObject.CreateInstance<SelectionInteractionTestWindow>();
            var sceneView = ScriptableObject.CreateInstance<SceneView>();
            try
            {
                window.position = new Rect(0f, 0f, 720f, 400f);
                window.ShowUtility();
                ShowSceneView(sceneView);
                action(window, sceneView);
                if (window.Failure != null) throw window.Failure;
            }
            finally
            {
                window.Failure = null;
                window.DrawAction = () => GUIUtility.hotControl = 0;
                window.SendEvent(new Event { type = EventType.Layout });
                window.DrawAction = null;
                window.Close();
                sceneView.Close();
            }
        }

        private static void DrawToolbar(SceneView sceneView)
        {
            typeof(ModelBoxSceneOverlay).GetMethod("OnSceneGUI", StaticMembers).Invoke(null, new object[] { sceneView });
        }

        private static void AssertPixelBarOwnsMouseDown(SelectionInteractionTestWindow window, SceneView sceneView,
            Vector2 mousePosition)
        {
            var coordinator = typeof(ModelBoxManager).Assembly.GetType("ModelBox.ModelBoxSceneGUI");
            Assert.IsNotNull(coordinator);
            var draw = coordinator.GetMethod("OnSceneGUI", StaticMembers);
            int capturedControl = int.MinValue;
            var sampleView = typeof(ModelBoxPixelBar).GetField("_sampleSceneView", StaticMembers);
            var sampleTime = typeof(ModelBoxPixelBar).GetField("_lastSampleTime", StaticMembers);
            object savedView = sampleView.GetValue(null);
            object savedTime = sampleTime.GetValue(null);
            sampleView.SetValue(null, sceneView);
            sampleTime.SetValue(null, float.MaxValue);
            window.DrawAction = () =>
            {
                var type = Event.current.type;
                draw.Invoke(null, new object[] { sceneView });
                if (type == EventType.MouseDown) capturedControl = GUIUtility.hotControl;
            };
            try
            {
                window.SendEvent(new Event { type = EventType.Layout, mousePosition = mousePosition });
                window.SendEvent(new Event { type = EventType.Repaint, mousePosition = mousePosition });
                window.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = mousePosition });
                window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = mousePosition });
            }
            finally
            {
                sampleView.SetValue(null, savedView);
                sampleTime.SetValue(null, savedTime);
            }
            if (window.Failure != null) throw window.Failure;
            Assert.AreNotEqual(int.MinValue, capturedControl, "The coordinator must process the actual mouse event.");
            Assert.AreEqual(0, capturedControl, "Controls hidden behind PixelBar must not capture the mouse.");
        }

        private static void ShowSceneView(SceneView sceneView)
        {
            sceneView.position = new Rect(740f, 0f, 400f, 300f);
            sceneView.ShowUtility();
            sceneView.SendEvent(new Event { type = EventType.Layout });
            sceneView.SendEvent(new Event { type = EventType.Repaint });
            Assert.IsNotNull(sceneView.camera, "The SceneView must create its camera before exercising scene GUI.");
        }

        private static object InvokePixel(string name, params object[] arguments)
        {
            return typeof(PixelInspector).GetMethod(name, StaticMembers).Invoke(null, arguments);
        }

        private static string Decode(Color color, DebugViewMode mode, ModelBoxParameters parameters)
        {
            var method = typeof(PixelInspector).GetMethod("DecodePixel", StaticMembers);
            return (string)method.Invoke(null, method.GetParameters().Length == 3
                ? new object[] { color, mode, parameters } : new object[] { color, mode });
        }

        private static void SetManagerState(DebugViewMode mode, ModelBoxParameters parameters)
        {
            SetManagerProperty("CurrentMode", mode);
            SetManagerProperty("CurrentParameters", parameters);
        }

        private static void SetManagerProperty(string name, object value)
        {
            typeof(ModelBoxManager).GetProperty(name).GetSetMethod(true)
                .Invoke(ModelBoxManager.Instance, new[] { value });
        }

        private static void SetLastPixel(PixelInfo pixel)
        {
            typeof(PixelInspector).GetProperty("LastPixelInfo").GetSetMethod(true).Invoke(null, new object[] { pixel });
        }
    }
}
