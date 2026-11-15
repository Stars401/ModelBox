using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 网格表面的 CPU 调试估算。纹理取 LOD0 原始通道，不代表合成后的屏幕像素。
    /// </summary>
    [InitializeOnLoad]
    public static class ModelBoxPixelBar
    {
        internal const float BarHeight = 48f;

        // 采样数据
        private static float _chR, _chG, _chB, _chA, _depth;
        private static string _customPropValue = "";
        private static string _uvWarning = ""; // [fix v0.4] 独立 UV 警告字段，不覆盖 _customPropValue
        private static string _selInfo = ""; // [fix v0.5] 选区调试采样摘要（Ch(A)=0.673→0.460 in）
        private static string _hitName = "";
        private static string _shaderName = "";
        private static Vector3 _worldPos;
        private static bool _hasData;
        private static bool _hasChannelValues;
        private static float _lastSampleTime;
        private static SceneView _sampleSceneView;

        // 自定义属性（由 EditorWindow 设置）
        public static string CustomPropName { get; set; } = "";
        public static bool ShowCustomProp { get; set; }

        static ModelBoxPixelBar()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        private static void Cleanup()
        {
            MeshRaycastUtility.Cleanup();
            _texPixelCaches.Clear();
        }

        internal static void OnSceneGUI(SceneView sceneView)
        {
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowPixelBar) return;

            var manager = ModelBoxManager.Instance;
            bool debugActive = (manager != null && manager.CurrentMode != DebugViewMode.None)
                || ModelBoxSelectionManager.Instance.CurrentMode != SelectionDebugMode.None;
            bool pixelBarStandalone = !debugActive && settings != null && settings.PixelBarStandalone;

            Event e = Event.current;

            // [fix v0.4] 节流从 0.1s 降为 0.05s，减少快速移动时数据陈旧
            if ((debugActive || pixelBarStandalone) && e.type == EventType.Repaint
                && (_sampleSceneView != sceneView || (float)EditorApplication.timeSinceStartup - _lastSampleTime > 0.05f))
            {
                _sampleSceneView = sceneView;
                _lastSampleTime = (float)EditorApplication.timeSinceStartup;
                SampleAtMouse(sceneView, e.mousePosition);
            }

            DrawBar(sceneView, debugActive, pixelBarStandalone);
        }

        /// <summary>
        /// 从当前 SceneView 的射线拾取实际网格，不以 Collider 外壳代替渲染表面。
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
                float vpW = hasViewport ? vr.width : camera.pixelWidth / EditorGUIUtility.pixelsPerPoint;
                float vpH = hasViewport ? vr.height : camera.pixelHeight / EditorGUIUtility.pixelsPerPoint;
                Vector2 viewportPos = new Vector2(
                    Mathf.Clamp01(mousePos.x / vpW),
                    Mathf.Clamp01(1f - mousePos.y / vpH)
                );
                ray = camera.ViewportPointToRay(viewportPos);
            }

            SampleRay(ray, camera);
        }

        private static void SampleRay(Ray ray, Camera camera)
        {
            Renderer meshHitRenderer = null;
            MeshRaycastUtility.MeshRayHit meshHit = default;
            float meshHitDist = float.MaxValue;

            var candidates = MeshRaycastUtility.PickNearestRenderers(ray, camera);
            if (candidates != null)
            {
                foreach (var rend in candidates)
                {
                    if (MeshRaycastUtility.Raycast(ray, rend, out var hit))
                    {
                        float eyeDepth = Vector3.Dot(hit.point - camera.transform.position, camera.transform.forward);
                        if (eyeDepth < camera.nearClipPlane || eyeDepth > camera.farClipPlane) continue;
                        if (SceneVisibilityManager.instance.IsHidden(rend.gameObject, false)) continue;
                        if (hit.distance < meshHitDist)
                        {
                            meshHitDist = hit.distance;
                            meshHitRenderer = rend;
                            meshHit = hit;
                        }
                    }
                }
            }

            if (meshHitRenderer != null)
            {
                _hasData = true;
                _hitName = meshHitRenderer.gameObject.name;
                _worldPos = meshHit.point;
                _uvWarning = "";
                _selInfo = "";
                // [fix v0.5] 被选区调试覆盖时读原始材质 shader 名
                _shaderName = GetShaderNameForDisplay(meshHitRenderer, meshHit.subMesh);
                _depth = Vector3.Dot(meshHit.point - camera.transform.position, camera.transform.forward);
                var manager = ModelBoxManager.Instance;
                var mode = manager.CurrentMode;
                var selected = Selection.activeTransform;
                if (manager.DebugOnlySelected && (selected == null
                    || !meshHitRenderer.transform.IsChildOf(selected))) mode = DebugViewMode.None;
                var sandbox = MaterialDiffPanel.SandboxPreviewRenderers;
                if (sandbox != null && sandbox.Contains(meshHitRenderer)) mode = DebugViewMode.None;

                var parameters = manager.CurrentParameters;
                var selCtx = new SelDebugHitContext
                {
                    mesh = GetMeshFromRenderer(meshHitRenderer), subMesh = meshHit.subMesh,
                    triIdx = meshHit.triangleIndex, bary = meshHit.barycentric,
                };
                _hasChannelValues = true;
                if (!TrySampleSelectionDebug(meshHitRenderer, meshHit.point, meshHit.textureCoord, selCtx))
                {
                    var normal = mode == DebugViewMode.LocalNormal ? meshHit.localNormal : meshHit.normal;
                    ComputeChannelValues(mode, meshHit.point, normal, meshHit.textureCoord,
                        meshHit.distance, meshHitRenderer.transform, parameters.Scale, parameters.DepthRange, camera);
                    if (mode == DebugViewMode.UV1)
                    {
                        var uv1 = InterpolateUV(selCtx.mesh, selCtx.subMesh, selCtx.triIdx, selCtx.bary, 1);
                        SetChannels(ApplyColorMap(new Color(uv1.x, uv1.y, 0, 1), parameters.ColorMapMode));
                    }
                    else if (mode == DebugViewMode.VertexColor)
                    {
                        SampleVertexColor(meshHitRenderer, selCtx);
                        if (_hasChannelValues)
                            SetChannels(ApplyColorMap(new Color(_chR, _chG, _chB, _chA), parameters.ColorMapMode));
                    }
                }
                if (manager.SplitScreenEnabled) _selInfo += " [实时表面，非合成像素]";
                ReadCustomProperty(meshHitRenderer, meshHit.subMesh);
            }
            else
            {
                _hasData = false;
                _hasChannelValues = false;
                _chR = _chG = _chB = _chA = _depth = 0;
                _hitName = "";
                _customPropValue = "";
                _uvWarning = "";
                _selInfo = "";
            }
        }

        /// <summary>
        /// 按对应 Shader 的处理顺序编码。normal 已由调用方选择世界或局部空间。
        /// </summary>
        private static void ComputeChannelValues(DebugViewMode mode,
            Vector3 worldPos, Vector3 normal, Vector2 uv, float distance,
            Transform rendererTransform, float scale, float depthRange, Camera camera)
        {
            _hasChannelValues = true;
            var parameters = ModelBoxManager.Instance.CurrentParameters;
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
                    _chR = normal.x * 0.5f + 0.5f; _chG = normal.y * 0.5f + 0.5f; _chB = normal.z * 0.5f + 0.5f; _chA = 1;
                    break;
                case DebugViewMode.UV0:
                    _chR = uv.x; _chG = uv.y; _chB = 0; _chA = 1;
                    break;
                case DebugViewMode.Depth:
                    float eyeDepth = Vector3.Dot(worldPos - camera.transform.position, camera.transform.forward);
                    _chR = Mathf.Pow(Mathf.Clamp01(eyeDepth / Mathf.Max(depthRange, 0.001f)), parameters.Gamma);
                    _chG = _chR; _chB = _chR; _chA = 1;
                    break;
                case DebugViewMode.DiagRawDepth:
                case DebugViewMode.DiagObjectDepth:
                case DebugViewMode.FlatNormal:
                case DebugViewMode.VertexColor:
                    SetUnavailable();
                    break;
                case DebugViewMode.NdotL:
                {
                    // [fix v0.4] CPU 端计算 NdotL
                    var light = RenderSettings.sun;
                    if (light != null && light.isActiveAndEnabled && light.type == LightType.Directional)
                    {
                        float ndotl = Mathf.Clamp01(Vector3.Dot(normal, -light.transform.forward));
                        _chR = ndotl; _chG = ndotl; _chB = ndotl; _chA = 1;
                    }
                    else SetUnavailable();
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
                    SetUnavailable();
                    break;
            }
            if (!_hasChannelValues) return;
            if (mode == DebugViewMode.WorldPosition || mode == DebugViewMode.LocalPosition)
            {
                _chR = Mathf.Clamp01(Mathf.Pow(Mathf.Abs(_chR), parameters.Gamma) + parameters.Offset);
                _chG = Mathf.Clamp01(Mathf.Pow(Mathf.Abs(_chG), parameters.Gamma) + parameters.Offset);
                _chB = Mathf.Clamp01(Mathf.Pow(Mathf.Abs(_chB), parameters.Gamma) + parameters.Offset);
            }
            if (mode != DebugViewMode.None)
                SetChannels(ApplyColorMap(new Color(_chR, _chG, _chB, _chA), parameters.ColorMapMode));
        }

        private static void SetChannels(Color color)
        {
            _hasChannelValues = true;
            _chR = color.r; _chG = color.g; _chB = color.b; _chA = color.a;
        }

        private static void SetUnavailable()
        {
            _hasChannelValues = false;
            _chR = _chG = _chB = _chA = -1f;
        }

        private static Color ApplyColorMap(Color color, int mode)
        {
            if (mode == 0) return color;
            float value = color.r * 0.299f + color.g * 0.587f + color.b * 0.114f;
            if (mode == 1) return new Color(value, value, value, color.a);
            value = Mathf.Clamp01(value);
            if (mode == 2)
                return new Color(SmoothStep(0.5f, 0.8f, value),
                    value < 0.5f ? SmoothStep(0f, 0.5f, value) : SmoothStep(1f, 0.5f, value),
                    SmoothStep(0f, 0.3f, value) * (1f - SmoothStep(0.3f, 0.6f, value)), color.a);
            if (mode == 3)
                return new Color(SmoothStep(0f, 0.25f, value) * (1f - SmoothStep(0.75f, 1f, value)),
                    SmoothStep(0f, 0.5f, value) * (1f - SmoothStep(0.5f, 1f, value)),
                    SmoothStep(0.25f, 0.75f, value) * (1f - SmoothStep(0.75f, 1f, value)), color.a);
            return color;
        }

        private static float SmoothStep(float start, float end, float value)
        {
            float t = Mathf.Clamp01((value - start) / (end - start));
            return t * t * (3f - 2f * t);
        }

        // ==================== [fix v0.5] 选区调试采样 ====================
        // PixelBar 此前只感知全局调试模式（DebugViewMode）；选区调试（贴图通道/顶点颜色等）激活时
        // 全局模式为 None，导致显示命中法线值而非屏幕上的调试数据，PixelBar 失去意义。

        /// <summary>选区调试插值上下文：命中 mesh + 子网格内三角形索引 + 重心坐标。</summary>
        private struct SelDebugHitContext
        {
            public Mesh mesh;
            public int subMesh;
            public int triIdx;
            public Vector2 bary;
        }

        /// <summary>被选区调试覆盖时返回原始材质 shader 名，否则返回当前材质 shader 名。</summary>
        private static string GetShaderNameForDisplay(Renderer hitRenderer, int subMesh)
        {
            var mat = GetHitMaterial(hitRenderer, subMesh);
            return (mat != null && mat.shader != null) ? mat.shader.name : "";
        }

        private static Material GetHitMaterial(Renderer renderer, int subMesh)
        {
            if (renderer == null) return null;
            var materials = ModelBoxSelectionManager.Instance.GetOriginalMaterials(renderer);
            return materials != null && materials.Length > 0
                ? materials[Mathf.Clamp(subMesh, 0, materials.Length - 1)] : null;
        }

        /// <summary>
        /// 选区覆盖优先；未覆盖的 Renderer 保留自身的全局模式结果。
        /// </summary>
        private static bool TrySampleSelectionDebug(Renderer hitRenderer, Vector3 worldPos, Vector2 uv0, in SelDebugHitContext ctx)
        {
            var selMgr = ModelBoxSelectionManager.Instance;
            if (selMgr == null || selMgr.CurrentMode == SelectionDebugMode.None) return false;

            if (hitRenderer == null || !selMgr.IsRendererOverridden(hitRenderer))
                return false;

            switch (selMgr.CurrentMode)
            {
                case SelectionDebugMode.TextureChannel:
                    SampleTextureChannel(selMgr, hitRenderer, worldPos, uv0, ctx);
                    return true;

                case SelectionDebugMode.VertexColor:
                    SampleVertexColor(hitRenderer, ctx);
                    return true;

                case SelectionDebugMode.Checkerboard:
                    float checker = (Mathf.Floor(uv0.x * selMgr.CheckerGridSize)
                        + Mathf.Floor(uv0.y * selMgr.CheckerGridSize)) % 2f;
                    SetChannels(Color.LerpUnclamped(selMgr.CheckerColorA, selMgr.CheckerColorB, checker));
                    _selInfo = $"UV0:({uv0.x:F3},{uv0.y:F3})";
                    return true;

                default:
                    // ShaderProperty 模式为常量色，无采样数据
                    SetUnavailable();
                    _selInfo = "";
                    return true;
            }
        }

        /// <summary>
        /// 贴图通道模式采样：CPU 复刻调试 shader 的采样链路
        /// （UV 通道/世界坐标 × Scale + Offset → wrap → 双线性采样 → 通道隔离/亮度 → 钳制重映射）。
        /// R/G/B/A 槽显示贴图原始 RGBA；摘要显示选中通道值（钳制激活时附重映射值 + 带内/外）。
        /// </summary>
        private static void SampleTextureChannel(ModelBoxSelectionManager selMgr, Renderer hitRenderer,
            Vector3 worldPos, Vector2 uv0, in SelDebugHitContext ctx)
        {
            // 1. 采样 UV（与 shader vert 一致：世界坐标 / UVn → × Scale + Offset）
            Vector2 uv;
            if (selMgr.WorldSpaceUV)
            {
                uv = new Vector2(worldPos.x, worldPos.z) * selMgr.WorldUVScale;
            }
            else if (selMgr.UVChannel > 0 && ctx.mesh != null)
            {
                uv = InterpolateUV(ctx.mesh, ctx.subMesh, ctx.triIdx, ctx.bary, selMgr.UVChannel);
            }
            else
            {
                uv = uv0;
            }
            uv = uv * selMgr.TextureScale + selMgr.TextureOffset;

            var source = selMgr.GetTextureChannelSource(hitRenderer, ctx.subMesh);
            var srcTex = source as Texture2D;
            if (srcTex == null)
            {
                SetUnavailable();
                _selInfo = "";
                _uvWarning = source == null ? "[材质无贴图]" : "[仅支持 Texture2D 表面采样]";
                return;
            }

            // 读回基础 mip；屏幕导数、mip/各向异性及后处理不属于此 CPU 估算。
            var cache = GetTexPixelCache(srcTex);
            if (cache == null)
            {
                SetUnavailable();
                _selInfo = "";
                _uvWarning = _texReadbackFailed.Contains(srcTex.GetInstanceID())
                    ? "[贴图读回失败]" : "[正在读取贴图…]";
                return;
            }

            Color c = SampleTexPixelCache(cache, ApplyWrapMode(uv, srcTex));
            SetChannels(c);

            // 4. 选中通道值 + 钳制重映射（与 shader frag 一致）
            float sel;
            string chName;
            switch (selMgr.ChannelMask)
            {
                case 1: sel = c.r; chName = "R"; break;
                case 2: sel = c.g; chName = "G"; break;
                case 3: sel = c.b; chName = "B"; break;
                case 4: sel = c.a; chName = "A"; break;
                default: sel = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b; chName = "Lum"; break;
            }

            float min = selMgr.ClampMin, max = selMgr.ClampMax;
            if (min > 0.001f || max < 0.999f)
            {
                float range = Mathf.Max(max - min, 1e-4f);
                float mapped = Mathf.Clamp01((sel - min) / range);
                bool inRange = sel >= min && sel <= max;
                _selInfo = $"LOD0 Ch({chName})={sel:F3}→{mapped:F3} {(inRange ? "in" : "out")}";
            }
            else
            {
                _selInfo = $"LOD0 Ch({chName})={sel:F3}";
            }
        }

        /// <summary>顶点颜色模式采样：重心坐标插值 mesh 顶点色。</summary>
        private static void SampleVertexColor(Renderer hitRenderer, in SelDebugHitContext ctx)
        {
            SetUnavailable();
            _selInfo = "";

            var mesh = ctx.mesh != null ? ctx.mesh : GetMeshFromRenderer(hitRenderer);
            if (mesh == null) return;

            var colors = mesh.colors;
            if (colors == null || colors.Length == 0)
            {
                _uvWarning = "[mesh 无顶点色]";
                return;
            }

            int[] tris = mesh.GetTriangles(ctx.subMesh);
            int b = ctx.triIdx * 3;
            if (b + 2 >= tris.Length) return;
            if (tris[b] >= colors.Length || tris[b + 1] >= colors.Length || tris[b + 2] >= colors.Length) return;

            float w0 = 1f - ctx.bary.x - ctx.bary.y;
            Color c = colors[tris[b]] * w0 + colors[tris[b + 1]] * ctx.bary.x
                + colors[tris[b + 2]] * ctx.bary.y;
            SetChannels(c);
        }

        /// <summary>插值任意 UV 通道（channel: 1~3；UV0 由 raycast 直接提供）。</summary>
        private static Vector2 InterpolateUV(Mesh mesh, int subMesh, int triIdx, Vector2 bary, int channel)
        {
            if (mesh == null) return Vector2.zero;
            var uvs = new List<Vector2>();
            mesh.GetUVs(channel, uvs);
            if (uvs.Count == 0) return Vector2.zero; // mesh 无该 UV 通道

            int[] tris = mesh.GetTriangles(subMesh);
            int b = triIdx * 3;
            if (b + 2 >= tris.Length) return Vector2.zero;
            if (tris[b] >= uvs.Count || tris[b + 1] >= uvs.Count || tris[b + 2] >= uvs.Count) return Vector2.zero;

            float w0 = 1f - bary.x - bary.y;
            return uvs[tris[b]] * w0 + uvs[tris[b + 1]] * bary.x + uvs[tris[b + 2]] * bary.y;
        }

        /// <summary>独立处理两个轴的环绕。MirrorOnce 在一次镜像后钳制。</summary>
        private static Vector2 ApplyWrapMode(Vector2 uv, Texture2D tex)
        {
            return new Vector2(WrapCoord(uv.x, tex.wrapModeU), WrapCoord(uv.y, tex.wrapModeV));
        }

        private static float WrapCoord(float value, TextureWrapMode wrap)
        {
            switch (wrap)
            {
                case TextureWrapMode.Clamp:
                    return Mathf.Clamp01(value);
                case TextureWrapMode.Mirror:
                    float repeated = Mathf.Repeat(value, 2f);
                    return repeated <= 1f ? repeated : 2f - repeated;
                case TextureWrapMode.MirrorOnce:
                    return Mathf.Clamp01(Mathf.Abs(value));
                default:
                    return value - Mathf.Floor(value);
            }
        }

        // 延后读回，避免干扰 SceneView 当前绘制；浮点缓存保留 HDR/负值。

        private class TexPixelCache
        {
            public Texture2D tex;
            public Color[] pixels;
            public int w, h;
            public uint updateCount;
            public Hash128 contentsHash;
        }

        private static readonly List<TexPixelCache> _texPixelCaches = new List<TexPixelCache>();
        private static readonly HashSet<int> _texReadbackPending = new HashSet<int>();
        private static readonly HashSet<int> _texReadbackFailed = new HashSet<int>();
        private const int MaxTexPixelCaches = 4;
        private const long MaxTexCacheBytes = 384L * 1024 * 1024;

        /// <summary>获取贴图像素缓存；未缓存时发起读回（delayCall），本帧返回 null（悬停 1-2 帧后可用）。</summary>
        private static TexPixelCache GetTexPixelCache(Texture2D tex)
        {
            for (int i = 0; i < _texPixelCaches.Count; i++)
            {
                if (_texPixelCaches[i].tex == tex)
                {
                    // LRU：命中移到头部
                    var hit = _texPixelCaches[i];
                    _texPixelCaches.RemoveAt(i);
                    if (hit.updateCount != tex.updateCount || hit.contentsHash != tex.imageContentsHash)
                        break;
                    _texPixelCaches.Insert(0, hit);
                    return hit;
                }
            }

            // 清理已销毁条目（域重载/资源卸载后 tex == null）
            _texPixelCaches.RemoveAll(t => t.tex == null);

            // 读回在 delayCall 中执行；pending 防重复注册，failed 防失败死循环重试
            int id = tex.GetInstanceID();
            if (!_texReadbackFailed.Contains(id) && _texReadbackPending.Add(id))
            {
                EditorApplication.delayCall += () =>
                {
                    _texReadbackPending.Remove(id);
                    if (tex == null) return;
                    try
                    {
                        BuildTexPixelCache(tex);
                    }
                    catch (System.Exception e)
                    {
                        _texReadbackFailed.Add(id);
                        Debug.LogWarning($"[ModelBox] PixelBar 贴图读回失败（{tex.name}）: {e.Message}");
                    }
                };
            }
            return null;
        }

        /// <summary>执行 GPU 读回并插入缓存头部（带 LRU 条数与字节预算淘汰）。</summary>
        private static void BuildTexPixelCache(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            long bytes = (long)w * h * 16;
            if (bytes > MaxTexCacheBytes)
                throw new System.InvalidOperationException("纹理浮点缓存超过 384 MB 预算");

            while (_texPixelCaches.Count >= MaxTexPixelCaches || (_texPixelCaches.Count > 0 && CurrentTexCacheBytes() + bytes > MaxTexCacheBytes))
                _texPixelCaches.RemoveAt(_texPixelCaches.Count - 1);

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D readable = null;
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var colors = readable.GetPixels();

                _texPixelCaches.Insert(0, new TexPixelCache
                {
                    tex = tex,
                    pixels = colors,
                    w = w,
                    h = h,
                    updateCount = tex.updateCount,
                    contentsHash = tex.imageContentsHash,
                });
            }
            finally
            {
                if (readable != null) Object.DestroyImmediate(readable);
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static long CurrentTexCacheBytes()
        {
            long total = 0;
            foreach (var c in _texPixelCaches)
                total += (long)c.w * c.h * 16;
            return total;
        }

        /// <summary>基础 mip 的 Point/双线性采样，输入 UV 已完成逐轴环绕。</summary>
        private static Color SampleTexPixelCache(TexPixelCache cache, Vector2 uv)
        {
            int w = cache.w, h = cache.h;
            if (cache.tex.filterMode == FilterMode.Point)
            {
                int x = WrapTexel(Mathf.FloorToInt(uv.x * w), w, cache.tex.wrapModeU);
                int y = WrapTexel(Mathf.FloorToInt(uv.y * h), h, cache.tex.wrapModeV);
                return cache.pixels[y * w + x];
            }

            float fx = uv.x * w - 0.5f;
            float fy = uv.y * h - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;

            x0 = WrapTexel(x0, w, cache.tex.wrapModeU); x1 = WrapTexel(x1, w, cache.tex.wrapModeU);
            y0 = WrapTexel(y0, h, cache.tex.wrapModeV); y1 = WrapTexel(y1, h, cache.tex.wrapModeV);

            var p = cache.pixels;
            Color top = Color.Lerp(p[y0 * w + x0], p[y0 * w + x1], tx);
            Color bottom = Color.Lerp(p[y1 * w + x0], p[y1 * w + x1], tx);
            return Color.Lerp(top, bottom, ty);
        }

        private static int WrapTexel(int value, int size, TextureWrapMode wrap)
        {
            return wrap == TextureWrapMode.Repeat ? Mod(value, size) : Mathf.Clamp(value, 0, size - 1);
        }

        private static int Mod(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }

        private static Mesh GetMeshFromRenderer(Renderer r)
        {
            if (r is MeshRenderer mr)
                return mr.GetComponent<MeshFilter>()?.sharedMesh;
            if (r is SkinnedMeshRenderer smr)
                return smr.sharedMesh;
            return null;
        }

        private static void ReadCustomProperty(Renderer hitRenderer, int subMesh)
        {
            if (!ShowCustomProp || string.IsNullOrEmpty(CustomPropName)) return;

            var mat = GetHitMaterial(hitRenderer, subMesh);

            if (hitRenderer != null && mat != null)
            {
                // [fix v0.4.1] shader 可能为 null（材质引用了已删除的 shader）
                var shader = mat.shader;
                if (shader == null) { _customPropValue = "[无Shader]"; return; }
                if (mat.HasProperty(CustomPropName))
                {
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

        private static Rect GetBarRect(SceneView sceneView)
        {
            Rect viewport = sceneView.cameraViewport;
            var camera = sceneView.camera;
            float height = viewport.height > 0 ? viewport.height
                : (camera != null ? camera.pixelHeight / EditorGUIUtility.pixelsPerPoint : 0f);
            float width = viewport.width > 0 ? viewport.width : sceneView.position.width;
            return new Rect(0f, Mathf.Max(0f, height - BarHeight - 2f), width, BarHeight);
        }

        internal static void HandleSceneInput(SceneView sceneView)
        {
            var settings = ModelBoxSettings.GetOrCreate();
            if (settings != null && !settings.ShowPixelBar) return;
            Handles.BeginGUI();
            try
            {
                int controlId = GUIUtility.GetControlID(0x4D425042, FocusType.Passive);
                ModelBoxSceneGUI.BlockSceneInput(GetBarRect(sceneView), controlId);
            }
            finally { Handles.EndGUI(); }
        }

        private static void DrawBar(SceneView sceneView, bool debugActive, bool standalone)
        {
            Handles.BeginGUI();
            try
            {
                Rect area = GetBarRect(sceneView);
                float barW = area.width;
                GUI.BeginGroup(area);
                try
                {
                    EditorGUI.DrawRect(new Rect(0, 0, barW, BarHeight), ModelBoxStyles.PixelBarBgColor);
                    var style = EditorStyles.miniLabel;
                    if (!(debugActive || standalone) || !_hasData)
                    {
                        string status = debugActive || standalone ? "CPU 表面估算 | 未命中网格" : "CPU 表面估算 | 未启用采样";
                        GUI.Label(new Rect(8, 12, Mathf.Max(0, barW - 16), 20), status, style);
                    }
                    else
                    {
                        GUI.Label(new Rect(4, 3, 30, 20), new GUIContent("CPU", "网格表面估算；非最终屏幕像素"), style);
                        float width = Mathf.Max(0, Mathf.Min(80, (barW - 42) / 5));
                        float x = 36;
                        x = ChannelRect(_hasChannelValues ? $"R:{_chR:G5}" : "R:N/A", x, 3, width, 20, new Color(1, 0.3f, 0.3f, 0.18f), style);
                        x = ChannelRect(_hasChannelValues ? $"G:{_chG:G5}" : "G:N/A", x, 3, width, 20, new Color(0.3f, 1, 0.3f, 0.18f), style);
                        x = ChannelRect(_hasChannelValues ? $"B:{_chB:G5}" : "B:N/A", x, 3, width, 20, new Color(0.3f, 0.4f, 1, 0.18f), style);
                        x = ChannelRect(_hasChannelValues ? $"A:{_chA:G5}" : "A:N/A", x, 3, width, 20, new Color(0.6f, 0.6f, 0.6f, 0.18f), style);
                        x = ChannelRect($"Z:{_depth:G5}", x, 3, width, 20, new Color(0.5f, 0.5f, 0.5f, 0.18f), style);
                        string property = ShowCustomProp && !string.IsNullOrEmpty(CustomPropName)
                            ? $" {CustomPropName}={_customPropValue}" : "";
                        string summary = $"{_selInfo} {_uvWarning}{property}".Trim();
                        if (barW > x + 16)
                            GUI.Label(new Rect(x + 6, 3, barW - x - 10, 20), new GUIContent(summary, summary), style);
                        string details = $"{_hitName} | {_shaderName} | WPos:({_worldPos.x:G5},{_worldPos.y:G5},{_worldPos.z:G5})";
                        if (barW <= x + 16 && summary.Length > 0) details = summary + " | " + details;
                        GUI.Label(new Rect(6, 25, Mathf.Max(0, barW - 12), 20), new GUIContent(details, details), style);
                    }
                }
                finally { GUI.EndGroup(); }
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
            GUI.Label(r, new GUIContent(text, text), style);
            return x + w;
        }
    }
}
