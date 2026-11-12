using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityPhysicsMaterial = UnityEngine.PhysicsMaterial;
using UnityPhysicsMaterialCombine = UnityEngine.PhysicsMaterialCombine;

namespace Unity.Physics.Editor
{
    /// <summary> Supplies the <see cref="PhysicsMaterialTemplate"/> that replaces a PhysX material. </summary>
    interface IPhysicsMaterialTemplateProvider
    {
        /// <param name="material"> The PhysX material, or null for the PhysX default material. </param>
        PhysicsMaterialTemplate GetTemplate(UnityPhysicsMaterial material);
    }

    /// <summary> Settings and callbacks shared by one replacement run. </summary>
    sealed class ConversionContext
    {
        public ConversionContext(IPhysicsMaterialTemplateProvider templates, ConversionReport report)
        {
            Templates = templates;
            Report = report;
        }

        public IPhysicsMaterialTemplateProvider Templates { get; }

        public ConversionReport Report { get; }

        /// <summary> Returns false for components that another asset owns, such as prefab instance content. </summary>
        public Func<Component, bool> IsOwned { get; set; } = _ => true;

        /// <summary> Called with each PhysX component and the ECS authoring component that replaced it. </summary>
        public Action<Component, Component> Converted { get; set; }
    }

    /// <summary>
    /// Effective <see cref="Rigidbody"/> settings, expressed as <see cref="PhysicsBodyAuthoring"/> values.
    /// </summary>
    sealed class BodyData
    {
        public BodyMotionType MotionType;
        public float Mass;
        public float LinearDamping;
        public float AngularDamping;
        public float GravityFactor;
        public BodySmoothing Smoothing;
        public bool OverrideMassDistribution;
        public MassDistribution MassDistribution;
        public bool HasSolverType;
        public SolverType SolverType;
        public uint WorldIndex;
    }

    /// <summary>
    /// Effective <see cref="UnityEngine.Collider"/> settings, expressed as <see cref="PhysicsShapeAuthoring"/> values.
    /// </summary>
    sealed class ShapeData
    {
        public bool Enabled;
        public ShapeType ShapeType;
        public float3 Center;
        public float3 Size;
        public quaternion Orientation;
        public float Radius;
        public float Height;
        public float BevelRadius;
        public Mesh Mesh;
        public UnityPhysicsMaterial Material;
        public CollisionResponsePolicy CollisionResponse;
        public uint BelongsTo;
        public uint CollidesWith;
        public bool DetailedStaticMeshCollision;
    }

    /// <summary> The custom joint type and values that replace a PhysX joint. </summary>
    sealed class JointData
    {
        public Type ComponentType;
        public Action<BaseJoint> Write;
    }

    /// <summary>
    /// Replaces built-in PhysX authoring components with the custom ECS physics authoring components, matching the
    /// output of the PhysX bakers in <c>Unity.Physics.Hybrid</c> as closely as the custom components allow.
    /// </summary>
    [NoAutoStaticsCleanup] // The only static state is an immutable lookup table.
    static partial class PhysXAuthoringConverter
    {
        static readonly IReadOnlyDictionary<UnityPhysicsMaterialCombine, Material.CombinePolicy> s_CombineLookup =
            new Dictionary<UnityPhysicsMaterialCombine, Material.CombinePolicy>
            {
                { UnityPhysicsMaterialCombine.Average, Material.CombinePolicy.ArithmeticMean },
                { UnityPhysicsMaterialCombine.Maximum, Material.CombinePolicy.Maximum },
                { UnityPhysicsMaterialCombine.Minimum, Material.CombinePolicy.Minimum },
            };

        /// <summary> Returns true for the collider types that the PhysX bakers convert. </summary>
        internal static bool IsSupportedCollider(UnityEngine.Collider collider) =>
            collider is UnityEngine.BoxCollider || collider is UnityEngine.SphereCollider ||
            collider is UnityEngine.CapsuleCollider || collider is UnityEngine.MeshCollider;

        /// <summary> Replaces owned PhysX authoring components in the hierarchy of <paramref name="root"/>. </summary>
        /// <returns> True if anything was changed. </returns>
        internal static bool Convert(GameObject root, ConversionContext context)
        {
            var report = context.Report;

            // Read everything before writing, because shapes and joints read values from the Rigidbodies.
            var removing = new HashSet<Component>();
            var bodies = new List<(Rigidbody Source, BodyData Data)>();
            var shapes = new List<(UnityEngine.Collider Source, ShapeData Data)>();
            var joints = new List<(UnityEngine.Joint Source, JointData Data)>();
            var skippedBodies = new HashSet<GameObject>();

            foreach (var joint in root.GetComponentsInChildren<UnityEngine.Joint>(true))
            {
                if (context.IsOwned(joint))
                    removing.Add(joint);
            }

            foreach (var worldIndex in root.GetComponentsInChildren<PhysicsWorldIndexAuthoring>(true))
            {
                if (context.IsOwned(worldIndex))
                    removing.Add(worldIndex);
            }

            foreach (var rigidbody in root.GetComponentsInChildren<Rigidbody>(true))
            {
                if (!context.IsOwned(rigidbody))
                    continue;

                if (FindForeignRequirement(rigidbody, removing, out var dependent))
                {
                    report.Error(rigidbody, $"Not converted, because {dependent.GetType().Name} requires a Rigidbody.");
                    skippedBodies.Add(rigidbody.gameObject);
                    continue;
                }

                removing.Add(rigidbody);
                bodies.Add((rigidbody, ReadBody(rigidbody, report)));
            }

            foreach (var collider in root.GetComponentsInChildren<UnityEngine.Collider>(true))
            {
                if (!context.IsOwned(collider))
                    continue;

                if (!IsSupportedCollider(collider))
                {
                    report.Warning(collider, "Not converted, because the PhysX bakers do not support this type.");
                    continue;
                }

                if (FindForeignRequirement(collider, removing, out var dependent))
                {
                    report.Error(collider, $"Not converted, because {dependent.GetType().Name} requires it.");
                    continue;
                }

                removing.Add(collider);
                shapes.Add((collider, ReadShape(collider, report)));
            }

            foreach (var joint in root.GetComponentsInChildren<UnityEngine.Joint>(true))
            {
                if (!context.IsOwned(joint))
                    continue;

                if (skippedBodies.Contains(joint.gameObject))
                {
                    removing.Remove(joint);
                    report.Error(joint, "Not converted, because its Rigidbody was not converted.");
                    continue;
                }

                var data = ReadJoint(joint, report);
                if (data == null)
                {
                    removing.Remove(joint);
                    continue;
                }

                joints.Add((joint, data));
            }

            // Write the replacement components.
            foreach (var (source, data) in bodies)
            {
                var gameObject = source.gameObject;
                var body = gameObject.GetComponent<PhysicsBodyAuthoring>();
                if (body != null)
                    report.Warning(gameObject, "Already had a Physics Body. The Rigidbody overwrote its values.");
                else
                    body = gameObject.AddComponent<PhysicsBodyAuthoring>();

                WriteBody(data, body);
                context.Converted?.Invoke(source, body);
                report.ConvertedBodies++;
            }

            foreach (var (source, data) in shapes)
            {
                var shape = source.gameObject.AddComponent<PhysicsShapeAuthoring>();
                WriteShape(data, shape, context);
                context.Converted?.Invoke(source, shape);
                report.ConvertedShapes++;
            }

            ConvertStaticSolverTypes(root, context);

            foreach (var (source, data) in joints)
            {
                var joint = (BaseJoint)source.gameObject.AddComponent(data.ComponentType);
                data.Write(joint);
                context.Converted?.Invoke(source, joint);
                report.ConvertedJoints++;
            }

            foreach (var helper in root.GetComponentsInChildren<SolverTypeAuthoring>(true))
            {
                if (context.IsOwned(helper) && !skippedBodies.Contains(helper.gameObject))
                    removing.Add(helper);
            }

            foreach (var helper in root.GetComponentsInChildren<DetailedStaticMeshCollisionAuthoring>(true))
            {
                if (context.IsOwned(helper))
                    removing.Add(helper);
            }

            foreach (var gameObject in skippedBodies)
            {
                var worldIndex = gameObject.GetComponent<PhysicsWorldIndexAuthoring>();
                if (worldIndex != null)
                    removing.Remove(worldIndex);
            }

            // Remove dependents before the components they require.
            var changed = bodies.Count > 0 || shapes.Count > 0 || joints.Count > 0;
            changed |= RemoveAll<UnityEngine.Joint>(removing);
            changed |= RemoveAll<PhysicsWorldIndexAuthoring>(removing);
            changed |= RemoveAll<SolverTypeAuthoring>(removing);
            changed |= RemoveAll<DetailedStaticMeshCollisionAuthoring>(removing);
            changed |= RemoveAll<UnityEngine.Collider>(removing);
            changed |= RemoveAll<Rigidbody>(removing);
            return changed;
        }

        static bool RemoveAll<T>(HashSet<Component> removing) where T : Component
        {
            var removed = false;
            removing.RemoveWhere(component =>
            {
                if (!(component is T))
                    return false;

                UnityEngine.Object.DestroyImmediate(component, true);
                removed = true;
                return true;
            });
            return removed;
        }

        /// <summary> Finds a component that requires <paramref name="target"/> and is not being removed. </summary>
        static bool FindForeignRequirement(Component target, HashSet<Component> removing, out Component dependent)
        {
            var targetType = target.GetType();
            foreach (var component in target.GetComponents<Component>())
            {
                if (component == null || component == target || removing.Contains(component))
                    continue;

                var attributes = (RequireComponent[])Attribute.GetCustomAttributes(
                    component.GetType(), typeof(RequireComponent), true);
                foreach (var attribute in attributes)
                {
                    // A requirement only blocks removal if no other component of the required type remains.
                    if (IsBlocking(attribute.m_Type0) || IsBlocking(attribute.m_Type1) || IsBlocking(attribute.m_Type2))
                    {
                        dependent = component;
                        return true;
                    }
                }
            }

            dependent = null;
            return false;

            bool IsBlocking(Type required) =>
                required != null && required.IsAssignableFrom(targetType) &&
                !HasRemainingComponent(target.gameObject, required, target, removing);
        }

        static bool HasRemainingComponent(
            GameObject gameObject, Type type, Component target, HashSet<Component> removing)
        {
            foreach (var component in gameObject.GetComponents(type))
            {
                if (component != target && !removing.Contains(component))
                    return true;
            }

            return false;
        }

        // ---- Rigidbody ------------------------------------------------------------------------------------------

        /// <summary> Reads a Rigidbody like <c>RigidbodyBaker</c> and <c>RigidbodyBakingSystem</c> do. </summary>
        internal static BodyData ReadBody(Rigidbody rigidbody, ConversionReport report)
        {
            var data = new BodyData
            {
                MotionType = rigidbody.isKinematic ? BodyMotionType.Kinematic : BodyMotionType.Dynamic,
                Mass = rigidbody.mass,
                LinearDamping = rigidbody.linearDamping,
                AngularDamping = rigidbody.angularDamping,
                GravityFactor = rigidbody.isKinematic || rigidbody.useGravity ? 1f : 0f,
                Smoothing = rigidbody.interpolation switch
                {
                    RigidbodyInterpolation.Interpolate => BodySmoothing.Interpolation,
                    RigidbodyInterpolation.Extrapolate => BodySmoothing.Extrapolation,
                    _ => BodySmoothing.None,
                },
            };

            var worldIndex = rigidbody.GetComponent<PhysicsWorldIndexAuthoring>();
            if (worldIndex != null)
                data.WorldIndex = worldIndex.WorldIndex;

            var solverType = rigidbody.GetComponent<SolverTypeAuthoring>();
            if (solverType != null && solverType.isActiveAndEnabled)
            {
                data.HasSolverType = true;
                data.SolverType = solverType.ContactSolverType;
                report?.Info(rigidbody, "The PhysX bakers ignore the Solver Type contact setting on Rigidbodies. " +
                    "It is now applied to the Physics Body.");
            }

            if (!rigidbody.automaticCenterOfMass || !rigidbody.automaticInertiaTensor)
            {
                // The PhysX baker treats Rigidbody.inertiaTensor as a unit-mass tensor, which is what the custom
                // mass distribution expects too.
                data.OverrideMassDistribution = true;
                var inertia = (float3)rigidbody.inertiaTensor;
                if (rigidbody.automaticInertiaTensor)
                    inertia /= math.max(rigidbody.mass, 1e-6f);

                data.MassDistribution = new MassDistribution
                {
                    Transform = new RigidTransform(rigidbody.inertiaTensorRotation, rigidbody.centerOfMass),
                    InertiaTensor = inertia,
                };

                if (rigidbody.automaticCenterOfMass != rigidbody.automaticInertiaTensor)
                {
                    report?.Warning(rigidbody, "Only one of center of mass and inertia tensor is automatic. " +
                        "The automatic one is approximated from the PhysX values, because the Physics Body can " +
                        "only override both together.");
                }

                report?.Info(rigidbody, "A custom mass distribution also widens the body's angular expansion " +
                    "factor, which the PhysX bakers do not do.");
            }

            if (rigidbody.constraints != RigidbodyConstraints.None)
            {
                report?.Info(rigidbody, "Rigidbody constraints were dropped. The PhysX bakers ignore them too. " +
                    "Use a Limit DOF Joint to lock axes.");
            }

            return data;
        }

        internal static void WriteBody(BodyData data, PhysicsBodyAuthoring body)
        {
            body.MotionType = data.MotionType;
            body.Mass = data.Mass;
            body.LinearDamping = data.LinearDamping;
            body.AngularDamping = data.AngularDamping;
            body.GravityFactor = data.GravityFactor;
            body.Smoothing = data.Smoothing;
            body.InitialLinearVelocity = float3.zero;
            body.InitialAngularVelocity = float3.zero;
            body.WorldIndex = data.WorldIndex;
            if (data.HasSolverType)
                body.SolverType = data.SolverType;

            if (data.OverrideMassDistribution)
                body.CustomMassDistribution = data.MassDistribution;
            else
                body.OverrideDefaultMassDistribution = false;
        }

        /// <summary>
        /// Adds a static Physics Body for <see cref="SolverTypeAuthoring"/> on a collider root without a Rigidbody,
        /// which is where <c>ColliderBaker</c> applies its contact solver type.
        /// </summary>
        static void ConvertStaticSolverTypes(GameObject root, ConversionContext context)
        {
            foreach (var solverType in root.GetComponentsInChildren<SolverTypeAuthoring>(true))
            {
                if (!context.IsOwned(solverType) || !solverType.isActiveAndEnabled)
                    continue;

                // Only collider roots without any body take the contact solver type. Rigidbodies still exist here,
                // because the PhysX components are removed after all replacements are written.
                var gameObject = solverType.gameObject;
                if (gameObject.GetComponentInParent<Rigidbody>(true) != null ||
                    gameObject.GetComponentInParent<PhysicsBodyAuthoring>(true) != null ||
                    gameObject.GetComponent<PhysicsShapeAuthoring>() == null)
                    continue;

                var parent = gameObject.transform.parent;
                if (parent != null && parent.GetComponentInParent<PhysicsShapeAuthoring>(true) != null)
                    continue;

                var body = gameObject.AddComponent<PhysicsBodyAuthoring>();
                body.MotionType = BodyMotionType.Static;
                body.SolverType = solverType.ContactSolverType;
                context.Report.Info(gameObject, "Added a static Physics Body to keep the Solver Type contact setting.");
            }
        }

        // ---- Collider -------------------------------------------------------------------------------------------

        /// <summary> Reads a collider like <c>ColliderBaker</c> and its shape bakers do. </summary>
        internal static ShapeData ReadShape(UnityEngine.Collider collider, ConversionReport report)
        {
            var body = GetPrimaryBody(collider.gameObject, out var rigidbody);
            var bakeToShape = GetColliderBakeMatrix(
                collider.transform.localToWorldMatrix, body.transform.localToWorldMatrix, out var bodyUniformScale);

            var data = new ShapeData
            {
                Enabled = collider.enabled,
                Orientation = quaternion.identity,
                Material = collider.sharedMaterial,
                CollisionResponse = collider.isTrigger ? CollisionResponsePolicy.RaiseTriggerEvents
                    : collider.providesContacts ? CollisionResponsePolicy.CollideRaiseCollisionEvents
                    : CollisionResponsePolicy.Collide,
            };

            var detailed = collider.GetComponent<DetailedStaticMeshCollisionAuthoring>();
            data.DetailedStaticMeshCollision = detailed != null && detailed.isActiveAndEnabled;

            ReadFilter(collider, rigidbody, out data.BelongsTo, out data.CollidesWith);

            switch (collider)
            {
                case UnityEngine.BoxCollider box:
                {
                    data.ShapeType = ShapeType.Box;
                    data.Center = box.center;
                    data.Size = box.size;
                    var bakedSize = math.abs((float3)box.size * (float3)bakeToShape.lossyScale);
                    data.BevelRadius = math.min(
                        ConvexHullGenerationParameters.Default.BevelRadius / bodyUniformScale,
                        math.cmin(bakedSize) * 0.1f);
                    break;
                }

                case UnityEngine.SphereCollider sphere:
                    data.ShapeType = ShapeType.Sphere;
                    data.Center = sphere.center;
                    data.Radius = sphere.radius;
                    break;

                case UnityEngine.CapsuleCollider capsule:
                    data.ShapeType = ShapeType.Capsule;
                    data.Center = capsule.center;
                    data.Radius = capsule.radius;
                    data.Height = capsule.height;

                    // The custom capsule runs along its local Z axis.
                    data.Orientation = capsule.direction switch
                    {
                        0 => quaternion.RotateY(math.PI * 0.5f),
                        1 => quaternion.RotateX(-math.PI * 0.5f),
                        _ => quaternion.identity,
                    };
                    break;

                case UnityEngine.MeshCollider mesh:
                    data.ShapeType = mesh.convex ? ShapeType.ConvexHull : ShapeType.Mesh;
                    data.Mesh = mesh.sharedMesh;
                    if (mesh.sharedMesh == null)
                    {
                        report?.Warning(collider, "The Mesh Collider has no mesh. The PhysX bakers fail on it, " +
                            "while the Physics Shape falls back to the meshes of its children.");
                    }

                    break;
            }

            ReadMaterial(collider, report);
            return data;
        }

        static void ReadMaterial(UnityEngine.Collider collider, ConversionReport report)
        {
            var material = collider.sharedMaterial;
            if (material == null || report == null)
                return;

            if (!s_CombineLookup.ContainsKey(material.frictionCombine) ||
                !s_CombineLookup.ContainsKey(material.bounceCombine))
            {
                report.Warning(collider, $"{material.name} uses Multiply combine mode, which the PhysX bakers " +
                    "replace with the Unity Physics default.");
            }

            if (!Mathf.Approximately(material.staticFriction, material.dynamicFriction))
                report.Info(collider, $"{material.name} static friction was dropped, as the PhysX bakers do.");
        }

        /// <summary> Builds a collision filter like <c>ColliderBaker.ProduceCollisionFilter</c>. </summary>
        static void ReadFilter(
            UnityEngine.Collider collider, Rigidbody rigidbody, out uint belongsTo, out uint collidesWith)
        {
            var layer = collider.gameObject.layer;
            belongsTo = 1u << layer;

            var includeMask = 0u;
            for (var i = 0; i < 32; ++i)
                includeMask |= UnityEngine.Physics.GetIgnoreLayerCollision(layer, i) ? 0 : 1u << i;

            includeMask |= (uint)collider.includeLayers.value;
            var excludeMask = (uint)collider.excludeLayers.value;

            if (rigidbody != null)
            {
                includeMask |= (uint)rigidbody.includeLayers.value;
                excludeMask |= (uint)rigidbody.excludeLayers.value;
            }

            collidesWith = includeMask & ~excludeMask;
        }

        /// <summary>
        /// Stores <paramref name="orientation"/> in the rotation order that reproduces it most precisely. A middle
        /// Euler angle near 90 degrees is at gimbal lock and loses precision, such as a capsule along Y in ZXY order.
        /// </summary>
        static EulerAngles ToEulerAngles(quaternion orientation)
        {
            var best = EulerAngles.Default;
            var bestError = float.MaxValue;
            foreach (var order in new[] { math.RotationOrder.ZXY, math.RotationOrder.XYZ, math.RotationOrder.YZX })
            {
                var euler = new EulerAngles { RotationOrder = order };
                euler.SetValue(orientation);
                var error = 1f - math.abs(math.dot(((quaternion)euler).value, math.normalize(orientation).value));
                if (error < bestError)
                {
                    best = euler;
                    bestError = error;
                }
            }

            return best;
        }

        internal static void WriteShape(ShapeData data, PhysicsShapeAuthoring shape, ConversionContext context)
        {
            shape.enabled = data.Enabled;

            var orientation = ToEulerAngles(data.Orientation);

            switch (data.ShapeType)
            {
                case ShapeType.Box:
                    shape.SetBox(new BoxGeometry
                    {
                        Center = data.Center,
                        Size = data.Size,
                        Orientation = data.Orientation,
                        BevelRadius = data.BevelRadius,
                    }, orientation);
                    break;
                case ShapeType.Sphere:
                    shape.SetSphere(new SphereGeometry { Center = data.Center, Radius = data.Radius }, orientation);
                    break;
                case ShapeType.Capsule:
                    var capsule = new CapsuleGeometryAuthoring
                    {
                        OrientationEuler = orientation,
                        Center = data.Center,
                        Height = data.Height,
                        Radius = data.Radius,
                    };
                    shape.SetCapsule(capsule);
                    break;
                case ShapeType.ConvexHull:
                    shape.SetConvexHull(ConvexHullGenerationParameters.Default.ToAuthoring(), data.Mesh);
                    break;
                case ShapeType.Mesh:
                    shape.SetMesh(data.Mesh);
                    break;
            }

            var template = context.Templates?.GetTemplate(data.Material);
            shape.MaterialTemplate = template;
            if (template == null)
            {
                var material = data.Material != null ? data.Material : new UnityPhysicsMaterial();
                shape.Friction = ToCoefficient(material.dynamicFriction, material.frictionCombine);
                shape.Restitution = ToCoefficient(material.bounciness, material.bounceCombine);
            }
            else
            {
                shape.OverrideFriction = false;
                shape.OverrideRestitution = false;
            }

            if (template == null || data.CollisionResponse != template.CollisionResponse)
                shape.CollisionResponse = data.CollisionResponse;
            else
                shape.OverrideCollisionResponse = false;

            shape.BelongsTo = new PhysicsCategoryTags { Value = data.BelongsTo };
            shape.CollidesWith = new PhysicsCategoryTags { Value = data.CollidesWith };
            shape.OverrideCustomTags = false;

            if (data.DetailedStaticMeshCollision)
                shape.DetailedStaticMeshCollision = new PhysicsMaterialFlag { Value = true };
            else
                shape.OverrideDetailedStaticMeshCollision = false;
        }

        /// <summary> Writes a PhysX material into a template, like <c>ColliderBaker.ProduceMaterial</c>. </summary>
        internal static void WriteTemplate(UnityPhysicsMaterial material, PhysicsMaterialTemplate template)
        {
            material = material != null ? material : new UnityPhysicsMaterial();
            template.Friction = ToCoefficient(material.dynamicFriction, material.frictionCombine);
            template.Restitution = ToCoefficient(material.bounciness, material.bounceCombine);
            template.CollisionResponse = CollisionResponsePolicy.Collide;
            template.BelongsTo = PhysicsCategoryTags.Everything;
            template.CollidesWith = PhysicsCategoryTags.Everything;
            template.CustomTags = CustomPhysicsMaterialTags.Nothing;
            template.DetailedStaticMeshCollision = new PhysicsMaterialFlag { Value = false };
        }

        static PhysicsMaterialCoefficient ToCoefficient(float value, UnityPhysicsMaterialCombine combine) =>
            new PhysicsMaterialCoefficient
            {
                Value = value,
                CombineMode = s_CombineLookup.TryGetValue(combine, out var policy)
                    ? policy
                    : new Material().FrictionCombinePolicy,
            };

        /// <summary> Finds the body a collider bakes into, like <c>ColliderBaker.GetPrimaryBody</c>. </summary>
        static GameObject GetPrimaryBody(GameObject shape, out Rigidbody rigidbody)
        {
            rigidbody = shape.GetComponentInParent<Rigidbody>(true);
            if (rigidbody != null)
                return rigidbody.gameObject;

            ColliderExtensions.FindTopmostStaticEnabledAncestor(shape, out var topStatic);
            if (topStatic != null)
                return topStatic;

            var colliders = shape.GetComponentsInParent<UnityEngine.Collider>(true);
            for (var i = colliders.Length - 1; i >= 0; --i)
            {
                if (colliders[i].enabled)
                    return colliders[i].gameObject;
            }

            return shape;
        }

        /// <summary> Copy of <c>BaseColliderBaker.GetColliderBakeMatrix</c>. </summary>
        static Matrix4x4 GetColliderBakeMatrix(
            Matrix4x4 localToWorldMatrix, Matrix4x4 bodyLocalToWorldMatrix, out float bodyUniformScale)
        {
            bodyUniformScale = 1f;
            var localToWorld = (float4x4)localToWorldMatrix;
            var bodyLocalToWorld = (float4x4)bodyLocalToWorldMatrix;

            var relativeTransform = math.mul(math.inverse(bodyLocalToWorld), localToWorld);
            var hasScaleOrShearRelativeToBody = relativeTransform.HasNonIdentityScale() || relativeTransform.HasShear();

            if (hasScaleOrShearRelativeToBody && !bodyLocalToWorld.HasShear() && !bodyLocalToWorld.HasNonUniformScale())
            {
                var bodyScale = bodyLocalToWorld.DecomposeScale();
                localToWorld = math.mul(localToWorld, float4x4.Scale(1 / bodyScale));
            }

            if (hasScaleOrShearRelativeToBody || localToWorld.HasShear() || localToWorld.HasNonUniformScale())
            {
                var rigidBodyTransform = Math.DecomposeRigidBodyTransform(localToWorld);
                var bakeMatrix = math.mul(math.inverse(new float4x4(rigidBodyTransform)), localToWorld);
                bakeMatrix.c0[3] = 0;
                bakeMatrix.c1[3] = 0;
                bakeMatrix.c2[3] = 0;
                bakeMatrix.c3[3] = 1;
                return bakeMatrix;
            }

            bodyUniformScale = bodyLocalToWorldMatrix.lossyScale[0];
            return Matrix4x4.identity;
        }
    }
}
