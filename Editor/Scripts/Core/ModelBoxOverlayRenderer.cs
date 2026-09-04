using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ModelBox
{
    /// <summary>
    /// 网格叠加渲染器（线框/顶点/法线/切线/AABB）。
    ///
    /// 渲染方式：
    /// - 线框/法线/切线：Handles API（自动处理 SceneView 相机变换）
    /// - 粗线：Handles.DrawAAPolyLine（抗锯齿粗线）
    /// - 顶点：Graphics.DrawMeshInstanced 实例化球体
    /// - AABB：Handles.DrawLine（12 条边）
    ///
    /// 优化：
    /// - LOD：距离远时自动跳过部分顶点/边
    /// - SkinnedMeshRenderer：BakeMesh 获取蒙皮后顶点
    /// - 边列表缓存：唯一边只构建一次
    /// </summary>
    public static class ModelBoxOverlayRenderer
    {
        // 缓存的材质和网格
        private static Material _lineMaterial;
        private static Material _vertexMaterial;
        private static Material _vertexMaterialAlways; // 深度测试关闭变体 (ZTest Always)
        private static Mesh _sphereMesh;
        private static bool _lineMaterialResolved;
        private static bool _lineMaterialIsInternal;

        // 顶点实例化临时缓冲 [O2/O3 fix]
        private static Matrix4x4[] _instanceMatrices;
        private static Matrix4x4[] _batchMatrices;
        private static MaterialPropertyBlock _sharedMPB;

        // 切线方向临时缓冲
        private static Vector3[] _tangentDirsBuffer;

        // 批量线段渲染缓冲 [性能优化]
        private static Vector3[] _wireframePointsBuffer;   // 变换后的世界空间顶点
        private static bool[] _wireframeTransformedBuffer; // 顶点变换标记（避免逐帧分配）
        private static int[] _wireframeEdgeBuffer;         // LOD 筛选后的边索引
        private static int[] _directionIndicesBuffer;      // 法线/切线索引数组
        private static Vector3[] _directionLinesBuffer;    // 法线/切线 line pairs
        private static Vector3[] _boundsPointsBuffer;      // AABB 24 个点

        // SkinnedMeshRenderer 烘焙缓冲 [Task 9]
        private static Mesh _bakeMesh;
        private static CachedMeshData _bakedCache;
        private static Mesh _bakedSourceMesh; // [R5 fix] 跟踪源网格以检测拓扑变化

        // [perf] BakeSkinnedMesh 零分配缓冲：用 GetVertices(List) 避免每帧分配大数组
        private static readonly System.Collections.Generic.List<Vector3> _bakeVertBuf = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<Vector3> _bakeNormBuf = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<Vector4> _bakeTanBuf = new System.Collections.Generic.List<Vector4>();
        private static int _bakeLastVertCount;

        private const int MAX_INSTANCES_PER_CALL = 1023;
        private const string VERTEX_SHADER_NAME = "Hidden/ModelBox/OverlayVertex";

        // LOD 参数 [Task 8]
        private const float LOD_FULL_DIST = 10f;     // < 10m: 全部绘制
        private const float LOD_HALF_DIST = 30f;     // 10-30m: 跳过 50%
        private const float LOD_MIN_DIST = 80f;      // > 80m: 跳过 90%

        // [fix] 性能保护：限制单帧最大实例数，防止 100K+ 顶点模型导致 Editor 卡死
        private const int MAX_OVERLAY_INSTANCES = 20000;

        // ========== 公开 API ==========

        /// <summary>
        /// 绘制线框（批量 Handles.DrawLines API）。
        /// 性能优化：将所有顶点变换到世界空间后，一次 DrawLines 调用绘制所有边。
        /// 相比逐条 DrawLine 减少数千次 C#→GPU 调用开销。
        /// </summary>
        public static void DrawWireframe(CachedMeshData data, Matrix4x4 localToWorld, Color color, Camera camera, Bounds worldBounds)
        {
            if (data == null || data.EdgeIndices == null || data.EdgeIndices.Length == 0) return;
            // [fix v0.4.1] 顶点数组空检查
            if (data.Vertices == null || data.Vertices.Length == 0) return;

            var verts = data.Vertices;
            var edges = data.EdgeIndices;

            // LOD: 根据距离决定步长
            int step = ComputeEdgeLODStep(worldBounds, camera, data.EdgeIndices != null ? data.EdgeIndices.Length / 2 : 0);
            int stride = 2 * step;

            // 计算有效边数
            int edgeCount = 0;
            for (int i = 0; i < edges.Length; i += stride)
                edgeCount++;
            if (edgeCount == 0) return;

            // 预变换顶点到世界空间（Handles.DrawLines 不使用 Handles.matrix）
            int maxVertIdx = 0;
            int vertLimit = verts.Length; // [fix v0.4.1] 边界检查基准
            for (int i = 0; i < edges.Length; i += stride)
            {
                // [fix v0.4.1] 防御性边界检查：边索引可能超出顶点数组范围（网格数据损坏/不完整）
                if (edges[i] >= vertLimit || edges[i + 1] >= vertLimit) continue;
                maxVertIdx = Mathf.Max(maxVertIdx, edges[i], edges[i + 1]);
            }

            int vertCount = maxVertIdx + 1;
            if (_wireframePointsBuffer == null || _wireframePointsBuffer.Length < vertCount)
                _wireframePointsBuffer = new Vector3[Mathf.Max(vertCount, 256)];

            // 只变换被引用的顶点（懒变换，避免变换整个 mesh）
            // 用缓存的标记数组避免重复变换和逐帧分配
            if (_wireframeTransformedBuffer == null || _wireframeTransformedBuffer.Length < vertCount)
                _wireframeTransformedBuffer = new bool[Mathf.Max(vertCount, 256)];

            for (int i = 0; i < edges.Length; i += stride)
            {
                int i0 = edges[i], i1 = edges[i + 1];
                // [fix v0.4.1] 跳过越界边索引，防止 IndexOutOfRangeException
                if (i0 >= vertLimit || i1 >= vertLimit) continue;
                if (!_wireframeTransformedBuffer[i0]) { _wireframePointsBuffer[i0] = localToWorld.MultiplyPoint3x4(verts[i0]); _wireframeTransformedBuffer[i0] = true; }
                if (!_wireframeTransformedBuffer[i1]) { _wireframePointsBuffer[i1] = localToWorld.MultiplyPoint3x4(verts[i1]); _wireframeTransformedBuffer[i1] = true; }
            }

            // 构建索引数组（LOD 筛选后的边索引，使用缓存缓冲）
            // [fix v0.4.1] 精确匹配数组长度，防止 LOD 步进变化后残留旧索引导致幽灵线框
            // 同时跳过越界边索引
            int validEdgeCount = 0;
            for (int i = 0; i < edges.Length; i += stride)
            {
                if (edges[i] < vertLimit && edges[i + 1] < vertLimit) validEdgeCount++;
            }
            if (validEdgeCount == 0) return;
            if (_wireframeEdgeBuffer == null || _wireframeEdgeBuffer.Length != validEdgeCount * 2)
                _wireframeEdgeBuffer = new int[validEdgeCount * 2];
            int idx = 0;
            for (int i = 0; i < edges.Length; i += stride)
            {
                if (edges[i] >= vertLimit || edges[i + 1] >= vertLimit) continue;
                _wireframeEdgeBuffer[idx++] = edges[i];
                _wireframeEdgeBuffer[idx++] = edges[i + 1];
            }

            Handles.color = color;
            Handles.DrawLines(_wireframePointsBuffer, _wireframeEdgeBuffer);

            // [fix] 清除整个使用范围的变换标记（防止 LOD 步进变化后残留 stale true）
            // 之前只清除当前边涉及的顶点，但 LOD 步进变化后旧步进标记的顶点不会被清除
            System.Array.Clear(_wireframeTransformedBuffer, 0, maxVertIdx + 1);
        }

        /// <summary>
        /// 绘制 Renderer 的世界空间 AABB 包围盒线框（12 条边）。
        /// 使用批量 DrawLines 一次绘制所有边。
        /// width > 1 时使用 DrawAAPolyLine 绘制粗线。
        /// </summary>
        public static void DrawBounds(Renderer renderer, Color color, float width = 1f)
        {
            if (renderer == null) return;

            Bounds b = renderer.bounds;
            Vector3 min = b.min;
            Vector3 max = b.max;

            // 8 个角点
            Vector3 p0 = new Vector3(min.x, min.y, min.z);
            Vector3 p1 = new Vector3(max.x, min.y, min.z);
            Vector3 p2 = new Vector3(max.x, min.y, max.z);
            Vector3 p3 = new Vector3(min.x, min.y, max.z);
            Vector3 p4 = new Vector3(min.x, max.y, min.z);
            Vector3 p5 = new Vector3(max.x, max.y, min.z);
            Vector3 p6 = new Vector3(max.x, max.y, max.z);
            Vector3 p7 = new Vector3(min.x, max.y, max.z);

            Handles.color = color;

            if (width > 1f)
            {
                // 粗线模式：每条边用 DrawAAPolyLine 绘制
                Handles.DrawAAPolyLine(width, p0, p1); Handles.DrawAAPolyLine(width, p1, p2);
                Handles.DrawAAPolyLine(width, p2, p3); Handles.DrawAAPolyLine(width, p3, p0);
                Handles.DrawAAPolyLine(width, p4, p5); Handles.DrawAAPolyLine(width, p5, p6);
                Handles.DrawAAPolyLine(width, p6, p7); Handles.DrawAAPolyLine(width, p7, p4);
                Handles.DrawAAPolyLine(width, p0, p4); Handles.DrawAAPolyLine(width, p1, p5);
                Handles.DrawAAPolyLine(width, p2, p6); Handles.DrawAAPolyLine(width, p3, p7);
            }
            else
            {
                // 批量模式：一次 DrawLines 绘制 12 条边
                if (_boundsPointsBuffer == null) _boundsPointsBuffer = new Vector3[24];
                _boundsPointsBuffer[0] = p0; _boundsPointsBuffer[1] = p1;
                _boundsPointsBuffer[2] = p1; _boundsPointsBuffer[3] = p2;
                _boundsPointsBuffer[4] = p2; _boundsPointsBuffer[5] = p3;
                _boundsPointsBuffer[6] = p3; _boundsPointsBuffer[7] = p0;
                _boundsPointsBuffer[8] = p4; _boundsPointsBuffer[9] = p5;
                _boundsPointsBuffer[10] = p5; _boundsPointsBuffer[11] = p6;
                _boundsPointsBuffer[12] = p6; _boundsPointsBuffer[13] = p7;
                _boundsPointsBuffer[14] = p7; _boundsPointsBuffer[15] = p4;
                _boundsPointsBuffer[16] = p0; _boundsPointsBuffer[17] = p4;
                _boundsPointsBuffer[18] = p1; _boundsPointsBuffer[19] = p5;
                _boundsPointsBuffer[20] = p2; _boundsPointsBuffer[21] = p6;
                _boundsPointsBuffer[22] = p3; _boundsPointsBuffer[23] = p7;
                Handles.DrawLines(_boundsPointsBuffer);
            }
        }

        // [feat v0.6] 局部坐标轴常量与样式缓存（避免每帧 GUIStyle 分配）
        private static readonly Color[] AxisLineColors =
        {
            new Color(1f, 0.3f, 0.3f, 1f),   // X 红
            new Color(0.3f, 1f, 0.4f, 1f),   // Y 绿
            new Color(0.35f, 0.6f, 1f, 1f),  // Z 蓝
        };
        private static readonly string[] AxisLabels = { "X", "Y", "Z" };
        private static GUIStyle[] _axisLabelStyles;

        /// <summary>
        /// [feat v0.6] 绘制模型局部坐标三向轴：模型原点出发的 X/Y/Z 轴（红/绿/蓝），
        /// 含抗锯齿轴线、圆锥箭头与轴标签，帮助开发者快速分辨模型局部坐标系朝向。
        /// 深度测试遵循与其他叠加一致的 OverlayDepthTest 设置；绘制时保存/恢复 Handles 状态。
        /// </summary>
        /// <param name="origin">模型原点（选中物体 transform.position）</param>
        /// <param name="rotation">局部坐标系旋转（transform.rotation；轴方向不受缩放影响）</param>
        /// <param name="length">轴长（调用方按模型包围盒自适应计算）</param>
        /// <param name="depthTest">是否被模型遮挡（false=透视显示）</param>
        public static void DrawLocalAxes(Vector3 origin, Quaternion rotation, float length, bool depthTest = true)
        {
            if (length <= 0f) return;

            var prevColor = Handles.color;
            var prevMatrix = Handles.matrix;
            var prevZTest = Handles.zTest;

            // 轴以世界空间原点绘制（传入的世界空间 origin/rotation 已含变换），不受 Handles.matrix 影响
            Handles.matrix = Matrix4x4.identity;
            Handles.zTest = depthTest ? CompareFunction.LessEqual : CompareFunction.Always;

            // 标签样式懒初始化（每轴独立颜色，便于快速辨认朝向）
            if (_axisLabelStyles == null)
            {
                _axisLabelStyles = new GUIStyle[3];
                for (int i = 0; i < 3; i++)
                {
                    _axisLabelStyles[i] = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
                    _axisLabelStyles[i].normal.textColor = AxisLineColors[i];
                }
            }

            // [perf v0.6] 端点计算抽象为纯函数（单元测试覆盖信息正确性），运行时复用缓存数组零分配
            ComputeLocalAxesEndpoints(origin, rotation, length, s_axisTips);
            float arrowSize = Mathf.Max(length * 0.12f, 0.005f);

            for (int i = 0; i < 3; i++)
            {
                Vector3 tip = s_axisTips[i];
                Vector3 dir = (tip - origin) / length; // length 有下限保证非零，与已验证端点严格一致

                Handles.color = AxisLineColors[i];
                Handles.DrawAAPolyLine(2f, origin, tip);
                // 圆锥箭头：锥底位于轴末端、指向轴方向，与轴线衔接为完整箭头
                Handles.ConeHandleCap(0, tip, Quaternion.LookRotation(dir), arrowSize, EventType.Repaint);
                Handles.Label(tip + dir * (arrowSize * 1.6f), AxisLabels[i], _axisLabelStyles[i]);
            }

            // 恢复 Handles 状态，避免影响后续 Handles 绘制
            Handles.color = prevColor;
            Handles.matrix = prevMatrix;
            Handles.zTest = prevZTest;
        }

        // ========== [perf v0.6] 烘焙线网格叠加（线框/法线/切线 GPU 快速路径） ==========

        private static Material s_overlayLineMat;
        private static readonly System.Collections.Generic.List<Mesh> s_trackedLineMeshes = new System.Collections.Generic.List<Mesh>();
        private static readonly Vector3[] s_axisTips = new Vector3[3]; // 局部轴端点缓存（零分配复用）

        /// <summary>
        /// [perf v0.6] 单个烘焙线网格的顶点数上限（超过则回退旧路径，防病态网格）。
        /// </summary>
        private const int MAX_LINE_MESH_VERTICES = 2000000;

        /// <summary>
        /// [feat v0.6] 计算局部坐标三向轴的世界空间端点（纯函数，单元测试覆盖信息正确性）。
        /// results 由调用方填充：运行时传缓存数组零分配，测试传新数组。
        /// </summary>
        public static void ComputeLocalAxesEndpoints(Vector3 origin, Quaternion rotation, float length, Vector3[] results)
        {
            results[0] = origin + rotation * Vector3.right * length;
            results[1] = origin + rotation * Vector3.up * length;
            results[2] = origin + rotation * Vector3.forward * length;
        }

        /// <summary>
        /// [feat v0.6] 轴长：合并包围盒对角线 × 系数，下限 0.05；无包围盒时固定回退 0.5。
        /// </summary>
        public static float ComputeLocalAxesLength(bool hasBounds, Vector3 extents, float coefficient)
        {
            return hasBounds ? Mathf.Max(0.05f, extents.magnitude * coefficient) : 0.5f;
        }

        /// <summary>
        /// [perf v0.6] 获取/初始化线框线网格：共享原始顶点 + Lines 拓扑边索引。
        /// 构建一次缓存于 CachedMeshData；每帧仅需 1 次 DrawMeshNow（顶点变换由 GPU 完成）。
        /// 返回 null 表示应回退旧路径（数据缺失 / 超顶点上限）。
        /// </summary>
        public static Mesh GetOrBuildWireframeLineMesh(CachedMeshData data)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return null;
            if (data.EdgeIndices == null || data.EdgeIndices.Length == 0) return null;
            if (data.Vertices.Length > MAX_LINE_MESH_VERTICES) return null;
            if (data.WireframeLineMesh != null) return data.WireframeLineMesh;

            var mesh = new Mesh
            {
                name = "ModelBox_WF_Line",
                vertices = data.Vertices,
                // uv0.x 全 0 → shader 不拉伸；即使 normals 缺失拉伸贡献也为 0，数学安全
                uv = new Vector2[data.Vertices.Length]
            };
            if (data.Vertices.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetIndices(data.EdgeIndices, MeshTopology.Lines, 0, false);
            data.WireframeLineMesh = mesh;
            s_trackedLineMeshes.Add(mesh);
            return mesh;
        }

        /// <summary>
        /// [perf v0.6] 获取/初始化法线/切线拉伸线网格：每顶点一条线段（基点 → 基点 + 方向 × _Length），
        /// 拉伸量为 shader uniform（_Length），长度滑条变化零重建。
        /// 返回 null 表示应回退旧路径（数据缺失 / 超顶点上限）。
        /// </summary>
        public static Mesh GetOrBuildStretchLineMesh(CachedMeshData data, bool tangents)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return null;
            if (data.Vertices.Length > MAX_LINE_MESH_VERTICES / 2) return null;
            if (!tangents && data.NormalLineMesh != null) return data.NormalLineMesh;
            if (tangents && data.TangentLineMesh != null) return data.TangentLineMesh;

            Vector3[] dirs;
            if (tangents)
            {
                if (data.Tangents == null || data.Tangents.Length < data.Vertices.Length) return null;
                dirs = new Vector3[data.Vertices.Length];
                for (int i = 0; i < data.Vertices.Length; i++)
                {
                    var t = data.Tangents[i];
                    dirs[i] = new Vector3(t.x, t.y, t.z).normalized;
                }
            }
            else
            {
                if (data.Normals == null || data.Normals.Length < data.Vertices.Length) return null;
                dirs = data.Normals;
            }

            int n = data.Vertices.Length;
            var pos = new Vector3[n * 2];
            var nrm = new Vector3[n * 2];
            var uv = new Vector2[n * 2];
            var idx = new int[n * 2];
            for (int i = 0; i < n; i++)
            {
                int a = i * 2, b = a + 1;
                pos[a] = data.Vertices[i];
                pos[b] = data.Vertices[i];
                nrm[a] = dirs[i];
                nrm[b] = dirs[i];
                uv[a] = Vector2.zero; // 起点：不拉伸
                uv[b] = Vector2.one;  // 终点：按 _Length 拉伸
                idx[a] = a;
                idx[b] = b;
            }

            var mesh = new Mesh { name = tangents ? "ModelBox_Tan_Line" : "ModelBox_Nrm_Line" };
            if (n * 2 > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = pos;
            mesh.normals = nrm;
            mesh.uv = uv;
            mesh.SetIndices(idx, MeshTopology.Lines, 0, false);

            if (tangents) data.TangentLineMesh = mesh;
            else data.NormalLineMesh = mesh;
            s_trackedLineMeshes.Add(mesh);
            return mesh;
        }

        /// <summary>
        /// [perf v0.6] 获取/初始化 OverlayLine 材质（单实例，_ZTest 按次切换，沿用 BoneWeight 材质范式）。
        /// </summary>
        private static Material GetOverlayLineMaterial(bool depthTest)
        {
            var shader = Shader.Find("Hidden/ModelBox/OverlayLine");
            if (shader == null)
            {
                Debug.LogWarning("[ModelBox] Shader 'Hidden/ModelBox/OverlayLine' not found, falling back to legacy line drawing path.");
                return null;
            }
            if (s_overlayLineMat == null || s_overlayLineMat.shader != shader)
                s_overlayLineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            s_overlayLineMat.SetInt("_ZTest", (int)(depthTest ? CompareFunction.LessEqual : CompareFunction.Always));
            return s_overlayLineMat;
        }

        /// <summary>
        /// [perf v0.6] 绘制烘焙线网格：单次 DrawMeshNow，顶点变换由 GPU 经 localToWorld 完成。
        /// stretchLength > 0 时 tip 顶点沿烘焙方向拉伸（法线/切线）；为 0 时原样渲染（线框）。
        /// </summary>
        public static void DrawOverlayLineMesh(Mesh lineMesh, Matrix4x4 localToWorld, Camera camera,
            Color color, float stretchLength, bool depthTest)
        {
            var mat = GetOverlayLineMaterial(depthTest);
            if (mat == null || lineMesh == null) return;

            mat.SetColor("_Color", color);
            mat.SetFloat("_Length", stretchLength);

            // 显式设置 SceneView 相机矩阵（duringSceneGui 期间 GL 状态），确保绘制落在正确相机空间
            GL.PushMatrix();
            GL.LoadProjectionMatrix(camera.projectionMatrix);
            GL.modelview = camera.worldToCameraMatrix * localToWorld;
            mat.SetPass(0);
            Graphics.DrawMeshNow(lineMesh, Matrix4x4.identity);
            GL.PopMatrix();
        }

        /// <summary>
        /// GPU 实例化绘制顶点球体（Graphics.DrawMeshInstanced）。
        /// </summary>
        public static void DrawVertices(CachedMeshData data, Matrix4x4 localToWorld,
            Color color, float size, bool scaleIndependent, Camera camera, Bounds worldBounds,
            bool depthTest = true)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return;

            var verts = data.Vertices;
            var sphere = GetSphereMesh();
            var mat = GetVertexMaterial(depthTest);
            if (sphere == null || mat == null) return;

            // LOD: 计算有效顶点子集
            int count = verts.Length;
            int lodStep = ComputeVertexLODStep(worldBounds, camera, count);

            // [perf 3.1] 视锥剔除：跳过相机视锥外的顶点，大幅减少近距离 draw call
            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanesCache);
            int maxInstances = (count + lodStep - 1) / lodStep;
            EnsureInstanceBuffer(maxInstances);

            int idx = 0;
            if (scaleIndependent)
            {
                var s = Vector3.one * size;
                for (int i = 0; i < count; i += lodStep)
                {
                    Vector3 wp = localToWorld.MultiplyPoint3x4(verts[i]);
                    if (!IsPointInFrustum(_frustumPlanesCache, wp)) continue;
                    _instanceMatrices[idx++] = Matrix4x4.TRS(wp, Quaternion.identity, s);
                }
            }
            else
            {
                var scale = Vector3.one * size;
                for (int i = 0; i < count; i += lodStep)
                {
                    Vector3 wp = localToWorld.MultiplyPoint3x4(verts[i]);
                    if (!IsPointInFrustum(_frustumPlanesCache, wp)) continue;
                    _instanceMatrices[idx++] = localToWorld * Matrix4x4.TRS(verts[i], Quaternion.identity, scale);
                }
            }
            int effectiveCount = idx;

            if (_sharedMPB == null) _sharedMPB = new MaterialPropertyBlock();
            _sharedMPB.Clear();
            _sharedMPB.SetColor("_Color", color);

            // Graphics.DrawMeshInstanced 自动扩展 bounds 以包含所有实例位置
            // 禁用阴影/光探针以减少开销（调试叠加不需要）
            for (int offset = 0; offset < effectiveCount; offset += MAX_INSTANCES_PER_CALL)
            {
                int batchCount = Mathf.Min(MAX_INSTANCES_PER_CALL, effectiveCount - offset);
                if (offset == 0 && batchCount == effectiveCount)
                {
                    Graphics.DrawMeshInstanced(sphere, 0, mat, _instanceMatrices, effectiveCount, _sharedMPB,
                        ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
                }
                else
                {
                    EnsureBatchBuffer(batchCount);
                    System.Array.Copy(_instanceMatrices, offset, _batchMatrices, 0, batchCount);
                    Graphics.DrawMeshInstanced(sphere, 0, mat, _batchMatrices, batchCount, _sharedMPB,
                        ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
                }
            }
        }

        // [feat] 骨骼权重可视化颜色缓冲
        private static Color[] _weightColorBuffer;
        private static Vector4[] _weightVectorBuffer; // SetVectorArray 用
        private static Plane[] _frustumPlanesCache = new Plane[6]; // [perf H3] 零分配视锥平面缓存

        // [feat] 骨骼权重表面渲染（Maya-style ColorMap）
        private static Material _boneWeightSurfaceMat;
        private static Mesh _boneWeightTempMesh;
        private static int _boneWeightTempMeshHash;
        // [perf] 跟踪权重数组引用，仅在骨骼选择变化时重建顶点颜色
        private static float[] _lastSurfaceWeights;

        /// <summary>
        /// Maya-style 骨骼权重表面渲染：将权重映射为顶点颜色，渲染整个网格表面。
        /// 使用临时 Mesh 副本（设置顶点颜色），配合 unlit vertex-color 材质。
        /// </summary>
        public static void DrawBoneWeightSurface(CachedMeshData data, Matrix4x4 localToWorld,
            float[] vertexWeights, Camera camera, Bounds worldBounds, bool depthTest = true,
            float opacity = 1.0f)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return;
            if (vertexWeights == null || vertexWeights.Length != data.Vertices.Length) return;
            // [fix v0.4.1] TriangleIndices 可能为 null（空网格或读取失败）
            if (data.TriangleIndices == null || data.TriangleIndices.Length == 0) return;

            var mat = GetBoneWeightSurfaceMaterial(depthTest);
            if (mat == null) return;
            mat.SetFloat("_Opacity", opacity);

            // 顶点颜色：权重 → 热力图颜色（仅在骨骼选择变化时重建）
            int vertCount = data.Vertices.Length;
            if (_weightColorBuffer == null || _weightColorBuffer.Length < vertCount)
                _weightColorBuffer = new Color[Mathf.Max(vertCount, 256)];

            // 创建/复用临时 Mesh（仅在源 Mesh 变化时重建）
            int hash = vertCount ^ (data.TriangleIndices?.GetHashCode() ?? 0);
            bool meshRebuilt = false;
            if (_boneWeightTempMesh == null || _boneWeightTempMeshHash != hash)
            {
                if (_boneWeightTempMesh != null) Object.DestroyImmediate(_boneWeightTempMesh);
                _boneWeightTempMesh = new Mesh { name = "ModelBox_BoneWeightTemp" };
                _boneWeightTempMesh.SetVertices(data.Vertices);
                _boneWeightTempMesh.SetTriangles(data.TriangleIndices, 0);
                _boneWeightTempMesh.RecalculateBounds();
                _boneWeightTempMeshHash = hash;
                meshRebuilt = true;
            }

            // [perf] 仅在权重数组变化或 Mesh 重建时更新顶点颜色（避免每帧上传）
            if (meshRebuilt || !ReferenceEquals(_lastSurfaceWeights, vertexWeights))
            {
                _lastSurfaceWeights = vertexWeights;
                for (int i = 0; i < vertCount; i++)
                    _weightColorBuffer[i] = WeightToHeatmap(vertexWeights[i]);
                _boneWeightTempMesh.SetColors(_weightColorBuffer, 0, vertCount);
            }

            // 渲染：使用 Graphics.DrawMesh 渲染到指定相机
            Graphics.DrawMesh(_boneWeightTempMesh, localToWorld, mat, 0, camera);
        }

        private static Material GetBoneWeightSurfaceMaterial(bool depthTest)
        {
            var shader = Shader.Find("Hidden/ModelBox/BoneWeight");
            if (shader == null)
            {
                Debug.LogWarning("[ModelBox] Shader 'Hidden/ModelBox/BoneWeight' not found.");
                return null;
            }

            if (depthTest)
            {
                if (_boneWeightSurfaceMat == null || _boneWeightSurfaceMat.shader != shader)
                {
                    if (_boneWeightSurfaceMat != null) Object.DestroyImmediate(_boneWeightSurfaceMat);
                    _boneWeightSurfaceMat = new Material(shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    _boneWeightSurfaceMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                }
                return _boneWeightSurfaceMat;
            }
            else
            {
                // 复用同一个材质，切换 ZTest
                if (_boneWeightSurfaceMat == null || _boneWeightSurfaceMat.shader != shader)
                {
                    if (_boneWeightSurfaceMat != null) Object.DestroyImmediate(_boneWeightSurfaceMat);
                    _boneWeightSurfaceMat = new Material(shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
                _boneWeightSurfaceMat.SetInt("_ZTest", (int)CompareFunction.Always);
                return _boneWeightSurfaceMat;
            }
        }

        /// <summary>
        /// GPU 实例化绘制带骨骼权重的顶点球体。
        /// ColorMap 模式：所有顶点按权重热力图着色。
        /// Threshold 模式：仅显示权重 > threshold 的顶点。
        /// </summary>
        public static void DrawVerticesWeighted(CachedMeshData data, Matrix4x4 localToWorld,
            float[] vertexWeights, BoneWeightDisplayMode displayMode, float threshold,
            float size, bool scaleIndependent, Camera camera, Bounds worldBounds,
            bool depthTest = true)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return;
            if (vertexWeights == null || vertexWeights.Length != data.Vertices.Length) return;

            var verts = data.Vertices;
            var sphere = GetSphereMesh();
            var mat = GetVertexMaterial(depthTest);
            if (sphere == null || mat == null) return;

            int vertCount = verts.Length;
            int lodStep = ComputeVertexLODStep(worldBounds, camera, vertCount);

            // [perf 3.1] 视锥剔除 + 权重过滤
            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanesCache);
            int maxInstances = (vertCount + lodStep - 1) / lodStep;
            EnsureInstanceBuffer(maxInstances);
            if (_weightColorBuffer == null || _weightColorBuffer.Length < maxInstances)
                _weightColorBuffer = new Color[Mathf.Max(maxInstances, 256)];

            int idx = 0;
            if (scaleIndependent)
            {
                var s = Vector3.one * size;
                for (int i = 0; i < vertCount; i += lodStep)
                {
                    float w = vertexWeights[i];
                    if (displayMode == BoneWeightDisplayMode.Threshold && w < threshold) continue;
                    Vector3 wp = localToWorld.MultiplyPoint3x4(verts[i]);
                    if (!IsPointInFrustum(_frustumPlanesCache, wp)) continue;
                    _instanceMatrices[idx] = Matrix4x4.TRS(wp, Quaternion.identity, s);
                    _weightColorBuffer[idx] = WeightToHeatmap(w);
                    idx++;
                }
            }
            else
            {
                var scale = Vector3.one * size;
                for (int i = 0; i < vertCount; i += lodStep)
                {
                    float w = vertexWeights[i];
                    if (displayMode == BoneWeightDisplayMode.Threshold && w < threshold) continue;
                    Vector3 wp = localToWorld.MultiplyPoint3x4(verts[i]);
                    if (!IsPointInFrustum(_frustumPlanesCache, wp)) continue;
                    _instanceMatrices[idx] = localToWorld * Matrix4x4.TRS(verts[i], Quaternion.identity, scale);
                    _weightColorBuffer[idx] = WeightToHeatmap(w);
                    idx++;
                }
            }
            int effectiveCount = idx;

            // 批量渲染：每批最多 1023 实例，带 per-instance 颜色
            if (_sharedMPB == null) _sharedMPB = new MaterialPropertyBlock();
            for (int offset = 0; offset < effectiveCount; offset += MAX_INSTANCES_PER_CALL)
            {
                int batchCount = Mathf.Min(MAX_INSTANCES_PER_CALL, effectiveCount - offset);

                EnsureBatchBuffer(batchCount);
                System.Array.Copy(_instanceMatrices, offset, _batchMatrices, 0, batchCount);

                // per-instance 颜色：Color → Vector4，复用缓存缓冲避免每帧分配
                if (_weightVectorBuffer == null || _weightVectorBuffer.Length < batchCount)
                    _weightVectorBuffer = new Vector4[Mathf.Max(batchCount, 256)];
                for (int ci = 0; ci < batchCount; ci++)
                {
                    var c = _weightColorBuffer[offset + ci];
                    _weightVectorBuffer[ci] = new Vector4(c.r, c.g, c.b, c.a);
                }
                _sharedMPB.Clear();
                _sharedMPB.SetVectorArray("_Color", _weightVectorBuffer);

                Graphics.DrawMeshInstanced(sphere, 0, mat, _batchMatrices, batchCount, _sharedMPB,
                    ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
            }
        }

        /// <summary>权重值 → 热力图颜色：蓝(0) → 青(0.25) → 绿(0.5) → 黄(0.75) → 红(1)</summary>
        private static Color WeightToHeatmap(float w)
        {
            float r = Mathf.Clamp01(Mathf.Max(0, (w - 0.25f) * 4f));
            float g = Mathf.Clamp01(w < 0.5f ? w * 4f : (1f - w) * 4f);
            float b = Mathf.Clamp01(Mathf.Max(0, (0.75f - w) * 4f));
            return new Color(r, g, b, 1f);
        }

        /// <summary>测试世界空间点是否在相机视锥内（含 margin 膨胀）。</summary>
        private static bool IsPointInFrustum(Plane[] planes, Vector3 worldPos)
        {
            for (int p = 0; p < planes.Length; p++)
            {
                if (planes[p].GetDistanceToPoint(worldPos) < -0.5f) // 0.5m margin for vertex sphere size
                    return false;
            }
            return true;
        }

        /// <summary>
        /// GPU 批量绘制法线。width > 1 时使用 Mesh-based 粗线渲染。
        /// </summary>
        public static void DrawNormals(CachedMeshData data, Matrix4x4 localToWorld,
            Color color, float length, float width, Camera camera, Bounds worldBounds)
        {
            // [R7 fix] 法线可能为空或长度不匹配
            if (data.Normals == null || data.Normals.Length < data.Vertices.Length) return;

            if (width > 1f)
                DrawThickDirectionLines(data.Vertices, data.Normals, localToWorld, color, length, width, camera, worldBounds);
            else
                DrawThinDirectionLines(data.Vertices, data.Normals, localToWorld, color, length, camera, worldBounds);
        }

        /// <summary>
        /// GPU 批量绘制切线。width > 1 时使用 Mesh-based 粗线渲染。
        /// </summary>
        public static void DrawTangents(CachedMeshData data, Matrix4x4 localToWorld,
            Color color, float length, float width, Camera camera, Bounds worldBounds)
        {
            if (data.Tangents == null || data.Tangents.Length != data.Vertices.Length) return;

            int count = data.Tangents.Length;
            if (_tangentDirsBuffer == null || _tangentDirsBuffer.Length < count)
                _tangentDirsBuffer = new Vector3[Mathf.Max(count, 256)];

            for (int i = 0; i < count; i++)
                _tangentDirsBuffer[i] = new Vector3(data.Tangents[i].x, data.Tangents[i].y, data.Tangents[i].z);

            if (width > 1f)
                DrawThickDirectionLines(data.Vertices, _tangentDirsBuffer, localToWorld, color, length, width, camera, worldBounds);
            else
                DrawThinDirectionLines(data.Vertices, _tangentDirsBuffer, localToWorld, color, length, camera, worldBounds);
        }

        /// <summary>
        /// 烘焙 SkinnedMeshRenderer 获取当前姿态的网格数据。[Task 9]
        /// 调用方需在每帧需要时调用此方法，返回的 CachedMeshData 在下次调用前有效。
        /// 注意：Unity 2020.2+ BakeMesh 输出为 renderer 本地空间，配合 localToWorldMatrix 使用。
        /// </summary>
        public static CachedMeshData BakeSkinnedMesh(SkinnedMeshRenderer smr)
        {
            if (smr == null) return null;
            // [fix v0.4.1] sharedMesh 可能为 null（SkinnedMeshRenderer 未配置网格）
            if (smr.sharedMesh == null) return null;

            if (_bakeMesh == null)
                _bakeMesh = new Mesh { name = "SkinnedBakeTemp" };

            smr.BakeMesh(_bakeMesh);

            // [R5 fix] 检测源网格变化（拓扑改变时重建边列表）
            Mesh sourceMesh = smr.sharedMesh;
            if (_bakedSourceMesh != sourceMesh)
            {
                _bakedSourceMesh = sourceMesh;
                if (_bakedCache != null) _bakedCache.EdgeIndices = null; // 强制重建
            }

            if (_bakedCache == null) _bakedCache = new CachedMeshData();

            // [perf] 使用 GetVertices(List) 避免每帧分配 managed 数组。
            // List 内部数组仅在容量不足时才重新分配，后续帧复用同一块内存。
            _bakeMesh.GetVertices(_bakeVertBuf);
            _bakeMesh.GetNormals(_bakeNormBuf);
            _bakeMesh.GetTangents(_bakeTanBuf);

            // 仅在顶点数变化时才重新分配输出数组（下游需要 Vector3[] 索引访问）
            int vertCount = _bakeVertBuf.Count;
            if (vertCount != _bakeLastVertCount)
            {
                _bakeLastVertCount = vertCount;
                _bakedCache.Vertices = _bakeVertBuf.ToArray();
                _bakedCache.Normals = _bakeNormBuf.Count > 0 ? _bakeNormBuf.ToArray() : null;
                _bakedCache.Tangents = _bakeTanBuf.Count > 0 ? _bakeTanBuf.ToArray() : null;
            }
            else
            {
                // 顶点数不变，直接覆盖已有数组内容（零分配）
                _bakeVertBuf.CopyTo(_bakedCache.Vertices);
                if (_bakeNormBuf.Count > 0 && _bakedCache.Normals != null)
                    _bakeNormBuf.CopyTo(_bakedCache.Normals);
                if (_bakeTanBuf.Count > 0 && _bakedCache.Tangents != null)
                    _bakeTanBuf.CopyTo(_bakedCache.Tangents);
            }

            // 边列表仅在拓扑变化时重建
            if (_bakedCache.EdgeIndices == null || _bakedCache.TriangleIndices == null)
            {
                _bakedCache.TriangleIndices = _bakeMesh.triangles;
                _bakedCache.EdgeIndices = ModelBoxMeshCache.BuildEdgeListPublic(_bakeMesh.triangles);
            }

            return _bakedCache;
        }

        /// <summary>
        /// 释放所有缓存资源（域重载时调用）。
        /// </summary>
        public static void Cleanup()
        {
            if (_lineMaterial != null && !_lineMaterialIsInternal) { Object.DestroyImmediate(_lineMaterial); }
            _lineMaterial = null;
            if (_vertexMaterial != null) { Object.DestroyImmediate(_vertexMaterial); _vertexMaterial = null; }
            if (_vertexMaterialAlways != null) { Object.DestroyImmediate(_vertexMaterialAlways); _vertexMaterialAlways = null; }
            if (_sphereMesh != null) { Object.DestroyImmediate(_sphereMesh); _sphereMesh = null; }
            if (_bakeMesh != null) { Object.DestroyImmediate(_bakeMesh); _bakeMesh = null; }
            // [fix] 清理骨骼权重表面渲染资源
            if (_boneWeightSurfaceMat != null) { Object.DestroyImmediate(_boneWeightSurfaceMat); _boneWeightSurfaceMat = null; }
            if (_boneWeightTempMesh != null) { Object.DestroyImmediate(_boneWeightTempMesh); _boneWeightTempMesh = null; }
            // [perf v0.6] 清理烘焙线网格与线材质
            foreach (var lm in s_trackedLineMeshes)
                if (lm != null) Object.DestroyImmediate(lm);
            s_trackedLineMeshes.Clear();
            if (s_overlayLineMat != null) { Object.DestroyImmediate(s_overlayLineMat); s_overlayLineMat = null; }
            _boneWeightTempMeshHash = 0;
            _lastSurfaceWeights = null;
            _instanceMatrices = null;
            _batchMatrices = null;
            _sharedMPB = null;
            _weightColorBuffer = null;
            _weightVectorBuffer = null;
            _tangentDirsBuffer = null;
            _wireframePointsBuffer = null;
            _wireframeTransformedBuffer = null;
            _wireframeEdgeBuffer = null;
            _directionLinesBuffer = null;
            _directionIndicesBuffer = null;
            _boundsPointsBuffer = null;
            _axisLabelStyles = null; // [feat v0.6] 局部坐标轴标签样式缓存
            _bakedCache = null;
            _bakedSourceMesh = null;
            _bakeVertBuf.Clear();
            _bakeNormBuf.Clear();
            _bakeTanBuf.Clear();
            _bakeLastVertCount = 0;
            _lineMaterialResolved = false;
            _lineMaterialIsInternal = false;
            // [fix] 重置视锥平面缓存（虽然域重载后会自动重新初始化，保持一致性）
            _frustumPlanesCache = new Plane[6];
        }

        // ========== 内部实现 ==========

        /// <summary>
        /// 批量绘制方向线（法线/切线）。使用 Handles.DrawLines 一次绘制所有线段。
        /// 性能：从 N 次 DrawLine 降为 1 次 DrawLines。
        /// </summary>
        private static void DrawThinDirectionLines(Vector3[] verts, Vector3[] dirs,
            Matrix4x4 localToWorld, Color color, float length, Camera camera, Bounds worldBounds)
        {
            if (verts == null || dirs == null || verts.Length == 0) return;
            if (dirs.Length < verts.Length) return;

            int step = ComputeVertexLODStep(worldBounds, camera, verts.Length);

            // 计算有效线段数
            int lineCount = (verts.Length + step - 1) / step;
            int pairCount = lineCount * 2; // 每条线 2 个点

            // 确保缓冲足够
            if (_directionLinesBuffer == null || _directionLinesBuffer.Length < pairCount)
                _directionLinesBuffer = new Vector3[Mathf.Max(pairCount, 512)];

            // 构建 line pairs（世界空间）
            int idx = 0;
            for (int i = 0; i < verts.Length; i += step)
            {
                Vector3 start = localToWorld.MultiplyPoint3x4(verts[i]);
                Vector3 end = localToWorld.MultiplyPoint3x4(verts[i] + dirs[i] * length);
                _directionLinesBuffer[idx++] = start;
                _directionLinesBuffer[idx++] = end;
            }

            Handles.color = color;

            // [fix] 索引数组必须精确匹配 pairCount，否则 Handles.DrawLines 会画残留脏数据
            if (_directionIndicesBuffer == null || _directionIndicesBuffer.Length != pairCount)
            {
                _directionIndicesBuffer = new int[pairCount];
                for (int j = 0; j < pairCount; j++) _directionIndicesBuffer[j] = j;
            }

            Handles.DrawLines(_directionLinesBuffer, _directionIndicesBuffer);
        }

        /// <summary>
        /// 粗线绘制（Handles.DrawAAPolyLine）。
        /// 使用 Handles API 替代 GL.TRIANGLES，彻底解决 GL 矩阵问题。
        /// Handles.DrawAAPolyLine 支持抗锯齿粗线，视觉效果优于 GL.LINES。
        /// </summary>
        private static void DrawThickDirectionLines(Vector3[] verts, Vector3[] dirs,
            Matrix4x4 localToWorld, Color color, float length, float width, Camera camera, Bounds worldBounds)
        {
            if (verts == null || dirs == null || verts.Length == 0) return;
            if (dirs.Length < verts.Length) return;

            int step = ComputeVertexLODStep(worldBounds, camera, verts.Length);

            Handles.matrix = localToWorld;
            Handles.color = color;

            for (int i = 0; i < verts.Length; i += step)
            {
                Vector3 start = verts[i];
                Vector3 end = start + dirs[i] * length;
                Handles.DrawAAPolyLine(width, start, end);
            }

            // [fix] 重置矩阵，避免影响后续 Handles 绘制
            Handles.matrix = Matrix4x4.identity;
        }

        // ========== LOD [Task 8] ==========

        /// <summary>
        /// 根据物体到相机的距离计算边的 LOD 步长。
        /// 使用 Renderer.bounds.center（世界空间包围盒中心）而非 transform 原点，兼容偏移 pivot。
        /// 1 = 全精度，2 = 每隔 1 条，3 = 每隔 2 条...
        /// </summary>
        private static int ComputeEdgeLODStep(Bounds worldBounds, Camera camera, int elementCount = 0)
        {
            if (camera == null) return 1;

            float dist = Vector3.Distance(camera.transform.position, worldBounds.center);
            int step;
            if (dist <= LOD_FULL_DIST) step = 1;
            else if (dist >= LOD_MIN_DIST) step = 10;
            else
            {
                float t = Mathf.InverseLerp(LOD_FULL_DIST, LOD_MIN_DIST, dist);
                step = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, 10f, t)));
            }

            // [fix] 性能保护：当元素数量超过上限时，动态增大步长
            if (elementCount > 0)
            {
                int estimated = (elementCount + step - 1) / step;
                if (estimated > MAX_OVERLAY_INSTANCES)
                    step = Mathf.CeilToInt((float)elementCount / MAX_OVERLAY_INSTANCES);
            }

            return step;
        }

        /// <summary>
        /// 根据物体到相机的距离计算顶点的 LOD 步长。
        /// </summary>
        private static int ComputeVertexLODStep(Bounds worldBounds, Camera camera, int elementCount = 0)
        {
            return ComputeEdgeLODStep(worldBounds, camera, elementCount);
        }

        // ========== 资源管理 ==========

        private static Material GetLineMaterial()
        {
            if (_lineMaterial != null) return _lineMaterial;
            if (_lineMaterialResolved) return null;

            try
            {
                var prop = typeof(HandleUtility).GetProperty("handleWireMaterial",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

                if (prop != null)
                {
                    _lineMaterial = (Material)prop.GetValue(null);
                    _lineMaterialIsInternal = (_lineMaterial != null);
                }
            }
            catch { }

            if (_lineMaterial == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader != null)
                {
                    _lineMaterial = new Material(shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    _lineMaterial.SetInt("_ZWrite", 0);
                    _lineMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                }
            }

            _lineMaterialResolved = true;
            return _lineMaterial;
        }

        private static Material GetVertexMaterial(bool depthTest = true)
        {
            if (depthTest)
            {
                if (_vertexMaterial != null) return _vertexMaterial;
            }
            else
            {
                if (_vertexMaterialAlways != null) return _vertexMaterialAlways;
            }

            var shader = Shader.Find(VERTEX_SHADER_NAME);
            if (shader == null)
            {
                Debug.LogWarning($"[ModelBox] Shader '{VERTEX_SHADER_NAME}' not found. Vertex overlay unavailable.");
                return null;
            }

            if (depthTest)
            {
                _vertexMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                _vertexMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                return _vertexMaterial;
            }
            else
            {
                _vertexMaterialAlways = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                _vertexMaterialAlways.SetInt("_ZTest", (int)CompareFunction.Always);
                return _vertexMaterialAlways;
            }
        }

        private static Mesh GetSphereMesh()
        {
            if (_sphereMesh != null) return _sphereMesh;

            _sphereMesh = CreateIcoSphere("DebugOverlaySphere", 1f, 2);
            _sphereMesh.RecalculateNormals();
            _sphereMesh.RecalculateBounds();
            return _sphereMesh;
        }

        private static Mesh CreateIcoSphere(string name, float radius, int subdivisions)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();

            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            float s = radius / Mathf.Sqrt(1f + t * t);

            verts.Add(new Vector3(-1, t, 0) * s);
            verts.Add(new Vector3(1, t, 0) * s);
            verts.Add(new Vector3(-1, -t, 0) * s);
            verts.Add(new Vector3(1, -t, 0) * s);
            verts.Add(new Vector3(0, -1, t) * s);
            verts.Add(new Vector3(0, 1, t) * s);
            verts.Add(new Vector3(0, -1, -t) * s);
            verts.Add(new Vector3(0, 1, -t) * s);
            verts.Add(new Vector3(t, 0, -1) * s);
            verts.Add(new Vector3(t, 0, 1) * s);
            verts.Add(new Vector3(-t, 0, -1) * s);
            verts.Add(new Vector3(-t, 0, 1) * s);

            int[] initTris = {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };
            tris.AddRange(initTris);

            var midCache = new System.Collections.Generic.Dictionary<long, int>();
            for (int s2 = 0; s2 < subdivisions; s2++)
            {
                var newTris = new System.Collections.Generic.List<int>();
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = GetMiddlePoint(tris[i], tris[i + 1], verts, midCache, radius);
                    int b = GetMiddlePoint(tris[i + 1], tris[i + 2], verts, midCache, radius);
                    int c = GetMiddlePoint(tris[i + 2], tris[i], verts, midCache, radius);
                    newTris.Add(tris[i]); newTris.Add(a); newTris.Add(c);
                    newTris.Add(tris[i + 1]); newTris.Add(b); newTris.Add(a);
                    newTris.Add(tris[i + 2]); newTris.Add(c); newTris.Add(b);
                    newTris.Add(a); newTris.Add(b); newTris.Add(c);
                }
                tris = newTris;
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            return mesh;
        }

        private static int GetMiddlePoint(int p1, int p2,
            System.Collections.Generic.List<Vector3> verts,
            System.Collections.Generic.Dictionary<long, int> cache, float radius)
        {
            long key = p1 < p2 ? ((long)p1 << 32) | (long)(uint)p2 : ((long)p2 << 32) | (long)(uint)p1;
            if (cache.TryGetValue(key, out int idx)) return idx;

            Vector3 mid = ((verts[p1] + verts[p2]) * 0.5f).normalized * radius;
            idx = verts.Count;
            verts.Add(mid);
            cache[key] = idx;
            return idx;
        }

        private static void EnsureInstanceBuffer(int count)
        {
            if (_instanceMatrices == null || _instanceMatrices.Length < count)
                _instanceMatrices = new Matrix4x4[Mathf.Max(count, 256)];
        }

        private static void EnsureBatchBuffer(int count)
        {
            if (_batchMatrices == null || _batchMatrices.Length < count)
                _batchMatrices = new Matrix4x4[Mathf.Max(count, MAX_INSTANCES_PER_CALL)];
        }
    }
}
