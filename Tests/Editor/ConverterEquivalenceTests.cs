using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using Unity.Physics.Authoring;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityPhysicsMaterial = UnityEngine.PhysicsMaterial;

namespace Unity.Physics.Editor.Tests
{
    // Inside Unity.Physics these names would resolve to the ECS collider types.
    using BoxCollider = UnityEngine.BoxCollider;
    using CapsuleCollider = UnityEngine.CapsuleCollider;
    using MeshCollider = UnityEngine.MeshCollider;
    using SphereCollider = UnityEngine.SphereCollider;

    /// <summary>
    /// Builds PhysX authoring hierarchies, converts a copy, bakes both, and asserts that the baked data matches.
    /// </summary>
    class ConverterEquivalenceTests
    {
        sealed class InMemoryTemplates : IPhysicsMaterialTemplateProvider, IDisposable
        {
            readonly Dictionary<UnityPhysicsMaterial, PhysicsMaterialTemplate> m_Templates =
                new Dictionary<UnityPhysicsMaterial, PhysicsMaterialTemplate>();

            PhysicsMaterialTemplate m_Default;

            public PhysicsMaterialTemplate GetTemplate(UnityPhysicsMaterial material)
            {
                if (material == null)
                    return m_Default != null ? m_Default : m_Default = Create(null);

                if (!m_Templates.TryGetValue(material, out var template))
                    m_Templates.Add(material, template = Create(material));

                return template;
            }

            static PhysicsMaterialTemplate Create(UnityPhysicsMaterial material)
            {
                var template = ScriptableObject.CreateInstance<PhysicsMaterialTemplate>();
                PhysXAuthoringConverter.WriteTemplate(material, template);
                return template;
            }

            public void Dispose()
            {
                foreach (var template in m_Templates.Values)
                    Object.DestroyImmediate(template);

                if (m_Default != null)
                    Object.DestroyImmediate(m_Default);
            }
        }

        readonly List<Object> m_Objects = new List<Object>();
        InMemoryTemplates m_Templates;
        ConversionReport m_Report;

        [SetUp]
        public void SetUp()
        {
            m_Templates = new InMemoryTemplates();
            m_Report = new ConversionReport();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in m_Objects)
            {
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }

            m_Objects.Clear();
            m_Templates.Dispose();
        }

        // ---- Colliders ------------------------------------------------------------------------------------------

        [Test]
        public void Box_OnStaticRoot_MatchesPhysX([Values(1f, 2f)] float scale)
        {
            var root = Create("Box", Vector3.zero, Vector3.one * scale);
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0.1f, 0.2f, 0.3f);
            box.size = new Vector3(1f, 2f, 3f);

            AssertEquivalent(root);
        }

        [Test]
        public void Box_WithNonUniformScaleAndRotation_MatchesPhysX()
        {
            var rotation = Quaternion.Euler(10f, 20f, 30f);
            var root = Create("Box", new Vector3(1f, 2f, 3f), new Vector3(1f, 2f, 0.5f), rotation);
            var box = root.AddComponent<BoxCollider>();
            box.size = new Vector3(2f, 1f, 1f);

            AssertEquivalent(root);
        }

        [Test]
        public void Compound_OnDynamicBody_MatchesPhysX()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>();
            root.AddComponent<BoxCollider>();

            var child = Create("Child", new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f), Quaternion.Euler(0f, 45f, 0f),
                root.transform);
            child.AddComponent<SphereCollider>().radius = 0.25f;
            child.AddComponent<BoxCollider>().size = new Vector3(0.5f, 0.5f, 0.5f);

            AssertEquivalent(root);
        }

        [Test]
        public void Sphere_WithNonUniformScale_MatchesPhysX()
        {
            var root = Create("Sphere", Vector3.zero, new Vector3(1f, 3f, 2f));
            root.AddComponent<Rigidbody>();
            var sphere = root.AddComponent<SphereCollider>();
            sphere.center = new Vector3(0f, 0.5f, 0f);
            sphere.radius = 0.75f;

            AssertEquivalent(root);
        }

        [Test]
        public void Capsule_MatchesPhysX([Values(0, 1, 2)] int direction)
        {
            var root = Create("Capsule", Vector3.zero, new Vector3(1f, 1.5f, 2f));
            root.AddComponent<Rigidbody>();
            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.direction = direction;
            capsule.center = new Vector3(0.1f, 0f, 0f);
            capsule.radius = 0.3f;
            capsule.height = 2f;

            AssertEquivalent(root);
        }

        [Test]
        public void Mesh_MatchesPhysX([Values(true, false)] bool convex)
        {
            var root = Create("Mesh", Vector3.zero, Vector3.one);
            if (convex)
                root.AddComponent<Rigidbody>();

            var mesh = root.AddComponent<MeshCollider>();
            mesh.sharedMesh = CreateMesh();
            mesh.convex = convex;

            AssertEquivalent(root);
        }

        [Test]
        public void DetailedStaticMeshCollision_MatchesPhysX()
        {
            var root = Create("Mesh", Vector3.zero, Vector3.one);
            root.AddComponent<MeshCollider>().sharedMesh = CreateMesh();
            root.AddComponent<DetailedStaticMeshCollisionAuthoring>();

            AssertEquivalent(root);
        }

        [Test]
        public void DisabledCollider_MatchesPhysX()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>();
            root.AddComponent<SphereCollider>();
            root.AddComponent<BoxCollider>().enabled = false;

            AssertEquivalent(root);
        }

        [Test]
        public void CollisionResponse_MatchesPhysX(
            [Values(true, false)] bool isTrigger, [Values(true, false)] bool providesContacts)
        {
            var root = Create("Box", Vector3.zero, Vector3.one);
            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = isTrigger;
            box.providesContacts = providesContacts;

            AssertEquivalent(root);
        }

        [Test]
        public void Material_MatchesPhysX(
            [Values(PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Minimum, PhysicsMaterialCombine.Maximum,
                PhysicsMaterialCombine.Multiply)] PhysicsMaterialCombine combine)
        {
            var root = Create("Box", Vector3.zero, Vector3.one);
            var material = Track(new UnityPhysicsMaterial("Test")
            {
                dynamicFriction = 0.3f,
                staticFriction = 0.7f,
                bounciness = 0.4f,
                frictionCombine = combine,
                bounceCombine = combine,
            });
            root.AddComponent<BoxCollider>().sharedMaterial = material;

            AssertEquivalent(root);
            var warned = m_Report.HasEntry(ConversionSeverity.Warning, "Multiply");
            Assert.That(warned, Is.EqualTo(combine == PhysicsMaterialCombine.Multiply));
        }

        [Test]
        public void CollisionFilter_MatchesPhysX()
        {
            // The layer collision matrix is read from the project as is. Changing it would modify project settings.
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.layer = 3;
            var rigidbody = root.AddComponent<Rigidbody>();
            rigidbody.includeLayers = 1 << 6;
            rigidbody.excludeLayers = 1 << 8;

            var child = Create("Child", Vector3.up, Vector3.one, Quaternion.identity, root.transform);
            child.layer = 4;
            var box = child.AddComponent<BoxCollider>();
            box.includeLayers = 1 << 9;
            box.excludeLayers = 1 << 10;

            AssertEquivalent(root);
        }

        // ---- Rigidbodies ----------------------------------------------------------------------------------------

        [Test]
        public void Rigidbody_MatchesPhysX(
            [Values(false, true)] bool kinematic, [Values(false, true)] bool useGravity,
            [Values(RigidbodyInterpolation.None, RigidbodyInterpolation.Interpolate,
                RigidbodyInterpolation.Extrapolate)]
            RigidbodyInterpolation interpolation)
        {
            var root = Create("Body", new Vector3(1f, 0f, 0f), Vector3.one * 1.5f);
            var rigidbody = root.AddComponent<Rigidbody>();
            rigidbody.isKinematic = kinematic;
            rigidbody.useGravity = useGravity;
            rigidbody.interpolation = interpolation;
            rigidbody.mass = 3f;
            rigidbody.linearDamping = 0.2f;
            rigidbody.angularDamping = 0.4f;
            root.AddComponent<CapsuleCollider>();

            AssertEquivalent(root);
        }

        [Test]
        public void Rigidbody_WithoutColliders_MatchesPhysX()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>().mass = 2f;

            AssertEquivalent(root);
        }

        [Test]
        public void Rigidbody_WithCustomMassDistribution_MatchesPhysX()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            var rigidbody = root.AddComponent<Rigidbody>();
            rigidbody.mass = 2f;
            rigidbody.automaticCenterOfMass = false;
            rigidbody.centerOfMass = new Vector3(0.1f, 0.2f, 0.3f);
            rigidbody.automaticInertiaTensor = false;
            rigidbody.inertiaTensor = new Vector3(1f, 2f, 3f);
            rigidbody.inertiaTensorRotation = Quaternion.Euler(0f, 30f, 0f);
            root.AddComponent<BoxCollider>();

            AssertEquivalent(root, new CompareOptions { IgnoreAngularExpansion = true });
            Assert.That(m_Report.HasEntry(ConversionSeverity.Info, "angular expansion"), Is.True);
        }

        [TestCase(RigidbodyConstraints.FreezeRotationY, RotationAxes.Y, "single frozen axis")]
        [TestCase(RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ,
            RotationAxes.X | RotationAxes.Z, "free axis")]
        [TestCase(RigidbodyConstraints.FreezeRotation, RotationAxes.All, null)]
        [TestCase(RigidbodyConstraints.FreezePositionY, RotationAxes.None, null)]
        public void Rigidbody_Constraints_FreezeRotation(
            RigidbodyConstraints constraints, RotationAxes expected, string warning)
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>().constraints = constraints;
            root.AddComponent<BoxCollider>();

            var converted = ConvertCopy(root);

            Assert.That(converted.GetComponent<PhysicsBodyAuthoring>().FreezeRotation, Is.EqualTo(expected));
            Assert.That(m_Report.HasEntry(ConversionSeverity.Info, "Freeze Rotation"),
                Is.EqualTo(expected != RotationAxes.None));
            Assert.That(m_Report.HasEntry(ConversionSeverity.Info, "Freeze Position"),
                Is.EqualTo((constraints & RigidbodyConstraints.FreezePosition) != 0));
            // An empty fragment matches any warning, so no warning may be reported then.
            Assert.That(m_Report.HasEntry(ConversionSeverity.Warning, warning ?? string.Empty),
                Is.EqualTo(warning != null));
        }

        [Test]
        public void WorldIndex_MatchesPhysX()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>();
            root.AddComponent<BoxCollider>();
            root.AddComponent<PhysicsWorldIndexAuthoring>().WorldIndex = 1;

            AssertEquivalent(root);
        }

        [Test]
        public void SolverType_OnStaticRoot_MatchesPhysX()
        {
            var root = Create("Static", Vector3.zero, Vector3.one);
            root.AddComponent<BoxCollider>();
            root.AddComponent<SolverTypeAuthoring>().ContactSolverType = SolverType.Direct;

            AssertEquivalent(root);
        }

        [Test]
        public void SolverType_OnRigidbody_IsApplied()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>();
            root.AddComponent<BoxCollider>();
            root.AddComponent<SolverTypeAuthoring>().ContactSolverType = SolverType.Direct;

            var converted = ConvertCopy(root);

            Assert.That(converted.GetComponent<PhysicsBodyAuthoring>().SolverType, Is.EqualTo(SolverType.Direct));
            Assert.That(converted.GetComponent<SolverTypeAuthoring>(), Is.Null);
            Assert.That(m_Report.HasEntry(ConversionSeverity.Info, "Solver Type"), Is.True);
        }

        [Test]
        public void Rigidbody_RequiredByOtherComponent_IsKept()
        {
            var root = Create("Body", Vector3.zero, Vector3.one);
            root.AddComponent<Rigidbody>();
            // ConstantForce requires a Rigidbody, so the Rigidbody cannot be removed.
            root.AddComponent<ConstantForce>();

            PhysXAuthoringConverter.Convert(root, new ConversionContext(m_Templates, m_Report));

            Assert.That(root.GetComponent<Rigidbody>(), Is.Not.Null);
            Assert.That(root.GetComponent<PhysicsBodyAuthoring>(), Is.Null);
            Assert.That(m_Report.HasEntry(ConversionSeverity.Error, nameof(ConstantForce)), Is.True);
        }

        // ---- Joints ---------------------------------------------------------------------------------------------

        [Test]
        public void FixedJoint_MatchesPhysX()
        {
            var (bodyA, bodyB) = CreateJointBodies();
            var joint = bodyA.AddComponent<FixedJoint>();
            joint.connectedBody = bodyB.GetComponent<Rigidbody>();

            AssertEquivalent(bodyA.transform.parent.gameObject);
        }

        [Test]
        public void HingeJoint_MatchesPhysX([Values("Free", "Limited", "Motor", "Spring")] string mode)
        {
            var (bodyA, bodyB) = CreateJointBodies();
            var joint = bodyA.AddComponent<HingeJoint>();
            joint.connectedBody = bodyB.GetComponent<Rigidbody>();
            joint.anchor = new Vector3(0f, 1f, 0f);
            joint.axis = new Vector3(1f, 0f, 0f);
            joint.enableCollision = true;

            switch (mode)
            {
                case "Limited":
                    joint.useLimits = true;
                    joint.limits = new JointLimits { min = -30f, max = 45f };
                    break;
                case "Motor":
                    joint.useMotor = true;
                    joint.motor = new JointMotor { targetVelocity = 90f, force = 10f };
                    break;
                case "Spring":
                    joint.useSpring = true;
                    joint.spring = new JointSpring { spring = 5f, damper = 1f, targetPosition = 20f };
                    break;
            }

            AssertEquivalent(bodyA.transform.parent.gameObject);
        }

        [Test]
        public void SpringJoint_KeepsDistanceAndWarns()
        {
            var (bodyA, bodyB) = CreateJointBodies();
            var joint = bodyA.AddComponent<SpringJoint>();
            joint.connectedBody = bodyB.GetComponent<Rigidbody>();
            joint.minDistance = 0.5f;
            joint.maxDistance = 1.5f;

            AssertEquivalent(bodyA.transform.parent.gameObject, new CompareOptions { IgnoreJointSprings = true });
            Assert.That(m_Report.HasEntry(ConversionSeverity.Warning, "spring and damper"), Is.True);
        }

        [Test]
        public void CharacterJoint_BecomesRagdollAndWarns()
        {
            var (bodyA, bodyB) = CreateJointBodies();
            var joint = bodyA.AddComponent<CharacterJoint>();
            joint.connectedBody = bodyB.GetComponent<Rigidbody>();

            var converted = ConvertCopy(bodyA.transform.parent.gameObject);

            Assert.That(converted.GetComponentInChildren<BaseJoint>().GetType(), Is.EqualTo(typeof(RagdollJoint)));
            Assert.That(m_Report.HasEntry(ConversionSeverity.Warning, "Ragdoll"), Is.True);
        }

        [TestCase("Locked,Locked,Locked", "Locked,Locked,Locked", typeof(RigidJoint))]
        [TestCase("Locked,Locked,Locked", "Free,Locked,Locked", typeof(FreeHingeJoint))]
        [TestCase("Locked,Locked,Locked", "Limited,Locked,Locked", typeof(LimitedHingeJoint))]
        [TestCase("Locked,Locked,Locked", "Free,Free,Free", typeof(BallAndSocketJoint))]
        [TestCase("Locked,Locked,Locked", "Limited,Limited,Locked", typeof(RagdollJoint))]
        [TestCase("Free,Locked,Locked", "Locked,Locked,Locked", typeof(PrismaticJoint))]
        [TestCase("Free,Free,Locked", "Locked,Locked,Locked", typeof(LimitDOFJoint))]
        public void ConfigurableJoint_MapsToClosestJoint(string linear, string angular, Type expected)
        {
            var (bodyA, bodyB) = CreateJointBodies();
            var joint = bodyA.AddComponent<ConfigurableJoint>();
            joint.connectedBody = bodyB.GetComponent<Rigidbody>();
            var linearMotions = Array.ConvertAll(linear.Split(','), Enum.Parse<ConfigurableJointMotion>);
            var angularMotions = Array.ConvertAll(angular.Split(','), Enum.Parse<ConfigurableJointMotion>);
            joint.xMotion = linearMotions[0];
            joint.yMotion = linearMotions[1];
            joint.zMotion = linearMotions[2];
            joint.angularXMotion = angularMotions[0];
            joint.angularYMotion = angularMotions[1];
            joint.angularZMotion = angularMotions[2];

            var converted = ConvertCopy(bodyA.transform.parent.gameObject);

            var replacement = converted.GetComponentInChildren<BaseJoint>();
            Assert.That(replacement.GetType(), Is.EqualTo(expected));
            Assert.That(replacement.ConnectedBody, Is.Not.Null);
        }

        // ---- Helpers --------------------------------------------------------------------------------------------

        (GameObject BodyA, GameObject BodyB) CreateJointBodies()
        {
            var root = Create("Joint Root", Vector3.zero, Vector3.one);
            var bodyA = Create("Body A", Vector3.zero, Vector3.one, Quaternion.identity, root.transform);
            bodyA.AddComponent<Rigidbody>();
            bodyA.AddComponent<BoxCollider>();

            var rotationB = Quaternion.Euler(0f, 30f, 0f);
            var bodyB = Create("Body B", new Vector3(0f, 2f, 0f), Vector3.one, rotationB, root.transform);
            bodyB.AddComponent<Rigidbody>();
            bodyB.AddComponent<BoxCollider>();
            return (bodyA, bodyB);
        }

        GameObject Create(
            string name, Vector3 position, Vector3 scale, Quaternion? rotation = null, Transform parent = null)
        {
            var gameObject = Track(new GameObject(name));
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.SetPositionAndRotation(position, rotation ?? Quaternion.identity);
            gameObject.transform.localScale = scale;
            return gameObject;
        }

        T Track<T>(T obj) where T : Object
        {
            m_Objects.Add(obj);
            return obj;
        }

        Mesh CreateMesh()
        {
            var mesh = Track(new Mesh { name = "Test Mesh" });
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.75f),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        GameObject ConvertCopy(GameObject source)
        {
            var copy = Track(Object.Instantiate(source));
            copy.name = $"{source.name} (Converted)";
            PhysXAuthoringConverter.Convert(copy, new ConversionContext(m_Templates, m_Report));

            Assert.That(copy.GetComponentsInChildren<Rigidbody>(true), Is.Empty, "Remaining Rigidbodies");
            Assert.That(copy.GetComponentsInChildren<UnityEngine.Joint>(true), Is.Empty, "Remaining joints");
            foreach (var collider in copy.GetComponentsInChildren<UnityEngine.Collider>(true))
                Assert.That(PhysXAuthoringConverter.IsSupportedCollider(collider), Is.False,
                    $"Remaining {collider.GetType().Name}");

            return copy;
        }

        void AssertEquivalent(GameObject source, CompareOptions options = default)
        {
            var converted = ConvertCopy(source);

            using var expectedStore = new BlobAssetStore(128);
            using var actualStore = new BlobAssetStore(128);
            using var expected = BakingTestUtility.Bake(source, expectedStore);
            using var actual = BakingTestUtility.Bake(converted, actualStore);
            BakedPhysicsComparer.AssertEquivalent(expected, actual, options);
        }
    }
}
