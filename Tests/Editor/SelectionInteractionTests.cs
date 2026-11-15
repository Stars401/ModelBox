using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ModelBox.Tests
{
    [TestFixture]
    public class SelectionInteractionTests
    {
        private static readonly string[] SavedProperties =
        {
            "CustomTexture", "ChannelMask", "UVChannel", "TextureScale", "TextureOffset",
            "WorldSpaceUV", "WorldUVScale", "ClampMin", "ClampMax", "MonoDisplay",
            "CheckerGridSize", "CheckerColorA", "CheckerColorB", "VertexColorChannel",
            "BoneWeightMode", "BoneVertexWeights", "BoneWeightThreshold", "BoneWeightOpacity",
            "BoneWeightTargetSMR", "SelectedBoneIndex", "ShowBoneGizmos"
        };

        private readonly List<Object> _objects = new List<Object>();
        private readonly Dictionary<PropertyInfo, object> _saved = new Dictionary<PropertyInfo, object>();
        private ModelBoxSelectionManager _manager;
        private ModelBoxBonePanel _panel;
        private Object[] _selection;
        private SelectionDebugMode _mode;

        [SetUp]
        public void SetUp()
        {
            _manager = ModelBoxSelectionManager.Instance;
            _selection = Selection.objects;
            _mode = _manager.CurrentMode;
            foreach (string name in SavedProperties)
            {
                var property = typeof(ModelBoxSelectionManager).GetProperty(name);
                _saved.Add(property, property.GetValue(_manager));
            }
            _manager.SetMode(SelectionDebugMode.None);
        }

        [TearDown]
        public void TearDown()
        {
            _panel?.Cleanup();
            _manager.SetMode(SelectionDebugMode.None);
            Selection.objects = Array.Empty<Object>();
            foreach (var obj in _objects) Object.DestroyImmediate(obj);
            foreach (var pair in _saved) pair.Key.SetValue(_manager, pair.Value);
            Selection.objects = _selection;
            _manager.SetMode(_mode);
            _objects.Clear();
            _saved.Clear();
            _panel = null;
        }

        [Test]
        public void SelectionInspector_ReopenedTexturePanelPreservesActiveParameters()
        {
            Selection.activeGameObject = Track(new GameObject("TexturePanelSelection"));
            var texture = Track(new Texture2D(2, 2));
            _manager.CustomTexture = texture;
            _manager.ChannelMask = 3;
            _manager.UVChannel = 2;
            _manager.TextureScale = new Vector2(2f, 3f);
            _manager.TextureOffset = new Vector2(0.25f, 0.5f);
            _manager.WorldSpaceUV = true;
            _manager.WorldUVScale = 0.3f;
            _manager.ClampMin = 0.2f;
            _manager.ClampMax = 0.8f;
            _manager.MonoDisplay = true;
            _manager.SetMode(SelectionDebugMode.TextureChannel);
            var inspector = new ModelBoxSelectionInspector();

            Draw(inspector.Draw);

            Assert.AreSame(texture, _manager.CustomTexture);
            Assert.AreEqual(3, _manager.ChannelMask);
            Assert.AreEqual(2, _manager.UVChannel);
            Assert.AreEqual(new Vector2(2f, 3f), _manager.TextureScale);
            Assert.AreEqual(new Vector2(0.25f, 0.5f), _manager.TextureOffset);
            Assert.AreEqual(0.3f, _manager.WorldUVScale);
            Assert.AreEqual(0.2f, _manager.ClampMin);
            Assert.AreEqual(0.8f, _manager.ClampMax);
            Assert.IsTrue(_manager.MonoDisplay);
            Assert.AreEqual(3, GetField<int>(inspector, "_channelMask"));
        }

        [Test]
        public void SelectionInspector_ReopenedPanelDisplaysSharedCheckerAndVertexParameters()
        {
            Selection.activeGameObject = Track(new GameObject("SharedPanelSelection"));
            _manager.CheckerGridSize = 23f;
            _manager.CheckerColorA = Color.cyan;
            _manager.CheckerColorB = Color.magenta;
            _manager.VertexColorChannel = 4;
            var inspector = new ModelBoxSelectionInspector();
            SetField(inspector, "_selectedTab", 1);

            Draw(inspector.Draw);

            Assert.AreEqual(23f, GetField<float>(inspector, "_gridSize"));
            Assert.AreEqual(Color.cyan, GetField<Color>(inspector, "_colorA"));
            Assert.AreEqual(Color.magenta, GetField<Color>(inspector, "_colorB"));
            SetField(inspector, "_selectedTab", 3);
            Draw(inspector.Draw);
            Assert.AreEqual(4, GetField<int>(inspector, "_vcChannel"));
        }

        [Test]
        public void BonePanel_CleanupClearsVisualizationAndGizmos()
        {
            ActivateWeights(CreateRenderer("CleanupTarget"));

            _panel.Cleanup();

            AssertVisualizationCleared(true);
        }

        [Test]
        public void BonePanel_DeselectedBoneClearsPreviousWeights()
        {
            ActivateWeights(CreateRenderer("DeselectTarget"));

            typeof(ModelBoxBonePanel).GetMethod("OnBoneSelectedInScene",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_panel, new object[] { -1 });
            Draw(_panel.Draw);

            AssertVisualizationCleared(false);
            Assert.AreEqual(-1, _manager.SelectedBoneIndex);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void BonePanel_InvalidSelectionClearsPreviousVisualization(int selectionKind)
        {
            ActivateWeights(CreateRenderer("PreviousTarget"));
            if (selectionKind == 0) Selection.activeGameObject = null;
            else if (selectionKind == 1) Selection.activeGameObject = Track(new GameObject("NoRenderer"));
            else Selection.activeGameObject = Track(new GameObject("NoMesh", typeof(SkinnedMeshRenderer)));

            Draw(_panel.Draw);

            AssertVisualizationCleared(true);
        }

        [Test]
        public void BonePanel_SwitchRendererClearsPreviousWeights()
        {
            ActivateWeights(CreateRenderer("PreviousRenderer"));
            var next = CreateRenderer("NextRenderer");
            Selection.activeGameObject = next.gameObject;

            Draw(_panel.Draw);

            AssertVisualizationCleared(false);
            Assert.AreSame(next, _manager.BoneWeightTargetSMR);
            Assert.AreEqual(-1, _manager.SelectedBoneIndex);
        }

        [Test]
        public void BonePanel_ExternallyDisabledVisualizationStaysDisabled()
        {
            ActivateWeights(CreateRenderer("DisabledTarget"));
            _manager.BoneWeightMode = BoneWeightDisplayMode.Off;
            _manager.BoneVertexWeights = null;
            _manager.BoneWeightTargetSMR = null;
            _manager.SelectedBoneIndex = -1;
            _manager.ShowBoneGizmos = false;

            Draw(_panel.Draw);

            AssertVisualizationCleared(false);
        }

        [Test]
        public void BoneWeightSurface_AnimatedPoseUpdatesSurfaceVertices()
        {
            Draw(() =>
            {
                if (Event.current.type != EventType.Repaint) return;
                var renderer = CreateRenderer("AnimatedWeightSurface");
                var camera = Track(new GameObject("WeightSurfaceCamera", typeof(Camera))).GetComponent<Camera>();
                camera.enabled = false;
                var weights = new[] { 1f, 0.5f, 0.25f };
                try
                {
                    var data = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
                    ModelBoxOverlayRenderer.DrawBoneWeightSurface(data, renderer.localToWorldMatrix,
                        weights, camera, renderer.bounds);
                    var firstVertices = (Vector3[])data.Vertices.Clone();
                    renderer.bones[0].localPosition = Vector3.up;
                    data = ModelBoxOverlayRenderer.BakeSkinnedMesh(renderer);
                    ModelBoxOverlayRenderer.DrawBoneWeightSurface(data, renderer.localToWorldMatrix,
                        weights, camera, renderer.bounds);
                    var surface = (Mesh)typeof(ModelBoxOverlayRenderer).GetField("_boneWeightTempMesh",
                        BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

                    CollectionAssert.AreNotEqual(firstVertices, data.Vertices, "The source pose must actually move.");
                    CollectionAssert.AreEqual(data.Vertices, surface.vertices,
                        "The displayed weight surface must follow the latest baked pose.");
                }
                finally
                {
                    ModelBoxOverlayRenderer.Cleanup();
                }
            });
        }

        private void ActivateWeights(SkinnedMeshRenderer renderer)
        {
            _panel = new ModelBoxBonePanel();
            Selection.activeGameObject = renderer.gameObject;
            Draw(_panel.Draw);
            SetField(_panel, "_selectedBoneIndex", 0);
            SetField(_panel, "_displayMode", BoneWeightDisplayMode.ColorMap);
            _manager.SelectedBoneIndex = 0;
            _manager.BoneWeightMode = BoneWeightDisplayMode.ColorMap;
            Draw(_panel.Draw);
            Assert.AreEqual(BoneWeightDisplayMode.ColorMap, _manager.BoneWeightMode);
            Assert.IsNotNull(_manager.BoneVertexWeights);
        }

        private void AssertVisualizationCleared(bool clearTarget)
        {
            Assert.AreEqual(BoneWeightDisplayMode.Off, _manager.BoneWeightMode);
            Assert.IsNull(_manager.BoneVertexWeights);
            if (!clearTarget) return;
            Assert.IsNull(_manager.BoneWeightTargetSMR);
            Assert.IsFalse(_manager.ShowBoneGizmos);
            Assert.AreEqual(-1, _manager.SelectedBoneIndex);
        }

        private SkinnedMeshRenderer CreateRenderer(string name)
        {
            var go = Track(new GameObject(name, typeof(SkinnedMeshRenderer)));
            var bone = new GameObject("Bone").transform;
            bone.SetParent(go.transform, false);
            var mesh = Track(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 },
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 0, weight0 = 0.5f },
                    new BoneWeight { boneIndex0 = 0, weight0 = 0.25f }
                }
            });
            var renderer = go.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;
            return renderer;
        }

        private T Track<T>(T obj) where T : Object
        {
            _objects.Add(obj);
            return obj;
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        private static void Draw(Action action)
        {
            var window = ScriptableObject.CreateInstance<SelectionInteractionTestWindow>();
            try
            {
                window.position = new Rect(0f, 0f, 720f, 1200f);
                window.ShowUtility();
                window.DrawAction = action;
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                if (window.Failure != null) throw window.Failure;
                Assert.IsTrue(window.DidRepaint, "The panel must run inside real Layout and Repaint events.");
            }
            finally
            {
                window.DrawAction = null;
                window.Close();
            }
        }
    }

    public class SelectionInteractionTestWindow : EditorWindow
    {
        public Action DrawAction;
        public Exception Failure;
        public bool DidRepaint;

        private void OnGUI()
        {
            if (DrawAction == null || Failure != null) return;
            try
            {
                DrawAction();
                if (Event.current.type == EventType.Repaint) DidRepaint = true;
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
        }
    }
}
