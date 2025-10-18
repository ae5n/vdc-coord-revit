using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitSuite.Host.Logging;
using RevitSuite.Host.Services;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class LevelReportCommand : IExternalCommand
    {
        private const string PipeName = "RevitSuitePipe";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "LevelReportCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var dialog = new SaveFileDialog
                {
                    Title = "Export Level Report",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = "LevelReport.csv",
                    AddExtension = true,
                    DefaultExt = "csv",
                    OverwritePrompt = true
                };

                if (dialog.ShowDialog() != true)
                {
                    LogManager.Info(correlationId, "Level report cancelled by user.");
                    return Result.Cancelled;
                }

                var targetPath = dialog.FileName;
                var levelRecords = CollectLevelRecords(uiDoc.Document, includeLinkedModels: true);

                var payload = new Dictionary<string, object?>
                {
                    ["schemaVersion"] = "1.0.0",
                    ["includeLinkedModels"] = true,
                    ["precision"] = 2,
                    ["maxPreviewRows"] = 5,
                    ["targetPath"] = targetPath,
                    ["levels"] = levelRecords
                };

                var client = new PipeClient(PipeName);
                var response = client.Call<LevelReportResponse>("level_report", payload, correlationId)
                               ?? new LevelReportResponse();

                var rows = response.Rows;
                var writtenPath = response.Written ?? targetPath;
                var preview = response.Preview ?? new List<Dictionary<string, object?>>();

                LogManager.Info(correlationId,
                    $"Level report exported to '{writtenPath}' with {rows} row(s). Preview rows: {preview.Count}");

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
                TaskDialog.Show("RevitSuite", "Level report failed. See log for details.");
                LogManager.Error(correlationId, "LevelReportCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static List<Dictionary<string, object?>> CollectLevelRecords(Document doc, bool includeLinkedModels)
        {
            var result = new List<Dictionary<string, object?>>();

            AppendLevelsFromDocument(doc, "Host", result);

            if (!includeLinkedModels)
            {
                return result;
            }

            var linkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var instance in linkInstances)
            {
                var linkDoc = instance.GetLinkDocument();
                if (linkDoc == null)
                {
                    continue;
                }

                AppendLevelsFromDocument(linkDoc, "Link", result);
            }

            return result;
        }

        private static void AppendLevelsFromDocument(Document doc, string type, IList<Dictionary<string, object?>> sink)
        {
            var modelName = doc.Title;
            var modelId = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ThenBy(level => level.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var level in levels)
            {
                sink.Add(new Dictionary<string, object?>
                {
                    ["model"] = modelName,
                    ["modelId"] = modelId,
                    ["type"] = type,
                    ["level"] = level.Name,
                    ["elevationFt"] = level.Elevation,
                    ["levelId"] = level.Id.Value,
                    ["levelUniqueId"] = level.UniqueId
                });
            }
        }

    }

    public class LevelReportResponse
    {
        public string? Written { get; set; }
        public int Rows { get; set; }
        public List<Dictionary<string, object?>>? Preview { get; set; }
    }
}
