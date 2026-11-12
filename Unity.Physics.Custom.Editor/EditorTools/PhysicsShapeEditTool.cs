using Unity.Physics.Authoring;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Unity.Physics.Editor
{
    // FireAlt: PhysX-style "Edit Collider" tool. While it is active, the transform handles are hidden and
    // PhysicsShapeAuthoringEditor.OnSceneGUI lets the primitive shape handles be dragged.
    [EditorTool("Edit Physics Shape", typeof(PhysicsShapeAuthoring))]
    class PhysicsShapeEditTool : EditorTool
    {
        GUIContent m_Icon;

        internal static bool IsActive => ToolManager.activeToolType == typeof(PhysicsShapeEditTool);

        public override GUIContent toolbarIcon =>
            m_Icon ??= new GUIContent(EditorGUIUtility.IconContent("EditCollider").image, L10n.Tr("Edit Collider"));

        public override bool IsAvailable()
        {
            foreach (var t in targets)
            {
                if (t is PhysicsShapeAuthoring shape && IsPrimitive(shape.ShapeType))
                    return true;
            }

            return false;
        }

        internal static bool IsPrimitive(ShapeType shapeType) => shapeType != ShapeType.ConvexHull && shapeType != ShapeType.Mesh;
    }
}
