using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// Per-model warning triage state (status/assignee/note), keyed by WarningKey so it
    /// survives sessions and snapshots. Stored beside the model's snapshot folders.
    /// </summary>
    public static class WarningMetadataStore
    {
        public static readonly string[] Statuses =
        {
            "Open", "Assigned", "InProgress", "Resolved", "Ignored", "AcceptedRisk"
        };

        public sealed record WarningMetadata(string Status, string? AssignedTo, string? Note);

        private static string PathFor(string modelIdentity) =>
            System.IO.Path.Combine(ExplorerPaths.WarningSnapshotsDirectory(modelIdentity), "triage.json");

        public static Dictionary<string, WarningMetadata> Load(string modelIdentity)
        {
            try
            {
                var path = PathFor(modelIdentity);
                if (!File.Exists(path))
                {
                    return new Dictionary<string, WarningMetadata>();
                }

                return JsonConvert.DeserializeObject<Dictionary<string, WarningMetadata>>(File.ReadAllText(path))
                       ?? new Dictionary<string, WarningMetadata>();
            }
            catch
            {
                return new Dictionary<string, WarningMetadata>();
            }
        }

        public static void Save(string modelIdentity, IReadOnlyDictionary<string, WarningMetadata> metadata)
        {
            File.WriteAllText(PathFor(modelIdentity), JsonConvert.SerializeObject(metadata, Formatting.Indented));
        }
    }
}
