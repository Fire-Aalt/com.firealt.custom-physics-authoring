using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;

namespace Unity.Physics.Editor
{
    [CustomEditor(typeof(PhysicsBodyAuthoring))]
    [CanEditMultipleObjects]
    class PhysicsBodyAuthoringEditor : BaseEditor
    {
        static class Content
        {
            public static readonly GUIContent MassLabel = L10n.TextContent("Mass", null, null, null);
            public static readonly GUIContent CenterOfMassLabel = L10n.TextContent(
                "Center of Mass", "Center of mass in the space of this body's transform."
, null, null            );
            public static readonly GUIContent InertiaTensorLabel = L10n.TextContent(
                "Inertia Tensor", "Resistance to angular motion about each axis of rotation."
, null, null            );
            public static readonly GUIContent OrientationLabel = L10n.TextContent(
                "Orientation", "Orientation of the body's inertia tensor in the space of its transform."
, null, null            );
            public static readonly GUIContent AdvancedLabel = L10n.TextContent(
                "Advanced", "Advanced options"
, null, null            );
            public static readonly GUIContent ConstraintsLabel = L10n.TextContent(
                "Constraints", "Axes the body cannot move along or rotate about", null, null);
            public static readonly GUIContent FreezeRotationLabel = L10n.TextContent("Freeze Rotation",
                "Stops rotation about the selected world axes, like the Rigidbody Freeze Rotation constraints. " +
                "It is baked into the mass properties from the body's rotation, so no joint is created.", null, null);
            public static readonly GUIContent[] AxisLabels =
            {
                L10n.TextContent("X", null, null, null), L10n.TextContent("Y", null, null, null),
                L10n.TextContent("Z", null, null, null),
            };
        }

        const float AXIS_TOGGLE_WIDTH = 28f;

        const string SINGLE_FROZEN_AXIS_WARNING = "A single frozen axis is locked relative to the body, not the " +
            "world as in PhysX. Once the body rotates about a free axis, the frozen axis tilts with it. " +
            "Spawning the body with a different rotation moves the frozen axis too.";

        const string TWO_FROZEN_AXES_WARNING = "The free axis is baked from this body's rotation. Spawning the " +
            "body with a different rotation, other than about the free axis, breaks the freeze.";

        /// <summary> Returns the warning for <paramref name="frozen"/>, or null if the freeze matches PhysX. </summary>
        internal static string GetFreezeRotationWarning(RotationAxes frozen) =>
            math.countbits((int)(frozen & RotationAxes.All)) switch
            {
                1 => SINGLE_FROZEN_AXIS_WARNING,
                2 => TWO_FROZEN_AXES_WARNING,
                _ => null,
            };

        #pragma warning disable 649
        [AutoPopulate] SerializedProperty m_MotionType;
        [AutoPopulate] SerializedProperty m_Smoothing;
        [AutoPopulate] SerializedProperty m_Mass;
        [AutoPopulate] SerializedProperty m_GravityFactor;
        [AutoPopulate] SerializedProperty m_FreezeRotation;
        [AutoPopulate] SerializedProperty m_LinearDamping;
        [AutoPopulate] SerializedProperty m_AngularDamping;
        [AutoPopulate] SerializedProperty m_InitialLinearVelocity;
        [AutoPopulate] SerializedProperty m_InitialAngularVelocity;
        [AutoPopulate] SerializedProperty m_OverrideDefaultMassDistribution;
        [AutoPopulate] SerializedProperty m_CenterOfMass;
        [AutoPopulate] SerializedProperty m_Orientation;
        [AutoPopulate] SerializedProperty m_InertiaTensor;
        [AutoPopulate] SerializedProperty m_SolverType;
        [AutoPopulate] SerializedProperty m_WorldIndex;
        [AutoPopulate] SerializedProperty m_CustomTags;
        #pragma warning restore 649

        bool showAdvanced;
        bool showConstraints;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(m_MotionType);

            if (m_MotionType.intValue != (int)BodyMotionType.Static)
                EditorGUILayout.PropertyField(m_Smoothing);

            var dynamic = m_MotionType.intValue == (int)BodyMotionType.Dynamic;

            if (dynamic)
                EditorGUILayout.PropertyField(m_Mass, Content.MassLabel);
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                var position = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
                EditorGUI.BeginProperty(position, Content.MassLabel, m_Mass);
                EditorGUI.FloatField(position, Content.MassLabel, float.PositiveInfinity);
                EditorGUI.EndProperty();
                EditorGUI.EndDisabledGroup();
            }

            if (m_MotionType.intValue == (int)BodyMotionType.Dynamic)
            {
                EditorGUILayout.PropertyField(m_LinearDamping, true);
                EditorGUILayout.PropertyField(m_AngularDamping, true);
            }

            if (m_MotionType.intValue != (int)BodyMotionType.Static)
            {
                EditorGUILayout.PropertyField(m_InitialLinearVelocity, true);
                EditorGUILayout.PropertyField(m_InitialAngularVelocity, true);
            }

            if (m_MotionType.intValue == (int)BodyMotionType.Dynamic)
            {
                EditorGUILayout.PropertyField(m_GravityFactor, true);

                showConstraints = EditorGUILayout.Foldout(showConstraints, Content.ConstraintsLabel, true);
                if (showConstraints)
                {
                    ++EditorGUI.indentLevel;
                    FreezeRotationField();
                    --EditorGUI.indentLevel;
                }

                // Shown while collapsed too, so a freeze that differs from PhysX is not hidden.
                FreezeRotationWarnings();
            }

            showAdvanced = EditorGUILayout.Foldout(showAdvanced, Content.AdvancedLabel);
            if (showAdvanced)
            {
                ++EditorGUI.indentLevel;
                EditorGUILayout.PropertyField(m_SolverType);
                EditorGUILayout.PropertyField(m_WorldIndex);
                if (m_MotionType.intValue != (int)BodyMotionType.Static)
                {
                    EditorGUILayout.PropertyField(m_OverrideDefaultMassDistribution);
                    if (m_OverrideDefaultMassDistribution.boolValue)
                    {
                        ++EditorGUI.indentLevel;
                        EditorGUILayout.PropertyField(m_CenterOfMass, Content.CenterOfMassLabel);

                        EditorGUI.BeginDisabledGroup(!dynamic);
                        if (dynamic)
                        {
                            EditorGUILayout.PropertyField(m_Orientation, Content.OrientationLabel);
                            EditorGUILayout.PropertyField(m_InertiaTensor, Content.InertiaTensorLabel);
                        }
                        else
                        {
                            EditorGUI.BeginDisabledGroup(true);
                            var position =
                                EditorGUILayout.GetControlRect(true, EditorGUI.GetPropertyHeight(m_InertiaTensor));
                            EditorGUI.BeginProperty(position, Content.InertiaTensorLabel, m_InertiaTensor);
                            EditorGUI.Vector3Field(position, Content.InertiaTensorLabel,
                                Vector3.one * float.PositiveInfinity);
                            EditorGUI.EndProperty();
                            EditorGUI.EndDisabledGroup();
                        }

                        EditorGUI.EndDisabledGroup();

                        --EditorGUI.indentLevel;
                    }
                }
                EditorGUILayout.PropertyField(m_CustomTags);
                --EditorGUI.indentLevel;
            }

            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();

            DisplayStatusMessages();
        }

        /// <summary> Draws one toggle per axis, like the Rigidbody constraints, with mixed values per axis. </summary>
        void FreezeRotationField()
        {
            var position = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var label = EditorGUI.BeginProperty(position, Content.FreezeRotationLabel, m_FreezeRotation);
            position = EditorGUI.PrefixLabel(position, label);

            var indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            for (var i = 0; i < 3; ++i)
            {
                var axis = (RotationAxes)(1 << i);
                var frozenCount = 0;
                foreach (var target in targets)
                    frozenCount += (((PhysicsBodyAuthoring)target).FreezeRotation & axis) != 0 ? 1 : 0;

                EditorGUI.showMixedValue = frozenCount > 0 && frozenCount < targets.Length;
                var toggle = new Rect(position.x + i * AXIS_TOGGLE_WIDTH, position.y, AXIS_TOGGLE_WIDTH, position.height);
                EditorGUI.BeginChangeCheck();
                var value = EditorGUI.ToggleLeft(toggle, Content.AxisLabels[i], frozenCount == targets.Length);
                if (EditorGUI.EndChangeCheck())
                    SetFrozenAxis(axis, value);
            }

            EditorGUI.showMixedValue = false;
            EditorGUI.indentLevel = indent;
            EditorGUI.EndProperty();
        }

        /// <summary> Shows each distinct freeze warning among the selected bodies once. </summary>
        void FreezeRotationWarnings()
        {
            var hasSingle = false;
            var hasTwo = false;
            foreach (var target in targets)
            {
                var warning = GetFreezeRotationWarning(((PhysicsBodyAuthoring)target).FreezeRotation);
                hasSingle |= warning == SINGLE_FROZEN_AXIS_WARNING;
                hasTwo |= warning == TWO_FROZEN_AXES_WARNING;
            }

            if (hasSingle)
                EditorGUILayout.HelpBox(SINGLE_FROZEN_AXIS_WARNING, MessageType.Warning);
            if (hasTwo)
                EditorGUILayout.HelpBox(TWO_FROZEN_AXES_WARNING, MessageType.Warning);
        }

        /// <summary> Sets one axis on every target and keeps their other axes. </summary>
        void SetFrozenAxis(RotationAxes axis, bool frozen)
        {
            foreach (var target in targets)
            {
                using var targetObject = new SerializedObject(target);
                var property = targetObject.FindProperty(m_FreezeRotation.propertyPath);
                property.intValue = frozen ? property.intValue | (int)axis : property.intValue & ~(int)axis;
                targetObject.ApplyModifiedProperties();
            }
        }

        MessageType m_Status;
        List<string> m_StatusMessages = new List<string>(8);

        void DisplayStatusMessages()
        {
            m_Status = MessageType.None;
            m_StatusMessages.Clear();

            var hierarchyStatus = StatusMessageUtility.GetHierarchyStatusMessage(targets, out var hierarchyStatusMessage);
            if (!string.IsNullOrEmpty(hierarchyStatusMessage))
            {
                m_StatusMessages.Add(hierarchyStatusMessage);
                m_Status = (MessageType)math.max((int)m_Status, (int)hierarchyStatus);
            }

            if (m_StatusMessages.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n\n", m_StatusMessages), m_Status);
        }
    }
}
