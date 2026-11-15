using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// 懒加载网格数据缓存，用于线框/顶点/法线叠加显示。
    /// 域重载时自动清理。
    /// </summary>
    public class CachedMeshData
    {
        internal Mesh SourceMesh;
        internal bool IsDynamic;
        internal int PoseVersion;
        internal int WireframePoseVersion;
        internal Bounds PoseBounds;
        internal readonly List<CachedDirectionLines> DirectionLines = new List<CachedDirectionLines>();
        internal Mesh NormalThickLineMesh;
        internal Mesh TangentThickLineMesh;

        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public int[] TriangleIndices;

        /// <summary>唯一边列表，每条边存两个索引，长度 = 边数 × 2。用于 GL.LINES 线框渲染。</summary>
        public int[] EdgeIndices;

        /// <summary>网格的绑定骨骼姿态（SkinnedMeshRenderer 需要）。</summary>
        public Matrix4x4[] Bindposes;

        /// <summary>[perf v0.6] 烘焙线框网格：共享原始顶点 + Lines 拓扑索引。懒构建，GPU 直接变换顶点，替代逐帧 DrawLines。</summary>
        public Mesh WireframeLineMesh;

        /// <summary>[perf v0.6] 烘焙法线拉伸线网格：每顶点 (基点, 方向, tip 标记)，_Length uniform 拉伸，滑条零重建。</summary>
        public Mesh NormalLineMesh;

        /// <summary>[perf v0.6] 烘焙切线拉伸线网格：结构同法线网格。</summary>
        public Mesh TangentLineMesh;
    }

    internal sealed class CachedDirectionLines
    {
        public Mesh Mesh;
        public Vector3[] Positions;
        public Vector3[] Directions;
        public bool Tangents;
        public int Copies;
        public int PoseVersion;
    }

    [InitializeOnLoad]
    public static class ModelBoxMeshCache
    {
        private static readonly Dictionary<Mesh, CachedMeshData> _cache = new Dictionary<Mesh, CachedMeshData>();
        private static readonly EdgeKeyComparer _edgeComparer = new EdgeKeyComparer();

        private sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public bool Equals(long x, long y) => x == y;

            public int GetHashCode(long value)
            {
                // Int64 默认高低位异或会让连续顶点边大量碰撞。
                unchecked
                {
                    uint hash = (uint)(value >> 32) * 0x9E3779B9u + (uint)value;
                    hash = (hash ^ (hash >> 16)) * 0x85EBCA6Bu;
                    return (int)(hash ^ (hash >> 13));
                }
            }
        }

        static ModelBoxMeshCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Invalidate;
        }

        public static CachedMeshData GetOrBuild(Mesh mesh, bool includeTopology = true)
        {
            if (mesh == null) return null;

            if (!_cache.TryGetValue(mesh, out var cached))
            {
                cached = new CachedMeshData
                {
                    SourceMesh = mesh,
                    Vertices = mesh.vertices,
                    Normals = mesh.normals,
                    Tangents = mesh.tangents,
                    Bindposes = mesh.bindposes,
                };
                _cache[mesh] = cached;
            }

            if (includeTopology) EnsureTopology(cached, true);
            return cached;
        }

        internal static void EnsureTopology(CachedMeshData data, bool includeEdges)
        {
            if (data == null || data.SourceMesh == null) return;
            var source = GetOrBuild(data.SourceMesh, false);
            if (source.TriangleIndices == null) source.TriangleIndices = data.SourceMesh.triangles;
            if (includeEdges && source.EdgeIndices == null)
                source.EdgeIndices = BuildEdgeList(source.TriangleIndices);
            data.TriangleIndices = source.TriangleIndices;
            if (includeEdges) data.EdgeIndices = source.EdgeIndices;
        }

        /// <summary>
        /// 从三角形索引提取唯一边列表。
        /// 使用排序对 (min, max) 去重，输出 int[] 供 GL.LINES 使用。
        /// </summary>
        private static int[] BuildEdgeList(int[] tris)
        {
            return BuildEdgeListPublic(tris);
        }

        /// <summary>
        /// 从三角形索引提取唯一边列表（公开版本，供 SkinnedMeshRenderer 烘焙使用）。
        /// </summary>
        public static int[] BuildEdgeListPublic(int[] tris)
        {
            // 估算边数：三角形数 × 1.5（共享边约占 1/3）
            int triCount = tris.Length / 3;
            var edgeSet = new HashSet<long>(triCount * 2, _edgeComparer);

            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                AddEdge(edgeSet, a, b);
                AddEdge(edgeSet, b, c);
                AddEdge(edgeSet, c, a);
            }

            var result = new int[edgeSet.Count * 2];
            int idx = 0;
            foreach (long key in edgeSet)
            {
                result[idx++] = (int)(key >> 32);
                result[idx++] = (int)(key & 0xFFFFFFFFL);
            }
            return result;
        }

        private static void AddEdge(HashSet<long> set, int a, int b)
        {
            if (a > b) { int t = a; a = b; b = t; }
            set.Add(((long)a << 32) | (long)(uint)b);
        }

        public static void Invalidate()
        {
            _cache.Clear();
        }

        public static void InvalidateMesh(Mesh mesh)
        {
            if (mesh != null)
                _cache.Remove(mesh);
        }
    }
}
