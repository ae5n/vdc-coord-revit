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
    public class GridReportCommand : IExternalCommand
    {
        private const string PipeName = "RevitSuitePipe";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "GridReportCommand started.");

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
                    Title = "Export Grid Report",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = "GridReport.csv",
                    AddExtension = true,
                    DefaultExt = "csv",
                    OverwritePrompt = true
                };

                if (dialog.ShowDialog() != true)
                {
                    LogManager.Info(correlationId, "Grid report cancelled by user.");
                    return Result.Cancelled;
                }

                var targetPath = dialog.FileName;
                var gridRecords = CollectGridRecords(uiDoc.Document, includeLinkedModels: true);

                var payload = new Dictionary<string, object?>
                {
                    ["schemaVersion"] = "1.0.0",
                    ["includeLinkedModels"] = true,
                    ["precision"] = 2,
                    ["maxPreviewRows"] = 5,
                    ["targetPath"] = targetPath,
                    ["grids"] = gridRecords
                };

                var client = new PipeClient(PipeName);
                var response = client.Call<GridReportResponse>("grid_report", payload, correlationId)
                               ?? new GridReportResponse();

                var rows = response.Rows;
                var writtenPath = response.Written ?? targetPath;

                LogManager.Info(correlationId,
                    $"Grid report exported to '{writtenPath}' with {rows} row(s). Preview rows: {response.Preview?.Count ?? 0}");

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
                TaskDialog.Show("RevitSuite", "Grid report failed. See log for details.");
                LogManager.Error(correlationId, "GridReportCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static List<Dictionary<string, object?>> CollectGridRecords(Document doc, bool includeLinkedModels)
        {
            var result = new List<Dictionary<string, object?>>();

            AppendGridsFromDocument(doc, "Host", result);

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

                AppendGridsFromDocument(linkDoc, "Link", result);
            }

            return result;
        }

        private static void AppendGridsFromDocument(Document doc, string type, IList<Dictionary<string, object?>> sink)
        {
            var modelName = doc.Title;
            var modelId = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .OrderBy(grid => grid.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var grid in grids)
            {
                var curve = grid.Curve;
                var curveType = curve?.GetType().Name ?? "Unknown";
                var length = curve?.Length ?? 0.0;

                double? angleDeg = null;
                double? radiusFt = null;

                if (curve is Line line)
                {
                    var direction = line.Direction.Normalize();
                    angleDeg = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
                }
                else if (curve is Arc arc)
                {
                    radiusFt = arc.Radius;
                }

                var start = curve?.GetEndPoint(0);
                var end = curve?.GetEndPoint(1);

                sink.Add(new Dictionary<string, object?>
                {
                    ["model"] = modelName,
                    ["modelId"] = modelId,
                    ["type"] = type,
                    ["name"] = grid.Name,
                    ["curveType"] = curveType,
                    ["lengthFt"] = length,
                    ["angleDeg"] = angleDeg,
                    ["radiusFt"] = radiusFt,
                    ["startX"] = start?.X,
                    ["startY"] = start?.Y,
                    ["startZ"] = start?.Z,
                    ["endX"] = end?.X,
                    ["endY"] = end?.Y,
                    ["endZ"] = end?.Z,
                    ["gridId"] = grid.Id.IntegerValue,
                    ["gridUniqueId"] = grid.UniqueId
                });
            }
        }
    }

    public class GridReportResponse
    {
        public string? Written { get; set; }
        public int Rows { get; set; }
        public List<Dictionary<string, object?>>? Preview { get; set; }
    }
}
