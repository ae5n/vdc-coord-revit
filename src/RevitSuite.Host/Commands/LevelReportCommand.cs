using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;
using RevitSuite.Host.UI;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class LevelReportCommand : IExternalCommand
    {
        internal sealed class LevelReportData
        {
            public string CsvContent { get; set; } = string.Empty;
            public string HtmlContent { get; set; } = string.Empty;
            public int RowCount { get; set; }
        }

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
                var config = LevelReportConfig.Load();
                var previewPath = BuildPreviewOutputPath(document);
                var runResult = RunCore(data.Application, previewPath, config.IncludeLinkedModels, config.Precision, config.MaxPreviewRows);

                if (runResult == null)
                {
                    TaskDialog.Show("RevitSuite", "No levels found to include in the report.");
                    LogManager.Warn(correlationId, "Level report found no levels to export.");
                    return Result.Cancelled;
                }

                var (targetPath, reportPath, rowCount) = runResult.Value;
                LogManager.Info(correlationId, $"Level report preview generated at '{targetPath}' with {rowCount} row(s).");

                ReportPreviewHost.Show(data.Application, new ReportPreviewModel
                {
                    Title = "Level Report",
                    Summary = $"Previewing {rowCount} level row(s). Export only if you want to keep the files.",
                    CsvPreviewPath = targetPath,
                    HtmlPreviewPath = reportPath,
                    SaveDialogTitle = "Export Level Report",
                    DefaultExportFileName = BuildDefaultFileName(document),
                    ExportAction = exportPath => ExportReportBundle(exportPath, targetPath, reportPath)
                });

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

        /// <summary>
        /// Executes the core level-report logic without any UI dialogs.
        /// Returns null if no levels were found; throws on error.
        /// </summary>
        internal static (string outputPath, string reportPath, int rowCount)? RunCore(
            UIApplication app,
            string? outputPath,
            bool includeLinkedModels,
            int precision,
            int maxPreviewRows)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            var document = (app.ActiveUIDocument
                ?? throw new InvalidOperationException("No active document.")).Document;

            var targetPath = string.IsNullOrWhiteSpace(outputPath)
                ? BuildAutoOutputPath(document, "Levels")
                : outputPath;

            var levelRecords = CollectLevelRecords(document, includeLinkedModels);
            if (levelRecords.Count == 0)
                return null;

            var pivotTable = BuildPivotTable(levelRecords);
            var reportData = BuildReportData(pivotTable, precision);
            WriteCsvFile(targetPath, reportData.CsvContent);
            var reportPath = Path.ChangeExtension(targetPath, ".html");
            WriteTextFile(reportPath, reportData.HtmlContent);

            var previewCount = Math.Min(maxPreviewRows, pivotTable.Rows.Count);
            LogManager.Info(correlationId,
                $"Level report exported to '{targetPath}' with {pivotTable.Rows.Count} row(s) across {pivotTable.Models.Count} model column(s). Preview rows: {previewCount}. HTML view: '{reportPath}'.");

            return (targetPath, reportPath, pivotTable.Rows.Count);
        }

        internal static LevelReportData? BuildMcpReportData(
            UIApplication app,
            bool includeLinkedModels,
            int precision)
        {
            var document = (app.ActiveUIDocument
                ?? throw new InvalidOperationException("No active document.")).Document;

            var levelRecords = CollectLevelRecords(document, includeLinkedModels);
            if (levelRecords.Count == 0)
            {
                return null;
            }

            var pivotTable = BuildPivotTable(levelRecords);
            return BuildReportData(pivotTable, precision);
        }

        private static string BuildAutoOutputPath(Document document, string suffix)
        {
            var baseName = string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title
                : Path.GetFileNameWithoutExtension(document.PathName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Model";
            var sanitized = SanitizeFileName(baseName);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitSuite");
            return Path.Combine(dir, $"{sanitized}_{suffix}_{timestamp}.csv");
        }

        private static string BuildPreviewOutputPath(Document document)
        {
            var previewDirectory = Path.Combine(Path.GetTempPath(), "RevitSuite", "Reports");
            Directory.CreateDirectory(previewDirectory);
            return Path.Combine(previewDirectory, $"{Guid.NewGuid():N}_{BuildDefaultFileName(document)}");
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

        private static LevelReportData BuildReportData(PivotTable table, int precision)
        {
            return new LevelReportData
            {
                CsvContent = BuildPivotCsvContent(table, precision),
                HtmlContent = BuildHtmlReportContent(table, precision),
                RowCount = table.Rows.Count
            };
        }

        private static string BuildPivotCsvContent(PivotTable table, int precision)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
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

            return writer.ToString();
        }

        private static void WritePivotCsv(string path, PivotTable table, int precision)
        {
            WriteCsvFile(path, BuildPivotCsvContent(table, precision));
        }

        private static string BuildHtmlReportContent(PivotTable table, int precision)
        {
            var hostModel = table.Models.FirstOrDefault(model =>
                string.Equals(model.Type, "Host", StringComparison.OrdinalIgnoreCase)) ?? table.Models.FirstOrDefault();

            var builder = new StringBuilder();
            builder.AppendLine("<!DOCTYPE html>");
            builder.AppendLine("<html lang=\"en\">");
            builder.AppendLine("<head>");
            builder.AppendLine("<meta charset=\"utf-8\" />");
            builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            builder.AppendLine("<title>Level Report</title>");
            builder.AppendLine("<style>");
            builder.AppendLine("body { font-family: \"Segoe UI\", Arial, sans-serif; margin: 32px; color: #1f2933; background-color: #f5f7fa; }");
            builder.AppendLine("h1 { font-size: 1.75rem; margin-bottom: 0.75rem; }");
            builder.AppendLine("p { margin: 0 0 1.25rem 0; color: #52606d; }");
            builder.AppendLine("table { border-collapse: collapse; width: 100%; background: #ffffff; box-shadow: 0 10px 30px rgba(15, 23, 42, 0.08); border-radius: 12px; overflow: hidden; }");
            builder.AppendLine("thead th { background: #0f172a; color: #f8fafc; font-weight: 600; padding: 6px 9px; text-align: left; font-size: 12px; line-height: 1.3; }");
            builder.AppendLine("tbody th { background: #e2e8f0; font-weight: 600; padding: 6px 9px; text-align: left; color: #111827; width: 190px; font-size: 12px; line-height: 1.3; }");
            builder.AppendLine("tbody td { padding: 6px 9px; border-bottom: 1px solid #e2e8f0; font-size: 12px; line-height: 1.3; }");
            builder.AppendLine("tbody tr:nth-child(even) td { background: #f8fafc; }");
            builder.AppendLine("td.host { background: #dbeafe; font-weight: 600; }");
            builder.AppendLine("td.match { background: #ecfdf5; }");
            builder.AppendLine("td.warn { background: #fef3c7; }");
            builder.AppendLine("td.alert { background: #fee2e2; }");
            builder.AppendLine("td.na { color: #94a3b8; font-style: italic; }");
            builder.AppendLine("td .delta { display: block; font-size: 10.5px; color: #475569; margin-top: 2px; }");
            builder.AppendLine("</style>");
            builder.AppendLine("</head>");
            builder.AppendLine("<body>");
            builder.AppendLine("<h1>Level Comparison</h1>");
            builder.AppendLine("<p>Elevations are shown in feet and inches. Delta highlights the difference from the host model.</p>");
            builder.AppendLine("<table>");
            builder.AppendLine("<thead>");
            builder.Append("<tr><th>Level</th>");
            foreach (var model in table.Models)
            {
                builder.Append("<th>")
                    .Append(HtmlEncode(model.Model))
                    .Append("</th>");
            }

            builder.AppendLine("</tr>");
            builder.AppendLine("</thead>");
            builder.AppendLine("<tbody>");

            foreach (var row in table.Rows)
            {
                builder.Append("<tr><th>")
                    .Append(HtmlEncode(row.LevelName))
                    .Append("</th>");

                LevelRecord hostRecord = null;
                var hostHasValue = hostModel != null && row.Records.TryGetValue(hostModel.ModelId, out hostRecord);
                var hostElevation = hostHasValue && hostRecord != null ? hostRecord.ElevationFt : double.NaN;

                foreach (var model in table.Models)
                {
                    var hasValue = row.Records.TryGetValue(model.ModelId, out var record);
                    var isHost = hostModel != null && string.Equals(model.ModelId, hostModel.ModelId, StringComparison.OrdinalIgnoreCase);
                    var cellClass = DetermineCellClass(isHost, hasValue, hostElevation, hasValue ? record.ElevationFt : double.NaN);
                    var formattedValue = hasValue ? FormatFeetInches(record.ElevationFt, precision) : string.Empty;
                    var encodedValue = string.IsNullOrEmpty(formattedValue) ? "--" : HtmlEncode(formattedValue).Replace(" ", "&nbsp;");

                    builder.Append("<td");
                    if (!string.IsNullOrEmpty(cellClass))
                    {
                        builder.Append(" class=\"").Append(cellClass).Append("\"");
                    }

                    builder.Append(">").Append(encodedValue);

                    if (!isHost && hasValue && hostHasValue)
                    {
                        var deltaText = FormatDelta(record.ElevationFt - hostElevation, precision);
                        if (!string.IsNullOrEmpty(deltaText))
                        {
                            builder.Append("<span class=\"delta\">").Append(deltaText).Append("</span>");
                        }
                    }

                    builder.Append("</td>");
                }

                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</tbody>");
            builder.AppendLine("</table>");
            builder.AppendLine("</body>");
            builder.AppendLine("</html>");

            return builder.ToString();
        }

        private static string WriteHtmlReport(string csvPath, PivotTable table, int precision)
        {
            var reportPath = Path.ChangeExtension(csvPath, ".html");
            WriteTextFile(reportPath, BuildHtmlReportContent(table, precision));
            return reportPath;
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

        private static string ExportReportBundle(string exportPath, string csvPath, string reportPath)
        {
            var directory = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(csvPath, exportPath, overwrite: true);
            var exportReportPath = Path.ChangeExtension(exportPath, ".html");
            if (File.Exists(reportPath))
            {
                File.Copy(reportPath, exportReportPath, overwrite: true);
            }

            return $"Level report exported to:\n{exportPath}\n\nHTML report exported to:\n{exportReportPath}";
        }

        private static void WriteTextFile(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void WriteCsvFile(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string DetermineCellClass(bool isHost, bool hasValue, double hostElevation, double value)
        {
            if (!hasValue)
            {
                return "na";
            }

            if (isHost || double.IsNaN(hostElevation))
            {
                return isHost ? "host" : string.Empty;
            }

            var difference = Math.Abs(value - hostElevation);
            if (difference <= 1e-4)
            {
                return "match";
            }

            return difference <= 0.25 ? "warn" : "alert";
        }

        private static string FormatDelta(double delta, int precision)
        {
            if (Math.Abs(delta) <= 1e-4)
            {
                return string.Empty;
            }

            var sign = delta >= 0 ? "+" : "-";
            var formatted = HtmlEncode(FormatFeetInches(Math.Abs(delta), precision)).Replace(" ", "&nbsp;");
            return $"&#916; {sign}{formatted}";
        }

        private static string HtmlEncode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
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
