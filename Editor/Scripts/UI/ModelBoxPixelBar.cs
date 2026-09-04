using System.Collections.Generic;
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
        private static string _selInfo = ""; // [fix v0.5] 选区调试采样摘要（Ch(A)=0.673→0.460 in）
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
        /// [fix v0.4.3] 修复预制体穿透：Physics.Raycast 命中后检查是否有更近的无 Collider Renderer
        /// 被射线穿透（预制体常见：根节点无 Collider，子模型有 Collider 或完全无 Collider）。
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

            // [fix v0.4.3] 统一拾取策略：先做 CPU mesh raycast 找到最近的可见 Renderer，
            // 再与 Physics.Raycast 结果比较取更近的。
            // 这解决了预制体无 Collider 被穿透的问题。

            // Step 1: CPU mesh raycast 找最近 Renderer
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
                        if (hit.distance < meshHitDist)
                        {
                            meshHitDist = hit.distance;
                            meshHitRenderer = rend;
                            meshHit = hit;
                        }
                    }
                }
            }

            // Step 2: Physics.Raycast 找最近 Collider 命中
            RaycastHit physicsHit;
            bool physicsHitValid = Physics.Raycast(ray, out physicsHit, maxDist);

            // Step 3: 比较两个结果，取距离更近的
            bool usePhysics = physicsHitValid && physicsHit.distance <= meshHitDist;
            // [fix v0.5] physics 命中数据可用性：非 MeshCollider（无 triangle/UV 数据，textureCoord 恒为 (0,0)）
            // 或 convex 壳（网格拓扑 ≠ 渲染网格）都无法提供真实网格数据；mesh raycast 有效时优先 mesh 结果
            if (usePhysics && meshHitRenderer != null)
            {
                var mcHit = physicsHit.collider as MeshCollider;
                if (mcHit == null || mcHit.convex)
                    usePhysics = false;
            }

            if (usePhysics)
            {
                // Physics 命中更近（或 mesh 未命中）—— 使用 Physics 结果
                _hasData = true;
                _hitName = physicsHit.collider.gameObject.name;
                _worldPos = physicsHit.point;
                _uvWarning = "";
                _selInfo = "";

                // Renderer 查找：优先 Collider 同级 → 父级 → [fix v0.5] mesh 命中兜底（根级第一个可能是未命中的其他部位） → 根的子级
                var hitRenderer = physicsHit.collider.GetComponent<Renderer>();
                if (hitRenderer == null)
                    hitRenderer = physicsHit.collider.GetComponentInParent<Renderer>();
                if (hitRenderer == null && meshHitRenderer != null)
                    hitRenderer = meshHitRenderer;
                if (hitRenderer == null)
                    hitRenderer = physicsHit.collider.transform.root.GetComponentInChildren<Renderer>();

                // [fix v0.5] 被选区调试覆盖时读原始材质 shader 名（避免显示 Hidden/ModelBox/... 调试 shader）
                _shaderName = GetShaderNameForDisplay(hitRenderer);
                var mode = ModelBoxManager.Instance?.CurrentMode ?? DebugViewMode.None;
                _depth = physicsHit.distance;

                var params2 = ModelBoxManager.Instance?.CurrentParameters ?? default;
                float scale = params2.Scale;
                float depthRange = params2.DepthRange;

                var rendererTransform = hitRenderer != null ? hitRenderer.transform : physicsHit.collider.transform;
                ComputeChannelValues(mode, physicsHit.point, physicsHit.normal, physicsHit.textureCoord,
                    physicsHit.distance, rendererTransform, scale, depthRange, camera);

                // [fix v0.5] 全局模式为 None 时按选区调试模式采样（贴图通道/顶点颜色等）
                if (mode == DebugViewMode.None)
                {
                    var selCtx = BuildSelCtxFromPhysics(physicsHit, hitRenderer);
                    TrySampleSelectionDebug(hitRenderer, physicsHit.point, physicsHit.textureCoord, selCtx);
                }

                ReadCustomProperty(hitRenderer);
            }
            else if (meshHitRenderer != null)
            {
                // CPU mesh raycast 命中更近（或 Physics 未命中）—— 使用 mesh 结果
                _hasData = true;
                _hitName = meshHitRenderer.gameObject.name;
                _worldPos = meshHit.point;
                _uvWarning = "";
                _selInfo = "";
                // [fix v0.5] 被选区调试覆盖时读原始材质 shader 名
                _shaderName = GetShaderNameForDisplay(meshHitRenderer);
                var mode = ModelBoxManager.Instance?.CurrentMode ?? DebugViewMode.None;
                _depth = meshHit.distance;

                var params3 = ModelBoxManager.Instance?.CurrentParameters ?? default;
                float scale3 = params3.Scale;
                float depthRange3 = params3.DepthRange;

                ComputeChannelValues(mode, meshHit.point, meshHit.normal, meshHit.textureCoord,
                    meshHit.distance, meshHitRenderer.transform, scale3, depthRange3, camera);

                // [fix v0.5] 全局模式为 None 时按选区调试模式采样
                if (mode == DebugViewMode.None)
                {
                    var selCtx = new SelDebugHitContext
                    {
                        mesh = GetMeshFromRenderer(meshHitRenderer),
                        subMesh = meshHit.subMesh,
                        triIdx = meshHit.triangleIndex,
                        bary = meshHit.barycentric,
                    };
                    TrySampleSelectionDebug(meshHitRenderer, meshHit.point, meshHit.textureCoord, selCtx);
                }

                ReadCustomProperty(meshHitRenderer);
            }
            else
            {
                _hasData = false;
                _chR = _chG = _chB = _chA = _depth = 0;
                _hitName = "";
                _customPropValue = "";
                _uvWarning = "";
                _selInfo = "";
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
        private static string GetShaderNameForDisplay(Renderer hitRenderer)
        {
            if (hitRenderer == null) return "";
            var selMgr = ModelBoxSelectionManager.Instance;
            var mat = (selMgr != null && selMgr.IsRendererOverridden(hitRenderer))
                ? selMgr.GetOriginalMaterial(hitRenderer)
                : hitRenderer.sharedMaterial;
            return (mat != null && mat.shader != null) ? mat.shader.name : "";
        }

        /// <summary>
        /// 选区调试模式采样入口（全局模式为 None 时调用）。
        /// 返回 true 表示已处理；未覆盖物体标记 N/A，避免法线值误导。
        /// </summary>
        private static bool TrySampleSelectionDebug(Renderer hitRenderer, Vector3 worldPos, Vector2 uv0, in SelDebugHitContext ctx)
        {
            var selMgr = ModelBoxSelectionManager.Instance;
            if (selMgr == null || selMgr.CurrentMode == SelectionDebugMode.None) return false;

            if (hitRenderer == null || !selMgr.IsRendererOverridden(hitRenderer))
            {
                _chR = _chG = _chB = _chA = -1;
                _selInfo = "";
                return true;
            }

            switch (selMgr.CurrentMode)
            {
                case SelectionDebugMode.TextureChannel:
                    SampleTextureChannel(selMgr, hitRenderer, worldPos, uv0, ctx);
                    return true;

                case SelectionDebugMode.VertexColor:
                    SampleVertexColor(hitRenderer, ctx);
                    return true;

                case SelectionDebugMode.Checkerboard:
                    // 棋盘格调试 UV：显示命中 UV 坐标
                    _chR = uv0.x; _chG = uv0.y; _chB = 0; _chA = 1;
                    _selInfo = "";
                    return true;

                default:
                    // ShaderProperty 模式为常量色，无采样数据
                    _chR = _chG = _chB = _chA = -1;
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

            // 2. 源贴图（与 ApplyDebugMaterial 一致：自定义优先，否则原始材质主贴图——含自定义 shader 属性扫描）
            var srcTex = selMgr.GetTextureChannelSource(hitRenderer) as Texture2D;
            if (srcTex == null)
            {
                _chR = _chG = _chB = _chA = -1;
                _selInfo = "";
                _uvWarning = "[材质无贴图]";
                return;
            }

            // 3. GPU 读回缓存采样（美术资产默认不开 Read/Write，GetPixelBilinear 直接不可用——
            //    此前 PixelBar 全 N/A 的主因。Blit→LinearRT→ReadPixels 读回值与 GPU 采样一致）
            var cache = GetTexPixelCache(srcTex);
            if (cache == null)
            {
                _chR = _chG = _chB = _chA = -1;
                _selInfo = "";
                _uvWarning = _texReadbackFailed.Contains(srcTex.GetInstanceID())
                    ? "[贴图读回失败]" : "[正在读取贴图…]";
                return;
            }

            Color c = SampleTexPixelCache(cache, ApplyWrapMode(uv, srcTex));
            _chR = c.r; _chG = c.g; _chB = c.b; _chA = c.a;

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
                _selInfo = $"Ch({chName})={sel:F3}→{mapped:F3} {(inRange ? "in" : "out")}";
            }
            else
            {
                _selInfo = $"Ch({chName})={sel:F3}";
            }
        }

        /// <summary>顶点颜色模式采样：重心坐标插值 mesh 顶点色。</summary>
        private static void SampleVertexColor(Renderer hitRenderer, in SelDebugHitContext ctx)
        {
            _chR = _chG = _chB = _chA = -1;
            _selInfo = "";

            var mesh = ctx.mesh != null ? ctx.mesh : GetMeshFromRenderer(hitRenderer);
            if (mesh == null) return;

            var colors = mesh.colors32;
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
            Color c = (Color)colors[tris[b]] * w0
                    + (Color)colors[tris[b + 1]] * ctx.bary.x
                    + (Color)colors[tris[b + 2]] * ctx.bary.y;
            _chR = c.r; _chG = c.g; _chB = c.b; _chA = c.a;
        }

        /// <summary>插值任意 UV 通道（channel: 1~3；UV0 由 raycast 直接提供）。</summary>
        private static Vector2 InterpolateUV(Mesh mesh, int subMesh, int triIdx, Vector2 bary, int channel)
        {
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

        /// <summary>按纹理 wrap 模式归一化采样 UV（与 GPU sampler 一致；PerAxis 差异用主模式近似）。</summary>
        private static Vector2 ApplyWrapMode(Vector2 uv, Texture2D tex)
        {
            switch (tex.wrapMode)
            {
                case TextureWrapMode.Clamp:
                    return new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));
                case TextureWrapMode.Mirror:
                case TextureWrapMode.MirrorOnce:
                    float mx = MirrorCoord(uv.x);
                    float my = MirrorCoord(uv.y);
                    if (tex.wrapMode == TextureWrapMode.MirrorOnce)
                        return new Vector2(Mathf.Clamp01(mx), Mathf.Clamp01(my));
                    return new Vector2(mx, my);
                default: // Repeat
                    return new Vector2(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));
            }
        }

        private static float MirrorCoord(float t)
        {
            return Mathf.Abs(Mathf.Repeat(t * 0.5f + 0.5f, 1f) * 2f - 1f);
        }

        // ==================== [fix v0.5] 贴图 GPU 读回缓存 ====================
        // 美术资产默认不开 Read/Write，Texture2D.GetPixelBilinear 直接不可用（PixelBar 全 N/A 的主因）。
        // 方案：首次需要时 Graphics.Blit → Linear RT → ReadPixels → Color32[] 缓存——读回值与 GPU shader
        // 采样一致（Linear RT 不做 sRGB 编码、源采样已解码；Gamma 色彩空间下无转换，同样一致），
        // 且不要求 isReadable。读回在 delayCall 中执行（脱离 duringSceneGui 渲染上下文，避免干扰 SceneView 绘制）。
        // 内存控制：单张 4K 约 64MB，条目上限 4 张 + 总量上限 384MB，超限淘汰最旧。

        private class TexPixelCache
        {
            public Texture2D tex;
            public Color32[] pixels;
            public int w, h;
            public TextureWrapMode wrap;
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
            long bytes = (long)w * h * 4;

            while (_texPixelCaches.Count >= MaxTexPixelCaches || (_texPixelCaches.Count > 0 && CurrentTexCacheBytes() + bytes > MaxTexCacheBytes))
                _texPixelCaches.RemoveAt(_texPixelCaches.Count - 1);

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D readable = null;
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var colors = readable.GetPixels32();

                _texPixelCaches.Insert(0, new TexPixelCache
                {
                    tex = tex,
                    pixels = colors,
                    w = w,
                    h = h,
                    wrap = tex.wrapMode,
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
                total += (long)c.w * c.h * 4;
            return total;
        }

        /// <summary>CPU 双线性插值（像素中心对齐，与 GPU 采样一致；wrap 环绕按纹理主模式，Mirror 边缘半 texel 近似 Repeat）。</summary>
        private static Color SampleTexPixelCache(TexPixelCache cache, Vector2 uv)
        {
            int w = cache.w, h = cache.h;
            bool clampWrap = cache.wrap == TextureWrapMode.Clamp;

            float fx = uv.x * w - 0.5f;
            float fy = uv.y * h - 0.5f;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;

            if (clampWrap)
            {
                x0 = Mathf.Clamp(x0, 0, w - 1); x1 = Mathf.Clamp(x1, 0, w - 1);
                y0 = Mathf.Clamp(y0, 0, h - 1); y1 = Mathf.Clamp(y1, 0, h - 1);
            }
            else
            {
                x0 = Mod(x0, w); x1 = Mod(x1, w);
                y0 = Mod(y0, h); y1 = Mod(y1, h);
            }

            var p = cache.pixels;
            Color top = Color.Lerp(p[y0 * w + x0], p[y0 * w + x1], tx);
            Color bottom = Color.Lerp(p[y1 * w + x0], p[y1 * w + x1], tx);
            return Color.Lerp(top, bottom, ty);
        }

        private static int Mod(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }

        /// <summary>Physics 全局三角形索引 → (子网格, 子网格内索引)。</summary>
        private static void GlobalTriToLocal(Mesh mesh, int globalTri, out int subMesh, out int localTri)
        {
            subMesh = 0;
            localTri = 0;
            if (mesh == null) return;
            int total = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int cnt = (int)(mesh.GetIndexCount(s) / 3);
                if (globalTri < total + cnt)
                {
                    subMesh = s;
                    localTri = globalTri - total;
                    return;
                }
                total += cnt;
            }
        }

        /// <summary>从 Physics 命中构建插值上下文（MeshCollider 优先取其 mesh）。</summary>
        private static SelDebugHitContext BuildSelCtxFromPhysics(RaycastHit physicsHit, Renderer hitRenderer)
        {
            var ctx = new SelDebugHitContext();
            var mc = physicsHit.collider as MeshCollider;
            ctx.mesh = (mc != null && mc.sharedMesh != null) ? mc.sharedMesh : GetMeshFromRenderer(hitRenderer);
            if (ctx.mesh != null)
                GlobalTriToLocal(ctx.mesh, physicsHit.triangleIndex, out ctx.subMesh, out ctx.triIdx);
            ctx.bary = new Vector2(physicsHit.barycentricCoordinate.x, physicsHit.barycentricCoordinate.y);
            return ctx;
        }

        private static Mesh GetMeshFromRenderer(Renderer r)
        {
            if (r is MeshRenderer mr)
                return mr.GetComponent<MeshFilter>()?.sharedMesh;
            if (r is SkinnedMeshRenderer smr)
                return smr.sharedMesh;
            return null;
        }

        private static void ReadCustomProperty(Renderer hitRenderer)
        {
            if (!ShowCustomProp || string.IsNullOrEmpty(CustomPropName)) return;

            // [fix v0.6] 选区调试激活时 sharedMaterial 已被调试材质替换，
            // 自定义属性必须回读原始材质（调试材质无业务属性，读出的是误导值）
            var selMgr = ModelBoxSelectionManager.Instance;
            var mat = (hitRenderer != null && selMgr != null)
                ? selMgr.GetOriginalMaterial(hitRenderer)
                : (hitRenderer != null ? hitRenderer.sharedMaterial : null);

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
                    // [fix v0.5] 显示选区调试采样摘要（贴图通道值/重映射值/带内状态）
                    if (!string.IsNullOrEmpty(_selInfo))
                        propText = string.IsNullOrEmpty(propText) ? _selInfo : $"{propText} {_selInfo}";
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
                        // [fix v0.5] 动态宽度（选区调试摘要可能比固定 155px 长）
                        var propSize = miniStyle.CalcSize(new GUIContent(propText));
                        GUI.Label(new Rect(x, y, propSize.x + 6, labelH), propText, miniStyle);
                        x += propSize.x + 10;
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
