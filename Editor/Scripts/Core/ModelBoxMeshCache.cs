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
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public int[] TriangleIndices;

        /// <summary>唯一边列表，每条边存两个索引，长度 = 边数 × 2。用于 GL.LINES 线框渲染。</summary>
        public int[] EdgeIndices;

        /// <summary>网格的绑定骨骼姿态（SkinnedMeshRenderer 需要）。</summary>
        public Matrix4x4[] Bindposes;
    }

    [InitializeOnLoad]
    public static class ModelBoxMeshCache
    {
        private static readonly Dictionary<Mesh, CachedMeshData> _cache = new Dictionary<Mesh, CachedMeshData>();

        static ModelBoxMeshCache()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Invalidate;
        }

        public static CachedMeshData GetOrBuild(Mesh mesh)
        {
            if (mesh == null) return null;

            if (_cache.TryGetValue(mesh, out var cached))
                return cached;

            var tris = mesh.triangles;
            var edgeIndices = BuildEdgeList(tris);

            cached = new CachedMeshData
            {
                Vertices = mesh.vertices,
                Normals = mesh.normals,
                Tangents = mesh.tangents,
                TriangleIndices = tris,
                EdgeIndices = edgeIndices,
                Bindposes = mesh.bindposes,
            };

            _cache[mesh] = cached;
            return cached;
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
            var edgeSet = new HashSet<long>(triCount * 2);

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
