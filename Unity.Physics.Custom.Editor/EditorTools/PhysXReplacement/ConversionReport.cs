using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Unity.Physics.Editor
{
    /// <summary> Severity of a <see cref="ConversionReport"/> entry. </summary>
    enum ConversionSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary> Collects the results and lossy mappings of a PhysX to ECS authoring replacement. </summary>
    sealed class ConversionReport
    {
        internal readonly struct Entry
        {
            public readonly ConversionSeverity Severity;
            public readonly string Location;
            public readonly string Message;

            public Entry(ConversionSeverity severity, string location, string message)
            {
                Severity = severity;
                Location = location;
                Message = message;
            }

            public override string ToString() => $"[{Severity}] {Location}: {Message}";
        }

        readonly List<Entry> m_Entries = new List<Entry>();

        internal IReadOnlyList<Entry> Entries => m_Entries;

        /// <summary> Asset path used as the location prefix of new entries. </summary>
        internal string CurrentAssetPath { get; set; }

        internal int ConvertedBodies { get; set; }

        internal int ConvertedShapes { get; set; }

        internal int ConvertedJoints { get; set; }

        internal int ReappliedOverrides { get; set; }

        internal int CheckedPrefabs { get; set; }

        internal int ModifiedPrefabs { get; set; }

        internal int CheckedScenes { get; set; }

        internal int ModifiedScenes { get; set; }

        internal int WarningCount { get; private set; }

        internal int ErrorCount { get; private set; }

        internal void Add(ConversionSeverity severity, Object context, string message)
        {
            var location = CurrentAssetPath;
            if (context is Component component)
                location = $"{CurrentAssetPath} > {GetHierarchyPath(component.transform)} ({component.GetType().Name})";
            else if (context is GameObject gameObject)
                location = $"{CurrentAssetPath} > {GetHierarchyPath(gameObject.transform)}";

            m_Entries.Add(new Entry(severity, location, message));
            if (severity == ConversionSeverity.Warning)
                WarningCount++;
            else if (severity == ConversionSeverity.Error)
                ErrorCount++;
        }

        internal void Info(Object context, string message) => Add(ConversionSeverity.Info, context, message);

        internal void Warning(Object context, string message) => Add(ConversionSeverity.Warning, context, message);

        internal void Error(Object context, string message) => Add(ConversionSeverity.Error, context, message);

        internal bool HasEntry(ConversionSeverity severity, string messageFragment)
        {
            foreach (var entry in m_Entries)
            {
                if (entry.Severity == severity && entry.Message.Contains(messageFragment))
                    return true;
            }

            return false;
        }

        internal string GetSummary() =>
            $"Checked {CheckedPrefabs} prefab(s) and modified {ModifiedPrefabs}.\n" +
            $"Checked {CheckedScenes} scene(s) and modified {ModifiedScenes}.\n" +
            $"Converted {ConvertedBodies} bodies, {ConvertedShapes} shapes and {ConvertedJoints} joints.\n" +
            $"Re-applied {ReappliedOverrides} prefab instance override(s).\n" +
            $"{WarningCount} warning(s), {ErrorCount} error(s).";

        internal void Log()
        {
            Debug.Log($"PhysX to ECS authoring replacement finished.\n{GetSummary()}");

            var builder = new StringBuilder();
            foreach (var entry in m_Entries)
            {
                switch (entry.Severity)
                {
                    case ConversionSeverity.Error:
                        Debug.LogError(entry.ToString());
                        break;
                    case ConversionSeverity.Warning:
                        Debug.LogWarning(entry.ToString());
                        break;
                    default:
                        builder.AppendLine(entry.ToString());
                        break;
                }
            }

            if (builder.Length > 0)
                Debug.Log(builder.ToString());
        }

        internal static string GetHierarchyPath(Transform transform)
        {
            var builder = new StringBuilder(transform.name);
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                builder.Insert(0, '/').Insert(0, parent.name);

            return builder.ToString();
        }
    }
}
