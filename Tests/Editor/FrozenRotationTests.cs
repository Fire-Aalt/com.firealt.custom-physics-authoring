using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEngine;
using Object = UnityEngine.Object;
using RangeAttribute = NUnit.Framework.RangeAttribute;

namespace Unity.Physics.Editor.Tests
{
    /// <summary> Checks the rotation freezes that are baked into <see cref="PhysicsMass"/>. </summary>
    class FrozenRotationTests
    {
        const float TOLERANCE = 1e-4f;

        static readonly quaternion s_InertiaOrientation = quaternion.Euler(0.3f, -0.5f, 0.8f);
        static readonly float3 s_Inertia = new float3(1f, 2.5f, 4f);

        static readonly quaternion[] s_BodyRotations =
        {
            quaternion.identity, quaternion.Euler(0.4f, 1.1f, -0.7f), quaternion.Euler(math.PI / 2f, 0f, 0f),
        };

        readonly List<Object> m_Objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in m_Objects)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }

            m_Objects.Clear();
        }

        /// <summary>
        /// For every freeze, the baked body-space inverse inertia must be the inverse of the inertia restricted to the
        /// free world axes: it maps I·f back to f for free directions f, and it is zero on frozen directions.
        /// </summary>
        [Test]
        public void Apply_MatchesInertiaRestrictedToFreeAxes(
            [Range(1, 7)] int frozenAxes, [Range(0, 2)] int rotationIndex)
        {
            var frozen = (RotationAxes)frozenAxes;
            var bodyRotation = s_BodyRotations[rotationIndex];
            var mass = CreateMass(s_InertiaOrientation, s_Inertia);
            var inertia = BodyMatrix(mass.InertiaOrientation, math.rcp(mass.InverseInertia));
            var bodyVelocity = new float3(0.5f, -1f, 2f);
            var angular = math.rotate(math.inverse(mass.InertiaOrientation), bodyVelocity);

            FrozenRotationUtility.Apply(ref mass, ref angular, frozen, bodyRotation);

            var inverse = BodyMatrix(mass.InertiaOrientation, mass.InverseInertia);
            var newBodyVelocity = math.rotate(mass.InertiaOrientation, angular);
            for (var i = 0; i < 3; ++i)
            {
                var axis = (RotationAxes)(1 << i);
                var direction = math.rotate(math.inverse(bodyRotation), Unit(i));
                if ((frozen & axis) != 0)
                {
                    AssertClose(math.mul(inverse, direction), float3.zero, $"{axis} frozen");
                    Assert.That(math.dot(newBodyVelocity, direction), Is.EqualTo(0f).Within(TOLERANCE), $"{axis} velocity");
                }
                else
                {
                    AssertClose(math.mul(inverse, math.mul(inertia, direction)), direction, $"{axis} free");
                    Assert.That(math.dot(newBodyVelocity, direction),
                        Is.EqualTo(math.dot(bodyVelocity, direction)).Within(TOLERANCE), $"{axis} velocity");
                }
            }
        }

        [Test]
        public void Apply_KeepsAlignedInertiaOrientation([Values(RotationAxes.Y, RotationAxes.X | RotationAxes.Z)] RotationAxes frozen)
        {
            var mass = CreateMass(quaternion.identity, s_Inertia);
            var unfrozen = mass.InverseInertia;
            var angular = new float3(1f, 2f, 3f);

            FrozenRotationUtility.Apply(ref mass, ref angular, frozen, quaternion.identity);

            var free = math.select(float3.zero, 1f, new bool3((frozen & RotationAxes.X) == 0,
                (frozen & RotationAxes.Y) == 0, (frozen & RotationAxes.Z) == 0));
            Assert.That(math.abs(math.dot(mass.InertiaOrientation.value, quaternion.identity.value)),
                Is.EqualTo(1f).Within(TOLERANCE));
            AssertClose(mass.InverseInertia, unfrozen * free, "inverse inertia");
            AssertClose(angular, new float3(1f, 2f, 3f) * free, "angular velocity");
        }

        [Test]
        public void Bake_AppliesFreezeToDynamicBodies([Values] BodyMotionType motionType)
        {
            var gameObject = Track(new GameObject("Body"));
            gameObject.transform.rotation = Quaternion.Euler(20f, 40f, 0f);
            var body = gameObject.AddComponent<PhysicsBodyAuthoring>();
            body.MotionType = motionType;
            body.InitialAngularVelocity = new float3(1f, 2f, 3f);
            body.FreezeRotation = RotationAxes.X | RotationAxes.Z;
            gameObject.AddComponent<PhysicsShapeAuthoring>().SetBox(new BoxGeometry
            {
                Size = new float3(1f, 2f, 3f), Orientation = quaternion.identity, BevelRadius = 0.01f,
            });

            using var store = new BlobAssetStore(128);
            using var world = BakingTestUtility.Bake(gameObject, store);
            using var query = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PhysicsMass>(), ComponentType.ReadOnly<PhysicsVelocity>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            if (motionType == BodyMotionType.Static)
            {
                Assert.That(entities.Length, Is.Zero);
                return;
            }

            Assert.That(entities.Length, Is.EqualTo(1));
            var mass = world.EntityManager.GetComponentData<PhysicsMass>(entities[0]);
            var velocity = world.EntityManager.GetComponentData<PhysicsVelocity>(entities[0]);
            if (motionType == BodyMotionType.Kinematic)
            {
                AssertClose(velocity.Angular, new float3(1f, 2f, 3f), "kinematic velocity is kept");
                return;
            }

            // Only rotation about world Y remains.
            var worldFromMotion = math.mul(gameObject.transform.rotation, mass.InertiaOrientation);
            Assert.That(math.countbits(math.bitmask(new bool4(mass.InverseInertia != 0f, false))), Is.EqualTo(1));
            var freeAxis = math.rotate(worldFromMotion, math.select(float3.zero, 1f, mass.InverseInertia != 0f));
            Assert.That(math.abs(freeAxis.y), Is.EqualTo(1f).Within(TOLERANCE));
            var worldVelocity = math.rotate(worldFromMotion, velocity.Angular);
            Assert.That(math.length(worldVelocity.xz), Is.EqualTo(0f).Within(TOLERANCE));
        }

        static PhysicsMass CreateMass(quaternion orientation, float3 inertia) =>
            PhysicsMass.CreateDynamic(new MassProperties
            {
                MassDistribution = new MassDistribution
                {
                    Transform = new RigidTransform(orientation, float3.zero), InertiaTensor = inertia,
                },
                Volume = 1f,
                AngularExpansionFactor = 0f,
            }, 2f);

        /// <summary> The body-space matrix with <paramref name="diagonal"/> along the given motion axes. </summary>
        static float3x3 BodyMatrix(quaternion orientation, float3 diagonal)
        {
            var axes = new float3x3(orientation);
            var scaled = new float3x3(axes.c0 * diagonal.x, axes.c1 * diagonal.y, axes.c2 * diagonal.z);
            return math.mul(scaled, math.transpose(axes));
        }

        static float3 Unit(int index) => index == 0 ? math.right() : index == 1 ? math.up() : math.forward();

        static void AssertClose(float3 actual, float3 expected, string label) =>
            Assert.That(math.distance(actual, expected), Is.LessThan(TOLERANCE),
                $"{label}: expected {expected}, but was {actual}");

        T Track<T>(T obj) where T : Object
        {
            m_Objects.Add(obj);
            return obj;
        }
    }
}
