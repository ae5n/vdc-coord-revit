using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
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
            double horizontalVerifiedThreshold,
            double horizontalCriticalThreshold,
            double elevationVerifiedThreshold,
            double elevationCriticalThreshold,
            bool useHorizontalThreshold,
            bool useElevationThreshold,
            bool useSelectedPointThresholds)
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

                var mapping = PromptCsvColumnMapping(
                    openDialog.FileName,
                    "Map As-Built CSV Columns",
                    useElevationThreshold,
                    correlationId);
                if (mapping == null)
                {
                    LogManager.Info(correlationId, "Import cancelled during CSV column mapping.");
                    return Result.Cancelled;
                }

                // Parse CSV
                var records = ParseCsvImport(openDialog.FileName, correlationId, mapping, useElevationThreshold);
                if (records.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No valid data found in CSV file.");
                    LogManager.Warn(correlationId, "Import cancelled - no valid data.");
                    return Result.Cancelled;
                }

                if (!useSelectedPointThresholds)
                {
                    var modelPointNumbers = CollectModelPointNumbers(doc, config);
                    var matchedPointNumbers = new HashSet<string>(
                        records
                            .Select(r => r.PointNumber)
                            .Where(p => !string.IsNullOrWhiteSpace(p) && modelPointNumbers.Contains(p)),
                        StringComparer.OrdinalIgnoreCase);

                    if (!ShowPointMatchPreview(records, modelPointNumbers, matchedPointNumbers))
                    {
                        LogManager.Info(correlationId, "Import cancelled from CSV match preview dialog.");
                        return Result.Cancelled;
                    }
                }

                var selectedPointNumbers = BuildSelectedPointFilter(
                    records,
                    uiDoc,
                    doc,
                    config,
                    useSelectedPointThresholds,
                    correlationId);
                if (useSelectedPointThresholds && selectedPointNumbers == null)
                {
                    LogManager.Info(correlationId, "Import cancelled while selecting points.");
                    return Result.Cancelled;
                }

                // Match to elements and calculate deviations
                var deviations = CalculateDeviations(
                    doc,
                    records,
                    config,
                    category,
                    correlationId,
                    horizontalVerifiedThreshold,
                    horizontalCriticalThreshold,
                    elevationVerifiedThreshold,
                    elevationCriticalThreshold,
                    useHorizontalThreshold,
                    useElevationThreshold,
                    selectedPointNumbers);
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
                        // Keep point coloring type/material-driven. Annotation overrides are applied separately.
                        // CreateDeviationLines(doc, deviations, correlationId); // Temporarily disabled for performance
                        if (config.CreateDeviationArrows)
                        {
                            CreateDeviationIndicators(doc, deviations, config, correlationId);
                        }
                        CreateDeviationAnnotations(doc, deviations, config, useHorizontalThreshold, useElevationThreshold, correlationId);

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        LogManager.Error(correlationId, "Transaction failed during import.", ex);
                        throw;
                    }
                }

                var verifiedCount = deviations.Count(d => d.Status == ToleranceStatus.Blue);
                var deviationCount = deviations.Count(d => d.Status == ToleranceStatus.Yellow);
                var redCount = deviations.Count(d => d.Status == ToleranceStatus.Red);

                LogManager.Info(
                    correlationId,
                    $"Import completed: {deviations.Count} points analyzed. Verified: {verifiedCount}, Deviation: {deviationCount}, Critical: {redCount}.");
                TaskDialog.Show("RevitSuite",
                    $"Import successful!\n\n" +
                    $"{deviations.Count} Control Points analyzed:\n" +
                    $"  Verified (<= verified threshold): {verifiedCount}\n" +
                    $"  Deviation (below threshold): {deviationCount}\n" +
                    $"  Critical (above threshold): {redCount}\n\n" +
                    $"Thresholds used:\n" +
                    $"  Horizontal (N/E): {(useHorizontalThreshold ? $"Verified <= {horizontalVerifiedThreshold:F3} ft, Critical > {horizontalCriticalThreshold:F3} ft" : "Disabled")}\n" +
                    $"  Elevation: {(useElevationThreshold ? $"Verified <= {elevationVerifiedThreshold:F3} ft, Critical > {elevationCriticalThreshold:F3} ft" : "Disabled")}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, "Import failed.", ex);
                throw;
            }
        }

        private List<ControlPointRecord> ParseCsvImport(
            string path,
            string correlationId,
            CsvColumnMapping mapping,
            bool requireElevationValues)
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

                int lineNumber = 1;
                while (!reader.EndOfStream)
                {
                    lineNumber++;
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var values = line.Split(',');

                    try
                    {
                        var maxRequiredIndex = Math.Max(
                            Math.Max(mapping.PointNumberIndex, mapping.NorthingIndex),
                            Math.Max(mapping.EastingIndex, mapping.ElevationIndex));
                        if (values.Length <= maxRequiredIndex)
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has insufficient columns for mapped headers - skipped.");
                            continue;
                        }

                        var pointNumber = values[mapping.PointNumberIndex].Trim();
                        if (string.IsNullOrWhiteSpace(pointNumber))
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has empty Point Number - skipped.");
                            continue;
                        }

                        if (!double.TryParse(values[mapping.EastingIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldEasting) ||
                            !double.TryParse(values[mapping.NorthingIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldNorthing))
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has invalid Northing/Easting values - skipped.");
                            continue;
                        }

                        double? fieldElevation = null;
                        if (mapping.ElevationIndex >= 0)
                        {
                            if (double.TryParse(values[mapping.ElevationIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedElevation))
                            {
                                fieldElevation = parsedElevation;
                            }
                            else if (requireElevationValues)
                            {
                                LogManager.Warn(correlationId, $"CSV line {lineNumber} has invalid Elevation value - skipped.");
                                continue;
                            }
                        }
                        else if (requireElevationValues)
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} missing Elevation column mapping - skipped.");
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

        private CsvColumnMapping PromptCsvColumnMapping(
            string csvPath,
            string dialogTitle,
            bool requireElevation,
            string correlationId)
        {
            var headers = ReadCsvHeaders(csvPath);
            if (headers == null || headers.Length == 0)
            {
                throw new InvalidDataException("CSV file is missing a header row.");
            }

            var defaults = BuildDefaultCsvColumnMapping(headers, requireElevation);
            using (var form = new CsvColumnMappingForm(headers, defaults, requireElevation, dialogTitle))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return null;
                }

                var selected = form.SelectedMapping;
                LogManager.Info(
                    correlationId,
                    $"CSV mapping selected. PointNumber={selected.PointNumberIndex}, Northing={selected.NorthingIndex}, Easting={selected.EastingIndex}, Elevation={selected.ElevationIndex}");
                return selected;
            }
        }

        private string[] ReadCsvHeaders(string path)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                var headerLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    return Array.Empty<string>();
                }

                return headerLine.Split(',');
            }
        }

        private CsvColumnMapping BuildDefaultCsvColumnMapping(string[] headers, bool requireElevation)
        {
            var pointNumberIndex = FindHeaderIndex(headers, "pointnumber", "point number", "point_no", "point id", "point");
            var northingIndex = FindHeaderIndex(headers, "northing", "north");
            var eastingIndex = FindHeaderIndex(headers, "easting", "east");
            var elevationIndex = FindHeaderIndex(headers, "elevation", "elev", "z");

            // Backward-compatible defaults for legacy 8-column QAQC format.
            if (pointNumberIndex < 0 && headers.Length >= 1)
            {
                pointNumberIndex = 0;
            }

            if (headers.Length >= 8)
            {
                if (eastingIndex < 0)
                {
                    eastingIndex = 5;
                }

                if (northingIndex < 0)
                {
                    northingIndex = 6;
                }

                if (elevationIndex < 0)
                {
                    elevationIndex = 7;
                }
            }

            if (!requireElevation)
            {
                elevationIndex = elevationIndex >= 0 ? elevationIndex : -1;
            }

            return new CsvColumnMapping(pointNumberIndex, northingIndex, eastingIndex, elevationIndex);
        }

        private static int FindHeaderIndex(string[] headers, params string[] aliases)
        {
            if (headers == null || aliases == null || aliases.Length == 0)
            {
                return -1;
            }

            var normalizedAliases = new HashSet<string>(aliases.Select(NormalizeHeaderToken), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
            {
                var normalizedHeader = NormalizeHeaderToken(headers[i]);
                if (normalizedAliases.Contains(normalizedHeader))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string NormalizeHeaderToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        private List<DeviationResult> CalculateDeviations(
            Document doc,
            List<ControlPointRecord> records,
            QaqcConfig config,
            string category,
            string correlationId,
            double horizontalVerifiedThreshold,
            double horizontalCriticalThreshold,
            double elevationVerifiedThreshold,
            double elevationCriticalThreshold,
            bool useHorizontalThreshold,
            bool useElevationThreshold,
            HashSet<string> selectedPointNumbers)
        {
            var deviations = new List<DeviationResult>();

            // Get category-specific settings
            var categorySettings = config.GetCategorySettings(category);
            LogManager.Info(
                correlationId,
                $"Using {category} settings with UI thresholds: Horizontal({useHorizontalThreshold}) Verified<={horizontalVerifiedThreshold}, Critical>{horizontalCriticalThreshold}; Elevation({useElevationThreshold}) Verified<={elevationVerifiedThreshold}, Critical>{elevationCriticalThreshold}. Schema reference: ToleranceGreen={categorySettings.ToleranceGreen}, ToleranceYellow={categorySettings.ToleranceYellow}, ComparisonMethod={categorySettings.ComparisonMethod}");

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
                if (selectedPointNumbers != null && !selectedPointNumbers.Contains(record.PointNumber))
                {
                    continue;
                }

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
                if (!record.FieldEasting.HasValue || !record.FieldNorthing.HasValue)
                {
                    LogManager.Warn(correlationId, $"Point Number '{record.PointNumber}' missing Northing/Easting in CSV - skipped.");
                    continue;
                }

                if (useElevationThreshold && !record.FieldElevation.HasValue)
                {
                    LogManager.Warn(correlationId, $"Point Number '{record.PointNumber}' missing Elevation in CSV while elevation check is enabled - skipped.");
                    continue;
                }

                var devEasting = record.FieldEasting.Value - modelEasting.Value;
                var devNorthing = record.FieldNorthing.Value - modelNorthing.Value;
                var devElevation = record.FieldElevation.HasValue
                    ? record.FieldElevation.Value - modelElevation.Value
                    : 0.0;
                var horizontalDev = Math.Sqrt(devEasting * devEasting + devNorthing * devNorthing);
                var totalDev = Math.Sqrt(devEasting * devEasting + devNorthing * devNorthing + devElevation * devElevation);

                var exceedsHorizontal = useHorizontalThreshold && horizontalDev > horizontalCriticalThreshold;
                var exceedsElevation = useElevationThreshold && Math.Abs(devElevation) > elevationCriticalThreshold;
                var isCritical = exceedsHorizontal || exceedsElevation;

                var withinHorizontalVerified = !useHorizontalThreshold || horizontalDev <= horizontalVerifiedThreshold;
                var withinElevationVerified = !useElevationThreshold || Math.Abs(devElevation) <= elevationVerifiedThreshold;
                var isVerified = (useHorizontalThreshold || useElevationThreshold) && withinHorizontalVerified && withinElevationVerified;

                var status = isCritical
                    ? ToleranceStatus.Red
                    : (isVerified ? ToleranceStatus.Blue : ToleranceStatus.Yellow);

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

        private HashSet<string> BuildSelectedPointFilter(
            List<ControlPointRecord> records,
            UIDocument uiDoc,
            Document doc,
            QaqcConfig config,
            bool useSelectedPointThresholds,
            string correlationId)
        {
            if (!useSelectedPointThresholds)
            {
                return null;
            }

            IList<Reference> pickedReferences;
            try
            {
                TaskDialog.Show(
                    "RevitSuite",
                    "Select model control points in the view to analyze.\nPress Finish when done, or Esc to cancel.");
                pickedReferences = uiDoc.Selection.PickObjects(
                    ObjectType.Element,
                    new ControlPointSelectionFilter(config.DefaultFamilyName),
                    "Select model control points to analyze.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }

            var pointNumbers = new List<string>();
            foreach (var reference in pickedReferences)
            {
                var element = doc.GetElement(reference);
                var pointNumber = GetParameterValueString(element, PointNumberGuid);
                if (!string.IsNullOrWhiteSpace(pointNumber))
                {
                    pointNumbers.Add(pointNumber);
                }
            }

            pointNumbers = pointNumbers
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pointNumbers.Count == 0)
            {
                TaskDialog.Show("RevitSuite", "No valid model control points were selected.");
                return null;
            }

            var csvPointNumbers = new HashSet<string>(
                records
                    .Select(r => r.PointNumber)
                    .Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);

            var matchedPointNumbers = pointNumbers
                .Where(csvPointNumbers.Contains)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var skippedCount = pointNumbers.Count - matchedPointNumbers.Count;
            if (skippedCount > 0)
            {
                LogManager.Warn(correlationId, $"{skippedCount} selected model points were not found in CSV by Point Number and will be ignored.");
            }

            if (matchedPointNumbers.Count == 0)
            {
                TaskDialog.Show("RevitSuite", "None of the selected model points match CSV Point Number values.");
                return null;
            }

            using (var previewForm = new PointMatchPreviewForm(
                records,
                new HashSet<string>(pointNumbers, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(matchedPointNumbers, StringComparer.OrdinalIgnoreCase)))
            {
                if (previewForm.ShowDialog() != DialogResult.OK)
                {
                    return null;
                }
            }

            LogManager.Info(correlationId, $"Selected point filter configured: {matchedPointNumbers.Count} point(s).");
            return new HashSet<string>(matchedPointNumbers, StringComparer.OrdinalIgnoreCase);
        }

        private HashSet<string> CollectModelPointNumbers(Document doc, QaqcConfig config)
        {
            return new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Site)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Symbol?.Family?.Name == config.DefaultFamilyName)
                    .Select(fi => GetParameterValueString(fi, PointNumberGuid))
                    .Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);
        }

        private bool ShowPointMatchPreview(
            IList<ControlPointRecord> records,
            HashSet<string> selectedPointNumbers,
            HashSet<string> matchedPointNumbers)
        {
            using (var previewForm = new PointMatchPreviewForm(records, selectedPointNumbers, matchedPointNumbers))
            {
                return previewForm.ShowDialog() == DialogResult.OK;
            }
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

            if (!TryEnsurePointTypeSymbols(doc, config, seedSymbol, correlationId, out var modelSymbol, out _, out _, out _))
            {
                LogManager.Warn(correlationId, "Could not ensure Model/Verified/Deviation/Critical types; model type assignment skipped.");
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

            if (!TryEnsurePointTypeSymbols(doc, config, seedSymbol, correlationId, out var modelSymbol, out var verifiedSymbol, out var deviationSymbol, out var criticalSymbol))
            {
                LogManager.Error(correlationId, "Failed to create/find Model/Verified/Deviation/Critical types for Control Point family.");
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
                    fi.Symbol?.Name == "Verified" ||
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
                        ToleranceStatus.Blue => verifiedSymbol,
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

                    // Keep reference to the as-built point instance for downstream annotations (e.g., spot elevation).
                    deviation.FieldElementId = fieldInstance.Id;
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

            var modelColor = new Autodesk.Revit.DB.Color(59, 130, 246);
            var verifiedColor = new Autodesk.Revit.DB.Color(34, 197, 94);
            var deviationColor = new Autodesk.Revit.DB.Color(249, 115, 22);
            var criticalColor = new Autodesk.Revit.DB.Color(239, 68, 68);

            // Build dictionary of field points by Point Number for fast lookup
            var fieldPointsDict = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            var fieldPoints = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi =>
                    fi.Symbol?.Family?.Name == config.DefaultFamilyName &&
                    (fi.Symbol?.Name == "Verified" || fi.Symbol?.Name == "Deviation" || fi.Symbol?.Name == "Critical" || fi.Symbol?.Name == "Field"))
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
                var asBuiltColor = deviation.Status switch
                {
                    ToleranceStatus.Blue => verifiedColor,
                    ToleranceStatus.Yellow => deviationColor,
                    ToleranceStatus.Red => criticalColor,
                    _ => modelColor
                };

                var modelOverrides = new OverrideGraphicSettings();
                modelOverrides.SetProjectionLineColor(modelColor);
                modelOverrides.SetProjectionLineWeight(5);

                // Apply to model point
                var modelElement = doc.GetElement(deviation.ElementId);
                if (modelElement != null)
                {
                    try
                    {
                        view.SetElementOverrides(modelElement.Id, modelOverrides);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"Failed to apply graphic override for model point {deviation.PointNumber}: {ex.Message}");
                    }
                }

                // Apply to corresponding field point using fast dictionary lookup
                if (fieldPointsDict.TryGetValue(deviation.PointNumber, out var fieldPointId))
                {
                    var fieldOverrides = new OverrideGraphicSettings();
                    fieldOverrides.SetProjectionLineColor(asBuiltColor);
                    fieldOverrides.SetProjectionLineWeight(5);

                    try
                    {
                        view.SetElementOverrides(fieldPointId, fieldOverrides);
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
            var blueMaterialId = EnsureMaterial(doc, "QAQC_Blue", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(59, 130, 246));
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
                        ToleranceStatus.Green => blueMaterialId,
                        ToleranceStatus.Blue => greenMaterialId,
                        ToleranceStatus.Yellow => yellowMaterialId,
                        ToleranceStatus.Red => redMaterialId,
                        _ => blueMaterialId
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
