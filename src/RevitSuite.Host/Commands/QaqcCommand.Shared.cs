using System;
using System.Collections.Generic;
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

        private void CreateDeviationAnnotations(Document doc, List<DeviationResult> deviations, string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend || view.ViewType == ViewType.ThreeD)
            {
                LogManager.Warn(correlationId, "Active view not suitable for text notes - skipping annotations. Use a plan, section, or elevation view.");
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

            // Get crop region bounds for filtering
            var cropBox = view.CropBox;
            var hasCropRegion = cropBox != null;
            var minCorner = hasCropRegion ? cropBox.Min : null;
            var maxCorner = hasCropRegion ? cropBox.Max : null;

            LogManager.Info(correlationId, $"Using TextNoteType: {textNoteType.Name}");

            int annotationsCreated = 0;
            int skippedOutsideCrop = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                // Skip points outside crop region
                if (hasCropRegion && !IsPointInCropRegion(deviation.ModelPoint, minCorner, maxCorner))
                {
                    skippedOutsideCrop++;
                    continue;
                }

                try
                {
                    // Convert deviations to feet-inches format (Revit standard)
                    var eastingFtIn = FormatFeetInches(Math.Abs(deviation.DeviationEasting));
                    var northingFtIn = FormatFeetInches(Math.Abs(deviation.DeviationNorthing));

                    // Add +/- signs
                    var eastingSign = deviation.DeviationEasting >= 0 ? "+" : "-";
                    var northingSign = deviation.DeviationNorthing >= 0 ? "+" : "-";

                    // Simple format: E and N deviations only (no point number)
                    var annotationText = $"E: {eastingSign}{eastingFtIn}\n" +
                        $"N: {northingSign}{northingFtIn}";

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

            LogManager.Info(correlationId, $"Created {annotationsCreated} annotations. Skipped {skippedOutsideCrop} points outside crop region.");
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
            private System.Windows.Forms.NumericUpDown horizontalThresholdNumericUpDown;
            private System.Windows.Forms.NumericUpDown elevationThresholdNumericUpDown;
            private System.Windows.Forms.Label horizontalThresholdLabel;
            private System.Windows.Forms.Label elevationThresholdLabel;
            private System.Windows.Forms.CheckBox useHorizontalThresholdCheckBox;
            private System.Windows.Forms.CheckBox useElevationThresholdCheckBox;
            private System.Windows.Forms.Label thresholdHelpLabel;

            public string SelectedCategory => categoryComboBox.SelectedItem?.ToString() ?? "Footings";
            public int SelectedPourNumber => (int)(pourNumericUpDown?.Value ?? 1);
            public double SelectedHorizontalThreshold => (double)(horizontalThresholdNumericUpDown?.Value ?? 0.05m);
            public double SelectedElevationThreshold => (double)(elevationThresholdNumericUpDown?.Value ?? 0.05m);
            public bool SelectedUseHorizontalThreshold => useHorizontalThresholdCheckBox?.Checked ?? true;
            public bool SelectedUseElevationThreshold => useElevationThresholdCheckBox?.Checked ?? true;
            public QaqcMode SelectedMode
            {
                get
                {
                    foreach (System.Windows.Forms.Control control in this.Controls)
                    {
                        if (control is System.Windows.Forms.RadioButton rb && rb.Checked && rb.Tag is QaqcMode mode)
                            return mode;
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
                this.Text = "QAQC - Control Point Verification";
                this.Size = new System.Drawing.Size(440, 430);
                this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                this.MaximizeBox = false;
                this.MinimizeBox = false;

                var categoryLabel = new System.Windows.Forms.Label
                {
                    Text = "Category:",
                    Location = new System.Drawing.Point(20, 20),
                    Size = new System.Drawing.Size(100, 20)
                };
                this.Controls.Add(categoryLabel);

                categoryComboBox = new System.Windows.Forms.ComboBox
                {
                    Location = new System.Drawing.Point(120, 18),
                    Size = new System.Drawing.Size(240, 25),
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
                };
                categoryComboBox.Items.AddRange(new object[] { "Footings", "Columns", "Walls", SogCategoryName });
                categoryComboBox.SelectedIndex = 0;
                this.Controls.Add(categoryComboBox);

                pourLabel = new System.Windows.Forms.Label
                {
                    Text = "Pour #:",
                    Location = new System.Drawing.Point(20, 50),
                    Size = new System.Drawing.Size(100, 20),
                    Visible = false
                };
                this.Controls.Add(pourLabel);

                pourNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(120, 48),
                    Size = new System.Drawing.Size(80, 25),
                    Minimum = 1,
                    Maximum = 99,
                    Value = 1,
                    Visible = false
                };
                this.Controls.Add(pourNumericUpDown);

                var modeLabel = new System.Windows.Forms.Label
                {
                    Text = "Mode:",
                    Location = new System.Drawing.Point(20, 85),
                    Size = new System.Drawing.Size(100, 20)
                };
                this.Controls.Add(modeLabel);

                placeRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Place Control Points",
                    Location = new System.Drawing.Point(120, 85),
                    Size = new System.Drawing.Size(240, 25),
                    Checked = true,
                    Tag = QaqcMode.Place
                };
                this.Controls.Add(placeRadioButton);

                exportRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Export Model Points",
                    Location = new System.Drawing.Point(120, 110),
                    Size = new System.Drawing.Size(240, 25),
                    Tag = QaqcMode.Export
                };
                this.Controls.Add(exportRadioButton);

                importRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Import && Analyze Field Data",
                    Location = new System.Drawing.Point(120, 135),
                    Size = new System.Drawing.Size(240, 25),
                    Tag = QaqcMode.ImportAndAnalyze
                };
                this.Controls.Add(importRadioButton);

                horizontalThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "N/E Threshold (ft):",
                    Location = new System.Drawing.Point(20, 202),
                    Size = new System.Drawing.Size(100, 20),
                    Visible = false
                };
                this.Controls.Add(horizontalThresholdLabel);

                useHorizontalThresholdCheckBox = new System.Windows.Forms.CheckBox
                {
                    Text = "Check horizontal (N/E)",
                    Location = new System.Drawing.Point(120, 174),
                    Size = new System.Drawing.Size(170, 24),
                    Checked = true,
                    Visible = false
                };
                this.Controls.Add(useHorizontalThresholdCheckBox);

                horizontalThresholdNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(120, 200),
                    Size = new System.Drawing.Size(100, 25),
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.001m,
                    Maximum = 10m,
                    Value = 0.050m,
                    Visible = false
                };
                this.Controls.Add(horizontalThresholdNumericUpDown);

                elevationThresholdLabel = new System.Windows.Forms.Label
                {
                    Text = "Elev Threshold (ft):",
                    Location = new System.Drawing.Point(20, 256),
                    Size = new System.Drawing.Size(100, 20),
                    Visible = false
                };
                this.Controls.Add(elevationThresholdLabel);

                useElevationThresholdCheckBox = new System.Windows.Forms.CheckBox
                {
                    Text = "Check elevation",
                    Location = new System.Drawing.Point(120, 228),
                    Size = new System.Drawing.Size(170, 24),
                    Checked = true,
                    Visible = false
                };
                this.Controls.Add(useElevationThresholdCheckBox);

                elevationThresholdNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(120, 254),
                    Size = new System.Drawing.Size(100, 25),
                    DecimalPlaces = 3,
                    Increment = 0.005m,
                    Minimum = 0.001m,
                    Maximum = 10m,
                    Value = 0.050m,
                    Visible = false
                };
                this.Controls.Add(elevationThresholdNumericUpDown);

                thresholdHelpLabel = new System.Windows.Forms.Label
                {
                    Text = "A point is Critical if any enabled check exceeds threshold.\nDisable one check to evaluate only the other.",
                    Location = new System.Drawing.Point(20, 288),
                    Size = new System.Drawing.Size(390, 40),
                    Visible = false
                };
                this.Controls.Add(thresholdHelpLabel);

                okButton = new System.Windows.Forms.Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(210, 340),
                    Size = new System.Drawing.Size(80, 30),
                    DialogResult = System.Windows.Forms.DialogResult.OK
                };
                this.Controls.Add(okButton);

                cancelButton = new System.Windows.Forms.Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(300, 340),
                    Size = new System.Drawing.Size(80, 30),
                    DialogResult = System.Windows.Forms.DialogResult.Cancel
                };
                this.Controls.Add(cancelButton);

                this.AcceptButton = okButton;
                this.CancelButton = cancelButton;

                categoryComboBox.SelectedIndexChanged += (sender, args) => UpdatePourVisibility();
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
                    }
                };
                UpdatePourVisibility();
                UpdateThresholdVisibility();
            }

            private void UpdatePourVisibility()
            {
                var showPour = string.Equals(categoryComboBox.SelectedItem?.ToString(), SogCategoryName, StringComparison.OrdinalIgnoreCase);
                pourLabel.Visible = showPour;
                pourNumericUpDown.Visible = showPour;
            }

            private void UpdateThresholdVisibility()
            {
                var showThresholds = importRadioButton != null && importRadioButton.Checked;
                useHorizontalThresholdCheckBox.Visible = showThresholds;
                useElevationThresholdCheckBox.Visible = showThresholds;
                horizontalThresholdLabel.Visible = showThresholds;
                horizontalThresholdNumericUpDown.Visible = showThresholds;
                elevationThresholdLabel.Visible = showThresholds;
                elevationThresholdNumericUpDown.Visible = showThresholds;
                thresholdHelpLabel.Visible = showThresholds;
                UpdateThresholdEnableState();
            }

            private void UpdateThresholdEnableState()
            {
                if (horizontalThresholdNumericUpDown != null && useHorizontalThresholdCheckBox != null)
                {
                    horizontalThresholdNumericUpDown.Enabled = useHorizontalThresholdCheckBox.Checked;
                }

                if (elevationThresholdNumericUpDown != null && useElevationThresholdCheckBox != null)
                {
                    elevationThresholdNumericUpDown.Enabled = useElevationThresholdCheckBox.Checked;
                }
            }
        }

        #endregion

    }
}
