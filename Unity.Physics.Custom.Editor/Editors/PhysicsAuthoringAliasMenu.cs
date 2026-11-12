using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;

namespace Unity.Physics.Editor
{
    // FireAlt: the Add Component window lists the "Component/" menu, so these entries make the Physics Body and
    // Physics Shape show up when searching for the PhysX component names.
    static class PhysicsAuthoringAliasMenu
    {
        const string ROOT = "Component/Entities/Physics/";
        const string RIGIDBODY = ROOT + "Rigidbody (Physics Body)";
        const string BOX_COLLIDER = ROOT + "Box Collider (Physics Shape)";
        const string SPHERE_COLLIDER = ROOT + "Sphere Collider (Physics Shape)";
        const string CAPSULE_COLLIDER = ROOT + "Capsule Collider (Physics Shape)";
        const string MESH_COLLIDER = ROOT + "Mesh Collider (Physics Shape)";
        const string CONVEX_MESH_COLLIDER = ROOT + "Convex Mesh Collider (Physics Shape)";

        [MenuItem(RIGIDBODY, true)]
        [MenuItem(BOX_COLLIDER, true)]
        [MenuItem(SPHERE_COLLIDER, true)]
        [MenuItem(CAPSULE_COLLIDER, true)]
        [MenuItem(MESH_COLLIDER, true)]
        [MenuItem(CONVEX_MESH_COLLIDER, true)]
        static bool HasTargets(MenuCommand command) => GetTargets(command).Length > 0;

        [MenuItem(RIGIDBODY)]
        static void AddRigidbody(MenuCommand command)
        {
            foreach (var gameObject in GetTargets(command))
                AddBody(gameObject);
        }

        [MenuItem(BOX_COLLIDER)]
        static void AddBoxCollider(MenuCommand command) => AddShapes(command, ShapeType.Box);

        [MenuItem(SPHERE_COLLIDER)]
        static void AddSphereCollider(MenuCommand command) => AddShapes(command, ShapeType.Sphere);

        [MenuItem(CAPSULE_COLLIDER)]
        static void AddCapsuleCollider(MenuCommand command) => AddShapes(command, ShapeType.Capsule);

        [MenuItem(MESH_COLLIDER)]
        static void AddMeshCollider(MenuCommand command) => AddShapes(command, ShapeType.Mesh);

        [MenuItem(CONVEX_MESH_COLLIDER)]
        static void AddConvexMeshCollider(MenuCommand command) => AddShapes(command, ShapeType.ConvexHull);

        /// <summary> Adds a Physics Body, or returns the existing one, since a GameObject can only have one. </summary>
        internal static PhysicsBodyAuthoring AddBody(GameObject gameObject)
        {
            var body = gameObject.GetComponent<PhysicsBodyAuthoring>();
            return body != null ? body : Undo.AddComponent<PhysicsBodyAuthoring>(gameObject);
        }

        /// <summary> Adds a Physics Shape fitted to the render meshes, converted to <paramref name="shapeType"/>. </summary>
        internal static PhysicsShapeAuthoring AddShape(GameObject gameObject, ShapeType shapeType)
        {
            var shape = Undo.AddComponent<PhysicsShapeAuthoring>(gameObject);
            switch (shapeType)
            {
                case ShapeType.Sphere:
                    shape.SetSphere(shape.GetSphereProperties(out EulerAngles sphereOrientation), sphereOrientation);
                    break;
                case ShapeType.Capsule:
                    shape.SetCapsule(shape.GetCapsuleProperties());
                    break;
                case ShapeType.ConvexHull:
                    shape.InitializeConvexHullGenerationParameters();
                    shape.SetConvexHull(shape.ConvexHullGenerationParameters);
                    break;
                case ShapeType.Mesh:
                    shape.SetMesh();
                    break;
            }

            return shape;
        }

        static void AddShapes(MenuCommand command, ShapeType shapeType)
        {
            foreach (var gameObject in GetTargets(command))
                AddShape(gameObject, shapeType);
        }

        // The Add Component window runs the entry once per GameObject with it as the context.
        // The main Component menu passes no context, so the selection is used instead.
        static GameObject[] GetTargets(MenuCommand command) =>
            command.context is GameObject gameObject ? new[] { gameObject } : Selection.gameObjects;
    }
}
