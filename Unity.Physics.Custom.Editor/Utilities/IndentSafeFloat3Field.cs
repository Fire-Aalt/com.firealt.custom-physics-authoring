using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;

namespace Unity.Physics.Editor
{
    // FireAlt: the built-in float3 drawer clips its X/Y/Z sub-labels and does not indent its prefix label when
    // EditorGUI.indentLevel > 0. This field indents the prefix label and draws the components at indent zero.
    [NoAutoStaticsCleanup]
    static class IndentSafeFloat3Field
    {
        static readonly GUIContent[] s_SubLabels = { new GUIContent("X"), new GUIContent("Y"), new GUIContent("Z") };

        public static void Draw(SerializedProperty property, GUIContent label) =>
            Draw(EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight), property, label);

        public static void Draw(Rect position, SerializedProperty property, GUIContent label)
        {
            position.height = EditorGUIUtility.singleLineHeight;
            label = EditorGUI.BeginProperty(position, label, property);
            position = EditorGUI.PrefixLabel(position, label);

            var indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            EditorGUI.MultiPropertyField(position, s_SubLabels, property.FindPropertyRelative("x"));
            EditorGUI.indentLevel = indent;

            EditorGUI.EndProperty();
        }
    }
}
