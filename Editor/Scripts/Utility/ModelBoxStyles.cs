using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 全局 GUIStyle / Color / UI 工具方法，统一工具视觉风格。
    /// 所有颜色、样式、通用 UI 组件在此集中管理。
    /// </summary>
    public static class ModelBoxStyles
    {
        // ==================== Colors ====================

        public static readonly Color ActiveButtonColor = new Color(0.35f, 0.70f, 0.40f, 1f);
        public static readonly Color ActiveButtonColorPro = new Color(0.25f, 0.58f, 0.30f, 1f);
        public static readonly Color WarningColor = new Color(0.90f, 0.65f, 0.25f, 1f);

        public static readonly Color SeparatorColor = new Color(0.5f, 0.5f, 0.5f, 0.3f);
        public static readonly Color DangerButtonColor = new Color(0.85f, 0.40f, 0.40f, 1f);
        public static readonly Color DebugIndicatorColor = new Color(0.85f, 0.45f, 0.15f, 0.8f);
        public static readonly Color SelButtonActiveColor = new Color(0.85f, 0.55f, 0.25f, 1f);
        public static readonly Color PixelBarBgColor = new Color(0.08f, 0.08f, 0.08f, 0.93f);

        // 工具栏分区背景色（极微妙的色调区分，不干扰场景内容）
        public static readonly Color ToolbarZoneMode = new Color(0.2f, 0.25f, 0.35f, 0.12f);   // 模式区：微蓝
        public static readonly Color ToolbarZoneTools = new Color(0.35f, 0.28f, 0.15f, 0.12f);  // 工具区：微橙
        public static readonly Color ToolbarZoneAction = new Color(0.15f, 0.3f, 0.15f, 0.12f);  // 操作区：微绿
        public static readonly Color ToolbarZoneGap = new Color(0.5f, 0.5f, 0.5f, 0.25f);      // 区域间隔线

        // Diff 面板颜色
        public static readonly Color DiffMatchColor = new Color(0.3f, 0.8f, 0.3f, 0.2f);
        public static readonly Color DiffMismatchColor = new Color(1f, 0.7f, 0.3f, 0.4f);

        // 状态栏颜色
        public static readonly Color StatusActiveColor = new Color(0.15f, 0.35f, 0.15f, 0.8f);
        public static readonly Color StatusInactiveColor = new Color(0.2f, 0.2f, 0.2f, 0.5f);
        public static readonly Color StatusActiveBorder = new Color(0.3f, 0.6f, 0.3f, 0.6f);
        public static readonly Color StatusInactiveBorder = new Color(0.4f, 0.4f, 0.4f, 0.3f);
        public static readonly Color StatusActiveText = new Color(0.5f, 1f, 0.5f);
        public static readonly Color StatusInactiveText = new Color(0.7f, 0.7f, 0.7f);

        // 导航分隔线颜色（比普通分隔线更醒目）
        public static readonly Color NavSeparatorColor = new Color(0.4f, 0.6f, 0.8f, 0.6f);

        // 标签页内容标题栏颜色
        public static readonly Color TabHeaderBgColor = EditorGUIUtility.isProSkin
            ? new Color(0.2f, 0.3f, 0.4f, 0.8f)
            : new Color(0.35f, 0.5f, 0.65f, 0.6f);
        public static readonly Color TabHeaderDescBgColor = EditorGUIUtility.isProSkin
            ? new Color(0.15f, 0.15f, 0.2f, 0.5f)
            : new Color(0.85f, 0.88f, 0.92f, 0.4f);

        // 分类卡片背景色（低饱和度，柔和不刺眼）
        public static Color CategoryOffBgColor => EditorGUIUtility.isProSkin
            ? new Color(0.4f, 0.4f, 0.4f, 0.85f)
            : new Color(0.72f, 0.72f, 0.72f, 0.85f);
        public static Color CategoryGeometryBgColor => EditorGUIUtility.isProSkin
            ? new Color(0.20f, 0.38f, 0.20f, 0.85f)
            : new Color(0.60f, 0.75f, 0.60f, 0.85f);
        public static Color CategoryDiagBgColor => EditorGUIUtility.isProSkin
            ? new Color(0.42f, 0.25f, 0.22f, 0.85f)
            : new Color(0.82f, 0.62f, 0.55f, 0.85f);
        public static Color CategoryAdvancedBgColor => EditorGUIUtility.isProSkin
            ? new Color(0.25f, 0.32f, 0.48f, 0.85f)
            : new Color(0.65f, 0.72f, 0.84f, 0.85f);
        public static Color CategoryPbrBgColor => EditorGUIUtility.isProSkin
            ? new Color(0.38f, 0.22f, 0.42f, 0.85f)
            : new Color(0.78f, 0.65f, 0.80f, 0.85f);
        public static Color CategoryLightingBgColor => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.35f, 0.38f, 0.85f)
            : new Color(0.60f, 0.78f, 0.80f, 0.85f);

        // 激活模式描述背景色
        public static readonly Color ActiveModeDescBgColor = EditorGUIUtility.isProSkin
            ? new Color(0.2f, 0.3f, 0.2f, 0.4f)
            : new Color(0.85f, 0.92f, 0.85f, 0.5f);

        // 中性回退按钮颜色（灰色系，表示安全回退而非危险操作）
        public static readonly Color NeutralButtonColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        public static Color GetActiveButtonColor()
        {
            return EditorGUIUtility.isProSkin ? ActiveButtonColorPro : ActiveButtonColor;
        }

        // ==================== Styles (lazy-init) ====================

        private static GUIStyle _headerStyle;
        public static GUIStyle HeaderStyle
        {
            get
            {
                if (_headerStyle == null)
                {
                    _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 16,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = EditorGUIUtility.isProSkin
                            ? new Color(0.9f, 0.92f, 0.95f)
                            : new Color(0.15f, 0.15f, 0.15f) }
                    };
                }
                return _headerStyle;
            }
        }

        private static GUIStyle _subtitleStyle;
        public static GUIStyle SubtitleStyle
        {
            get
            {
                if (_subtitleStyle == null)
                {
                    _subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = EditorGUIUtility.isProSkin
                            ? new Color(0.6f, 0.6f, 0.6f, 1f)
                            : new Color(0.35f, 0.35f, 0.35f, 1f) }
                    };
                }
                return _subtitleStyle;
            }
        }

        private static GUIStyle _tabHeaderTitleStyle;
        public static GUIStyle TabHeaderTitleStyle
        {
            get
            {
                if (_tabHeaderTitleStyle == null)
                {
                    _tabHeaderTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        alignment = TextAnchor.MiddleLeft,
                        normal = { textColor = EditorGUIUtility.isProSkin
                            ? new Color(0.95f, 0.97f, 1f)
                            : new Color(0.1f, 0.1f, 0.1f) }
                    };
                }
                return _tabHeaderTitleStyle;
            }
        }

        private static GUIStyle _categoryHeaderStyle;
        public static GUIStyle CategoryHeaderStyle
        {
            get
            {
                if (_categoryHeaderStyle == null)
                {
                    _categoryHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 11,
                        alignment = TextAnchor.MiddleLeft,
                        normal = { textColor = EditorGUIUtility.isProSkin
                            ? Color.white
                            : new Color(0.1f, 0.1f, 0.1f) }
                    };
                }
                return _categoryHeaderStyle;
            }
        }

        private static GUIStyle _modeButtonStyle;
        public static GUIStyle ModeButtonStyle
        {
            get
            {
                if (_modeButtonStyle == null)
                {
                    _modeButtonStyle = new GUIStyle(GUI.skin.button)
                    {
                        fixedHeight = 27,
                        margin = new RectOffset(2, 2, 1, 1),
                        alignment = TextAnchor.MiddleLeft,
                        padding = new RectOffset(8, 8, 2, 2),
                    };
                }
                return _modeButtonStyle;
            }
        }

        private static GUIStyle _tooltipStyle;
        public static GUIStyle PixelTooltipStyle
        {
            get
            {
                if (_tooltipStyle == null)
                {
                    _tooltipStyle = new GUIStyle(GUI.skin.box)
                    {
                        fontSize = 11,
                        padding = new RectOffset(8, 8, 4, 4),
                        alignment = TextAnchor.MiddleLeft,
                    };
                    _tooltipStyle.normal.textColor = Color.white;
                    _tooltipStyle.normal.background = GetTooltipBackground();
                }
                return _tooltipStyle;
            }
        }

        private static GUIStyle _statusActiveStyle;
        private static GUIStyle _statusInactiveStyle;

        public static GUIStyle GetActiveStatusStyle(bool isActive)
        {
            if (isActive)
            {
                if (_statusActiveStyle == null)
                {
                    _statusActiveStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        normal = { textColor = StatusActiveText }
                    };
                }
                return _statusActiveStyle;
            }
            else
            {
                if (_statusInactiveStyle == null)
                {
                    _statusInactiveStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        normal = { textColor = StatusInactiveText }
                    };
                }
                return _statusInactiveStyle;
            }
        }

        // ==================== Texture (S2 fix) ====================

        private static Texture2D _tooltipBg;
        private static Texture2D GetTooltipBackground()
        {
            if (_tooltipBg != null) return _tooltipBg;
            _tooltipBg = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _tooltipBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.8f));
            _tooltipBg.Apply();
            return _tooltipBg;
        }

        // ==================== Button Color Helpers (S1 fix: Stack) ====================

        private static readonly Stack<Color> _bgColorStack = new Stack<Color>();

        public static void BeginDangerButton()
        {
            _bgColorStack.Push(GUI.backgroundColor);
            GUI.backgroundColor = DangerButtonColor;
        }

        public static void EndDangerButton()
        {
            GUI.backgroundColor = _bgColorStack.Count > 0 ? _bgColorStack.Pop() : Color.white;
        }

        public static void BeginPrimaryButton()
        {
            _bgColorStack.Push(GUI.backgroundColor);
            GUI.backgroundColor = GetActiveButtonColor();
        }

        public static void EndPrimaryButton()
        {
            GUI.backgroundColor = _bgColorStack.Count > 0 ? _bgColorStack.Pop() : Color.white;
        }

        public static void BeginNeutralButton()
        {
            _bgColorStack.Push(GUI.backgroundColor);
            GUI.backgroundColor = NeutralButtonColor;
        }

        public static void EndNeutralButton()
        {
            GUI.backgroundColor = _bgColorStack.Count > 0 ? _bgColorStack.Pop() : Color.white;
        }

        // ==================== Property Type Colors (S3: centralized) ====================

        public static Color GetPropertyTypeColor(ShaderPropertyType type)
        {
            if (EditorGUIUtility.isProSkin)
            {
                switch (type)
                {
                    case ShaderPropertyType.Color: return new Color(0.15f, 0.25f, 0.15f, 0.5f);
                    case ShaderPropertyType.Texture: return new Color(0.25f, 0.15f, 0.15f, 0.5f);
                    case ShaderPropertyType.Vector: return new Color(0.15f, 0.15f, 0.25f, 0.5f);
                    case ShaderPropertyType.Range: return new Color(0.25f, 0.25f, 0.15f, 0.5f);
                    case ShaderPropertyType.Int: return new Color(0.2f, 0.2f, 0.2f, 0.5f);
                    default: return new Color(0, 0, 0, 0);
                }
            }
            else
            {
                switch (type)
                {
                    case ShaderPropertyType.Color: return new Color(0.7f, 0.95f, 0.7f, 0.5f);
                    case ShaderPropertyType.Texture: return new Color(0.95f, 0.8f, 0.7f, 0.5f);
                    case ShaderPropertyType.Vector: return new Color(0.7f, 0.8f, 1f, 0.5f);
                    case ShaderPropertyType.Range: return new Color(1f, 0.95f, 0.7f, 0.5f);
                    case ShaderPropertyType.Int: return new Color(0.85f, 0.85f, 0.85f, 0.5f);
                    default: return new Color(0, 0, 0, 0);
                }
            }
        }

        public static Color GetPropertyTypeLabelColor(ShaderPropertyType type)
        {
            if (EditorGUIUtility.isProSkin)
            {
                switch (type)
                {
                    case ShaderPropertyType.Color: return new Color(0.4f, 0.9f, 0.4f);
                    case ShaderPropertyType.Texture: return new Color(0.9f, 0.6f, 0.4f);
                    case ShaderPropertyType.Vector: return new Color(0.5f, 0.7f, 1f);
                    case ShaderPropertyType.Float: return new Color(0.8f, 0.8f, 0.5f);
                    case ShaderPropertyType.Range: return new Color(1f, 0.85f, 0.4f);
                    case ShaderPropertyType.Int: return new Color(0.7f, 0.7f, 0.7f);
                    default: return Color.white;
                }
            }
            else
            {
                switch (type)
                {
                    case ShaderPropertyType.Color: return new Color(0.1f, 0.5f, 0.1f);
                    case ShaderPropertyType.Texture: return new Color(0.6f, 0.3f, 0.1f);
                    case ShaderPropertyType.Vector: return new Color(0.2f, 0.3f, 0.7f);
                    case ShaderPropertyType.Float: return new Color(0.45f, 0.4f, 0.1f);
                    case ShaderPropertyType.Range: return new Color(0.6f, 0.45f, 0.1f);
                    case ShaderPropertyType.Int: return new Color(0.35f, 0.35f, 0.35f);
                    default: return new Color(0.2f, 0.2f, 0.2f);
                }
            }
        }

        // ==================== Common UI Components (S4, S5) ====================

        /// <summary>统一节标题样式：粗体标签 + 1px 分隔线 + 4px 间距。</summary>
        public static void DrawSectionHeader(string title)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, SeparatorColor);
            EditorGUILayout.Space(4);
        }

        /// <summary>统一搜索栏样式。返回是否清除。</summary>
        public static bool DrawSearchBar(ref string filter)
        {
            bool cleared = false;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("搜索", GUILayout.Width(32));
            filter = EditorGUILayout.TextField(filter ?? "");
            if (GUILayout.Button("×", GUILayout.Width(22)))
            {
                filter = "";
                cleared = true;
            }
            EditorGUILayout.EndHorizontal();
            return cleared;
        }
    }
}
