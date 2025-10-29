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
        private const double DifferenceTolerance = 1e-4;
        private const double AngleDifferenceTolerance = 1e-3;

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

                var document = uiDoc.Document;
                var defaultFileName = BuildDefaultFileName(document);

                var dialog = new SaveFileDialog
                {
                    Title = "Export Grid Report",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = defaultFileName,
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

                var gridRecords = CollectGridRecords(document, config.IncludeLinkedModels);
                if (gridRecords.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No grids found to include in the report.");
                    LogManager.Warn(correlationId, "Grid report found no grids to export.");
                    return Result.Cancelled;
                }

                var precision = config.Precision;
                var pivotTable = BuildPivotTable(gridRecords);
                var discrepancyRecords = BuildDiscrepancyRecords(pivotTable);

                WritePivotCsv(targetPath, pivotTable, precision);
                var discrepancyPath = BuildDiscrepancyPath(targetPath);
                WriteDiscrepancyCsv(discrepancyPath, discrepancyRecords, precision);
                var reportPath = BuildReportPath(targetPath);
                WriteReportHtml(reportPath, pivotTable, discrepancyRecords, precision);

                var previewCount = Math.Min(config.MaxPreviewRows, pivotTable.Rows.Count);
                LogManager.Info(correlationId,
                    $"Grid report exported to '{targetPath}' with {pivotTable.Rows.Count} row(s) across {pivotTable.Models.Count} model column(s). Discrepancies: {discrepancyRecords.Count}. Report: {reportPath}. Preview rows: {previewCount}");

                var messageBuilder = new StringBuilder()
                    .AppendLine("Grid report written to:")
                    .AppendLine(targetPath)
                    .AppendLine($"Rows: {pivotTable.Rows.Count}")
                    .AppendLine()
                    .AppendLine("Discrepancy summary written to:")
                    .AppendLine(discrepancyPath)
                    .AppendLine($"Rows: {discrepancyRecords.Count}")
                    .AppendLine()
                    .AppendLine("Alignment report written to:")
                    .AppendLine(reportPath);

                TaskDialog.Show("RevitSuite", messageBuilder.ToString());

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

        private static PivotTable BuildPivotTable(IReadOnlyCollection<GridRecord> records)
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
                .GroupBy(r => r.GridName, StringComparer.OrdinalIgnoreCase)
                .Select(CreatePivotRow)
                .OrderBy(row => row.GridName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PivotTable(models, rows);
        }

        private static GridPivotRow CreatePivotRow(IGrouping<string, GridRecord> group)
        {
            var recordsByModel = new Dictionary<string, GridRecord>(StringComparer.OrdinalIgnoreCase);
            var curveTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in group)
            {
                if (!recordsByModel.ContainsKey(record.ModelId))
                {
                    recordsByModel[record.ModelId] = record;
                }

                if (!string.IsNullOrWhiteSpace(record.CurveType))
                {
                    curveTypes.Add(record.CurveType);
                }
            }

            var curveSummary = curveTypes.Count == 0 ? string.Empty : string.Join(" | ", curveTypes);
            return new GridPivotRow(group.Key, curveSummary, recordsByModel);
        }

        private static List<GridDiscrepancyRecord> BuildDiscrepancyRecords(PivotTable table)
        {
            var result = new List<GridDiscrepancyRecord>();

            foreach (var row in table.Rows)
            {
                var reference = SelectReferenceRecord(row);
                if (reference == null)
                {
                    continue;
                }

                foreach (var model in table.Models)
                {
                    if (!row.Records.TryGetValue(model.ModelId, out var record) ||
                        ReferenceEquals(reference, record))
                    {
                        continue;
                    }

                    var discrepancy = CreateDiscrepancyRecord(row.GridName, reference, record);
                    if (discrepancy.HasMeaningfulDifference)
                    {
                        result.Add(discrepancy);
                    }
                }
            }

            result.Sort((left, right) =>
            {
                var nameCompare = string.Compare(left.GridName, right.GridName, StringComparison.OrdinalIgnoreCase);
                if (nameCompare != 0)
                {
                    return nameCompare;
                }

                var modelCompare = string.Compare(left.ComparedModel, right.ComparedModel, StringComparison.OrdinalIgnoreCase);
                if (modelCompare != 0)
                {
                    return modelCompare;
                }

                return string.Compare(left.ReferenceModel, right.ReferenceModel, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        private static GridRecord? SelectReferenceRecord(GridPivotRow row)
        {
            var host = row.Records.Values.FirstOrDefault(r =>
                string.Equals(r.Type, "Host", StringComparison.OrdinalIgnoreCase));
            if (host != null)
            {
                return host;
            }

            return row.Records.Values
                .OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static GridDiscrepancyRecord CreateDiscrepancyRecord(string gridName, GridRecord reference, GridRecord compare)
        {
            var notes = new List<string>();

            var lengthDelta = Math.Abs(reference.LengthFt - compare.LengthFt);
            var angleDelta = CalculateAngleDifference(reference.AngleDeg, compare.AngleDeg, notes);
            var radiusDelta = CalculateNullableDifference(reference.RadiusFt, compare.RadiusFt, "radius", notes);
            var startOffset = CalculatePointOffset(
                reference.StartX, reference.StartY, reference.StartZ,
                compare.StartX, compare.StartY, compare.StartZ,
                "start", notes);
            var endOffset = CalculatePointOffset(
                reference.EndX, reference.EndY, reference.EndZ,
                compare.EndX, compare.EndY, compare.EndZ,
                "end", notes);

            var curveMismatch = !string.Equals(reference.CurveType, compare.CurveType, StringComparison.OrdinalIgnoreCase);
            if (curveMismatch)
            {
                notes.Add($"Curve type differs ({reference.CurveType} vs {compare.CurveType})");
            }

            var hasLengthDelta = lengthDelta > DifferenceTolerance;
            var hasRadiusDelta = radiusDelta.HasValue && radiusDelta.Value > DifferenceTolerance;
            var hasStartOffset = startOffset.HasValue && startOffset.Value > DifferenceTolerance;
            var hasEndOffset = endOffset.HasValue && endOffset.Value > DifferenceTolerance;
            var hasAngleDelta = angleDelta.HasValue && angleDelta.Value > AngleDifferenceTolerance;
            var hasMissingData = notes.Any(note => note.StartsWith("Missing", StringComparison.OrdinalIgnoreCase));

            return new GridDiscrepancyRecord(
                gridName,
                reference.Model,
                reference.Type,
                compare.Model,
                compare.Type,
                hasLengthDelta ? lengthDelta : (double?)null,
                angleDelta,
                hasRadiusDelta ? radiusDelta : (double?)null,
                hasStartOffset ? startOffset : (double?)null,
                hasEndOffset ? endOffset : (double?)null,
                curveMismatch,
                string.Join("; ", notes.Where(n => !string.IsNullOrWhiteSpace(n))),
                hasLengthDelta || hasRadiusDelta || hasStartOffset || hasEndOffset || hasAngleDelta || curveMismatch || hasMissingData);
        }

        private static double? CalculateAngleDifference(double? referenceAngle, double? compareAngle, ICollection<string> notes)
        {
            if (!referenceAngle.HasValue && !compareAngle.HasValue)
            {
                return null;
            }

            if (referenceAngle.HasValue && compareAngle.HasValue)
            {
                var difference = Math.Abs(referenceAngle.Value - compareAngle.Value) % 360.0;
                if (difference > 180.0)
                {
                    difference = 360.0 - difference;
                }

                return difference;
            }

            notes.Add(compareAngle.HasValue
                ? "Missing angle on reference model."
                : "Missing angle on compared model.");
            return null;
        }

        private static double? CalculateNullableDifference(double? referenceValue, double? compareValue, string label, ICollection<string> notes)
        {
            if (!referenceValue.HasValue && !compareValue.HasValue)
            {
                return null;
            }

            if (referenceValue.HasValue && compareValue.HasValue)
            {
                return Math.Abs(referenceValue.Value - compareValue.Value);
            }

            notes.Add(compareValue.HasValue
                ? $"Missing {label} on reference model."
                : $"Missing {label} on compared model.");
            return null;
        }

        private static double? CalculatePointOffset(
            double? refX, double? refY, double? refZ,
            double? cmpX, double? cmpY, double? cmpZ,
            string label,
            ICollection<string> notes)
        {
            var referenceHasPoint = refX.HasValue && refY.HasValue && refZ.HasValue;
            var compareHasPoint = cmpX.HasValue && cmpY.HasValue && cmpZ.HasValue;

            if (!referenceHasPoint && !compareHasPoint)
            {
                return null;
            }

            if (referenceHasPoint && compareHasPoint)
            {
                return CalculateDistance(refX!.Value, refY!.Value, refZ!.Value, cmpX!.Value, cmpY!.Value, cmpZ!.Value);
            }

            notes.Add(compareHasPoint
                ? $"Missing {label} point on reference model."
                : $"Missing {label} point on compared model.");
            return null;
        }

        private static double CalculateDistance(double x1, double y1, double z1, double x2, double y2, double z2)
        {
            var dx = x1 - x2;
            var dy = y1 - y2;
            var dz = z1 - z2;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static void WritePivotCsv(string path, PivotTable table, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var header = new List<string> { "Grid", "CurveTypes" };
            header.AddRange(table.Models.Select(model => model.Model));
            WriteCsvLine(writer, header.ToArray());

            foreach (var row in table.Rows)
            {
                var values = new List<string> { row.GridName, row.CurveTypes };
                foreach (var model in table.Models)
                {
                    if (row.Records.TryGetValue(model.ModelId, out var record))
                    {
                        values.Add(BuildModelSummary(record, precision));
                    }
                    else
                    {
                        values.Add(string.Empty);
                    }
                }

                WriteCsvLine(writer, values.ToArray());
            }
        }

        private static void WriteDiscrepancyCsv(string path, IReadOnlyList<GridDiscrepancyRecord> records, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine("Grid,ReferenceModel,ReferenceType,ComparedModel,ComparedType,LengthDelta_ft,AngleDelta_deg,RadiusDelta_ft,StartOffset_ft,EndOffset_ft,CurveTypeMismatch,Notes");

            foreach (var record in records)
            {
                WriteCsvLine(writer,
                    record.GridName,
                    record.ReferenceModel,
                    record.ReferenceType,
                    record.ComparedModel,
                    record.ComparedType,
                    FormatNullable(record.LengthDeltaFt, precision),
                    FormatNullable(record.AngleDeltaDeg, precision),
                    FormatNullable(record.RadiusDeltaFt, precision),
                    FormatNullable(record.StartOffsetFt, precision),
                    FormatNullable(record.EndOffsetFt, precision),
                    record.CurveTypeMismatch ? "Yes" : "No",
                    record.Notes);
            }
        }

        private static void WriteExecutiveSummary(StreamWriter writer, PivotTable table, Dictionary<string, GridAnalyticsRow> analytics, List<ModelQualityScore> modelQuality, IReadOnlyList<GridDiscrepancyRecord> discrepancies)
        {
            writer.WriteLine("<h2>Executive Summary</h2>");

            var totalGrids = analytics.Count;
            var criticalIssues = analytics.Values.Count(a => a.SeverityCategory == Severity.Critical);
            var bestModel = modelQuality.FirstOrDefault();
            var avgQuality = modelQuality.Average(m => m.QualityScore);

            writer.WriteLine("<div class=\"card-grid\">");

            writer.WriteLine("<div class=\"card blue\">");
            writer.WriteLine("<div class=\"card-value\">" + totalGrids + "</div>");
            writer.WriteLine("<div class=\"card-label\">Total Unique Grids</div>");
            writer.WriteLine("</div>");

            writer.WriteLine("<div class=\"card green\">");
            writer.WriteLine("<div class=\"card-value\">" + table.Models.Count + "</div>");
            writer.WriteLine("<div class=\"card-label\">Models Analyzed</div>");
            writer.WriteLine("</div>");

            if (bestModel != null)
            {
                writer.WriteLine("<div class=\"card orange\">");
                writer.WriteLine("<div class=\"card-value\" style=\"font-size: 20px;\">" + bestModel.RankBadge + " " + EscapeHtml(bestModel.ModelName) + "</div>");
                writer.WriteLine("<div class=\"card-label\">Recommended Source of Truth</div>");
                writer.WriteLine("<div style=\"margin-top: 8px; font-size: 13px;\">Quality Score: " + bestModel.QualityScore.ToString("F1") + "/100</div>");
                writer.WriteLine("</div>");
            }

            writer.WriteLine("<div class=\"card " + (criticalIssues > 0 ? "red" : "green") + "\">");
            writer.WriteLine("<div class=\"card-value\">" + criticalIssues + "</div>");
            writer.WriteLine("<div class=\"card-label\">Critical Alignment Issues</div>");
            writer.WriteLine("</div>");

            writer.WriteLine("</div>");
        }

        private static void WriteModelQualityRankings(StreamWriter writer, List<ModelQualityScore> modelQuality, int precision)
        {
            writer.WriteLine("<h2>🏆 Model Quality Rankings - Source of Truth Analysis</h2>");
            writer.WriteLine("<p style=\"color: #6c757d;\">Models ranked by geometric alignment to consensus. Higher scores indicate better agreement with the statistical average across all models.</p>");

            writer.WriteLine("<table>");
            writer.WriteLine("<thead>");
            writer.WriteLine("<tr>");
            writer.WriteLine("<th style=\"width: 60px;\">Rank</th>");
            writer.WriteLine("<th>Model Name</th>");
            writer.WriteLine("<th style=\"width: 100px;\">Type</th>");
            writer.WriteLine("<th style=\"width: 120px;\">Total Grids</th>");
            writer.WriteLine("<th style=\"width: 140px;\">Aligned Grids</th>");
            writer.WriteLine("<th style=\"width: 150px;\">Avg Deviation (ft)</th>");
            writer.WriteLine("<th style=\"width: 150px;\">Quality Score</th>");
            writer.WriteLine("</tr>");
            writer.WriteLine("</thead>");
            writer.WriteLine("<tbody>");

            foreach (var model in modelQuality)
            {
                var rowClass = model.Rank == 1 ? "sev-aligned" : "";
                writer.WriteLine("<tr class=\"" + rowClass + "\">");
                writer.Write("<td class=\"quality-badge\">" + model.RankBadge + " " + model.Rank + "</td>");
                writer.Write("<td><strong>" + EscapeHtml(model.ModelName) + "</strong></td>");
                writer.Write("<td><span class=\"pill " + (model.ModelType == "Host" ? "pill-host" : "pill-link") + "\">" + model.ModelType + "</span></td>");
                writer.Write("<td style=\"text-align: center;\">" + model.TotalGrids + "</td>");
                writer.Write("<td style=\"text-align: center;\">" + model.AlignedGrids + " (" + (model.TotalGrids > 0 ? (model.AlignedGrids * 100.0 / model.TotalGrids).ToString("F0") : "0") + "%)</td>");
                writer.Write("<td style=\"text-align: center;\">" + model.AverageDeviation.ToString("F3") + "</td>");
                writer.Write("<td style=\"text-align: center; font-size: 16px; font-weight: bold;\">" + model.QualityScore.ToString("F1") + " / 100</td>");
                writer.WriteLine("</tr>");
            }

            writer.WriteLine("</tbody>");
            writer.WriteLine("</table>");
        }

        private static void WriteGridConsensusMatrix(StreamWriter writer, PivotTable table, Dictionary<string, GridAnalyticsRow> analytics, int precision)
        {
            writer.WriteLine("<h2>🗺️ Grid Consensus Matrix</h2>");
            writer.WriteLine("<p style=\"color: #6c757d;\">Heatmap showing spatial deviation of grid <strong>midpoints</strong> from consensus position (in shared/project coordinates). Lower values = better alignment.</p>");
            writer.WriteLine("<p style=\"color: #6c757d; font-size: 13px;\"><strong>Note:</strong> Grid length differences are ignored - only spatial position matters. Grids can extend different amounts but still be aligned.</p>");

            // Legend
            writer.WriteLine("<div class=\"legend\">");
            writer.WriteLine("<div class=\"legend-item\"><div class=\"legend-box heat-perfect\"></div><span>Perfect (&lt;0.1 ft)</span></div>");
            writer.WriteLine("<div class=\"legend-item\"><div class=\"legend-box heat-good\"></div><span>Good (&lt;1 ft)</span></div>");
            writer.WriteLine("<div class=\"legend-item\"><div class=\"legend-box heat-fair\"></div><span>Fair (&lt;5 ft)</span></div>");
            writer.WriteLine("<div class=\"legend-item\"><div class=\"legend-box heat-poor\"></div><span>Poor (&lt;10 ft)</span></div>");
            writer.WriteLine("<div class=\"legend-item\"><div class=\"legend-box heat-critical\"></div><span>Critical (≥10 ft)</span></div>");
            writer.WriteLine("<div class=\"legend-item\"><div class=\"legend-box heat-missing\"></div><span>Missing</span></div>");
            writer.WriteLine("</div>");

            writer.WriteLine("<table>");
            writer.WriteLine("<thead>");
            writer.WriteLine("<tr>");
            writer.WriteLine("<th style=\"width: 120px;\">Grid</th>");
            writer.WriteLine("<th style=\"width: 100px;\">Models</th>");

            foreach (var model in table.Models)
            {
                writer.WriteLine("<th style=\"min-width: 120px;\">" + EscapeHtml(model.Model) + "</th>");
            }

            writer.WriteLine("</tr>");
            writer.WriteLine("</thead>");
            writer.WriteLine("<tbody>");

            foreach (var gridRow in analytics.Values.OrderBy(a => a.GridName, StringComparer.OrdinalIgnoreCase).Take(30))
            {
                writer.WriteLine("<tr>");
                writer.Write("<td><strong>" + EscapeHtml(gridRow.GridName) + "</strong></td>");
                writer.Write("<td style=\"text-align: center;\">" + gridRow.ModelsPresent + " / " + table.Models.Count + "</td>");

                foreach (var model in table.Models)
                {
                    if (gridRow.ModelDeviations.TryGetValue(model.Model, out var deviation))
                    {
                        var heatClass = GetHeatmapClass(deviation);
                        writer.Write("<td class=\"heatmap-cell " + heatClass + "\">" + deviation.ToString("F2") + " ft</td>");
                    }
                    else
                    {
                        writer.Write("<td class=\"heatmap-cell heat-missing\">—</td>");
                    }
                }

                writer.WriteLine("</tr>");
            }

            writer.WriteLine("</tbody>");
            writer.WriteLine("</table>");
            writer.WriteLine("<p style=\"color: #6c757d; font-size: 12px;\">Showing top 30 grids. Values represent deviation from consensus geometry.</p>");
        }

        private static string GetHeatmapClass(double deviation)
        {
            if (deviation < 0.1) return "heat-perfect";
            if (deviation < 1.0) return "heat-good";
            if (deviation < 5.0) return "heat-fair";
            if (deviation < 10.0) return "heat-poor";
            return "heat-critical";
        }

        private static void WriteDetailedGridAnalysis(StreamWriter writer, PivotTable table, Dictionary<string, GridAnalyticsRow> analytics, int precision)
        {
            writer.WriteLine("<h2>📋 Detailed Grid Analysis</h2>");
            writer.WriteLine("<p style=\"color: #6c757d;\">Grid-by-grid breakdown with consensus position (midpoint in shared coordinates), severity, and deviation metrics.</p>");

            writer.WriteLine("<table>");
            writer.WriteLine("<thead>");
            writer.WriteLine("<tr>");
            writer.WriteLine("<th>Grid</th>");
            writer.WriteLine("<th>Severity</th>");
            writer.WriteLine("<th>Models</th>");
            writer.WriteLine("<th>Consensus Midpoint (X, Y)</th>");
            writer.WriteLine("<th>Angle (°)</th>");
            writer.WriteLine("<th>Agreement</th>");
            writer.WriteLine("<th>Max Deviation (ft)</th>");
            writer.WriteLine("</tr>");
            writer.WriteLine("</thead>");
            writer.WriteLine("<tbody>");

            foreach (var row in analytics.Values.OrderByDescending(a => a.SeverityRank).ThenBy(a => a.GridName, StringComparer.OrdinalIgnoreCase).Take(50))
            {
                writer.WriteLine("<tr class=\"" + row.SeverityCssClass + "\">");
                writer.Write("<td><strong>" + EscapeHtml(row.GridName) + "</strong></td>");
                writer.Write("<td>" + row.SeverityText + "</td>");
                writer.Write("<td style=\"text-align: center;\">" + row.ModelsPresent + "</td>");

                // Show consensus midpoint coordinates
                if (row.Consensus?.ConsensusStart != null && row.Consensus?.ConsensusEnd != null)
                {
                    var midX = (row.Consensus.ConsensusStart.X + row.Consensus.ConsensusEnd.X) / 2.0;
                    var midY = (row.Consensus.ConsensusStart.Y + row.Consensus.ConsensusEnd.Y) / 2.0;
                    writer.Write("<td style=\"text-align: center; font-family: monospace; font-size: 11px;\">(" +
                        midX.ToString("F1") + ", " + midY.ToString("F1") + ")</td>");
                }
                else
                {
                    writer.Write("<td style=\"text-align: center;\">—</td>");
                }

                writer.Write("<td style=\"text-align: center;\">" + (row.Consensus?.ConsensusAngleDeg?.ToString("F1") ?? "—") + "</td>");
                writer.Write("<td style=\"text-align: center;\">" + (row.Consensus?.AgreementScore.ToString("F1") ?? "—") + "</td>");

                var maxDeviation = row.ModelDeviations.Values.Any() ? row.ModelDeviations.Values.Max() : 0;
                writer.Write("<td style=\"text-align: center;\">" + maxDeviation.ToString("F2") + "</td>");
                writer.WriteLine("</tr>");
            }

            writer.WriteLine("</tbody>");
            writer.WriteLine("</table>");
            writer.WriteLine("<p style=\"color: #6c757d; font-size: 12px;\">Showing top 50 grids by severity. Coordinates are in project/shared coordinate system.</p>");
        }

        private static void WriteConsensusSection(StreamWriter writer, PivotTable table, Dictionary<string, GridAnalyticsRow> analytics)
        {
            // Calculate grid consensus (grids by model count)
            var gridsByModelCount = analytics.Values
                .Select(row => new { row.GridName, row.ModelsPresent })
                .OrderByDescending(x => x.ModelsPresent)
                .ThenBy(x => x.GridName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Calculate model completeness (count of grids per model)
            var modelGridCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in table.Rows)
            {
                foreach (var model in table.Models)
                {
                    if (row.Records.ContainsKey(model.ModelId))
                    {
                        if (!modelGridCounts.ContainsKey(model.Model))
                        {
                            modelGridCounts[model.Model] = 0;
                        }
                        modelGridCounts[model.Model]++;
                    }
                }
            }

            var modelsByGridCount = modelGridCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var totalModels = table.Models.Count;
            var totalUniqueGrids = gridsByModelCount.Count;

            // Summary cards
            writer.WriteLine("<div style=\"display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; margin-bottom: 24px;\">");

            writer.WriteLine("<div style=\"background: #f0f9ff; border: 2px solid #0ea5e9; border-radius: 8px; padding: 16px;\">");
            writer.WriteLine("<div style=\"font-size: 24px; font-weight: bold; color: #0369a1;\">" + totalModels + "</div>");
            writer.WriteLine("<div style=\"color: #0c4a6e;\">Total Models Compared</div>");
            writer.WriteLine("</div>");

            writer.WriteLine("<div style=\"background: #f0fdf4; border: 2px solid #22c55e; border-radius: 8px; padding: 16px;\">");
            writer.WriteLine("<div style=\"font-size: 24px; font-weight: bold; color: #15803d;\">" + totalUniqueGrids + "</div>");
            writer.WriteLine("<div style=\"color: #14532d;\">Total Unique Grids Found</div>");
            writer.WriteLine("</div>");

            var mostCompleteModel = modelsByGridCount.FirstOrDefault();
            writer.WriteLine("<div style=\"background: #fef3c7; border: 2px solid #eab308; border-radius: 8px; padding: 16px;\">");
            if (mostCompleteModel.Key != null)
            {
                writer.WriteLine("<div style=\"font-size: 16px; font-weight: bold; color: #854d0e;\">" + EscapeHtml(mostCompleteModel.Key) + "</div>");
                writer.WriteLine("<div style=\"color: #713f12;\">Most Complete (" + mostCompleteModel.Value + " grids)</div>");
            }
            else
            {
                writer.WriteLine("<div style=\"font-size: 16px; font-weight: bold; color: #854d0e;\">N/A</div>");
                writer.WriteLine("<div style=\"color: #713f12;\">Most Complete Model</div>");
            }
            writer.WriteLine("</div>");

            writer.WriteLine("</div>");

            writer.WriteLine("<h2>📊 Which grids appear in the most models?</h2>");
            writer.WriteLine("<p style=\"color: #555; margin-bottom: 16px;\">Grids found in more models = higher confidence and agreement across disciplines.</p>");
            writer.WriteLine("<table style=\"margin-bottom: 32px;\">");
            writer.WriteLine("<thead>");
            writer.WriteLine("<tr>");
            writer.WriteLine("<th style=\"width: 120px;\">Grid Name</th>");
            writer.WriteLine("<th>Found In</th>");
            writer.WriteLine("<th style=\"width: 300px;\">Coverage</th>");
            writer.WriteLine("</tr>");
            writer.WriteLine("</thead>");
            writer.WriteLine("<tbody>");

            foreach (var item in gridsByModelCount.Take(20))
            {
                var coverage = totalModels > 0 ? (item.ModelsPresent * 100.0 / totalModels) : 0;
                var barWidth = (int)coverage;
                var barColor = coverage >= 80 ? "#22c55e" : coverage >= 50 ? "#eab308" : "#ef4444";

                writer.WriteLine("<tr>");
                writer.Write("<td><strong style=\"font-size: 16px;\">" + EscapeHtml(item.GridName) + "</strong></td>");
                writer.Write("<td>" + item.ModelsPresent + " of " + totalModels + " models</td>");
                writer.Write("<td>");
                writer.Write("<div style=\"background: #f3f4f6; border-radius: 4px; height: 24px; position: relative; overflow: hidden;\">");
                writer.Write("<div style=\"background: " + barColor + "; width: " + barWidth + "%; height: 100%;\"></div>");
                writer.Write("<div style=\"position: absolute; top: 0; left: 8px; line-height: 24px; font-weight: bold; color: " + (coverage >= 50 ? "#fff" : "#000") + ";\">" + coverage.ToString("F0") + "%</div>");
                writer.Write("</div>");
                writer.Write("</td>");
                writer.WriteLine("</tr>");
            }

            writer.WriteLine("</tbody>");
            writer.WriteLine("</table>");

            writer.WriteLine("<h2>🏗️ Which model has the most grids?</h2>");
            writer.WriteLine("<p style=\"color: #555; margin-bottom: 16px;\">Higher grid count typically indicates the most comprehensive or authoritative model.</p>");
            writer.WriteLine("<table style=\"margin-bottom: 32px;\">");
            writer.WriteLine("<thead>");
            writer.WriteLine("<tr>");
            writer.WriteLine("<th>Model Name</th>");
            writer.WriteLine("<th style=\"width: 120px;\">Grid Count</th>");
            writer.WriteLine("<th style=\"width: 300px;\">Completeness</th>");
            writer.WriteLine("</tr>");
            writer.WriteLine("</thead>");
            writer.WriteLine("<tbody>");

            var maxGrids = modelsByGridCount.FirstOrDefault().Value;
            foreach (var item in modelsByGridCount)
            {
                var completeness = maxGrids > 0 ? (item.Value * 100.0 / maxGrids) : 0;
                var barWidth = (int)completeness;
                var barColor = completeness >= 90 ? "#22c55e" : completeness >= 70 ? "#eab308" : "#ef4444";

                writer.WriteLine("<tr>");
                writer.Write("<td><strong>" + EscapeHtml(item.Key) + "</strong></td>");
                writer.Write("<td style=\"text-align: center; font-size: 18px; font-weight: bold;\">" + item.Value + "</td>");
                writer.Write("<td>");
                writer.Write("<div style=\"background: #f3f4f6; border-radius: 4px; height: 24px; position: relative; overflow: hidden;\">");
                writer.Write("<div style=\"background: " + barColor + "; width: " + barWidth + "%; height: 100%;\"></div>");
                writer.Write("<div style=\"position: absolute; top: 0; left: 8px; line-height: 24px; font-weight: bold; color: " + (completeness >= 50 ? "#fff" : "#000") + ";\">" + completeness.ToString("F0") + "%</div>");
                writer.Write("</div>");
                writer.Write("</td>");
                writer.WriteLine("</tr>");
            }

            writer.WriteLine("</tbody>");
            writer.WriteLine("</table>");
        }

        private static void WriteReportHtml(string path, PivotTable table, IReadOnlyList<GridDiscrepancyRecord> discrepancies, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var analytics = InitializeAnalytics(table);
            ApplyDiscrepancies(analytics, discrepancies);
            var modelQuality = CalculateModelQuality(table, analytics);
            var spacingIssues = AnalyzeGridSpacing(table, analytics);

            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine("<!DOCTYPE html>");
            writer.WriteLine("<html>");
            writer.WriteLine("<head>");
            writer.WriteLine("<meta charset=\"utf-8\">");
            writer.WriteLine("<title>Grid Report</title>");
            writer.WriteLine("<style>");
            writer.WriteLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 20px; font-size: 14px; }");
            writer.WriteLine("h1 { font-size: 20px; margin-bottom: 5px; }");
            writer.WriteLine("h2 { font-size: 16px; margin-top: 30px; margin-bottom: 10px; color: #c00; }");
            writer.WriteLine("table { border-collapse: collapse; width: 100%; margin-top: 10px; }");
            writer.WriteLine("th, td { border: 1px solid #ccc; padding: 6px 10px; text-align: left; }");
            writer.WriteLine("th { background: #f5f5f5; font-weight: 600; font-size: 13px; }");
            writer.WriteLine(".bad { background: #ffe6e6; }");
            writer.WriteLine(".num { text-align: right; font-family: 'Consolas', monospace; }");
            writer.WriteLine(".info { color: #666; font-size: 11px; margin-top: 5px; }");
            writer.WriteLine("</style>");
            writer.WriteLine("</head>");
            writer.WriteLine("<body>");

            writer.WriteLine("<h1>Grid Discrepancy Analysis</h1>");
            writer.WriteLine("<p class=\"info\">Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | Distance between parallel grid lines</p>");

            // Build comprehensive spacing data
            var spacingData = BuildGridSpacingData(table, analytics);

            writer.WriteLine("<h2>Grid Spacing by Model</h2>");
            writer.WriteLine("<p>Distance (ft-in) between consecutive grids. Red = differs from consensus by >0.01 ft.</p>");

            writer.WriteLine("<table>");
            writer.WriteLine("<tr>");
            writer.WriteLine("<th>Grid Pair</th>");
            writer.WriteLine("<th class=\"num\">Consensus (ft-in)</th>");

            foreach (var model in table.Models.OrderBy(m => m.Type == "Host" ? 0 : 1).ThenBy(m => m.Model))
            {
                writer.WriteLine("<th class=\"num\">" + EscapeHtml(model.Model) + "</th>");
            }

            writer.WriteLine("</tr>");

            foreach (var spacing in spacingData.OrderBy(s => s.GridA, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.GridB, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine("<tr>");
                writer.WriteLine("<td><strong>" + EscapeHtml(spacing.GridA) + " ↔ " + EscapeHtml(spacing.GridB) + "</strong></td>");
                writer.WriteLine("<td class=\"num\">" + FormatFeetInches(spacing.ConsensusSpacing) + "</td>");

                foreach (var model in table.Models.OrderBy(m => m.Type == "Host" ? 0 : 1).ThenBy(m => m.Model))
                {
                    if (spacing.ModelSpacings.TryGetValue(model.Model, out var modelSpacing))
                    {
                        var diff = Math.Abs(modelSpacing - spacing.ConsensusSpacing);
                        var cellClass = diff > 0.01 ? "num bad" : "num";
                        writer.WriteLine("<td class=\"" + cellClass + "\">" + FormatFeetInches(modelSpacing) + "</td>");
                    }
                    else
                    {
                        writer.WriteLine("<td class=\"num\" style=\"color: #ccc;\">—</td>");
                    }
                }

                writer.WriteLine("</tr>");
            }

            writer.WriteLine("</table>");

            writer.WriteLine("</body>");
            writer.WriteLine("</html>");
        }

        private static Dictionary<string, GridAnalyticsRow> InitializeAnalytics(PivotTable table)
        {
            var analytics = new Dictionary<string, GridAnalyticsRow>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in table.Rows)
            {
                var hostPresent = row.Records.Values.Any(r => string.Equals(r.Type, "Host", StringComparison.OrdinalIgnoreCase));
                var analyticsRow = new GridAnalyticsRow(row.GridName, hostPresent, row.Records.Count);

                // Calculate consensus geometry for this grid
                analyticsRow.Consensus = CalculateGridConsensus(row);

                analytics[row.GridName] = analyticsRow;
            }

            return analytics;
        }

        private static GridConsensus CalculateGridConsensus(GridPivotRow row)
        {
            var records = row.Records.Values.ToList();
            if (records.Count == 0)
            {
                return new GridConsensus { GridName = row.GridName };
            }

            // Calculate median/average values across all models
            var lengths = records.Select(r => r.LengthFt).ToList();
            var angles = records.Where(r => r.AngleDeg.HasValue).Select(r => r.AngleDeg!.Value).ToList();
            var radii = records.Where(r => r.RadiusFt.HasValue).Select(r => r.RadiusFt!.Value).ToList();

            var startPoints = records.Where(r => r.StartX.HasValue && r.StartY.HasValue && r.StartZ.HasValue)
                .Select(r => new XYZ(r.StartX!.Value, r.StartY!.Value, r.StartZ!.Value))
                .ToList();

            var endPoints = records.Where(r => r.EndX.HasValue && r.EndY.HasValue && r.EndZ.HasValue)
                .Select(r => new XYZ(r.EndX!.Value, r.EndY!.Value, r.EndZ!.Value))
                .ToList();

            // Use median for robust central tendency
            var consensusLength = Median(lengths);
            var consensusAngle = angles.Count > 0 ? (double?)Median(angles) : null;
            var consensusRadius = radii.Count > 0 ? (double?)Median(radii) : null;
            var consensusStart = startPoints.Count > 0 ? MedianPoint(startPoints) : null;
            var consensusEnd = endPoints.Count > 0 ? MedianPoint(endPoints) : null;

            // Most common curve type
            var curveTypes = records.GroupBy(r => r.CurveType).OrderByDescending(g => g.Count()).First();
            var consensusCurveType = curveTypes.Key;

            // Calculate agreement score based on spatial position variance, not length
            // Calculate variance in start and end point positions
            double positionVariance = 0;
            if (startPoints.Count > 1)
            {
                var startXVariance = Variance(startPoints.Select(p => p.X).ToList(), consensusStart?.X ?? 0);
                var startYVariance = Variance(startPoints.Select(p => p.Y).ToList(), consensusStart?.Y ?? 0);
                var endXVariance = Variance(endPoints.Select(p => p.X).ToList(), consensusEnd?.X ?? 0);
                var endYVariance = Variance(endPoints.Select(p => p.Y).ToList(), consensusEnd?.Y ?? 0);
                positionVariance = (startXVariance + startYVariance + endXVariance + endYVariance) / 4.0;
            }
            var agreementScore = 100.0 / (1.0 + Math.Sqrt(positionVariance));

            // Calculate midpoint
            XYZ? consensusMidpoint = null;
            if (consensusStart != null && consensusEnd != null)
            {
                consensusMidpoint = new XYZ(
                    (consensusStart.X + consensusEnd.X) / 2.0,
                    (consensusStart.Y + consensusEnd.Y) / 2.0,
                    (consensusStart.Z + consensusEnd.Z) / 2.0
                );
            }

            return new GridConsensus
            {
                GridName = row.GridName,
                ConsensusLengthFt = consensusLength,
                ConsensusAngleDeg = consensusAngle,
                ConsensusRadiusFt = consensusRadius,
                ConsensusStart = consensusStart,
                ConsensusEnd = consensusEnd,
                ConsensusMidpoint = consensusMidpoint,
                ConsensusCurveType = consensusCurveType,
                ParticipatingModels = records.Count,
                AgreementScore = agreementScore
            };
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }

        private static XYZ MedianPoint(List<XYZ> points)
        {
            return new XYZ(
                Median(points.Select(p => p.X).ToList()),
                Median(points.Select(p => p.Y).ToList()),
                Median(points.Select(p => p.Z).ToList())
            );
        }

        private static double Variance(List<double> values, double mean)
        {
            if (values.Count == 0) return 0;
            return values.Average(v => Math.Pow(v - mean, 2));
        }

        private static List<GridSpacingData> BuildGridSpacingData(PivotTable table, Dictionary<string, GridAnalyticsRow> analytics)
        {
            var result = new List<GridSpacingData>();

            // Group grids by direction
            var numericGrids = analytics.Values
                .Where(a => a.Consensus?.ConsensusMidpoint != null && char.IsDigit(a.GridName.FirstOrDefault()))
                .OrderBy(a => ParseGridNumber(a.GridName))
                .ToList();

            var alphaGrids = analytics.Values
                .Where(a => a.Consensus?.ConsensusMidpoint != null && char.IsLetter(a.GridName.FirstOrDefault()))
                .OrderBy(a => a.GridName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ProcessGridGroup(numericGrids, table, result);
            ProcessGridGroup(alphaGrids, table, result);

            return result;
        }

        private static void ProcessGridGroup(List<GridAnalyticsRow> grids, PivotTable table, List<GridSpacingData> result)
        {
            for (int i = 0; i < grids.Count - 1; i++)
            {
                var gridA = grids[i];
                var gridB = grids[i + 1];

                if (gridA.Consensus?.ConsensusStart == null || gridA.Consensus?.ConsensusEnd == null ||
                    gridB.Consensus?.ConsensusStart == null || gridB.Consensus?.ConsensusEnd == null)
                    continue;

                // Calculate consensus spacing
                var lineA_p1 = new XYZ(gridA.Consensus.ConsensusStart.X, gridA.Consensus.ConsensusStart.Y, gridA.Consensus.ConsensusStart.Z);
                var lineA_p2 = new XYZ(gridA.Consensus.ConsensusEnd.X, gridA.Consensus.ConsensusEnd.Y, gridA.Consensus.ConsensusEnd.Z);
                var lineB_p1 = new XYZ(gridB.Consensus.ConsensusStart.X, gridB.Consensus.ConsensusStart.Y, gridB.Consensus.ConsensusStart.Z);
                var lineB_p2 = new XYZ(gridB.Consensus.ConsensusEnd.X, gridB.Consensus.ConsensusEnd.Y, gridB.Consensus.ConsensusEnd.Z);

                var consensusSpacing = PerpendicularDistanceBetweenLines2D(lineA_p1, lineA_p2, lineB_p1, lineB_p2);

                if (consensusSpacing < 0.01) continue; // Skip non-parallel

                var spacingData = new GridSpacingData
                {
                    GridA = gridA.GridName,
                    GridB = gridB.GridName,
                    ConsensusSpacing = consensusSpacing
                };

                // Get spacing in each model
                foreach (var model in table.Models)
                {
                    var rowA = table.Rows.FirstOrDefault(r => r.GridName == gridA.GridName);
                    var rowB = table.Rows.FirstOrDefault(r => r.GridName == gridB.GridName);

                    if (rowA == null || rowB == null) continue;

                    rowA.Records.TryGetValue(model.ModelId, out var recordA);
                    rowB.Records.TryGetValue(model.ModelId, out var recordB);

                    if (recordA == null || recordB == null) continue;

                    if (!recordA.StartX.HasValue || !recordA.StartY.HasValue || !recordA.StartZ.HasValue ||
                        !recordA.EndX.HasValue || !recordA.EndY.HasValue || !recordA.EndZ.HasValue ||
                        !recordB.StartX.HasValue || !recordB.StartY.HasValue || !recordB.StartZ.HasValue ||
                        !recordB.EndX.HasValue || !recordB.EndY.HasValue || !recordB.EndZ.HasValue)
                        continue;

                    var recA_p1 = new XYZ(recordA.StartX.Value, recordA.StartY.Value, recordA.StartZ.Value);
                    var recA_p2 = new XYZ(recordA.EndX.Value, recordA.EndY.Value, recordA.EndZ.Value);
                    var recB_p1 = new XYZ(recordB.StartX.Value, recordB.StartY.Value, recordB.StartZ.Value);
                    var recB_p2 = new XYZ(recordB.EndX.Value, recordB.EndY.Value, recordB.EndZ.Value);

                    var modelSpacing = PerpendicularDistanceBetweenLines2D(recA_p1, recA_p2, recB_p1, recB_p2);

                    if (modelSpacing >= 0.01)
                    {
                        spacingData.ModelSpacings[model.Model] = modelSpacing;
                    }
                }

                result.Add(spacingData);
            }
        }

        private static List<GridSpacingIssue> AnalyzeGridSpacing(PivotTable table, Dictionary<string, GridAnalyticsRow> analytics)
        {
            var issues = new List<GridSpacingIssue>();

            // Group grids by direction (numeric vs alphabetic)
            var numericGrids = analytics.Values
                .Where(a => a.Consensus?.ConsensusMidpoint != null && char.IsDigit(a.GridName.FirstOrDefault()))
                .OrderBy(a => ParseGridNumber(a.GridName))
                .ToList();

            var alphaGrids = analytics.Values
                .Where(a => a.Consensus?.ConsensusMidpoint != null && char.IsLetter(a.GridName.FirstOrDefault()))
                .OrderBy(a => a.GridName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Analyze spacing for each group
            AnalyzeGridGroup(numericGrids, table, issues);
            AnalyzeGridGroup(alphaGrids, table, issues);

            return issues.Where(i => i.IsCritical).OrderByDescending(i => i.DeviationFt).ToList();
        }

        private static void AnalyzeGridGroup(List<GridAnalyticsRow> grids, PivotTable table, List<GridSpacingIssue> issues)
        {
            for (int i = 0; i < grids.Count - 1; i++)
            {
                var gridA = grids[i];
                var gridB = grids[i + 1];

                if (gridA.Consensus?.ConsensusMidpoint == null || gridB.Consensus?.ConsensusMidpoint == null)
                    continue;

                // Calculate consensus spacing (perpendicular distance between parallel lines)
                var lineA_p1 = new XYZ(gridA.Consensus.ConsensusStart.X, gridA.Consensus.ConsensusStart.Y, gridA.Consensus.ConsensusStart.Z);
                var lineA_p2 = new XYZ(gridA.Consensus.ConsensusEnd.X, gridA.Consensus.ConsensusEnd.Y, gridA.Consensus.ConsensusEnd.Z);
                var lineB_p1 = new XYZ(gridB.Consensus.ConsensusStart.X, gridB.Consensus.ConsensusStart.Y, gridB.Consensus.ConsensusStart.Z);
                var lineB_p2 = new XYZ(gridB.Consensus.ConsensusEnd.X, gridB.Consensus.ConsensusEnd.Y, gridB.Consensus.ConsensusEnd.Z);

                var consensusSpacing = PerpendicularDistanceBetweenLines2D(lineA_p1, lineA_p2, lineB_p1, lineB_p2);

                if (consensusSpacing < 0.01) continue; // Skip non-parallel grids

                // Check spacing in each model
                foreach (var model in table.Models)
                {
                    var rowA = table.Rows.FirstOrDefault(r => r.GridName == gridA.GridName);
                    var rowB = table.Rows.FirstOrDefault(r => r.GridName == gridB.GridName);

                    if (rowA == null || rowB == null) continue;

                    rowA.Records.TryGetValue(model.ModelId, out var recordA);
                    rowB.Records.TryGetValue(model.ModelId, out var recordB);

                    if (recordA == null || recordB == null) continue;

                    if (!recordA.StartX.HasValue || !recordA.StartY.HasValue || !recordA.StartZ.HasValue ||
                        !recordA.EndX.HasValue || !recordA.EndY.HasValue || !recordA.EndZ.HasValue ||
                        !recordB.StartX.HasValue || !recordB.StartY.HasValue || !recordB.StartZ.HasValue ||
                        !recordB.EndX.HasValue || !recordB.EndY.HasValue || !recordB.EndZ.HasValue)
                        continue;

                    var recA_p1 = new XYZ(recordA.StartX.Value, recordA.StartY.Value, recordA.StartZ.Value);
                    var recA_p2 = new XYZ(recordA.EndX.Value, recordA.EndY.Value, recordA.EndZ.Value);
                    var recB_p1 = new XYZ(recordB.StartX.Value, recordB.StartY.Value, recordB.StartZ.Value);
                    var recB_p2 = new XYZ(recordB.EndX.Value, recordB.EndY.Value, recordB.EndZ.Value);

                    var modelSpacing = PerpendicularDistanceBetweenLines2D(recA_p1, recA_p2, recB_p1, recB_p2);

                    if (modelSpacing < 0.01) continue; // Skip non-parallel grids

                    var deviation = Math.Abs(modelSpacing - consensusSpacing);

                    // Flag if deviation > 0.01 foot
                    if (deviation > 0.01)
                    {
                        issues.Add(new GridSpacingIssue
                        {
                            GridA = gridA.GridName,
                            GridB = gridB.GridName,
                            ModelName = model.Model,
                            SpacingFt = modelSpacing,
                            ConsensusSpacingFt = consensusSpacing,
                            DeviationFt = deviation,
                            IsCritical = deviation > 0.01
                        });
                    }
                }
            }
        }

        private static double ParseGridNumber(string gridName)
        {
            var numStr = new string(gridName.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return double.TryParse(numStr, out var num) ? num : 0;
        }

        private static List<GridMissingIssue> AnalyzeMissingGrids(PivotTable table, Dictionary<string, GridAnalyticsRow> analytics)
        {
            var issues = new List<GridMissingIssue>();

            foreach (var grid in analytics.Values)
            {
                var presentModels = new List<string>();
                var missingModels = new List<string>();

                foreach (var model in table.Models)
                {
                    var hasGrid = table.Rows.Any(r => r.GridName == grid.GridName && r.Records.ContainsKey(model.ModelId));
                    if (hasGrid)
                        presentModels.Add(model.Model);
                    else
                        missingModels.Add(model.Model);
                }

                // Only flag if grid is missing from some but not all models
                if (missingModels.Count > 0 && presentModels.Count > 0)
                {
                    issues.Add(new GridMissingIssue
                    {
                        GridName = grid.GridName,
                        PresentInModels = presentModels,
                        MissingFromModels = missingModels,
                        IsCritical = missingModels.Count >= presentModels.Count // Critical if missing from majority
                    });
                }
            }

            return issues.OrderByDescending(i => i.MissingFromModels.Count).ThenBy(i => i.GridName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ApplyDiscrepancies(IDictionary<string, GridAnalyticsRow> analytics, IReadOnlyList<GridDiscrepancyRecord> discrepancies)
        {
            foreach (var record in discrepancies)
            {
                if (!analytics.TryGetValue(record.GridName, out var row))
                {
                    row = new GridAnalyticsRow(record.GridName, false, 0);
                    analytics[record.GridName] = row;
                }

                row.ComparisonCount += 1;
                row.MaxLengthDeltaFt = UpdateMax(row.MaxLengthDeltaFt, record.LengthDeltaFt);
                row.MaxStartOffsetFt = UpdateMax(row.MaxStartOffsetFt, record.StartOffsetFt);
                row.MaxEndOffsetFt = UpdateMax(row.MaxEndOffsetFt, record.EndOffsetFt);
                row.MaxAngleDeltaDeg = UpdateMax(row.MaxAngleDeltaDeg, record.AngleDeltaDeg);
                row.HasCurveMismatch |= record.CurveTypeMismatch;
                row.HasMissingData |= !string.IsNullOrWhiteSpace(record.Notes) &&
                                      record.Notes.IndexOf("Missing", StringComparison.OrdinalIgnoreCase) >= 0;
                row.RecalculateSeverity();
            }

            foreach (var row in analytics.Values)
            {
                row.RecalculateSeverity();
            }
        }

        private static List<ModelQualityScore> CalculateModelQuality(PivotTable table, Dictionary<string, GridAnalyticsRow> analytics)
        {
            var modelScores = new Dictionary<string, ModelQualityScore>(StringComparer.OrdinalIgnoreCase);

            // Initialize scores for each model
            foreach (var model in table.Models)
            {
                modelScores[model.Model] = new ModelQualityScore
                {
                    ModelName = model.Model,
                    ModelType = model.Type
                };
            }

            // Calculate deviations from consensus for each grid in each model
            foreach (var analyticsRow in analytics.Values)
            {
                if (analyticsRow.Consensus == null) continue;

                foreach (var row in table.Rows.Where(r => r.GridName.Equals(analyticsRow.GridName, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var model in table.Models)
                    {
                        if (!row.Records.TryGetValue(model.ModelId, out var record))
                        {
                            continue;
                        }

                        var score = modelScores[model.Model];
                        score.TotalGrids++;

                        // Calculate deviation from consensus
                        var deviation = CalculateDeviationFromConsensus(record, analyticsRow.Consensus);
                        analyticsRow.ModelDeviations[model.Model] = deviation;

                        if (deviation < 1.0) // Within 1 foot tolerance
                        {
                            score.AlignedGrids++;
                        }

                        score.AverageDeviation = (score.AverageDeviation * (score.TotalGrids - 1) + deviation) / score.TotalGrids;
                    }
                }
            }

            // Calculate quality score (higher is better)
            foreach (var score in modelScores.Values)
            {
                if (score.TotalGrids == 0)
                {
                    score.QualityScore = 0;
                    continue;
                }

                var alignmentRatio = score.TotalGrids > 0 ? (double)score.AlignedGrids / score.TotalGrids : 0;
                var deviationPenalty = 1.0 / (1.0 + score.AverageDeviation);
                score.QualityScore = (alignmentRatio * 60.0) + (deviationPenalty * 40.0);
            }

            // Assign ranks
            var rankedScores = modelScores.Values.OrderByDescending(s => s.QualityScore).ToList();
            for (int i = 0; i < rankedScores.Count; i++)
            {
                rankedScores[i].Rank = i + 1;
            }

            return rankedScores;
        }

        private static double CalculateDeviationFromConsensus(GridRecord record, GridConsensus consensus)
        {
            // Calculate perpendicular distance between two infinite grid lines
            // This is the correct metric for grid alignment

            if (consensus.ConsensusStart == null || consensus.ConsensusEnd == null ||
                !record.StartX.HasValue || !record.StartY.HasValue || !record.StartZ.HasValue ||
                !record.EndX.HasValue || !record.EndY.HasValue || !record.EndZ.HasValue)
            {
                return 0;
            }

            // Consensus line points
            var p1 = new XYZ(consensus.ConsensusStart.X, consensus.ConsensusStart.Y, consensus.ConsensusStart.Z);
            var p2 = new XYZ(consensus.ConsensusEnd.X, consensus.ConsensusEnd.Y, consensus.ConsensusEnd.Z);

            // Record line points
            var p3 = new XYZ(record.StartX.Value, record.StartY.Value, record.StartZ.Value);
            var p4 = new XYZ(record.EndX.Value, record.EndY.Value, record.EndZ.Value);

            // Calculate perpendicular distance between lines (2D - ignore Z for grids)
            return PerpendicularDistanceBetweenLines2D(p1, p2, p3, p4);
        }

        private static double PerpendicularDistanceBetweenLines2D(XYZ p1, XYZ p2, XYZ p3, XYZ p4)
        {
            // Direction vectors (ignore Z)
            var d1 = new { X = p2.X - p1.X, Y = p2.Y - p1.Y };
            var d2 = new { X = p4.X - p3.X, Y = p4.Y - p3.Y };

            var len1 = Math.Sqrt(d1.X * d1.X + d1.Y * d1.Y);
            var len2 = Math.Sqrt(d2.X * d2.X + d2.Y * d2.Y);

            if (len1 < 0.001 || len2 < 0.001) return 0; // Degenerate line

            // Normalize directions
            var n1 = new { X = d1.X / len1, Y = d1.Y / len1 };
            var n2 = new { X = d2.X / len2, Y = d2.Y / len2 };

            // Check if lines are parallel (dot product close to 1 or -1)
            var dot = Math.Abs(n1.X * n2.X + n1.Y * n2.Y);

            if (dot > 0.9999) // Parallel lines
            {
                // Distance from point p3 to line p1-p2
                return PointToLineDistance2D(p3, p1, p2);
            }

            // Lines are not parallel - return 0 (they will intersect, different grid direction)
            return 0;
        }

        private static string FormatFeetInches(double feet)
        {
            var totalInches = feet * 12.0;
            var wholeFeet = (int)Math.Floor(feet);
            var remainingInches = (feet - wholeFeet) * 12.0;

            // Round to nearest 1/16"
            var sixteenths = (int)Math.Round(remainingInches * 16.0);
            var wholeInches = sixteenths / 16;
            var fraction = sixteenths % 16;

            // Build the inches part
            string inchPart;
            if (fraction == 0)
            {
                inchPart = wholeInches > 0 ? $"{wholeInches}\"" : "";
            }
            else
            {
                // Simplify fraction
                var numerator = fraction;
                var denominator = 16;
                while (numerator % 2 == 0 && denominator % 2 == 0)
                {
                    numerator /= 2;
                    denominator /= 2;
                }
                if (wholeInches > 0)
                {
                    inchPart = $"{wholeInches} {numerator}/{denominator}\"";
                }
                else
                {
                    inchPart = $"{numerator}/{denominator}\"";
                }
            }

            if (wholeFeet == 0)
            {
                return string.IsNullOrEmpty(inchPart) ? "0\"" : inchPart;
            }
            else if (string.IsNullOrEmpty(inchPart))
            {
                return $"{wholeFeet}'";
            }
            else
            {
                return $"{wholeFeet}'-{inchPart}";
            }
        }

        private static double PointToLineDistance2D(XYZ point, XYZ lineStart, XYZ lineEnd)
        {
            // Vector from lineStart to lineEnd
            var dx = lineEnd.X - lineStart.X;
            var dy = lineEnd.Y - lineStart.Y;
            var lenSquared = dx * dx + dy * dy;

            if (lenSquared < 0.000001) return 0;

            // Vector from lineStart to point
            var px = point.X - lineStart.X;
            var py = point.Y - lineStart.Y;

            // Project point onto line, calculate perpendicular distance
            var t = (px * dx + py * dy) / lenSquared;

            var closestX = lineStart.X + t * dx;
            var closestY = lineStart.Y + t * dy;

            var distX = point.X - closestX;
            var distY = point.Y - closestY;

            return Math.Sqrt(distX * distX + distY * distY);
        }

        private static double UpdateMax(double target, double? candidate)
        {
            if (candidate.HasValue && candidate.Value > target)
            {
                return candidate.Value;
            }

            return target;
        }

        private static string FormatNumber(double? value, int precision)
        {
            return value.HasValue ? Math.Round(value.Value, precision).ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string EscapeHtml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static double CalculateSeverityScore(GridDiscrepancyRecord record)
        {
            var linear = new[]
            {
                record.LengthDeltaFt ?? 0.0,
                record.StartOffsetFt ?? 0.0,
                record.EndOffsetFt ?? 0.0
            }.Max();
            var angle = record.AngleDeltaDeg ?? 0.0;
            var curve = record.CurveTypeMismatch ? 1.0 : 0.0;
            return Math.Max(linear, angle / 10.0 + curve);
        }

        private static string BuildModelSummary(GridRecord record, int precision)
        {
            var parts = new List<string>
            {
                $"Type={record.CurveType}",
                $"Length={FormatNullable(record.LengthFt, precision)} ft"
            };

            if (record.AngleDeg.HasValue)
            {
                parts.Add($"Angle={FormatNullable(record.AngleDeg, precision)}°");
            }

            if (record.RadiusFt.HasValue)
            {
                parts.Add($"Radius={FormatNullable(record.RadiusFt, precision)} ft");
            }

            parts.Add($"Start={FormatPoint(record.StartX, record.StartY, record.StartZ, precision)}");
            parts.Add($"End={FormatPoint(record.EndX, record.EndY, record.EndZ, precision)}");

            return string.Join(" | ", parts);
        }

        private static string FormatPoint(double? x, double? y, double? z, int precision)
        {
            if (!x.HasValue || !y.HasValue || !z.HasValue)
            {
                return "-";
            }

            var xText = FormatNullable(x, precision);
            var yText = FormatNullable(y, precision);
            var zText = FormatNullable(z, precision);

            return $"({xText}, {yText}, {zText})";
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
            return $"{sanitized}_Grids_{timestamp}.csv";
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

        private static string BuildDiscrepancyPath(string basePath)
        {
            var directory = Path.GetDirectoryName(basePath);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(basePath);
            var discrepancyName = $"{fileNameWithoutExtension}_Discrepancies.csv";
            return string.IsNullOrEmpty(directory)
                ? discrepancyName
                : Path.Combine(directory, discrepancyName);
        }

        private static string BuildReportPath(string basePath)
        {
            var directory = Path.GetDirectoryName(basePath);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(basePath);
            var reportName = $"{fileNameWithoutExtension}_Report.html";
            return string.IsNullOrEmpty(directory)
                ? reportName
                : Path.Combine(directory, reportName);
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

        private sealed class PivotTable
        {
            public PivotTable(IReadOnlyList<ModelDescriptor> models, IReadOnlyList<GridPivotRow> rows)
            {
                Models = models;
                Rows = rows;
            }

            public IReadOnlyList<ModelDescriptor> Models { get; }
            public IReadOnlyList<GridPivotRow> Rows { get; }
        }

        private sealed class GridPivotRow
        {
            public GridPivotRow(string gridName, string curveTypes, IDictionary<string, GridRecord> records)
            {
                GridName = gridName;
                CurveTypes = curveTypes;
                Records = new Dictionary<string, GridRecord>(records, StringComparer.OrdinalIgnoreCase);
            }

            public string GridName { get; }
            public string CurveTypes { get; }
            public IReadOnlyDictionary<string, GridRecord> Records { get; }
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

        private sealed class GridDiscrepancyRecord
        {
            public GridDiscrepancyRecord(
                string gridName,
                string referenceModel,
                string referenceType,
                string comparedModel,
                string comparedType,
                double? lengthDeltaFt,
                double? angleDeltaDeg,
                double? radiusDeltaFt,
                double? startOffsetFt,
                double? endOffsetFt,
                bool curveTypeMismatch,
                string notes,
                bool hasMeaningfulDifference)
            {
                GridName = gridName;
                ReferenceModel = referenceModel;
                ReferenceType = referenceType;
                ComparedModel = comparedModel;
                ComparedType = comparedType;
                LengthDeltaFt = lengthDeltaFt;
                AngleDeltaDeg = angleDeltaDeg;
                RadiusDeltaFt = radiusDeltaFt;
                StartOffsetFt = startOffsetFt;
                EndOffsetFt = endOffsetFt;
                CurveTypeMismatch = curveTypeMismatch;
                Notes = notes;
                HasMeaningfulDifference = hasMeaningfulDifference;
            }

            public string GridName { get; }
            public string ReferenceModel { get; }
            public string ReferenceType { get; }
            public string ComparedModel { get; }
            public string ComparedType { get; }
            public double? LengthDeltaFt { get; }
            public double? AngleDeltaDeg { get; }
            public double? RadiusDeltaFt { get; }
            public double? StartOffsetFt { get; }
            public double? EndOffsetFt { get; }
            public bool CurveTypeMismatch { get; }
            public string Notes { get; }
            public bool HasMeaningfulDifference { get; }
        }

        private sealed class GridAnalyticsRow
        {
            public GridAnalyticsRow(string gridName, bool hostPresent, int modelsPresent)
            {
                GridName = gridName;
                HostPresent = hostPresent;
                ModelsPresent = modelsPresent;
                SeverityCategory = Severity.Aligned;
            }

            public string GridName { get; }
            public bool HostPresent { get; }
            public int ModelsPresent { get; }
            public int ComparisonCount { get; set; }
            public double MaxLengthDeltaFt { get; set; }
            public double MaxStartOffsetFt { get; set; }
            public double MaxEndOffsetFt { get; set; }
            public double MaxAngleDeltaDeg { get; set; }
            public bool HasCurveMismatch { get; set; }
            public bool HasMissingData { get; set; }
            public Severity SeverityCategory { get; private set; }
            public GridConsensus? Consensus { get; set; }
            public Dictionary<string, double> ModelDeviations { get; } = new Dictionary<string, double>();

            public int SeverityRank => (int)SeverityCategory;
            public string SeverityText => SeverityCategory switch
            {
                Severity.Critical => "Critical",
                Severity.Major => "Major",
                Severity.Minor => "Minor",
                _ => "Aligned"
            };

            public string SeverityCssClass => SeverityCategory switch
            {
                Severity.Critical => "sev-critical",
                Severity.Major => "sev-major",
                Severity.Minor => "sev-minor",
                _ => "sev-aligned"
            };

            public void RecalculateSeverity()
            {
                SeverityCategory = DetermineSeverity(MaxLengthDeltaFt, MaxStartOffsetFt, MaxEndOffsetFt, MaxAngleDeltaDeg, HasCurveMismatch, HasMissingData);
            }
        }

        private sealed class GridConsensus
        {
            public string GridName { get; set; } = string.Empty;
            public double ConsensusLengthFt { get; set; }
            public double? ConsensusAngleDeg { get; set; }
            public double? ConsensusRadiusFt { get; set; }
            public XYZ? ConsensusStart { get; set; }
            public XYZ? ConsensusEnd { get; set; }
            public XYZ? ConsensusMidpoint { get; set; }
            public string ConsensusCurveType { get; set; } = string.Empty;
            public int ParticipatingModels { get; set; }
            public double AgreementScore { get; set; }
        }

        private sealed class GridSpacingIssue
        {
            public string GridA { get; set; } = string.Empty;
            public string GridB { get; set; } = string.Empty;
            public string ModelName { get; set; } = string.Empty;
            public double SpacingFt { get; set; }
            public double ConsensusSpacingFt { get; set; }
            public double DeviationFt { get; set; }
            public bool IsCritical { get; set; }
        }

        private sealed class GridSpacingData
        {
            public string GridA { get; set; } = string.Empty;
            public string GridB { get; set; } = string.Empty;
            public double ConsensusSpacing { get; set; }
            public Dictionary<string, double> ModelSpacings { get; set; } = new Dictionary<string, double>();
        }

        private sealed class GridMissingIssue
        {
            public string GridName { get; set; } = string.Empty;
            public List<string> PresentInModels { get; set; } = new List<string>();
            public List<string> MissingFromModels { get; set; } = new List<string>();
            public bool IsCritical { get; set; }
        }

        private sealed class ModelQualityScore
        {
            public string ModelName { get; set; } = string.Empty;
            public string ModelType { get; set; } = string.Empty;
            public int TotalGrids { get; set; }
            public int AlignedGrids { get; set; }
            public double AverageDeviation { get; set; }
            public double QualityScore { get; set; }
            public int Rank { get; set; }

            public string RankBadge => Rank switch
            {
                1 => "🥇",
                2 => "🥈",
                3 => "🥉",
                _ => ""
            };
        }

        private sealed class XYZ
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }

            public XYZ(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        private enum Severity
        {
            Aligned = 0,
            Minor = 1,
            Major = 2,
            Critical = 3
        }

        private static Severity DetermineSeverity(double maxLength, double maxStart, double maxEnd, double maxAngle, bool curveMismatch, bool missingData)
        {
            var linear = Math.Max(maxLength, Math.Max(maxStart, maxEnd));

            if (curveMismatch || maxAngle >= 45.0 || linear >= 10.0)
            {
                return Severity.Critical;
            }

            if (maxAngle >= 10.0 || linear >= 1.0)
            {
                return Severity.Major;
            }

            if (missingData || maxAngle >= 1.0 || linear >= 0.1)
            {
                return Severity.Minor;
            }

            return Severity.Aligned;
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
