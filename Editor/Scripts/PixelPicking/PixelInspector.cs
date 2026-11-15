using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 像素拾取结果数据。
    /// </summary>
    public struct PixelInfo
    {
        public Color Color;
        public float Depth; // 无可验证的深度时为 NaN。
        public string DecodedString;
        public bool IsValid;
    }

    /// <summary>
    /// 像素拾取器。在 SceneView 鼠标悬停时显示调试值解码信息。
    /// GAP-2 修正：使用 AsyncGPUReadback 异步读取，延迟 1-2 帧。
    /// RUNTIME-1 / LOGIC-1 修正：临时 RT 在回调中释放，避免数据竞争。
    /// 降级方案：GPU Readback 不可用时使用 Raycast 从 CPU 获取近似值。
    /// </summary>
    [InitializeOnLoad]
    public static class PixelInspector
    {
        private static string _decodedInfo = "";
        private static float _lastCaptureTime;
        private static bool _gpuReadbackPending;

        /// <summary>
        /// 最近一次采样的像素信息（供 PixelBar 等外部模块读取）。
        /// </summary>
        public static PixelInfo LastPixelInfo { get; private set; }

        // LOGIC-1 修正：保存临时 RT 引用，在回调完成后释放
        private static RenderTexture _pendingTempRT;
        private static Color _asyncPixelColor;
        private static AsyncGPUReadbackRequest _pendingRequest;
        private static DebugViewMode _pendingMode;
        private static ModelBoxParameters _pendingParameters;
        private static GUIStyle _tooltipStyle;

        static PixelInspector()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            // [fix v0.4] 取消所有事件订阅（包括自身），防止域重载后 handler 叠加泄漏
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            MeshRaycastUtility.Cleanup();

            if (_gpuReadbackPending)
                _pendingRequest.WaitForCompletion();
            if (_pendingTempRT != null)
            {
                RenderTexture.ReleaseTemporary(_pendingTempRT);
                _pendingTempRT = null;
            }
            _gpuReadbackPending = false;
        }

        internal static void OnSceneGUI(SceneView sceneView)
        {
            var manager = ModelBoxManager.Instance;
            if (manager == null || manager.CurrentMode == DebugViewMode.None)
            {
                _decodedInfo = "";
                LastPixelInfo = default;
                return;
            }

            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowPixelInspector) return;

            Event e = Event.current;
            if (e == null) return;
            Vector2 mousePos = e.mousePosition;

            // 节流：每 150ms 采样一次
            if (e.type == EventType.Repaint && (float)EditorApplication.timeSinceStartup - _lastCaptureTime > 0.15f)
            {
                _lastCaptureTime = (float)EditorApplication.timeSinceStartup;
                TryCapturePixel(sceneView, mousePos);
            }

            // 绘制 Tooltip
            if (!string.IsNullOrEmpty(_decodedInfo))
            {
                Handles.BeginGUI();
                try
                {
                    Vector2 tooltipPos = mousePos + new Vector2(18, 18);

                    var content = new GUIContent(_decodedInfo);
                    // 使用 cameraViewport 获取正确的 IMGUI 可用区域
                    // 注意：Handles.BeginGUI() 的坐标系对应 camera viewport（不含 toolbar），
                    // sceneView.position.height 包含 toolbar 高度，不能用于 GUI 坐标。
                    Rect vr = sceneView.cameraViewport;
                    var cam = sceneView.camera;
                    float vpW = vr.width > 0 ? vr.width : (cam != null ? cam.pixelWidth : vr.width);
                    float vpH = vr.height > 0 ? vr.height : (cam != null ? cam.pixelHeight : vr.height);
                    if (_tooltipStyle == null)
                        _tooltipStyle = new GUIStyle(ModelBoxStyles.PixelTooltipStyle)
                        { wordWrap = true, clipping = TextClipping.Clip };
                    float width = Mathf.Min(_tooltipStyle.CalcSize(content).x, Mathf.Max(1f, vpW - 8f));
                    float height = Mathf.Min(_tooltipStyle.CalcHeight(content, width), Mathf.Max(1f, vpH - 8f));
                    tooltipPos.x = Mathf.Clamp(tooltipPos.x, 0, Mathf.Max(0, vpW - width));
                    tooltipPos.y = Mathf.Clamp(tooltipPos.y, 0, Mathf.Max(0, vpH - height));

                    var tooltipRect = new Rect(tooltipPos.x, tooltipPos.y, width, height);
                    GUI.Box(tooltipRect, GUIContent.none, _tooltipStyle);
                    GUI.Label(tooltipRect, content, _tooltipStyle);
                    int controlId = GUIUtility.GetControlID(732025, FocusType.Passive);
                    ModelBoxSceneGUI.BlockSceneInput(tooltipRect, controlId);
                }
                finally
                {
                    // [fix] 确保 EndGUI 始终被调用，防止渲染状态泄漏导致递归渲染错误
                    Handles.EndGUI();
                }
            }
        }

        private static void TryCapturePixel(SceneView sceneView, Vector2 mousePos)
        {
            var camera = sceneView.camera;
            if (camera == null) return;
            if (_gpuReadbackPending) return;

            // 方式1：AsyncGPUReadback（如果可用）
            if (SystemInfo.supportsAsyncGPUReadback && camera.targetTexture != null
                && camera.targetTexture.antiAliasing == 1)
            {
                TryAsyncReadback(sceneView, mousePos);
                return;
            }

            // 方式2：CPU Raycast 降级方案
            TryRaycastFallback(sceneView, mousePos);
            _decodedInfo = "CPU " + _decodedInfo;
            LastPixelInfo = default;
        }

        private static Vector2Int GetReadbackPixel(Vector2 mousePos, Vector2 viewportSize, Vector2Int targetSize)
        {
            int x = Mathf.FloorToInt(mousePos.x / Mathf.Max(1f, viewportSize.x) * targetSize.x);
            int y = targetSize.y - 1 - Mathf.FloorToInt(mousePos.y / Mathf.Max(1f, viewportSize.y) * targetSize.y);
            return new Vector2Int(Mathf.Clamp(x, 0, targetSize.x - 1), Mathf.Clamp(y, 0, targetSize.y - 1));
        }

        private static void TryAsyncReadback(SceneView sceneView, Vector2 mousePos)
        {
            var camera = sceneView.camera;
            var activeRT = camera.targetTexture;

            // Scene GUI 使用逻辑点，目标 RT 使用实际像素，同时兼容高 DPI 和渲染缩放。
            var viewportSize = sceneView.cameraViewport.size;
            if (viewportSize.x <= 0 || viewportSize.y <= 0)
                viewportSize = new Vector2(camera.pixelWidth, camera.pixelHeight) / EditorGUIUtility.pixelsPerPoint;
            var pixel = GetReadbackPixel(mousePos, viewportSize, new Vector2Int(activeRT.width, activeRT.height));

            // LOGIC-1 修正：创建一个持久的临时 RT，不在请求后立即释放
            var desc = activeRT.descriptor;
            desc.width = 1;
            desc.height = 1;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;

            var tempRT = RenderTexture.GetTemporary(desc);
            tempRT.name = "_PixelInspectorTemp";

            var cmd = CommandBufferPool.Get("PixelInspector");
            try
            {
                cmd.CopyTexture(activeRT, 0, 0, pixel.x, pixel.y, 1, 1, tempRT, 0, 0, 0, 0);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            catch
            {
                RenderTexture.ReleaseTemporary(tempRT);
                throw;
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }

            // 保存 RT 引用，延迟到回调中释放
            _pendingTempRT = tempRT;
            var manager = ModelBoxManager.Instance;
            _pendingMode = manager != null ? manager.CurrentMode : DebugViewMode.None;
            _pendingParameters = manager != null ? manager.CurrentParameters : ModelBoxParameters.Default;
            _gpuReadbackPending = true;
            try
            {
                _pendingRequest = AsyncGPUReadback.Request(tempRT, 0, TextureFormat.RGBA32, OnReadbackComplete);
            }
            catch
            {
                _gpuReadbackPending = false;
                RenderTexture.ReleaseTemporary(_pendingTempRT);
                _pendingTempRT = null;
                throw;
            }
        }

        private static void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            try
            {
                if (request.hasError)
                {
                    LastPixelInfo = default;
                    _decodedInfo = "Screen RGBA: unavailable";
                    return;
                }

                var data = request.GetData<Color32>();
                if (data.Length > 0)
                {
                    _asyncPixelColor = data[0];
                    _decodedInfo = DecodePixel(_asyncPixelColor, _pendingMode, _pendingParameters);
                    LastPixelInfo = new PixelInfo
                    {
                        Color = _asyncPixelColor,
                        Depth = float.NaN,
                        DecodedString = _decodedInfo,
                        IsValid = true,
                    };
                }
            }
            finally
            {
                _gpuReadbackPending = false;
                if (_pendingTempRT != null)
                {
                    RenderTexture.ReleaseTemporary(_pendingTempRT);
                    _pendingTempRT = null;
                }
            }
        }

        private static void TryRaycastFallback(SceneView sceneView, Vector2 mousePos)
        {
            var camera = sceneView.camera;
            if (camera == null) return;

            // 坐标转换：优先使用 HandleUtility（内置 API，正确处理 toolbar 偏移和平台差异），
            // 降级时手动计算 viewport 坐标（考虑 cameraViewport 的原点偏移）。
            Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);

            // 降级检测：HandleUtility 在某些 Unity 版本/duringSceneGui 上下文中可能产生退化射线
            Vector3 dir = ray.direction;
            if (dir.sqrMagnitude < 0.001f || float.IsNaN(dir.x + dir.y + dir.z))
            {
                Rect vr = sceneView.cameraViewport;
                bool hasViewport = vr.width > 0 && vr.height > 0;
                float vpW = hasViewport ? vr.width : camera.pixelWidth;
                float vpH = hasViewport ? vr.height : camera.pixelHeight;
                float relX = hasViewport ? mousePos.x - vr.x : mousePos.x;
                float relY = hasViewport ? mousePos.y - vr.y : mousePos.y;
                Vector2 viewportPos = new Vector2(
                    Mathf.Clamp01(relX / vpW),
                    Mathf.Clamp01(1f - relY / vpH)
                );
                ray = camera.ViewportPointToRay(viewportPos);
            }

            // Edit Mode 下物理 broadphase 可能未同步
            Physics.SyncTransforms();

            if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Min(camera.farClipPlane, 5000f)))
            {
                var manager = ModelBoxManager.Instance;
                if (manager == null) return;

                switch (manager.CurrentMode)
                {
                    case DebugViewMode.WorldPosition:
                        var wp = hit.point;
                        _decodedInfo = $"WP: ({wp.x:F3}, {wp.y:F3}, {wp.z:F3})";
                        break;
                    case DebugViewMode.WorldNormal:
                        var wn = hit.normal;
                        _decodedInfo = $"WN: ({wn.x:F3}, {wn.y:F3}, {wn.z:F3})";
                        break;
                    case DebugViewMode.UV0:
                        var uv = hit.textureCoord;
                        _decodedInfo = $"UV0: ({uv.x:F3}, {uv.y:F3})";
                        break;
                    case DebugViewMode.Depth:
                        float dist = hit.distance;
                        _decodedInfo = $"Dist: {dist:F3}m";
                        break;
                    default:
                        _decodedInfo = $"Hit: {hit.collider.gameObject.name}";
                        break;
                }
            }
            else
            {
                // [fix] Physics.Raycast 未命中（物体可能无 Collider）：
                // 使用 Bounds-Ray 拾取 + CPU 射线-网格求交（不触发 GUI 递归）
                Renderer pickedRenderer = MeshRaycastUtility.PickNearestRenderer(ray, camera);
                if (pickedRenderer != null && MeshRaycastUtility.Raycast(ray, pickedRenderer, out var meshHit))
                {
                    var mgr = ModelBoxManager.Instance;
                    if (mgr != null)
                    {
                        switch (mgr.CurrentMode)
                        {
                            case DebugViewMode.WorldPosition:
                                _decodedInfo = $"WP: ({meshHit.point.x:F3}, {meshHit.point.y:F3}, {meshHit.point.z:F3})";
                                break;
                            case DebugViewMode.WorldNormal:
                                _decodedInfo = $"WN: ({meshHit.normal.x:F3}, {meshHit.normal.y:F3}, {meshHit.normal.z:F3})";
                                break;
                            case DebugViewMode.UV0:
                                _decodedInfo = $"UV0: ({meshHit.textureCoord.x:F3}, {meshHit.textureCoord.y:F3})";
                                break;
                            case DebugViewMode.Depth:
                                _decodedInfo = $"Dist: {meshHit.distance:F3}m";
                                break;
                            default:
                                _decodedInfo = $"Hit: {pickedRenderer.gameObject.name}";
                                break;
                        }
                        return;
                    }
                }

                _decodedInfo = "No hit";
            }
        }

        private static string DecodePixel(Color color, DebugViewMode mode, ModelBoxParameters parameters)
        {
            // 相机目标已可能经过后处理与颜色映射，不能据此反解精确法线、UV 或深度。
            string colorMap = parameters.ColorMapMode != 0 ? ", ColorMap" : "";
            return $"Screen RGBA: ({color.r:F3}, {color.g:F3}, {color.b:F3}, {color.a:F3})\n[{mode}{colorMap}]";
        }
    }
}
