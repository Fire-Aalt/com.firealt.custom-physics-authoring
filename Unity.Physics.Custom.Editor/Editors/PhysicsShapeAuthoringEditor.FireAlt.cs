using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using FitSources = Unity.Physics.Editor.PhysicsShapeFitUtility.FitSources;

namespace Unity.Physics.Editor
{
    // FireAlt: PhysX-style inspector additions, kept out of the upstream file to ease merging upstream changes:
    // the "Edit Collider" toolbar, "Fit to Self" / "Fit to Children" buttons and edit-tool gated shape handles.
    partial class PhysicsShapeAuthoringEditor
    {
        static class FireAltStyles
        {
            public static readonly GUIContent EditColliderLabel = L10n.TextContent("Edit Collider", null, null, null);

            public static readonly GUIContent FitToSelf = L10n.TextContent(
                "Fit to Self", "Resize the collider to match this GameObject's renderer bounds.", null, null);
            public static readonly GUIContent FitToSelfDisabled = L10n.TextContent(
                "Fit to Self", "No renderer to fit to.", null, null);
            public static readonly GUIContent FitToChildren = L10n.TextContent(
                "Fit to Children",
                "Resize the collider to match the combined renderer bounds of this GameObject and its children.", null, null);
            public static readonly GUIContent FitToChildrenDisabled = L10n.TextContent(
                "Fit to Children", "No children to fit to.", null, null);

            public static readonly string FitToSelfUndo = L10n.Tr("Fit Shape to Self", null);
            public static readonly string FitToChildrenUndo = L10n.Tr("Fit Shape to Children", null);
        }

        const float FIT_BUTTON_SPACING = 2f;
        const float EDIT_COLLIDER_SPACING = 4f;
        const string MINIMUM_SKINNED_VERTEX_WEIGHT_PROPERTY = "m_MinimumSkinnedVertexWeight";

        bool HasSinglePrimitiveShapeType =>
            !m_ShapeType.hasMultipleDifferentValues && PhysicsShapeEditTool.IsPrimitive((ShapeType)m_ShapeType.intValue);

        void DrawEditColliderToolbar()
        {
            if (HasSinglePrimitiveShapeType)
            {
                EditorGUILayout.EditorToolbarForTarget(FireAltStyles.EditColliderLabel, this);
                GUILayout.Space(EDIT_COLLIDER_SPACING);
            }
            else if (PhysicsShapeEditTool.IsActive)
                ToolManager.RestorePreviousTool();
        }

        // Outside the edit tool, handles only draw their wireframe: they are invisible and never receive input.
        static bool SkipSceneGUIEvent() => !PhysicsShapeEditTool.IsActive && Event.current.type != EventType.Repaint;

        static void SetHandlesEditable(bool editable)
        {
            var color = editable ? Color.white : Color.clear;
            s_Box.handleColor = color;
            s_Capsule.handleColor = color;
            s_Cylinder.handleColor = color;
            s_Sphere.handleColor = color;
            s_Plane.handleColor = color;
        }

        void DisplayForceUnique()
        {
            --EditorGUI.indentLevel;
            EditorGUILayout.PropertyField(m_ForceUnique, Styles.ForceUniqueLabel);
            ++EditorGUI.indentLevel;
        }

        void DisplayFitButtons()
        {
            var sources = FitSources.None;
            foreach (PhysicsShapeAuthoring shape in targets)
                sources |= PhysicsShapeFitUtility.GetSources(shape);

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, EditorStyles.miniButton);
            rect.xMin += EditorGUIUtility.labelWidth + FIT_BUTTON_SPACING;
            var left = new Rect(rect) { width = 0.5f * (rect.width - FIT_BUTTON_SPACING) };
            var right = new Rect(left) { x = left.xMax + FIT_BUTTON_SPACING };

            var persistent = EditorUtility.IsPersistent(target);
            DisplayFitButton(left, false, sources, persistent);
            DisplayFitButton(right, true, sources, persistent);
        }

        void DisplayFitButton(Rect rect, bool includeChildren, FitSources sources, bool persistent)
        {
            var modeSources = includeChildren ? FitSources.Children : FitSources.Self;
            var hasSources = (sources & modeSources) != 0;
            var label = includeChildren
                ? hasSources ? FireAltStyles.FitToChildren : FireAltStyles.FitToChildrenDisabled
                : hasSources ? FireAltStyles.FitToSelf : FireAltStyles.FitToSelfDisabled;

            // Fit to Children also includes this GameObject's own skinned vertices.
            var skinnedSources = includeChildren ? FitSources.Skinned : FitSources.SelfSkinned;

            using (new EditorGUI.DisabledScope(!hasSources || persistent))
            {
                if (!GUI.Button(rect, label, EditorStyles.miniButton))
                    return;
            }

            if ((sources & skinnedSources) != 0)
            {
                FitDropDown.Show(rect, label.text, m_MinimumSkinnedVertexWeight, includeChildren);
                return;
            }

            Undo.RecordObjects(targets, includeChildren ? FireAltStyles.FitToChildrenUndo : FireAltStyles.FitToSelfUndo);
            FitTargets(targets, includeChildren);
            serializedObject.Update();
        }

        // Each shape keeps its own minimum skinned vertex weight, as in the upstream dropdown.
        static void FitTargets(Object[] shapes, bool includeChildren)
        {
            var modeSources = includeChildren ? FitSources.Children : FitSources.Self;
            foreach (PhysicsShapeAuthoring shape in shapes)
            {
                if ((PhysicsShapeFitUtility.GetSources(shape) & modeSources) == 0)
                    continue;

                float minimumSkinnedVertexWeight;
                using (var so = new SerializedObject(shape))
                    minimumSkinnedVertexWeight = so.FindProperty(MINIMUM_SKINNED_VERTEX_WEIGHT_PROPERTY).floatValue;

                PhysicsShapeFitUtility.Fit(shape, minimumSkinnedVertexWeight, includeChildren);
                EditorUtility.SetDirty(shape);
            }
        }

        // Same behaviour as the upstream FitToRenderMeshesDropDown, for both fit modes: the shape follows the weight
        // slider live, Apply keeps the result and Cancel reverts it.
        class FitDropDown : EditorWindow
        {
            const float WINDOW_WIDTH = 400f;
            const float LABEL_WIDTH = 200f;

            static class Content
            {
                public static readonly string ApplyLabel = L10n.Tr("Apply", null);
                public static readonly string CancelLabel = L10n.Tr("Cancel", null);
            }

            bool m_ApplyChanges;
            bool m_IncludeChildren;
            int m_UndoGroup;
            SerializedProperty m_MinimumSkinnedVertexWeight;

            public static void Show(
                Rect buttonRect, string title, SerializedProperty minimumSkinnedVertexWeight, bool includeChildren)
            {
                var window = CreateInstance<FitDropDown>();
                window.titleContent = new GUIContent(title);
                window.m_UndoGroup = Undo.GetCurrentGroup();
                window.m_MinimumSkinnedVertexWeight = minimumSkinnedVertexWeight;
                window.m_IncludeChildren = includeChildren;
                var size = new Vector2(
                    math.max(buttonRect.width, WINDOW_WIDTH),
                    (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) * 3f);
                window.maxSize = window.minSize = size;
                window.ShowAsDropDown(GUIUtility.GUIToScreenRect(buttonRect), size);
            }

            void OnGUI()
            {
                if (m_MinimumSkinnedVertexWeight.serializedObject.targetObject == null)
                {
                    Close();
                    GUIUtility.ExitGUI();
                }

                var labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = LABEL_WIDTH;

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(m_MinimumSkinnedVertexWeight);
                if (EditorGUI.EndChangeCheck())
                    ApplyChanges();

                EditorGUIUtility.labelWidth = labelWidth;

                GUILayout.FlexibleSpace();

                var buttonRect = GUILayoutUtility.GetRect(0f, EditorGUIUtility.singleLineHeight);
                var buttonLeft = new Rect(buttonRect)
                {
                    width = 0.5f * (buttonRect.width - EditorGUIUtility.standardVerticalSpacing),
                };
                var buttonRight = new Rect(buttonLeft) { x = buttonLeft.xMax + EditorGUIUtility.standardVerticalSpacing };

                var close = false;
                var isMac = Application.platform == RuntimePlatform.OSXEditor;

                var cancelPressed = GUI.Button(isMac ? buttonLeft : buttonRight, Content.CancelLabel, EditorStyles.miniButton);
                if (cancelPressed || Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                    close = true;

                if (GUI.Button(isMac ? buttonRight : buttonLeft, Content.ApplyLabel, EditorStyles.miniButton))
                {
                    close = true;
                    m_ApplyChanges = true;
                }

                if (close)
                {
                    Close();
                    GUIUtility.ExitGUI();
                }
            }

            void ApplyChanges()
            {
                var serializedObject = m_MinimumSkinnedVertexWeight.serializedObject;
                serializedObject.ApplyModifiedProperties();
                Undo.RecordObjects(serializedObject.targetObjects, titleContent.text);
                FitTargets(serializedObject.targetObjects, m_IncludeChildren);
                serializedObject.Update();
            }

            void OnDestroy()
            {
                if (m_ApplyChanges)
                    ApplyChanges();
                else
                    Undo.RevertAllDownToGroup(m_UndoGroup);
            }
        }
    }
}
