using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 底部信息条。显示鼠标处调试值：R/G/B/A 通道 + Depth + 自定义属性。
    /// [fix] 根据调试模式直接计算 R/G/B/A（与 shader 代码一致）。
    /// [fix v0.4] 修复物体拾取错误：优先 Collider 同级 Renderer 查找、多 Renderer mesh raycast、UV 警告不覆盖自定义属性。
    /// 不使用 Camera.Render（避免性能问题和闪烁）。
    /// </summary>
    [InitializeOnLoad]
    public static class ModelBoxPixelBar
    {
        private const float BarHeight = 38f;

        // 采样数据
        private static float _chR, _chG, _chB, _chA, _depth;
        private static string _customPropValue = "";
        private static string _uvWarning = ""; // [fix v0.4] 独立 UV 警告字段，不覆盖 _customPropValue
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

            // [fix v0.4] 节流从 0.1s 降为 0.05s，减少快速移动时数据陈旧
            if ((debugActive || pixelBarStandalone) && e.type == EventType.Repaint
                && (float)EditorApplication.timeSinceStartup - _lastSampleTime > 0.05f)
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
                _uvWarning = ""; // [fix v0.4] 每次采样重置 UV 警告

                // [fix v0.4] Renderer 查找：优先 Collider 同级 → 父级 → 根的子级
                // 之前的 GetComponentInChildren 深度优先搜索在复杂层级中可能返回错误子级
                var hitRenderer = hit.collider.GetComponent<Renderer>();
                if (hitRenderer == null)
                    hitRenderer = hit.collider.GetComponentInParent<Renderer>();
                if (hitRenderer == null)
                    hitRenderer = hit.collider.transform.root.GetComponentInChildren<Renderer>();

                _shaderName = (hitRenderer != null && hitRenderer.sharedMaterial != null)
                    ? hitRenderer.sharedMaterial.shader.name : "";
                var mode = ModelBoxManager.Instance?.CurrentMode ?? DebugViewMode.None;
                _depth = hit.distance;

                var params2 = ModelBoxManager.Instance?.CurrentParameters ?? default;
                float scale = params2.Scale;
                float depthRange = params2.DepthRange;

                // [fix v0.4] 使用统一的 ComputeChannelValues 方法，修复 LocalPosition Transform 问题
                var rendererTransform = hitRenderer != null ? hitRenderer.transform : hit.collider.transform;
                ComputeChannelValues(mode, hit.point, hit.normal, hit.textureCoord,
                    hit.distance, rendererTransform, scale, depthRange, camera);

                ReadCustomProperty(hitRenderer);
            }
            else
            {
                // [fix v0.4] Physics.Raycast 未命中（物体可能无 Collider）：
                // 使用 Bounds-Ray 拾取 + CPU 射线-网格求交（不依赖 Collider，不触发递归渲染）
                // 改进：PickNearestRenderers 返回所有 Bounds 命中的 Renderer（按距离排序），
                // 然后逐个做 mesh raycast 取最近命中，避免只检查一个 Renderer 导致遗漏
                var mode = ModelBoxManager.Instance?.CurrentMode ?? DebugViewMode.None;
                var candidates = MeshRaycastUtility.PickNearestRenderers(ray, camera);

                if (candidates != null && candidates.Count > 0)
                {
                    float closestMeshDist = float.MaxValue;
                    Renderer bestRenderer = null;
                    MeshRaycastUtility.MeshRayHit bestHit = default;

                    foreach (var rend in candidates)
                    {
                        if (MeshRaycastUtility.Raycast(ray, rend, out var meshHit))
                        {
                            if (meshHit.distance < closestMeshDist)
                            {
                                closestMeshDist = meshHit.distance;
                                bestRenderer = rend;
                                bestHit = meshHit;
                            }
                        }
                    }

                    if (bestRenderer != null)
                    {
                        _hasData = true;
                        _hitName = bestRenderer.gameObject.name;
                        _worldPos = bestHit.point;
                        _uvWarning = "";
                        _shaderName = (bestRenderer.sharedMaterial != null)
                            ? bestRenderer.sharedMaterial.shader.name : "";
                        _depth = bestHit.distance;

                        var params3 = ModelBoxManager.Instance?.CurrentParameters ?? default;
                        float scale3 = params3.Scale;
                        float depthRange3 = params3.DepthRange;

                        ComputeChannelValues(mode, bestHit.point, bestHit.normal, bestHit.textureCoord,
                            bestHit.distance, bestRenderer.transform, scale3, depthRange3, camera);

                        ReadCustomProperty(bestRenderer);
                        return;
                    }
                }

                _hasData = false;
                _chR = _chG = _chB = _chA = _depth = 0;
                _hitName = "";
                _customPropValue = "";
                _uvWarning = "";
            }
        }

        /// <summary>
        /// [fix v0.4] 统一的通道值计算，消除 Physics.Raycast 和 MeshRaycast 两条路径的代码重复。
        /// 修复 LocalPosition 使用 Renderer transform 而非 Collider transform。
        /// 修复 DiagRawDepth/DiagObjectDepth 标记为 GPU-only 而非错误近似值。
        /// 为 NdotL/NdotV/Fresnel 等 PBR 模式添加 CPU 端计算。
        /// 对无法 CPU 计算的模式显示 N/A 而非误导性的 (0,0,0,1)。
        /// </summary>
        private static void ComputeChannelValues(DebugViewMode mode,
            Vector3 worldPos, Vector3 normal, Vector2 uv, float distance,
            Transform rendererTransform, float scale, float depthRange, Camera camera)
        {
            switch (mode)
            {
                case DebugViewMode.WorldPosition:
                    var wp = worldPos / Mathf.Max(scale, 0.001f) * 0.5f + Vector3.one * 0.5f;
                    _chR = Mathf.Clamp01(wp.x); _chG = Mathf.Clamp01(wp.y); _chB = Mathf.Clamp01(wp.z); _chA = 1;
                    break;
                case DebugViewMode.LocalPosition:
                    // [fix v0.4] 使用 Renderer 的 transform 而非 Collider 的 transform
                    var lp = rendererTransform.InverseTransformPoint(worldPos) * scale;
                    _chR = lp.x - Mathf.Floor(lp.x); _chG = lp.y - Mathf.Floor(lp.y); _chB = lp.z - Mathf.Floor(lp.z); _chA = 1;
                    break;
                case DebugViewMode.WorldNormal:
                case DebugViewMode.LocalNormal:
                case DebugViewMode.FlatNormal:
                    _chR = normal.x * 0.5f + 0.5f; _chG = normal.y * 0.5f + 0.5f; _chB = normal.z * 0.5f + 0.5f; _chA = 1;
                    break;
                case DebugViewMode.UV0:
                    _chR = uv.x; _chG = uv.y; _chB = 0; _chA = 1;
                    break;
                case DebugViewMode.Depth:
                    _chR = Mathf.Clamp01(distance / Mathf.Max(depthRange, 0.001f));
                    _chG = _chR; _chB = _chR; _chA = 1;
                    break;
                case DebugViewMode.DiagRawDepth:
                    // [fix v0.4] 标记为 GPU-only：CPU 端无法精确重现非线性深度缓冲值
                    _chR = -1; _chG = -1; _chB = -1; _chA = -1; // N/A 标记
                    break;
                case DebugViewMode.DiagObjectDepth:
                    // [fix v0.4] 标记为 GPU-only：NDC 深度需要顶点变换，CPU 端无法精确计算
                    _chR = -1; _chG = -1; _chB = -1; _chA = -1; // N/A 标记
                    break;
                case DebugViewMode.VertexColor:
                    // CPU Raycast 无法读取顶点颜色
                    _chR = -1; _chG = -1; _chB = -1; _chA = -1; // N/A 标记
                    break;
                case DebugViewMode.NdotL:
                {
                    // [fix v0.4] CPU 端计算 NdotL
                    var light = RenderSettings.sun;
                    if (light != null)
                    {
                        float ndotl = Mathf.Clamp01(Vector3.Dot(normal, light.transform.forward));
                        _chR = ndotl; _chG = ndotl; _chB = ndotl; _chA = 1;
                    }
                    else { _chR = -1; _chG = -1; _chB = -1; _chA = -1; }
                    break;
                }
                case DebugViewMode.NdotV:
                {
                    // [fix v0.4] CPU 端计算 NdotV
                    Vector3 viewDir = (camera.transform.position - worldPos).normalized;
                    float ndotv = Mathf.Clamp01(Vector3.Dot(normal, viewDir));
                    _chR = ndotv; _chG = ndotv; _chB = ndotv; _chA = 1;
                    break;
                }
                case DebugViewMode.Fresnel:
                {
                    // [fix v0.4] CPU 端计算 Schlick Fresnel
                    Vector3 viewDir = (camera.transform.position - worldPos).normalized;
                    float ndotv = Mathf.Clamp01(Vector3.Dot(normal, viewDir));
                    float fresnel = Mathf.Pow(1.0f - ndotv, 5.0f);
                    _chR = fresnel; _chG = fresnel; _chB = fresnel; _chA = 1;
                    break;
                }
                case DebugViewMode.None:
                    // 独立模式：显示命中法线方向（映射到 0-1）
                    _chR = normal.x * 0.5f + 0.5f; _chG = normal.y * 0.5f + 0.5f; _chB = normal.z * 0.5f + 0.5f;
                    _chA = 1;
                    break;
                default:
                    // [fix v0.4] 无法 CPU 计算的模式标记为 N/A 而非误导性的 (0,0,0,1)
                    _chR = -1; _chG = -1; _chB = -1; _chA = -1; // N/A 标记
                    break;
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
                    // [fix v0.4] N/A 标记（值为 -1 表示 GPU-only 模式，CPU 端无法计算）
                    if (_chR < 0)
                    {
                        rText = "R: N/A";
                        gText = "G: N/A";
                        bText = "B: N/A";
                        aText = "A: N/A";
                    }
                    else
                    {
                        rText = $"R:{_chR:F3}";
                        gText = $"G:{_chG:F3}";
                        bText = $"B:{_chB:F3}";
                        aText = $"A:{_chA:F3}";
                    }
                    dpText = $"Dp:{_depth:F3}";
                    propText = (ShowCustomProp && !string.IsNullOrEmpty(CustomPropName))
                        ? $"{CustomPropName}={_customPropValue}" : "";
                    // [fix v0.4] 显示 UV 警告（如有）
                    if (!string.IsNullOrEmpty(_uvWarning))
                        propText = string.IsNullOrEmpty(propText) ? _uvWarning : $"{propText} {_uvWarning}";
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
