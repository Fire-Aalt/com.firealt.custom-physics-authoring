using System;
using System.Collections.Generic;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.Physics.Editor
{
    /// <summary>
    /// Converts single PhysX components in place, as one undo step, with the same mappings as <see cref="Convert"/>.
    /// Each replacement takes the position of the component it replaces.
    /// </summary>
    static partial class PhysXAuthoringConverter
    {
        /// <summary> Returns true for scene components that <see cref="ConvertColliderInPlace"/> or
        /// <see cref="ConvertBodyInPlace"/> can convert. </summary>
        internal static bool CanConvertInPlace(Component component)
        {
            if (component == null || EditorUtility.IsPersistent(component) || !component.gameObject.scene.IsValid())
                return false;

            return component is Rigidbody || component is UnityEngine.Joint ||
                component is UnityEngine.Collider collider && IsSupportedCollider(collider);
        }

        /// <summary> Replaces a collider with a Physics Shape. </summary>
        /// <returns> The new shape, or null if the collider could not be converted. </returns>
        internal static PhysicsShapeAuthoring ConvertColliderInPlace(UnityEngine.Collider collider, ConversionContext context)
        {
            var report = context.Report;
            if (!IsSupportedCollider(collider))
            {
                report.Error(collider, "Not converted, because the PhysX bakers do not support this type.");
                return null;
            }

            if (FindForeignRequirement(collider, new HashSet<Component> { collider }, out var dependent))
            {
                report.Error(collider, $"Not converted, because {dependent.GetType().Name} requires it.");
                return null;
            }

            var group = BeginUndoGroup("Convert to Physics Shape");
            var gameObject = collider.gameObject;
            var data = ReadShape(collider, report);
            var shape = (PhysicsShapeAuthoring)AddComponentAt(collider, typeof(PhysicsShapeAuthoring));
            WriteShape(data, shape, context);
            context.Converted?.Invoke(collider, shape);
            report.ConvertedShapes++;

            Undo.DestroyObjectImmediate(collider);
            RemoveHelpersInPlace(gameObject, context);
            Undo.CollapseUndoOperations(group);
            return shape;
        }

        /// <summary>
        /// Replaces a Rigidbody with a Physics Body. PhysX joints require their Rigidbody and custom joints require
        /// their Physics Body, and joints reference their connected body, so every Rigidbody connected to this one
        /// through joints is converted together with those joints.
        /// </summary>
        /// <returns> True if the bodies and joints were converted, false if nothing was changed. </returns>
        internal static bool ConvertBodyInPlace(Rigidbody rigidbody, ConversionContext context)
        {
            var report = context.Report;
            var rigidbodies = CollectJointConnectedBodies(rigidbody, out var joints);

            // The world index is read into the Physics Body, and requires the Rigidbody.
            var worldIndices = new List<PhysicsWorldIndexAuthoring>();
            foreach (var body in rigidbodies)
            {
                var worldIndex = body.GetComponent<PhysicsWorldIndexAuthoring>();
                if (worldIndex != null)
                    worldIndices.Add(worldIndex);
            }

            var removing = new HashSet<Component>(joints);
            removing.UnionWith(rigidbodies);
            removing.UnionWith(worldIndices);
            foreach (var body in rigidbodies)
            {
                if (FindForeignRequirement(body, removing, out var dependent))
                {
                    report.Error(body, $"Not converted, because {dependent.GetType().Name} requires a Rigidbody.");
                    return false;
                }
            }

            var bodies = new List<(Rigidbody Source, BodyData Data)>();
            foreach (var body in rigidbodies)
                bodies.Add((body, ReadBody(body, report)));

            var jointData = new List<(UnityEngine.Joint Source, JointData Data)>();
            foreach (var joint in joints)
            {
                var data = ReadJoint(joint, report);
                if (data == null)
                {
                    report.Error(rigidbody, $"Not converted, because the joint on {joint.name} cannot be converted.");
                    return false;
                }

                jointData.Add((joint, data));
            }

            var group = BeginUndoGroup("Convert to Physics Body");

            foreach (var (source, data) in bodies)
            {
                var body = source.GetComponent<PhysicsBodyAuthoring>();
                if (body != null)
                {
                    Undo.RecordObject(body, "Convert to Physics Body");
                    report.Warning(source.gameObject, "Already had a Physics Body. The Rigidbody overwrote its values.");
                }
                else
                {
                    body = (PhysicsBodyAuthoring)AddComponentAt(source, typeof(PhysicsBodyAuthoring));
                }

                WriteBody(data, body);
                context.Converted?.Invoke(source, body);
                report.ConvertedBodies++;
            }

            // Bodies are written first, so the joints find their connected Physics Body.
            foreach (var (source, data) in jointData)
            {
                var joint = (BaseJoint)AddComponentAt(source, data.ComponentType);
                data.Write(joint);
                context.Converted?.Invoke(source, joint);
                report.ConvertedJoints++;
            }

            // Remove dependents before the components they require.
            foreach (var joint in joints)
                Undo.DestroyObjectImmediate(joint);

            foreach (var worldIndex in worldIndices)
                Undo.DestroyObjectImmediate(worldIndex);

            foreach (var body in rigidbodies)
            {
                var gameObject = body.gameObject;
                Undo.DestroyObjectImmediate(body);
                RemoveHelpersInPlace(gameObject, context);
            }

            Undo.CollapseUndoOperations(group);
            return true;
        }

        /// <summary> Finds the Rigidbodies connected to <paramref name="rigidbody"/> through joints, and the joints. </summary>
        static List<Rigidbody> CollectJointConnectedBodies(Rigidbody rigidbody, out List<UnityEngine.Joint> joints)
        {
            var sceneJoints = new List<UnityEngine.Joint>();
            foreach (var root in rigidbody.gameObject.scene.GetRootGameObjects())
                sceneJoints.AddRange(root.GetComponentsInChildren<UnityEngine.Joint>(true));

            var rigidbodies = new List<Rigidbody> { rigidbody };
            var visited = new HashSet<Rigidbody> { rigidbody };
            for (var i = 0; i < rigidbodies.Count; ++i)
            {
                foreach (var joint in sceneJoints)
                {
                    var owner = joint.GetComponent<Rigidbody>();
                    var connected = joint.connectedBody;
                    if (owner == rigidbodies[i] && connected != null && visited.Add(connected))
                        rigidbodies.Add(connected);
                    else if (connected == rigidbodies[i] && owner != null && visited.Add(owner))
                        rigidbodies.Add(owner);
                }
            }

            joints = new List<UnityEngine.Joint>();
            foreach (var joint in sceneJoints)
            {
                if (visited.Contains(joint.GetComponent<Rigidbody>()))
                    joints.Add(joint);
            }

            return rigidbodies;
        }

        /// <summary>
        /// Removes the PhysX baking helpers, whose values the replacements took over, once no PhysX body, joint or
        /// collider is left on <paramref name="gameObject"/>. Like <see cref="ConvertStaticSolverTypes"/>, a static
        /// collider root keeps its contact solver type on a new static Physics Body.
        /// </summary>
        static void RemoveHelpersInPlace(GameObject gameObject, ConversionContext context)
        {
            if (gameObject.GetComponent<Rigidbody>() != null || gameObject.GetComponent<UnityEngine.Joint>() != null ||
                gameObject.GetComponent<UnityEngine.Collider>() != null)
                return;

            var solverType = gameObject.GetComponent<SolverTypeAuthoring>();
            if (solverType != null && solverType.isActiveAndEnabled &&
                gameObject.GetComponentInParent<Rigidbody>(true) == null &&
                gameObject.GetComponentInParent<PhysicsBodyAuthoring>(true) == null &&
                gameObject.GetComponent<PhysicsShapeAuthoring>() != null &&
                (gameObject.transform.parent == null ||
                    gameObject.transform.parent.GetComponentInParent<PhysicsShapeAuthoring>(true) == null))
            {
                var body = Undo.AddComponent<PhysicsBodyAuthoring>(gameObject);
                body.MotionType = BodyMotionType.Static;
                body.SolverType = solverType.ContactSolverType;
                context.Report.Info(gameObject, "Added a static Physics Body to keep the Solver Type contact setting.");
            }

            RemoveHelperInPlace<SolverTypeAuthoring>(gameObject);
            RemoveHelperInPlace<DetailedStaticMeshCollisionAuthoring>(gameObject);
            RemoveHelperInPlace<PhysicsWorldIndexAuthoring>(gameObject);
        }

        static void RemoveHelperInPlace<T>(GameObject gameObject) where T : Component
        {
            var helper = gameObject.GetComponent<T>();
            if (helper != null && !FindForeignRequirement(helper, new HashSet<Component> { helper }, out _))
                Undo.DestroyObjectImmediate(helper);
        }

        /// <summary> Adds a component directly above <paramref name="source"/>, so it takes its place once removed. </summary>
        static Component AddComponentAt(Component source, Type type)
        {
            var component = Undo.AddComponent(source.gameObject, type);
            var index = Array.IndexOf(source.GetComponents<Component>(), source);
            while (Array.IndexOf(source.GetComponents<Component>(), component) > index)
            {
                if (!ComponentUtility.MoveComponentUp(component))
                    break;
            }

            return component;
        }

        static int BeginUndoGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return Undo.GetCurrentGroup();
        }
    }
}
