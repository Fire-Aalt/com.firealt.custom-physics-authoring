using System;
using System.Reflection;
using Unity.Entities;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Unity.Physics.Editor.Tests
{
    /// <summary>
    /// Bakes GameObjects into a new world through the internal <c>BakingUtility.BakeGameObjects</c>, which the
    /// Unity Physics authoring tests use too. Reflection is needed, because Entities only exposes it to its own
    /// test assemblies.
    /// </summary>
    [NoAutoStaticsCleanup] // The only static state is immutable reflection data.
    static class BakingTestUtility
    {
        const string HYBRID_ASSEMBLY = "Unity.Entities.Hybrid";

        static readonly Type s_BakingUtility = Type.GetType($"Unity.Entities.BakingUtility, {HYBRID_ASSEMBLY}", true);
        static readonly Type s_BakingSettings = Type.GetType($"Unity.Entities.BakingSettings, {HYBRID_ASSEMBLY}", true);
        static readonly Type s_BakingFlags =
            s_BakingUtility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic);

        static readonly MethodInfo s_BakeGameObjects = s_BakingUtility.GetMethod(
            "BakeGameObjects", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(World), typeof(GameObject[]), s_BakingSettings }, null);

        /// <summary> Bakes <paramref name="root"/> into a new world, which the caller disposes. </summary>
        internal static World Bake(GameObject root, BlobAssetStore blobAssetStore)
        {
            var world = new World($"Baking {root.name}");
            var flags = Enum.Parse(s_BakingFlags, "AddEntityGUID");
            var settings = Activator.CreateInstance(s_BakingSettings, flags, blobAssetStore);
            s_BakeGameObjects.Invoke(null, new[] { world, new[] { root }, settings });
            return world;
        }
    }
}
