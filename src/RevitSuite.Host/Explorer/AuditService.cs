using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// Runs audit rule packs and computes the model health score.
    /// Built-in rules use code detectors; user rules may define parameter queries.
    /// </summary>
    internal static class AuditService
    {
        private const int SnapshotSchemaVersion = 1;
        private const string BuiltInPackFileName = "revitsuite-core.rules.json";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() }
        };

        public sealed record LoadedPack(AuditRulePack? Pack, string FilePath, string? Error);

        public static IReadOnlyList<LoadedPack> LoadPacks()
        {
            EnsureBuiltInPack();

            var packs = new List<LoadedPack>();
            foreach (var path in Directory.GetFiles(ExplorerPaths.RulesDirectory, "*.rules.json")
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var pack = JsonConvert.DeserializeObject<AuditRulePack>(File.ReadAllText(path), Settings);
                    var error = pack == null ? "File does not contain a rule pack." : ValidatePack(pack);
                    packs.Add(new LoadedPack(error == null ? pack : null, path, error));
                }
                catch (Exception ex)
                {
                    packs.Add(new LoadedPack(null, path, $"Invalid rule pack JSON: {ex.Message}"));
                }
            }

            return packs;
        }

        public static string? ValidatePack(AuditRulePack pack)
        {
            if (string.IsNullOrWhiteSpace(pack.PackId))
            {
                return "Rule pack id is required.";
            }

            if (pack.Rules == null || pack.Rules.Count == 0)
            {
                return "Rule pack contains no rules.";
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in pack.Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    return "Every rule needs an id.";
                }

                if (!seenIds.Add(rule.Id))
                {
                    return $"Duplicate rule id '{rule.Id}'.";
                }

                if (rule.DetectorId == null && rule.Query == null)
                {
                    return $"Rule '{rule.Id}' needs either a detector or a query.";
                }

                if (rule.DetectorId != null && !Detectors.ContainsKey(rule.DetectorId))
                {
                    return $"Rule '{rule.Id}' references unknown detector '{rule.DetectorId}'.";
                }

                if (rule.Query != null)
                {
                    var queryError = FilterStore.Validate(rule.Query);
                    if (queryError != null)
                    {
                        return $"Rule '{rule.Id}': {queryError}";
                    }
                }
            }

            return null;
        }

        /// <summary>API context required.</summary>
        public static IReadOnlyList<AuditFinding> Run(UIDocument uidoc, IEnumerable<AuditRulePack> packs)
        {
            var findings = new List<AuditFinding>();

            foreach (var pack in packs)
            {
                foreach (var rule in pack.Rules.Where(r => r.Enabled))
                {
                    IReadOnlyList<long> elementIds;
                    if (rule.DetectorId != null && Detectors.TryGetValue(rule.DetectorId, out var detector))
                    {
                        elementIds = detector(uidoc.Document);
                    }
                    else if (rule.Query != null)
                    {
                        elementIds = QueryRunner.Run(uidoc, rule.Query).Select(r => r.IdValue).ToList();
                    }
                    else
                    {
                        continue;
                    }

                    if (elementIds.Count == 0)
                    {
                        continue;
                    }

                    findings.Add(new AuditFinding(
                        RuleId: rule.Id,
                        RuleName: rule.Name,
                        Severity: rule.Severity,
                        ElementIds: elementIds,
                        Summary: $"{elementIds.Count} element(s) matched '{rule.Name}'.",
                        WhyItMatters: rule.WhyItMatters,
                        SafeFixGuidance: rule.SafeFixGuidance));
                }
            }

            return findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.RuleName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Transparent weighted score: every component and its deduction is listed,
        /// so the number is explainable rather than a black box.
        /// </summary>
        public static HealthScore ComputeHealth(
            IReadOnlyList<AuditFinding> findings,
            IReadOnlyList<WarningRecord> warnings)
        {
            var components = new List<HealthScoreComponent>();

            foreach (var finding in findings)
            {
                var weight = SeverityWeight(finding.Severity);
                if (weight <= 0)
                {
                    continue;
                }

                components.Add(new HealthScoreComponent(
                    $"Audit: {finding.RuleName}",
                    finding.Severity,
                    finding.ElementIds.Count,
                    Math.Round(Math.Min(finding.ElementIds.Count * weight, 25.0), 2)));
            }

            foreach (var group in warnings.GroupBy(w => w.Rank))
            {
                var weight = RankWeight(group.Key);
                if (weight <= 0)
                {
                    continue;
                }

                var count = group.Count();
                components.Add(new HealthScoreComponent(
                    $"Warnings: {group.Key}",
                    group.Key == WarningRank.High ? AuditSeverity.High : AuditSeverity.Medium,
                    count,
                    Math.Round(Math.Min(count * weight, 30.0), 2)));
            }

            var score = Math.Max(0.0, 100.0 - components.Sum(c => c.Deduction));
            return new HealthScore(Math.Round(score, 1), components, DateTimeOffset.UtcNow);
        }

        public static string SaveSnapshot(Document doc, IReadOnlyList<AuditFinding> findings, HealthScore health)
        {
            var identity = ExplorerPaths.GetModelIdentity(doc);
            var snapshot = new AuditSnapshot(
                SnapshotSchemaVersion, identity, doc.Title, DateTimeOffset.UtcNow, findings, health);

            var path = Path.Combine(
                ExplorerPaths.AuditSnapshotsDirectory(identity),
                $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, Settings));
            return path;
        }

        public static IReadOnlyList<AuditSnapshot> LoadSnapshots(Document doc)
        {
            var directory = ExplorerPaths.AuditSnapshotsDirectory(ExplorerPaths.GetModelIdentity(doc));
            var snapshots = new List<AuditSnapshot>();

            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    var snapshot = JsonConvert.DeserializeObject<AuditSnapshot>(File.ReadAllText(path), Settings);
                    if (snapshot != null)
                    {
                        snapshots.Add(snapshot);
                    }
                }
                catch
                {
                    // Skip unreadable snapshots.
                }
            }

            return snapshots.OrderByDescending(s => s.CreatedUtc).ToList();
        }

        private static double SeverityWeight(AuditSeverity severity) => severity switch
        {
            AuditSeverity.Critical => 4.0,
            AuditSeverity.High => 2.0,
            AuditSeverity.Medium => 0.8,
            AuditSeverity.Low => 0.25,
            _ => 0.0
        };

        private static double RankWeight(WarningRank rank) => rank switch
        {
            WarningRank.High => 2.0,
            WarningRank.Medium => 0.8,
            WarningRank.Low => 0.25,
            WarningRank.NotRanked => 0.4,
            _ => 0.0
        };

        private static void EnsureBuiltInPack()
        {
            var path = Path.Combine(ExplorerPaths.RulesDirectory, BuiltInPackFileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(BuildBuiltInPack(), Settings));
            }
        }

        private static AuditRulePack BuildBuiltInPack()
        {
            var rules = new List<AuditRule>
            {
                new AuditRule("cad-imports", "CAD imports and links",
                    "All DWG/DXF/DGN imports and links in the model.", AuditSeverity.High,
                    "CAD imports and links can bloat file size, slow views, and print unexpected linework.",
                    "Use Show to locate each import. Confirm with the team whether it is still needed before deleting.",
                    "cad-imports", null),

                new AuditRule("view-specific-cad", "View-specific CAD imports",
                    "CAD imports placed in a single view, hidden from model-wide review.", AuditSeverity.High,
                    "View-specific CAD hides in one view and can silently affect documentation output.",
                    "Open the owner view, review sheet placement, and delete only once the documentation impact is understood.",
                    "view-specific-cad", null),

                new AuditRule("in-place-families", "In-place family instances",
                    "Instances of families modeled in place.", AuditSeverity.Medium,
                    "In-place families block reuse, complicate schedules, and are hard to standardize.",
                    "Evaluate whether the geometry should become a loadable family.",
                    "in-place-families", null),

                new AuditRule("model-groups", "Model and detail groups",
                    "All group instances in the model.", AuditSeverity.Medium,
                    "Heavily used or nested groups can hurt performance and cause duplication warnings.",
                    "Review whether groups are still needed; consider links or assemblies for repeated content.",
                    "groups", null),

                new AuditRule("arrays", "Array elements",
                    "Grouped array relationships in the model.", AuditSeverity.Medium,
                    "Live arrays keep relationships that can slow edits and surprise users when members change together.",
                    "Ungroup arrays that no longer need to stay parametric.",
                    "arrays", null),

                new AuditRule("unnamed-reference-planes", "Unnamed reference planes",
                    "Reference planes without a name.", AuditSeverity.Low,
                    "Unnamed reference planes accumulate as invisible clutter nobody dares to delete.",
                    "Name the planes that matter and delete the rest after checking for hosted dependencies.",
                    "unnamed-reference-planes", null),

                new AuditRule("scope-boxes", "Scope boxes",
                    "All scope boxes in the model.", AuditSeverity.Low,
                    "Stale scope boxes confuse view extents and datum propagation.",
                    "Verify each scope box is still referenced by views or datums before removing.",
                    "scope-boxes", null),

                new AuditRule("revision-clouds", "Revision clouds",
                    "All revision clouds in the model.", AuditSeverity.Medium,
                    "Old revision clouds left visible can misrepresent the current revision state.",
                    "Confirm the revision workflow before hiding or deleting clouds.",
                    "revision-clouds", null),

                new AuditRule("plan-regions", "Plan regions",
                    "All plan regions in the model.", AuditSeverity.Medium,
                    "Plan regions override view ranges locally and are easy to forget.",
                    "Check each plan region still reflects an intended view-range override.",
                    "plan-regions", null),

                new AuditRule("raster-images", "Raster images",
                    "All placed raster images.", AuditSeverity.Medium,
                    "Raster images inflate file size and can print poorly.",
                    "Replace with vector content where possible, or confirm the image is still required.",
                    "raster-images", null),

                new AuditRule("duplicate-door-marks", "Duplicate door marks",
                    "Doors sharing the same non-empty Mark value.", AuditSeverity.Medium,
                    "Duplicate marks create ambiguous schedules, tags, and quantity takeoffs.",
                    "Select the affected doors, decide the correct mark values, and renumber the duplicates.",
                    "duplicate-door-marks", null),

                new AuditRule("duplicate-room-numbers", "Duplicate room numbers",
                    "Placed rooms sharing the same number.", AuditSeverity.High,
                    "Duplicate room numbers corrupt schedules, tags, and downstream space data.",
                    "Review the affected rooms and renumber so every placed room is unique.",
                    "duplicate-room-numbers", null),

                new AuditRule("zero-area-rooms", "Unplaced, redundant, or unenclosed rooms",
                    "Rooms that report zero area (unplaced, redundant, or not enclosed).", AuditSeverity.High,
                    "Zero-area rooms corrupt area totals, schedules, and downstream analysis.",
                    "Check whether each room should be placed, re-enclosed, or deleted. Confirm phases before deleting.",
                    "zero-area-rooms", null)
            };

            return new AuditRulePack(1, "revitsuite-core", "RevitSuite Core Audit Rules", "RevitSuite", rules);
        }

        private static readonly IReadOnlyDictionary<string, Func<Document, IReadOnlyList<long>>> Detectors =
            new Dictionary<string, Func<Document, IReadOnlyList<long>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["cad-imports"] = doc => Ids(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))),

                ["view-specific-cad"] = doc => new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance))
                    .Where(e => e.ViewSpecific)
                    .Select(e => e.Id.Value)
                    .ToList(),

                ["in-place-families"] = CollectInPlaceInstances,

                ["groups"] = doc => Ids(new FilteredElementCollector(doc)
                    .OfClass(typeof(Group))
                    .WhereElementIsNotElementType()),

                ["arrays"] = doc => Ids(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSArrays)),

                ["unnamed-reference-planes"] = doc => new FilteredElementCollector(doc)
                    .OfClass(typeof(ReferencePlane))
                    .Where(e =>
                    {
                        try
                        {
                            var name = e.Name;
                            return string.IsNullOrWhiteSpace(name) ||
                                   string.Equals(name, "Reference Plane", StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return true;
                        }
                    })
                    .Select(e => e.Id.Value)
                    .ToList(),

                ["scope-boxes"] = doc => Ids(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_VolumeOfInterest)),

                ["revision-clouds"] = doc => Ids(new FilteredElementCollector(doc)
                    .OfClass(typeof(RevisionCloud))),

                ["plan-regions"] = doc => Ids(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PlanRegion)
                    .WhereElementIsNotElementType()),

                ["raster-images"] = doc => Ids(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RasterImages)
                    .WhereElementIsNotElementType()),

                ["duplicate-door-marks"] = doc => CollectDuplicateParameterValues(
                    doc, BuiltInCategory.OST_Doors, BuiltInParameter.ALL_MODEL_MARK, requirePlaced: false),

                ["duplicate-room-numbers"] = doc => CollectDuplicateParameterValues(
                    doc, BuiltInCategory.OST_Rooms, BuiltInParameter.ROOM_NUMBER, requirePlaced: true),

                ["zero-area-rooms"] = doc => new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<Room>()
                    .Where(room => room.Area <= 1e-9)
                    .Select(room => room.Id.Value)
                    .ToList()
            };

        private static IReadOnlyList<long> Ids(FilteredElementCollector collector) =>
            collector.ToElementIds().Select(id => id.Value).ToList();

        private static IReadOnlyList<long> CollectInPlaceInstances(Document doc)
        {
            var ids = new List<long>();

            foreach (var family in new FilteredElementCollector(doc)
                         .OfClass(typeof(Family))
                         .Cast<Family>()
                         .Where(f => f.IsInPlace))
            {
                foreach (var symbolId in family.GetFamilySymbolIds())
                {
                    ids.AddRange(new FilteredElementCollector(doc)
                        .WherePasses(new FamilyInstanceFilter(doc, symbolId))
                        .ToElementIds()
                        .Select(id => id.Value));
                }
            }

            return ids;
        }

        private static IReadOnlyList<long> CollectDuplicateParameterValues(
            Document doc,
            BuiltInCategory category,
            BuiltInParameter parameter,
            bool requirePlaced)
        {
            var byValue = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in new FilteredElementCollector(doc)
                         .OfCategory(category)
                         .WhereElementIsNotElementType())
            {
                if (requirePlaced && element is SpatialElement { Location: null })
                {
                    continue;
                }

                var value = element.get_Parameter(parameter)?.AsString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!byValue.TryGetValue(value!, out var list))
                {
                    byValue[value!] = list = new List<long>();
                }

                list.Add(element.Id.Value);
            }

            return byValue.Values
                .Where(list => list.Count > 1)
                .SelectMany(list => list)
                .ToList();
        }
    }
}
