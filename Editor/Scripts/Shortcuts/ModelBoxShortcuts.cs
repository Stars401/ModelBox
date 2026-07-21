using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace ModelBox
{
    public static class ModelBoxShortcuts
    {
        // Alt+0: Toggle enable/disable
        [Shortcut("modelBox/Toggle On-Off", typeof(SceneView), KeyCode.Alpha0, ShortcutModifiers.Alt)]
        private static void ToggleDebug() => ModelBoxManager.Instance?.ToggleEnabled();

        // Alt+S: Toggle split-screen comparison
        [Shortcut("modelBox/Toggle Split Screen", typeof(SceneView), KeyCode.S, ShortcutModifiers.Alt)]
        private static void ToggleSplitScreen()
        {
            var manager = ModelBoxManager.Instance;
            if (manager == null) return;
            manager.SetSplitScreen(!manager.SplitScreenEnabled);
        }

        // Alt+1 through Alt+9: Primary modes
        [Shortcut("modelBox/Mode 1 - World Position", typeof(SceneView), KeyCode.Alpha1, ShortcutModifiers.Alt)]
        private static void SetMode1() => SetMode(DebugViewMode.WorldPosition);

        [Shortcut("modelBox/Mode 2 - Local Position", typeof(SceneView), KeyCode.Alpha2, ShortcutModifiers.Alt)]
        private static void SetMode2() => SetMode(DebugViewMode.LocalPosition);

        [Shortcut("modelBox/Mode 3 - World Normal", typeof(SceneView), KeyCode.Alpha3, ShortcutModifiers.Alt)]
        private static void SetMode3() => SetMode(DebugViewMode.WorldNormal);

        [Shortcut("modelBox/Mode 4 - Local Normal", typeof(SceneView), KeyCode.Alpha4, ShortcutModifiers.Alt)]
        private static void SetMode4() => SetMode(DebugViewMode.LocalNormal);

        [Shortcut("modelBox/Mode 5 - UV0", typeof(SceneView), KeyCode.Alpha5, ShortcutModifiers.Alt)]
        private static void SetMode5() => SetMode(DebugViewMode.UV0);

        [Shortcut("modelBox/Mode 6 - UV1", typeof(SceneView), KeyCode.Alpha6, ShortcutModifiers.Alt)]
        private static void SetMode6() => SetMode(DebugViewMode.UV1);

        [Shortcut("modelBox/Mode 7 - Depth", typeof(SceneView), KeyCode.Alpha7, ShortcutModifiers.Alt)]
        private static void SetMode7() => SetMode(DebugViewMode.Depth);

        [Shortcut("modelBox/Mode 8 - Vertex Color", typeof(SceneView), KeyCode.Alpha8, ShortcutModifiers.Alt)]
        private static void SetMode8() => SetMode(DebugViewMode.VertexColor);

        [Shortcut("modelBox/Mode 9 - Diag Screen UV", typeof(SceneView), KeyCode.Alpha9, ShortcutModifiers.Alt)]
        private static void SetMode9() => SetMode(DebugViewMode.DiagScreenUV);

        // Alt+Shift+1 through Alt+Shift+4: Diagnostic & advanced modes
        [Shortcut("modelBox/Mode 10 - Diag Raw Depth", typeof(SceneView), KeyCode.Alpha1, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode10() => SetMode(DebugViewMode.DiagRawDepth);

        [Shortcut("modelBox/Mode 11 - Diag Object Depth", typeof(SceneView), KeyCode.Alpha2, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode11() => SetMode(DebugViewMode.DiagObjectDepth);

        [Shortcut("modelBox/Mode 12 - Diag Pure Color", typeof(SceneView), KeyCode.Alpha3, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode12() => SetMode(DebugViewMode.DiagPureColor);

        [Shortcut("modelBox/Mode 13 - Wireframe", typeof(SceneView), KeyCode.Alpha4, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode13() => SetMode(DebugViewMode.Wireframe);

        [Shortcut("modelBox/Mode 14 - Opaque Texture", typeof(SceneView), KeyCode.Alpha5, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode14() => SetMode(DebugViewMode.OpaqueTexture);

        [Shortcut("modelBox/Mode 15 - Overdraw", typeof(SceneView), KeyCode.Alpha6, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode15() => SetMode(DebugViewMode.Overdraw);

        [Shortcut("modelBox/Mode 17 - Screen Normal", typeof(SceneView), KeyCode.Alpha7, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode17() => SetMode(DebugViewMode.ScreenNormal);

        [Shortcut("modelBox/Mode 18 - Shadow Map", typeof(SceneView), KeyCode.Alpha8, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode18() => SetMode(DebugViewMode.ShadowMap);

        // Alt+Shift+9: PBR diagnostics (NdotL)
        [Shortcut("modelBox/Mode 19 - NdotL", typeof(SceneView), KeyCode.Alpha9, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void SetMode19() => SetMode(DebugViewMode.NdotL);

        // Note: FlatNormal(16), NdotV(20), Fresnel(21), ObjectID(22) accessible via Alt+,/Alt+. cycle
        // Alt+Shift+N shortcut slots are now fully consumed (1-9)

        private static void SetMode(DebugViewMode mode)
        {
            ModelBoxManager.Instance?.SetDebugMode(mode);
        }

        // F4: Cycle mode shortcuts (Alt+, / Alt+.)
        private static readonly DebugViewMode[] CycleOrder =
        {
            DebugViewMode.WorldPosition, DebugViewMode.LocalPosition,
            DebugViewMode.WorldNormal, DebugViewMode.LocalNormal,
            DebugViewMode.UV0, DebugViewMode.UV1,
            DebugViewMode.Depth, DebugViewMode.VertexColor,
            DebugViewMode.DiagScreenUV, DebugViewMode.DiagRawDepth,
            DebugViewMode.DiagObjectDepth, DebugViewMode.DiagPureColor,
            DebugViewMode.Wireframe, DebugViewMode.OpaqueTexture,
            DebugViewMode.Overdraw, DebugViewMode.TransparencyLayers,
            DebugViewMode.ScreenNormal,
            DebugViewMode.ShadowMap,
            // PBR 诊断模式
            DebugViewMode.FlatNormal, DebugViewMode.NdotL,
            DebugViewMode.NdotV, DebugViewMode.Fresnel,
            DebugViewMode.ObjectID,
            // 光照 & 材质分离模式
            DebugViewMode.Tangent, DebugViewMode.Bitangent,
            DebugViewMode.DiffuseColor, DebugViewMode.SpecularHighlight,
            DebugViewMode.LightingOnly, DebugViewMode.Roughness,
            DebugViewMode.Metallic,
            // 导数诊断模式
            DebugViewMode.MipmapLevel, DebugViewMode.GeoDensity,
            DebugViewMode.SkyExposure,
            // 屏幕空间模式
            DebugViewMode.RayMarch,
        };

        [Shortcut("modelBox/Cycle Next Mode", typeof(SceneView), KeyCode.Period, ShortcutModifiers.Alt)]
        private static void CycleNext()
        {
            var manager = ModelBoxManager.Instance;
            if (manager == null) return;
            int idx = System.Array.IndexOf(CycleOrder, manager.CurrentMode);
            int next = (idx + 1) % CycleOrder.Length;
            manager.SetDebugMode(CycleOrder[next]);
        }

        [Shortcut("modelBox/Cycle Prev Mode", typeof(SceneView), KeyCode.Comma, ShortcutModifiers.Alt)]
        private static void CyclePrev()
        {
            var manager = ModelBoxManager.Instance;
            if (manager == null) return;
            int idx = System.Array.IndexOf(CycleOrder, manager.CurrentMode);
            int prev = idx <= 0 ? CycleOrder.Length - 1 : idx - 1;
            manager.SetDebugMode(CycleOrder[prev]);
        }
    }
}
