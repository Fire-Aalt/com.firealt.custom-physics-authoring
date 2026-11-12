using System;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEngine;
using FloatRange = Unity.Physics.Math.FloatRange;

namespace Unity.Physics.Editor
{
    static partial class PhysXAuthoringConverter
    {
        const float SPRING_THRESHOLD = 0.001f;

        /// <summary> Values shared by all joint mappings, read like <c>BaseJointBaker</c> does. </summary>
        struct JointCommon
        {
            // The path resolves the connected body after its asset was unloaded and loaded again.
            public GameObject Connected;
            public string ConnectedPath;
            public bool EnableCollision;
            public float3 MaxImpulse;
            public SolverType SolverType;
            public float3 AnchorA;
            public float3 ConnectedAnchor;
            public bool AutoConnect;
            public RigidTransform BFromA;
        }

        /// <summary> Reads a PhysX joint and returns the custom joint that replaces it, or null if none can. </summary>
        internal static JointData ReadJoint(UnityEngine.Joint joint, ConversionReport report)
        {
            if (joint.connectedArticulationBody != null)
            {
                report?.Error(joint, "Not converted, because joints to an Articulation Body are not supported.");
                return null;
            }

            var common = ReadCommon(joint, report);
            switch (joint)
            {
                case FixedJoint fixedJoint:
                    return ReadFixed(fixedJoint, common, report);
                case HingeJoint hinge:
                    return ReadHinge(hinge, common, report);
                case SpringJoint spring:
                    return ReadSpring(spring, common, report);
                case CharacterJoint character:
                    return ReadCharacter(character, common, report);
                case ConfigurableJoint configurable:
                    return ReadConfigurable(configurable, common, report);
                default:
                    report?.Warning(joint, "Not converted, because this joint type is not supported.");
                    return null;
            }
        }

        static JointCommon ReadCommon(UnityEngine.Joint joint, ConversionReport report)
        {
            var connected = joint.connectedBody != null ? joint.connectedBody.gameObject : null;

            var solverType = SolverType.Iterative;
            var solverTypeAuthoring = joint.GetComponent<SolverTypeAuthoring>();
            if (solverTypeAuthoring != null && solverTypeAuthoring.enabled)
                solverType = solverTypeAuthoring.JointSolverType;

            if (!Mathf.Approximately(joint.breakForce, joint.breakTorque))
            {
                report?.Warning(joint, "Break force and break torque differ. The custom joint uses the break force " +
                    "impulse for all constraints.");
            }

            if (!Mathf.Approximately(joint.massScale, 1f) || !Mathf.Approximately(joint.connectedMassScale, 1f))
                report?.Info(joint, "Mass scales were dropped. The PhysX bakers ignore them too.");

            var worldFromA = Math.DecomposeRigidBodyTransform(joint.transform.localToWorldMatrix);
            var worldFromB = connected != null
                ? Math.DecomposeRigidBodyTransform(connected.transform.localToWorldMatrix)
                : RigidTransform.identity;

            return new JointCommon
            {
                Connected = connected,
                ConnectedPath = connected != null ? GetObjectPath(connected.transform) : null,
                EnableCollision = joint.enableCollision,
                MaxImpulse = joint.breakForce * Time.fixedDeltaTime,
                SolverType = solverType,
                AnchorA = GetScaledLocalAnchorPosition(joint.transform, joint.anchor),
                ConnectedAnchor = connected != null
                    ? GetScaledLocalAnchorPosition(connected.transform, joint.connectedAnchor)
                    : (float3)joint.connectedAnchor,
                AutoConnect = joint.autoConfigureConnectedAnchor,
                BFromA = math.mul(math.inverse(worldFromB), worldFromA),
            };
        }

        static void WriteCommon(BaseJoint target, in JointCommon common, ConversionReport report)
        {
            PhysicsBodyAuthoring connectedBody = null;
            if (common.ConnectedPath != null)
            {
                var connected = common.Connected != null
                    ? common.Connected
                    : FindByPath(target.gameObject.scene.GetRootGameObjects(), common.ConnectedPath);
                connectedBody = connected != null ? connected.GetComponent<PhysicsBodyAuthoring>() : null;
                if (connectedBody == null)
                {
                    report?.Error(target, "The connected Rigidbody was not converted, so the joint now connects " +
                        "to the world. Convert the connected body too.");
                }
            }

            target.ConnectedBody = connectedBody;
            target.EnableCollision = common.EnableCollision;
            target.MaxImpulse = common.MaxImpulse;
            target.SolverType = common.SolverType;
        }

        static void WriteBallAndSocket(BallAndSocketJoint target, in JointCommon common, float3 anchor)
        {
            target.AutoSetConnected = common.AutoConnect;
            target.PositionLocal = anchor;
            target.PositionInConnectedEntity =
                common.AutoConnect ? math.transform(common.BFromA, anchor) : common.ConnectedAnchor;
        }

        static JointData ReadFixed(FixedJoint joint, JointCommon common, ConversionReport report)
        {
            // FixedBaker uses the unscaled anchor and always derives the connected frame from the current pose.
            var anchor = (float3)joint.anchor;
            return new JointData
            {
                ComponentType = typeof(RigidJoint),
                Write = target =>
                {
                    var rigid = (RigidJoint)target;
                    WriteCommon(rigid, common, report);
                    rigid.AutoSetConnected = true;
                    rigid.PositionLocal = anchor;
                    rigid.PositionInConnectedEntity = math.transform(common.BFromA, anchor);
                    rigid.OrientationLocal = quaternion.identity;
                    rigid.OrientationInConnectedEntity = common.BFromA.rot;
                },
            };
        }

        static JointData ReadHinge(HingeJoint joint, JointCommon common, ConversionReport report)
        {
            var axis = (float3)joint.axis;
            Math.CalculatePerpendicularNormalized(axis, out var perpendicular, out _);
            var limits = new FloatRange(joint.limits.min, joint.limits.max).Sorted();
            ConvertSpringDamperSettings(joint.spring.spring, joint.spring.damper, GetConstrainedBodyInertia(joint),
                out var springFrequency, out var dampingRatio);

            if (joint.useSpring && !joint.useMotor)
            {
                if (joint.useLimits)
                    report?.Warning(joint, "Hinge limits were dropped, because the Rotational Motor has no limits.");

                WarnMotorAnchor(joint, common, report);
                var maxImpulseOfMotor = joint.breakTorque * Time.fixedDeltaTime;
                var target = joint.spring.targetPosition;
                return new JointData
                {
                    ComponentType = typeof(RotationalMotor),
                    Write = component =>
                    {
                        var motor = (RotationalMotor)component;
                        WriteCommon(motor, common, report);
                        motor.PivotPosition = common.AnchorA;
                        motor.AxisOfRotation = axis;
                        motor.TargetAngle = target;
                        motor.MaxImpulseAppliedByMotor = maxImpulseOfMotor;
                        motor.SpringFrequency = springFrequency;
                        motor.DampingRatio = dampingRatio;
                    },
                };
            }

            if (joint.useMotor)
            {
                if (!Mathf.Approximately(springFrequency, Constraint.DefaultSpringFrequency))
                {
                    report?.Warning(joint, "The motor spring was dropped, because the Angular Velocity Motor " +
                        "always uses the default spring frequency.");
                }

                WarnMotorAnchor(joint, common, report);
                var maxImpulseOfMotor = joint.motor.force * Time.fixedDeltaTime;
                var targetSpeed = joint.motor.targetVelocity;
                return new JointData
                {
                    ComponentType = typeof(AngularVelocityMotor),
                    Write = component =>
                    {
                        var motor = (AngularVelocityMotor)component;
                        WriteCommon(motor, common, report);
                        motor.PivotPosition = common.AnchorA;
                        motor.AxisOfRotation = axis;
                        motor.TargetSpeed = targetSpeed;
                        motor.MaxImpulseAppliedByMotor = maxImpulseOfMotor;
                        motor.DampingRatio = dampingRatio;
                    },
                };
            }

            if (joint.useLimits)
            {
                return new JointData
                {
                    ComponentType = typeof(LimitedHingeJoint),
                    Write = component =>
                    {
                        var hinge = (LimitedHingeJoint)component;
                        WriteCommon(hinge, common, report);
                        WriteBallAndSocket(hinge, common, common.AnchorA);
                        hinge.HingeAxisLocal = axis;
                        hinge.HingeAxisInConnectedEntity = math.rotate(common.BFromA.rot, axis);
                        hinge.PerpendicularAxisLocal = perpendicular;
                        hinge.PerpendicularAxisInConnectedEntity = math.rotate(common.BFromA.rot, perpendicular);
                        hinge.MinAngle = limits.Min;
                        hinge.MaxAngle = limits.Max;
                    },
                };
            }

            return new JointData
            {
                ComponentType = typeof(FreeHingeJoint),
                Write = component =>
                {
                    var hinge = (FreeHingeJoint)component;
                    WriteCommon(hinge, common, report);
                    WriteBallAndSocket(hinge, common, common.AnchorA);
                    hinge.HingeAxisLocal = axis;
                    hinge.HingeAxisInConnectedEntity = math.rotate(common.BFromA.rot, axis);
                },
            };
        }

        static void WarnMotorAnchor(UnityEngine.Joint joint, in JointCommon common, ConversionReport report)
        {
            if (!common.AutoConnect)
            {
                report?.Warning(joint, "The connected anchor was dropped, because custom motors always derive it " +
                    "from the current pose.");
            }
        }

        static JointData ReadSpring(SpringJoint joint, JointCommon common, ConversionReport report)
        {
            report?.Warning(joint, "The spring and damper were dropped, because the Limited Distance Joint has a " +
                "stiff distance limit.");

            var range = new FloatRange(joint.minDistance, joint.maxDistance).Sorted();
            return new JointData
            {
                ComponentType = typeof(LimitedDistanceJoint),
                Write = component =>
                {
                    var distance = (LimitedDistanceJoint)component;
                    WriteCommon(distance, common, report);
                    WriteBallAndSocket(distance, common, common.AnchorA);
                    distance.MinDistance = range.Min;
                    distance.MaxDistance = range.Max;
                },
            };
        }

        static JointData ReadCharacter(CharacterJoint joint, JointCommon common, ConversionReport report)
        {
            report?.Warning(joint, "Converted to a Ragdoll Joint. Swing limits become a cone, so the result is " +
                "approximate, and limit springs and bounciness were dropped.");

            var frame = GetJointFrameOrientation(joint.axis, joint.swingAxis);
            var twistAxis = math.rotate(frame, new float3(1f, 0f, 0f));
            var perpendicular = math.rotate(frame, new float3(0f, 1f, 0f));
            var twist = new FloatRange(joint.lowTwistLimit.limit, joint.highTwistLimit.limit).Sorted();
            var cone = joint.swing1Limit.limit;
            var swing2 = joint.swing2Limit.limit;
            var perpendicularRange = new FloatRange(-swing2, swing2);
            return CreateRagdoll(common, report, twistAxis, perpendicular, cone, perpendicularRange, twist);
        }

        static JointData CreateRagdoll(
            JointCommon common, ConversionReport report, float3 twistAxis, float3 perpendicular, float cone,
            FloatRange perpendicularRange, FloatRange twist)
        {
            return new JointData
            {
                ComponentType = typeof(RagdollJoint),
                Write = component =>
                {
                    var ragdoll = (RagdollJoint)component;

                    // A new component is at version 0, which would shift the perpendicular range by 90 degrees.
                    ragdoll.UpgradeVersionIfNecessary();
                    WriteCommon(ragdoll, common, report);
                    WriteBallAndSocket(ragdoll, common, common.AnchorA);
                    ragdoll.TwistAxisLocal = twistAxis;
                    ragdoll.TwistAxisInConnectedEntity = math.rotate(common.BFromA.rot, twistAxis);
                    ragdoll.PerpendicularAxisLocal = perpendicular;
                    ragdoll.PerpendicularAxisInConnectedEntity = math.rotate(common.BFromA.rot, perpendicular);
                    ragdoll.MaxConeAngle = math.clamp(cone, 0f, 180f);
                    ragdoll.MinPerpendicularAngle = math.clamp(perpendicularRange.Min, -90f, 90f);
                    ragdoll.MaxPerpendicularAngle = math.clamp(perpendicularRange.Max, -90f, 90f);
                    ragdoll.MinTwistAngle = math.clamp(twist.Min, -180f, 180f);
                    ragdoll.MaxTwistAngle = math.clamp(twist.Max, -180f, 180f);
                },
            };
        }

        static JointData ReadConfigurable(ConfigurableJoint joint, JointCommon common, ConversionReport report)
        {
            if (HasActiveDrive(joint.xDrive) || HasActiveDrive(joint.yDrive) || HasActiveDrive(joint.zDrive) ||
                HasActiveDrive(joint.angularXDrive) || HasActiveDrive(joint.angularYZDrive) ||
                HasActiveDrive(joint.slerpDrive))
            {
                report?.Warning(joint, "Drives were dropped. Add a motor component to restore them.");
            }

            var linear = new[] { joint.xMotion, joint.yMotion, joint.zMotion };
            var angular = new[] { joint.angularXMotion, joint.angularYMotion, joint.angularZMotion };
            var frame = GetJointFrameOrientation(joint.axis, joint.secondaryAxis);
            var axes = new[]
            {
                math.rotate(frame, new float3(1f, 0f, 0f)),
                math.rotate(frame, new float3(0f, 1f, 0f)),
                math.rotate(frame, new float3(0f, 0f, 1f)),
            };

            var linearLocked = Count(linear, ConfigurableJointMotion.Locked);
            var angularLocked = Count(angular, ConfigurableJointMotion.Locked);
            var angularFree = Count(angular, ConfigurableJointMotion.Free);

            if (linearLocked == 3 && angularLocked == 3)
            {
                return new JointData
                {
                    ComponentType = typeof(RigidJoint),
                    Write = component =>
                    {
                        var rigid = (RigidJoint)component;
                        WriteCommon(rigid, common, report);
                        WriteBallAndSocket(rigid, common, common.AnchorA);
                        rigid.OrientationLocal = frame;
                        rigid.OrientationInConnectedEntity = math.mul(common.BFromA.rot, frame);
                    },
                };
            }

            if (linearLocked == 3 && angular[1] == ConfigurableJointMotion.Locked &&
                angular[2] == ConfigurableJointMotion.Locked)
            {
                var axis = axes[0];
                var perpendicular = axes[1];
                if (angular[0] == ConfigurableJointMotion.Free)
                {
                    return new JointData
                    {
                        ComponentType = typeof(FreeHingeJoint),
                        Write = component =>
                        {
                            var hinge = (FreeHingeJoint)component;
                            WriteCommon(hinge, common, report);
                            WriteBallAndSocket(hinge, common, common.AnchorA);
                            hinge.HingeAxisLocal = axis;
                            hinge.HingeAxisInConnectedEntity = math.rotate(common.BFromA.rot, axis);
                        },
                    };
                }

                // ConfigurableBaker limits the twist to (-high, -low).
                var limits = new FloatRange(-joint.highAngularXLimit.limit, -joint.lowAngularXLimit.limit).Sorted();
                WarnLimitSprings(joint, report);
                return new JointData
                {
                    ComponentType = typeof(LimitedHingeJoint),
                    Write = component =>
                    {
                        var hinge = (LimitedHingeJoint)component;
                        WriteCommon(hinge, common, report);
                        WriteBallAndSocket(hinge, common, common.AnchorA);
                        hinge.HingeAxisLocal = axis;
                        hinge.HingeAxisInConnectedEntity = math.rotate(common.BFromA.rot, axis);
                        hinge.PerpendicularAxisLocal = perpendicular;
                        hinge.PerpendicularAxisInConnectedEntity = math.rotate(common.BFromA.rot, perpendicular);
                        hinge.MinAngle = limits.Min;
                        hinge.MaxAngle = limits.Max;
                    },
                };
            }

            if (linearLocked == 3 && angularFree == 3)
            {
                return new JointData
                {
                    ComponentType = typeof(BallAndSocketJoint),
                    Write = component =>
                    {
                        var ball = (BallAndSocketJoint)component;
                        WriteCommon(ball, common, report);
                        WriteBallAndSocket(ball, common, common.AnchorA);
                    },
                };
            }

            if (linearLocked == 3)
            {
                report?.Warning(joint, "Converted to a Ragdoll Joint. Angular limits become a cone, so the result " +
                    "is approximate.");
                WarnLimitSprings(joint, report);

                var twist = angular[0] == ConfigurableJointMotion.Limited
                    ? new FloatRange(-joint.highAngularXLimit.limit, -joint.lowAngularXLimit.limit).Sorted()
                    : angular[0] == ConfigurableJointMotion.Locked
                        ? new FloatRange(0f, 0f)
                        : new FloatRange(-180f, 180f);
                var cone = angular[1] switch
                {
                    ConfigurableJointMotion.Limited => joint.angularYLimit.limit,
                    ConfigurableJointMotion.Locked => 0f,
                    _ => 180f,
                };
                var perpendicularRange = angular[2] switch
                {
                    ConfigurableJointMotion.Limited =>
                        new FloatRange(-joint.angularZLimit.limit, joint.angularZLimit.limit),
                    ConfigurableJointMotion.Locked => new FloatRange(0f, 0f),
                    _ => new FloatRange(-90f, 90f),
                };
                return CreateRagdoll(common, report, axes[0], axes[1], cone, perpendicularRange, twist);
            }

            if (linearLocked == 2 && angularLocked == 3)
            {
                var index = Array.FindIndex(linear, motion => motion != ConfigurableJointMotion.Locked);
                var axis = axes[index];
                var perpendicular = axes[(index + 1) % 3];
                var range = linear[index] == ConfigurableJointMotion.Limited
                    ? new FloatRange(-joint.linearLimit.limit, joint.linearLimit.limit)
                    : new FloatRange(float.NegativeInfinity, float.PositiveInfinity);
                if (linear[index] == ConfigurableJointMotion.Limited)
                {
                    report?.Warning(joint, "Converted to a Prismatic Joint. The linear limit becomes a symmetric " +
                        "range on the free axis, and its spring was dropped.");
                }

                return new JointData
                {
                    ComponentType = typeof(PrismaticJoint),
                    Write = component =>
                    {
                        var prismatic = (PrismaticJoint)component;
                        WriteCommon(prismatic, common, report);
                        WriteBallAndSocket(prismatic, common, common.AnchorA);
                        prismatic.AxisLocal = axis;
                        prismatic.AxisInConnectedEntity = math.rotate(common.BFromA.rot, axis);
                        prismatic.PerpendicularAxisLocal = perpendicular;
                        prismatic.PerpendicularAxisInConnectedEntity = math.rotate(common.BFromA.rot, perpendicular);
                        prismatic.MinDistanceOnAxis = range.Min;
                        prismatic.MaxDistanceOnAxis = range.Max;
                    },
                };
            }

            report?.Warning(joint, "Converted to a Limit DOF Joint. Only locked axes are kept: limited axes are " +
                "now free, and the axes follow the body instead of the joint frame.");
            var lockLinear = new bool3(linear[0] == ConfigurableJointMotion.Locked,
                linear[1] == ConfigurableJointMotion.Locked,
                linear[2] == ConfigurableJointMotion.Locked);
            var lockAngular = new bool3(angular[0] == ConfigurableJointMotion.Locked,
                angular[1] == ConfigurableJointMotion.Locked, angular[2] == ConfigurableJointMotion.Locked);
            return new JointData
            {
                ComponentType = typeof(LimitDOFJoint),
                Write = component =>
                {
                    var limitDof = (LimitDOFJoint)component;
                    WriteCommon(limitDof, common, report);
                    limitDof.LockLinearAxes = lockLinear;
                    limitDof.LockAngularAxes = lockAngular;
                },
            };
        }

        /// <summary> Sibling indices from the scene root, so the path is stable while components change. </summary>
        internal static string GetObjectPath(Transform transform)
        {
            var indices = new System.Collections.Generic.List<int>();
            for (; transform != null; transform = transform.parent)
                indices.Add(transform.GetSiblingIndex());

            indices.Reverse();
            return string.Join("/", indices);
        }

        internal static GameObject FindByPath(System.Collections.Generic.IReadOnlyList<GameObject> roots, string path)
        {
            var indices = Array.ConvertAll(path.Split('/'), int.Parse);
            Transform current = null;
            foreach (var root in roots)
            {
                if (root.transform.GetSiblingIndex() == indices[0])
                {
                    current = root.transform;
                    break;
                }
            }

            for (var i = 1; current != null && i < indices.Length; ++i)
                current = indices[i] < current.childCount ? current.GetChild(indices[i]) : null;

            return current != null ? current.gameObject : null;
        }

        static int Count(ConfigurableJointMotion[] motions, ConfigurableJointMotion motion)
        {
            var count = 0;
            foreach (var value in motions)
            {
                if (value == motion)
                    count++;
            }

            return count;
        }

        static bool HasActiveDrive(JointDrive drive) => drive.positionSpring > 0f || drive.positionDamper > 0f;

        static void WarnLimitSprings(ConfigurableJoint joint, ConversionReport report)
        {
            if (joint.angularXLimitSpring.spring > 0f || joint.angularYZLimitSpring.spring > 0f ||
                joint.linearLimitSpring.spring > 0f)
                report?.Warning(joint, "Limit springs were dropped, because custom joint limits are stiff.");
        }

        /// <summary> Copy of <c>BaseJointBaker.GetScaledLocalAnchorPosition</c>. </summary>
        static float3 GetScaledLocalAnchorPosition(Transform bodyTransform, float3 anchorPosition)
        {
            const float SCALE_EPSILON = 0.0001f;
            if (math.lengthsq((float3)bodyTransform.lossyScale - new float3(1f)) > SCALE_EPSILON)
            {
                var localToWorld = (float4x4)bodyTransform.localToWorldMatrix;
                var rigidBodyTransform = Math.DecomposeRigidBodyTransform(localToWorld);
                var skewMatrix = math.mul(math.inverse(new float4x4(rigidBodyTransform)), localToWorld);
                return math.mul(skewMatrix, new float4(anchorPosition, 1)).xyz;
            }

            return anchorPosition;
        }

        /// <summary> Copy of <c>BaseJointBaker.GetJointFrameOrientation</c>. </summary>
        static quaternion GetJointFrameOrientation(Vector3 axis, Vector3 secondaryAxis)
        {
            Vector3.OrthoNormalize(ref axis, ref secondaryAxis);
            return new BodyFrame { Axis = axis, PerpendicularAxis = secondaryAxis }.AsRigidTransform().rot;
        }

        /// <summary> Copy of <c>BaseJointBaker.ConvertSpringDamperSettings</c>. </summary>
        static void ConvertSpringDamperSettings(float springConstant, float dampingCoefficient, float constrainedMass,
            out float springFrequency, out float dampingRatio)
        {
            if (springConstant <= SPRING_THRESHOLD)
            {
                springFrequency = Constraint.DefaultSpringFrequency;
                dampingRatio = dampingCoefficient <= SPRING_THRESHOLD
                    ? Constraint.DefaultDampingRatio
                    : dampingCoefficient;
                return;
            }

            // Copies of the internal JacobianUtilities spring conversions.
            springFrequency = math.sqrt(springConstant / constrainedMass) * Math.Constants.OneOverTau;
            if (dampingCoefficient <= SPRING_THRESHOLD)
            {
                dampingRatio = 0f;
                return;
            }

            var criticalProduct = springConstant * constrainedMass;
            dampingRatio = criticalProduct < Math.Constants.Eps
                ? dampingCoefficient
                : dampingCoefficient / (2 * math.sqrt(criticalProduct));
        }

        /// <summary> Copy of <c>BaseJointBaker.GetConstrainedBodyInertia</c>. </summary>
        static float GetConstrainedBodyInertia(UnityEngine.Joint joint)
        {
            var rigidbody = joint.GetComponent<Rigidbody>();
            var connectedBody = joint.connectedBody;
            var inertia = 0f;

            if (rigidbody != null && !rigidbody.isKinematic)
                inertia += math.csum((float3)rigidbody.inertiaTensor) / 3f;

            if (connectedBody != null && !connectedBody.isKinematic)
                inertia += math.csum((float3)connectedBody.inertiaTensor) / 3f;

            return inertia > 0 ? inertia : 1.0f;
        }
    }
}
