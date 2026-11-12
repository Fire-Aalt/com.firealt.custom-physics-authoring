using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.Physics.Editor.Tests
{
    // Inside Unity.Physics these names would resolve to the ECS collider types.
    using BoxCollider = UnityEngine.BoxCollider;
    using SphereCollider = UnityEngine.SphereCollider;

    /// <summary> Checks the component context menu conversions and the Add Component aliases. </summary>
    class PhysXInPlaceConversionTests
    {
        const float TOLERANCE = 1e-5f;

        readonly List<Object> m_Objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in m_Objects)
            {
                if (o != null)
                    Object.DestroyImmediate(o);
            }

            m_Objects.Clear();
        }

        [Test]
        public void ConvertCollider_MatchesBulkConversionAndKeepsPosition()
        {
            var inPlace = CreateBoxAndSphere("InPlace");
            var bulk = CreateBoxAndSphere("Bulk");

            var shape = PhysXAuthoringConverter.ConvertColliderInPlace(inPlace.GetComponent<BoxCollider>(), CreateContext());
            PhysXAuthoringConverter.Convert(bulk, CreateContext());

            Assert.That(shape, Is.Not.Null);
            var components = inPlace.GetComponents<Component>();
            Assert.That(components[1], Is.EqualTo(shape), "Takes the Box Collider's position");
            Assert.That(components[2], Is.InstanceOf<SphereCollider>(), "Other colliders are kept");
            Assert.That(inPlace.GetComponent<BoxCollider>(), Is.Null);

            var expected = bulk.GetComponents<PhysicsShapeAuthoring>()[0];
            var box = shape.GetBoxProperties();
            var expectedBox = expected.GetBoxProperties();
            Assert.That(math.distance(box.Center, expectedBox.Center), Is.LessThan(TOLERANCE));
            Assert.That(math.distance(box.Size, expectedBox.Size), Is.LessThan(TOLERANCE));
            Assert.That(box.BevelRadius, Is.EqualTo(expectedBox.BevelRadius).Within(TOLERANCE));
            Assert.That(shape.CollisionResponse, Is.EqualTo(expected.CollisionResponse));
            Assert.That(shape.BelongsTo.Value, Is.EqualTo(expected.BelongsTo.Value));
            Assert.That(shape.CollidesWith.Value, Is.EqualTo(expected.CollidesWith.Value));
        }

        [Test]
        public void ConvertCollider_RemovesHelpersWithTheLastCollider_AndUndoRestores()
        {
            var gameObject = CreateBoxAndSphere("Helpers");
            gameObject.AddComponent<DetailedStaticMeshCollisionAuthoring>();

            PhysXAuthoringConverter.ConvertColliderInPlace(gameObject.GetComponent<BoxCollider>(), CreateContext());
            Assert.That(gameObject.GetComponent<DetailedStaticMeshCollisionAuthoring>(), Is.Not.Null,
                "Kept while a PhysX collider remains");

            PhysXAuthoringConverter.ConvertColliderInPlace(gameObject.GetComponent<SphereCollider>(), CreateContext());
            Assert.That(gameObject.GetComponent<DetailedStaticMeshCollisionAuthoring>(), Is.Null);

            Undo.PerformUndo();
            Assert.That(gameObject.GetComponent<SphereCollider>(), Is.Not.Null);
            Assert.That(gameObject.GetComponent<DetailedStaticMeshCollisionAuthoring>(), Is.Not.Null);
            Assert.That(gameObject.GetComponents<PhysicsShapeAuthoring>(), Has.Length.EqualTo(1));
        }

        [Test]
        public void ConvertBody_ConvertsJointConnectedBodiesTogether_AndUndoRestores()
        {
            var a = Track(new GameObject("A"));
            var b = Track(new GameObject("B"));
            var rigidbodyB = b.AddComponent<Rigidbody>();
            var hinge = a.AddComponent<HingeJoint>();
            hinge.connectedBody = rigidbodyB;

            Assert.That(PhysXAuthoringConverter.ConvertBodyInPlace(rigidbodyB, CreateContext()), Is.True);

            var bodyB = b.GetComponent<PhysicsBodyAuthoring>();
            Assert.That(bodyB, Is.Not.Null);
            Assert.That(a.GetComponent<PhysicsBodyAuthoring>(), Is.Not.Null);
            Assert.That(a.GetComponent<BaseJoint>().ConnectedBody, Is.EqualTo(bodyB));
            Assert.That(a.GetComponent<UnityEngine.Joint>(), Is.Null);
            Assert.That(a.GetComponent<Rigidbody>(), Is.Null);
            Assert.That(b.GetComponent<Rigidbody>(), Is.Null);

            Undo.PerformUndo();
            Assert.That(a.GetComponent<BaseJoint>(), Is.Null);
            Assert.That(b.GetComponent<PhysicsBodyAuthoring>(), Is.Null);
            Assert.That(a.GetComponent<HingeJoint>().connectedBody, Is.EqualTo(b.GetComponent<Rigidbody>()));
        }

        [Test]
        public void ConvertBody_WithForeignRequirement_ChangesNothing()
        {
            var gameObject = Track(new GameObject("Blocked"));
            var rigidbody = gameObject.AddComponent<Rigidbody>();
            gameObject.AddComponent<ConstantForce>();
            var context = CreateContext();

            Assert.That(PhysXAuthoringConverter.ConvertBodyInPlace(rigidbody, context), Is.False);
            Assert.That(gameObject.GetComponent<Rigidbody>(), Is.Not.Null);
            Assert.That(gameObject.GetComponent<PhysicsBodyAuthoring>(), Is.Null);
            Assert.That(context.Report.ErrorCount, Is.EqualTo(1));
        }

        [TestCase(ShapeType.Box)]
        [TestCase(ShapeType.Sphere)]
        [TestCase(ShapeType.Capsule)]
        [TestCase(ShapeType.ConvexHull)]
        [TestCase(ShapeType.Mesh)]
        public void AliasShape_HasRequestedShapeType(ShapeType shapeType)
        {
            var gameObject = Track(new GameObject("Alias"));

            Assert.That(PhysicsAuthoringAliasMenu.AddShape(gameObject, shapeType).ShapeType, Is.EqualTo(shapeType));
        }

        [Test]
        public void AliasBody_ReusesTheExistingBody()
        {
            var gameObject = Track(new GameObject("Alias"));
            var body = PhysicsAuthoringAliasMenu.AddBody(gameObject);

            Assert.That(PhysicsAuthoringAliasMenu.AddBody(gameObject), Is.EqualTo(body));
        }

        static ConversionContext CreateContext() => new ConversionContext(null, new ConversionReport());

        GameObject CreateBoxAndSphere(string name)
        {
            var gameObject = Track(new GameObject(name));
            var box = gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0.5f, 1f, -2f);
            box.size = new Vector3(2f, 3f, 4f);
            gameObject.AddComponent<SphereCollider>();
            return gameObject;
        }

        GameObject Track(GameObject gameObject)
        {
            m_Objects.Add(gameObject);
            return gameObject;
        }
    }
}
