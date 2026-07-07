using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// Converts live Revit elements into immutable <see cref="ElementRecord"/> DTOs.
    /// Must only be called on the Revit API thread (command Execute or ExternalEvent handler).
    /// </summary>
    internal static class ElementCollectionService
    {
        public static IReadOnlyList<ElementRecord> Collect(
            UIDocument uidoc,
            ExplorerScope scope,
            bool includeLinkedModels,
            bool includeUncategorized = false,
            Action<int>? progress = null,
            Func<bool>? isCancelled = null)
        {
            var doc = uidoc.Document;
            var records = new List<ElementRecord>();
            var options = new CollectOptions(includeUncategorized, progress, isCancelled);

            switch (scope)
            {
                case ExplorerScope.EntireProject:
                    AppendFromDocument(doc, "Host", isLinked: false, records, options);
                    if (includeLinkedModels)
                    {
                        // A link placed multiple times returns the same link document per
                        // placement; sweep each linked file once or counts inflate per instance.
                        var visitedLinkDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var instance in new FilteredElementCollector(doc)
                                     .OfClass(typeof(RevitLinkInstance))
                                     .Cast<RevitLinkInstance>())
                        {
                            var linkDoc = instance.GetLinkDocument();
                            if (linkDoc == null)
                            {
                                continue;
                            }

                            var linkKey = string.IsNullOrWhiteSpace(linkDoc.PathName)
                                ? linkDoc.Title
                                : linkDoc.PathName;
                            if (!visitedLinkDocs.Add(linkKey))
                            {
                                continue;
                            }

                            AppendFromDocument(linkDoc, linkDoc.Title, isLinked: true, records, options,
                                linkInstanceIdValue: instance.Id.Value);
                        }
                    }

                    break;

                case ExplorerScope.ActiveView:
                    AppendElements(
                        doc,
                        new FilteredElementCollector(doc, uidoc.ActiveView.Id)
                            .WhereElementIsNotElementType(),
                        "Host",
                        isLinked: false,
                        records,
                        options);
                    if (includeLinkedModels)
                    {
                        // A view-scoped collector never returns linked contents, so links are
                        // approximated: every link instance visible in this view contributes its
                        // elements clipped to the view's crop/section box (transformed into link
                        // coordinates). No crop active = the whole link.
                        AppendLinkedFromActiveView(uidoc, records, options);
                    }

                    break;

                case ExplorerScope.CurrentSelection:
                    var context = new RecordContext(doc, "Host", isLinked: false);
                    foreach (var id in uidoc.Selection.GetElementIds())
                    {
                        var element = doc.GetElement(id);
                        if (element == null || (element.Category == null && !includeUncategorized))
                        {
                            continue;
                        }

                        records.Add(CreateRecord(element, context));
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
            }

            return records;
        }

        public sealed record WarmResult(IReadOnlyList<ElementRecord> Records, string Note);

        /// <summary>
        /// Forma-style warm indexing for EntireProject scope: linked models come from the
        /// session cache (they cannot change while loaded), and the host is patched from
        /// DocumentChanged deltas instead of re-swept when possible. Falls back to a full
        /// sweep on the first run, heavy churn, or option changes. Other scopes delegate
        /// to the plain <see cref="Collect"/>.
        /// </summary>
        public static WarmResult CollectWarm(
            UIDocument uidoc,
            ExplorerScope scope,
            bool includeLinkedModels,
            bool includeUncategorized,
            IReadOnlyList<ElementRecord>? previousRecords,
            Action<int>? progress = null,
            Func<bool>? isCancelled = null)
        {
            if (scope != ExplorerScope.EntireProject)
            {
                return new WarmResult(
                    Collect(uidoc, scope, includeLinkedModels, includeUncategorized, progress, isCancelled),
                    "full index (view/selection scope)");
            }

            var doc = uidoc.Document;
            var options = new CollectOptions(includeUncategorized, progress, isCancelled);
            var records = new List<ElementRecord>();
            var notes = new List<string>();

            // ---- Host: delta patch when a previous baseline exists ----
            var previousHost = previousRecords?.Where(r => !r.IsLinked).ToList();
            var (upserts, deletes, overflow) = DocumentChangeTracker.TakeChanges(doc);
            var patched = false;

            if (previousHost is { Count: > 0 } && !overflow)
            {
                try
                {
                    var stale = new HashSet<long>(deletes);
                    stale.UnionWith(upserts);
                    var kept = previousHost.Where(r => !stale.Contains(r.IdValue)).ToList();

                    var context = new RecordContext(doc, "Host", isLinked: false);
                    var reindexed = 0;
                    foreach (var idValue in upserts)
                    {
                        var element = doc.GetElement(new ElementId(idValue));
                        if (element == null || element is ElementType)
                        {
                            continue;
                        }

                        if (element.Category == null && !includeUncategorized)
                        {
                            continue;
                        }

                        kept.Add(CreateRecord(element, context));
                        reindexed++;
                    }

                    records.AddRange(kept);
                    notes.Add(upserts.Count == 0 && deletes.Count == 0
                        ? "host unchanged"
                        : $"host patched ({reindexed} changed, {deletes.Count} deleted)");
                    patched = true;
                }
                catch
                {
                    records.Clear();
                }
            }

            if (!patched)
            {
                AppendFromDocument(doc, "Host", isLinked: false, records, options);
                DocumentChangeTracker.MarkClean(doc);
                notes.Add(overflow ? "host re-indexed (heavy churn)" : "host indexed");
            }

            // ---- Links: version-stamped session cache ----
            if (includeLinkedModels)
            {
                int cachedCount = 0, sweptCount = 0;
                foreach (var (instance, linkDoc) in GetDistinctLinkDocuments(doc))
                {
                    var cached = LinkIndexCache.TryGet(linkDoc, instance.Id.Value, includeUncategorized);
                    if (cached != null)
                    {
                        records.AddRange(cached);
                        cachedCount++;
                        continue;
                    }

                    var linkRecords = new List<ElementRecord>();
                    AppendFromDocument(linkDoc, linkDoc.Title, isLinked: true, linkRecords, options,
                        linkInstanceIdValue: instance.Id.Value);
                    LinkIndexCache.Store(linkDoc, instance.Id.Value, includeUncategorized, linkRecords);
                    records.AddRange(linkRecords);
                    sweptCount++;
                }

                if (cachedCount + sweptCount > 0)
                {
                    notes.Add($"links: {cachedCount} from cache, {sweptCount} indexed");
                }
            }

            return new WarmResult(records, string.Join("; ", notes));
        }

        /// <summary>Loaded link documents, one entry per distinct linked file (first placement wins).</summary>
        public static IReadOnlyList<(RevitLinkInstance Instance, Document Document)> GetDistinctLinkDocuments(Document doc)
        {
            var result = new List<(RevitLinkInstance, Document)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                var linkDoc = instance.GetLinkDocument();
                if (linkDoc == null)
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(linkDoc.PathName) ? linkDoc.Title : linkDoc.PathName;
                if (visited.Add(key))
                {
                    result.Add((instance, linkDoc));
                }
            }

            return result;
        }

        private static void AppendLinkedFromActiveView(UIDocument uidoc, List<ElementRecord> sink, CollectOptions options)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            var viewBox = GetViewExtentsBox(view);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // A view-scoped collector for RevitLinkInstance naturally honors hide/VG state,
            // so only links actually visible in this view are swept.
            foreach (var instance in new FilteredElementCollector(doc, view.Id)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                var linkDoc = instance.GetLinkDocument();
                if (linkDoc == null)
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(linkDoc.PathName) ? linkDoc.Title : linkDoc.PathName;
                if (!visited.Add(key))
                {
                    continue;
                }

                var collector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType();
                if (viewBox != null)
                {
                    var outline = TransformViewBoxToLinkOutline(viewBox, view.ViewType, instance);
                    if (outline != null)
                    {
                        collector = collector.WherePasses(new BoundingBoxIntersectsFilter(outline));
                    }
                }

                AppendElements(linkDoc, collector, linkDoc.Title, isLinked: true, sink, options,
                    linkInstanceIdValue: instance.Id.Value);
            }
        }

        /// <summary>The active view's spatial extents: 3D section box, else crop box, else null (uncropped).</summary>
        private static BoundingBoxXYZ? GetViewExtentsBox(View view)
        {
            try
            {
                if (view is View3D { IsSectionBoxActive: true } view3D)
                {
                    return view3D.GetSectionBox();
                }

                if (view.CropBoxActive)
                {
                    return view.CropBox;
                }
            }
            catch
            {
                // Some view kinds have no usable extents.
            }

            return null;
        }

        /// <summary>
        /// Converts a view extents box into an axis-aligned outline in the link's coordinate
        /// space (conservative: bounding box of the transformed corners). Plan-view crop boxes
        /// carry unreliable Z, so plans keep X/Y and open up Z.
        /// </summary>
        private static Outline? TransformViewBoxToLinkOutline(
            BoundingBoxXYZ box,
            ViewType viewType,
            RevitLinkInstance instance)
        {
            try
            {
                var toModel = box.Transform ?? Transform.Identity;
                var toLink = instance.GetTotalTransform().Inverse;

                XYZ? min = null, max = null;
                foreach (var x in new[] { box.Min.X, box.Max.X })
                foreach (var y in new[] { box.Min.Y, box.Max.Y })
                foreach (var z in new[] { box.Min.Z, box.Max.Z })
                {
                    var point = toLink.OfPoint(toModel.OfPoint(new XYZ(x, y, z)));
                    min = min == null
                        ? point
                        : new XYZ(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                    max = max == null
                        ? point
                        : new XYZ(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
                }

                if (min == null || max == null)
                {
                    return null;
                }

                if (viewType is ViewType.FloorPlan or ViewType.CeilingPlan
                    or ViewType.EngineeringPlan or ViewType.AreaPlan)
                {
                    min = new XYZ(min.X, min.Y, -1e6);
                    max = new XYZ(max.X, max.Y, 1e6);
                }

                return new Outline(min, max);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Loaded vs unloaded link counts, so the UI can say why a link isn't indexable.</summary>
        public static (int Loaded, int Unloaded) CountLinkStatus(Document doc)
        {
            var loaded = GetDistinctLinkDocuments(doc).Count;
            var unloaded = 0;

            foreach (var linkType in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkType))
                         .Cast<RevitLinkType>())
            {
                try
                {
                    if (linkType.GetLinkedFileStatus() != LinkedFileStatus.Loaded)
                    {
                        unloaded++;
                    }
                }
                catch
                {
                    // Treat unknown status as not indexable.
                    unloaded++;
                }
            }

            return (loaded, unloaded);
        }

        private sealed record CollectOptions(bool IncludeUncategorized, Action<int>? Progress, Func<bool>? IsCancelled);

        private static void AppendFromDocument(
            Document doc, string origin, bool isLinked, List<ElementRecord> sink, CollectOptions options,
            long? linkInstanceIdValue = null)
        {
            AppendElements(
                doc,
                new FilteredElementCollector(doc).WhereElementIsNotElementType(),
                origin,
                isLinked,
                sink,
                options,
                linkInstanceIdValue);
        }

        private static void AppendElements(
            Document doc,
            FilteredElementCollector collector,
            string origin,
            bool isLinked,
            List<ElementRecord> sink,
            CollectOptions options,
            long? linkInstanceIdValue = null)
        {
            const int progressInterval = 2500;
            var context = new RecordContext(doc, origin, isLinked, linkInstanceIdValue);
            var sinceCheck = 0;

            foreach (var element in collector)
            {
                if (++sinceCheck >= progressInterval)
                {
                    sinceCheck = 0;
                    options.Progress?.Invoke(sink.Count);
                    if (options.IsCancelled?.Invoke() == true)
                    {
                        throw new OperationCanceledException();
                    }
                }

                if (element == null || (element.Category == null && !options.IncludeUncategorized))
                {
                    continue;
                }

                sink.Add(CreateRecord(element, context));
            }
        }

        public static ElementRecord CreateRecord(Element element, RecordContext context)
        {
            string? category = null;
            try
            {
                category = element.Category?.Name;
            }
            catch
            {
                // Some internal elements throw on Category access; treat as uncategorized.
            }

            var typeId = element.GetTypeId();
            var hasType = typeId != ElementId.InvalidElementId;

            return new ElementRecord(
                IdValue: element.Id.Value,
                UniqueId: element.UniqueId,
                Category: category,
                Family: context.GetFamilyName(element, hasType ? typeId : null),
                TypeName: hasType ? context.GetTypeName(typeId) : null,
                InstanceName: SafeName(element),
                TypeIdValue: hasType ? typeId.Value : null,
                LevelName: context.GetLevelName(element),
                WorksetName: context.GetWorksetName(element),
                OwnerViewName: context.GetOwnerViewName(element),
                DesignOptionName: context.GetDesignOptionName(element),
                Origin: context.Origin,
                IsLinked: context.IsLinked,
                LinkInstanceIdValue: context.LinkInstanceIdValue,
                IsElementType: element is ElementType,
                IsViewSpecific: element.ViewSpecific,
                IsPinned: element.Pinned,
                IsInGroup: element.GroupId != ElementId.InvalidElementId);
        }

        private static string? SafeName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Per-document caches so a 100k-element sweep doesn't resolve the same type/level/workset names repeatedly.</summary>
        internal sealed class RecordContext
        {
            private readonly Document _doc;
            private readonly Dictionary<long, string?> _typeNames = new Dictionary<long, string?>();
            private readonly Dictionary<long, string?> _familyNames = new Dictionary<long, string?>();
            private readonly Dictionary<long, string?> _levelNames = new Dictionary<long, string?>();
            private readonly Dictionary<long, string?> _viewNames = new Dictionary<long, string?>();
            private readonly Dictionary<long, string?> _designOptionNames = new Dictionary<long, string?>();
            private readonly Dictionary<int, string?> _worksetNames = new Dictionary<int, string?>();
            private readonly bool _isWorkshared;

            public RecordContext(Document doc, string origin, bool isLinked, long? linkInstanceIdValue = null)
            {
                _doc = doc;
                Origin = origin;
                IsLinked = isLinked;
                LinkInstanceIdValue = linkInstanceIdValue;
                _isWorkshared = doc.IsWorkshared;
            }

            public string Origin { get; }
            public bool IsLinked { get; }
            public long? LinkInstanceIdValue { get; }

            public string? GetTypeName(ElementId typeId)
            {
                if (_typeNames.TryGetValue(typeId.Value, out var cached))
                {
                    return cached;
                }

                var name = SafeName(_doc.GetElement(typeId));
                _typeNames[typeId.Value] = name;
                return name;
            }

            public string? GetFamilyName(Element element, ElementId? typeId)
            {
                if (element is FamilyInstance familyInstance)
                {
                    try
                    {
                        return familyInstance.Symbol?.Family?.Name;
                    }
                    catch
                    {
                        return null;
                    }
                }

                if (typeId == null)
                {
                    return null;
                }

                if (_familyNames.TryGetValue(typeId.Value, out var cached))
                {
                    return cached;
                }

                string? familyName = null;
                try
                {
                    familyName = (_doc.GetElement(typeId) as ElementType)?.FamilyName;
                }
                catch
                {
                    // Leave null.
                }

                _familyNames[typeId.Value] = familyName;
                return familyName;
            }

            public string? GetLevelName(Element element)
            {
                var levelId = element.LevelId;
                if (levelId == ElementId.InvalidElementId)
                {
                    return null;
                }

                if (_levelNames.TryGetValue(levelId.Value, out var cached))
                {
                    return cached;
                }

                var name = SafeName(_doc.GetElement(levelId));
                _levelNames[levelId.Value] = name;
                return name;
            }

            public string? GetOwnerViewName(Element element)
            {
                var viewId = element.OwnerViewId;
                if (viewId == ElementId.InvalidElementId)
                {
                    return null;
                }

                if (_viewNames.TryGetValue(viewId.Value, out var cached))
                {
                    return cached;
                }

                var name = SafeName(_doc.GetElement(viewId));
                _viewNames[viewId.Value] = name;
                return name;
            }

            public string? GetDesignOptionName(Element element)
            {
                ElementId? optionId = null;
                try
                {
                    optionId = element.get_Parameter(BuiltInParameter.DESIGN_OPTION_ID)?.AsElementId();
                }
                catch
                {
                    // Leave null.
                }

                if (optionId == null || optionId == ElementId.InvalidElementId)
                {
                    return null;
                }

                if (_designOptionNames.TryGetValue(optionId.Value, out var cached))
                {
                    return cached;
                }

                var name = SafeName(_doc.GetElement(optionId));
                _designOptionNames[optionId.Value] = name;
                return name;
            }

            public string? GetWorksetName(Element element)
            {
                if (!_isWorkshared)
                {
                    return null;
                }

                var worksetId = element.WorksetId;
                if (worksetId == null)
                {
                    return null;
                }

                if (_worksetNames.TryGetValue(worksetId.IntegerValue, out var cached))
                {
                    return cached;
                }

                string? name = null;
                try
                {
                    name = _doc.GetWorksetTable().GetWorkset(worksetId)?.Name;
                }
                catch
                {
                    // Leave null.
                }

                _worksetNames[worksetId.IntegerValue] = name;
                return name;
            }

            private static string? SafeName(Element? element)
            {
                try
                {
                    return element?.Name;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
