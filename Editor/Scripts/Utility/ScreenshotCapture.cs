using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// SceneView 截图工具。捕获当前 SceneView（含 debug 可视化）为 PNG 文件。
    /// 使用 Camera.Render() + RenderTexture + ReadPixels 方案（Editor 兼容）。
    /// </summary>
    public static class ScreenshotCapture
    {
        /// <summary>
        /// 捕获当前活跃 SceneView 的截图。
        /// </summary>
        public static void CaptureSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                Debug.LogWarning("[ModelBox] No active SceneView found.");
                return;
            }

            var camera = sceneView.camera;
            if (camera == null)
            {
                Debug.LogWarning("[ModelBox] SceneView camera is null.");
                return;
            }

            // 获取视口尺寸
            var viewport = sceneView.cameraViewport;
            int width = Mathf.Max((int)viewport.width, 256);
            int height = Mathf.Max((int)viewport.height, 256);

            // 保存对话框
            var settings = ModelBoxSettings.GetOrCreate();
            string defaultDir = !string.IsNullOrEmpty(settings?.LastScreenshotDir)
                ? settings.LastScreenshotDir
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = EditorUtility.SaveFilePanel(
                "Save Screenshot",
                defaultDir,
                $"ModelBox_{timestamp}",
                "png");

            if (string.IsNullOrEmpty(path)) return;

            // 记住目录
            if (settings != null)
            {
                settings.LastScreenshotDir = Path.GetDirectoryName(path);
                settings.Save();
            }

            // 捕获
            RenderTexture rt = null;
            RenderTexture prevRT = null;
            Texture2D texture = null;

            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                prevRT = camera.targetTexture;

                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = prevRT;

                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                File.WriteAllBytes(path, texture.EncodeToPNG());
                Debug.Log($"[ModelBox] Screenshot saved: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ModelBox] Screenshot failed: {e.Message}");
            }
            finally
            {
                RenderTexture.active = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
