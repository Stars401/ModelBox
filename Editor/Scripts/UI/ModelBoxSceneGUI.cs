using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    [InitializeOnLoad]
    internal static class ModelBoxSceneGUI
    {
        static ModelBoxSceneGUI()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        private static void Cleanup()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload -= Cleanup;
        }

        internal static void OnSceneGUI(SceneView sceneView)
        {
            if (sceneView == null || Event.current == null) return;

            ModelBoxPixelBar.HandleSceneInput(sceneView);
            // 各事件保持相同 GUI 顺序；骨骼拾取等 UI 消费事件之后再执行。
            var selection = ModelBoxSelectionManager.Instance;
            selection?.OnSceneGUI(sceneView);
            ModelBoxSceneOverlay.OnSceneGUI(sceneView);
            PixelInspector.OnSceneGUI(sceneView);
            ModelBoxPixelBar.OnSceneGUI(sceneView);
            selection?.HandleSceneInput(sceneView);
        }

        internal static void BlockSceneInput(Rect rect, int controlId)
        {
            var e = Event.current;
            if (e == null || !rect.Contains(e.mousePosition)) return;
            if (e.type == EventType.Layout)
                HandleUtility.AddDefaultControl(controlId);
            if (GUIUtility.hotControl != 0 && GUIUtility.hotControl != controlId) return;
            if (e.isMouse || e.type == EventType.ScrollWheel)
                e.Use();
        }
    }
}
