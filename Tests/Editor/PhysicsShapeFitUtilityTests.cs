using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEngine;
using FitSources = Unity.Physics.Editor.PhysicsShapeFitUtility.FitSources;
using Object = UnityEngine.Object;

namespace Unity.Physics.Editor.Tests
{
    /// <summary> Checks the sources and results of the "Fit to Self" and "Fit to Children" shape buttons. </summary>
    class PhysicsShapeFitUtilityTests
    {
        const float TOLERANCE = 1e-4f;

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
        public void FitToSelf_IgnoresChildRenderers()
        {
            var shape = CreateShapeWithChildCube(out _);

            PhysicsShapeFitUtility.Fit(shape, 0f, false);

            AssertBox(shape, float3.zero, new float3(1f));
        }

        [Test]
        public void FitToChildren_EncapsulatesSelfAndChildRenderers()
        {
            var shape = CreateShapeWithChildCube(out _);

            PhysicsShapeFitUtility.Fit(shape, 0f, true);

            AssertBox(shape, new float3(1.5f, 0f, 0f), new float3(4f, 1f, 1f));
        }

        [Test]
        public void FitToChildren_ExcludesChildrenWithTheirOwnShape()
        {
            var shape = CreateShapeWithChildCube(out var child);
            child.AddComponent<PhysicsShapeAuthoring>();

            Assert.That(PhysicsShapeFitUtility.GetSources(shape), Is.EqualTo(FitSources.SelfMesh));

            PhysicsShapeFitUtility.Fit(shape, 0f, true);

            AssertBox(shape, float3.zero, new float3(1f));
        }

        [Test]
        public void GetSources_WithoutOwnRenderer_OnlyReportsChildren()
        {
            var root = Track(new GameObject("Root"));
            var child = CreateCube("Child");
            child.transform.SetParent(root.transform, false);
            var shape = root.AddComponent<PhysicsShapeAuthoring>();

            Assert.That(PhysicsShapeFitUtility.GetSources(shape), Is.EqualTo(FitSources.ChildMesh));
        }

        [Test]
        public void GetSources_WithoutRenderers_IsNone()
        {
            var shape = Track(new GameObject("Root")).AddComponent<PhysicsShapeAuthoring>();

            Assert.That(PhysicsShapeFitUtility.GetSources(shape), Is.EqualTo(FitSources.None));
        }

        PhysicsShapeAuthoring CreateShapeWithChildCube(out GameObject child)
        {
            var root = CreateCube("Root");
            child = CreateCube("Child");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(3f, 0f, 0f);
            return root.AddComponent<PhysicsShapeAuthoring>();
        }

        GameObject CreateCube(string name)
        {
            var cube = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            cube.name = name;
            Object.DestroyImmediate(cube.GetComponent<UnityEngine.BoxCollider>());
            return cube;
        }

        GameObject Track(GameObject gameObject)
        {
            m_Objects.Add(gameObject);
            return gameObject;
        }

        static void AssertBox(PhysicsShapeAuthoring shape, float3 center, float3 size)
        {
            var box = shape.GetBoxProperties();
            Assert.That(math.distance(box.Center, center), Is.LessThan(TOLERANCE), $"Center {box.Center}");
            Assert.That(math.distance(box.Size, size), Is.LessThan(TOLERANCE), $"Size {box.Size}");
        }
    }
}
