using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ModelBox.Tests
{
    [TestFixture]
    public class MaterialInteractionTests
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject _target;
        private Material _original;
        private Material _secondOriginal;
        private MaterialDiffPanel _panel;
        private Object _savedSelection;
        private SelectionDebugMode _savedMode;

        [SetUp]
        public void SetUp()
        {
            var manager = ModelBoxSelectionManager.Instance;
            _savedSelection = Selection.activeObject;
            _savedMode = manager.CurrentMode;
            manager.SetMode(SelectionDebugMode.None);
            _original = new Material(RequireShader("Hidden/ModelBox/OverlayLine")) { name = "OriginalSlot0" };
            _secondOriginal = new Material(_original) { name = "OriginalSlot1" };
            _target = new GameObject("MaterialInteractionTest", typeof(MeshRenderer));
            _target.GetComponent<Renderer>().sharedMaterials = new[] { _original, _secondOriginal };
            Selection.activeGameObject = _target;
            _panel = new MaterialDiffPanel();
            SetField(_panel, "_targetRenderer", _target.GetComponent<Renderer>());
        }

        [TearDown]
        public void TearDown()
        {
            _panel?.Cleanup();
            var manager = ModelBoxSelectionManager.Instance;
            manager.SetMode(SelectionDebugMode.None);
            if (_target != null) Undo.ClearUndo(_target.transform);
            if (_original != null) Undo.ClearUndo(_original);
            if (_secondOriginal != null) Undo.ClearUndo(_secondOriginal);
            Object.DestroyImmediate(_target);
            Object.DestroyImmediate(_original);
            Object.DestroyImmediate(_secondOriginal);
            Selection.activeObject = _savedSelection;
            manager.SetMode(_savedMode);
            Undo.IncrementCurrentGroup();
        }

        [TestCase(SelectionDebugMode.Checkerboard)]
        [TestCase(SelectionDebugMode.VertexColor)]
        public void StartSandbox_AfterSelectionDebug_UsesOriginalMaterialFromSelectedSlot(SelectionDebugMode mode)
        {
            var renderer = _target.GetComponent<Renderer>();
            ModelBoxSelectionManager.Instance.SetMode(mode);
            var debugMaterial = renderer.sharedMaterials[1];
            Assert.AreNotSame(_secondOriginal, debugMaterial, "The test must start with an active debug replacement.");

            Invoke(_panel, "StartSandbox", debugMaterial, 1);

            Assert.AreSame(_secondOriginal, GetField(_panel, "_originalMaterial"), "The stale debug material must not become the sandbox source.");
            var sandbox = (Material)GetField(_panel, "_sandboxMaterial");
            Assert.AreSame(_secondOriginal.shader, sandbox.shader);
            Assert.AreSame(_original, renderer.sharedMaterials[0], "A different original material must keep its own slot.");
            Assert.AreSame(sandbox, renderer.sharedMaterials[1]);
            _panel.Cleanup();
            Assert.AreSame(_secondOriginal, renderer.sharedMaterials[1]);
        }

        [TestCase(SelectionDebugMode.Checkerboard)]
        [TestCase(SelectionDebugMode.VertexColor)]
        public void RebuildCandidates_DuringSelectionDebug_PreservesOriginalMaterialSlots(SelectionDebugMode mode)
        {
            ModelBoxSelectionManager.Instance.SetMode(mode);

            Invoke(_panel, "RebuildCandidates", _target.transform);

            var candidates = (IList)GetField(_panel, "_candidates");
            Assert.AreEqual(2, candidates.Count, "Two different original materials must not collapse into one debug-material candidate.");
            Assert.AreSame(_original, GetField(candidates[0], "Material"));
            Assert.AreSame(_secondOriginal, GetField(candidates[1], "Material"));
        }

        [Test]
        public void DrawPropertyList_SearchDoesNotHideDifferencesFromApplyCount()
        {
            Invoke(_panel, "StartSandbox", _original, 0);
            var sandbox = (Material)GetField(_panel, "_sandboxMaterial");
            sandbox.SetFloat("_Length", _original.GetFloat("_Length") + 0.5f);
            Draw(() => Invoke(_panel, "DrawPropertyList"));
            Assert.AreEqual(1, GetField(_panel, "_diffCount"));
            int totalCount = (int)GetField(_panel, "_totalCount");

            SetField(_panel, "_searchFilter", "no_matching_property");
            Draw(() => Invoke(_panel, "DrawPropertyList"));

            Assert.AreEqual(1, GetField(_panel, "_diffCount"), "Search must not disable applying a real difference.");
            Assert.AreEqual(totalCount, GetField(_panel, "_totalCount"), "The total must describe the material, not the search results.");
            float expected = sandbox.GetFloat("_Length");
            Invoke(_panel, "ApplyToOriginal");
            Assert.AreEqual(expected, _original.GetFloat("_Length"));
        }

        [Test]
        public void DrawPropertyList_KeywordOnlyChangeEnablesApply()
        {
            _original.shader = RequireShader("Universal Render Pipeline/Lit");
            CollectionAssert.Contains(_original.shader.keywordSpace.keywordNames, "_NORMALMAP");
            _original.DisableKeyword("_NORMALMAP");
            Invoke(_panel, "StartSandbox", _original, 0);
            var sandbox = (Material)GetField(_panel, "_sandboxMaterial");
            sandbox.EnableKeyword("_NORMALMAP");

            Draw(() => Invoke(_panel, "DrawPropertyList"));

            Assert.AreEqual(1, GetField(_panel, "_diffCount"), "A keyword change is a material difference even when all properties are equal.");
            Invoke(_panel, "ApplyToOriginal");
            Assert.IsTrue(_original.IsKeywordEnabled("_NORMALMAP"));
        }

        [Test]
        public void Analyze_PassOpeningBraceOnNextLine_UsesRealPassName()
        {
            var data = ShaderInfoData.Analyze(_original.shader, _original);

            Assert.IsTrue(data.Passes.Exists(pass => pass.Name == "ModelBoxOverlayLine"), "The shader's real pass name must remain available for pass toggles.");
            Assert.IsFalse(data.Passes.Exists(pass => pass.Name.StartsWith("pass_")));
        }

        [Test]
        public void ApplyKeywordChange_SupportsUndoAndRedo()
        {
            const string keyword = "_MODELBOX_TEST_KEYWORD";
            _original.shader = RequireShader("Hidden/ModelBox/Tests/MaterialKeywordUndo");
            CollectionAssert.Contains(_original.shader.keywordSpace.keywordNames, keyword);
            _original.DisableKeyword(keyword);
            var data = ShaderInfoData.Analyze(_original.shader, _original);
            BeginIsolatedUndoGroup();

            data.ApplyKeywordChange(_original, keyword, true);
            Undo.FlushUndoRecordObjects();
            Assert.IsTrue(_original.IsKeywordEnabled(keyword));
            Undo.PerformUndo();

            Assert.IsFalse(_original.IsKeywordEnabled(keyword), "Undo must restore the material keyword, not only the panel's cached state.");
            Undo.PerformRedo();
            Assert.IsTrue(_original.IsKeywordEnabled(keyword));
        }

        [Test]
        public void ApplyPassChange_SupportsUndoAndRedo()
        {
            const string passName = "ModelBoxOverlayLine";
            _original.SetShaderPassEnabled(passName, true);
            var data = ShaderInfoData.Analyze(_original.shader, _original);
            BeginIsolatedUndoGroup();

            data.ApplyPassChange(_original, passName, false);
            Undo.FlushUndoRecordObjects();
            Assert.IsFalse(_original.GetShaderPassEnabled(passName));
            Undo.PerformUndo();

            Assert.IsTrue(_original.GetShaderPassEnabled(passName), "Undo must restore the material pass enable state.");
            Undo.PerformRedo();
            Assert.IsFalse(_original.GetShaderPassEnabled(passName));
        }

        [Test]
        public void ShaderInfoPanel_AfterMaterialUndo_RefreshesCachedKeywordState()
        {
            _original.shader = RequireShader("Universal Render Pipeline/Lit");
            CollectionAssert.Contains(_original.shader.keywordSpace.keywordNames, "_NORMALMAP");
            _original.DisableKeyword("_NORMALMAP");
            BeginIsolatedUndoGroup();
            Undo.RecordObject(_original, "Test keyword change");
            _original.EnableKeyword("_NORMALMAP");
            Undo.FlushUndoRecordObjects();
            var shaderPanel = new ShaderInfoPanel();
            Draw(shaderPanel.Draw);
            var before = (ShaderInfoData)GetField(shaderPanel, "_cachedData");
            Assert.IsTrue(before.Keywords.Exists(keyword => keyword.Name == "_NORMALMAP" && keyword.IsEnabled));

            Undo.PerformUndo();
            Assert.IsFalse(_original.IsKeywordEnabled("_NORMALMAP"), "The real material must be restored before checking the UI cache.");
            Draw(shaderPanel.Draw);

            var after = (ShaderInfoData)GetField(shaderPanel, "_cachedData");
            Assert.IsFalse(after.Keywords.Exists(keyword => keyword.Name == "_NORMALMAP" && keyword.IsEnabled), "Undo must be reflected without reselecting the material.");
        }

        [Test]
        public void TextureChannel_PerRendererAndSlotSourcesSurviveRefreshAndParameterUpdates()
        {
            var manager = ModelBoxSelectionManager.Instance;
            var savedTexture = manager.CustomTexture;
            var savedScale = manager.TextureScale;
            var savedOffset = manager.TextureOffset;
            int savedChannel = manager.ChannelMask;
            bool savedMono = manager.MonoDisplay;
            var child = new GameObject("SecondRenderer", typeof(MeshRenderer));
            child.transform.SetParent(_target.transform);
            var renderer = _target.GetComponent<Renderer>();
            var secondRenderer = child.GetComponent<Renderer>();
            _original.shader = RequireShader("Hidden/ModelBox/TextureChannel");
            _secondOriginal.shader = _original.shader;
            _original.SetTexture("_MainTex", Texture2D.whiteTexture);
            _secondOriginal.SetTexture("_MainTex", Texture2D.blackTexture);
            secondRenderer.sharedMaterial = _secondOriginal;
            try
            {
                manager.CustomTexture = null;
                manager.SetMode(SelectionDebugMode.TextureChannel);
                Assert.AreSame(Texture2D.whiteTexture, renderer.sharedMaterials[0].GetTexture("_MainTex"));
                Assert.AreSame(Texture2D.blackTexture, renderer.sharedMaterials[1].GetTexture("_MainTex"));
                Assert.AreSame(Texture2D.blackTexture, secondRenderer.sharedMaterial.GetTexture("_MainTex"));

                manager.RefreshDebugMaterial();
                Assert.AreSame(Texture2D.whiteTexture, renderer.sharedMaterials[0].GetTexture("_MainTex"));
                Assert.AreSame(Texture2D.blackTexture, renderer.sharedMaterials[1].GetTexture("_MainTex"));
                Assert.AreSame(Texture2D.blackTexture, secondRenderer.sharedMaterial.GetTexture("_MainTex"));
                manager.TextureScale = new Vector2(2f, 3f);
                manager.TextureOffset = new Vector2(0.25f, 0.5f);
                manager.ChannelMask = 4;
                manager.MonoDisplay = true;
                manager.UpdateTextureChannelProperties();
                foreach (var debugMaterial in new[] { renderer.sharedMaterials[0], renderer.sharedMaterials[1], secondRenderer.sharedMaterial })
                {
                    Assert.AreEqual(new Vector4(2f, 3f, 0f, 0f), debugMaterial.GetVector("_TexScale"));
                    Assert.AreEqual(new Vector4(0.25f, 0.5f, 0f, 0f), debugMaterial.GetVector("_TexOffset"));
                    Assert.AreEqual(new Vector4(0f, 0f, 0f, 1f), debugMaterial.GetVector("_ChannelMask"));
                    Assert.AreEqual(1, debugMaterial.GetInt("_DisplayMode"));
                }
                manager.SetMode(SelectionDebugMode.None);
                CollectionAssert.AreEqual(new[] { _original, _secondOriginal }, renderer.sharedMaterials);
                Assert.AreSame(_secondOriginal, secondRenderer.sharedMaterial);
            }
            finally
            {
                manager.SetMode(SelectionDebugMode.None);
                manager.CustomTexture = savedTexture;
                manager.TextureScale = savedScale;
                manager.TextureOffset = savedOffset;
                manager.ChannelMask = savedChannel;
                manager.MonoDisplay = savedMono;
            }
        }

        private void BeginIsolatedUndoGroup()
        {
            // Give a missing production Undo record a harmless test-owned operation to undo.
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_target.transform, "Material interaction undo sentinel");
            _target.transform.localPosition = Vector3.one;
            Undo.FlushUndoRecordObjects();
        }

        private static Shader RequireShader(string name)
        {
            var shader = Shader.Find(name);
            Assert.IsNotNull(shader, "Required shader: " + name);
            return shader;
        }

        private static void Draw(System.Action action)
        {
            var window = ScriptableObject.CreateInstance<ModelBoxLayoutTestWindow>();
            try
            {
                window.ShowUtility();
                window.DrawAction = () =>
                {
                    GUILayout.BeginArea(new Rect(0f, 0f, 600f, 4000f));
                    try { action(); }
                    finally { GUILayout.EndArea(); }
                };
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                if (window.Failure != null) throw window.Failure;
                Assert.IsTrue(window.DidLayout);
                Assert.IsTrue(window.DidRepaint);
            }
            finally
            {
                window.DrawAction = null;
                window.Close();
            }
        }

        private static object GetField(object instance, string name)
        {
            var field = instance.GetType().GetField(name, InstanceMembers);
            Assert.IsNotNull(field, name);
            return field.GetValue(instance);
        }

        private static void SetField(object instance, string name, object value)
        {
            var field = instance.GetType().GetField(name, InstanceMembers);
            Assert.IsNotNull(field, name);
            field.SetValue(instance, value);
        }

        private static void Invoke(object instance, string name, params object[] arguments)
        {
            var method = instance.GetType().GetMethod(name, InstanceMembers);
            Assert.IsNotNull(method, name);
            method.Invoke(instance, arguments);
        }
    }
}
