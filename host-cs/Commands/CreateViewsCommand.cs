using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Logging;
using RevitSuite.Host.Services;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CreateViewsCommand : IExternalCommand
    {
        private const string PipeName = "RevitSuitePipe";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "CreateViewsCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var doc = uiDoc.Document;
                var payload = new Dictionary<string, object?>
                {
                    ["schemaVersion"] = "1.0.0",
                    ["levelName"] = "LEVEL 01",
                    ["viewType"] = "FloorPlan",
                    ["scale"] = 96
                };

                var client = new PipeClient(PipeName);
                var plan = client.Call<CreateViewsPlan>("create_views", payload, correlationId)
                           ?? new CreateViewsPlan();

                if (plan.Actions.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "Engine returned no actions.");
                    LogManager.Warn(correlationId, "No actions received from engine.");
                    return Result.Cancelled;
                }

                var viewFamilyTypes = CacheViewFamilyTypes(doc);
                var existingNames = new HashSet<string>(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .Select(v => v.Name),
                    StringComparer.OrdinalIgnoreCase);

                var createdCount = 0;

                using (var transaction = new Transaction(doc, "RevitSuite: Create Views"))
                {
                    transaction.Start();

                    foreach (var action in plan.Actions)
                    {
                        var detail = action.CreateView;
                        if (detail == null)
                        {
                            continue;
                        }

                        if (existingNames.Contains(detail.Name))
                        {
                            LogManager.Warn(correlationId, $"View '{detail.Name}' already exists. Skipping.");
                            continue;
                        }

                        var level = FindLevel(doc, detail.LevelName);
                        if (level == null)
                        {
                            throw new InvalidOperationException($"Level not found: {detail.LevelName}");
                        }

                        var viewFamilyType = ResolveViewFamilyType(viewFamilyTypes, detail.Type);
                        if (viewFamilyType == null)
                        {
                            throw new InvalidOperationException($"No ViewFamilyType found for {detail.Type}");
                        }

                        var view = ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
                        view.Name = detail.Name;
                        view.Scale = Math.Max(1, detail.Scale);

                        existingNames.Add(detail.Name);
                        createdCount++;
                    }

                    transaction.Commit();
                }

                var resultMessage = createdCount > 0
                    ? $"Created {createdCount} plan view(s)."
                    : "No new views were created (they may already exist).";

                TaskDialog.Show("RevitSuite", resultMessage);
                LogManager.Info(correlationId, resultMessage);
                return Result.Succeeded;
            }
            catch (EngineUnavailableException ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", ex.Message);
                LogManager.Warn(correlationId, "Engine unavailable: " + ex.Message);
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Create Views failed. See log for details.");
                LogManager.Error(correlationId, "CreateViewsCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static Dictionary<string, ViewFamilyType> CacheViewFamilyTypes(Document doc)
        {
            var result = new Dictionary<string, ViewFamilyType>(StringComparer.OrdinalIgnoreCase);
            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>();

            foreach (var vft in collector)
            {
                result[vft.ViewFamily.ToString()] = vft;
            }

            return result;
        }

        private static Level? FindLevel(Document doc, string levelName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(level => level.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        }

        private static ViewFamilyType? ResolveViewFamilyType(Dictionary<string, ViewFamilyType> cache, string type)
        {
            if (type.Equals("FloorPlan", StringComparison.OrdinalIgnoreCase) &&
                cache.TryGetValue(ViewFamily.FloorPlan.ToString(), out var floor))
            {
                return floor;
            }

            if (type.Equals("CeilingPlan", StringComparison.OrdinalIgnoreCase) &&
                cache.TryGetValue(ViewFamily.CeilingPlan.ToString(), out var ceiling))
            {
                return ceiling;
            }

            if (cache.TryGetValue(ViewFamily.FloorPlan.ToString(), out var defaultFloor))
            {
                return defaultFloor;
            }

            return null;
        }
    }

    public class CreateViewsPlan
    {
        public List<CreateViewsAction> Actions { get; set; } = new List<CreateViewsAction>();
    }

    public class CreateViewsAction
    {
        public CreateViewDetail? CreateView { get; set; }
    }

    public class CreateViewDetail
    {
        public string Name { get; set; } = string.Empty;
        public string LevelName { get; set; } = string.Empty;
        public string Type { get; set; } = "FloorPlan";
        public int Scale { get; set; } = 96;
    }
}
