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

        /// <summary>
        /// Maya-style 骨骼权重表面渲染：将权重映射为顶点颜色，渲染整个网格表面。
        /// 使用临时 Mesh 副本（设置顶点颜色），配合 unlit vertex-color 材质。
        /// </summary>
        public static void DrawBoneWeightSurface(CachedMeshData data, Matrix4x4 localToWorld,
            float[] vertexWeights, Camera camera, Bounds worldBounds, bool depthTest = true)
        {
            if (data == null || data.Vertices == null || data.Vertices.Length == 0) return;
            if (vertexWeights == null || vertexWeights.Length != data.Vertices.Length) return;
            // [fix v0.4.1] TriangleIndices 可能为 null（空网格或读取失败）
            if (data.TriangleIndices == null || data.TriangleIndices.Length == 0) return;

            var mat = GetBoneWeightSurfaceMaterial(depthTest);
            if (mat == null) return;

            // 顶点颜色：权重 → 热力图颜色（蓝→绿→红）
            int vertCount = data.Vertices.Length;
            if (_weightColorBuffer == null || _weightColorBuffer.Length < vertCount)
                _weightColorBuffer = new Color[Mathf.Max(vertCount, 256)];

            for (int i = 0; i < vertCount; i++)
                _weightColorBuffer[i] = WeightToHeatmap(vertexWeights[i]);

            // 创建/复用临时 Mesh（仅在源 Mesh 变化时重建）
            int hash = vertCount ^ (data.TriangleIndices?.GetHashCode() ?? 0);
            if (_boneWeightTempMesh == null || _boneWeightTempMeshHash != hash)
            {
                if (_boneWeightTempMesh != null) Object.DestroyImmediate(_boneWeightTempMesh);
                _boneWeightTempMesh = new Mesh { name = "ModelBox_BoneWeightTemp" };
                _boneWeightTempMesh.SetVertices(data.Vertices);
                _boneWeightTempMesh.SetTriangles(data.TriangleIndices, 0);
                _boneWeightTempMesh.RecalculateBounds();
                _boneWeightTempMeshHash = hash;
            }

            // 更新顶点颜色（每帧更新，因为权重可能随骨骼选择变化）
            _boneWeightTempMesh.SetColors(_weightColorBuffer, 0, vertCount);

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
            _boneWeightTempMeshHash = 0;
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
