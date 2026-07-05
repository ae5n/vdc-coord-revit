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
