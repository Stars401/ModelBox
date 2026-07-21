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
        public float Depth;
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

        static PixelInspector()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            // [fix H2] 取消事件订阅
            SceneView.duringSceneGui -= OnSceneGUI;
            MeshRaycastUtility.Cleanup();

            if (_pendingTempRT != null)
            {
                RenderTexture.ReleaseTemporary(_pendingTempRT);
                _pendingTempRT = null;
            }
            _gpuReadbackPending = false;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            var manager = ModelBoxManager.Instance;
            if (manager == null || manager.CurrentMode == DebugViewMode.None)
            {
                _decodedInfo = "";
                return;
            }

            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowPixelInspector) return;

            Event e = Event.current;
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
                    var size = GUI.skin.label.CalcSize(content);
                    // 使用 cameraViewport 获取正确的 IMGUI 可用区域
                    // 注意：Handles.BeginGUI() 的坐标系对应 camera viewport（不含 toolbar），
                    // sceneView.position.height 包含 toolbar 高度，不能用于 GUI 坐标。
                    Rect vr = sceneView.cameraViewport;
                    var cam = sceneView.camera;
                    float vpW = vr.width > 0 ? vr.width : (cam != null ? cam.pixelWidth : vr.width);
                    float vpH = vr.height > 0 ? vr.height : (cam != null ? cam.pixelHeight : vr.height);
                    float maxX = vpW - size.x - 20;
                    float maxY = vpH - size.y - 20;
                    tooltipPos.x = Mathf.Clamp(tooltipPos.x, 0, maxX);
                    tooltipPos.y = Mathf.Clamp(tooltipPos.y, 0, maxY);

                    var tooltipRect = new Rect(tooltipPos.x, tooltipPos.y, size.x + 12, size.y + 6);
                    GUI.Box(tooltipRect, GUIContent.none, ModelBoxStyles.PixelTooltipStyle);
                    GUI.Label(tooltipRect, content, ModelBoxStyles.PixelTooltipStyle);
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

            // 方式1：AsyncGPUReadback（如果可用）
            if (SystemInfo.supportsAsyncGPUReadback && !_gpuReadbackPending)
            {
                TryAsyncReadback(camera, mousePos);
                return;
            }

            // 方式2：CPU Raycast 降级方案
            TryRaycastFallback(sceneView, mousePos);
        }

        private static void TryAsyncReadback(Camera camera, Vector2 mousePos)
        {
            var activeRT = camera.targetTexture;

            // targetTexture 为 null 时，走 Raycast 降级
            if (activeRT == null)
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv != null)
                    TryRaycastFallback(sv, mousePos);
                return;
            }

            // 计算像素坐标（SceneView 坐标系 Y 翻转）
            int px = Mathf.Clamp((int)mousePos.x, 0, activeRT.width - 1);
            int py = Mathf.Clamp((int)(activeRT.height - mousePos.y), 0, activeRT.height - 1);

            // LOGIC-1 修正：创建一个持久的临时 RT，不在请求后立即释放
            var desc = activeRT.descriptor;
            desc.width = 1;
            desc.height = 1;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            var tempRT = RenderTexture.GetTemporary(desc);
            tempRT.name = "_PixelInspectorTemp";

            var cmd = CommandBufferPool.Get("PixelInspector");
            cmd.CopyTexture(activeRT, 0, 0, px, py, 1, 1, tempRT, 0, 0, 0, 0);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // 保存 RT 引用，延迟到回调中释放
            _pendingTempRT = tempRT;
            AsyncGPUReadback.Request(tempRT, 0, TextureFormat.RGBA32, OnReadbackComplete);
            _gpuReadbackPending = true;
        }

        private static void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _gpuReadbackPending = false;

            // LOGIC-1：在回调中释放临时 RT
            if (_pendingTempRT != null)
            {
                RenderTexture.ReleaseTemporary(_pendingTempRT);
                _pendingTempRT = null;
            }

            if (request.hasError) return;

            var data = request.GetData<Color>();
            if (data.Length > 0)
            {
                _asyncPixelColor = data[0];

                var manager = ModelBoxManager.Instance;
                if (manager != null)
                {
                    _decodedInfo = DecodePixel(_asyncPixelColor, manager.CurrentMode);
                    LastPixelInfo = new PixelInfo
                    {
                        Color = _asyncPixelColor,
                        Depth = _asyncPixelColor.r,
                        DecodedString = _decodedInfo,
                        IsValid = true,
                    };
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

        private static string DecodePixel(Color color, DebugViewMode mode)
        {
            switch (mode)
            {
                case DebugViewMode.WorldNormal:
                case DebugViewMode.LocalNormal:
                    var normal = new Vector3(
                        color.r * 2f - 1f,
                        color.g * 2f - 1f,
                        color.b * 2f - 1f
                    );
                    return $"N: ({normal.x:F3}, {normal.y:F3}, {normal.z:F3})";

                case DebugViewMode.UV0:
                case DebugViewMode.UV1:
                    return $"UV: ({color.r:F3}, {color.g:F3})";

                case DebugViewMode.Depth:
                    return $"Depth: {color.r:F4} (linear)";

                case DebugViewMode.VertexColor:
                    return $"RGBA: ({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})";

                case DebugViewMode.WorldPosition:
                case DebugViewMode.LocalPosition:
                    return $"RGB: ({color.r:F3}, {color.g:F3}, {color.b:F3}) [frac-encoded]";

                default:
                    return $"RGB: ({color.r:F3}, {color.g:F3}, {color.b:F3})";
            }
        }
    }
}
