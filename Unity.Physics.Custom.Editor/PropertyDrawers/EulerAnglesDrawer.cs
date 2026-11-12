using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;

namespace Unity.Physics.Editor
{
    [CustomPropertyDrawer(typeof(EulerAngles))]
    class EulerAnglesDrawer : BaseDrawer
    {
        protected override bool IsCompatible(SerializedProperty property) => true;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var value = property.FindPropertyRelative(nameof(EulerAngles.Value));
            return EditorGUI.GetPropertyHeight(value);
        }

        protected override void DoGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var value = property.FindPropertyRelative(nameof(EulerAngles.Value));
            IndentSafeFloat3Field.Draw(position, value, label); // FireAlt: keep X/Y/Z labels when indented
        }
    }
}
