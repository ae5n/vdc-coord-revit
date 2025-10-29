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

                var document = uiDoc.Document;
                var defaultFileName = BuildDefaultFileName(document);

                var dialog = new SaveFileDialog
                {
                    Title = "Export Level Report",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = defaultFileName,
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

                var levelRecords = CollectLevelRecords(document, includeLinked);

                if (levelRecords.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No levels found to include in the report.");
                    LogManager.Warn(correlationId, "Level report found no levels to export.");
                    return Result.Cancelled;
                }

                var pivotTable = BuildPivotTable(levelRecords);

                WritePivotCsv(targetPath, pivotTable, precision);

                var previewCount = Math.Min(maxPreview, pivotTable.Rows.Count);
                LogManager.Info(correlationId,
                    $"Level report exported to '{targetPath}' with {pivotTable.Rows.Count} row(s) across {pivotTable.Models.Count} model column(s). Preview rows: {previewCount}");

                TaskDialog.Show("RevitSuite",
                    $"Level report written to:\n{targetPath}\nRows: {pivotTable.Rows.Count}");

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

        private static PivotTable BuildPivotTable(IReadOnlyCollection<LevelRecord> records)
        {
            var models = records
                .GroupBy(r => r.ModelId, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var exemplar = group.First();
                    return new ModelDescriptor(group.Key, exemplar.Model, exemplar.Type);
                })
                .OrderBy(descriptor =>
                    string.Equals(descriptor.Type, "Host", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(descriptor => descriptor.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = records
                .GroupBy(r => r.LevelName, StringComparer.OrdinalIgnoreCase)
                .Select(CreatePivotRow)
                .ToList();

            rows.Sort((left, right) =>
            {
                var elevationCompare = left.SortElevation.CompareTo(right.SortElevation);
                if (elevationCompare != 0)
                {
                    return elevationCompare;
                }

                return string.Compare(left.LevelName, right.LevelName, StringComparison.OrdinalIgnoreCase);
            });

            return new PivotTable(models, rows);
        }

        private static LevelPivotRow CreatePivotRow(IGrouping<string, LevelRecord> group)
        {
            var recordsByModel = new Dictionary<string, LevelRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in group)
            {
                if (!recordsByModel.ContainsKey(record.ModelId))
                {
                    recordsByModel[record.ModelId] = record;
                }
            }

            var hostRecord = group.FirstOrDefault(r =>
                string.Equals(r.Type, "Host", StringComparison.OrdinalIgnoreCase));
            var sortElevation = hostRecord?.ElevationFt ?? recordsByModel.Values.Min(r => r.ElevationFt);

            return new LevelPivotRow(group.Key, sortElevation, recordsByModel);
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

        private static void WritePivotCsv(string path, PivotTable table, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var header = new List<string> { "Level" };
            header.AddRange(table.Models.Select(model => model.Model));
            WriteCsvLine(writer, header.ToArray());

            foreach (var row in table.Rows)
            {
                var values = new List<string> { row.LevelName };
                foreach (var model in table.Models)
                {
                    if (row.Records.TryGetValue(model.ModelId, out var record))
                    {
                        var elevationFeetInches = FormatFeetInches(record.ElevationFt, precision);
                        values.Add(elevationFeetInches);
                    }
                    else
                    {
                        values.Add(string.Empty);
                    }
                }

                WriteCsvLine(writer, values.ToArray());
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

        private static string BuildDefaultFileName(Document document)
        {
            var baseName = string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title
                : Path.GetFileNameWithoutExtension(document.PathName);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "Model";
            }

            var sanitized = SanitizeFileName(baseName);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            return $"{sanitized}_Levels_{timestamp}.csv";
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);

            foreach (var ch in value)
            {
                builder.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
            }

            return builder.Length == 0 ? "Model" : builder.ToString();
        }

        private sealed class PivotTable
        {
            public PivotTable(IReadOnlyList<ModelDescriptor> models, IReadOnlyList<LevelPivotRow> rows)
            {
                Models = models;
                Rows = rows;
            }

            public IReadOnlyList<ModelDescriptor> Models { get; }
            public IReadOnlyList<LevelPivotRow> Rows { get; }
        }

        private sealed class LevelPivotRow
        {
            public LevelPivotRow(string levelName, double sortElevation, IDictionary<string, LevelRecord> records)
            {
                LevelName = levelName;
                SortElevation = sortElevation;
                Records = new Dictionary<string, LevelRecord>(records, StringComparer.OrdinalIgnoreCase);
            }

            public string LevelName { get; }
            public double SortElevation { get; }
            public IReadOnlyDictionary<string, LevelRecord> Records { get; }
        }

        private sealed class ModelDescriptor
        {
            public ModelDescriptor(string modelId, string model, string type)
            {
                ModelId = modelId;
                Model = model;
                Type = type;
            }

            public string ModelId { get; }
            public string Model { get; }
            public string Type { get; }
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
