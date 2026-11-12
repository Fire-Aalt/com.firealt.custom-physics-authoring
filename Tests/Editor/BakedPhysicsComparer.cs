using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics.GraphicsIntegration;
using Unity.Transforms;

namespace Unity.Physics.Editor.Tests
{
    /// <summary> Options for differences that a conversion is known to introduce. </summary>
    struct CompareOptions
    {
        /// <summary> A custom mass distribution also widens the angular expansion factor. </summary>
        public bool IgnoreAngularExpansion;

        /// <summary> Skips joint comparison, for mappings that change the joint structure. </summary>
        public bool IgnoreJoints;

        /// <summary> Skips joint spring frequency and damping ratio. </summary>
        public bool IgnoreJointSprings;
    }

    /// <summary> Asserts that two baked worlds contain equivalent physics bodies and joints. </summary>
    static unsafe class BakedPhysicsComparer
    {
        const float TOLERANCE = 1e-4f;

        internal static void AssertEquivalent(World expected, World actual, CompareOptions options = default)
        {
            var expectedBodies = GetBodies(expected.EntityManager);
            var actualBodies = GetBodies(actual.EntityManager);
            Assert.That(actualBodies.Count, Is.EqualTo(expectedBodies.Count), "Body count");

            for (var i = 0; i < expectedBodies.Count; ++i)
                AssertBody(expected.EntityManager, expectedBodies[i], actual.EntityManager, actualBodies[i],
                    $"Body {i}",
                    options);

            if (options.IgnoreJoints)
                return;

            var expectedJoints = GetJoints(expected.EntityManager);
            var actualJoints = GetJoints(actual.EntityManager);
            Assert.That(actualJoints.Count, Is.EqualTo(expectedJoints.Count), "Joint count");

            for (var i = 0; i < expectedJoints.Count; ++i)
            {
                AssertJoint(expected.EntityManager, expectedJoints[i], expectedBodies,
                    actual.EntityManager, actualJoints[i],
                    actualBodies, $"Joint {i}", options);
            }
        }

        /// <summary> Bodies in a stable order: by position, so both worlds match when the hierarchies match. </summary>
        static List<Entity> GetBodies(EntityManager manager)
        {
            using var query = manager.CreateEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<PhysicsCollider>(), ComponentType.ReadOnly<PhysicsMass>() },
                None = new[] { ComponentType.ReadOnly<PhysicsJoint>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.ToArray()
                .OrderBy(entity => GetPosition(manager, entity).x)
                .ThenBy(entity => GetPosition(manager, entity).y)
                .ThenBy(entity => GetPosition(manager, entity).z)
                .ToList();
        }

        static List<Entity> GetJoints(EntityManager manager)
        {
            using var query = manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PhysicsJoint>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.ToArray()
                .OrderBy(entity => (int)manager.GetComponentData<PhysicsJoint>(entity).JointType)
                .ThenBy(entity => manager.GetComponentData<PhysicsJoint>(entity).BodyAFromJoint.Position.x)
                .ToList();
        }

        static float3 GetPosition(EntityManager manager, Entity entity) =>
            manager.HasComponent<LocalToWorld>(entity)
                ? manager.GetComponentData<LocalToWorld>(entity).Position
                : float3.zero;

        static void AssertBody(
            EntityManager expectedManager, Entity expected, EntityManager actualManager, Entity actual,
            string label, CompareOptions options)
        {
            AssertSamePresence<PhysicsCollider>(expectedManager, expected, actualManager, actual, label);
            if (expectedManager.HasComponent<PhysicsCollider>(expected))
            {
                var expectedCollider = expectedManager.GetComponentData<PhysicsCollider>(expected);
                var actualCollider = actualManager.GetComponentData<PhysicsCollider>(actual);
                Assert.That(actualCollider.IsValid, Is.EqualTo(expectedCollider.IsValid), $"{label} collider validity");
                if (expectedCollider.IsValid)
                    AssertCollider(expectedCollider.ColliderPtr, actualCollider.ColliderPtr, $"{label} collider");
            }

            AssertTransform(expectedManager, expected, actualManager, actual, label);

            AssertSamePresence<PhysicsMass>(expectedManager, expected, actualManager, actual, label);
            if (expectedManager.HasComponent<PhysicsMass>(expected))
            {
                var e = expectedManager.GetComponentData<PhysicsMass>(expected);
                var a = actualManager.GetComponentData<PhysicsMass>(actual);
                AssertClose(a.InverseMass, e.InverseMass, $"{label} inverse mass");
                AssertClose(a.InverseInertia, e.InverseInertia, $"{label} inverse inertia");
                AssertClose(a.Transform.pos, e.Transform.pos, $"{label} center of mass");
                AssertRotation(a.Transform.rot, e.Transform.rot, $"{label} inertia orientation");
                if (!options.IgnoreAngularExpansion)
                    AssertClose(a.AngularExpansionFactor, e.AngularExpansionFactor, $"{label} angular expansion");
            }

            AssertSamePresence<PhysicsVelocity>(expectedManager, expected, actualManager, actual, label);
            if (expectedManager.HasComponent<PhysicsVelocity>(expected))
            {
                var e = expectedManager.GetComponentData<PhysicsVelocity>(expected);
                var a = actualManager.GetComponentData<PhysicsVelocity>(actual);
                AssertClose(a.Linear, e.Linear, $"{label} linear velocity");
                AssertClose(a.Angular, e.Angular, $"{label} angular velocity");
            }

            AssertSamePresence<PhysicsDamping>(expectedManager, expected, actualManager, actual, label);
            if (expectedManager.HasComponent<PhysicsDamping>(expected))
            {
                var e = expectedManager.GetComponentData<PhysicsDamping>(expected);
                var a = actualManager.GetComponentData<PhysicsDamping>(actual);
                AssertClose(a.Linear, e.Linear, $"{label} linear damping");
                AssertClose(a.Angular, e.Angular, $"{label} angular damping");
            }

            AssertClose(GetGravityFactor(actualManager, actual), GetGravityFactor(expectedManager, expected),
                $"{label} gravity factor");
            Assert.That(GetSolverType(actualManager, actual), Is.EqualTo(GetSolverType(expectedManager, expected)),
                $"{label} solver type");
            Assert.That(GetWorldIndex(actualManager, actual), Is.EqualTo(GetWorldIndex(expectedManager, expected)),
                $"{label} world index");
            AssertSamePresence<PhysicsGraphicalSmoothing>(expectedManager, expected, actualManager, actual, label);
            AssertSamePresence<PhysicsGraphicalInterpolationBuffer>(
                expectedManager, expected, actualManager, actual, label);
        }

        static void AssertTransform(
            EntityManager expectedManager, Entity expected, EntityManager actualManager, Entity actual,
            string label)
        {
            AssertSamePresence<LocalTransform>(expectedManager, expected, actualManager, actual, label);
            if (expectedManager.HasComponent<LocalTransform>(expected))
            {
                var e = expectedManager.GetComponentData<LocalTransform>(expected);
                var a = actualManager.GetComponentData<LocalTransform>(actual);
                AssertClose(a.Position, e.Position, $"{label} position");
                AssertRotation(a.Rotation, e.Rotation, $"{label} rotation");
                AssertClose(a.Scale, e.Scale, $"{label} scale");
            }

            AssertSamePresence<PostTransformMatrix>(expectedManager, expected, actualManager, actual, label);
        }

        static float GetGravityFactor(EntityManager manager, Entity entity) =>
            manager.HasComponent<PhysicsGravityFactor>(entity)
                ? manager.GetComponentData<PhysicsGravityFactor>(entity).Value
                : 1f;

        static SolverType GetSolverType(EntityManager manager, Entity entity) =>
            manager.HasComponent<PhysicsSolverType>(entity)
                ? manager.GetComponentData<PhysicsSolverType>(entity).Value
                : SolverType.Iterative;

        static int GetWorldIndex(EntityManager manager, Entity entity) =>
            manager.HasComponent<PhysicsWorldIndex>(entity)
                ? (int)manager.GetSharedComponent<PhysicsWorldIndex>(entity).Value
                : -1;

        static void AssertSamePresence<T>(
            EntityManager expectedManager, Entity expected, EntityManager actualManager, Entity actual,
            string label)
        {
            Assert.That(actualManager.HasComponent<T>(actual), Is.EqualTo(expectedManager.HasComponent<T>(expected)),
                $"{label} has {typeof(T).Name}");
        }

        // ---- Colliders ------------------------------------------------------------------------------------------

        static void AssertCollider(Collider* expected, Collider* actual, string label)
        {
            Assert.That(actual->Type, Is.EqualTo(expected->Type), $"{label} type");
            Assert.That(actual->CollisionType, Is.EqualTo(expected->CollisionType), $"{label} collision type");

            if (expected->Type == ColliderType.Compound)
            {
                var e = (CompoundCollider*)expected;
                var a = (CompoundCollider*)actual;
                Assert.That(a->NumChildren, Is.EqualTo(e->NumChildren), $"{label} child count");

                // Children may be ordered differently, so match each expected child by its transform and type.
                // Children must be accessed by reference, because their collider pointer is a relative blob offset.
                var unmatched = Enumerable.Range(0, a->NumChildren).ToList();
                for (var i = 0; i < e->NumChildren; ++i)
                {
                    ref var expectedChild = ref e->Children[i];
                    var match = -1;
                    for (var j = 0; j < unmatched.Count && match < 0; ++j)
                    {
                        ref var candidate = ref a->Children[unmatched[j]];
                        var expectedPosition = expectedChild.CompoundFromChild.pos;
                        var distance = math.distance(candidate.CompoundFromChild.pos, expectedPosition);
                        if (distance < TOLERANCE &&
                            candidate.Collider->Type == expectedChild.Collider->Type)
                            match = j;
                    }

                    Assert.That(match, Is.GreaterThanOrEqualTo(0), $"{label} child {i} has a match");

                    ref var actualChild = ref a->Children[unmatched[match]];
                    unmatched.RemoveAt(match);
                    var childLabel = $"{label} child {i}";
                    AssertRotation(actualChild.CompoundFromChild.rot, expectedChild.CompoundFromChild.rot,
                        $"{childLabel} rotation");
                    AssertCollider(expectedChild.Collider, actualChild.Collider, childLabel);
                }

                return;
            }

            var expectedFilter = expected->GetCollisionFilter();
            var actualFilter = actual->GetCollisionFilter();
            Assert.That(actualFilter.BelongsTo, Is.EqualTo(expectedFilter.BelongsTo), $"{label} belongs to");
            Assert.That(actualFilter.CollidesWith, Is.EqualTo(expectedFilter.CollidesWith), $"{label} collides with");
            Assert.That(actualFilter.GroupIndex, Is.EqualTo(expectedFilter.GroupIndex), $"{label} group index");

            AssertClose(actual->GetFriction(), expected->GetFriction(), $"{label} friction");
            AssertClose(actual->GetRestitution(), expected->GetRestitution(), $"{label} restitution");
            Assert.That(actual->GetCollisionResponse(), Is.EqualTo(expected->GetCollisionResponse()),
                $"{label} response");

            if (IsConvex(expected->Type))
            {
                var e = ((ConvexCollider*)expected)->Material;
                var a = ((ConvexCollider*)actual)->Material;
                Assert.That(a.FrictionCombinePolicy, Is.EqualTo(e.FrictionCombinePolicy), $"{label} friction combine");
                Assert.That(a.RestitutionCombinePolicy, Is.EqualTo(e.RestitutionCombinePolicy),
                    $"{label} restitution combine");
                Assert.That(a.CustomTags, Is.EqualTo(e.CustomTags), $"{label} custom tags");
                Assert.That(a.EnableDetailedStaticMeshCollision, Is.EqualTo(e.EnableDetailedStaticMeshCollision),
                    $"{label} detailed static mesh collision");
            }

            var expectedAabb = expected->CalculateAabb();
            var actualAabb = actual->CalculateAabb();
            AssertClose(actualAabb.Min, expectedAabb.Min, $"{label} AABB min");
            AssertClose(actualAabb.Max, expectedAabb.Max, $"{label} AABB max");

            var expectedMass = expected->MassProperties;
            var actualMass = actual->MassProperties;
            AssertClose(actualMass.Volume, expectedMass.Volume, $"{label} volume", relative: true);
            AssertClose(actualMass.MassDistribution.Transform.pos, expectedMass.MassDistribution.Transform.pos,
                $"{label} center");

            switch (expected->Type)
            {
                case ColliderType.Box:
                {
                    var e = ((BoxCollider*)expected)->Geometry;
                    var a = ((BoxCollider*)actual)->Geometry;
                    AssertClose(a.Center, e.Center, $"{label} box center");
                    AssertClose(a.BevelRadius, e.BevelRadius, $"{label} box bevel radius");
                    break;
                }

                case ColliderType.Sphere:
                {
                    var e = ((SphereCollider*)expected)->Geometry;
                    var a = ((SphereCollider*)actual)->Geometry;
                    AssertClose(a.Center, e.Center, $"{label} sphere center");
                    AssertClose(a.Radius, e.Radius, $"{label} sphere radius");
                    break;
                }

                case ColliderType.Capsule:
                {
                    var e = ((CapsuleCollider*)expected)->Geometry;
                    var a = ((CapsuleCollider*)actual)->Geometry;
                    AssertClose(a.Radius, e.Radius, $"{label} capsule radius");
                    var sameOrder = math.distance(a.Vertex0, e.Vertex0) <= math.distance(a.Vertex0, e.Vertex1);
                    AssertClose(sameOrder ? a.Vertex0 : a.Vertex1, e.Vertex0, $"{label} capsule vertex 0");
                    AssertClose(sameOrder ? a.Vertex1 : a.Vertex0, e.Vertex1, $"{label} capsule vertex 1");
                    break;
                }
            }
        }

        static bool IsConvex(ColliderType type) =>
            type == ColliderType.Box || type == ColliderType.Sphere || type == ColliderType.Capsule ||
            type == ColliderType.Cylinder || type == ColliderType.Convex;

        // ---- Joints ---------------------------------------------------------------------------------------------

        static void AssertJoint(EntityManager expectedManager, Entity expected, List<Entity> expectedBodies,
            EntityManager actualManager, Entity actual, List<Entity> actualBodies, string label, CompareOptions options)
        {
            var e = expectedManager.GetComponentData<PhysicsJoint>(expected);
            var a = actualManager.GetComponentData<PhysicsJoint>(actual);
            Assert.That(a.JointType, Is.EqualTo(e.JointType), $"{label} type");
            AssertClose(a.BodyAFromJoint.Position, e.BodyAFromJoint.Position, $"{label} position A");
            AssertClose(a.BodyBFromJoint.Position, e.BodyBFromJoint.Position, $"{label} position B");
            AssertClose(a.BodyAFromJoint.Axis, e.BodyAFromJoint.Axis, $"{label} axis A");
            AssertClose(a.BodyBFromJoint.Axis, e.BodyBFromJoint.Axis, $"{label} axis B");

            var expectedConstraints = e.GetConstraints();
            var actualConstraints = a.GetConstraints();
            Assert.That(actualConstraints.Length, Is.EqualTo(expectedConstraints.Length), $"{label} constraint count");
            for (var i = 0; i < expectedConstraints.Length; ++i)
            {
                var ec = expectedConstraints[i];
                var ac = actualConstraints[i];
                var constraintLabel = $"{label} constraint {i}";
                Assert.That(ac.Type, Is.EqualTo(ec.Type), $"{constraintLabel} type");
                Assert.That(ac.ConstrainedAxes, Is.EqualTo(ec.ConstrainedAxes), $"{constraintLabel} axes");
                AssertClose(ac.Min, ec.Min, $"{constraintLabel} min");
                AssertClose(ac.Max, ec.Max, $"{constraintLabel} max");
                AssertClose(ac.MaxImpulse, ec.MaxImpulse, $"{constraintLabel} max impulse");
                AssertClose(ac.Target, ec.Target, $"{constraintLabel} target");
                if (!options.IgnoreJointSprings)
                {
                    AssertClose(ac.SpringFrequency, ec.SpringFrequency, $"{constraintLabel} spring frequency",
                        relative: true);
                    AssertClose(ac.DampingRatio, ec.DampingRatio, $"{constraintLabel} damping ratio", relative: true);
                }
            }

            var expectedPair = expectedManager.GetComponentData<PhysicsConstrainedBodyPair>(expected);
            var actualPair = actualManager.GetComponentData<PhysicsConstrainedBodyPair>(actual);
            Assert.That(actualPair.EnableCollision, Is.EqualTo(expectedPair.EnableCollision),
                $"{label} enable collision");
            var expectedBodyA = expectedBodies.IndexOf(expectedPair.EntityA);
            Assert.That(actualBodies.IndexOf(actualPair.EntityA), Is.EqualTo(expectedBodyA),
                $"{label} body A");
            var expectedBodyB = expectedBodies.IndexOf(expectedPair.EntityB);
            Assert.That(actualBodies.IndexOf(actualPair.EntityB), Is.EqualTo(expectedBodyB),
                $"{label} body B");
            Assert.That(GetSolverType(actualManager, actual), Is.EqualTo(GetSolverType(expectedManager, expected)),
                $"{label} solver type");
            Assert.That(GetWorldIndex(actualManager, actual), Is.EqualTo(GetWorldIndex(expectedManager, expected)),
                $"{label} world index");
        }

        // ---- Value comparison -----------------------------------------------------------------------------------

        static void AssertClose(float actual, float expected, string label, bool relative = false)
        {
            if (float.IsInfinity(expected) || float.IsNaN(expected))
            {
                Assert.That(actual, Is.EqualTo(expected), label);
                return;
            }

            var tolerance = relative ? TOLERANCE * math.max(1f, math.abs(expected)) : TOLERANCE;
            Assert.That(actual, Is.EqualTo(expected).Within(tolerance), label);
        }

        static void AssertClose(float3 actual, float3 expected, string label)
        {
            for (var i = 0; i < 3; ++i)
                AssertClose(actual[i], expected[i], $"{label}[{i}]", relative: true);
        }

        static void AssertRotation(quaternion actual, quaternion expected, string label)
        {
            var dot = math.abs(math.dot(math.normalizesafe(actual).value, math.normalizesafe(expected).value));
            Assert.That(dot, Is.GreaterThan(1f - TOLERANCE), $"{label}: expected {expected}, but was {actual}");
        }
    }
}
