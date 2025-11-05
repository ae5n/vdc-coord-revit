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
    public class SharedCoordinatesReportCommand : IExternalCommand
    {
        private const int FractionDenominator = 256;

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "SharedCoordinatesReportCommand started.");

            try
            {
                var uiDocument = data.Application.ActiveUIDocument;
                if (uiDocument == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var document = uiDocument.Document;
                var defaultFileName = BuildDefaultFileName(document);

                var dialog = new SaveFileDialog
                {
                    Title = "Export Shared Coordinate Report",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = defaultFileName,
                    AddExtension = true,
                    DefaultExt = "csv",
                    OverwritePrompt = true
                };

                if (dialog.ShowDialog() != true)
                {
                    LogManager.Info(correlationId, "Shared coordinate report cancelled by user.");
                    return Result.Cancelled;
                }

                var config = SharedCoordinatesReportConfig.Load();
                var targetPath = dialog.FileName;

                var records = CollectRecords(document, config.IncludeLinkedModels);
                if (records.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No shared coordinate data found to include in the report.");
                    LogManager.Warn(correlationId, "Shared coordinate report found no data to export.");
                    return Result.Cancelled;
                }

                var previewCount = Math.Min(config.MaxPreviewRows, records.Count);
                var pivotTable = BuildPivotTable(records);
                WritePivotCsv(targetPath, pivotTable, config.Precision, config.AnglePrecision);
                var reportPath = WriteHtmlReport(targetPath, pivotTable, config.Precision, config.AnglePrecision);

                var preview = records
                    .Take(previewCount)
                    .Select(r =>
                        $"{r.ModelName}/{r.PointLabel}: EW={FormatFeetInches(r.SharedEastWest, FractionDenominator)}, " +
                        $"NS={FormatFeetInches(r.SharedNorthSouth, FractionDenominator)}, " +
                        $"Elev={FormatFeetInches(r.SharedElevation, FractionDenominator)}, " +
                        $"theta={FormatDouble(r.AngleToTrueNorth, config.AnglePrecision)}")
                    .ToList();

                var previewSummary = preview.Count == 0 ? "None" : string.Join(" | ", preview);
                var pointCount = pivotTable.Rows
                    .Where(row => !row.IsSeparator)
                    .Select(row => row.PointLabel)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                LogManager.Info(
                    correlationId,
                    $"Shared coordinate report exported to '{targetPath}' with {records.Count} record(s). HTML view: '{reportPath}'. Preview: {previewSummary}");

                TaskDialog.Show(
                    "RevitSuite",
                    $"Shared coordinate report written to:\n{targetPath}\nPivot view: {reportPath}\nRecords: {records.Count}\nPoints: {pointCount}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Shared coordinate report failed. See log for details.");
                LogManager.Error(correlationId, "SharedCoordinatesReportCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static List<SharedCoordinateRecord> CollectRecords(Document hostDocument, bool includeLinks)
        {
            var records = new List<SharedCoordinateRecord>();

            var hostLocation = GetProjectLocationData(hostDocument);
            AppendDocumentRecords(hostDocument, "Host", null, Transform.Identity, hostLocation, records);

            if (!includeLinks)
            {
                return OrderRecords(records);
            }

            var linkInstances = new FilteredElementCollector(hostDocument)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(instance => instance.GetLinkDocument() != null)
                .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var instance in linkInstances)
            {
                var linkDocument = instance.GetLinkDocument();
                if (linkDocument == null)
                {
                    continue;
                }

                var transform = instance.GetTotalTransform() ?? Transform.Identity;
                var linkLocation = GetProjectLocationData(linkDocument);
                AppendDocumentRecords(linkDocument, "Link", instance, transform, linkLocation, records);
            }

            return OrderRecords(records);
        }

        private static void AppendDocumentRecords(
            Document document,
            string modelType,
            RevitLinkInstance? instance,
            Transform hostTransform,
            ProjectLocationData location,
            ICollection<SharedCoordinateRecord> sink)
        {
            var modelName = document.Title;
            var modelIdentifier = string.IsNullOrWhiteSpace(document.PathName)
                ? document.Title
                : document.PathName;
            var instanceName = instance?.Name ?? string.Empty;
            var instanceId = instance?.Id.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

            var basePoints = GetBasePoints(document);
            foreach (var point in basePoints)
            {
                var pointLabel = ClassifyPoint(point);
                sink.Add(CreateRecord(
                    modelType,
                    modelName,
                    modelIdentifier,
                    instanceName,
                    instanceId,
                    point,
                    hostTransform,
                    location.ProjectToShared,
                    location.AngleToTrueNorthDegrees,
                    pointLabel));
            }
        }

        private static SharedCoordinateRecord CreateRecord(
            string modelType,
            string modelName,
            string modelIdentifier,
            string instanceName,
            string instanceId,
            BasePoint point,
            Transform hostTransform,
            Transform projectToShared,
            double angleToTrueNorthDegrees,
            string pointLabel)
        {
            var position = point.Position;
            var hostPosition = hostTransform.OfPoint(position);
            var sharedPosition = projectToShared.OfPoint(position);
            var basePointKind = point.IsShared ? "Survey" : "Project";
            var angleToTrueNorth = double.IsNaN(angleToTrueNorthDegrees)
                ? double.NaN
                : angleToTrueNorthDegrees;

            return new SharedCoordinateRecord(
                modelType,
                modelName,
                modelIdentifier,
                instanceName,
                instanceId,
                pointLabel,
                point.UniqueId,
                basePointKind,
                point.IsShared,
                sharedPosition.X,
                sharedPosition.Y,
                sharedPosition.Z,
                angleToTrueNorth,
                position.X,
                position.Y,
                position.Z,
                hostPosition.X,
                hostPosition.Y,
                hostPosition.Z);
        }

        private static IReadOnlyList<BasePoint> GetBasePoints(Document document)
        {
            var points = new FilteredElementCollector(document)
                .OfClass(typeof(BasePoint))
                .Cast<BasePoint>()
                .ToList();

            if (points.Count > 0)
            {
                return points;
            }

            var fallback = new List<BasePoint>();
            var projectBasePoint = BasePoint.GetProjectBasePoint(document);
            if (projectBasePoint != null)
            {
                fallback.Add(projectBasePoint);
            }

            var surveyPoint = BasePoint.GetSurveyPoint(document);
            if (surveyPoint != null)
            {
                fallback.Add(surveyPoint);
            }

            return fallback;
        }

        private static string ClassifyPoint(BasePoint point)
        {
            var baseLabel = point.IsShared ? "Survey Point" : "Project Base Point";
            var name = point.Name;

            if (!string.IsNullOrWhiteSpace(name))
            {
                if (name.IndexOf("project", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    baseLabel = "Project Base Point";
                }
                else if (name.IndexOf("survey", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    baseLabel = "Survey Point";
                }

                var trimmed = name.Trim();
                if (!trimmed.Equals("Project Base Point", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.Equals("Survey Point", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{baseLabel} ({trimmed})";
                }
            }

            return baseLabel;
        }

        private static ProjectLocationData GetProjectLocationData(Document document)
        {
            var projectLocation = document.ActiveProjectLocation;
            if (projectLocation == null)
            {
                return new ProjectLocationData(Transform.Identity, double.NaN);
            }

            var sharedToProject = projectLocation.GetTransform() ?? Transform.Identity;
            var projectToShared = sharedToProject.Inverse;
            var projectPosition = projectLocation.GetProjectPosition(XYZ.Zero);
            var angleDegrees = projectPosition == null
                ? double.NaN
                : projectPosition.Angle * 180.0 / Math.PI;

            return new ProjectLocationData(projectToShared, angleDegrees);
        }

        private static PivotTable BuildPivotTable(IReadOnlyCollection<SharedCoordinateRecord> records)
        {
            var modelDescriptors = new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                var modelKey = BuildModelKey(record);
                if (!modelDescriptors.ContainsKey(modelKey))
                {
                    modelDescriptors[modelKey] = CreateModelDescriptor(modelKey, record);
                }
            }

            var models = modelDescriptors.Values
                .OrderBy(descriptor =>
                    string.Equals(descriptor.ModelType, "Host", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(descriptor => descriptor.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new List<PivotRow>();
            var metricOrder = new[]
            {
                CoordinateMetric.EastWest,
                CoordinateMetric.NorthSouth,
                CoordinateMetric.Elevation,
                CoordinateMetric.AngleToTrueNorth
            };

            var groupedEntries = records
                .GroupBy(r => r.PointLabel, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Group = group, Sample = group.First() })
                .OrderBy(entry => GetCategoryOrder(entry.Sample.BasePointType))
                .ThenBy(entry => entry.Group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seenCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in groupedEntries)
            {
                var categoryLabel = GetCategoryLabel(entry.Sample.BasePointType);
                if (seenCategories.Add(categoryLabel))
                {
                    rows.Add(PivotRow.CreateSeparator(categoryLabel));
                }

                foreach (var metric in metricOrder)
                {
                    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var record in entry.Group)
                    {
                        var modelKey = BuildModelKey(record);
                        values[modelKey] = GetMetricValue(record, metric);
                    }

                    var label = BuildRowLabel(entry.Sample.PointLabel, metric);
                    rows.Add(PivotRow.CreateDataRow(entry.Sample.PointLabel, metric, label, values));
                }
            }

            return new PivotTable(models, rows);
        }

        private static string BuildModelKey(SharedCoordinateRecord record)
        {
            if (string.Equals(record.ModelType, "Host", StringComparison.OrdinalIgnoreCase))
            {
                return $"Host::{record.ModelIdentifier}";
            }

            var instanceKey = string.IsNullOrWhiteSpace(record.LinkInstanceId)
                ? record.LinkInstanceName
                : record.LinkInstanceId;

            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                instanceKey = record.ModelIdentifier;
            }

            return $"Link::{record.ModelIdentifier}::{instanceKey}";
        }

        private static ModelDescriptor CreateModelDescriptor(string modelKey, SharedCoordinateRecord record)
        {
            var label = string.Equals(record.ModelType, "Host", StringComparison.OrdinalIgnoreCase)
                ? $"Host | {record.ModelName}"
                : $"Link | {(!string.IsNullOrWhiteSpace(record.LinkInstanceName) ? record.LinkInstanceName : record.ModelName)}";

            return new ModelDescriptor(modelKey, label, record.ModelType);
        }

        private static string BuildRowLabel(string pointLabel, CoordinateMetric metric)
        {
            var metricLabel = metric switch
            {
                CoordinateMetric.EastWest => "East / West (ft)",
                CoordinateMetric.NorthSouth => "North / South (ft)",
                CoordinateMetric.Elevation => "Elevation (ft)",
                CoordinateMetric.AngleToTrueNorth => "Angle to True North (deg)",
                _ => metric.ToString()
            };

            return $"{pointLabel} - {metricLabel}";
        }

        private static string GetCategoryLabel(string basePointType)
        {
            if (string.Equals(basePointType, "Project", StringComparison.OrdinalIgnoreCase))
            {
                return "Project Base Points";
            }

            if (string.Equals(basePointType, "Survey", StringComparison.OrdinalIgnoreCase))
            {
                return "Survey Points";
            }

            return "Other Points";
        }

        private static int GetCategoryOrder(string basePointType)
        {
            if (string.Equals(basePointType, "Project", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(basePointType, "Survey", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private static double GetMetricValue(SharedCoordinateRecord record, CoordinateMetric metric)
        {
            return metric switch
            {
                CoordinateMetric.EastWest => record.SharedEastWest,
                CoordinateMetric.NorthSouth => record.SharedNorthSouth,
                CoordinateMetric.Elevation => record.SharedElevation,
                CoordinateMetric.AngleToTrueNorth => record.AngleToTrueNorth,
                _ => double.NaN
            };
        }

        private static string FormatMetricValue(
            double value,
            CoordinateMetric metric,
            int precision,
            int anglePrecision)
        {
            _ = precision;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return string.Empty;
            }

            if (metric == CoordinateMetric.AngleToTrueNorth)
            {
                return FormatDouble(value, anglePrecision);
            }

            _ = precision; // retained for future fractional control via schema precision
            var formatted = FormatFeetInches(value, FractionDenominator);
            return formatted;
        }

        private static void WritePivotCsv(
            string path,
            PivotTable table,
            int precision,
            int anglePrecision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.Append("Point Metric");
            foreach (var model in table.Models)
            {
                builder.Append(',').Append(Escape(model.Label));
            }
            builder.AppendLine();

            foreach (var row in table.Rows)
            {
                if (row.IsSeparator)
                {
                    builder.Append(Escape(row.DisplayLabel));
                    for (var i = 0; i < table.Models.Count; i++)
                    {
                        builder.Append(',');
                    }
                    builder.AppendLine();
                    continue;
                }

                var metric = row.Metric!.Value;
                builder.Append(Escape(row.DisplayLabel));
                foreach (var model in table.Models)
                {
                    var value = row.TryGetValue(model.Key, out var metricValue) ? metricValue : double.NaN;
                    var formatted = FormatMetricValue(value, metric, precision, anglePrecision);
                    var csvValue = string.IsNullOrEmpty(formatted) ? string.Empty : Escape(formatted);
                    builder.Append(',').Append(csvValue);
                }
                builder.AppendLine();
            }

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        }

        private static string WriteHtmlReport(
            string csvPath,
            PivotTable table,
            int precision,
            int anglePrecision)
        {
            var reportPath = Path.ChangeExtension(csvPath, ".html");
            var hostModel = table.Models.FirstOrDefault(m =>
                string.Equals(m.ModelType, "Host", StringComparison.OrdinalIgnoreCase)) ?? table.Models.FirstOrDefault();

            var builder = new StringBuilder();
            builder.AppendLine("<!DOCTYPE html>");
            builder.AppendLine("<html lang=\"en\">");
            builder.AppendLine("<head>");
            builder.AppendLine("<meta charset=\"utf-8\" />");
            builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            builder.AppendLine("<title>Shared Coordinate Report</title>");
            builder.AppendLine("<style>");
            builder.AppendLine("body { font-family: \"Segoe UI\", Arial, sans-serif; margin: 32px; color: #1f2933; background-color: #f5f7fa; }");
            builder.AppendLine("h1 { font-size: 1.75rem; margin-bottom: 0.75rem; }");
            builder.AppendLine("p { margin: 0 0 1.25rem 0; color: #52606d; }");
            builder.AppendLine("table { border-collapse: collapse; width: 100%; background: #ffffff; box-shadow: 0 10px 30px rgba(15, 23, 42, 0.08); border-radius: 12px; overflow: hidden; }");
            builder.AppendLine("thead th { background: #0f172a; color: #f8fafc; font-weight: 600; padding: 14px 16px; text-align: left; font-size: 0.95rem; }");
            builder.AppendLine("tbody th { background: #e2e8f0; font-weight: 600; padding: 12px 16px; text-align: left; color: #111827; width: 220px; }");
            builder.AppendLine("tbody td { padding: 12px 16px; border-bottom: 1px solid #e2e8f0; font-size: 0.95rem; }");
            builder.AppendLine("tbody tr:nth-child(even) td { background: #f8fafc; }");
            builder.AppendLine("td.host { background: #dbeafe; font-weight: 600; }");
            builder.AppendLine("td.match { background: #ecfdf5; }");
            builder.AppendLine("td.warn { background: #fef3c7; }");
            builder.AppendLine("td.alert { background: #fee2e2; }");
            builder.AppendLine("td.na { color: #94a3b8; font-style: italic; }");
            builder.AppendLine("td .delta { display: block; font-size: 0.78rem; color: #475569; margin-top: 4px; }");
            builder.AppendLine("tr.section th { background: #1e293b; color: #f8fafc; font-size: 0.85rem; letter-spacing: 0.08em; text-transform: uppercase; padding: 10px 16px; }");
            builder.AppendLine("</style>");
            builder.AppendLine("</head>");
            builder.AppendLine("<body>");
            builder.AppendLine("<h1>Shared Coordinate Comparison</h1>");
            builder.AppendLine("<p>Values are shown in shared coordinates (feet, degrees). Δ highlights the difference from the host model.</p>");
            builder.AppendLine("<table>");
            builder.AppendLine("<thead>");
            builder.Append("<tr><th>Point Metric</th>");
            foreach (var model in table.Models)
            {
                builder.Append("<th>")
                    .Append(HtmlEncode(model.Label))
                    .Append("</th>");
            }
            builder.AppendLine("</tr>");
            builder.AppendLine("</thead>");
            builder.AppendLine("<tbody>");

            foreach (var row in table.Rows)
            {
                if (row.IsSeparator)
                {
                    builder.Append("<tr class=\"section\"><th colspan=\"")
                        .Append(table.Models.Count + 1)
                        .Append("\">")
                        .Append(HtmlEncode(row.DisplayLabel))
                        .Append("</th></tr>");
                    continue;
                }

                builder.Append("<tr><th>")
                    .Append(HtmlEncode(row.DisplayLabel))
                    .Append("</th>");

                var metric = row.Metric!.Value;
                var hostValue = double.NaN;
                var hostHasValue = false;
                if (hostModel != null && row.TryGetValue(hostModel.Key, out hostValue) && !double.IsNaN(hostValue))
                {
                    hostHasValue = true;
                }

                foreach (var model in table.Models)
                {
                    var hasValue = row.TryGetValue(model.Key, out var value) && !double.IsNaN(value);
                    var formattedValue = hasValue
                        ? FormatMetricValue(value, metric, precision, anglePrecision)
                        : string.Empty;
                    var encodedValue = HtmlEncode(formattedValue);
                    if (string.IsNullOrEmpty(encodedValue))
                    {
                        encodedValue = "--";
                    }
                    else if (metric != CoordinateMetric.AngleToTrueNorth)
                    {
                        encodedValue = encodedValue.Replace(" ", "&nbsp;");
                    }

                    var isHost = hostModel != null && string.Equals(model.Key, hostModel.Key, StringComparison.OrdinalIgnoreCase);
                    var cellClass = DetermineCellClass(isHost, hostHasValue ? hostValue : double.NaN, value, metric, hasValue);
                    builder.Append("<td");
                    if (!string.IsNullOrEmpty(cellClass))
                    {
                        builder.Append(" class=\"").Append(cellClass).Append("\"");
                    }
                    builder.Append(">");
                    builder.Append(encodedValue);

                    if (!isHost && hasValue && hostHasValue)
                    {
                        var delta = value - hostValue;
                        var deltaText = FormatDelta(delta, metric, precision, anglePrecision);
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

            File.WriteAllText(reportPath, builder.ToString(), Encoding.UTF8);
            return reportPath;
        }

        private static string DetermineCellClass(
            bool isHost,
            double hostValue,
            double value,
            CoordinateMetric metric,
            bool hasValue)
        {
            if (!hasValue)
            {
                return "na";
            }

            if (isHost || double.IsNaN(hostValue) || double.IsNaN(value))
            {
                return isHost ? "host" : string.Empty;
            }

            var difference = Math.Abs(value - hostValue);
            var tolerance = metric == CoordinateMetric.AngleToTrueNorth ? 0.1 : 0.05;
            var warningThreshold = metric == CoordinateMetric.AngleToTrueNorth ? 1.0 : 1.0;

            if (difference <= tolerance)
            {
                return "match";
            }

            return difference <= warningThreshold ? "warn" : "alert";
        }

        private static string FormatDelta(double delta, CoordinateMetric metric, int precision, int anglePrecision)
        {
            _ = precision;
            if (double.IsNaN(delta))
            {
                return string.Empty;
            }

            var tolerance = metric == CoordinateMetric.AngleToTrueNorth ? 0.1 : 0.05;
            if (Math.Abs(delta) <= tolerance)
            {
                return string.Empty;
            }

            if (metric == CoordinateMetric.AngleToTrueNorth)
            {
                var formattedAngle = FormatDouble(Math.Abs(delta), anglePrecision);
                if (string.IsNullOrEmpty(formattedAngle))
                {
                    return string.Empty;
                }

                var angleSign = delta >= 0 ? "+" : "-";
                return $"&#916; {angleSign}{formattedAngle}";
            }

            var formattedFeet = FormatFeetInches(Math.Abs(delta), FractionDenominator, includeZeroInches: false);
            if (string.IsNullOrEmpty(formattedFeet))
            {
                return string.Empty;
            }

            var sign = delta >= 0 ? "+" : "-";
            var formattedFeetHtml = formattedFeet.Replace(" ", "&nbsp;");
            return $"&#916; {sign}{formattedFeetHtml}";
        }

        private static string FormatFeetInches(double value, int denominator, bool includeZeroInches = true)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return string.Empty;
            }

            if (!includeZeroInches && Math.Abs(value) < 1e-9)
            {
                return string.Empty;
            }

            var sign = value < 0 ? "-" : string.Empty;
            var absolute = Math.Abs(value);

            var feetPart = (long)Math.Floor(absolute);
            var fractionalFeet = absolute - feetPart;

            var totalInches = fractionalFeet * 12.0;
            var inchWhole = (int)Math.Floor(totalInches);
            var fractionalInch = totalInches - inchWhole;

            var numerator = (int)Math.Round(fractionalInch * denominator);
            if (numerator == denominator)
            {
                inchWhole += 1;
                numerator = 0;
            }

            if (inchWhole >= 12)
            {
                feetPart += inchWhole / 12;
                inchWhole %= 12;
            }

            var reducedDenominator = denominator;
            if (numerator != 0)
            {
                var gcd = GreatestCommonDivisor(Math.Abs(numerator), denominator);
                numerator /= gcd;
                reducedDenominator /= gcd;
            }

            var components = new List<string>();
            if (inchWhole > 0)
            {
                components.Add(inchWhole.ToString(CultureInfo.InvariantCulture));
            }

            if (numerator > 0)
            {
                components.Add($"{numerator}/{reducedDenominator}");
            }

            if (components.Count == 0)
            {
                if (!includeZeroInches && feetPart == 0)
                {
                    return string.Empty;
                }

                components.Add("0");
            }

            var inchText = string.Join(" ", components);
            var feetText = $"{sign}{feetPart.ToString(CultureInfo.InvariantCulture)}'";
            return $"{feetText}  {inchText}\"";
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0)
            {
                var temp = a % b;
                a = b;
                b = temp;
            }

            return a == 0 ? 1 : a;
        }

        private readonly struct ProjectLocationData
        {
            public ProjectLocationData(Transform projectToShared, double angleToTrueNorthDegrees)
            {
                ProjectToShared = projectToShared;
                AngleToTrueNorthDegrees = angleToTrueNorthDegrees;
            }

            public Transform ProjectToShared { get; }
            public double AngleToTrueNorthDegrees { get; }
        }

        private sealed class PivotTable
        {
            public PivotTable(IReadOnlyList<ModelDescriptor> models, IReadOnlyList<PivotRow> rows)
            {
                Models = models;
                Rows = rows;
            }

            public IReadOnlyList<ModelDescriptor> Models { get; }
            public IReadOnlyList<PivotRow> Rows { get; }
        }

        private sealed class ModelDescriptor
        {
            public ModelDescriptor(string key, string label, string modelType)
            {
                Key = key;
                Label = label;
                ModelType = modelType;
            }

            public string Key { get; }
            public string Label { get; }
            public string ModelType { get; }
        }

        private sealed class PivotRow
        {
            private readonly Dictionary<string, double> _values;

            private PivotRow(
                string displayLabel,
                bool isSeparator,
                string pointLabel,
                CoordinateMetric? metric,
                IDictionary<string, double>? values)
            {
                DisplayLabel = displayLabel;
                IsSeparator = isSeparator;
                PointLabel = pointLabel;
                Metric = metric;
                _values = values != null
                    ? new Dictionary<string, double>(values, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            public string DisplayLabel { get; }
            public bool IsSeparator { get; }
            public string PointLabel { get; }
            public CoordinateMetric? Metric { get; }

            public static PivotRow CreateDataRow(
                string pointLabel,
                CoordinateMetric metric,
                string displayLabel,
                IDictionary<string, double> values) =>
                new PivotRow(displayLabel, false, pointLabel, metric, values);

            public static PivotRow CreateSeparator(string displayLabel) =>
                new PivotRow(displayLabel, true, string.Empty, null, null);

            public bool TryGetValue(string modelKey, out double value)
            {
                if (IsSeparator)
                {
                    value = double.NaN;
                    return false;
                }

                return _values.TryGetValue(modelKey, out value);
            }
        }

        private enum CoordinateMetric
        {
            EastWest,
            NorthSouth,
            Elevation,
            AngleToTrueNorth
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

        private static List<SharedCoordinateRecord> OrderRecords(IEnumerable<SharedCoordinateRecord> records)
        {
            return records
                .OrderBy(record =>
                    string.Equals(record.ModelType, "Host", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(record => record.ModelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.LinkInstanceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.PointLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatDouble(double value, int precision)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return string.Empty;
            }

            return value.ToString($"F{precision}", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = value.Replace("\"", "\"\"");
            return $"\"{sanitized}\"";
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
            return $"{sanitized}_SharedCoordinates_{timestamp}.csv";
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

        private sealed class SharedCoordinateRecord
        {
            public SharedCoordinateRecord(
                string modelType,
                string modelName,
                string modelIdentifier,
                string linkInstanceName,
                string linkInstanceId,
                string pointLabel,
                string pointUniqueId,
                string basePointType,
                bool isShared,
                double sharedEastWest,
                double sharedNorthSouth,
                double sharedElevation,
                double angleToTrueNorth,
                double internalX,
                double internalY,
                double internalZ,
                double hostX,
                double hostY,
                double hostZ)
            {
                ModelType = modelType;
                ModelName = modelName;
                ModelIdentifier = modelIdentifier;
                LinkInstanceName = linkInstanceName;
                LinkInstanceId = linkInstanceId;
                PointLabel = pointLabel;
                PointUniqueId = pointUniqueId;
                BasePointType = basePointType;
                IsShared = isShared;
                SharedEastWest = sharedEastWest;
                SharedNorthSouth = sharedNorthSouth;
                SharedElevation = sharedElevation;
                AngleToTrueNorth = angleToTrueNorth;
                InternalX = internalX;
                InternalY = internalY;
                InternalZ = internalZ;
                HostX = hostX;
                HostY = hostY;
                HostZ = hostZ;
            }

            public string ModelType { get; }
            public string ModelName { get; }
            public string ModelIdentifier { get; }
            public string LinkInstanceName { get; }
            public string LinkInstanceId { get; }
            public string PointLabel { get; }
            public string PointUniqueId { get; }
            public string BasePointType { get; }
            public bool IsShared { get; }
            public double SharedEastWest { get; }
            public double SharedNorthSouth { get; }
            public double SharedElevation { get; }
            public double AngleToTrueNorth { get; }
            public double InternalX { get; }
            public double InternalY { get; }
            public double InternalZ { get; }
            public double HostX { get; }
            public double HostY { get; }
            public double HostZ { get; }
        }
    }
}
