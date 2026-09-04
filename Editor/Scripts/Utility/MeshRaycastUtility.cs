using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ModelBox
{
    /// <summary>
    /// CPU 端射线-网格求交工具。用于 Physics.Raycast 无法命中（缺少 Collider）时的降级方案。
    /// 使用 Möller-Trumbore 算法进行射线-三角形相交测试。
    /// 射线在本地空间计算，避免逐顶点 world 变换开销。
    /// </summary>
    public static class MeshRaycastUtility
    {
        /// <summary>
        /// 射线-网格命中结果。字段语义与 UnityEngine.RaycastHit 一致。
        /// </summary>
        public struct MeshRayHit
        {
            public Vector3 point;        // 世界坐标命中点
            public Vector3 normal;       // 世界空间法线
            public Vector2 textureCoord; // UV0
            public float distance;       // 射线起点到命中点的距离
            public int triangleIndex;    // 命中的三角形索引（子网格内）
            public int subMesh;          // [feat] 命中的子网格索引（供任意 UV 通道/顶点色插值）
            public Vector2 barycentric;  // [feat] 重心坐标 (u, v)：v1/v2 权重，v0 权重 = 1 - x - y
        }

        private const float Epsilon = 1e-8f;

        // SkinnedMeshRenderer 烘焙目标（静态复用，避免 GC）
        private static Mesh _skinnedBakeMesh;

        /// <summary>
        /// 对指定 Renderer 执行 CPU 射线求交。支持 MeshRenderer 和 SkinnedMeshRenderer。
        /// 返回最近命中点，未命中时返回 false。
        /// </summary>
        public static bool Raycast(Ray worldRay, Renderer renderer, out MeshRayHit hit)
        {
            hit = default;
            if (renderer == null) return false;

            Mesh mesh = GetMesh(renderer);
            if (mesh == null) return false;

            // 射线变换到本地空间（比逐顶点变换到世界空间更高效）
            Matrix4x4 localToWorld = renderer.transform.localToWorldMatrix;
            Matrix4x4 worldToLocal = renderer.transform.worldToLocalMatrix;

            Vector3 localOrigin = worldToLocal.MultiplyPoint3x4(worldRay.origin);
            Vector3 localDir = worldToLocal.MultiplyVector(worldRay.direction);

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;

            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasUVs = uvs != null && uvs.Length == vertices.Length;

            bool found = false;
            float closestDist = float.MaxValue;
            int hitTriIdx = 0;
            float hitU = 0, hitV = 0;
            int hitSubMesh = 0;

            // 遍历所有子网格
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                int[] tris = mesh.GetTriangles(sub);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 v0 = vertices[tris[i]];
                    Vector3 v1 = vertices[tris[i + 1]];
                    Vector3 v2 = vertices[tris[i + 2]];

                    if (!MollerTrumbore(localOrigin, localDir, v0, v1, v2,
                            out float t, out float u, out float v))
                        continue;

                    if (t < Epsilon || t >= closestDist) continue;

                    closestDist = t;
                    hitTriIdx = i / 3;
                    hitU = u;
                    hitV = v;
                    hitSubMesh = sub;
                    found = true;
                }
            }

            if (!found) return false;

            // 命中点 → 世界空间
            Vector3 localHitPoint = localOrigin + localDir * closestDist;
            hit.point = localToWorld.MultiplyPoint3x4(localHitPoint);
            hit.distance = Vector3.Distance(worldRay.origin, hit.point);

            // 法线计算：有顶点法线时双线性插值（平滑着色），否则用面法线
            int[] hitTris = mesh.GetTriangles(hitSubMesh);
            int i0 = hitTris[hitTriIdx * 3];
            int i1 = hitTris[hitTriIdx * 3 + 1];
            int i2 = hitTris[hitTriIdx * 3 + 2];

            if (hasNormals)
            {
                float w0 = 1f - hitU - hitV;
                Vector3 localNormal = (normals[i0] * w0 + normals[i1] * hitU + normals[i2] * hitV).normalized;
                hit.normal = localToWorld.MultiplyVector(localNormal).normalized;
            }
            else
            {
                Vector3 e1 = vertices[i1] - vertices[i0];
                Vector3 e2 = vertices[i2] - vertices[i0];
                Vector3 localFaceNormal = Vector3.Cross(e1, e2).normalized;
                hit.normal = localToWorld.MultiplyVector(localFaceNormal).normalized;
            }

            // UV 插值
            if (hasUVs)
            {
                float w0 = 1f - hitU - hitV;
                hit.textureCoord = uvs[i0] * w0 + uvs[i1] * hitU + uvs[i2] * hitV;
            }

            hit.triangleIndex = hitTriIdx;
            hit.subMesh = hitSubMesh;
            hit.barycentric = new Vector2(hitU, hitV);
            return true;
        }

        /// <summary>
        /// 手动拾取射线方向上最近的 Mesh/SkinnedMesh Renderer（基于 Bounds 相交测试）。
        /// 替代 HandleUtility.PickGameObject，避免在 duringSceneGui Repaint 中触发递归渲染。
        /// [fix v0.4] 新增：返回所有 Bounds 命中的 Renderer 列表（按距离排序），供调用方逐个 mesh raycast。
        /// </summary>
        public static List<Renderer> PickNearestRenderers(Ray worldRay, Camera camera)
        {
            float maxDist = camera != null ? camera.farClipPlane : 5000f;
            var results = new List<(Renderer rend, float dist)>();

            foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsSortMode.None))
            {
                if (!mr.enabled || mr.gameObject.hideFlags != HideFlags.None) continue;
                if (!RayIntersectsBounds(worldRay, mr.bounds, out float dist)) continue;
                if (dist > maxDist) continue;
                results.Add((mr, dist));
            }

            foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(
                         FindObjectsSortMode.None))
            {
                if (!smr.enabled || smr.gameObject.hideFlags != HideFlags.None) continue;
                if (!RayIntersectsBounds(worldRay, smr.bounds, out float dist)) continue;
                if (dist > maxDist) continue;
                results.Add((smr, dist));
            }

            // 按距离排序（最近在前）
            results.Sort((a, b) => a.dist.CompareTo(b.dist));
            return results.Select(r => r.rend).ToList();
        }

        /// <summary>
        /// 手动拾取射线方向上最近的 Mesh/SkinnedMesh Renderer（基于 Bounds 相交测试）。
        /// 替代 HandleUtility.PickGameObject，避免在 duringSceneGui Repaint 中触发递归渲染。
        /// </summary>
        public static Renderer PickNearestRenderer(Ray worldRay, Camera camera)
        {
            float maxDist = camera != null ? camera.farClipPlane : 5000f;
            Renderer nearest = null;
            float nearestDist = float.MaxValue;

            // 遍历所有可见 Renderer，测试 Bounds-Ray 相交
            // 100ms 节流下每帧最多执行一次，场景物体数量通常可控
            foreach (var mr in UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsSortMode.None))
            {
                if (!mr.enabled || mr.gameObject.hideFlags != HideFlags.None) continue;
                if (!RayIntersectsBounds(worldRay, mr.bounds, out float dist)) continue;
                if (dist >= nearestDist || dist > maxDist) continue;
                nearestDist = dist;
                nearest = mr;
            }

            foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(
                         FindObjectsSortMode.None))
            {
                if (!smr.enabled || smr.gameObject.hideFlags != HideFlags.None) continue;
                if (!RayIntersectsBounds(worldRay, smr.bounds, out float dist)) continue;
                if (dist >= nearestDist || dist > maxDist) continue;
                nearestDist = dist;
                nearest = smr;
            }

            return nearest;
        }

        /// <summary>
        /// Slab 法 Ray-AABB 相交测试。
        /// </summary>
        private static bool RayIntersectsBounds(Ray ray, Bounds bounds, out float distance)
        {
            distance = 0f;
            var min = bounds.min;
            var max = bounds.max;
            float tmin = float.MinValue, tmax = float.MaxValue;

            for (int i = 0; i < 3; i++)
            {
                float origin = i == 0 ? ray.origin.x : (i == 1 ? ray.origin.y : ray.origin.z);
                float dir = i == 0 ? ray.direction.x : (i == 1 ? ray.direction.y : ray.direction.z);
                float bmin = i == 0 ? min.x : (i == 1 ? min.y : min.z);
                float bmax = i == 0 ? max.x : (i == 1 ? max.y : max.z);

                if (Mathf.Abs(dir) < 1e-8f)
                {
                    if (origin < bmin || origin > bmax) return false;
                }
                else
                {
                    float invD = 1f / dir;
                    float t1 = (bmin - origin) * invD;
                    float t2 = (bmax - origin) * invD;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }

            distance = tmin >= 0 ? tmin : tmax;
            return distance >= 0;
        }

        /// <summary>
        /// 释放内部缓存资源。在域重载前调用。
        /// </summary>
        public static void Cleanup()
        {
            if (_skinnedBakeMesh != null)
            {
                Object.DestroyImmediate(_skinnedBakeMesh);
                _skinnedBakeMesh = null;
            }
        }

        // ---- 内部方法 ----

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is MeshRenderer mr)
                return mr.GetComponent<MeshFilter>()?.sharedMesh;

            if (renderer is SkinnedMeshRenderer smr)
            {
                if (_skinnedBakeMesh == null)
                {
                    _skinnedBakeMesh = new Mesh();
                    _skinnedBakeMesh.name = "_ModelBox_BakeMesh";
                    _skinnedBakeMesh.hideFlags = HideFlags.HideAndDontSave;
                }
                smr.BakeMesh(_skinnedBakeMesh);
                return _skinnedBakeMesh;
            }

            return null;
        }

        /// <summary>
        /// Möller-Trumbore 射线-三角形相交算法。
        /// 返回射线参数 t 和重心坐标 (u, v)，命中时 t > 0。
        /// </summary>
        private static bool MollerTrumbore(
            Vector3 origin, Vector3 dir,
            Vector3 v0, Vector3 v1, Vector3 v2,
            out float t, out float u, out float v)
        {
            t = u = v = 0;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 h = Vector3.Cross(dir, edge2);
            float a = Vector3.Dot(edge1, h);

            // 射线与三角形平行
            if (a > -Epsilon && a < Epsilon)
                return false;

            float f = 1f / a;
            Vector3 s = origin - v0;
            u = f * Vector3.Dot(s, h);
            if (u < 0f || u > 1f)
                return false;

            Vector3 q = Vector3.Cross(s, edge1);
            v = f * Vector3.Dot(dir, q);
            if (v < 0f || u + v > 1f)
                return false;

            t = f * Vector3.Dot(edge2, q);
            return t > Epsilon;
        }
    }
}
