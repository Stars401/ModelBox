using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 底部信息条。显示鼠标处调试值：R/G/B/A 通道 + Depth + 自定义属性。
    /// [fix] 根据调试模式直接计算 R/G/B/A（与 shader 编码一致），
    /// 不使用 Camera.Render（避免性能问题和闪烁）。
    /// </summary>
    [InitializeOnLoad]
    public static class ModelBoxPixelBar
    {
        private const float BarHeight = 38f;

        // 采样数据
        private static float _chR, _chG, _chB, _chA, _depth;
        private static string _customPropValue = "";
        private static string _hitName = "";
        private static string _shaderName = "";
        private static Vector3 _worldPos;
        private static bool _hasData;
        private static float _lastSampleTime;

        // 自定义属性（由 EditorWindow 设置）
        public static string CustomPropName { get; set; } = "";
        public static bool ShowCustomProp { get; set; }

        static ModelBoxPixelBar()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        private static void Cleanup()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            MeshRaycastUtility.Cleanup();
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowPixelBar) return;

            var manager = ModelBoxManager.Instance;
            bool debugActive = manager != null && manager.CurrentMode != DebugViewMode.None;
            bool pixelBarStandalone = !debugActive && settings != null && settings.PixelBarStandalone;

            Event e = Event.current;

            // 只在 Repaint 事件中采样
            if ((debugActive || pixelBarStandalone) && e.type == EventType.Repaint
                && (float)EditorApplication.timeSinceStartup - _lastSampleTime > 0.1f)
            {
                _lastSampleTime = (float)EditorApplication.timeSinceStartup;
                SampleAtMouse(sceneView, e.mousePosition);
            }

            DrawBar(sceneView, debugActive, pixelBarStandalone);
        }

        /// <summary>
        /// 采样鼠标位置的物体信息，根据调试模式计算 R/G/B/A。
        /// R/G/B/A 值与 shader 编码一致（与屏幕上显示的颜色对应）。
        /// </summary>
        private static void SampleAtMouse(SceneView sceneView, Vector2 mousePos)
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

            float maxDist = Mathf.Min(camera.farClipPlane, 5000f);
            if (Physics.Raycast(ray, out RaycastHit hit, maxDist))
            {
                _hasData = true;
                _hitName = hit.collider.gameObject.name;
                _worldPos = hit.point;

                // [fix] 三级回退查找 Renderer：collider 子级 → collider 父级 → 根节点子级
                // 覆盖 LODGroup、静态合批等复杂层级结构
                var hitRenderer = hit.collider.GetComponentInChildren<Renderer>();
                if (hitRenderer == null)
                    hitRenderer = hit.collider.GetComponentInParent<Renderer>();
                if (hitRenderer == null)
                    hitRenderer = hit.collider.transform.root.GetComponentInChildren<Renderer>();

                _shaderName = (hitRenderer != null && hitRenderer.sharedMaterial != null)
                    ? hitRenderer.sharedMaterial.shader.name : "";
                var mode = ModelBoxManager.Instance?.CurrentMode ?? DebugViewMode.None;
                _depth = hit.distance;

                // [fix] R/G/B/A 与 shader 编码一致（与屏幕上显示的颜色对应）
                var params2 = ModelBoxManager.Instance?.CurrentParameters ?? default;
                float scale = params2.Scale;
                float depthRange = params2.DepthRange;

                switch (mode)
                {
                    case DebugViewMode.WorldPosition:
                        // Shader: normalized = ws / max(scale, 0.001) * 0.5 + 0.5; saturate
                        var wp = hit.point / Mathf.Max(scale, 0.001f) * 0.5f + Vector3.one * 0.5f;
                        _chR = Mathf.Clamp01(wp.x); _chG = Mathf.Clamp01(wp.y); _chB = Mathf.Clamp01(wp.z); _chA = 1;
                        break;
                    case DebugViewMode.LocalPosition:
                        // Shader: frac(positionOS * scale)
                        var lp = hit.collider.transform.InverseTransformPoint(hit.point) * scale;
                        _chR = lp.x - Mathf.Floor(lp.x); _chG = lp.y - Mathf.Floor(lp.y); _chB = lp.z - Mathf.Floor(lp.z); _chA = 1;
                        break;
                    case DebugViewMode.WorldNormal:
                    case DebugViewMode.LocalNormal:
                        // Shader: normal * 0.5 + 0.5
                        _chR = hit.normal.x * 0.5f + 0.5f; _chG = hit.normal.y * 0.5f + 0.5f; _chB = hit.normal.z * 0.5f + 0.5f; _chA = 1;
                        break;
                    case DebugViewMode.UV0:
                        _chR = hit.textureCoord.x; _chG = hit.textureCoord.y; _chB = 0; _chA = 1;
                        if (hit.collider is not MeshCollider && _chR == 0 && _chG == 0)
                            _customPropValue = "[UV可能无效:非MeshCollider]";
                        break;
                    case DebugViewMode.Depth:
                        // Shader: saturate(linearDepth / depthRange)
                        _chR = Mathf.Clamp01(hit.distance / Mathf.Max(depthRange, 0.001f));
                        _chG = _chR; _chB = _chR; _chA = 1;
                        break;
                    case DebugViewMode.DiagRawDepth:
                    {
                        // Shader: 从 _CameraDepthTexture 采样非线性深度 → (d, d*0.5, 1-d)
                        // CPU 近似：Unity URP 默认 reversed-Z，rawDepth ≈ 1 - linearDist/far
                        float linearDist = Mathf.Min(hit.distance, camera.farClipPlane);
                        float rawDepth = 1.0f - linearDist / camera.farClipPlane;
                        _chR = rawDepth; _chG = rawDepth * 0.5f; _chB = 1.0f - rawDepth; _chA = 1;
                        break;
                    }
                    case DebugViewMode.DiagObjectDepth:
                    {
                        // Shader: 1.0 - positionCS.z/positionCS.w（NDC 深度，近=0 远=1，近亮远暗）
                        // CPU 近似：线性映射到 NDC 深度范围
                        float ndcDepth = Mathf.Clamp01(hit.distance / camera.farClipPlane);
                        _chR = ndcDepth; _chG = ndcDepth; _chB = ndcDepth; _chA = 1;
                        break;
                    }
                    case DebugViewMode.VertexColor:
                        // Shader: 直接输出 input.color，CPU Raycast 无法读取顶点颜色
                        _chR = 0; _chG = 0; _chB = 0; _chA = 0;
                        break;
                    case DebugViewMode.FlatNormal:
                        _chR = hit.normal.x * 0.5f + 0.5f;
                        _chG = hit.normal.y * 0.5f + 0.5f;
                        _chB = hit.normal.z * 0.5f + 0.5f;
                        _chA = 1;
                        break;
                    // [fix] 独立模式：显示命中法线方向（映射到 0-1）
                    case DebugViewMode.None:
                        _chR = hit.normal.x * 0.5f + 0.5f; _chG = hit.normal.y * 0.5f + 0.5f; _chB = hit.normal.z * 0.5f + 0.5f;
                        _chA = 1;
                        break;
                    default:
                        _chR = 0; _chG = 0; _chB = 0; _chA = 1;
                        break;
                }

                ReadCustomProperty(hitRenderer);
            }
            else
            {
                // [fix] Physics.Raycast 未命中（物体可能无 Collider）：
                // 使用 Bounds-Ray 拾取 + CPU 射线-网格求交（不依赖 Collider，不触发递归渲染）
                // 注意：不使用 HandleUtility.PickGameObject，它在 duringSceneGui Repaint 中会触发 GUI 递归
                var mode = ModelBoxManager.Instance?.CurrentMode ?? DebugViewMode.None;
                Renderer pickedRenderer = MeshRaycastUtility.PickNearestRenderer(ray, camera);

                if (pickedRenderer != null && MeshRaycastUtility.Raycast(ray, pickedRenderer, out var meshHit))
                {
                    _hasData = true;
                    _hitName = pickedRenderer.gameObject.name;
                    _worldPos = meshHit.point;
                    _shaderName = (pickedRenderer.sharedMaterial != null)
                        ? pickedRenderer.sharedMaterial.shader.name : "";
                    _depth = meshHit.distance;

                    // [fix] R/G/B/A 与 shader 编码一致
                    var params3 = ModelBoxManager.Instance?.CurrentParameters ?? default;
                    float scale3 = params3.Scale;
                    float depthRange3 = params3.DepthRange;

                    switch (mode)
                    {
                        case DebugViewMode.WorldPosition:
                            var wp3 = meshHit.point / Mathf.Max(scale3, 0.001f) * 0.5f + Vector3.one * 0.5f;
                            _chR = Mathf.Clamp01(wp3.x); _chG = Mathf.Clamp01(wp3.y); _chB = Mathf.Clamp01(wp3.z); _chA = 1;
                            break;
                        case DebugViewMode.LocalPosition:
                            var lp3 = pickedRenderer.transform.InverseTransformPoint(meshHit.point) * scale3;
                            _chR = lp3.x - Mathf.Floor(lp3.x); _chG = lp3.y - Mathf.Floor(lp3.y); _chB = lp3.z - Mathf.Floor(lp3.z); _chA = 1;
                            break;
                        case DebugViewMode.WorldNormal:
                        case DebugViewMode.LocalNormal:
                            _chR = meshHit.normal.x * 0.5f + 0.5f; _chG = meshHit.normal.y * 0.5f + 0.5f; _chB = meshHit.normal.z * 0.5f + 0.5f; _chA = 1;
                            break;
                        case DebugViewMode.UV0:
                            _chR = meshHit.textureCoord.x; _chG = meshHit.textureCoord.y; _chB = 0; _chA = 1;
                            break;
                        case DebugViewMode.Depth:
                            _chR = Mathf.Clamp01(meshHit.distance / Mathf.Max(depthRange3, 0.001f));
                            _chG = _chR; _chB = _chR; _chA = 1;
                            break;
                        case DebugViewMode.DiagRawDepth:
                        {
                            float linDist3 = Mathf.Min(meshHit.distance, camera.farClipPlane);
                            float rawD3 = 1.0f - linDist3 / camera.farClipPlane;
                            _chR = rawD3; _chG = rawD3 * 0.5f; _chB = 1.0f - rawD3; _chA = 1;
                            break;
                        }
                        case DebugViewMode.DiagObjectDepth:
                        {
                            float ndcD3 = Mathf.Clamp01(meshHit.distance / camera.farClipPlane);
                            _chR = ndcD3; _chG = ndcD3; _chB = ndcD3; _chA = 1;
                            break;
                        }
                        case DebugViewMode.VertexColor:
                            _chR = 0; _chG = 0; _chB = 0; _chA = 0;
                            break;
                        case DebugViewMode.FlatNormal:
                            _chR = meshHit.normal.x * 0.5f + 0.5f;
                            _chG = meshHit.normal.y * 0.5f + 0.5f;
                            _chB = meshHit.normal.z * 0.5f + 0.5f;
                            _chA = 1;
                            break;
                        case DebugViewMode.None:
                            _chR = meshHit.normal.x * 0.5f + 0.5f; _chG = meshHit.normal.y * 0.5f + 0.5f; _chB = meshHit.normal.z * 0.5f + 0.5f;
                            _chA = 1;
                            break;
                        default:
                            _chR = 0; _chG = 0; _chB = 0; _chA = 1;
                            break;
                    }

                    ReadCustomProperty(pickedRenderer);
                    return;
                }

                _hasData = false;
                _chR = _chG = _chB = _chA = _depth = 0;
                _hitName = "";
                _customPropValue = "";
            }
        }

        private static void ReadCustomProperty(Renderer hitRenderer)
        {
            if (!ShowCustomProp || string.IsNullOrEmpty(CustomPropName)) return;

            if (hitRenderer != null && hitRenderer.sharedMaterial != null)
            {
                var mat = hitRenderer.sharedMaterial;
                if (mat.HasProperty(CustomPropName))
                {
                    var shader = mat.shader;
                    int propIdx = shader.FindPropertyIndex(CustomPropName);
                    if (propIdx >= 0)
                    {
                        var propType = shader.GetPropertyType(propIdx);
                        try
                        {
                            switch (propType)
                            {
                                case UnityEngine.Rendering.ShaderPropertyType.Color:
                                    var c = mat.GetColor(CustomPropName);
                                    _customPropValue = $"C:[{c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2}]";
                                    break;
                                case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                    var v = mat.GetVector(CustomPropName);
                                    _customPropValue = $"V:[{v.x:F2},{v.y:F2},{v.z:F2},{v.w:F2}]";
                                    break;
                                case UnityEngine.Rendering.ShaderPropertyType.Float:
                                case UnityEngine.Rendering.ShaderPropertyType.Range:
                                    float f = mat.GetFloat(CustomPropName);
                                    _customPropValue = $"[{f:F3}]";
                                    break;
                                case UnityEngine.Rendering.ShaderPropertyType.Int:
                                    int i = mat.GetInt(CustomPropName);
                                    _customPropValue = $"[{i}]";
                                    break;
                                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                    var tex = mat.GetTexture(CustomPropName);
                                    _customPropValue = tex != null ? $"[Tex:{tex.name}]" : "[Tex:null]";
                                    break;
                                default:
                                    _customPropValue = $"[{propType}]";
                                    break;
                            }
                        }
                        catch (System.Exception e)
                        {
                            _customPropValue = $"[err:{e.GetType().Name}]";
                        }
                    }
                    else _customPropValue = "[N/A]";
                }
                else _customPropValue = "[N/A]";
            }
            else _customPropValue = "[无Renderer组件]";
        }

        private static void DrawBar(SceneView sceneView, bool debugActive, bool standalone)
        {
            Handles.BeginGUI();
            try
            {
                Rect vr = sceneView.cameraViewport;
                var cam = sceneView.camera;
                float viewportH = vr.height > 0 ? vr.height : (cam != null ? cam.pixelHeight : vr.height);
                float barW = vr.width > 0 ? vr.width : sceneView.position.width;

                string statusText, rText, gText, bText, aText, dpText, propText, nameText, shaderText, wposText;

                bool showSampling = debugActive || standalone;
                if (!showSampling)
                {
                    statusText = "modelBox: 开启调试模式或 PixelBar 独立模式后显示像素信息";
                    rText = gText = bText = aText = dpText = propText = nameText = shaderText = wposText = "";
                }
                else if (showSampling && !_hasData)
                {
                    statusText = "移动鼠标到物体上采样";
                    rText = gText = bText = aText = dpText = propText = nameText = shaderText = wposText = "";
                }
                else
                {
                    statusText = "";
                    rText = $"R:{_chR:F3}";
                    gText = $"G:{_chG:F3}";
                    bText = $"B:{_chB:F3}";
                    aText = $"A:{_chA:F3}";
                    dpText = $"Dp:{_depth:F3}";
                    propText = (ShowCustomProp && !string.IsNullOrEmpty(CustomPropName))
                        ? $"{CustomPropName}={_customPropValue}" : "";
                    nameText = _hitName;
                    shaderText = !string.IsNullOrEmpty(_shaderName) ? $"Shader:{_shaderName}" : "";
                    wposText = $"WPos:({_worldPos.x:F1},{_worldPos.y:F1},{_worldPos.z:F1})";
                }

                float areaY = viewportH - BarHeight - 2;
                Rect area = new Rect(0, areaY, barW, BarHeight);
                GUI.BeginGroup(area);
                EditorGUI.DrawRect(new Rect(0, 0, barW, BarHeight), ModelBoxStyles.PixelBarBgColor);

                if (!string.IsNullOrEmpty(statusText))
                {
                    GUI.Label(new Rect(8, 8, barW - 16, 20), statusText, EditorStyles.miniLabel);
                }
                else
                {
                    float x = 4f;
                    float labelH = 20f;
                    float y = (BarHeight - labelH) * 0.5f;
                    var miniStyle = EditorStyles.miniLabel;

                    x = ChannelRect(rText, x, y, 85, labelH, new Color(1f, 0.3f, 0.3f, 0.18f), miniStyle);
                    x = ChannelRect(gText, x, y, 85, labelH, new Color(0.3f, 1f, 0.3f, 0.18f), miniStyle);
                    x = ChannelRect(bText, x, y, 85, labelH, new Color(0.3f, 0.4f, 1f, 0.18f), miniStyle);
                    x = ChannelRect(aText, x, y, 75, labelH, new Color(0.6f, 0.6f, 0.6f, 0.18f), miniStyle);
                    x += 4;
                    x = ChannelRect(dpText, x, y, 75, labelH, new Color(0.5f, 0.5f, 0.5f, 0.18f), miniStyle);

                    if (!string.IsNullOrEmpty(propText))
                    {
                        x += 4;
                        GUI.Label(new Rect(x, y, 155, labelH), propText, miniStyle);
                        x += 155;
                    }

                    if (!string.IsNullOrEmpty(nameText))
                    {
                        string rightText = nameText;
                        if (!string.IsNullOrEmpty(shaderText))
                            rightText += $" | {shaderText}";
                        if (!string.IsNullOrEmpty(wposText))
                            rightText += $" | {wposText}";
                        var rightSize = miniStyle.CalcSize(new GUIContent(rightText));
                        GUI.Label(new Rect(barW - rightSize.x - 8, y, rightSize.x, labelH), rightText, miniStyle);
                    }
                }

                GUI.EndGroup();
            }
            finally
            {
                Handles.EndGUI();
            }
        }

        private static float ChannelRect(string text, float x, float y, float w, float h, Color bg, GUIStyle style)
        {
            var r = new Rect(x, y, w, h);
            EditorGUI.DrawRect(r, bg);
            GUI.Label(r, text, style);
            return x + w;
        }
    }
}
