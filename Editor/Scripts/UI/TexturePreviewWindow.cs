using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 纹理预览独立窗口。显示纹理预览图、基本信息及 Importer 设置。
    /// 由 ShaderInfoPanel 的 Texture 属性行中的按钮触发打开。
    /// </summary>
    public class TexturePreviewWindow : EditorWindow
    {
        private Texture _texture;
        private string _propertyName;
        private Vector2 _scrollPos;

        /// <summary>
        /// 打开或聚焦纹理预览窗口。
        /// </summary>
        /// <param name="tex">要预览的纹理。</param>
        /// <param name="propertyName">Shader Property 名称，用于窗口标题。</param>
        public static void ShowWindow(Texture tex, string propertyName)
        {
            if (tex == null) return;

            // 如果已有同名窗口则聚焦，否则新建
            var existing = FindExistingWindow(tex);
            if (existing != null)
            {
                existing.Focus();
                return;
            }

            var window = CreateInstance<TexturePreviewWindow>();
            window._texture = tex;
            window._propertyName = propertyName;
            window.titleContent = new GUIContent(tex.name);
            window.minSize = new Vector2(320, 400);
            window.maxSize = new Vector2(600, 900);
            window.Show();
        }

        private static TexturePreviewWindow FindExistingWindow(Texture tex)
        {
            var windows = Resources.FindObjectsOfTypeAll<TexturePreviewWindow>();
            foreach (var w in windows)
            {
                if (w._texture == tex) return w;
            }
            return null;
        }

        private void OnGUI()
        {
            if (_texture == null)
            {
                EditorGUILayout.HelpBox("纹理引用已丢失。", MessageType.Warning);
                return;
            }

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawPreview();
            EditorGUILayout.Space(6);
            DrawBasicInfo();
            EditorGUILayout.Space(4);

            // Texture2D 额外信息
            if (_texture is Texture2D tex2D)
            {
                DrawTexture2DInfo(tex2D);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>绘制纹理预览图，居中显示并保持宽高比。</summary>
        private void DrawPreview()
        {
            var area = EditorGUILayout.GetControlRect(false, 220);
            area = EditorGUI.IndentedRect(area);

            float texAspect = (float)_texture.width / Mathf.Max(1, _texture.height);
            float areaAspect = area.width / Mathf.Max(1, area.height);

            Rect previewRect;
            if (texAspect > areaAspect)
            {
                // 纹理更宽，以宽度为准
                float h = area.width / texAspect;
                previewRect = new Rect(area.x, area.y + (area.height - h) * 0.5f, area.width, h);
            }
            else
            {
                // 纹理更高，以高度为准
                float w = area.height * texAspect;
                previewRect = new Rect(area.x + (area.width - w) * 0.5f, area.y, w, area.height);
            }

            EditorGUI.DrawPreviewTexture(previewRect, _texture);
        }

        /// <summary>绘制基本信息表格。</summary>
        private void DrawBasicInfo()
        {
            ModelBoxStyles.DrawSectionHeader("基本信息");

            // 分辨率
            DrawInfoRow("分辨率", $"{_texture.width} x {_texture.height}");

            // 格式
            string format = "N/A";
            try { format = _texture.graphicsFormat.ToString(); }
            catch { /* 部分纹理不支持 graphicsFormat */ }
            DrawInfoRow("格式", format);

            // Mip Map Count
            DrawInfoRow("Mip Map Count", _texture.mipmapCount.ToString());

            // sRGB / Linear
            string colorSpace = "N/A";
            var path = AssetDatabase.GetAssetPath(_texture);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        colorSpace = importer.sRGBTexture ? "sRGB" : "Linear";
                    }
                }
                catch { /* 运行时纹理无 Importer */ }
            }
            DrawInfoRow("颜色空间", colorSpace);

            // Filter Mode
            DrawInfoRow("Filter Mode", _texture.filterMode.ToString());

            // Wrap Mode
            DrawInfoRow("Wrap Mode", _texture.wrapMode.ToString());

            // Aniso Level
            DrawInfoRow("Aniso Level", _texture.anisoLevel.ToString());
        }

        /// <summary>绘制 Texture2D 专有信息。</summary>
        private void DrawTexture2DInfo(Texture2D tex2D)
        {
            EditorGUILayout.Space(4);
            ModelBoxStyles.DrawSectionHeader("Texture2D 详情");

            var path = AssetDatabase.GetAssetPath(tex2D);
            if (string.IsNullOrEmpty(path))
            {
                EditorGUILayout.HelpBox("运行时纹理无 Importer 信息。", MessageType.Info);
                return;
            }

            try
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) return;

                DrawInfoRow("可读 (Readable)", importer.isReadable ? "是" : "否");
                DrawInfoRow("压缩", importer.textureCompression.ToString());
                DrawInfoRow("Streaming Mipmaps", tex2D.streamingMipmaps ? "开启" : "关闭");

                if (tex2D.streamingMipmaps)
                {
                    DrawInfoRow("已加载 Mip Level", tex2D.loadedMipmapLevel.ToString());
                }
            }
            catch
            {
                EditorGUILayout.HelpBox("无法读取 TextureImporter 设置。", MessageType.Warning);
            }
        }

        /// <summary>绘制一行键值对信息，左 label 右 value，与 ModelBoxStyles 风格一致。</summary>
        private static void DrawInfoRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(130));
            EditorGUILayout.LabelField(value, EditorStyles.label);
            EditorGUILayout.EndHorizontal();
        }
    }
}
