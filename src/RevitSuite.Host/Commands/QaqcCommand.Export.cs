using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    public partial class QaqcCommand
    {

        private Result ExecuteExport(string correlationId, UIDocument uiDoc, Document doc, QaqcConfig config, string category)
        {
            try
            {
                // Auto-collect all Control Point instances
                var controlPoints = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Site)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Symbol?.Family?.Name == config.DefaultFamilyName)
                    .ToList();

                if (controlPoints == null || controlPoints.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No Control Points found in the model.");
                    LogManager.Warn(correlationId, "Export cancelled - no Control Points found.");
                    return Result.Cancelled;
                }

                LogManager.Info(correlationId, $"Found {controlPoints.Count} Control Points for export.");

                // Extract model coordinates
                var records = ExtractModelCoordinates(doc, controlPoints, correlationId);
                if (records.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No valid Control Points with required parameters found.");
                    LogManager.Warn(correlationId, "Export cancelled - no valid Control Points.");
                    return Result.Cancelled;
                }

                // Prompt for save location
                var saveDialog = new SaveFileDialog
                {
                    Title = "Export QAQC Control Points",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = $"QAQC_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                    AddExtension = true,
                    DefaultExt = "csv",
                    OverwritePrompt = true
                };

                if (saveDialog.ShowDialog() != DialogResult.OK)
                {
                    LogManager.Info(correlationId, "Export cancelled by user.");
                    return Result.Cancelled;
                }

                // Write CSV
                WriteExportCsv(saveDialog.FileName, records, config.CoordinatePrecision);

                LogManager.Info(correlationId, $"Export completed: {records.Count} points written to '{saveDialog.FileName}'");
                TaskDialog.Show("RevitSuite", $"Export successful!\n\n{records.Count} Control Points exported to:\n{saveDialog.FileName}");

                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                LogManager.Info(correlationId, "Export cancelled by user.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, "Export failed.", ex);
                throw;
            }
        }

        private List<ControlPointRecord> ExtractModelCoordinates(Document doc, List<FamilyInstance> instances, string correlationId)
        {
            var records = new List<ControlPointRecord>();

            foreach (var instance in instances)
            {
                try
                {
                    var pointNumber = GetParameterValueString(instance, PointNumberGuid);
                    if (string.IsNullOrWhiteSpace(pointNumber))
                    {
                        LogManager.Warn(correlationId, $"Control Point {instance.Id.IntegerValue} missing Point Number - skipped.");
                        continue;
                    }

                    var easting = GetParameterValueDouble(instance, CsEastingGuid);
                    var northing = GetParameterValueDouble(instance, CsNorthingGuid);
                    var elevation = GetParameterValueDouble(instance, CsElevationGuid);

                    if (!easting.HasValue || !northing.HasValue || !elevation.HasValue)
                    {
                        LogManager.Warn(correlationId, $"Control Point {pointNumber} missing coordinate parameters - skipped.");
                        continue;
                    }

                    string description = null;
                    var commentsParam = instance.LookupParameter("Comments");
                    if (commentsParam != null && commentsParam.StorageType == StorageType.String)
                    {
                        description = commentsParam.AsString();
                    }

                    records.Add(new ControlPointRecord
                    {
                        PointNumber = pointNumber,
                        Description = description,
                        ModelEasting = easting.Value,
                        ModelNorthing = northing.Value,
                        ModelElevation = elevation.Value,
                        ElementId = instance.Id,
                        UniqueId = instance.UniqueId
                    });
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to extract coordinates from Control Point {instance.Id.IntegerValue}: {ex.Message}");
                }
            }

            return records;
        }

        private void WriteExportCsv(string path, List<ControlPointRecord> records, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                // Header
                writer.WriteLine("Point Number,Northing,Easting,Elevation,Description");

                // Data rows
                foreach (var record in records.OrderBy(r => r.PointNumber))
                {
                    var format = $"F{precision}";
                    var description = record.Description ?? "";
                    writer.WriteLine($"{EscapeCsvValue(record.PointNumber)}," +
                        $"{record.ModelNorthing.ToString(format, CultureInfo.InvariantCulture)}," +
                        $"{record.ModelEasting.ToString(format, CultureInfo.InvariantCulture)}," +
                        $"{record.ModelElevation.ToString(format, CultureInfo.InvariantCulture)}," +
                        $"{EscapeCsvValue(description)}");
                }
            }
        }

    }
}
