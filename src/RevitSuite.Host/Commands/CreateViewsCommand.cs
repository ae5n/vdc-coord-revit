using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Logging;
using RevitSuite.Host.Config;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CreateViewsCommand : IExternalCommand
    {
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
                var config = CreateViewsConfig.Load();

                var targetViewName = $"Plan - {config.LevelName}";
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

                    if (existingNames.Contains(targetViewName))
                    {
                        LogManager.Warn(correlationId, $"View '{targetViewName}' already exists. Skipping creation.");
                    }
                    else
                    {
                        var level = FindLevel(doc, config.LevelName);
                        if (level == null)
                        {
                            throw new InvalidOperationException($"Level not found: {config.LevelName}");
                        }

                        var viewFamilyType = ResolveViewFamilyType(viewFamilyTypes, config.ViewType);
                        if (viewFamilyType == null)
                        {
                            throw new InvalidOperationException($"No ViewFamilyType found for {config.ViewType}");
                        }

                        var view = ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
                        view.Name = targetViewName;
                        view.Scale = Math.Max(1, config.Scale);

                        existingNames.Add(targetViewName);
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
}
