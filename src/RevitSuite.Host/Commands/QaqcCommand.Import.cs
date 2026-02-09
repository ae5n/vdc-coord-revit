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

        private Result ExecuteImport(
            string correlationId,
            UIDocument uiDoc,
            Document doc,
            QaqcConfig config,
            string category,
            double horizontalThreshold,
            double elevationThreshold,
            bool useHorizontalThreshold,
            bool useElevationThreshold)
        {
            try
            {
                // Prompt for CSV file
                var openDialog = new OpenFileDialog
                {
                    Title = "Import QAQC Field Data",
                    Filter = "CSV Files (*.csv)|*.csv",
                    CheckFileExists = true
                };

                if (openDialog.ShowDialog() != DialogResult.OK)
                {
                    LogManager.Info(correlationId, "Import cancelled by user.");
                    return Result.Cancelled;
                }

                // Parse CSV
                var records = ParseCsvImport(openDialog.FileName, correlationId);
                if (records.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No valid data found in CSV file.");
                    LogManager.Warn(correlationId, "Import cancelled - no valid data.");
                    return Result.Cancelled;
                }

                // Match to elements and calculate deviations
                var deviations = CalculateDeviations(
                    doc,
                    records,
                    config,
                    category,
                    correlationId,
                    horizontalThreshold,
                    elevationThreshold,
                    useHorizontalThreshold,
                    useElevationThreshold);
                if (deviations.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No matching Control Points found in model.");
                    LogManager.Warn(correlationId, "Import cancelled - no matches.");
                    return Result.Cancelled;
                }

                // Update parameters and create visualizations
                using (var tx = new Transaction(doc, "RevitSuite: QAQC Import"))
                {
                    tx.Start();

                    try
                    {
                        UpdateSharedParameters(doc, deviations, correlationId);
                        ApplyModelPointType(doc, deviations, config, correlationId);
                        PlaceFieldPoints(doc, deviations, config, correlationId);
                        // CreateDeviationLines(doc, deviations, correlationId); // Temporarily disabled for performance
                        if (config.CreateDeviationArrows)
                        {
                            CreateDeviationIndicators(doc, deviations, config, correlationId);
                        }
                        CreateDeviationAnnotations(doc, deviations, correlationId);

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        LogManager.Error(correlationId, "Transaction failed during import.", ex);
                        throw;
                    }
                }

                var deviationCount = deviations.Count(d => d.Status == ToleranceStatus.Yellow || d.Status == ToleranceStatus.Green);
                var redCount = deviations.Count(d => d.Status == ToleranceStatus.Red);

                LogManager.Info(
                    correlationId,
                    $"Import completed: {deviations.Count} points analyzed. Deviation: {deviationCount}, Critical: {redCount}. Thresholds => Horizontal({useHorizontalThreshold}): {horizontalThreshold}, Elevation({useElevationThreshold}): {elevationThreshold}");
                TaskDialog.Show("RevitSuite",
                    $"Import successful!\n\n" +
                    $"{deviations.Count} Control Points analyzed:\n" +
                    $"  Deviation (below threshold): {deviationCount}\n" +
                    $"  Critical (above threshold): {redCount}\n\n" +
                    $"Thresholds used:\n" +
                    $"  Horizontal (N/E): {(useHorizontalThreshold ? $"{horizontalThreshold:F3} ft" : "Disabled")}\n" +
                    $"  Elevation: {(useElevationThreshold ? $"{elevationThreshold:F3} ft" : "Disabled")}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, "Import failed.", ex);
                throw;
            }
        }

        private List<ControlPointRecord> ParseCsvImport(string path, string correlationId)
        {
            var records = new List<ControlPointRecord>();

            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                // Read header
                var headerLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    throw new InvalidDataException("CSV file is empty.");
                }

                // Validate header (should have 8 columns with Footing ID)
                var headers = headerLine.Split(',');
                if (headers.Length < 8)
                {
                    throw new InvalidDataException($"Invalid CSV format. Expected 8 columns, found {headers.Length}.");
                }

                int lineNumber = 1;
                while (!reader.EndOfStream)
                {
                    lineNumber++;
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var values = line.Split(',');
                    if (values.Length < 8)
                    {
                        LogManager.Warn(correlationId, $"CSV line {lineNumber} has insufficient columns - skipped.");
                        continue;
                    }

                    try
                    {
                        var pointNumber = values[0].Trim();
                        if (string.IsNullOrWhiteSpace(pointNumber))
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has empty Point Number - skipped.");
                            continue;
                        }

                        // Parse Field coordinates (columns 5, 6, 7 - after Footing ID)
                        if (!double.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldEasting) ||
                            !double.TryParse(values[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldNorthing) ||
                            !double.TryParse(values[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldElevation))
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has invalid Field coordinates - skipped.");
                            continue;
                        }

                        records.Add(new ControlPointRecord
                        {
                            PointNumber = pointNumber,
                            FieldEasting = fieldEasting,
                            FieldNorthing = fieldNorthing,
                            FieldElevation = fieldElevation
                        });
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"CSV line {lineNumber} parse error: {ex.Message} - skipped.");
                    }
                }
            }

            return records;
        }

        private List<DeviationResult> CalculateDeviations(
            Document doc,
            List<ControlPointRecord> records,
            QaqcConfig config,
            string category,
            string correlationId,
            double horizontalThreshold,
            double elevationThreshold,
            bool useHorizontalThreshold,
            bool useElevationThreshold)
        {
            var deviations = new List<DeviationResult>();

            // Get category-specific settings
            var categorySettings = config.GetCategorySettings(category);
            LogManager.Info(
                correlationId,
                $"Using {category} settings with UI thresholds: Horizontal({useHorizontalThreshold})={horizontalThreshold}, Elevation({useElevationThreshold})={elevationThreshold}. Schema reference: ToleranceGreen={categorySettings.ToleranceGreen}, ToleranceYellow={categorySettings.ToleranceYellow}, ComparisonMethod={categorySettings.ComparisonMethod}");

            // Collect all Control Points from document
            var allControlPoints = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => fi.Symbol?.Family?.Name == config.DefaultFamilyName)
                .ToList();

            // Build dictionary of control points by Point Number for fast O(1) lookup
            var controlPointsDict = new Dictionary<string, FamilyInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var cp in allControlPoints)
            {
                var pointNum = GetParameterValueString(cp, PointNumberGuid);
                if (!string.IsNullOrEmpty(pointNum) && !controlPointsDict.ContainsKey(pointNum))
                {
                    controlPointsDict[pointNum] = cp;
                }
            }

            LogManager.Info(correlationId, $"Built control points dictionary: {controlPointsDict.Count} entries for fast matching.");

            foreach (var record in records)
            {
                // Fast dictionary lookup instead of linear search
                if (!controlPointsDict.TryGetValue(record.PointNumber, out var matchingElement))
                {
                    LogManager.Warn(correlationId, $"Point Number '{record.PointNumber}' not found in model - skipped.");
                    continue;
                }

                // Get model coordinates
                var modelEasting = GetParameterValueDouble(matchingElement, CsEastingGuid);
                var modelNorthing = GetParameterValueDouble(matchingElement, CsNorthingGuid);
                var modelElevation = GetParameterValueDouble(matchingElement, CsElevationGuid);

                if (!modelEasting.HasValue || !modelNorthing.HasValue || !modelElevation.HasValue)
                {
                    LogManager.Warn(correlationId, $"Point Number '{record.PointNumber}' missing model coordinates - skipped.");
                    continue;
                }

                // Calculate deviations
                var devEasting = record.FieldEasting.Value - modelEasting.Value;
                var devNorthing = record.FieldNorthing.Value - modelNorthing.Value;
                var devElevation = record.FieldElevation.Value - modelElevation.Value;
                var horizontalDev = Math.Sqrt(devEasting * devEasting + devNorthing * devNorthing);
                var totalDev = Math.Sqrt(devEasting * devEasting + devNorthing * devNorthing + devElevation * devElevation);

                var exceedsHorizontal = useHorizontalThreshold && horizontalDev > horizontalThreshold;
                var exceedsElevation = useElevationThreshold && Math.Abs(devElevation) > elevationThreshold;
                var isCritical = exceedsHorizontal || exceedsElevation;
                var status = isCritical ? ToleranceStatus.Red : ToleranceStatus.Yellow;

                // Get model point location
                XYZ modelPoint = null;
                if (matchingElement.Location is LocationPoint locPoint)
                {
                    modelPoint = locPoint.Point;
                }

                deviations.Add(new DeviationResult
                {
                    PointNumber = record.PointNumber,
                    ElementId = matchingElement.Id,
                    UniqueId = matchingElement.UniqueId,
                    DeviationEasting = devEasting,
                    DeviationNorthing = devNorthing,
                    DeviationElevation = devElevation,
                    HorizontalDeviation = horizontalDev,
                    TotalDeviation = totalDev,
                    Status = status,
                    ModelPoint = modelPoint
                });
            }

            return deviations;
        }

        private void UpdateSharedParameters(Document doc, List<DeviationResult> deviations, string correlationId)
        {
            foreach (var deviation in deviations)
            {
                var element = doc.GetElement(deviation.ElementId);
                if (element == null)
                    continue;

                try
                {
                    SetParameterByGuid(element, DeviationEastingGuid, deviation.DeviationEasting);
                    SetParameterByGuid(element, DeviationNorthingGuid, deviation.DeviationNorthing);
                    SetDeviationElevationParameter(element, deviation.DeviationElevation);
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to update parameters for Point {deviation.PointNumber}: {ex.Message}");
                }
            }
        }

        private void SetDeviationElevationParameter(Element element, double value)
        {
            // GUID for elevation deviation is not currently defined in codebase.
            // Write by known shared-parameter names used in control point families.
            if (SetDoubleParameterByName(element, "Deviation Elevation", value))
            {
                return;
            }

            if (SetDoubleParameterByName(element, "DeviationElevation", value))
            {
                return;
            }

            SetDoubleParameterByName(element, "Deviation_Elevation", value);
        }

        private void ApplyModelPointType(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            var seedSymbol = FindControlPointSymbol(doc, config);
            if (seedSymbol == null)
            {
                LogManager.Warn(correlationId, "Control Point family not found; model type assignment skipped.");
                return;
            }

            if (!TryEnsurePointTypeSymbols(doc, config, seedSymbol, correlationId, out var modelSymbol, out _, out _))
            {
                LogManager.Warn(correlationId, "Could not ensure Model/Deviation/Critical types; model type assignment skipped.");
                return;
            }

            foreach (var deviation in deviations)
            {
                if (!(doc.GetElement(deviation.ElementId) is FamilyInstance modelPoint))
                    continue;

                try
                {
                    if (modelPoint.Symbol?.Id != modelSymbol.Id)
                    {
                        modelPoint.Symbol = modelSymbol;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to set 'Model' type for point {deviation.PointNumber}: {ex.Message}");
                }
            }
        }

        private void PlaceFieldPoints(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            var seedSymbol = FindControlPointSymbol(doc, config);
            if (seedSymbol == null)
            {
                LogManager.Error(correlationId, "Control Point family not found.");
                return;
            }

            if (!TryEnsurePointTypeSymbols(doc, config, seedSymbol, correlationId, out var modelSymbol, out var deviationSymbol, out var criticalSymbol))
            {
                LogManager.Error(correlationId, "Failed to create/find Model/Deviation/Critical types for Control Point family.");
                return;
            }

            // Collect existing field points by Point Number for smart update
            var existingFieldPoints = new Dictionary<string, FamilyInstance>(StringComparer.OrdinalIgnoreCase);
            var existingFieldPointsList = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi =>
                    fi.Symbol?.Name == "Field" ||
                    fi.Symbol?.Name == "Deviation" ||
                    fi.Symbol?.Name == "Critical")
                .ToList();

            foreach (var fp in existingFieldPointsList)
            {
                var pointNum = GetParameterValueString(fp, PointNumberGuid);
                if (!string.IsNullOrEmpty(pointNum))
                {
                    existingFieldPoints[pointNum] = fp;
                }
            }

            LogManager.Info(correlationId, $"Found {existingFieldPoints.Count} existing field points.");

            // Get survey point transform
            var projectLocation = doc.ActiveProjectLocation;
            var sharedToProject = projectLocation?.GetTransform() ?? Transform.Identity;
            var projectToShared = sharedToProject.Inverse;

            int fieldPointsPlaced = 0;
            int fieldPointsUpdated = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    // Calculate field point location (model point + deviations)
                    var fieldPoint = new XYZ(
                        deviation.ModelPoint.X + deviation.DeviationEasting,
                        deviation.ModelPoint.Y + deviation.DeviationNorthing,
                        deviation.ModelPoint.Z + deviation.DeviationElevation);
                    var fieldSymbol = deviation.Status switch
                    {
                        ToleranceStatus.Green => modelSymbol,
                        ToleranceStatus.Yellow => deviationSymbol,
                        ToleranceStatus.Red => criticalSymbol,
                        _ => modelSymbol
                    };

                    FamilyInstance fieldInstance;

                    // Check if field point exists - update vs create
                    if (existingFieldPoints.TryGetValue(deviation.PointNumber, out var existingInstance))
                    {
                        // UPDATE existing field point
                        fieldInstance = existingInstance;

                        // Move to new location
                        if (fieldInstance.Location is LocationPoint locPoint)
                        {
                            locPoint.Point = fieldPoint;
                        }

                        if (fieldInstance.Symbol?.Id != fieldSymbol.Id)
                        {
                            fieldInstance.Symbol = fieldSymbol;
                        }

                        fieldPointsUpdated++;
                    }
                    else
                    {
                        // CREATE new field point
                        fieldInstance = doc.Create.NewFamilyInstance(fieldPoint, fieldSymbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                        // Set Point Number (same as model point for easy matching)
                        SetParameterByGuid(fieldInstance, PointNumberGuid, deviation.PointNumber);

                        fieldPointsPlaced++;
                    }

                    // Convert to survey/shared coordinates
                    var sharedFieldPoint = projectToShared.OfPoint(fieldPoint);

                    // Update coordinates (for both new and existing)
                    SetParameterByGuid(fieldInstance, CsEastingGuid, sharedFieldPoint.X);
                    SetParameterByGuid(fieldInstance, CsNorthingGuid, sharedFieldPoint.Y);
                    SetParameterByGuid(fieldInstance, CsElevationGuid, sharedFieldPoint.Z);

                    // Update deviation parameters (for both new and existing)
                    SetParameterByGuid(fieldInstance, DeviationEastingGuid, deviation.DeviationEasting);
                    SetParameterByGuid(fieldInstance, DeviationNorthingGuid, deviation.DeviationNorthing);
                    SetDeviationElevationParameter(fieldInstance, deviation.DeviationElevation);

                    // Update comments (for both new and existing)
                    var commentsParam = fieldInstance.LookupParameter("Comments");
                    if (commentsParam != null && !commentsParam.IsReadOnly)
                    {
                        commentsParam.Set($"Field measurement for {deviation.PointNumber}");
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to place/update field point for {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Field points: {fieldPointsPlaced} created, {fieldPointsUpdated} updated.");
        }

        private void CreateDeviationLines(Document doc, List<DeviationResult> deviations, string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend)
            {
                LogManager.Warn(correlationId, "Active view not suitable for detail lines - skipping deviation lines.");
                return;
            }

            int linesCreated = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    // Calculate field point location
                    var fieldPoint = new XYZ(
                        deviation.ModelPoint.X + deviation.DeviationEasting,
                        deviation.ModelPoint.Y + deviation.DeviationNorthing,
                        deviation.ModelPoint.Z + deviation.DeviationElevation);

                    // Create a line between model point and field point
                    var line = Line.CreateBound(deviation.ModelPoint, fieldPoint);

                    // Create detail line in the view
                    var detailLine = doc.Create.NewDetailCurve(view, line);

                    if (detailLine != null)
                    {
                        linesCreated++;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create deviation line for {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Created {linesCreated} deviation lines.");
        }

        private void ApplyGraphicOverrides(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule)
            {
                LogManager.Warn(correlationId, "Active view not suitable for graphic overrides - skipping visualization.");
                return;
            }

            var greenColor = new Autodesk.Revit.DB.Color(34, 197, 94);
            var yellowColor = new Autodesk.Revit.DB.Color(234, 179, 8);
            var redColor = new Autodesk.Revit.DB.Color(239, 68, 68);

            // Build dictionary of field points by Point Number for fast lookup
            var fieldPointsDict = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            var fieldPoints = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => fi.Symbol?.Name == "Field")
                .ToList();

            foreach (var fp in fieldPoints)
            {
                var pointNum = GetParameterValueString(fp, PointNumberGuid);
                if (!string.IsNullOrEmpty(pointNum) && !fieldPointsDict.ContainsKey(pointNum))
                {
                    fieldPointsDict[pointNum] = fp.Id;
                }
            }

            LogManager.Info(correlationId, $"Built field points dictionary: {fieldPointsDict.Count} entries.");

            foreach (var deviation in deviations)
            {
                var color = deviation.Status switch
                {
                    ToleranceStatus.Green => greenColor,
                    ToleranceStatus.Yellow => yellowColor,
                    ToleranceStatus.Red => redColor,
                    _ => greenColor
                };

                var overrides = new OverrideGraphicSettings();
                overrides.SetProjectionLineColor(color);
                overrides.SetProjectionLineWeight(5);

                // Apply to model point
                var modelElement = doc.GetElement(deviation.ElementId);
                if (modelElement != null)
                {
                    try
                    {
                        view.SetElementOverrides(modelElement.Id, overrides);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"Failed to apply graphic override for model point {deviation.PointNumber}: {ex.Message}");
                    }
                }

                // Apply to corresponding field point using fast dictionary lookup
                if (fieldPointsDict.TryGetValue(deviation.PointNumber, out var fieldPointId))
                {
                    try
                    {
                        view.SetElementOverrides(fieldPointId, overrides);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"Failed to apply graphic override for field point {deviation.PointNumber}: {ex.Message}");
                    }
                }
            }
        }

        private void CreateDeviationIndicators(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            var greenMaterialId = EnsureMaterial(doc, "QAQC_Green", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(34, 197, 94));
            var yellowMaterialId = EnsureMaterial(doc, "QAQC_Yellow", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(234, 179, 8));
            var redMaterialId = EnsureMaterial(doc, "QAQC_Red", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(239, 68, 68));

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    var materialId = deviation.Status switch
                    {
                        ToleranceStatus.Green => greenMaterialId,
                        ToleranceStatus.Yellow => yellowMaterialId,
                        ToleranceStatus.Red => redMaterialId,
                        _ => greenMaterialId
                    };

                    var fieldPoint = new XYZ(
                        deviation.ModelPoint.X + deviation.DeviationEasting,
                        deviation.ModelPoint.Y + deviation.DeviationNorthing,
                        deviation.ModelPoint.Z + deviation.DeviationElevation);

                    var arrow = BuildArrowGeometry(deviation.ModelPoint, fieldPoint, config.ArrowScaleFactor, materialId);
                    if (arrow != null && arrow.Count > 0)
                    {
                        var directShape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        directShape.SetShape(arrow);
                        directShape.Name = $"Deviation Indicator - {deviation.PointNumber}";
                        directShape.ApplicationId = "RevitSuite.QAQC";
                        directShape.ApplicationDataId = deviation.UniqueId;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create deviation indicator for Point {deviation.PointNumber}: {ex.Message}");
                }
            }
        }

    }
}
