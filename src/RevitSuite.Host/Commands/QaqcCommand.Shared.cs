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
            bool includeHorizontalAnnotations,
            bool includeElevationAnnotations,
            string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend || view.ViewType == ViewType.ThreeD)
            {
                LogManager.Warn(correlationId, "Active view not suitable for text notes - skipping annotations. Use a plan, section, or elevation view.");
                return;
            }

            if (!includeHorizontalAnnotations && !includeElevationAnnotations)
            {
                LogManager.Info(correlationId, "No annotation components selected (N/E and Elevation both disabled).");
                return;
            }

            // Delete all existing text notes in this view to prevent duplicates
            try
            {
                var existingTextNotes = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote))
                    .ToList();

                int deletedCount = 0;
                foreach (var textNote in existingTextNotes)
                {
                    try
                    {
                        doc.Delete(textNote.Id);
                        deletedCount++;
                    }
                    catch
                    {
                        // Some text notes might be locked or system-owned, skip them
                    }
                }

                LogManager.Info(correlationId, $"Deleted {deletedCount} existing annotations in current view.");
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to delete old annotations: {ex.Message}");
            }

            // Delete all existing detail lines in this view (leader lines)
            try
            {
                var existingDetailLines = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(CurveElement))
                    .OfType<DetailCurve>()
                    .ToList();

                int deletedLines = 0;
                foreach (var line in existingDetailLines)
                {
                    try
                    {
                        doc.Delete(line.Id);
                        deletedLines++;
                    }
                    catch
                    {
                        // Skip locked elements
                    }
                }

                LogManager.Info(correlationId, $"Deleted {deletedLines} existing detail lines in current view.");
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to delete old detail lines: {ex.Message}");
            }

            // Get a valid TextNoteType from the document
            var textNoteType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .FirstOrDefault() as TextNoteType;

            if (textNoteType == null)
            {
                LogManager.Warn(correlationId, "No TextNoteType found in document - skipping annotations.");
                return;
            }

            LogManager.Info(correlationId, $"Using TextNoteType: {textNoteType.Name}");

            int annotationsCreated = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    var annotationLines = new List<string>();
                    if (includeHorizontalAnnotations)
                    {
                        // Convert deviations to feet-inches format (Revit standard)
                        var eastingFtIn = FormatFeetInches(Math.Abs(deviation.DeviationEasting));
                        var northingFtIn = FormatFeetInches(Math.Abs(deviation.DeviationNorthing));

                        // Add +/- signs
                        var eastingSign = deviation.DeviationEasting >= 0 ? "+" : "-";
                        var northingSign = deviation.DeviationNorthing >= 0 ? "+" : "-";
                        annotationLines.Add($"E: {eastingSign}{eastingFtIn}");
                        annotationLines.Add($"N: {northingSign}{northingFtIn}");
                    }

                    if (includeElevationAnnotations)
                    {
                        var elevationFtIn = FormatFeetInches(Math.Abs(deviation.DeviationElevation));
                        var elevationSign = deviation.DeviationElevation >= 0 ? "+" : "-";
                        annotationLines.Add($"Z: {elevationSign}{elevationFtIn}");
                    }

                    var annotationText = string.Join("\n", annotationLines);

                    // Offset annotation point slightly to the right and up from Control Point
                    var annotationPoint = new XYZ(
                        deviation.ModelPoint.X + 2.0,  // 2 feet to the right
                        deviation.ModelPoint.Y + 1.0,  // 1 foot up
                        deviation.ModelPoint.Z);

                    var textNote = TextNote.Create(doc, view.Id, annotationPoint, annotationText, textNoteType.Id);

                    if (textNote != null)
                    {
                        // Create a detail line from annotation to model point as a leader
                        try
                        {
                            var leaderLine = Line.CreateBound(annotationPoint, deviation.ModelPoint);
                            doc.Create.NewDetailCurve(view, leaderLine);
                        }
                        catch
                        {
                            // Leader line creation might fail, continue anyway
                        }

                        annotationsCreated++;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create annotation for Point {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Created {annotationsCreated} deviation annotations.");
        }

        private string FormatFeetInches(double feet)
        {
            // Convert feet to feet and inches
            int wholeFeet = (int)Math.Floor(feet);
            double remainingInches = (feet - wholeFeet) * 12.0;

            // Round to nearest 1/8 inch
            double eighths = Math.Round(remainingInches * 8.0);
            int wholeInches = (int)(eighths / 8.0);
            int fractionalEighths = (int)(eighths % 8);

            // Build string
            if (wholeFeet == 0 && wholeInches == 0 && fractionalEighths == 0)
                return "0\"";

            var result = "";
            if (wholeFeet > 0)
                result += $"{wholeFeet}'-";

            if (wholeInches > 0 || wholeFeet > 0)
                result += $"{wholeInches}";

            // Add fraction if needed
            if (fractionalEighths > 0)
            {
                // Simplify fraction
                var (num, den) = SimplifyFraction(fractionalEighths, 8);
                result += $" {num}/{den}";
            }

            result += "\"";

            return result.Replace("'-0\"", "'"); // Clean up cases like "1'-0"" to "1'"
        }

        private (int numerator, int denominator) SimplifyFraction(int num, int den)
        {
            // Simplify fraction (e.g., 4/8 -> 1/2)
            int gcd = GCD(num, den);
            return (num / gcd, den / gcd);
        }

        private int GCD(int a, int b)
        {
            while (b != 0)
            {
                int temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        #region Nested Classes

        private enum QaqcMode
        {
            Place,
            Export,
            ImportAndAnalyze
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
            public string UniqueId { get; set; }
            public double DeviationEasting { get; set; }
            public double DeviationNorthing { get; set; }
            public double DeviationElevation { get; set; }
            public double HorizontalDeviation { get; set; }
            public double TotalDeviation { get; set; }
            public ToleranceStatus Status { get; set; }
            public XYZ ModelPoint { get; set; }
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
                       elem.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors;
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
            private System.Windows.Forms.NumericUpDown horizontalVerifiedThresholdNumericUpDown;
            private System.Windows.Forms.NumericUpDown horizontalCriticalThresholdNumericUpDown;
            private System.Windows.Forms.NumericUpDown elevationVerifiedThresholdNumericUpDown;
            private System.Windows.Forms.NumericUpDown elevationCriticalThresholdNumericUpDown;
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

            public string SelectedCategory => categoryComboBox.SelectedItem?.ToString() ?? "Footings";
            public int SelectedPourNumber => (int)(pourNumericUpDown?.Value ?? 1);
            public double SelectedHorizontalVerifiedThreshold => (double)(horizontalVerifiedThresholdNumericUpDown?.Value ?? 0.01m);
            public double SelectedHorizontalCriticalThreshold => (double)(horizontalCriticalThresholdNumericUpDown?.Value ?? 0.05m);
            public double SelectedElevationVerifiedThreshold => (double)(elevationVerifiedThresholdNumericUpDown?.Value ?? 0.01m);
            public double SelectedElevationCriticalThreshold => (double)(elevationCriticalThresholdNumericUpDown?.Value ?? 0.05m);
            public bool SelectedUseHorizontalThreshold => useHorizontalThresholdCheckBox?.Checked ?? true;
            public bool SelectedUseElevationThreshold => useElevationThresholdCheckBox?.Checked ?? true;
            public bool SelectedUseSelectedPointThresholds => thresholdScopeComboBox?.SelectedIndex == 1;
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
                Size = new System.Drawing.Size(600, 660);
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
                    Location = new System.Drawing.Point(16, 64),
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
                    Location = new System.Drawing.Point(16, 164),
                    Size = new System.Drawing.Size(550, 118)
                };
                Controls.Add(modeGroupBox);

                placeRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Place Control Points",
                    Location = new System.Drawing.Point(18, 28),
                    Size = new System.Drawing.Size(220, 25),
                    Checked = true,
                    Tag = QaqcMode.Place
                };
                modeGroupBox.Controls.Add(placeRadioButton);

                exportRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Export Model Points",
                    Location = new System.Drawing.Point(18, 55),
                    Size = new System.Drawing.Size(220, 25),
                    Tag = QaqcMode.Export
                };
                modeGroupBox.Controls.Add(exportRadioButton);

                importRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Import && Analyze Field Data",
                    Location = new System.Drawing.Point(18, 82),
                    Size = new System.Drawing.Size(240, 25),
                    Tag = QaqcMode.ImportAndAnalyze
                };
                modeGroupBox.Controls.Add(importRadioButton);

                thresholdGroupBox = new System.Windows.Forms.GroupBox
                {
                    Text = "Thresholds (Import Mode)",
                    Location = new System.Drawing.Point(16, 290),
                    Size = new System.Drawing.Size(550, 270),
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
                    Text = "N/E Verified <= (ft):",
                    Location = new System.Drawing.Point(32, 88),
                    Size = new System.Drawing.Size(130, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalVerifiedThresholdLabel);

                horizontalVerifiedThresholdNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(170, 86),
                    Size = new System.Drawing.Size(100, 25),
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.001m,
                    Maximum = 10m,
                    Value = 0.010m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalVerifiedThresholdNumericUpDown);

                horizontalCriticalThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "N/E Critical > (ft):",
                    Location = new System.Drawing.Point(292, 88),
                    Size = new System.Drawing.Size(120, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalCriticalThresholdLabel);

                horizontalCriticalThresholdNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(418, 86),
                    Size = new System.Drawing.Size(100, 25),
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.001m,
                    Maximum = 10m,
                    Value = 0.050m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(horizontalCriticalThresholdNumericUpDown);

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
                    Text = "Elev Verified <= (ft):",
                    Location = new System.Drawing.Point(32, 152),
                    Size = new System.Drawing.Size(130, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationVerifiedThresholdLabel);

                elevationVerifiedThresholdNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(170, 150),
                    Size = new System.Drawing.Size(100, 25),
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.001m,
                    Maximum = 10m,
                    Value = 0.010m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationVerifiedThresholdNumericUpDown);

                elevationCriticalThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "Elev Critical > (ft):",
                    Location = new System.Drawing.Point(292, 152),
                    Size = new System.Drawing.Size(120, 20),
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationCriticalThresholdLabel);

                elevationCriticalThresholdNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(418, 150),
                    Size = new System.Drawing.Size(100, 25),
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.001m,
                    Maximum = 10m,
                    Value = 0.050m,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(elevationCriticalThresholdNumericUpDown);

                thresholdHelpLabel = new System.Windows.Forms.Label
                {
                    Text = "Per enabled check: <= Verified => Verified (Blue), > Critical => Critical (Red), otherwise Deviation (Orange).",
                    Location = new System.Drawing.Point(14, 194),
                    Size = new System.Drawing.Size(510, 36),
                    ForeColor = System.Drawing.Color.DimGray,
                    Visible = false
                };
                thresholdGroupBox.Controls.Add(thresholdHelpLabel);

                okButton = new System.Windows.Forms.Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(396, 578),
                    Size = new System.Drawing.Size(80, 32),
                    DialogResult = System.Windows.Forms.DialogResult.OK
                };
                Controls.Add(okButton);

                cancelButton = new System.Windows.Forms.Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(486, 578),
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
                okButton.Click += (sender, args) =>
                {
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
                useHorizontalThresholdCheckBox.Visible = showThresholds;
                useElevationThresholdCheckBox.Visible = showThresholds;
                horizontalVerifiedThresholdLabel.Visible = showThresholds;
                horizontalVerifiedThresholdNumericUpDown.Visible = showThresholds;
                horizontalCriticalThresholdLabel.Visible = showThresholds;
                horizontalCriticalThresholdNumericUpDown.Visible = showThresholds;
                elevationVerifiedThresholdLabel.Visible = showThresholds;
                elevationVerifiedThresholdNumericUpDown.Visible = showThresholds;
                elevationCriticalThresholdLabel.Visible = showThresholds;
                elevationCriticalThresholdNumericUpDown.Visible = showThresholds;
                thresholdHelpLabel.Visible = showThresholds;
                UpdateThresholdEnableState();
            }

            private void UpdateThresholdEnableState()
            {
                if (useHorizontalThresholdCheckBox != null)
                {
                    var enabled = useHorizontalThresholdCheckBox.Checked;
                    if (horizontalVerifiedThresholdNumericUpDown != null) horizontalVerifiedThresholdNumericUpDown.Enabled = enabled;
                    if (horizontalCriticalThresholdNumericUpDown != null) horizontalCriticalThresholdNumericUpDown.Enabled = enabled;
                }

                if (useElevationThresholdCheckBox != null)
                {
                    var enabled = useElevationThresholdCheckBox.Checked;
                    if (elevationVerifiedThresholdNumericUpDown != null) elevationVerifiedThresholdNumericUpDown.Enabled = enabled;
                    if (elevationCriticalThresholdNumericUpDown != null) elevationCriticalThresholdNumericUpDown.Enabled = enabled;
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
            public CsvColumnMapping(int pointNumberIndex, int northingIndex, int eastingIndex, int elevationIndex)
            {
                PointNumberIndex = pointNumberIndex;
                NorthingIndex = northingIndex;
                EastingIndex = eastingIndex;
                ElevationIndex = elevationIndex;
            }

            public int PointNumberIndex { get; }
            public int NorthingIndex { get; }
            public int EastingIndex { get; }
            public int ElevationIndex { get; }
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
            private readonly bool _requireElevation;

            public CsvColumnMapping SelectedMapping { get; private set; }

            public CsvColumnMappingForm(
                string[] headers,
                CsvColumnMapping defaults,
                bool requireElevation,
                string title)
            {
                _requireElevation = requireElevation;

                Text = string.IsNullOrWhiteSpace(title) ? "Map CSV Columns" : title;
                Size = new System.Drawing.Size(560, 310);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                var helpLabel = new Label
                {
                    Text = requireElevation
                        ? "Required: Point Number, Northing, Easting, Elevation."
                        : "Required: Point Number, Northing, Easting. For N/E-only checks, set Elevation to <Not Used>.",
                    Location = new System.Drawing.Point(16, 14),
                    Size = new System.Drawing.Size(520, 20)
                };
                Controls.Add(helpLabel);

                AddMappingRow("Point Number:", _pointNumberComboBox, 50);
                AddMappingRow("Northing:", _northingComboBox, 90);
                AddMappingRow("Easting:", _eastingComboBox, 130);
                AddMappingRow("Elevation:", _elevationComboBox, 170);

                PopulateHeaderOptions(_pointNumberComboBox, headers, allowNone: false);
                PopulateHeaderOptions(_northingComboBox, headers, allowNone: false);
                PopulateHeaderOptions(_eastingComboBox, headers, allowNone: false);
                PopulateHeaderOptions(_elevationComboBox, headers, allowNone: !requireElevation);

                SetSelectedIndex(_pointNumberComboBox, defaults?.PointNumberIndex ?? -1);
                SetSelectedIndex(_northingComboBox, defaults?.NorthingIndex ?? -1);
                SetSelectedIndex(_eastingComboBox, defaults?.EastingIndex ?? -1);
                SetSelectedIndex(_elevationComboBox, requireElevation ? (defaults?.ElevationIndex ?? -1) : -1);

                var okButton = new Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(370, 225),
                    Size = new System.Drawing.Size(75, 28)
                };
                okButton.Click += OnOkClick;
                Controls.Add(okButton);

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(455, 225),
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

                if (pointIndex < 0 || northingIndex < 0 || eastingIndex < 0)
                {
                    MessageBox.Show(this, "Point Number, Northing, and Easting are required.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_requireElevation && elevationIndex < 0)
                {
                    MessageBox.Show(this, "Elevation is required for this mode.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var usedIndices = new HashSet<int> { pointIndex, northingIndex, eastingIndex };
                if (_requireElevation && elevationIndex >= 0)
                {
                    usedIndices.Add(elevationIndex);
                }

                var expectedCount = _requireElevation ? 4 : 3;
                if (usedIndices.Count != expectedCount)
                {
                    MessageBox.Show(this, "Each mapped field must use a different CSV column.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                SelectedMapping = new CsvColumnMapping(pointIndex, northingIndex, eastingIndex, elevationIndex);
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
                    Text = "These selected model points match CSV Point Number values. Set thresholds per point.",
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
                    HeaderText = "N/E Threshold (ft)",
                    Name = "HorizontalThreshold",
                    FillWeight = 25
                };
                var elevationColumn = new System.Windows.Forms.DataGridViewTextBoxColumn
                {
                    HeaderText = "Elev Threshold (ft)",
                    Name = "ElevationThreshold",
                    FillWeight = 25
                };

                _grid.Columns.AddRange(pointColumn, horizontalColumn, elevationColumn);

                foreach (var pointNumber in pointNumbers)
                {
                    _grid.Rows.Add(pointNumber, defaultHorizontalThreshold.ToString("F3", CultureInfo.InvariantCulture), defaultElevationThreshold.ToString("F3", CultureInfo.InvariantCulture));
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
                        MessageBox.Show(this, $"Invalid N/E threshold for point '{pointNumber}'.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = System.Windows.Forms.DialogResult.None;
                        return;
                    }

                    if (!TryParsePositiveThreshold(row.Cells["ElevationThreshold"].Value, out var elevationThreshold))
                    {
                        MessageBox.Show(this, $"Invalid Elev threshold for point '{pointNumber}'.", "RevitSuite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold))
                {
                    return false;
                }

                return threshold > 0;
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
