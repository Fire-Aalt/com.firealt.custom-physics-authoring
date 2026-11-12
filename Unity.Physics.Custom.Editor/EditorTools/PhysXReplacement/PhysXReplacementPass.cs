using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.Physics.Editor
{
    /// <summary> Options for a <see cref="PhysXReplacementPass"/> run. </summary>
    sealed class PhysXReplacementOptions
    {
        /// <summary> Folders searched for prefabs and scenes. </summary>
        public string[] Folders = { "Assets" };

        /// <summary> Shows progress bars and allows cancelling. </summary>
        public bool Interactive = true;

        /// <summary> Folder for generated material templates and category names. </summary>
        public string GeneratedAssetsFolder = "Assets/Settings/Physics";
    }

    /// <summary>
    /// Replaces PhysX authoring with ECS physics authoring in all prefabs and scenes of the selected folders.
    /// Follows <c>ComponentVersionUpgrader</c>, and additionally preserves prefab instance overrides:
    /// instance values are captured before any source prefab changes, sources are converted before the assets that
    /// contain them, and the captured values are then written to the converted instance components.
    /// </summary>
    static class PhysXReplacementPass
    {
        enum Kind
        {
            None,
            Body,
            Shape,
            Joint,
        }

        /// <summary> Identifies a component by its asset, hierarchy path, kind and index among that kind. </summary>
        readonly struct ComponentKey : IEquatable<ComponentKey>
        {
            readonly string m_AssetPath;
            readonly string m_ObjectPath;
            readonly Kind m_Kind;
            readonly int m_Index;

            public ComponentKey(string assetPath, string objectPath, Kind kind, int index)
            {
                m_AssetPath = assetPath;
                m_ObjectPath = objectPath;
                m_Kind = kind;
                m_Index = index;
            }

            public bool Equals(ComponentKey other) =>
                m_AssetPath == other.m_AssetPath && m_ObjectPath == other.m_ObjectPath && m_Kind == other.m_Kind &&
                m_Index == other.m_Index;

            public override bool Equals(object obj) => obj is ComponentKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(m_AssetPath, m_ObjectPath, m_Kind, m_Index);
        }

        /// <summary> Effective values of a PhysX component in a prefab instance, captured before conversion. </summary>
        sealed class Snapshot
        {
            public string ObjectPath;
            public Kind Kind;
            public ComponentKey Original;
            public bool Removed;
            public BodyData Body;
            public ShapeData Shape;
            public JointData Joint;
        }

        internal static ConversionReport Run(PhysXReplacementOptions options)
        {
            var report = new ConversionReport();
            var templates = new PhysXMaterialTemplateCache(options.GeneratedAssetsFolder, report);
            var sceneSetup = EditorSceneManager.GetSceneManagerSetup();

            try
            {
                report.CurrentAssetPath = options.GeneratedAssetsFolder;
                PhysXMaterialTemplateCache.EnsureCategoryNames(options.GeneratedAssetsFolder, report);

                var prefabs = SortPrefabs(FindAssets("t:Prefab", ".prefab", options.Folders));
                var scenes = FindAssets("t:Scene", ".unity", options.Folders);
                var assets = prefabs.Concat(scenes).ToList();

                // Capture instance values before any source prefab changes.
                var snapshots = new Dictionary<string, List<Snapshot>>();
                for (var i = 0; i < assets.Count; ++i)
                {
                    if (Progress(options, "Capturing prefab instance values", assets[i], i, assets.Count))
                        return report;

                    var path = assets[i];
                    report.CurrentAssetPath = path;
                    WithAsset(path, report, roots =>
                    {
                        var captured = CaptureSnapshots(roots, report);
                        if (captured.Count > 0)
                            snapshots.Add(path, captured);

                        return false;
                    });
                }

                // Convert sources before the assets that contain them, then re-apply the captured values.
                var converted = new Dictionary<ComponentKey, ComponentKey>();
                for (var i = 0; i < assets.Count; ++i)
                {
                    var path = assets[i];
                    var isScene = i >= prefabs.Count;
                    var title = isScene ? "Replacing in scenes" : "Replacing in prefabs";
                    if (Progress(options, title, path, i, assets.Count))
                    {
                        report.Warning(null, "Cancelled. Some assets are already converted.");
                        return report;
                    }

                    if (isScene)
                        report.CheckedScenes++;
                    else
                        report.CheckedPrefabs++;

                    report.CurrentAssetPath = path;
                    snapshots.TryGetValue(path, out var assetSnapshots);
                    var modified = WithAsset(path, report,
                        roots => ConvertAsset(path, roots, assetSnapshots, converted, templates, report));

                    if (modified && isScene)
                        report.ModifiedScenes++;
                    else if (modified)
                        report.ModifiedPrefabs++;
                }
            }
            finally
            {
                if (options.Interactive)
                    EditorUtility.ClearProgressBar();

                // Generated assets are saved one by one. SaveAssets would also save unrelated dirty assets.
                if (sceneSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(sceneSetup);

                report.CurrentAssetPath = null;
                report.Log();
            }

            return report;
        }

        static bool Progress(PhysXReplacementOptions options, string title, string path, int index, int count) =>
            options.Interactive &&
            EditorUtility.DisplayCancelableProgressBar(title, path, (float)index / math.max(1, count));

        static List<string> FindAssets(string filter, string extension, string[] folders) =>
            AssetDatabase.FindAssets(filter, folders)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

        /// <summary> Orders prefabs so that base and nested prefabs come before the prefabs that use them. </summary>
        static List<string> SortPrefabs(List<string> prefabs)
        {
            var inScope = new HashSet<string>(prefabs);
            var visited = new HashSet<string>();
            var sorted = new List<string>(prefabs.Count);

            foreach (var prefab in prefabs)
                Visit(prefab);

            return sorted;

            void Visit(string path)
            {
                if (!visited.Add(path))
                    return;

                foreach (var dependency in AssetDatabase.GetDependencies(path, false))
                {
                    if (dependency != path && inScope.Contains(dependency))
                        Visit(dependency);
                }

                sorted.Add(path);
            }
        }

        /// <summary> Opens a prefab or scene, runs <paramref name="process"/>, and saves if it returns true. </summary>
        static bool WithAsset(string path, ConversionReport report, Func<IReadOnlyList<GameObject>, bool> process)
        {
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                Scene scene;
                try
                {
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                }
                catch (Exception exception)
                {
                    report.Error(null, $"Could not open the scene: {exception.Message}");
                    return false;
                }

                if (!process(scene.GetRootGameObjects()))
                    return false;

                EditorSceneManager.SaveScene(scene);
                return true;
            }

            GameObject root;
            try
            {
                root = PrefabUtility.LoadPrefabContents(path);
            }
            catch (Exception exception)
            {
                report.Error(null, $"Could not load the prefab: {exception.Message}");
                return false;
            }

            try
            {
                if (!process(new[] { root }))
                    return false;

                PrefabUtility.SaveAsPrefabAsset(root, path);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static bool IsOwned(Component component) =>
            !PrefabUtility.IsPartOfPrefabInstance(component) || PrefabUtility.IsAddedComponentOverride(component);

        // ---- Snapshots ------------------------------------------------------------------------------------------

        static List<Snapshot> CaptureSnapshots(IReadOnlyList<GameObject> roots, ConversionReport report)
        {
            var snapshots = new List<Snapshot>();
            foreach (var root in roots)
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    var kind = GetPhysXKind(component);
                    if (kind == Kind.None || IsOwned(component))
                        continue;

                    var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(component);
                    if (original == null)
                        continue;

                    // Values are only read here. Reading uses the instance hierarchy, so values that depend on the
                    // instance, such as an overridden layer or scale, are captured too.
                    var snapshot = new Snapshot
                    {
                        ObjectPath = GetObjectPath(component.transform),
                        Kind = kind,
                        Original = GetAssetKey(original, GetPhysXIndex(original, kind), kind),
                    };

                    switch (component)
                    {
                        case Rigidbody rigidbody:
                            snapshot.Body = PhysXAuthoringConverter.ReadBody(rigidbody, null);
                            break;
                        case UnityEngine.Collider collider:
                            snapshot.Shape = PhysXAuthoringConverter.ReadShape(collider, null);
                            break;
                        case UnityEngine.Joint joint:
                            snapshot.Joint = PhysXAuthoringConverter.ReadJoint(joint, null);
                            if (snapshot.Joint == null)
                                continue;

                            break;
                    }

                    snapshots.Add(snapshot);
                }

                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    var gameObject = transform.gameObject;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
                        continue;

                    foreach (var removed in PrefabUtility.GetRemovedComponents(gameObject))
                    {
                        var kind = GetPhysXKind(removed.assetComponent);
                        if (kind == Kind.None)
                            continue;

                        var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(removed.assetComponent);
                        if (original == null)
                            original = removed.assetComponent;

                        snapshots.Add(new Snapshot
                        {
                            ObjectPath = GetObjectPath(removed.containingInstanceGameObject.transform),
                            Kind = kind,
                            Original = GetAssetKey(original, GetPhysXIndex(original, kind), kind),
                            Removed = true,
                        });
                    }
                }
            }

            return snapshots;
        }

        // ---- Conversion -----------------------------------------------------------------------------------------

        static bool ConvertAsset(
            string path, IReadOnlyList<GameObject> roots, List<Snapshot> snapshots,
            Dictionary<ComponentKey, ComponentKey> converted, IPhysicsMaterialTemplateProvider templates,
            ConversionReport report)
        {
            var context = new ConversionContext(templates, report)
            {
                IsOwned = IsOwned,
                Converted = (source, replacement) =>
                {
                    var kind = GetPhysXKind(source);
                    var objectPath = GetObjectPath(source.transform);
                    converted[new ComponentKey(path, objectPath, kind, GetPhysXIndex(source, kind))] =
                        new ComponentKey(path, objectPath, kind, GetEcsIndex(replacement, kind));
                },
            };

            var modified = false;
            foreach (var root in roots)
                modified |= PhysXAuthoringConverter.Convert(root, context);

            if (snapshots == null)
                return modified;

            // Bodies first, because joints connect to them.
            foreach (var snapshot in snapshots.OrderBy(snapshot => snapshot.Kind))
                modified |= Reapply(snapshot, roots, converted, templates, report);

            return modified;
        }

        static bool Reapply(
            Snapshot snapshot, IReadOnlyList<GameObject> roots, Dictionary<ComponentKey, ComponentKey> converted,
            IPhysicsMaterialTemplateProvider templates, ConversionReport report)
        {
            var gameObject = FindByPath(roots, snapshot.ObjectPath);
            if (gameObject == null)
                return false;

            if (!converted.TryGetValue(snapshot.Original, out var replacementKey))
            {
                report.Warning(gameObject, "Kept PhysX components of a prefab instance, because its source prefab " +
                    "is outside the converted folders.");
                return false;
            }

            Component replacement = null;
            foreach (var component in gameObject.GetComponents(GetEcsType(snapshot.Kind)))
            {
                var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(component);
                if (original != null &&
                    GetAssetKey(original, GetEcsIndex(original, snapshot.Kind), snapshot.Kind).Equals(replacementKey))
                {
                    replacement = component;
                    break;
                }
            }

            if (replacement == null)
            {
                report.Warning(gameObject, "Could not find the converted component of a prefab instance override.");
                return false;
            }

            if (snapshot.Removed)
            {
                UnityEngine.Object.DestroyImmediate(replacement, true);
                report.ReappliedOverrides++;
                return true;
            }

            var before = EditorJsonUtility.ToJson(replacement);
            switch (replacement)
            {
                case PhysicsBodyAuthoring body:
                    PhysXAuthoringConverter.WriteBody(snapshot.Body, body);
                    break;
                case PhysicsShapeAuthoring shape:
                    PhysXAuthoringConverter.WriteShape(snapshot.Shape, shape, new ConversionContext(templates, report));
                    break;
                case BaseJoint joint:
                    snapshot.Joint.Write(joint);
                    break;
            }

            if (EditorJsonUtility.ToJson(replacement) == before)
                return false;

            PrefabUtility.RecordPrefabInstancePropertyModifications(replacement);
            report.ReappliedOverrides++;
            return true;
        }

        // ---- Keys and paths -------------------------------------------------------------------------------------

        static Kind GetPhysXKind(Component component) => component switch
        {
            Rigidbody _ => Kind.Body,
            UnityEngine.Collider collider when PhysXAuthoringConverter.IsSupportedCollider(collider) => Kind.Shape,
            UnityEngine.Joint _ => Kind.Joint,
            _ => Kind.None,
        };

        static Type GetPhysXType(Kind kind) => kind switch
        {
            Kind.Body => typeof(Rigidbody),
            Kind.Shape => typeof(UnityEngine.Collider),
            _ => typeof(UnityEngine.Joint),
        };

        static Type GetEcsType(Kind kind) => kind switch
        {
            Kind.Body => typeof(PhysicsBodyAuthoring),
            Kind.Shape => typeof(PhysicsShapeAuthoring),
            _ => typeof(BaseJoint),
        };

        static int GetPhysXIndex(Component component, Kind kind)
        {
            var index = 0;
            foreach (var other in component.GetComponents(GetPhysXType(kind)))
            {
                if (other == component)
                    return index;

                if (GetPhysXKind(other) == kind)
                    index++;
            }

            return -1;
        }

        static int GetEcsIndex(Component component, Kind kind) =>
            Array.IndexOf(component.GetComponents(GetEcsType(kind)), component);

        static ComponentKey GetAssetKey(Component assetComponent, int index, Kind kind) =>
            new ComponentKey(
                AssetDatabase.GetAssetPath(assetComponent), GetObjectPath(assetComponent.transform), kind, index);

        static string GetObjectPath(Transform transform) => PhysXAuthoringConverter.GetObjectPath(transform);

        static GameObject FindByPath(IReadOnlyList<GameObject> roots, string path) =>
            PhysXAuthoringConverter.FindByPath(roots, path);
    }
}
