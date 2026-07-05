using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RevitSuite.Host.Explorer
{
    /// <summary>Extracts, ranks, snapshots, and diffs Revit warnings.</summary>
    internal static class WarningService
    {
        private const int SnapshotSchemaVersion = 1;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() }
        };

        /// <summary>API context required.</summary>
        public static IReadOnlyList<WarningRecord> Extract(Document doc, IReadOnlyList<WarningRanking> rankings)
        {
            var records = new List<WarningRecord>();

            foreach (var warning in doc.GetWarnings())
            {
                var failingIds = warning.GetFailingElements().Select(id => id.Value).OrderBy(v => v).ToList();
                var additionalIds = warning.GetAdditionalElements().Select(id => id.Value).OrderBy(v => v).ToList();
                var description = warning.GetDescriptionText() ?? "(no description)";

                string failureId;
                try
                {
                    failureId = warning.GetFailureDefinitionId()?.Guid.ToString("D") ?? description;
                }
                catch
                {
                    failureId = description;
                }

                records.Add(new WarningRecord(
                    WarningKey: CreateKey(failureId, failingIds, additionalIds),
                    FailureDefinitionId: failureId,
                    Description: description,
                    Rank: ResolveRank(rankings, failureId, description),
                    FailingElementIds: failingIds,
                    AdditionalElementIds: additionalIds,
                    Categories: ResolveCategories(doc, failingIds),
                    ElementNames: ResolveNames(doc, failingIds)));
            }

            return records;
        }

        public static IReadOnlyList<WarningRanking> LoadRankings()
        {
            try
            {
                if (!File.Exists(ExplorerPaths.WarningRankingsFile))
                {
                    return DefaultRankings;
                }

                var loaded = JsonConvert.DeserializeObject<List<WarningRanking>>(
                    File.ReadAllText(ExplorerPaths.WarningRankingsFile), Settings);
                return loaded is { Count: > 0 } ? loaded : DefaultRankings;
            }
            catch
            {
                return DefaultRankings;
            }
        }

        public static void SaveRankings(IReadOnlyList<WarningRanking> rankings)
        {
            File.WriteAllText(ExplorerPaths.WarningRankingsFile, JsonConvert.SerializeObject(rankings, Settings));
        }

        public static string SaveSnapshot(Document doc, IReadOnlyList<WarningRecord> warnings)
        {
            var identity = ExplorerPaths.GetModelIdentity(doc);
            var snapshot = new WarningSnapshot(
                SnapshotSchemaVersion, identity, doc.Title, DateTimeOffset.UtcNow, warnings);

            var path = Path.Combine(
                ExplorerPaths.WarningSnapshotsDirectory(identity),
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, Settings));
            return path;
        }

        public static IReadOnlyList<(string Path, DateTimeOffset CreatedUtc)> ListSnapshots(Document doc)
        {
            var directory = ExplorerPaths.WarningSnapshotsDirectory(ExplorerPaths.GetModelIdentity(doc));
            var results = new List<(string, DateTimeOffset)>();

            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    var snapshot = JsonConvert.DeserializeObject<WarningSnapshot>(File.ReadAllText(path), Settings);
                    if (snapshot != null)
                    {
                        results.Add((path, snapshot.CreatedUtc));
                    }
                }
                catch
                {
                    // Skip unreadable snapshots.
                }
            }

            return results.OrderByDescending(entry => entry.Item2).ToList();
        }

        public static WarningSnapshot? LoadSnapshot(string path)
        {
            try
            {
                return JsonConvert.DeserializeObject<WarningSnapshot>(File.ReadAllText(path), Settings);
            }
            catch
            {
                return null;
            }
        }

        public static WarningDiff Diff(WarningSnapshot baseline, IReadOnlyList<WarningRecord> current)
        {
            var baselineKeys = new HashSet<string>(baseline.Warnings.Select(w => w.WarningKey), StringComparer.Ordinal);
            var currentKeys = new HashSet<string>(current.Select(w => w.WarningKey), StringComparer.Ordinal);

            return new WarningDiff(
                NewWarnings: current.Where(w => !baselineKeys.Contains(w.WarningKey)).ToList(),
                ResolvedWarnings: baseline.Warnings.Where(w => !currentKeys.Contains(w.WarningKey)).ToList(),
                BaselineUtc: baseline.CreatedUtc,
                CurrentUtc: DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Warning identity stable across sessions: failure definition + involved element ids.
        /// Revit does not expose a persistent warning id, so this is the best stable key.
        /// </summary>
        private static string CreateKey(string failureId, IReadOnlyList<long> failingIds, IReadOnlyList<long> additionalIds)
        {
            var builder = new StringBuilder(failureId);
            builder.Append('|');
            builder.Append(string.Join(",", failingIds));
            builder.Append('|');
            builder.Append(string.Join(",", additionalIds));
            return builder.ToString();
        }

        private static WarningRank ResolveRank(IReadOnlyList<WarningRanking> rankings, string failureId, string description)
        {
            foreach (var ranking in rankings)
            {
                if (!string.IsNullOrEmpty(ranking.FailureDefinitionId) &&
                    string.Equals(ranking.FailureDefinitionId, failureId, StringComparison.OrdinalIgnoreCase))
                {
                    return ranking.Rank;
                }

                if (!string.IsNullOrEmpty(ranking.DescriptionPattern) &&
                    MatchesPattern(description, ranking.DescriptionPattern!))
                {
                    return ranking.Rank;
                }
            }

            return WarningRank.NotRanked;
        }

        /// <summary>Case-insensitive match; a trailing '*' means prefix match, otherwise substring.</summary>
        private static bool MatchesPattern(string text, string pattern)
        {
            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                return text.StartsWith(pattern.Substring(0, pattern.Length - 1),
                    StringComparison.OrdinalIgnoreCase);
            }

            return text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IReadOnlyList<string> ResolveCategories(Document doc, IEnumerable<long> idValues)
        {
            var categories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var idValue in idValues)
            {
                try
                {
                    var name = doc.GetElement(new ElementId(idValue))?.Category?.Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        categories.Add(name!);
                    }
                }
                catch
                {
                    // Ignore unresolvable elements.
                }
            }

            return categories.ToList();
        }

        private static IReadOnlyList<string> ResolveNames(Document doc, IEnumerable<long> idValues)
        {
            var names = new List<string>();
            foreach (var idValue in idValues)
            {
                // Always add exactly one entry per id so names stay index-aligned with
                // FailingElementIds (export rows pair them by position).
                try
                {
                    var element = doc.GetElement(new ElementId(idValue));
                    names.Add(element != null ? $"{element.Name} [{idValue}]" : $"[{idValue}]");
                }
                catch
                {
                    names.Add($"[{idValue}]");
                }
            }

            return names;
        }

        /// <summary>Original default rankings for common warning families (editable via warning-rankings.json).</summary>
        private static readonly IReadOnlyList<WarningRanking> DefaultRankings = new List<WarningRanking>
        {
            new WarningRanking(null, "Duplicate*", WarningRank.Medium),
            new WarningRanking(null, "identical instances in the same place", WarningRank.High),
            new WarningRanking(null, "Room is not in a properly enclosed region", WarningRank.High),
            new WarningRanking(null, "Multiple Rooms are in the same enclosed region", WarningRank.High),
            new WarningRanking(null, "overlap", WarningRank.Medium),
            new WarningRanking(null, "slightly off axis", WarningRank.Medium),
            new WarningRanking(null, "Highlighted walls are attached to, but miss", WarningRank.Medium),
            new WarningRanking(null, "not visible", WarningRank.Low)
        };
    }
}
