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
        private readonly HashSet<long> _hiddenLinkInstanceIds;
        private readonly HashSet<string> _hiddenCategoryNames;

        public ViewVisibilitySnapshot(
            long viewIdValue,
            string viewName,
            bool temporaryIsolateActive,
            HashSet<long> visibleHostIds,
            HashSet<long> hiddenHostIds,
            HashSet<long> hiddenLinkInstanceIds,
            HashSet<string> hiddenCategoryNames)
        {
            ViewIdValue = viewIdValue;
            ViewName = viewName;
            TemporaryIsolateActive = temporaryIsolateActive;
            _visibleHostIds = visibleHostIds;
            _hiddenHostIds = hiddenHostIds;
            _hiddenLinkInstanceIds = hiddenLinkInstanceIds;
            _hiddenCategoryNames = hiddenCategoryNames;
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

        /// <summary>true = visible, false = hidden, null = not applicable in this view.</summary>
        public bool? Classify(ElementRecord record)
        {
            if (record.IsLinked)
            {
                if (record.LinkInstanceIdValue is { } linkId && _hiddenLinkInstanceIds.Contains(linkId))
                {
                    return false;
                }

                if (record.Category != null && _hiddenCategoryNames.Contains(record.Category))
                {
                    return false;
                }

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
            var hiddenHostIds = new HashSet<long>();
            foreach (var record in records)
            {
                if (record.IsLinked || visibleHostIds.Contains(record.IdValue))
                {
                    continue;
                }

                if (record.Category != null && hiddenCategoryNames.Contains(record.Category))
                {
                    hiddenHostIds.Add(record.IdValue);
                    continue;
                }

                try
                {
                    var element = doc.GetElement(new ElementId(record.IdValue));
                    if (element != null && element.IsHidden(view))
                    {
                        hiddenHostIds.Add(record.IdValue);
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
                visibleHostIds, hiddenHostIds, hiddenLinks, hiddenCategoryNames);
        }
    }
}
