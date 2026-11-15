using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ModelBox.Tests
{
    [TestFixture]
    public class MeshRaycastTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private static readonly Vector3 Weights = new Vector3(0.2f, 0.3f, 0.5f);

        [TearDown]
        public void TearDown()
        {
            MeshRaycastUtility.Cleanup();
            foreach (var obj in _objects) Object.DestroyImmediate(obj);
            _objects.Clear();
        }

        [Test]
        public void Raycast_SmoothNormalMatchesNormalizedVertexNormalInterpolation()
        {
            var mesh = CreateMesh();
            var renderer = CreateRenderer(mesh);
            SetTransform(renderer.transform);
            var matrix = renderer.transform.localToWorldMatrix;
            var normalMatrix = renderer.transform.worldToLocalMatrix.transpose;
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var point = matrix.MultiplyPoint3x4(Interpolate(vertices));
            var face = Vector3.Cross(matrix.MultiplyVector(vertices[1] - vertices[0]),
                matrix.MultiplyVector(vertices[2] - vertices[0])).normalized;
            var ray = new Ray(point + face * 5f, -face);
            var expected = (normalMatrix.MultiplyVector(normals[0]).normalized * Weights.x
                + normalMatrix.MultiplyVector(normals[1]).normalized * Weights.y
                + normalMatrix.MultiplyVector(normals[2]).normalized * Weights.z).normalized;

            Assert.IsTrue(MeshRaycastUtility.Raycast(ray, renderer, out var hit));

            Assert.Less(Vector3.Distance(point, hit.point), 1e-4f);
            Assert.Less(Vector3.Distance(expected, hit.normal), 1e-4f,
                "Each vertex normal must use inverse transpose and normalize before interpolation, as the geometry shader does.");
            Assert.AreEqual(5f, hit.distance, 1e-4f);
            Assert.Less(Vector2.Distance(new Vector2(Weights.y, Weights.z), hit.barycentric), 1e-4f);
        }

        [Test]
        public void Raycast_WithoutNormalsUsesInverseTransposeFaceNormal()
        {
            var mesh = CreateMesh();
            mesh.normals = new Vector3[0];
            var renderer = CreateRenderer(mesh);
            SetTransform(renderer.transform);
            var vertices = mesh.vertices;
            var localFace = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]).normalized;
            var expected = renderer.transform.worldToLocalMatrix.transpose.MultiplyVector(localFace).normalized;
            var point = renderer.transform.TransformPoint(Interpolate(vertices));

            Assert.IsTrue(MeshRaycastUtility.Raycast(new Ray(point + expected * 5f, -expected), renderer, out var hit));

            Assert.Less(Vector3.Distance(expected, hit.normal), 1e-4f);
        }

        [Test]
        public void Raycast_HitCarriesLocalAndGeometricWorldNormals()
        {
            var mesh = CreateMesh();
            var renderer = CreateRenderer(mesh);
            SetTransform(renderer.transform);
            var matrix = renderer.transform.localToWorldMatrix;
            var vertices = mesh.vertices;
            var point = matrix.MultiplyPoint3x4(Interpolate(vertices));
            var face = Vector3.Cross(matrix.MultiplyVector(vertices[1] - vertices[0]),
                matrix.MultiplyVector(vertices[2] - vertices[0])).normalized;
            Assert.IsTrue(MeshRaycastUtility.Raycast(new Ray(point + face * 5f, -face), renderer, out var hit));

            var localField = typeof(MeshRaycastUtility.MeshRayHit).GetField("localNormal");
            var flatField = typeof(MeshRaycastUtility.MeshRayHit).GetField("flatNormal");
            Assert.IsNotNull(localField, "The hit must carry local normal data without exposing a reusable baked mesh.");
            Assert.IsNotNull(flatField, "The hit must carry the geometric world face normal.");
            Assert.Less(Vector3.Distance(Interpolate(mesh.normals).normalized,
                (Vector3)localField.GetValue(hit)), 1e-4f);
            Assert.Less(Vector3.Distance(face, (Vector3)flatField.GetValue(hit)), 1e-4f);
        }

        [TestCase(2f)]
        [TestCase(0.01f)]
        [TestCase(-1f)]
        public void Raycast_SkinnedPoseMatchesAnimatedBoneUnderParentScale(float parentScale)
        {
            var mesh = CreateMesh();
            var root = Track(new GameObject("RaycastScaledRoot"));
            root.transform.SetPositionAndRotation(new Vector3(3f, 2f, -1f), Quaternion.Euler(90f, 30f, 0f));
            root.transform.localScale = new Vector3(1f, 0.5f, 1.5f) * parentScale;
            var rendererObject = new GameObject("SkinnedRenderer", typeof(SkinnedMeshRenderer));
            rendererObject.transform.SetParent(root.transform, false);
            rendererObject.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            rendererObject.transform.localScale = Vector3.one * 0.5f;
            var bone = new GameObject("AnimatedBone").transform;
            bone.SetParent(root.transform, false);
            var bindpose = bone.worldToLocalMatrix * rendererObject.transform.localToWorldMatrix;
            mesh.bindposes = new[] { bindpose };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f }
            };
            var renderer = rendererObject.GetComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;
            bone.localPosition = new Vector3(0.25f, 0.5f, 0f);
            bone.localRotation = Quaternion.Euler(0f, 0f, 25f);
            var skinMatrix = bone.localToWorldMatrix * bindpose;
            var vertices = mesh.vertices;
            var expected = skinMatrix.MultiplyPoint3x4(Interpolate(vertices));
            var face = Vector3.Cross(skinMatrix.MultiplyVector(vertices[1] - vertices[0]),
                skinMatrix.MultiplyVector(vertices[2] - vertices[0])).normalized;

            Assert.IsTrue(MeshRaycastUtility.Raycast(new Ray(expected + face * 5f, -face), renderer, out var hit),
                "The ray through the animated triangle must hit for every supported parent scale.");

            Assert.Less(Vector3.Distance(expected, hit.point), 1e-4f);
            Assert.Less(Vector2.Distance(new Vector2(Weights.y, Weights.z), hit.barycentric), 1e-4f);
        }

        [Test]
        public void PickRenderers_RespectsCameraCullingMaskForBothRendererTypes()
        {
            var mesh = CreateMesh();
            var visible = CreateRenderer(mesh);
            visible.transform.position = new Vector3(1300f, 1700f, 0f);
            visible.gameObject.layer = 30;
            var hidden = CreateRenderer(mesh);
            hidden.transform.position = visible.transform.position - Vector3.forward * 4f;
            hidden.gameObject.layer = 31;
            var hiddenSkin = Track(new GameObject("CulledSkinnedRenderer", typeof(SkinnedMeshRenderer)))
                .GetComponent<SkinnedMeshRenderer>();
            hiddenSkin.sharedMesh = mesh;
            hiddenSkin.localBounds = mesh.bounds;
            hiddenSkin.transform.position = visible.transform.position - Vector3.forward * 2f;
            hiddenSkin.gameObject.layer = 31;
            var camera = Track(new GameObject("RaycastCullingCamera", typeof(Camera))).GetComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 1 << 30;
            camera.farClipPlane = 100f;
            var ray = new Ray(new Vector3(1300f, 1700f, -20f), Vector3.forward);

            var candidates = MeshRaycastUtility.PickNearestRenderers(ray, camera);

            CollectionAssert.Contains(candidates, visible);
            CollectionAssert.DoesNotContain(candidates, hidden);
            CollectionAssert.DoesNotContain(candidates, hiddenSkin);
            Assert.AreSame(visible, MeshRaycastUtility.PickNearestRenderer(ray, camera));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PickRenderers_VisibleOffAxisMeshCanBeFartherThanFarClipDistance(bool skinned)
        {
            var mesh = CreateMesh();
            Renderer renderer;
            if (skinned)
            {
                var skin = Track(new GameObject("OffAxisSkinnedRenderer", typeof(SkinnedMeshRenderer)))
                    .GetComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh;
                skin.localBounds = mesh.bounds;
                renderer = skin;
            }
            else renderer = CreateRenderer(mesh);
            renderer.gameObject.layer = 30;
            var camera = Track(new GameObject("OffAxisCamera", typeof(Camera))).GetComponent<Camera>();
            camera.enabled = false;
            camera.transform.position = new Vector3(1300f, 1700f, 0f);
            camera.fieldOfView = 90f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 100f;
            camera.cullingMask = 1 << 30;
            var point = camera.transform.position + new Vector3(72f, 0f, 90f);
            renderer.transform.position = point - Interpolate(mesh.vertices);
            var ray = new Ray(camera.transform.position, (point - camera.transform.position).normalized);
            Assert.AreEqual(90f, Vector3.Dot(point - camera.transform.position, camera.transform.forward), 1e-4f);
            Assert.Greater(Vector3.Distance(point, camera.transform.position), camera.farClipPlane);

            var candidates = MeshRaycastUtility.PickNearestRenderers(ray, camera);

            CollectionAssert.Contains(candidates, renderer,
                "A visible surface at eye depth 90 must remain a candidate when far clip is 100.");
            Assert.AreSame(renderer, MeshRaycastUtility.PickNearestRenderer(ray, camera));
        }

        private Mesh CreateMesh()
        {
            return Track(new Mesh
            {
                vertices = new[] { new Vector3(-1f, -1f, -0.2f), new Vector3(1f, -1f, 0.6f), new Vector3(0f, 1f, 0.4f) },
                triangles = new[] { 0, 1, 2 },
                normals = new[] { new Vector3(1f, 0f, 1f).normalized, new Vector3(0f, 1f, 1f).normalized,
                    new Vector3(0.3f, 0.4f, 1f).normalized },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up }
            });
        }

        private MeshRenderer CreateRenderer(Mesh mesh)
        {
            var go = Track(new GameObject("RaycastRenderer", typeof(MeshFilter), typeof(MeshRenderer)));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            return go.GetComponent<MeshRenderer>();
        }

        private static void SetTransform(Transform transform)
        {
            transform.SetPositionAndRotation(new Vector3(3f, 2f, -4f), Quaternion.Euler(20f, 35f, -10f));
            transform.localScale = new Vector3(2f, 0.5f, 1.25f);
        }

        private static Vector3 Interpolate(Vector3[] values)
        {
            return values[0] * Weights.x + values[1] * Weights.y + values[2] * Weights.z;
        }

        private T Track<T>(T obj) where T : Object
        {
            _objects.Add(obj);
            return obj;
        }
    }
}
