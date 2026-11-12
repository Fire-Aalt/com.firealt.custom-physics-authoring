using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Unity.Physics.Editor
{
    /// <summary> Component context menu entries that convert a single PhysX component in place. </summary>
    static class PhysXComponentConversionMenu
    {
        const string COLLIDER_MENU = "CONTEXT/Collider/Convert to Physics Shape";
        const string RIGIDBODY_MENU = "CONTEXT/Rigidbody/Convert to Physics Body";
        const string JOINT_MENU = "CONTEXT/Joint/Convert to ECS Joint";

        [MenuItem(COLLIDER_MENU, true)]
        [MenuItem(RIGIDBODY_MENU, true)]
        [MenuItem(JOINT_MENU, true)]
        static bool CanConvert(MenuCommand command) =>
            PhysXAuthoringConverter.CanConvertInPlace(command.context as Component);

        [MenuItem(COLLIDER_MENU)]
        static void ConvertCollider(MenuCommand command)
        {
            if (command.context is UnityEngine.Collider collider && collider != null)
            {
                Run(collider, "Convert to Physics Shape", context =>
                {
                    PhysXMaterialTemplateCache.EnsureCategoryNames(GetGeneratedAssetsFolder(), context.Report);
                    return PhysXAuthoringConverter.ConvertColliderInPlace(collider, context) != null;
                });
            }
        }

        [MenuItem(RIGIDBODY_MENU)]
        static void ConvertRigidbody(MenuCommand command)
        {
            // With several objects selected, an earlier call may already have converted this one with its joints.
            if (command.context is Rigidbody rigidbody && rigidbody != null)
                Run(rigidbody, "Convert to Physics Body", context => PhysXAuthoringConverter.ConvertBodyInPlace(rigidbody, context));
        }

        [MenuItem(JOINT_MENU)]
        static void ConvertJoint(MenuCommand command)
        {
            if (!(command.context is UnityEngine.Joint joint) || joint == null)
                return;

            var rigidbody = joint.GetComponent<Rigidbody>();
            Run(joint, "Convert to ECS Joint", context => PhysXAuthoringConverter.ConvertBodyInPlace(rigidbody, context));
        }

        static void Run(Component component, string title, Func<ConversionContext, bool> convert)
        {
            var report = new ConversionReport { CurrentAssetPath = component.gameObject.scene.path };
            var context = new ConversionContext(new PhysXMaterialTemplateCache(GetGeneratedAssetsFolder(), report), report);
            var converted = convert(context);

            var errors = new StringBuilder();
            foreach (var entry in report.Entries)
            {
                switch (entry.Severity)
                {
                    case ConversionSeverity.Error:
                        Debug.LogError(entry.ToString(), component);
                        errors.AppendLine(entry.Message);
                        break;
                    case ConversionSeverity.Warning:
                        Debug.LogWarning(entry.ToString(), component);
                        break;
                    default:
                        Debug.Log(entry.ToString(), component);
                        break;
                }
            }

            if (!converted)
                EditorUtility.DisplayDialog(title, $"Nothing was converted.\n\n{errors}", "OK");
        }

        static string GetGeneratedAssetsFolder() => new PhysXReplacementOptions().GeneratedAssetsFolder;
    }
}
