using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;

namespace Unity.Physics.Editor
{
    // FireAlt: render geometry sources and fitting for the PhysX-style "Fit to Self" and "Fit to Children" buttons.
    // "Fit to Children" uses the upstream PhysicsShapeAuthoring.FitToEnabledRenderMeshes() unchanged, so a child only
    // counts when this shape is its nearest ancestor shape. "Fit to Self" only uses this GameObject's own mesh and
    // skinned vertices weighted to this GameObject's transform.
    [NoAutoStaticsCleanup]
    static class PhysicsShapeFitUtility
    {
        [Flags]
        internal enum FitSources
        {
            None = 0,
            SelfMesh = 1 << 0,
            SelfSkinned = 1 << 1,
            ChildMesh = 1 << 2,
            ChildSkinned = 1 << 3,
            Self = SelfMesh | SelfSkinned,
            Children = ChildMesh | ChildSkinned,
            Skinned = SelfSkinned | ChildSkinned,
        }

        const string MINIMUM_SKINNED_VERTEX_WEIGHT_PROPERTY = "m_MinimumSkinnedVertexWeight";

        static readonly HashSet<Transform> s_ShapeTransforms = new HashSet<Transform>();
        static readonly List<Vector3> s_Vertices = new List<Vector3>(4096);
        static UnityEngine.Mesh s_BakeMesh;

        /// <summary> Finds which render geometry the fit buttons can use, without reading any vertices. </summary>
        internal static FitSources GetSources(PhysicsShapeAuthoring shape)
        {
            var sources = FitSources.None;
            var self = shape.transform;

            using (var scope = new GetActiveChildrenScope<MeshFilter>(shape, self))
            {
                foreach (var meshFilter in scope.Buffer)
                {
                    if (scope.IsChildActiveAndBelongsToShape(meshFilter, false))
                        sources |= meshFilter.transform == self ? FitSources.SelfMesh : FitSources.ChildMesh;
                }
            }

            CollectShapeTransforms(shape);
            using (var scope = new GetActiveChildrenScope<SkinnedMeshRenderer>(shape, self.root))
            {
                foreach (var skin in scope.Buffer)
                {
                    if (!skin.enabled || skin.sharedMesh == null || !scope.IsChildActiveAndBelongsToShape(skin))
                        continue;

                    foreach (var bone in skin.bones)
                    {
                        if (bone == self)
                            sources |= FitSources.SelfSkinned;
                        else if (bone != null && s_ShapeTransforms.Contains(bone))
                            sources |= FitSources.ChildSkinned;
                    }
                }
            }

            s_ShapeTransforms.Clear();
            return sources;
        }

        /// <summary> Fits the primitive shape to its own render geometry, or to that of its hierarchy. </summary>
        internal static void Fit(PhysicsShapeAuthoring shape, float minimumSkinnedVertexWeight, bool includeChildren)
        {
            if (includeChildren)
            {
                shape.FitToEnabledRenderMeshes(minimumSkinnedVertexWeight);
                return;
            }

            using (var so = new SerializedObject(shape))
            {
                so.FindProperty(MINIMUM_SKINNED_VERTEX_WEIGHT_PROPERTY).floatValue = minimumSkinnedVertexWeight;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var points = new NativeList<float3>(1024, Allocator.Temp);
            GetSelfPoints(shape, minimumSkinnedVertexWeight, points);
            if (points.Length == 0)
            {
                points.Dispose();
                return;
            }

            var bounds = new Bounds(points[0], Vector3.zero);
            for (var i = 1; i < points.Length; ++i)
                bounds.Encapsulate(points[i]);
            points.Dispose();

            var shapeType = shape.ShapeType;
            shape.SetBox(new BoxGeometry
            {
                Center = bounds.center,
                Size = bounds.size,
                Orientation = quaternion.identity,
                BevelRadius = shape.ConvexHullGenerationParameters.BevelRadius,
            });

            // Same conversion back from the fitted box as PhysicsShapeAuthoring.FitToEnabledRenderMeshes().
            EulerAngles orientation;
            switch (shapeType)
            {
                case ShapeType.Capsule:
                    shape.SetCapsule(shape.GetCapsuleProperties());
                    break;
                case ShapeType.Cylinder:
                    shape.SetCylinder(shape.GetCylinderProperties(out orientation), orientation);
                    break;
                case ShapeType.Sphere:
                    shape.SetSphere(shape.GetSphereProperties(out orientation), orientation);
                    break;
                case ShapeType.Plane:
                    shape.GetPlaneProperties(out var center, out var size2D, out orientation);
                    shape.SetPlane(center, size2D, orientation);
                    break;
            }
        }

        static void GetSelfPoints(PhysicsShapeAuthoring shape, float minimumSkinnedVertexWeight, NativeList<float3> points)
        {
            var self = shape.transform;

            using (var scope = new GetActiveChildrenScope<MeshFilter>(shape, self))
            {
                foreach (var meshFilter in scope.Buffer)
                {
                    if (meshFilter.transform == self && scope.IsChildActiveAndBelongsToShape(meshFilter, false))
                    {
                        PhysicsShapeAuthoring.AppendMeshPropertiesToNativeBuffers(
                            float4x4.identity, meshFilter.sharedMesh, points, default, default, null);
                    }
                }
            }

            using (var scope = new GetActiveChildrenScope<SkinnedMeshRenderer>(shape, self.root))
            {
                foreach (var skin in scope.Buffer)
                {
                    var mesh = skin.sharedMesh;
                    if (!skin.enabled || mesh == null || !scope.IsChildActiveAndBelongsToShape(skin))
                        continue;

                    var boneIndex = Array.IndexOf(skin.bones, self);
                    if (boneIndex < 0)
                        continue;

                    if (s_BakeMesh == null)
                        s_BakeMesh = new UnityEngine.Mesh { hideFlags = HideFlags.HideAndDontSave };

                    skin.BakeMesh(s_BakeMesh);
                    s_BakeMesh.GetVertices(s_Vertices);

                    var shapeFromSkin = math.mul(self.worldToLocalMatrix, skin.transform.localToWorldMatrix);
                    var bonesPerVertex = mesh.GetBonesPerVertex();
                    var weights = mesh.GetAllBoneWeights();
                    var weightsOffset = 0;
                    for (var vertexIndex = 0; vertexIndex < bonesPerVertex.Length; ++vertexIndex)
                    {
                        var weightCount = bonesPerVertex[vertexIndex];
                        var totalWeight = 0f;
                        for (var i = 0; i < weightCount; ++i)
                        {
                            var weight = weights[weightsOffset + i];
                            if (weight.boneIndex == boneIndex)
                                totalWeight += weight.weight;
                        }

                        if (totalWeight > minimumSkinnedVertexWeight)
                            points.Add(math.mul(shapeFromSkin, new float4(s_Vertices[vertexIndex], 1f)).xyz);

                        weightsOffset += weightCount;
                    }
                }
            }

            s_Vertices.Clear();
        }

        static void CollectShapeTransforms(PhysicsShapeAuthoring shape)
        {
            s_ShapeTransforms.Clear();
            using var scope = new GetActiveChildrenScope<Transform>(shape, shape.transform);
            foreach (var t in scope.Buffer)
            {
                if (scope.IsChildActiveAndBelongsToShape(t))
                    s_ShapeTransforms.Add(t);
            }
        }
    }
}
