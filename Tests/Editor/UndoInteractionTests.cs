using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace ModelBox.Tests
{
    [TestFixture]
    public class UndoInteractionTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo PendingUndoField = typeof(ModelBoxSettings).GetField(
            "_pendingModelBoxUndo", BindingFlags.Static | BindingFlags.NonPublic);

        private ModelBoxManager _manager;
        private ModelBoxSettings _settings;
        private ModelBoxSelectionManager _selection;
        private string _savedSettings;
        private DebugViewMode _savedMode;
        private ModelBoxParameters _savedParameters;
        private bool _savedEnabled;
        private MeshOverlayFlags _savedOverlayFlags;
        private MeshOverlayFlags _savedBeforeWireframe;
        private bool _savedHasOverlayFlags;
        private object _savedPendingUndo;

        [SetUp]
        public void SetUp()
        {
            _manager = ModelBoxManager.Instance;
            _settings = ModelBoxSettings.GetOrCreate();
            _selection = ModelBoxSelectionManager.Instance;
            Undo.FlushUndoRecordObjects();
            Undo.ClearUndo(_settings);

            _savedSettings = EditorJsonUtility.ToJson(_settings);
            _savedMode = _manager.CurrentMode;
            _savedParameters = _manager.CurrentParameters;
            _savedEnabled = _manager.IsEnabled;
            _savedOverlayFlags = _selection.OverlayFlags;
            _savedBeforeWireframe = GetManagerField<MeshOverlayFlags>("_overlayFlagsBeforeWireframe");
            _savedHasOverlayFlags = GetManagerField<bool>("_hasSavedOverlayFlags");
            _savedPendingUndo = PendingUndoField?.GetValue(null);

            SetBaseline(DebugViewMode.None, MeshOverlayFlags.None);
            PendingUndoField?.SetValue(null, 0);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.FlushUndoRecordObjects();
            Undo.ClearUndo(_settings);
            SetManagerProperty("CurrentMode", _savedMode);
            SetManagerProperty("CurrentParameters", _savedParameters);
            SetManagerProperty("IsEnabled", _savedEnabled);
            SetManagerField("_overlayFlagsBeforeWireframe", _savedBeforeWireframe);
            SetManagerField("_hasSavedOverlayFlags", _savedHasOverlayFlags);
            _selection.SetOverlayFlags(_savedOverlayFlags);
            EditorJsonUtility.FromJsonOverwrite(_savedSettings, _settings);
            PendingUndoField?.SetValue(null, _savedPendingUndo);
        }

        [Test]
        public void Parameters_Undo_RestoresSettingsManagerAndEvent()
        {
            SetBaseline(DebugViewMode.WorldPosition, MeshOverlayFlags.None);
            var initial = _manager.CurrentParameters;
            var changed = ChangedParameters();
            int eventCount = 0;
            ModelBoxParameters received = default;
            Action<ModelBoxParameters> handler = value => { eventCount++; received = value; };
            _manager.SetParameters(changed);
            FinishUndoGroup();
            _manager.OnParametersChanged += handler;
            try
            {
                Undo.PerformUndo();
                Assert.AreEqual(initial, _settings.LastParameters, "Unity must restore the recorded settings.");
                Assert.AreEqual(initial, _manager.CurrentParameters, "The active render parameters must follow Undo.");
                Assert.AreEqual(1, eventCount);
                Assert.AreEqual(initial, received);
            }
            finally
            {
                _manager.OnParametersChanged -= handler;
            }
        }

        [Test]
        public void Parameters_Redo_RestoresSettingsManagerAndEvent()
        {
            SetBaseline(DebugViewMode.WorldPosition, MeshOverlayFlags.None);
            var changed = ChangedParameters();
            _manager.SetParameters(changed);
            FinishUndoGroup();
            Undo.PerformUndo();

            int eventCount = 0;
            ModelBoxParameters received = default;
            Action<ModelBoxParameters> handler = value => { eventCount++; received = value; };
            _manager.OnParametersChanged += handler;
            try
            {
                Undo.PerformRedo();
                Assert.AreEqual(changed, _settings.LastParameters, "Unity must replay the recorded settings.");
                Assert.AreEqual(changed, _manager.CurrentParameters);
                Assert.AreEqual(1, eventCount, "Redo must notify parameter consumers after Undo.");
                Assert.AreEqual(changed, received);
            }
            finally
            {
                _manager.OnParametersChanged -= handler;
            }
        }

        [Test]
        public void Mode_UndoRedo_RestoresModeAndEnabledState()
        {
            _manager.SetDebugMode(DebugViewMode.WorldNormal);
            FinishUndoGroup();

            Undo.PerformUndo();
            Assert.AreEqual(DebugViewMode.None, _settings.LastMode);
            Assert.AreEqual(DebugViewMode.None, _manager.CurrentMode);
            Assert.IsFalse(_manager.IsEnabled);

            Undo.PerformRedo();
            Assert.AreEqual(DebugViewMode.WorldNormal, _settings.LastMode);
            Assert.AreEqual(DebugViewMode.WorldNormal, _manager.CurrentMode, "Redo must restore the manager after Undo.");
            Assert.IsTrue(_manager.IsEnabled);
        }

        [TestCase(DebugViewMode.WorldNormal, DebugViewMode.UV0)]
        [TestCase(DebugViewMode.UV0, DebugViewMode.WorldNormal)]
        [TestCase(DebugViewMode.WorldNormal, DebugViewMode.None)]
        public void NormalModeTransition_KeepsManualWireframe(DebugViewMode before, DebugViewMode after)
        {
            var manualFlags = MeshOverlayFlags.Wireframe | MeshOverlayFlags.Normals;
            SetBaseline(before, manualFlags);

            _manager.SetDebugMode(after);

            Assert.AreEqual(manualFlags, _selection.OverlayFlags);
            Assert.AreEqual(manualFlags, _settings.MeshOverlayState);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WireframeTransition_PreservesManualAndNewOverlayFlags(bool manualWireframe)
        {
            var initial = MeshOverlayFlags.Normals;
            if (manualWireframe) initial |= MeshOverlayFlags.Wireframe;
            SetBaseline(DebugViewMode.WorldNormal, initial);

            _manager.SetDebugMode(DebugViewMode.Wireframe);
            Assert.AreEqual(initial | MeshOverlayFlags.Wireframe, _selection.OverlayFlags);
            _selection.SetOverlayFlags(_selection.OverlayFlags | MeshOverlayFlags.Tangents);
            _manager.SetDebugMode(DebugViewMode.UV0);

            Assert.AreEqual(initial | MeshOverlayFlags.Tangents, _selection.OverlayFlags);
        }

        [Test]
        public void WireframeMode_UndoRedo_RestoresAutomaticOverlay()
        {
            SetBaseline(DebugViewMode.WorldNormal, MeshOverlayFlags.Normals);
            _manager.SetDebugMode(DebugViewMode.Wireframe);
            FinishUndoGroup();

            Undo.PerformUndo();
            Assert.AreEqual(DebugViewMode.WorldNormal, _manager.CurrentMode);
            Assert.AreEqual(MeshOverlayFlags.Normals, _selection.OverlayFlags,
                "Undo must remove the Wireframe bit added by entering the mode.");

            Undo.PerformRedo();
            Assert.AreEqual(DebugViewMode.Wireframe, _manager.CurrentMode);
            Assert.AreEqual(MeshOverlayFlags.Normals | MeshOverlayFlags.Wireframe, _selection.OverlayFlags);
        }

        private void SetBaseline(DebugViewMode mode, MeshOverlayFlags flags)
        {
            SetManagerProperty("CurrentMode", mode);
            SetManagerProperty("CurrentParameters", ModelBoxParameters.Default);
            SetManagerProperty("IsEnabled", mode != DebugViewMode.None);
            SetManagerField("_overlayFlagsBeforeWireframe", MeshOverlayFlags.None);
            SetManagerField("_hasSavedOverlayFlags", false);
            _selection.SetOverlayFlags(flags);
            _settings.LastMode = mode;
            _settings.LastParameters = ModelBoxParameters.Default;
            Undo.IncrementCurrentGroup();
        }

        private static ModelBoxParameters ChangedParameters()
        {
            var changed = ModelBoxParameters.Default;
            changed.Scale = 2.5f;
            changed.Offset = 0.25f;
            changed.Gamma = 1.75f;
            changed.DepthRange = 42f;
            return changed;
        }

        private static void FinishUndoGroup()
        {
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
        }

        private T GetManagerField<T>(string name)
        {
            return (T)typeof(ModelBoxManager).GetField(name, InstanceFields).GetValue(_manager);
        }

        private void SetManagerField(string name, object value)
        {
            typeof(ModelBoxManager).GetField(name, InstanceFields).SetValue(_manager, value);
        }

        private void SetManagerProperty(string name, object value)
        {
            typeof(ModelBoxManager).GetProperty(name).GetSetMethod(true).Invoke(_manager, new[] { value });
        }
    }
}
