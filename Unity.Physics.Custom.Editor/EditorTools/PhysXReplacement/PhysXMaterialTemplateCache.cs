using System.Collections.Generic;
using System.IO;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEngine;
using UnityPhysicsMaterial = UnityEngine.PhysicsMaterial;

namespace Unity.Physics.Editor
{
    /// <summary>
    /// Creates one <see cref="PhysicsMaterialTemplate"/> asset per PhysX material, so shapes keep sharing materials.
    /// </summary>
    sealed class PhysXMaterialTemplateCache : IPhysicsMaterialTemplateProvider
    {
        const string DEFAULT_TEMPLATE_NAME = "Default PhysX Material Template";

        readonly string m_GeneratedFolder;
        readonly ConversionReport m_Report;
        readonly Dictionary<UnityPhysicsMaterial, PhysicsMaterialTemplate> m_Templates =
            new Dictionary<UnityPhysicsMaterial, PhysicsMaterialTemplate>();

        PhysicsMaterialTemplate m_DefaultTemplate;

        /// <param name="generatedFolder"> Folder for templates of default and read-only materials. </param>
        public PhysXMaterialTemplateCache(string generatedFolder, ConversionReport report)
        {
            m_GeneratedFolder = generatedFolder;
            m_Report = report;
        }

        public PhysicsMaterialTemplate GetTemplate(UnityPhysicsMaterial material)
        {
            if (material == null)
            {
                if (m_DefaultTemplate == null)
                    m_DefaultTemplate = LoadOrCreate($"{m_GeneratedFolder}/{DEFAULT_TEMPLATE_NAME}.asset", null);

                return m_DefaultTemplate;
            }

            if (m_Templates.TryGetValue(material, out var template))
                return template;

            template = LoadOrCreate(GetTemplatePath(material), material);
            m_Templates.Add(material, template);
            return template;
        }

        string GetTemplatePath(UnityPhysicsMaterial material)
        {
            var materialPath = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(materialPath) && materialPath.StartsWith("Assets/") &&
                AssetDatabase.LoadMainAssetAtPath(materialPath) == material)
            {
                var folder = Path.GetDirectoryName(materialPath)?.Replace('\\', '/');
                return $"{folder}/{material.name} Template.asset";
            }

            // Materials in read-only packages or embedded in other assets get their template in the generated folder.
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out var guid, out long localId);
            var suffix = string.IsNullOrEmpty(guid)
                ? material.GetEntityId().ToString()
                : $"{guid.Substring(0, 8)}{localId}";
            return $"{m_GeneratedFolder}/{material.name} Template {suffix}.asset";
        }

        PhysicsMaterialTemplate LoadOrCreate(string path, UnityPhysicsMaterial material)
        {
            var template = AssetDatabase.LoadAssetAtPath<PhysicsMaterialTemplate>(path);
            if (template == null)
            {
                EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                template = ScriptableObject.CreateInstance<PhysicsMaterialTemplate>();
                PhysXAuthoringConverter.WriteTemplate(material, template);
                AssetDatabase.CreateAsset(template, path);
                m_Report?.Info(template, $"Created material template '{path}'.");
            }
            else
            {
                PhysXAuthoringConverter.WriteTemplate(material, template);
                EditorUtility.SetDirty(template);
                AssetDatabase.SaveAssetIfDirty(template);
            }

            return template;
        }

        /// <summary>
        /// Creates a <see cref="PhysicsCategoryNames"/> asset that mirrors the layer names, unless one already exists.
        /// Shapes map layer i to category i.
        /// </summary>
        internal static void EnsureCategoryNames(string folder, ConversionReport report)
        {
            var guids = AssetDatabase.FindAssets($"t:{nameof(PhysicsCategoryNames)}");
            if (guids.Length > 0)
            {
                var existingPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                var existing = AssetDatabase.LoadAssetAtPath<PhysicsCategoryNames>(existingPath);
                if (existing != null)
                {
                    var mismatches = new List<string>();
                    for (var i = 0; i < 32 && i < existing.CategoryNames.Count; ++i)
                    {
                        var layerName = LayerMask.LayerToName(i);
                        if (!string.IsNullOrEmpty(layerName) && existing.CategoryNames[i] != layerName)
                            mismatches.Add($"layer {i} '{layerName}' is category '{existing.CategoryNames[i]}'");
                    }

                    if (mismatches.Count > 0)
                    {
                        report?.Warning(existing, "Shapes use layer i as category i, like the PhysX bakers, but the " +
                            $"names differ: {string.Join(", ", mismatches)}.");
                    }
                }

                return;
            }

            EnsureFolder(folder);
            var path = $"{folder}/PhysicsCategoryNames.asset";
            var names = ScriptableObject.CreateInstance<PhysicsCategoryNames>();
            AssetDatabase.CreateAsset(names, path);

            var serialized = new SerializedObject(names);
            var array = serialized.FindProperty("m_CategoryNames");
            array.arraySize = 32;
            for (var i = 0; i < 32; ++i)
                array.GetArrayElementAtIndex(i).stringValue = LayerMask.LayerToName(i);

            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(names);
            report?.Info(names, $"Created '{path}' with the layer names.");
        }

        internal static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
                return;

            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
