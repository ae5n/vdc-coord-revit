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
    public class LevelReportCommand : IExternalCommand
    {
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

                var config = LevelReportConfig.Load();
                var targetPath = dialog.FileName;
                var includeLinked = config.IncludeLinkedModels;
                var precision = config.Precision;
                var maxPreview = config.MaxPreviewRows;

                var levelRecords = CollectLevelRecords(uiDoc.Document, includeLinked);

                if (levelRecords.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No levels found to include in the report.");
                    LogManager.Warn(correlationId, "Level report found no levels to export.");
                    return Result.Cancelled;
                }

                var sortedRecords = levelRecords
                    .OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.ElevationFt)
                    .ThenBy(r => r.LevelName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                WriteCsv(targetPath, sortedRecords, precision);

                var previewCount = Math.Min(maxPreview, sortedRecords.Count);
                LogManager.Info(correlationId,
                    $"Level report exported to '{targetPath}' with {sortedRecords.Count} row(s). Preview rows: {previewCount}");

                TaskDialog.Show("RevitSuite",
                    $"Level report written to:\n{targetPath}\nRows: {sortedRecords.Count}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Level report failed. See log for details.");
                LogManager.Error(correlationId, "LevelReportCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static List<LevelRecord> CollectLevelRecords(Document doc, bool includeLinkedModels)
        {
            var result = new List<LevelRecord>();

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

        private static void AppendLevelsFromDocument(Document doc, string type, IList<LevelRecord> sink)
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
                sink.Add(new LevelRecord(
                    modelName,
                    modelId,
                    type,
                    level.Name,
                    level.Elevation,
                    level.Id.Value,
                    level.UniqueId));
            }
        }

        private static void WriteCsv(string path, IReadOnlyList<LevelRecord> records, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine("Model,Type,Level,Elevation_ft,Elevation_ft_in,LevelId,LevelUniqueId,ModelId");

            foreach (var record in records)
            {
                var elevationRounded = Math.Round(record.ElevationFt, precision);
                var elevationFeetInches = FormatFeetInches(record.ElevationFt, precision);

                WriteCsvLine(writer,
                    record.Model,
                    record.Type,
                    record.LevelName,
                    elevationRounded.ToString(CultureInfo.InvariantCulture),
                    elevationFeetInches,
                    record.LevelId.ToString(CultureInfo.InvariantCulture),
                    record.LevelUniqueId,
                    record.ModelId);
            }
        }

        private static string FormatFeetInches(double feetValue, int precision)
        {
            var totalInches = Math.Abs(feetValue) * 12.0;
            var feet = (int)(totalInches / 12.0);
            var inches = totalInches - (feet * 12.0);
            var roundedInches = Math.Round(inches, precision);

            if (roundedInches >= 12.0)
            {
                feet += 1;
                roundedInches = 0.0;
            }

            var inchesText = precision == 0
                ? ((int)Math.Round(roundedInches)).ToString(CultureInfo.InvariantCulture)
                : roundedInches.ToString($"F{precision}", CultureInfo.InvariantCulture);

            var sign = feetValue < 0 ? "-" : string.Empty;
            return $"{sign}{feet}'-{inchesText}\"";
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

        private class LevelRecord
        {
            public LevelRecord(string model, string modelId, string type, string levelName, double elevationFt, long levelId, string levelUniqueId)
            {
                Model = model;
                ModelId = modelId;
                Type = type;
                LevelName = levelName;
                ElevationFt = elevationFt;
                LevelId = levelId;
                LevelUniqueId = levelUniqueId;
            }

            public string Model { get; }
            public string ModelId { get; }
            public string Type { get; }
            public string LevelName { get; }
            public double ElevationFt { get; }
            public long LevelId { get; }
            public string LevelUniqueId { get; }
        }
    }
}
