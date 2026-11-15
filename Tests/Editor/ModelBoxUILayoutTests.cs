using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ModelBox.Tests
{
    [TestFixture]
    public class UILayoutTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [TestCase(true, 240f)]
        [TestCase(true, 400f)]
        [TestCase(false, 240f)]
        [TestCase(false, 400f)]
        public void NameRow_LongValueStaysAlignedAndWithinAvailableWidth(bool shaderHeader, float width)
        {
            var savedSelection = Selection.activeObject;
            var window = ScriptableObject.CreateInstance<ModelBoxLayoutTestWindow>();
            var go = new GameObject("UILayoutTest", typeof(MeshFilter));
            var mesh = new Mesh
            {
                name = new string('W', 120),
                vertices = new[] { Vector3.zero, Vector3.up, Vector3.right },
                triangles = new[] { 0, 1, 2 }
            };
            try
            {
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                Selection.activeGameObject = go;
                var shader = Shader.Find("Hidden/ModelBox/OverlayLine");
                Assert.IsNotNull(shader);
                var shaderPanel = new ShaderInfoPanel();
                var meshPanel = new MeshInfoPanel();
                typeof(ShaderInfoPanel).GetField("_cachedData", InstanceFields).SetValue(shaderPanel,
                    new ShaderInfoData { ShaderName = new string('W', 120) });
                var drawHeader = typeof(ShaderInfoPanel).GetMethod("DrawShaderHeader", InstanceFields);
                Assert.IsNotNull(drawHeader);

                window.ShowUtility();
                window.DrawAction = () =>
                {
                    GUILayout.BeginArea(new Rect(0f, 0f, width, 1000f));
                    try
                    {
                        if (shaderHeader) drawHeader.Invoke(shaderPanel, new object[] { shader, null });
                        else meshPanel.Draw();
                        if (Event.current.type == EventType.Repaint)
                        {
                            var cache = typeof(GUILayoutUtility).GetField("current",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
                            var group = cache.GetType().GetField("topLevel", InstanceFields).GetValue(cache);
                            var row = FindNameRow(group, shaderHeader ? 44f : 42f);
                            Assert.IsNotNull(row, "The real panel must contain the name row.");
                            AssertNameRow(row, width);
                        }
                    }
                    finally
                    {
                        GUILayout.EndArea();
                    }
                };
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                if (window.Failure != null) throw window.Failure;
                Assert.IsTrue(window.DidLayout, "A real Layout event is required.");
                Assert.IsTrue(window.DidRepaint, "A real Repaint event is required.");
            }
            finally
            {
                window.DrawAction = null;
                window.Close();
                Selection.activeObject = savedSelection;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mesh);
            }
        }

        private static object FindNameRow(object group, float labelWidth)
        {
            var entriesField = group.GetType().GetField("entries", InstanceFields);
            if (entriesField == null) return null;
            var entries = (IList)entriesField.GetValue(group);
            if (entries.Count == 2 && Mathf.Approximately((float)GetField(entries[0], "minWidth"), labelWidth)
                && Mathf.Approximately((float)GetField(entries[0], "maxWidth"), labelWidth))
                return group;
            foreach (var entry in entries)
            {
                var found = FindNameRow(entry, labelWidth);
                if (found != null) return found;
            }
            return null;
        }

        private static void AssertNameRow(object group, float width)
        {
            var entries = (IList)GetField(group, "entries");
            var label = (Rect)GetField(entries[0], "rect");
            var value = (Rect)GetField(entries[1], "rect");
            Assert.AreEqual(EditorGUIUtility.singleLineHeight, label.height, 0.01f, "label height");
            Assert.AreEqual(EditorGUIUtility.singleLineHeight, value.height, 0.01f, "value height");
            Assert.AreEqual(label.y, value.y, 0.01f, "The name and label must share a baseline row.");
            Assert.LessOrEqual(label.xMax, value.xMin + 0.01f, "The label must not overlap the name.");
            Assert.Greater(value.width, 0f, "The name must retain visible space.");
            Assert.LessOrEqual(value.xMax, width + 0.01f, "Long names must not push the row outside its panel.");
            var labelStyle = (GUIStyle)entries[0].GetType().GetProperty("style", InstanceFields).GetValue(entries[0]);
            var valueStyle = (GUIStyle)entries[1].GetType().GetProperty("style", InstanceFields).GetValue(entries[1]);
            Assert.AreEqual(labelStyle.fontSize, valueStyle.fontSize, "font size");
            Assert.AreEqual(labelStyle.fontStyle, valueStyle.fontStyle, "font style");
            Assert.AreEqual(labelStyle.alignment, valueStyle.alignment, "vertical alignment");
        }

        private static object GetField(object instance, string name)
        {
            return instance.GetType().GetField(name, InstanceFields).GetValue(instance);
        }
    }

    public class ModelBoxLayoutTestWindow : EditorWindow
    {
        public System.Action DrawAction;
        public System.Exception Failure;
        public bool DidLayout;
        public bool DidRepaint;

        private void OnGUI()
        {
            var type = Event.current.type;
            if (DrawAction == null || (type != EventType.Layout && type != EventType.Repaint)) return;
            try
            {
                DrawAction();
                if (type == EventType.Layout) DidLayout = true;
                else DidRepaint = true;
            }
            catch (System.Exception exception)
            {
                Failure = exception;
            }
        }
    }
}
