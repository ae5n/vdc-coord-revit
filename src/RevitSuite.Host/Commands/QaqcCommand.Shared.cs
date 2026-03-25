using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    public partial class QaqcCommand
    {


        private bool IsPointInCropRegion(XYZ point, XYZ min, XYZ max)
        {
            if (min == null || max == null)
                return true;

            return point.X >= min.X && point.X <= max.X &&
                   point.Y >= min.Y && point.Y <= max.Y &&
                   point.Z >= min.Z && point.Z <= max.Z;
        }

        private Parameter GetParameterByGuid(Element element, Guid guid)
        {
            foreach (Parameter param in element.Parameters)
            {
                if (param.IsShared && param.GUID == guid)
                    return param;
            }
            return null;
        }

        private string GetParameterValueString(Element element, Guid guid)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && param.StorageType == StorageType.String)
                return param.AsString();
            return null;
        }

        private double? GetParameterValueDouble(Element element, Guid guid)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && param.StorageType == StorageType.Double && param.HasValue)
                return param.AsDouble();
            return null;
        }

        private void SetParameterByGuid(Element element, Guid guid, double value)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.Double)
            {
                param.Set(value);
            }
        }

        private bool SetDoubleParameterByName(Element element, string parameterName, double value)
        {
            if (element == null || string.IsNullOrWhiteSpace(parameterName))
            {
                return false;
            }

            var param = element.LookupParameter(parameterName);
            if (param == null || param.IsReadOnly || param.StorageType != StorageType.Double)
            {
                return false;
            }

            param.Set(value);
            return true;
        }

        private string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        private static bool TryParseThresholdInput(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text.Trim();
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return value > 0;
            }

            var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var whole) &&
                TryParseFraction(parts[1], out var mixedFraction))
            {
                value = whole + mixedFraction;
                return value > 0;
            }

            if (TryParseFraction(trimmed, out var fraction))
            {
                value = fraction;
                return value > 0;
            }

            return false;
        }

        private static double InchesToFeet(double inches)
        {
            return inches / 12.0;
        }

        private static double FeetToInches(double feet)
        {
            return feet * 12.0;
        }

        private static string FormatFeetAsInchFraction(double feet)
        {
            var inches = FeetToInches(feet);
            if (inches <= 0)
            {
                return "0";
            }

            const int denominator = 8;
            var whole = (int)Math.Floor(inches);
            var fractional = inches - whole;
            var numerator = (int)Math.Round(fractional * denominator);

            if (numerator == denominator)
            {
                whole += 1;
                numerator = 0;
            }

            if (numerator == 0)
            {
                return whole.ToString(CultureInfo.InvariantCulture);
            }

            if (whole == 0)
            {
                return $"{numerator}/{denominator}";
            }

            return $"{whole} {numerator}/{denominator}";
        }

        private static bool TryParseFraction(string text, out double value)
        {
            value = 0;
            var fractionParts = text.Split('/');
            if (fractionParts.Length != 2)
            {
                return false;
            }

            if (!double.TryParse(fractionParts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator))
            {
                return false;
            }

            if (!double.TryParse(fractionParts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator))
            {
                return false;
            }

            if (Math.Abs(denominator) < 1e-9)
            {
                return false;
            }

            value = numerator / denominator;
            return true;
        }

        private ElementId EnsureMaterial(Document doc, string name, int transparency, Autodesk.Revit.DB.Color color)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                try
                {
                    existing.Transparency = transparency;
                    existing.Color = color;
                }
                catch
                {
                    // Ignore updates
                }

                return existing.Id;
            }

            var materialId = Material.Create(doc, name);
            if (doc.GetElement(materialId) is Material created)
            {
                try
                {
                    created.Transparency = transparency;
                    created.Color = color;
                }
                catch
                {
                    // Ignore updates
                }
            }

            return materialId;
        }

        private List<GeometryObject> BuildArrowGeometry(XYZ start, XYZ end, double scaleFactor, ElementId materialId)
        {
            var geometryList = new List<GeometryObject>();

            var deviation = end - start;
            var deviationLength = deviation.GetLength();

            if (deviationLength < 1e-6)
                return geometryList; // Too small

            // Scale the arrow
            var scaledLength = Math.Min(Math.Max(deviationLength * scaleFactor, 0.1), 10.0); // Min 0.1 ft, Max 10 ft
            var direction = deviation.Normalize();
            var scaledEnd = start + (direction * scaledLength);

            // Create simple line-based arrow (cylinder shaft)
            var shaftRadius = 0.05; // 0.05 ft diameter
            var line = Line.CreateBound(start, scaledEnd);

            // Note: Full arrow geometry with cone would require more complex solid creation
            // For now, creating a simple representation
            // In production, you would create a cylinder and cone using GeometryCreationUtilities

            return geometryList;
        }

        private void CreateDeviationAnnotations(
            Document doc,
            List<DeviationResult> deviations,
            RevitSuite.Host.Config.QaqcConfig config,
            bool includeHorizontalAnnotations,
            bool includeElevationAnnotations,
            double tagOffsetEast,
            double tagOffsetNorth,
            string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend || view.ViewType == ViewType.ThreeD)
            {
                LogManager.Warn(correlationId, "Active view not suitable for tags - skipping annotations. Use a plan/section/elevation view.");
                return;
            }

            if (!includeHorizontalAnnotations && !includeElevationAnnotations)
            {
                LogManager.Info(correlationId, "No annotation components selected (N/E and Elevation both disabled).");
                return;
            }

            if (!TryGetDeviationTagSymbols(doc, config, out var horizontalTagSymbol, out var elevationTagSymbol))
            {
                LogManager.Warn(correlationId, $"Tag family '{config.DeviationTagFamilyName}' with types '{config.DeviationTagHorizontalTypeName}' and '{config.DeviationTagElevationTypeName}' not found.");
                return;
            }

            if (includeHorizontalAnnotations && horizontalTagSymbol == null)
            {
                LogManager.Warn(correlationId, $"Horizontal tag type '{config.DeviationTagHorizontalTypeName}' not found in '{config.DeviationTagFamilyName}' - horizontal tags will be skipped.");
                includeHorizontalAnnotations = false;
            }

            if (includeElevationAnnotations && elevationTagSymbol == null)
            {
                LogManager.Warn(correlationId, $"Elevation tag type '{config.DeviationTagElevationTypeName}' not found in '{config.DeviationTagFamilyName}' - elevation tags will be skipped.");
                includeElevationAnnotations = false;
            }

            if (!includeHorizontalAnnotations && !includeElevationAnnotations)
            {
                LogManager.Warn(correlationId, "No tag types available - skipping all annotations.");
                return;
            }

            // Preserve existing annotations. New run adds/updates only current run outputs.

            var createdTags = 0;
            foreach (var deviation in deviations)
            {
                var targetElement = ResolveTagTargetElement(doc, deviation);
                if (targetElement == null || !TryGetTagHeadBasePoint(targetElement, deviation.ModelPoint, out var basePoint))
                {
                    continue;
                }

                var annotationColor = GetAnnotationColorByStatus(deviation.Status);
                try
                {
                    if (includeHorizontalAnnotations)
                    {
                        var horizontalHead = new XYZ(basePoint.X + tagOffsetEast, basePoint.Y + tagOffsetNorth, basePoint.Z);
                        if (TryCreateDeviationTag(doc, view, targetElement, horizontalTagSymbol, horizontalHead, annotationColor, true, correlationId, out _))
                        {
                            createdTags++;
                        }
                    }

                    if (includeElevationAnnotations)
                    {
                        if (TryCreateSpotElevationForDeviation(
                                doc,
                                view,
                                deviation,
                                basePoint,
                                tagOffsetEast,
                                tagOffsetNorth,
                                annotationColor,
                                correlationId,
                                out _))
                        {
                            // Keep Z deviation tag under spot elevation text.
                            var elevationHead = new XYZ(
                                basePoint.X + tagOffsetEast,
                                basePoint.Y + tagOffsetNorth - 0.8,
                                basePoint.Z);

                            if (TryCreateDeviationTag(
                                    doc,
                                    view,
                                    targetElement,
                                    elevationTagSymbol,
                                    elevationHead,
                                    annotationColor,
                                    false,
                                    correlationId,
                                    out var elevationTagId))
                            {
                                createdTags++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create tag annotation for Point {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Created {createdTags} deviation tag annotation(s).");
        }

        private bool TryCreateSpotElevationForDeviation(
            Document doc,
            Autodesk.Revit.DB.View view,
            DeviationResult deviation,
            XYZ basePoint,
            double tagOffsetEast,
            double tagOffsetNorth,
            Autodesk.Revit.DB.Color annotationColor,
            string correlationId,
            out ElementId spotId)
        {
            spotId = ElementId.InvalidElementId;
            if (deviation == null || deviation.FieldElementId == null || deviation.FieldElementId == ElementId.InvalidElementId)
            {
                LogManager.Warn(correlationId, $"Spot elevation skipped for point '{deviation?.PointNumber ?? "Unknown"}' - no as-built element id.");
                return false;
            }

            var element = doc.GetElement(deviation.FieldElementId);
            if (element == null)
            {
                LogManager.Warn(correlationId, $"Spot elevation skipped for point '{deviation.PointNumber}' - as-built element not found.");
                return false;
            }

            return TryCreateSpotElevation(doc, view, element, basePoint, tagOffsetEast, tagOffsetNorth, annotationColor, correlationId, out spotId);
        }

        private bool TryCreateSpotElevation(
            Document doc,
            Autodesk.Revit.DB.View view,
            Element targetElement,
            XYZ basePoint,
            double tagOffsetEast,
            double tagOffsetNorth,
            Autodesk.Revit.DB.Color annotationColor,
            string correlationId,
            out ElementId spotId)
        {
            spotId = ElementId.InvalidElementId;
            var candidates = GetSpotElevationReferenceCandidates(targetElement, view, basePoint);
            if (candidates.Count == 0)
            {
                LogManager.Warn(correlationId, $"Spot elevation skipped on element {targetElement.Id} - no valid geometric reference.");
                return false;
            }

            Exception lastException = null;
            foreach (var candidate in candidates)
            {
                var origin = candidate.ReferencePoint;
                // Respect user-entered East/North offsets for spot position.
                var bend = new XYZ(origin.X + tagOffsetEast, origin.Y + tagOffsetNorth, origin.Z);
                var endDirection = tagOffsetEast >= 0 ? 1.0 : -1.0;
                var end = new XYZ(bend.X + endDirection, bend.Y, bend.Z);

                try
                {
                    var spot = doc.Create.NewSpotElevation(view, candidate.Reference, origin, bend, end, candidate.ReferencePoint, true);
                    if (spot == null)
                    {
                        continue;
                    }

                    try
                    {
                        var annotationOverrides = new OverrideGraphicSettings();
                        annotationOverrides.SetProjectionLineColor(annotationColor);
                        annotationOverrides.SetProjectionLineWeight(3);
                        view.SetElementOverrides(spot.Id, annotationOverrides);
                    }
                    catch
                    {
                        // Non-fatal
                    }

                    spotId = spot.Id;
                    return true;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            LogManager.Warn(
                correlationId,
                $"Spot elevation failed on element {targetElement.Id}: {lastException?.Message ?? "No candidate references succeeded."}");
            return false;
        }

        private List<SpotElevationReferenceCandidate> GetSpotElevationReferenceCandidates(
            Element element,
            Autodesk.Revit.DB.View view,
            XYZ samplePoint)
        {
            var candidates = new List<SpotElevationReferenceCandidate>();

            if (!(element is FamilyInstance familyInstance))
            {
                return candidates;
            }

            TryAddStrongFamilyReferenceCandidates(familyInstance, samplePoint, candidates);

            return candidates
                .GroupBy(c => GetStableReferenceKey(familyInstance.Document, c.Reference))
                .Select(g => g.OrderBy(x => x.Distance).First())
                .OrderBy(c => c.Distance)
                .ToList();
        }

        private static string GetStableReferenceKey(Document doc, Reference reference)
        {
            if (doc == null || reference == null)
            {
                return string.Empty;
            }

            try
            {
                return reference.ConvertToStableRepresentation(doc);
            }
            catch
            {
                return reference.ElementId.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void TryAddStrongFamilyReferenceCandidates(
            FamilyInstance familyInstance,
            XYZ samplePoint,
            List<SpotElevationReferenceCandidate> candidates)
        {
            if (familyInstance == null || candidates == null)
            {
                return;
            }

            try
            {
                var refs = familyInstance.GetReferences(FamilyInstanceReferenceType.StrongReference);
                if (refs == null)
                {
                    return;
                }

                foreach (var r in refs)
                {
                    if (r != null)
                    {
                        candidates.Add(new SpotElevationReferenceCandidate(r, samplePoint, 0.1));
                    }
                }
            }
            catch
            {
                // Ignore unsupported reference extraction.
            }
        }

        private static Autodesk.Revit.DB.Color GetAnnotationColorByStatus(ToleranceStatus status)
        {
            return status switch
            {
                ToleranceStatus.Blue => new Autodesk.Revit.DB.Color(34, 197, 94),   // Verified
                ToleranceStatus.Yellow => new Autodesk.Revit.DB.Color(249, 115, 22), // Deviation
                ToleranceStatus.Red => new Autodesk.Revit.DB.Color(239, 68, 68),     // Critical
                _ => new Autodesk.Revit.DB.Color(59, 130, 246)                        // Model/default
            };
        }

        private bool TryGetDeviationTagSymbols(
            Document doc,
            RevitSuite.Host.Config.QaqcConfig config,
            out FamilySymbol horizontalTagSymbol,
            out FamilySymbol elevationTagSymbol)
        {
            horizontalTagSymbol = null;
            elevationTagSymbol = null;

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.FamilyName != null &&
                            s.FamilyName.Equals(config.DeviationTagFamilyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            horizontalTagSymbol = symbols.FirstOrDefault(s => s.Name.Equals(config.DeviationTagHorizontalTypeName, StringComparison.OrdinalIgnoreCase));
            elevationTagSymbol = symbols.FirstOrDefault(s => s.Name.Equals(config.DeviationTagElevationTypeName, StringComparison.OrdinalIgnoreCase));

            return horizontalTagSymbol != null || elevationTagSymbol != null;
        }

        private void DeleteExistingDeviationTags(Document doc, Autodesk.Revit.DB.View view, RevitSuite.Host.Config.QaqcConfig config, string correlationId)
        {
            try
            {
                var existingTags = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .Where(t => IsDeviationTagFamily(t, config))
                    .Select(t => t.Id)
                    .ToList();

                if (existingTags.Count == 0)
                {
                    return;
                }

                doc.Delete(existingTags);
                LogManager.Info(correlationId, $"Deleted {existingTags.Count} existing deviation tags in current view.");
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to delete existing deviation tags: {ex.Message}");
            }
        }

        private bool IsDeviationTagFamily(IndependentTag tag, RevitSuite.Host.Config.QaqcConfig config)
        {
            if (tag == null)
            {
                return false;
            }

            var tagType = tag.Document.GetElement(tag.GetTypeId()) as FamilySymbol;
            return tagType?.FamilyName != null &&
                   tagType.FamilyName.Equals(config.DeviationTagFamilyName, StringComparison.OrdinalIgnoreCase);
        }

        private Element ResolveTagTargetElement(Document doc, DeviationResult deviation)
        {
            if (deviation.FieldElementId != null && deviation.FieldElementId != ElementId.InvalidElementId)
            {
                var fieldElement = doc.GetElement(deviation.FieldElementId);
                if (fieldElement != null)
                {
                    return fieldElement;
                }
            }

            return doc.GetElement(deviation.ElementId);
        }

        private bool TryGetTagHeadBasePoint(Element element, XYZ fallbackPoint, out XYZ basePoint)
        {
            basePoint = fallbackPoint;
            if (element?.Location is LocationPoint locationPoint)
            {
                basePoint = locationPoint.Point;
            }

            return basePoint != null;
        }

        private bool TryCreateDeviationTag(
            Document doc,
            Autodesk.Revit.DB.View view,
            Element targetElement,
            FamilySymbol tagSymbol,
            XYZ tagHeadPoint,
            Autodesk.Revit.DB.Color annotationColor,
            bool hasLeader,
            string correlationId,
            out ElementId tagId)
        {
            tagId = ElementId.InvalidElementId;
            Reference reference;
            try
            {
                reference = new Reference(targetElement);
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to get tag reference for element {targetElement?.Id}: {ex.Message}");
                return false;
            }

            IndependentTag tag;
            try
            {
                tag = IndependentTag.Create(
                    doc,
                    view.Id,
                    reference,
                    hasLeader,
                    TagMode.TM_ADDBY_CATEGORY,
                    TagOrientation.Horizontal,
                    tagHeadPoint);
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to create deviation tag on element {targetElement?.Id}: {ex.Message}");
                return false;
            }

            if (tag == null)
            {
                return false;
            }
            tagId = tag.Id;

            try
            {
                if (tag.GetTypeId() != tagSymbol.Id)
                {
                    tag.ChangeTypeId(tagSymbol.Id);
                }

                // Force final head position so UI offsets are honored after type change.
                tag.TagHeadPosition = tagHeadPoint;
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to set tag type '{tagSymbol.Name}' for tag {tag.Id}: {ex.Message}");
            }

            try
            {
                var annotationOverrides = new OverrideGraphicSettings();
                annotationOverrides.SetProjectionLineColor(annotationColor);
                annotationOverrides.SetProjectionLineWeight(3);
                view.SetElementOverrides(tag.Id, annotationOverrides);
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to apply color override to tag {tag.Id}: {ex.Message}");
            }

            return true;
        }

        #region Nested Classes

        private enum QaqcMode
        {
            Place,
            Export,
            ImportAndAnalyze
        }

        private enum PairingMode
        {
            PointNumber,
            Proximity
        }

        private enum ToleranceStatus
        {
            Green,
            Blue,
            Yellow,
            Red
        }

        private class FootingInfo
        {
            public Element Footing { get; set; }
            public Transform Transform { get; set; }
            public string SourceModel { get; set; }
            public RevitLinkInstance LinkInstance { get; set; }
        }

        private class ControlPointRecord
        {
            public string PointNumber { get; set; }
            public string Description { get; set; }
            public string Comment { get; set; }
            public double ModelEasting { get; set; }
            public double ModelNorthing { get; set; }
            public double ModelElevation { get; set; }
            public double? FieldEasting { get; set; }
            public double? FieldNorthing { get; set; }
            public double? FieldElevation { get; set; }
            public ElementId ElementId { get; set; }
            public string UniqueId { get; set; }
        }

        private class DeviationResult
        {
            public string PointNumber { get; set; }
            public ElementId ElementId { get; set; }
            public ElementId FieldElementId { get; set; }
            public string UniqueId { get; set; }
            public string SourceComment { get; set; }
            public double DeviationEasting { get; set; }
            public double DeviationNorthing { get; set; }
            public double DeviationElevation { get; set; }
            public double HorizontalDeviation { get; set; }
            public double TotalDeviation { get; set; }
            public ToleranceStatus Status { get; set; }
            public XYZ ModelPoint { get; set; }
        }

        private class ProximityModelCandidate
        {
            public FamilyInstance Element { get; set; }
            public string PointNumber { get; set; }
            public double Easting { get; set; }
            public double Northing { get; set; }
            public double Elevation { get; set; }
        }

        private class SpotElevationReferenceCandidate
        {
            public SpotElevationReferenceCandidate(Reference reference, XYZ referencePoint, double distance)
            {
                Reference = reference;
                ReferencePoint = referencePoint;
                Distance = distance;
            }

            public Reference Reference { get; }
            public XYZ ReferencePoint { get; }
            public double Distance { get; }
        }

        private enum SogSelectionMode
        {
            Host,
            Linked
        }

        private class SogFloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is Floor)
                {
                    return true;
                }

                return elem.Category != null &&
                       elem.Category.Id.Value == (long)BuiltInCategory.OST_Floors;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class LinkedFloorSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;

            public LinkedFloorSelectionFilter(Document doc)
            {
                _doc = doc;
            }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference.LinkedElementId == ElementId.InvalidElementId)
                    return false;

                var linkInstance = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var linkDoc = linkInstance?.GetLinkDocument();
                if (linkDoc == null)
                    return false;

                var linkedElement = linkDoc.GetElement(reference.LinkedElementId);
                return linkedElement is Floor;
            }
        }

        private class ControlPointSelectionFilter : ISelectionFilter
        {
            private readonly string _familyName;

            public ControlPointSelectionFilter(string familyName)
            {
                _familyName = familyName ?? string.Empty;
            }

            public bool AllowElement(Element elem)
            {
                if (!(elem is FamilyInstance instance))
                {
                    return false;
                }

                return string.Equals(instance.Symbol?.Family?.Name, _familyName, StringComparison.OrdinalIgnoreCase);
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private SogSelectionMode? PromptSogSelectionMode()
        {
            var dialog = new TaskDialog("RevitSuite")
            {
                MainInstruction = "Select slab-on-grade location",
                MainContent = "Choose whether the slab is in the host model or a linked model.",
                AllowCancellation = true,
                CommonButtons = TaskDialogCommonButtons.Cancel
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Host model");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Linked model");

            var result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1)
                return SogSelectionMode.Host;
            if (result == TaskDialogResult.CommandLink2)
                return SogSelectionMode.Linked;

            return null;
        }

        private class QaqcDialog : System.Windows.Forms.Form
        {
            private System.Windows.Forms.ComboBox categoryComboBox;
            private System.Windows.Forms.RadioButton exportRadioButton;
            private System.Windows.Forms.RadioButton importRadioButton;
            private System.Windows.Forms.Button okButton;
            private System.Windows.Forms.Button cancelButton;
            private System.Windows.Forms.RadioButton placeRadioButton;
            private System.Windows.Forms.NumericUpDown pourNumericUpDown;
            private System.Windows.Forms.Label pourLabel;
            private System.Windows.Forms.TextBox horizontalVerifiedThresholdTextBox;
            private System.Windows.Forms.TextBox horizontalCriticalThresholdTextBox;
            private System.Windows.Forms.TextBox elevationVerifiedThresholdTextBox;
            private System.Windows.Forms.TextBox elevationCriticalThresholdTextBox;
            private System.Windows.Forms.Label horizontalVerifiedThresholdLabel;
            private System.Windows.Forms.Label horizontalCriticalThresholdLabel;
            private System.Windows.Forms.Label elevationVerifiedThresholdLabel;
            private System.Windows.Forms.Label elevationCriticalThresholdLabel;
            private System.Windows.Forms.CheckBox useHorizontalThresholdCheckBox;
            private System.Windows.Forms.CheckBox useElevationThresholdCheckBox;
            private System.Windows.Forms.Label thresholdHelpLabel;
            private System.Windows.Forms.ComboBox thresholdScopeComboBox;
            private System.Windows.Forms.Label thresholdScopeLabel;
            private System.Windows.Forms.GroupBox thresholdGroupBox;
            private System.Windows.Forms.Label tagPlacementLabel;
            private System.Windows.Forms.Label tagOffsetEastLabel;
            private System.Windows.Forms.NumericUpDown tagOffsetEastNumericUpDown;
            private System.Windows.Forms.Label tagOffsetNorthLabel;
            private System.Windows.Forms.NumericUpDown tagOffsetNorthNumericUpDown;
            private System.Windows.Forms.Label pairingModeLabel;
            private System.Windows.Forms.ComboBox pairingModeComboBox;
            private System.Windows.Forms.Label proximityMaxDistLabel;
            private System.Windows.Forms.NumericUpDown proximityMaxDistNumericUpDown;
            private double _selectedHorizontalVerifiedThreshold = InchesToFeet(0.125);
            private double _selectedHorizontalCriticalThreshold = InchesToFeet(0.625);
            private double _selectedElevationVerifiedThreshold = InchesToFeet(0.125);
            private double _selectedElevationCriticalThreshold = InchesToFeet(0.625);

            public string SelectedCategory => categoryComboBox.SelectedItem?.ToString() ?? "Footings";
            public int SelectedPourNumber => (int)(pourNumericUpDown?.Value ?? 1);
            public double SelectedHorizontalVerifiedThreshold => _selectedHorizontalVerifiedThreshold;
            public double SelectedHorizontalCriticalThreshold => _selectedHorizontalCriticalThreshold;
            public double SelectedElevationVerifiedThreshold => _selectedElevationVerifiedThreshold;
            public double SelectedElevationCriticalThreshold => _selectedElevationCriticalThreshold;
            public bool SelectedUseHorizontalThreshold => useHorizontalThresholdCheckBox?.Checked ?? true;
            public bool SelectedUseElevationThreshold => useElevationThresholdCheckBox?.Checked ?? true;
            public bool SelectedUseSelectedPointThresholds => thresholdScopeComboBox?.SelectedIndex == 1;
            public double SelectedTagOffsetEast => (double)(tagOffsetEastNumericUpDown?.Value ?? 3.0m);
            public double SelectedTagOffsetNorth => (double)(tagOffsetNorthNumericUpDown?.Value ?? 3.0m);
            public PairingMode SelectedPairingMode => (pairingModeComboBox?.SelectedIndex ?? 0) == 1
                ? PairingMode.Proximity
                : PairingMode.PointNumber;
            public double SelectedProximityMaxDistanceFt => (double)(proximityMaxDistNumericUpDown?.Value ?? 1.0m);
            public QaqcMode SelectedMode
            {
                get
                {
                    if (importRadioButton != null && importRadioButton.Checked)
                    {
                        return QaqcMode.ImportAndAnalyze;
                    }

                    if (exportRadioButton != null && exportRadioButton.Checked)
                    {
                        return QaqcMode.Export;
                    }

                    return QaqcMode.Place;
                }
            }

            public QaqcDialog()
            {
                InitializeComponent();
            }

            private void InitializeComponent()
            {
                Text = "QAQC - Control Point Verification";
                Size = new System.Drawing.Size(620, 710);
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);

                var titleLabel = new System.Windows.Forms.Label
                {
                    Text = "QAQC Configuration",
                    Location = new System.Drawing.Point(16, 12),
                    Size = new System.Drawing.Size(260, 24),
                    Font = new System.Drawing.Font("Segoe UI Semibold", 11F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point)
                };
                Controls.Add(titleLabel);

                var subtitleLabel = new System.Windows.Forms.Label
                {
                    Text = "Choose workflow, scope, and thresholds for control-point verification.",
                    Location = new System.Drawing.Point(16, 36),
                    Size = new System.Drawing.Size(550, 20),
                    ForeColor = System.Drawing.Color.DimGray
                };
                Controls.Add(subtitleLabel);

                var generalGroupBox = new System.Windows.Forms.GroupBox
                {
                    Text = "General",
                    Location = new System.Drawing.Point(16, 190),
                    Size = new System.Drawing.Size(550, 92)
                };
                Controls.Add(generalGroupBox);

                var categoryLabel = new System.Windows.Forms.Label
                {
                    Text = "Category:",
                    Location = new System.Drawing.Point(14, 30),
                    Size = new System.Drawing.Size(90, 20)
                };
                generalGroupBox.Controls.Add(categoryLabel);

                categoryComboBox = new System.Windows.Forms.ComboBox
                {
                    Location = new System.Drawing.Point(110, 28),
                    Size = new System.Drawing.Size(290, 25),
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
                };
                categoryComboBox.Items.AddRange(new object[] { "Footings", "Columns", "Walls", SogCategoryName, ReadyPointsCategoryName });
                categoryComboBox.SelectedItem = ReadyPointsCategoryName;
                if (categoryComboBox.SelectedIndex < 0)
                {
                    categoryComboBox.SelectedIndex = 0;
                }
                generalGroupBox.Controls.Add(categoryComboBox);

                pourLabel = new System.Windows.Forms.Label
                {
                    Text = "Pour #:",
                    Location = new System.Drawing.Point(14, 60),
                    Size = new System.Drawing.Size(90, 20),
                    Visible = false
                };
                generalGroupBox.Controls.Add(pourLabel);

                pourNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(110, 58),
                    Size = new System.Drawing.Size(80, 25),
                    Minimum = 1,
                    Maximum = 99,
                    Value = 1,
                    Visible = false
                };
                generalGroupBox.Controls.Add(pourNumericUpDown);

                var modeGroupBox = new System.Windows.Forms.GroupBox
                {
                    Text = "Mode",
                    Location = new System.Drawing.Point(16, 64),
                    Size = new System.Drawing.Size(550, 118)
                };
                Controls.Add(modeGroupBox);

                placeRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Place Model Control Points",
                    Location = new System.Drawing.Point(18, 28),
                    Size = new System.Drawing.Size(220, 25),
                    Checked = true,
                    Tag = QaqcMode.Place
                };
                modeGroupBox.Controls.Add(placeRadioButton);

                exportRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Export Model Points (CSV Template)",
                    Location = new System.Drawing.Point(18, 55),
                    Size = new System.Drawing.Size(340, 25),
                    Tag = QaqcMode.Export
                };
                modeGroupBox.Controls.Add(exportRadioButton);

                importRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Place && Analyze As-built Points",
                    Location = new System.Drawing.Point(18, 82),
                    Size = new System.Drawing.Size(240, 25),
                    Tag = QaqcMode.ImportAndAnalyze
                };
                modeGroupBox.Controls.Add(importRadioButton);

                thresholdGroupBox = new System.Windows.Forms.GroupBox
                {
                    Text = "Thresholds for Deviation Calculation && Tag Placement",
                    Location = new System.Drawing.Point(16, 290),
                    Size = new System.Drawing.Size(550, 305),
                    Visible = false
                };
                Controls.Add(thresholdGroupBox);

                thresholdScopeLabel = new System.Windows.Forms.Label
                {
                    Text = "Scope:",
                    Location = new System.Drawing.Point(14, 30),
                    Size = new System.Drawing.Size(90, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(thresholdScopeLabel);

                thresholdScopeComboBox = new System.Windows.Forms.ComboBox
                {
                    Location = new System.Drawing.Point(110, 28),
                    Size = new System.Drawing.Size(200, 25),
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                    Visible = false
                };
                thresholdScopeComboBox.Items.AddRange(new object[] { "All points", "Selected points" });
                thresholdScopeComboBox.SelectedIndex = 0;
                thresholdGroupBox.Controls.Add(thresholdScopeComboBox);

                pairingModeLabel = new System.Windows.Forms.Label
                {
                    Text = "Pairing:",
                    Location = new System.Drawing.Point(330, 30),
                    Size = new System.Drawing.Size(62, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(pairingModeLabel);

                pairingModeComboBox = new System.Windows.Forms.ComboBox
                {
                    Location = new System.Drawing.Point(394, 28),
                    Size = new System.Drawing.Size(124, 25),
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                    Visible = false
                };
                pairingModeComboBox.Items.AddRange(new object[] { "Point Number", "Proximity" });
                pairingModeComboBox.SelectedIndex = 0;
                thresholdGroupBox.Controls.Add(pairingModeComboBox);

                useHorizontalThresholdCheckBox = new System.Windows.Forms.CheckBox
                {
                    Text = "Check horizontal (N/E)",
                    Location = new System.Drawing.Point(14, 60),
                    Size = new System.Drawing.Size(220, 24),
                    Checked = true,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(useHorizontalThresholdCheckBox);

                horizontalVerifiedThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "N/E Verified <= (in):",
                    Location = new System.Drawing.Point(32, 88),
                    Size = new System.Drawing.Size(130, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalVerifiedThresholdLabel);

                horizontalVerifiedThresholdTextBox = new System.Windows.Forms.TextBox
                {
                    Location = new System.Drawing.Point(170, 86),
                    Size = new System.Drawing.Size(100, 25),
                    Text = "1/8",
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalVerifiedThresholdTextBox);

                horizontalCriticalThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "N/E Critical > (in):",
                    Location = new System.Drawing.Point(292, 88),
                    Size = new System.Drawing.Size(120, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalCriticalThresholdLabel);

                horizontalCriticalThresholdTextBox = new System.Windows.Forms.TextBox
                {
                    Location = new System.Drawing.Point(418, 86),
                    Size = new System.Drawing.Size(100, 25),
                    Text = "5/8",
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalCriticalThresholdTextBox);

                useElevationThresholdCheckBox = new System.Windows.Forms.CheckBox
                {
                    Text = "Check elevation",
                    Location = new System.Drawing.Point(14, 124),
                    Size = new System.Drawing.Size(180, 24),
                    Checked = true,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(useElevationThresholdCheckBox);

                elevationVerifiedThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "Elev Verified <= (in):",
                    Location = new System.Drawing.Point(32, 152),
                    Size = new System.Drawing.Size(130, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationVerifiedThresholdLabel);

                elevationVerifiedThresholdTextBox = new System.Windows.Forms.TextBox
                {
                    Location = new System.Drawing.Point(170, 150),
                    Size = new System.Drawing.Size(100, 25),
                    Text = "1/8",
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationVerifiedThresholdTextBox);

                elevationCriticalThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "Elev Critical > (in):",
                    Location = new System.Drawing.Point(292, 152),
                    Size = new System.Drawing.Size(120, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationCriticalThresholdLabel);

                elevationCriticalThresholdTextBox = new System.Windows.Forms.TextBox
                {
                    Location = new System.Drawing.Point(418, 150),
                    Size = new System.Drawing.Size(100, 25),
                    Text = "5/8",
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationCriticalThresholdTextBox);

                thresholdHelpLabel = new System.Windows.Forms.Label
                {
                    Text = "Threshold units are inches. Use decimal or fraction (for example: 0.125 or 1/8)." + Environment.NewLine +
                           "<= Verified => Verified (Green), > Critical => Critical (Red), else Deviation (Orange).",
                    Location = new System.Drawing.Point(14, 194),
                    Size = new System.Drawing.Size(522, 40),
                    ForeColor = System.Drawing.Color.DimGray,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(thresholdHelpLabel);

                tagPlacementLabel = new System.Windows.Forms.Label
                {
                    Text = "Tag Placement (ft):",
                    Location = new System.Drawing.Point(14, 232),
                    Size = new System.Drawing.Size(160, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(tagPlacementLabel);

                tagOffsetEastLabel = new System.Windows.Forms.Label
                {
                    Text = "East:",
                    Location = new System.Drawing.Point(32, 254),
                    Size = new System.Drawing.Size(40, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(tagOffsetEastLabel);

                tagOffsetEastNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(72, 252),
                    Size = new System.Drawing.Size(84, 25),
                    DecimalPlaces = 2,
                    Increment = 0.10m,
                    Minimum = -50m,
                    Maximum = 50m,
                    Value = 3.00m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(tagOffsetEastNumericUpDown);

                tagOffsetNorthLabel = new System.Windows.Forms.Label
                {
                    Text = "North:",
                    Location = new System.Drawing.Point(174, 254),
                    Size = new System.Drawing.Size(50, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(tagOffsetNorthLabel);

                tagOffsetNorthNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(224, 252),
                    Size = new System.Drawing.Size(84, 25),
                    DecimalPlaces = 2,
                    Increment = 0.10m,
                    Minimum = -50m,
                    Maximum = 50m,
                    Value = 3.00m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(tagOffsetNorthNumericUpDown);

                proximityMaxDistLabel = new System.Windows.Forms.Label
                {
                    Text = "Max Match Dist (ft):",
                    Location = new System.Drawing.Point(14, 283),
                    Size = new System.Drawing.Size(130, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(proximityMaxDistLabel);

                proximityMaxDistNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(148, 281),
                    Size = new System.Drawing.Size(84, 25),
                    DecimalPlaces = 2,
                    Increment = 0.25m,
                    Minimum = 0.10m,
                    Maximum = 100m,
                    Value = 1.00m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(proximityMaxDistNumericUpDown);

                okButton = new System.Windows.Forms.Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(396, 624),
                    Size = new System.Drawing.Size(80, 32),
                    DialogResult = System.Windows.Forms.DialogResult.OK
                };
                Controls.Add(okButton);

                cancelButton = new System.Windows.Forms.Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(486, 624),
                    Size = new System.Drawing.Size(80, 32),
                    DialogResult = System.Windows.Forms.DialogResult.Cancel
                };
                Controls.Add(cancelButton);

                AcceptButton = okButton;
                CancelButton = cancelButton;

                categoryComboBox.SelectedIndexChanged += (sender, args) => UpdatePourVisibility();
                categoryComboBox.SelectedIndexChanged += (sender, args) => UpdateModeAvailability();
                placeRadioButton.CheckedChanged += (sender, args) => UpdateThresholdVisibility();
                exportRadioButton.CheckedChanged += (sender, args) => UpdateThresholdVisibility();
                importRadioButton.CheckedChanged += (sender, args) => UpdateThresholdVisibility();
                useHorizontalThresholdCheckBox.CheckedChanged += (sender, args) => UpdateThresholdEnableState();
                useElevationThresholdCheckBox.CheckedChanged += (sender, args) => UpdateThresholdEnableState();
                pairingModeComboBox.SelectedIndexChanged += (sender, args) => UpdateThresholdVisibility();
                okButton.Click += (sender, args) =>
                {
                    if (importRadioButton.Checked &&
                        !TryParseThresholdInput(horizontalVerifiedThresholdTextBox.Text, out _selectedHorizontalVerifiedThreshold))
                    {
                        TaskDialog.Show("RevitSuite", "Invalid N/E Verified threshold. Enter inches (decimal or fraction, for example: 0.125 or 1/8).");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }
                    else if (!importRadioButton.Checked)
                    {
                        TryParseThresholdInput(horizontalVerifiedThresholdTextBox.Text, out _selectedHorizontalVerifiedThreshold);
                    }
                    _selectedHorizontalVerifiedThreshold = InchesToFeet(_selectedHorizontalVerifiedThreshold);

                    if (importRadioButton.Checked &&
                        !TryParseThresholdInput(horizontalCriticalThresholdTextBox.Text, out _selectedHorizontalCriticalThreshold))
                    {
                        TaskDialog.Show("RevitSuite", "Invalid N/E Critical threshold. Enter inches (decimal or fraction, for example: 0.250 or 1/4).");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }
                    else if (!importRadioButton.Checked)
                    {
                        TryParseThresholdInput(horizontalCriticalThresholdTextBox.Text, out _selectedHorizontalCriticalThreshold);
                    }
                    _selectedHorizontalCriticalThreshold = InchesToFeet(_selectedHorizontalCriticalThreshold);

                    if (importRadioButton.Checked &&
                        !TryParseThresholdInput(elevationVerifiedThresholdTextBox.Text, out _selectedElevationVerifiedThreshold))
                    {
                        TaskDialog.Show("RevitSuite", "Invalid Elev Verified threshold. Enter inches (decimal or fraction, for example: 0.125 or 1/8).");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }
                    else if (!importRadioButton.Checked)
                    {
                        TryParseThresholdInput(elevationVerifiedThresholdTextBox.Text, out _selectedElevationVerifiedThreshold);
                    }
                    _selectedElevationVerifiedThreshold = InchesToFeet(_selectedElevationVerifiedThreshold);

                    if (importRadioButton.Checked &&
                        !TryParseThresholdInput(elevationCriticalThresholdTextBox.Text, out _selectedElevationCriticalThreshold))
                    {
                        TaskDialog.Show("RevitSuite", "Invalid Elev Critical threshold. Enter inches (decimal or fraction, for example: 0.250 or 1/4).");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }
                    else if (!importRadioButton.Checked)
                    {
                        TryParseThresholdInput(elevationCriticalThresholdTextBox.Text, out _selectedElevationCriticalThreshold);
                    }
                    _selectedElevationCriticalThreshold = InchesToFeet(_selectedElevationCriticalThreshold);

                    if (importRadioButton.Checked &&
                        !useHorizontalThresholdCheckBox.Checked &&
                        !useElevationThresholdCheckBox.Checked)
                    {
                        TaskDialog.Show("RevitSuite", "Enable at least one threshold check (N/E or Elevation).");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }

                    if (importRadioButton.Checked &&
                        useHorizontalThresholdCheckBox.Checked &&
                        SelectedHorizontalVerifiedThreshold > SelectedHorizontalCriticalThreshold)
                    {
                        TaskDialog.Show("RevitSuite", "For N/E, Verified threshold must be less than or equal to Critical threshold.");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }

                    if (importRadioButton.Checked &&
                        useElevationThresholdCheckBox.Checked &&
                        SelectedElevationVerifiedThreshold > SelectedElevationCriticalThreshold)
                    {
                        TaskDialog.Show("RevitSuite", "For Elevation, Verified threshold must be less than or equal to Critical threshold.");
                        this.DialogResult = System.Windows.Forms.DialogResult.None;
                    }
                };
                UpdatePourVisibility();
                UpdateModeAvailability();
                UpdateThresholdVisibility();
            }

            private void UpdatePourVisibility()
            {
                var showPour = string.Equals(categoryComboBox.SelectedItem?.ToString(), SogCategoryName, StringComparison.OrdinalIgnoreCase);
                pourLabel.Visible = showPour;
                pourNumericUpDown.Visible = showPour;
            }

            private void UpdateModeAvailability()
            {
                var isReadyPoints = string.Equals(categoryComboBox.SelectedItem?.ToString(), ReadyPointsCategoryName, StringComparison.OrdinalIgnoreCase);
                exportRadioButton.Enabled = !isReadyPoints;
                if (isReadyPoints && exportRadioButton.Checked)
                {
                    placeRadioButton.Checked = true;
                }
            }

            private void UpdateThresholdVisibility()
            {
                var showThresholds = importRadioButton != null && importRadioButton.Checked;
                if (thresholdGroupBox != null)
                {
                    thresholdGroupBox.Visible = showThresholds;
                }
                thresholdScopeLabel.Visible = showThresholds;
                thresholdScopeComboBox.Visible = showThresholds;
                pairingModeLabel.Visible = showThresholds;
                pairingModeComboBox.Visible = showThresholds;
                useHorizontalThresholdCheckBox.Visible = showThresholds;
                useElevationThresholdCheckBox.Visible = showThresholds;
                horizontalVerifiedThresholdLabel.Visible = showThresholds;
                horizontalVerifiedThresholdTextBox.Visible = showThresholds;
                horizontalCriticalThresholdLabel.Visible = showThresholds;
                horizontalCriticalThresholdTextBox.Visible = showThresholds;
                elevationVerifiedThresholdLabel.Visible = showThresholds;
                elevationVerifiedThresholdTextBox.Visible = showThresholds;
                elevationCriticalThresholdLabel.Visible = showThresholds;
                elevationCriticalThresholdTextBox.Visible = showThresholds;
                thresholdHelpLabel.Visible = showThresholds;
                tagPlacementLabel.Visible = showThresholds;
                tagOffsetEastLabel.Visible = showThresholds;
                tagOffsetEastNumericUpDown.Visible = showThresholds;
                tagOffsetNorthLabel.Visible = showThresholds;
                tagOffsetNorthNumericUpDown.Visible = showThresholds;
                var showProximityDist = showThresholds && pairingModeComboBox?.SelectedIndex == 1;
                proximityMaxDistLabel.Visible = showProximityDist;
                proximityMaxDistNumericUpDown.Visible = showProximityDist;
                UpdateThresholdEnableState();
            }

            private void UpdateThresholdEnableState()
            {
                if (useHorizontalThresholdCheckBox != null)
                {
                    var enabled = useHorizontalThresholdCheckBox.Checked;
                    if (horizontalVerifiedThresholdTextBox != null) horizontalVerifiedThresholdTextBox.Enabled = enabled;
                    if (horizontalCriticalThresholdTextBox != null) horizontalCriticalThresholdTextBox.Enabled = enabled;
                }

                if (useElevationThresholdCheckBox != null)
                {
                    var enabled = useElevationThresholdCheckBox.Checked;
                    if (elevationVerifiedThresholdTextBox != null) elevationVerifiedThresholdTextBox.Enabled = enabled;
                    if (elevationCriticalThresholdTextBox != null) elevationCriticalThresholdTextBox.Enabled = enabled;
                }
            }
        }

        private class PointThresholdSettings
        {
            public PointThresholdSettings(double horizontalThreshold, double elevationThreshold)
            {
                HorizontalThreshold = horizontalThreshold;
                ElevationThreshold = elevationThreshold;
            }

            public double HorizontalThreshold { get; }
            public double ElevationThreshold { get; }
        }

        private class CsvColumnMapping
        {
            public CsvColumnMapping(int pointNumberIndex, int northingIndex, int eastingIndex, int elevationIndex, int commentIndex)
            {
                PointNumberIndex = pointNumberIndex;
                NorthingIndex = northingIndex;
                EastingIndex = eastingIndex;
                ElevationIndex = elevationIndex;
                CommentIndex = commentIndex;
            }

            public int PointNumberIndex { get; }
            public int NorthingIndex { get; }
            public int EastingIndex { get; }
            public int ElevationIndex { get; }
            public int CommentIndex { get; }
        }

        private class CsvColumnMappingForm : System.Windows.Forms.Form
        {
            private class ColumnOption
            {
                public ColumnOption(int index, string label)
                {
                    Index = index;
                    Label = label;
                }

                public int Index { get; }
                public string Label { get; }

                public override string ToString() => Label;
            }

            private readonly System.Windows.Forms.ComboBox _pointNumberComboBox = new System.Windows.Forms.ComboBox();
            private readonly System.Windows.Forms.ComboBox _northingComboBox = new System.Windows.Forms.ComboBox();
            private readonly System.Windows.Forms.ComboBox _eastingComboBox = new System.Windows.Forms.ComboBox();
            private readonly System.Windows.Forms.ComboBox _elevationComboBox = new System.Windows.Forms.ComboBox();
            private readonly System.Windows.Forms.ComboBox _commentComboBox = new System.Windows.Forms.ComboBox();
            private readonly bool _requireElevation;
            private readonly bool _requirePointNumber;

            public CsvColumnMapping SelectedMapping { get; private set; }

            public CsvColumnMappingForm(
                string[] headers,
                CsvColumnMapping defaults,
                bool requireElevation,
                bool requirePointNumber,
                string title)
            {
                _requireElevation = requireElevation;
                _requirePointNumber = requirePointNumber;

                Text = string.IsNullOrWhiteSpace(title) ? "Map CSV Columns" : title;
                Size = new System.Drawing.Size(560, 350);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                var helpLabel = new Label
                {
                    Text = requirePointNumber
                        ? (requireElevation
                            ? "Required: Point Number, Northing, Easting, Elevation."
                            : "Required: Point Number, Northing, Easting. For N/E-only checks, set Elevation to <Not Used>.")
                        : (requireElevation
                            ? "Required: Northing, Easting, Elevation. Point Number can be <Not Used>."
                            : "Required: Northing, Easting. Point Number can be <Not Used>. Elevation can be <Not Used> for N/E-only checks."),
                    Location = new System.Drawing.Point(16, 14),
                    Size = new System.Drawing.Size(520, 20)
                };
                Controls.Add(helpLabel);

                AddMappingRow("Point Number:", _pointNumberComboBox, 50);
                AddMappingRow("Northing:", _northingComboBox, 90);
                AddMappingRow("Easting:", _eastingComboBox, 130);
                AddMappingRow("Elevation:", _elevationComboBox, 170);
                AddMappingRow("Comment:", _commentComboBox, 210);

                PopulateHeaderOptions(_pointNumberComboBox, headers, allowNone: !requirePointNumber);
                PopulateHeaderOptions(_northingComboBox, headers, allowNone: false);
                PopulateHeaderOptions(_eastingComboBox, headers, allowNone: false);
                PopulateHeaderOptions(_elevationComboBox, headers, allowNone: !requireElevation);
                PopulateHeaderOptions(_commentComboBox, headers, allowNone: true);

                SetSelectedIndex(_pointNumberComboBox, requirePointNumber ? (defaults?.PointNumberIndex ?? -1) : -1);
                SetSelectedIndex(_northingComboBox, defaults?.NorthingIndex ?? -1);
                SetSelectedIndex(_eastingComboBox, defaults?.EastingIndex ?? -1);
                SetSelectedIndex(_elevationComboBox, requireElevation ? (defaults?.ElevationIndex ?? -1) : -1);
                SetSelectedIndex(_commentComboBox, defaults?.CommentIndex ?? -1);

                var okButton = new Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(370, 265),
                    Size = new System.Drawing.Size(75, 28)
                };
                okButton.Click += OnOkClick;
                Controls.Add(okButton);

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(455, 265),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.Cancel
                };
                Controls.Add(cancelButton);

                AcceptButton = okButton;
                CancelButton = cancelButton;
            }

            private void AddMappingRow(string label, System.Windows.Forms.ComboBox comboBox, int y)
            {
                var textLabel = new Label
                {
                    Text = label,
                    Location = new System.Drawing.Point(16, y + 3),
                    Size = new System.Drawing.Size(110, 22)
                };
                Controls.Add(textLabel);

                comboBox.Location = new System.Drawing.Point(128, y);
                comboBox.Size = new System.Drawing.Size(402, 24);
                comboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
                Controls.Add(comboBox);
            }

            private static void PopulateHeaderOptions(System.Windows.Forms.ComboBox comboBox, string[] headers, bool allowNone)
            {
                if (allowNone)
                {
                    comboBox.Items.Add(new ColumnOption(-1, "<Not Used>"));
                }

                for (var i = 0; i < headers.Length; i++)
                {
                    comboBox.Items.Add(new ColumnOption(i, $"{i + 1}: {headers[i]}"));
                }

                if (comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                }
            }

            private static void SetSelectedIndex(System.Windows.Forms.ComboBox comboBox, int columnIndex)
            {
                for (var i = 0; i < comboBox.Items.Count; i++)
                {
                    if (comboBox.Items[i] is ColumnOption option && option.Index == columnIndex)
                    {
                        comboBox.SelectedIndex = i;
                        return;
                    }
                }
            }

            private void OnOkClick(object sender, EventArgs e)
            {
                var pointIndex = GetSelectedColumnIndex(_pointNumberComboBox);
                var northingIndex = GetSelectedColumnIndex(_northingComboBox);
                var eastingIndex = GetSelectedColumnIndex(_eastingComboBox);
                var elevationIndex = GetSelectedColumnIndex(_elevationComboBox);
                var commentIndex = GetSelectedColumnIndex(_commentComboBox);

                if ((_requirePointNumber && pointIndex < 0) || northingIndex < 0 || eastingIndex < 0)
                {
                    MessageBox.Show(this, _requirePointNumber
                        ? "Point Number, Northing, and Easting are required."
                        : "Northing and Easting are required.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_requireElevation && elevationIndex < 0)
                {
                    MessageBox.Show(this, "Elevation is required for this mode.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var usedIndices = new HashSet<int> { northingIndex, eastingIndex };
                if (pointIndex >= 0)
                {
                    usedIndices.Add(pointIndex);
                }
                if (_requireElevation && elevationIndex >= 0)
                {
                    usedIndices.Add(elevationIndex);
                }
                if (commentIndex >= 0)
                {
                    usedIndices.Add(commentIndex);
                }

                var expectedCount = 2 + (pointIndex >= 0 ? 1 : 0) + (_requireElevation ? 1 : 0) + (commentIndex >= 0 ? 1 : 0);
                if (usedIndices.Count != expectedCount)
                {
                    MessageBox.Show(this, "Each mapped field must use a different CSV column.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                SelectedMapping = new CsvColumnMapping(pointIndex, northingIndex, eastingIndex, elevationIndex, commentIndex);
                DialogResult = DialogResult.OK;
                Close();
            }

            private static int GetSelectedColumnIndex(System.Windows.Forms.ComboBox comboBox)
            {
                return comboBox.SelectedItem is ColumnOption option ? option.Index : -1;
            }
        }

        private class PointThresholdSelectionForm : System.Windows.Forms.Form
        {
            private readonly System.Windows.Forms.DataGridView _grid = new System.Windows.Forms.DataGridView();
            private readonly System.Windows.Forms.Button _okButton = new System.Windows.Forms.Button();
            private readonly System.Windows.Forms.Button _cancelButton = new System.Windows.Forms.Button();
            private readonly double _defaultHorizontalThreshold;
            private readonly double _defaultElevationThreshold;

            public Dictionary<string, PointThresholdSettings> SelectedThresholds { get; } =
                new Dictionary<string, PointThresholdSettings>(StringComparer.OrdinalIgnoreCase);

            public PointThresholdSelectionForm(
                IList<string> pointNumbers,
                double defaultHorizontalThreshold,
                double defaultElevationThreshold)
            {
                _defaultHorizontalThreshold = defaultHorizontalThreshold;
                _defaultElevationThreshold = defaultElevationThreshold;

                Text = "QAQC - Selected Point Thresholds";
                Size = new System.Drawing.Size(620, 520);
                StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                var descriptionLabel = new System.Windows.Forms.Label
                {
                    Text = "These selected model points match CSV Point Number values. Set thresholds per point in inches (fraction or decimal).",
                    Location = new System.Drawing.Point(12, 12),
                    Size = new System.Drawing.Size(580, 20)
                };
                Controls.Add(descriptionLabel);

                _grid.Location = new System.Drawing.Point(12, 40);
                _grid.Size = new System.Drawing.Size(580, 380);
                _grid.AllowUserToAddRows = false;
                _grid.AllowUserToDeleteRows = false;
                _grid.RowHeadersVisible = false;
                _grid.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
                _grid.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;

                var pointColumn = new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Point Number",
                    Name = "PointNumber",
                    ReadOnly = true,
                    FillWeight = 50
                };
                var horizontalColumn = new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "N/E Threshold (in)",
                    Name = "HorizontalThreshold",
                    FillWeight = 25
                };
                var elevationColumn = new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Elev Threshold (in)",
                    Name = "ElevationThreshold",
                    FillWeight = 25
                };

                _grid.Columns.AddRange(pointColumn, horizontalColumn, elevationColumn);

                foreach (var pointNumber in pointNumbers)
                {
                    _grid.Rows.Add(pointNumber, FormatFeetAsInchFraction(defaultHorizontalThreshold), FormatFeetAsInchFraction(defaultElevationThreshold));
                }

                Controls.Add(_grid);

                _okButton.Text = "OK";
                _okButton.Location = new System.Drawing.Point(420, 435);
                _okButton.Size = new System.Drawing.Size(80, 30);
                _okButton.Click += OnOkClick;
                Controls.Add(_okButton);

                _cancelButton.Text = "Cancel";
                _cancelButton.Location = new System.Drawing.Point(510, 435);
                _cancelButton.Size = new System.Drawing.Size(80, 30);
                _cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
                Controls.Add(_cancelButton);

                AcceptButton = _okButton;
                CancelButton = _cancelButton;
            }

            private void OnOkClick(object sender, EventArgs e)
            {
                SelectedThresholds.Clear();

                foreach (System.Windows.Forms.DataGridViewRow row in _grid.Rows)
                {
                    var pointNumber = row.Cells["PointNumber"].Value?.ToString();
                    if (string.IsNullOrWhiteSpace(pointNumber))
                    {
                        continue;
                    }

                    if (!TryParsePositiveThreshold(row.Cells["HorizontalThreshold"].Value, out var horizontalThreshold))
                    {
                        MessageBox.Show(this, $"Invalid N/E threshold for point '{pointNumber}'. Enter inches (fraction or decimal).", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }

                    if (!TryParsePositiveThreshold(row.Cells["ElevationThreshold"].Value, out var elevationThreshold))
                    {
                        MessageBox.Show(this, $"Invalid Elev threshold for point '{pointNumber}'. Enter inches (fraction or decimal).", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }

                    SelectedThresholds[pointNumber] = new PointThresholdSettings(horizontalThreshold, elevationThreshold);
                }

                DialogResult = System.Windows.Forms.DialogResult.OK;
                Close();
            }

            private static bool TryParsePositiveThreshold(object value, out double threshold)
            {
                threshold = 0;
                var text = value?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                if (!TryParseThresholdInput(text, out threshold) || threshold <= 0)
                {
                    return false;
                }

                threshold = InchesToFeet(threshold);
                return true;
            }
        }

        private class PointMatchPreviewForm : System.Windows.Forms.Form
        {
            public PointMatchPreviewForm(
                IList<ControlPointRecord> importedRecords,
                HashSet<string> selectedPointNumbers,
                HashSet<string> matchedPointNumbers)
            {
                Text = "QAQC - CSV Match Preview";
                Size = new System.Drawing.Size(760, 560);
                StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                var summaryLabel = new System.Windows.Forms.Label
                {
                    Text = $"Imported CSV points: {importedRecords.Count} | Selected set: {selectedPointNumbers.Count} | Matched by Point Number: {matchedPointNumbers.Count}",
                    Location = new System.Drawing.Point(12, 12),
                    Size = new System.Drawing.Size(730, 20)
                };
                Controls.Add(summaryLabel);

                var grid = new System.Windows.Forms.DataGridView
                {
                    Location = new System.Drawing.Point(12, 40),
                    Size = new System.Drawing.Size(730, 440),
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    ReadOnly = true,
                    RowHeadersVisible = false,
                    AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill
                };

                grid.Columns.Add(new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Point Number",
                    FillWeight = 30
                });
                grid.Columns.Add(new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Field Northing",
                    FillWeight = 20
                });
                grid.Columns.Add(new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Field Easting",
                    FillWeight = 20
                });
                grid.Columns.Add(new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Field Elevation",
                    FillWeight = 20
                });
                grid.Columns.Add(new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Match Status",
                    FillWeight = 20
                });

                foreach (var record in importedRecords.OrderBy(r => r.PointNumber, StringComparer.OrdinalIgnoreCase))
                {
                    var matched = matchedPointNumbers.Contains(record.PointNumber);
                    var status = matched ? "Matched" : "CSV Only";

                    var rowIndex = grid.Rows.Add(
                        record.PointNumber,
                        record.FieldNorthing?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
                        record.FieldEasting?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
                        record.FieldElevation?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
                        status);

                    if (matched)
                    {
                        grid.Rows[rowIndex].DefaultCellStyle.BackColor = System.Drawing.Color.Honeydew;
                    }
                }

                Controls.Add(grid);

                var continueButton = new System.Windows.Forms.Button
                {
                    Text = "Continue",
                    Location = new System.Drawing.Point(560, 495),
                    Size = new System.Drawing.Size(80, 30),
                    DialogResult = System.Windows.Forms.DialogResult.OK
                };
                Controls.Add(continueButton);

                var cancelButton = new System.Windows.Forms.Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(650, 495),
                    Size = new System.Drawing.Size(80, 30),
                    DialogResult = System.Windows.Forms.DialogResult.Cancel
                };
                Controls.Add(cancelButton);

                AcceptButton = continueButton;
                CancelButton = cancelButton;
            }
        }

        #endregion

    }
}
