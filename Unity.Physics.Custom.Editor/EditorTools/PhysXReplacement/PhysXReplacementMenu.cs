using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Unity.Physics.Editor
{
    /// <summary> Menu entry for <see cref="PhysXReplacementPass"/>, next to the Unity Physics upgrade tools. </summary>
    static class PhysXReplacementMenu
    {
        const string MENU_PATH = "Tools/Unity Physics/Replace PhysX Authoring with ECS Physics Authoring";

        [MenuItem(MENU_PATH)]
        static void ReplacePhysXAuthoring()
        {
            var folders = Selection.GetFiltered<DefaultAsset>(SelectionMode.Assets)
                .Select(AssetDatabase.GetAssetPath)
                .Where(AssetDatabase.IsValidFolder)
                .ToArray();
            if (folders.Length == 0)
                folders = new[] { "Assets" };

            var message =
                "Replaces Rigidbodies, colliders and joints with Physics Body, Physics Shape and custom joint " +
                "components in all prefabs and scenes of:\n\n" + string.Join("\n", folders) + "\n\n" +
                "Settings are matched to the PhysX bakers. Lossy mappings are listed in the console.\n\n" +
                "This cannot be undone. Commit your project to version control first.";
            if (!EditorUtility.DisplayDialog("Replace PhysX Authoring", message, "Replace", "Cancel"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var report = PhysXReplacementPass.Run(new PhysXReplacementOptions { Folders = folders });

            EditorUtility.FocusProjectWindow();
            EditorUtility.DisplayDialog("PhysX Authoring Replaced", report.GetSummary(), "OK");
        }
    }
}
