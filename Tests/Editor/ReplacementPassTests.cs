using NUnit.Framework;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityPhysicsMaterial = UnityEngine.PhysicsMaterial;

namespace Unity.Physics.Editor.Tests
{
    // Inside Unity.Physics these names would resolve to the ECS collider types.
    using BoxCollider = UnityEngine.BoxCollider;
    using SphereCollider = UnityEngine.SphereCollider;

    /// <summary>
    /// Runs the asset pass on a temporary folder with a prefab, a variant, a nested prefab and a scene, and checks
    /// that prefab instance overrides survive the replacement.
    /// </summary>
    class ReplacementPassTests
    {
        const string FOLDER = "Assets/__PhysXReplacementTests";
        const string BASE_PATH = FOLDER + "/Base.prefab";
        const string VARIANT_PATH = FOLDER + "/Variant.prefab";
        const string CONTAINER_PATH = FOLDER + "/Container.prefab";
        const string SCENE_PATH = FOLDER + "/Test.unity";
        const string MATERIAL_PATH = FOLDER + "/Shared.physicMaterial";

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(FOLDER))
                AssetDatabase.DeleteAsset(FOLDER);

            AssetDatabase.CreateFolder("Assets", "__PhysXReplacementTests");
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(FOLDER);
        }

        [Test]
        public void Run_ReplacesPhysXAndPreservesInstanceOverrides()
        {
            CreateAssets();

            var report = PhysXReplacementPass.Run(new PhysXReplacementOptions
            {
                Folders = new[] { FOLDER },
                Interactive = false,
                GeneratedAssetsFolder = FOLDER + "/Generated",
            });

            Assert.That(report.ErrorCount, Is.Zero, "Errors");
            Assert.That(report.ModifiedPrefabs, Is.EqualTo(3), "Modified prefabs");
            Assert.That(report.ModifiedScenes, Is.EqualTo(1), "Modified scenes");

            // Base prefab: converted in place.
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BASE_PATH);
            AssertNoPhysX(basePrefab);
            Assert.That(basePrefab.GetComponent<PhysicsBodyAuthoring>(), Is.Not.Null, "Base body");
            var baseBox = basePrefab.GetComponent<PhysicsShapeAuthoring>();
            Assert.That(baseBox.ShapeType, Is.EqualTo(ShapeType.Box));
            Assert.That(baseBox.GetBoxProperties().Size.x, Is.EqualTo(1f), "Base box size");
            Assert.That(GetSphereRadius(basePrefab.transform.Find("Child")), Is.EqualTo(0.5f), "Base sphere radius");

            var template = AssetDatabase.LoadAssetAtPath<PhysicsMaterialTemplate>(FOLDER + "/Shared Template.asset");
            var assets = string.Join(", ", System.Array.ConvertAll(
                AssetDatabase.FindAssets(string.Empty, new[] { FOLDER }), AssetDatabase.GUIDToAssetPath));
            Assert.That(template, Is.Not.Null, $"Shared template. Assets: {assets}");
            Assert.That(baseBox.MaterialTemplate, Is.EqualTo(template), "Base template");
            Assert.That(template.Friction.Value, Is.EqualTo(0.2f), "Template friction");

            // Variant: keeps its size override and shares the template.
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VARIANT_PATH);
            AssertNoPhysX(variant);
            var variantBox = variant.GetComponent<PhysicsShapeAuthoring>();
            Assert.That(variantBox.GetBoxProperties().Size.x, Is.EqualTo(5f), "Variant box size");
            Assert.That(variantBox.MaterialTemplate, Is.EqualTo(template), "Variant template");
            var variantSource = PrefabUtility.GetCorrespondingObjectFromSource(variantBox);
            Assert.That(variantSource, Is.EqualTo(baseBox), "Variant shape source");

            // Container: keeps the nested instance override.
            var container = AssetDatabase.LoadAssetAtPath<GameObject>(CONTAINER_PATH);
            AssertNoPhysX(container);
            var nestedRadius = GetSphereRadius(container.transform.Find("Base/Child"));
            Assert.That(nestedRadius, Is.EqualTo(1.5f), "Nested sphere radius");

            // Scene: keeps the radius override and the removed collider.
            var scene = EditorSceneManager.OpenScene(SCENE_PATH, OpenSceneMode.Single);
            var instance = FindRoot(scene, "Base");
            AssertNoPhysX(instance);
            Assert.That(instance.GetComponent<PhysicsBodyAuthoring>(), Is.Not.Null, "Scene body");
            Assert.That(instance.GetComponent<PhysicsShapeAuthoring>(), Is.Null, "Removed box stays removed");
            Assert.That(GetSphereRadius(instance.transform.Find("Child")), Is.EqualTo(2f), "Scene sphere radius");

            Assert.That(AssetDatabase.FindAssets($"t:{nameof(PhysicsCategoryNames)}"), Is.Not.Empty, "Category names");
        }

        static void CreateAssets()
        {
            var material = new UnityPhysicsMaterial("Shared") { dynamicFriction = 0.2f, bounciness = 0.1f };
            AssetDatabase.CreateAsset(material, MATERIAL_PATH);

            // Base: a body with a box, and a child sphere.
            var baseRoot = new GameObject("Base");
            baseRoot.AddComponent<Rigidbody>();
            baseRoot.AddComponent<BoxCollider>().sharedMaterial = material;
            var child = new GameObject("Child");
            child.transform.SetParent(baseRoot.transform, false);
            child.transform.localPosition = Vector3.up;
            child.AddComponent<SphereCollider>().radius = 0.5f;
            var basePrefab = PrefabUtility.SaveAsPrefabAsset(baseRoot, BASE_PATH);
            Object.DestroyImmediate(baseRoot);

            // Variant: overrides the box size.
            var variantRoot = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            var variantBox = variantRoot.GetComponent<BoxCollider>();
            variantBox.size = new Vector3(5f, 1f, 1f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(variantBox);
            PrefabUtility.SaveAsPrefabAsset(variantRoot, VARIANT_PATH);
            Object.DestroyImmediate(variantRoot);

            // Container: a nested Base whose sphere radius is overridden.
            var containerRoot = new GameObject("Container");
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab, containerRoot.transform);
            var nestedSphere = nested.transform.Find("Child").GetComponent<SphereCollider>();
            nestedSphere.radius = 1.5f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(nestedSphere);
            PrefabUtility.SaveAsPrefabAsset(containerRoot, CONTAINER_PATH);
            Object.DestroyImmediate(containerRoot);

            // Scene: a Base instance with a radius override and a removed box.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sceneInstance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab, scene);
            var sceneSphere = sceneInstance.transform.Find("Child").GetComponent<SphereCollider>();
            sceneSphere.radius = 2f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(sceneSphere);
            Object.DestroyImmediate(sceneInstance.GetComponent<BoxCollider>());
            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static void AssertNoPhysX(GameObject root)
        {
            Assert.That(root.GetComponentsInChildren<Rigidbody>(true), Is.Empty, $"{root.name} Rigidbodies");
            Assert.That(root.GetComponentsInChildren<UnityEngine.Collider>(true), Is.Empty, $"{root.name} colliders");
        }

        static float GetSphereRadius(Transform transform)
        {
            var shape = transform.GetComponent<PhysicsShapeAuthoring>();
            Assert.That(shape, Is.Not.Null, $"{transform.name} shape");
            Assert.That(shape.ShapeType, Is.EqualTo(ShapeType.Sphere), $"{transform.name} shape type");
            return shape.GetSphereProperties(out quaternion _).Radius;
        }

        static GameObject FindRoot(Scene scene, string name)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name)
                    return root;
            }

            Assert.Fail($"Root '{name}' not found");
            return null;
        }
    }
}
