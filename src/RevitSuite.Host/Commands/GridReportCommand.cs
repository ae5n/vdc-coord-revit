using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class GridReportCommand : IExternalCommand
    {
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

                var config = GridReportConfig.Load();
                var targetPath = dialog.FileName;

                var gridRecords = CollectGridRecords(uiDoc.Document, config.IncludeLinkedModels);
                if (gridRecords.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No grids found to include in the report.");
                    LogManager.Warn(correlationId, "Grid report found no grids to export.");
                    return Result.Cancelled;
                }

                var precision = config.Precision;
                var sortedRecords = gridRecords
                    .OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.GridName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                WriteCsv(targetPath, sortedRecords, precision);

                var previewCount = Math.Min(config.MaxPreviewRows, sortedRecords.Count);
                LogManager.Info(correlationId,
                    $"Grid report exported to '{targetPath}' with {sortedRecords.Count} row(s). Preview rows: {previewCount}");

                TaskDialog.Show("RevitSuite",
                    $"Grid report written to:\n{targetPath}\nRows: {sortedRecords.Count}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Grid report failed. See log for details.");
                LogManager.Error(correlationId, "GridReportCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static List<GridRecord> CollectGridRecords(Document doc, bool includeLinkedModels)
        {
            var result = new List<GridRecord>();

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

        private static void AppendGridsFromDocument(Document doc, string type, IList<GridRecord> sink)
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

                sink.Add(new GridRecord(
                    modelName,
                    modelId,
                    type,
                    grid.Name,
                    curveType,
                    length,
                    angleDeg,
                    radiusFt,
                    start?.X,
                    start?.Y,
                    start?.Z,
                    end?.X,
                    end?.Y,
                    end?.Z,
                    grid.Id.Value,
                    grid.UniqueId));
            }
        }

        private static void WriteCsv(string path, IReadOnlyList<GridRecord> records, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine("Model,Type,Grid,CurveType,Length_ft,Angle_deg,Radius_ft,Start_X_ft,Start_Y_ft,Start_Z_ft,End_X_ft,End_Y_ft,End_Z_ft,GridId,GridUniqueId,ModelId");

            foreach (var record in records)
            {
                WriteCsvLine(writer,
                    record.Model,
                    record.Type,
                    record.GridName,
                    record.CurveType,
                    FormatNullable(record.LengthFt, precision),
                    FormatNullable(record.AngleDeg, precision),
                    FormatNullable(record.RadiusFt, precision),
                    FormatNullable(record.StartX, precision),
                    FormatNullable(record.StartY, precision),
                    FormatNullable(record.StartZ, precision),
                    FormatNullable(record.EndX, precision),
                    FormatNullable(record.EndY, precision),
                    FormatNullable(record.EndZ, precision),
                    record.GridId.ToString(CultureInfo.InvariantCulture),
                    record.GridUniqueId,
                    record.ModelId);
            }
        }

        private static string FormatNullable(double? value, int precision)
        {
            if (!value.HasValue)
            {
                return string.Empty;
            }

            var rounded = Math.Round(value.Value, precision);
            return rounded.ToString(CultureInfo.InvariantCulture);
        }

        private static void WriteCsvLine(TextWriter writer, params string[] values)
        {
            var escaped = values.Select(EscapeCsvValue);
            writer.WriteLine(string.Join(",", escaped));
        }

        private static string EscapeCsvValue(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        private class GridRecord
        {
            public GridRecord(
                string model,
                string modelId,
                string type,
                string gridName,
                string curveType,
                double lengthFt,
                double? angleDeg,
                double? radiusFt,
                double? startX,
                double? startY,
                double? startZ,
                double? endX,
                double? endY,
                double? endZ,
                long gridId,
                string gridUniqueId)
            {
                Model = model;
                ModelId = modelId;
                Type = type;
                GridName = gridName;
                CurveType = curveType;
                LengthFt = lengthFt;
                AngleDeg = angleDeg;
                RadiusFt = radiusFt;
                StartX = startX;
                StartY = startY;
                StartZ = startZ;
                EndX = endX;
                EndY = endY;
                EndZ = endZ;
                GridId = gridId;
                GridUniqueId = gridUniqueId;
            }

            public string Model { get; }
            public string ModelId { get; }
            public string Type { get; }
            public string GridName { get; }
            public string CurveType { get; }
            public double LengthFt { get; }
            public double? AngleDeg { get; }
            public double? RadiusFt { get; }
            public double? StartX { get; }
            public double? StartY { get; }
            public double? StartZ { get; }
            public double? EndX { get; }
            public double? EndY { get; }
            public double? EndZ { get; }
            public long GridId { get; }
            public string GridUniqueId { get; }
        }
    }
}
