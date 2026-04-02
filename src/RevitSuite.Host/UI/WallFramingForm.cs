using System;
using System.Drawing;
using System.Globalization;
using WinForms = System.Windows.Forms;

namespace RevitSuite.Host.UI
{
    internal enum WallFramingDepthSource
    {
        Auto,
        OverallWallThickness,
        StructuralLayerOnly,
        Manual
    }

    internal enum WallFramingManualReference
    {
        ExteriorFaceOfWall,
        InteriorFaceOfWall,
        WallCenterline
    }

    internal sealed class WallFramingOptions
    {
        public bool FrameWall { get; set; }
        public bool FrameOpenings { get; set; }
        public WallFramingDepthSource DepthSource { get; set; }
        public double ManualDepthFeet { get; set; }
        public WallFramingManualReference ManualReference { get; set; }
        public double ManualInsetFeet { get; set; }
    }

    internal class WallFramingForm : WinForms.Form
    {
        private readonly WinForms.CheckBox _frameWallCheck = new()
        {
            Text = "Frame full wall runs with OFW_STR_Wall-Framing",
            AutoSize = true,
            Checked = true,
            MaximumSize = new Size(420, 0)
        };

        private readonly WinForms.CheckBox _frameOpeningsCheck = new()
        {
            Text = "Frame hosted doors and windows",
            AutoSize = true,
            Checked = true,
            MaximumSize = new Size(420, 0)
        };

        private readonly WinForms.ComboBox _depthSourceCombo = new()
        {
            DropDownStyle = WinForms.ComboBoxStyle.DropDownList
        };

        private readonly WinForms.ComboBox _manualReferenceCombo = new()
        {
            DropDownStyle = WinForms.ComboBoxStyle.DropDownList
        };

        private readonly WinForms.TextBox _manualDepthBox = new();
        private readonly WinForms.TextBox _manualInsetBox = new() { Text = "0" };
        private readonly WinForms.Button _okButton = new() { Text = "OK", Width = 90 };
        private readonly WinForms.Button _cancelButton = new() { Text = "Cancel", Width = 90 };

        public WallFramingOptions Options { get; private set; } = null!;

        public WallFramingForm()
        {
            Text = "Wall Framing";
            StartPosition = WinForms.FormStartPosition.CenterParent;
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = WinForms.AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9F);
            Padding = new WinForms.Padding(12);
            AutoSize = true;
            AutoSizeMode = WinForms.AutoSizeMode.GrowAndShrink;

            _depthSourceCombo.Items.AddRange(new object[]
            {
                "Auto (structural layer, else wall thickness)",
                "Overall wall thickness",
                "Structural layer only",
                "Manual"
            });
            _depthSourceCombo.SelectedIndex = 0;
            _depthSourceCombo.SelectedIndexChanged += (_, _) => UpdateManualDepthState();

            _manualReferenceCombo.Items.AddRange(new object[]
            {
                "Exterior face of wall",
                "Interior face of wall",
                "Wall centerline"
            });
            _manualReferenceCombo.SelectedIndex = 0;

            _okButton.DialogResult = WinForms.DialogResult.OK;
            _okButton.Click += OnOkClick;
            _cancelButton.DialogResult = WinForms.DialogResult.Cancel;

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            var scopeGroup = BuildScopeGroup();
            var depthGroup = BuildDepthGroup();

            var buttonPanel = new WinForms.FlowLayoutPanel
            {
                FlowDirection = WinForms.FlowDirection.RightToLeft,
                Dock = WinForms.DockStyle.Bottom,
                AutoSize = true,
                Padding = new WinForms.Padding(0, 12, 0, 0)
            };
            buttonPanel.Controls.Add(_okButton);
            buttonPanel.Controls.Add(_cancelButton);

            var container = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                AutoSize = true
            };
            container.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.AutoSize));
            container.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.AutoSize));
            container.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.AutoSize));

            container.Controls.Add(scopeGroup, 0, 0);
            container.Controls.Add(depthGroup, 0, 1);
            container.Controls.Add(buttonPanel, 0, 2);

            Controls.Add(container);
            UpdateManualDepthState();
        }

        private WinForms.GroupBox BuildScopeGroup()
        {
            var group = new WinForms.GroupBox
            {
                Text = "Scope",
                Dock = WinForms.DockStyle.Fill,
                AutoSize = true
            };

            var layout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 1,
                AutoSize = true
            };

            var info = new WinForms.Label
            {
                Text = "The command uses currently selected walls when available. If nothing is selected, it will prompt you to pick walls.",
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Margin = new WinForms.Padding(10, 8, 10, 4)
            };

            _frameWallCheck.Margin = new WinForms.Padding(12, 6, 10, 0);
            _frameOpeningsCheck.Margin = new WinForms.Padding(12, 2, 10, 10);

            layout.Controls.Add(info);
            layout.Controls.Add(_frameWallCheck);
            layout.Controls.Add(_frameOpeningsCheck);
            group.Controls.Add(layout);
            return group;
        }

        private WinForms.GroupBox BuildDepthGroup()
        {
            var group = new WinForms.GroupBox
            {
                Text = "Depth",
                Dock = WinForms.DockStyle.Fill,
                AutoSize = true
            };

            var layout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                AutoSize = true
            };
            layout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 45));
            layout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 55));

            AddLabeledRow(layout, "Depth source:", _depthSourceCombo, 0);
            AddLabeledRow(layout, "Manual depth (in):", _manualDepthBox, 1);
            AddLabeledRow(layout, "Manual reference:", _manualReferenceCombo, 2);
            AddLabeledRow(layout, "Manual inset (in):", _manualInsetBox, 3);

            var note = new WinForms.Label
            {
                Text = "Manual mode reuses the same placement math as auto mode, but treats the entered depth and reference as a virtual framing layer inside the wall.",
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Margin = new WinForms.Padding(0, 6, 0, 0)
            };
            layout.Controls.Add(note, 0, 4);
            layout.SetColumnSpan(note, 2);

            group.Controls.Add(layout);
            return group;
        }

        private void AddLabeledRow(WinForms.TableLayoutPanel layout, string labelText, WinForms.Control input, int row)
        {
            var label = new WinForms.Label
            {
                Text = labelText,
                AutoSize = true,
                Dock = WinForms.DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new WinForms.Padding(0, row == 0 ? 0 : 6, 6, 0)
            };

            input.Dock = WinForms.DockStyle.Fill;
            input.Margin = new WinForms.Padding(0, row == 0 ? 0 : 6, 0, 0);

            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        private void UpdateManualDepthState()
        {
            var enabled = _depthSourceCombo.SelectedIndex == (int) WallFramingDepthSource.Manual;
            _manualDepthBox.Enabled = enabled;
            _manualReferenceCombo.Enabled = enabled;
            _manualInsetBox.Enabled = enabled;
        }

        private void OnOkClick(object? sender, EventArgs e)
        {
            if (!_frameWallCheck.Checked && !_frameOpeningsCheck.Checked)
            {
                ShowValidationError("Select at least one framing action.");
                DialogResult = WinForms.DialogResult.None;
                return;
            }

            var depthSource = (WallFramingDepthSource) _depthSourceCombo.SelectedIndex;
            var manualDepthFeet = 0.0;
            var manualInsetFeet = 0.0;

            if (depthSource == WallFramingDepthSource.Manual)
            {
                if (!double.TryParse(_manualDepthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var manualDepthInches) ||
                    manualDepthInches <= 0)
                {
                    ShowValidationError("Manual depth must be a positive number in inches.");
                    DialogResult = WinForms.DialogResult.None;
                    return;
                }

                manualDepthFeet = manualDepthInches / 12.0;

                if (!double.TryParse(_manualInsetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var manualInsetInches) ||
                    manualInsetInches < 0)
                {
                    ShowValidationError("Manual inset must be zero or a positive number in inches.");
                    DialogResult = WinForms.DialogResult.None;
                    return;
                }

                manualInsetFeet = manualInsetInches / 12.0;
            }

            Options = new WallFramingOptions
            {
                FrameWall = _frameWallCheck.Checked,
                FrameOpenings = _frameOpeningsCheck.Checked,
                DepthSource = depthSource,
                ManualDepthFeet = manualDepthFeet,
                ManualReference = (WallFramingManualReference) _manualReferenceCombo.SelectedIndex,
                ManualInsetFeet = manualInsetFeet
            };
        }

        private static void ShowValidationError(string message)
        {
            WinForms.MessageBox.Show(message, "Wall Framing", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
        }
    }
}
