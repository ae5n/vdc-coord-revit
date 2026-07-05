using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitSuite.Host.Explorer
{
    /// <summary>Inventory of views, sheets, and schedules with placement info. API context required.</summary>
    internal static class NavigateService
    {
        public sealed record NavigateInventory(
            IReadOnlyList<ViewRecord> Views,
            IReadOnlyList<ViewRecord> Sheets,
            IReadOnlyList<ViewRecord> Schedules);

        public static NavigateInventory Collect(Document doc)
        {
            // Map placed view id -> sheet numbers via viewports and schedule instances.
            var placements = new Dictionary<long, List<string>>();

            foreach (var viewport in new FilteredElementCollector(doc)
                         .OfClass(typeof(Viewport))
                         .Cast<Viewport>())
            {
                AddPlacement(placements, viewport.ViewId.Value, SheetNumberOf(doc, viewport.SheetId));
            }

            foreach (var scheduleInstance in new FilteredElementCollector(doc)
                         .OfClass(typeof(ScheduleSheetInstance))
                         .Cast<ScheduleSheetInstance>())
            {
                AddPlacement(placements, scheduleInstance.ScheduleId.Value,
                    SheetNumberOf(doc, scheduleInstance.OwnerViewId));
            }

            var views = new List<ViewRecord>();
            var sheets = new List<ViewRecord>();
            var schedules = new List<ViewRecord>();

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            var nameCounts = allViews
                .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var view in allViews)
            {
                if (view.ViewType == ViewType.Internal || view.ViewType == ViewType.ProjectBrowser ||
                    view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Undefined)
                {
                    continue;
                }

                var record = CreateRecord(doc, view, placements, nameCounts);

                if (view is ViewSheet)
                {
                    sheets.Add(record);
                }
                else if (view is ViewSchedule schedule)
                {
                    if (!schedule.IsTitleblockRevisionSchedule)
                    {
                        schedules.Add(record);
                    }
                }
                else
                {
                    views.Add(record);
                }
            }

            return new NavigateInventory(
                Sort(views),
                sheets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Sort(schedules));
        }

        private static ViewRecord CreateRecord(
            Document doc,
            View view,
            IReadOnlyDictionary<long, List<string>> placements,
            IReadOnlyDictionary<string, int> nameCounts)
        {
            placements.TryGetValue(view.Id.Value, out var sheetNumbers);

            string? templateName = null;
            try
            {
                if (view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    templateName = doc.GetElement(view.ViewTemplateId)?.Name;
                }
            }
            catch
            {
                // Some view kinds do not support templates.
            }

            string? levelName = null;
            try
            {
                levelName = view.GenLevel?.Name;
            }
            catch
            {
                // Not a plan view.
            }

            var name = view is ViewSheet sheet ? $"{sheet.SheetNumber} - {sheet.Name}" : view.Name;

            return new ViewRecord(
                IdValue: view.Id.Value,
                Name: name,
                ViewKind: view.ViewType.ToString(),
                IsTemplate: view.IsTemplate,
                IsPlacedOnSheet: view is ViewSheet || (sheetNumbers?.Count ?? 0) > 0,
                SheetNumbers: (IReadOnlyList<string>?)sheetNumbers ?? Array.Empty<string>(),
                ViewTemplateName: templateName,
                LevelName: levelName,
                HasDuplicateName: nameCounts.TryGetValue(view.Name, out var count) && count > 1);
        }

        private static void AddPlacement(Dictionary<long, List<string>> placements, long viewIdValue, string? sheetNumber)
        {
            if (sheetNumber == null)
            {
                return;
            }

            if (!placements.TryGetValue(viewIdValue, out var list))
            {
                placements[viewIdValue] = list = new List<string>();
            }

            list.Add(sheetNumber);
        }

        private static string? SheetNumberOf(Document doc, ElementId sheetId)
        {
            if (sheetId == ElementId.InvalidElementId)
            {
                return null;
            }

            return (doc.GetElement(sheetId) as ViewSheet)?.SheetNumber;
        }

        private static IReadOnlyList<ViewRecord> Sort(List<ViewRecord> records) =>
            records
                .OrderBy(r => r.ViewKind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
