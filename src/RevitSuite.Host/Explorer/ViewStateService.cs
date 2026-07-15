using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// Ground truth of what the active view shows, with three-way classification per record:
    /// visible (true), hidden (false), or not applicable (null — nothing graphical to show,
    /// e.g. Materials/Phases). Built on the Revit thread, immutable, safe for the UI thread.
    /// </summary>
    public sealed class ViewVisibilitySnapshot
    {
        private readonly HashSet<long> _visibleHostIds;
        private readonly HashSet<long> _hiddenHostIds;
        private readonly HashSet<long> _elementHiddenHostIds;
        private readonly HashSet<long> _hiddenLinkInstanceIds;
        private readonly HashSet<string> _hiddenCategoryNames;
        private readonly Dictionary<long, string> _filterNameByHostId;
        private readonly Dictionary<long, HashSet<long>> _visibleLinkedIdsByInstance;
        private readonly HashSet<string> _visibleCategoryNames;

        public ViewVisibilitySnapshot(
            long viewIdValue,
            string viewName,
            bool temporaryIsolateActive,
            HashSet<long> visibleHostIds,
            HashSet<long> hiddenHostIds,
            HashSet<long> elementHiddenHostIds,
            HashSet<long> hiddenLinkInstanceIds,
            HashSet<string> hiddenCategoryNames,
            Dictionary<long, string> filterNameByHostId,
            Dictionary<long, HashSet<long>> visibleLinkedIdsByInstance,
            HashSet<string> visibleCategoryNames)
        {
            ViewIdValue = viewIdValue;
            ViewName = viewName;
            TemporaryIsolateActive = temporaryIsolateActive;
            _visibleHostIds = visibleHostIds;
            _hiddenHostIds = hiddenHostIds;
            _elementHiddenHostIds = elementHiddenHostIds;
            _hiddenLinkInstanceIds = hiddenLinkInstanceIds;
            _hiddenCategoryNames = hiddenCategoryNames;
            _filterNameByHostId = filterNameByHostId;
            _visibleLinkedIdsByInstance = visibleLinkedIdsByInstance;
            _visibleCategoryNames = visibleCategoryNames;
        }

        public long ViewIdValue { get; }
        public string ViewName { get; }

        /// <summary>
        /// Revit's collectors ignore TEMPORARY hide/isolate, so while it is active the
        /// checkboxes describe the underlying (permanent) visibility — surfaced in the UI
        /// rather than silently mis-reported.
        /// </summary>
        public bool TemporaryIsolateActive { get; }

        // Truth-based restore needs the raw hidden sets — hides are saved with the model,
        // so in-memory trackers can never be the source of record for "unhide everything".
        public IReadOnlyCollection<long> HiddenHostIds => _hiddenHostIds;
        public IReadOnlyCollection<long> HiddenLinkInstanceIds => _hiddenLinkInstanceIds;
        public IReadOnlyCollection<string> HiddenCategoryNames => _hiddenCategoryNames;

        /// <summary>Visibility-off view filters that actually hide indexed elements.</summary>
        public IReadOnlyCollection<string> HiddenFilterNames =>
            _filterNameByHostId.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>true = visible, false = hidden, null = not applicable in this view.</summary>
        public bool? Classify(ElementRecord record)
        {
            if (record.IsLinked)
            {
                if (record.LinkInstanceIdValue is not { } linkId)
                {
                    return true;
                }

                if (_hiddenLinkInstanceIds.Contains(linkId))
                {
                    return false;
                }

                if (record.Category != null && _hiddenCategoryNames.Contains(record.Category))
                {
                    return false;
                }

                // Per-element link truth (Revit 2024+ link-aware view collector): drawn →
                // visible. Not drawn → hidden, but ONLY when its category demonstrably draws
                // in this view (guards against calling out-of-range/non-graphical records
                // hidden — the linked counterpart of the host IsHidden disambiguation, which
                // Revit does not offer for linked elements).
                if (_visibleLinkedIdsByInstance.TryGetValue(linkId, out var visibleInLink))
                {
                    if (visibleInLink.Contains(record.IdValue))
                    {
                        return true;
                    }

                    return record.Category != null && _visibleCategoryNames.Contains(record.Category)
                        ? false
                        : (bool?)null;
                }

                // Collector unavailable for this view kind — legacy assumption.
                return true;
            }

            if (_visibleHostIds.Contains(record.IdValue))
            {
                return true;
            }

            // Not visible: hidden if Revit says so (element hide / category / filter),
            // otherwise it simply has nothing to draw here — not applicable, never a lie.
            return _hiddenHostIds.Contains(record.IdValue) ? false : (bool?)null;
        }

        /// <summary>
        /// WHY a record is hidden — the two Revit hide mechanisms are independent and both
        /// must be lifted for the element to reappear, so the distinction is surfaced
        /// instead of a bare "hidden". Null when the record is not hidden.
        /// </summary>
        public string? DescribeHidden(ElementRecord record)
        {
            if (Classify(record) != false)
            {
                return null;
            }

            if (record.IsLinked)
            {
                if (record.LinkInstanceIdValue is { } linkId && _hiddenLinkInstanceIds.Contains(linkId))
                {
                    return record.Category != null && _hiddenCategoryNames.Contains(record.Category)
                        ? $"Hidden: link instance hidden AND category '{record.Category}' off in Visibility/Graphics — unhide must lift both"
                        : "Hidden: the link instance is hidden in this view";
                }

                if (record.Category != null && _hiddenCategoryNames.Contains(record.Category))
                {
                    return $"Hidden: category '{record.Category}' is off in Visibility/Graphics";
                }

                return "Hidden inside the link: individually hidden element, view filter, phase, or view " +
                       "range (Revit does not report which for linked elements). Individually hidden " +
                       "linked elements can only be unhidden via Revit's Reveal Hidden Elements mode.";
            }

            var categoryOff = record.Category != null && _hiddenCategoryNames.Contains(record.Category);
            var elementHidden = _elementHiddenHostIds.Contains(record.IdValue);

            if (categoryOff && elementHidden)
            {
                return $"Hidden: element hidden (right-click ▸ Hide in View) AND category '{record.Category}' " +
                       "off in Visibility/Graphics — unhide must lift both";
            }

            if (elementHidden)
            {
                return "Hidden: element hidden in this view (right-click ▸ Hide in View ▸ Elements)";
            }

            if (categoryOff)
            {
                return $"Hidden: category '{record.Category}' is off in Visibility/Graphics";
            }

            if (_filterNameByHostId.TryGetValue(record.IdValue, out var filterName))
            {
                return $"Hidden: matches view filter '{filterName}', which is set to not visible " +
                       "in Visibility/Graphics ▸ Filters";
            }

            return "Hidden in the active view";
        }

        /// <summary>
        /// Compact hide-MECHANISM tag rendered next to the eye glyph in the tree:
        /// "VG" (category off), "elem" (element hide), "VG+elem" (both), "filter"
        /// (visibility-off view filter), "link off" (the link instance is hidden —
        /// distinct from the 🔗 origin marker, which merely says where the element
        /// lives), "link off+VG". Null when not hidden.
        /// </summary>
        public string? HiddenTag(ElementRecord record)
        {
            if (Classify(record) != false)
            {
                return null;
            }

            var categoryOff = record.Category != null && _hiddenCategoryNames.Contains(record.Category);

            if (record.IsLinked)
            {
                var linkHidden = record.LinkInstanceIdValue is { } linkId &&
                                 _hiddenLinkInstanceIds.Contains(linkId);
                if (linkHidden)
                {
                    return categoryOff ? "link off+VG" : "link off";
                }

                return categoryOff ? "VG" : "hidden";
            }

            var elementHidden = _elementHiddenHostIds.Contains(record.IdValue);
            if (categoryOff || elementHidden)
            {
                return categoryOff
                    ? elementHidden ? "VG+elem" : "VG"
                    : "elem";
            }

            return _filterNameByHostId.ContainsKey(record.IdValue) ? "filter" : "hidden";
        }

        /// <summary>
        /// Names of the visibility-off view filters responsible for hiding any of the given
        /// records — lets Unhide lift the right filters (view-wide, stated honestly).
        /// </summary>
        public IReadOnlyList<string> HiddenFilterNamesFor(IEnumerable<ElementRecord> records)
        {
            var names = new List<string>();
            foreach (var record in records)
            {
                if (!record.IsLinked &&
                    _filterNameByHostId.TryGetValue(record.IdValue, out var name) &&
                    !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>Captures view-visibility ground truth. API context required.</summary>
    internal static class ViewStateService
    {
        /// <summary>
        /// One native collector pass for the visible set; the indexed host records that are
        /// NOT in it are classified hidden vs not-applicable via IsHidden/category checks.
        /// </summary>
        public static ViewVisibilitySnapshot Capture(UIDocument uidoc, IReadOnlyList<ElementRecord> records)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            var visibleHostIds = new HashSet<long>();
            try
            {
                foreach (var id in new FilteredElementCollector(doc, view.Id)
                             .WhereElementIsNotElementType()
                             .ToElementIds())
                {
                    visibleHostIds.Add(id.Value);
                }
            }
            catch
            {
                // Sheets/schedules reject element collection; empty set + hidden checks below
                // classify everything honestly as hidden or not-applicable.
            }

            var hiddenCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Category category in doc.Settings.Categories)
                {
                    try
                    {
                        if (view.GetCategoryHidden(category.Id))
                        {
                            hiddenCategoryNames.Add(category.Name);
                        }
                    }
                    catch
                    {
                        // Category not applicable to this view kind.
                    }
                }
            }
            catch
            {
                // Leave empty.
            }

            // Classify the not-visible host records: explicitly hidden vs nothing-to-draw.
            // The element-hide flag is checked even when the category is already off — the
            // two mechanisms stack in Revit, and knowing BOTH apply changes how the user
            // (and Unhide) must restore the element.
            var hiddenHostIds = new HashSet<long>();
            var elementHiddenHostIds = new HashSet<long>();
            foreach (var record in records)
            {
                if (record.IsLinked || visibleHostIds.Contains(record.IdValue))
                {
                    continue;
                }

                var categoryOff = record.Category != null && hiddenCategoryNames.Contains(record.Category);
                if (categoryOff)
                {
                    hiddenHostIds.Add(record.IdValue);
                }

                try
                {
                    var element = doc.GetElement(new ElementId(record.IdValue));
                    if (element != null && element.IsHidden(view))
                    {
                        hiddenHostIds.Add(record.IdValue);
                        elementHiddenHostIds.Add(record.IdValue);
                    }
                }
                catch
                {
                    // Unqueryable → not applicable.
                }
            }

            var hiddenLinks = new HashSet<long>();
            try
            {
                foreach (var instance in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)))
                {
                    try
                    {
                        if (instance.IsHidden(view))
                        {
                            hiddenLinks.Add(instance.Id.Value);
                        }
                    }
                    catch
                    {
                        // Skip instances that reject the query.
                    }
                }
            }
            catch
            {
                // No links.
            }

            // VG Filters: elements still unexplained (not visible, not element/category-hidden)
            // are tested against the view's visibility-OFF filters — only those candidates,
            // so the cost stays proportional to what's actually unaccounted for. An element
            // matching such a filter is reported hidden-by-filter (it may additionally be
            // outside the view's range/crop — Revit offers no cheap way to separate that).
            var filterNameByHostId = new Dictionary<long, string>();
            try
            {
                var candidateIds = new List<ElementId>();
                foreach (var record in records)
                {
                    if (!record.IsLinked &&
                        !visibleHostIds.Contains(record.IdValue) &&
                        !hiddenHostIds.Contains(record.IdValue))
                    {
                        candidateIds.Add(new ElementId(record.IdValue));
                    }
                }

                if (candidateIds.Count > 0)
                {
                    CollectFilterHidden(doc, view, candidateIds, filterNameByHostId);
                    foreach (var id in filterNameByHostId.Keys)
                    {
                        hiddenHostIds.Add(id);
                    }
                }
            }
            catch
            {
                // Filter APIs unavailable for this view kind — filter hides stay undetected.
            }

            // Per-element link truth (Revit 2024+): a link-aware view collector per visible
            // link instance says exactly which linked elements the view draws — this is how
            // right-click hides INSIDE links become detectable.
            var visibleLinkedIdsByInstance = new Dictionary<long, HashSet<long>>();
            foreach (var linkId in records
                         .Where(r => r.IsLinked && r.LinkInstanceIdValue.HasValue)
                         .Select(r => r.LinkInstanceIdValue!.Value)
                         .Distinct())
            {
                if (hiddenLinks.Contains(linkId))
                {
                    continue;
                }

                try
                {
                    var visibleInLink = new HashSet<long>();
                    foreach (var id in new FilteredElementCollector(doc, view.Id, new ElementId(linkId))
                                 .WhereElementIsNotElementType()
                                 .ToElementIds())
                    {
                        visibleInLink.Add(id.Value);
                    }

                    visibleLinkedIdsByInstance[linkId] = visibleInLink;
                }
                catch
                {
                    // View kind rejects link collection — linked truth unavailable, classify
                    // falls back to link/category level for this instance.
                }
            }

            // Categories that demonstrably draw in this view (host + links) — the N/A guard
            // for linked records, where Revit offers no IsHidden to disambiguate.
            var visibleCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record.Category == null)
                {
                    continue;
                }

                var isVisible = record.IsLinked
                    ? record.LinkInstanceIdValue is { } instanceId &&
                      visibleLinkedIdsByInstance.TryGetValue(instanceId, out var set) &&
                      set.Contains(record.IdValue)
                    : visibleHostIds.Contains(record.IdValue);
                if (isVisible)
                {
                    visibleCategoryNames.Add(record.Category);
                }
            }

            var temporaryIsolate = false;
            try
            {
                temporaryIsolate = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            }
            catch
            {
                // Leave false.
            }

            return new ViewVisibilitySnapshot(
                view.Id.Value, view.Name, temporaryIsolate,
                visibleHostIds, hiddenHostIds, elementHiddenHostIds, hiddenLinks, hiddenCategoryNames,
                filterNameByHostId, visibleLinkedIdsByInstance, visibleCategoryNames);
        }

        /// <summary>
        /// Marks candidates matched by any enabled, visibility-off view filter. Rule-based
        /// filters combine their category set with their element filter; selection-based
        /// filters contribute their explicit id list.
        /// </summary>
        private static void CollectFilterHidden(
            Document doc,
            View view,
            ICollection<ElementId> candidateIds,
            Dictionary<long, string> filterNameByHostId)
        {
            foreach (var filterId in view.GetFilters())
            {
                try
                {
                    var enabled = true;
                    try
                    {
                        enabled = view.GetIsFilterEnabled(filterId);
                    }
                    catch
                    {
                        // Older view kinds: treat as enabled.
                    }

                    if (!enabled || view.GetFilterVisibility(filterId))
                    {
                        continue;
                    }

                    var filterElement = doc.GetElement(filterId);
                    var filterName = filterElement?.Name ?? "view filter";

                    if (filterElement is SelectionFilterElement selection)
                    {
                        var selected = new HashSet<long>(selection.GetElementIds().Select(id => id.Value));
                        foreach (var id in candidateIds)
                        {
                            if (selected.Contains(id.Value) && !filterNameByHostId.ContainsKey(id.Value))
                            {
                                filterNameByHostId[id.Value] = filterName;
                            }
                        }

                        continue;
                    }

                    if (filterElement is not ParameterFilterElement parameterFilter)
                    {
                        continue;
                    }

                    ElementFilter combined = new ElementMulticategoryFilter(parameterFilter.GetCategories());
                    var ruleFilter = parameterFilter.GetElementFilter();
                    if (ruleFilter != null)
                    {
                        combined = new LogicalAndFilter(combined, ruleFilter);
                    }

                    foreach (var id in new FilteredElementCollector(doc, candidateIds)
                                 .WherePasses(combined)
                                 .ToElementIds())
                    {
                        if (!filterNameByHostId.ContainsKey(id.Value))
                        {
                            filterNameByHostId[id.Value] = filterName;
                        }
                    }
                }
                catch
                {
                    // A malformed filter never breaks the snapshot.
                }
            }
        }
    }
}
