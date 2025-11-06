using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace RevitSuite.Host.UI
{
    internal class FootingZoneForm : Form
    {
        private readonly TextBox _clearDepthBox = new TextBox();
        private readonly TextBox _slopeRatioBox = new TextBox();
        private readonly TextBox _offsetBox = new TextBox();
        private readonly TextBox _transparencyBox = new TextBox();
        private readonly CheckBox _includeFootingsCheck = new CheckBox
        {
            Text = "Automatically include all structural foundations",
            AutoSize = true,
            MaximumSize = new Size(360, 0)
        };

        private readonly CheckBox _promptFootingsCheck = new CheckBox
        {
            Text = "Prompt to select foundations manually",
            AutoSize = true,
            MaximumSize = new Size(360, 0)
        };

        private readonly CheckBox _includeSlabsCheck = new CheckBox
        {
            Text = "Prompt to select slabs/floors manually",
            AutoSize = true,
            MaximumSize = new Size(360, 0)
        };
        private readonly Button _okButton = new Button { Text = "OK", Width = 90 };
        private readonly Button _cancelButton = new Button { Text = "Cancel", Width = 90 };

        public FootingZoneParameters Parameters { get; private set; } = null!;

        public FootingZoneForm()
        {
            Text = "Footing Influence Settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9F);
            Padding = new Padding(12);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var numericLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true
            };

            numericLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            numericLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

            AddLabeledRow(numericLayout, "Clear depth (ft):", _clearDepthBox, 0);
            AddLabeledRow(numericLayout, "Slope ratio (h/v):", _slopeRatioBox, 1);
            AddLabeledRow(numericLayout, "Vertical offset (ft):", _offsetBox, 2);
            AddLabeledRow(numericLayout, "Transparency (0-100):", _transparencyBox, 3);

            var selectionGroup = BuildSelectionGroup();

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(0, 12, 0, 0)
            };

            _okButton.DialogResult = DialogResult.OK;
            _okButton.Click += OnOkClick;
            _cancelButton.DialogResult = DialogResult.Cancel;

            buttonPanel.Controls.Add(_okButton);
            buttonPanel.Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            var container = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                AutoSize = true
            };
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            container.Controls.Add(numericLayout, 0, 0);
            container.Controls.Add(selectionGroup, 0, 1);
            container.Controls.Add(buttonPanel, 0, 2);

            Controls.Add(container);
        }

        public void SetDefaults(double clearDepth, double slopeRatio, double offset, int transparency, bool includeFootings, bool promptForFootings, bool includeSlabs)
        {
            _clearDepthBox.Text = clearDepth.ToString(CultureInfo.InvariantCulture);
            _slopeRatioBox.Text = slopeRatio.ToString(CultureInfo.InvariantCulture);
            _offsetBox.Text = offset.ToString(CultureInfo.InvariantCulture);
            _transparencyBox.Text = transparency.ToString(CultureInfo.InvariantCulture);
            _includeFootingsCheck.Checked = includeFootings;
            _promptFootingsCheck.Checked = promptForFootings;
            _includeSlabsCheck.Checked = includeSlabs;
        }

        private void AddLabeledRow(TableLayoutPanel layout, string labelText, Control input, int row)
        {
            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, row == 0 ? 0 : 6, 6, 0)
            };

            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(0, row == 0 ? 0 : 6, 0, 0);

            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(input, 1, row);
        }

        private void OnOkClick(object? sender, EventArgs e)
        {
            if (!TryParseDouble(_clearDepthBox.Text, out var clearDepth, minimum: 0.0))
            {
                ShowValidationError("Clear depth must be a non-negative number.");
                DialogResult = DialogResult.None;
                return;
            }

            if (!TryParseDouble(_slopeRatioBox.Text, out var slopeRatio, minimum: 0.0))
            {
                ShowValidationError("Slope ratio must be a non-negative number.");
                DialogResult = DialogResult.None;
                return;
            }

            if (!TryParseDouble(_offsetBox.Text, out var offset, minimum: double.MinValue))
            {
                ShowValidationError("Vertical offset must be a number.");
                DialogResult = DialogResult.None;
                return;
            }

            if (!TryParseInt(_transparencyBox.Text, out var transparency))
            {
                ShowValidationError("Transparency must be an integer between 0 and 100.");
                DialogResult = DialogResult.None;
                return;
            }

            Parameters = new FootingZoneParameters
            {
                ClearDepth = clearDepth,
                SlopeRatio = slopeRatio,
                VerticalOffset = offset,
                Transparency = transparency,
                IncludeFootings = _includeFootingsCheck.Checked,
                PromptForFootings = _promptFootingsCheck.Checked,
                PromptForSlabs = _includeSlabsCheck.Checked
            };
        }

        private GroupBox BuildSelectionGroup()
        {
            var group = new GroupBox
            {
                Text = "Element Selection",
                Dock = DockStyle.Fill,
                AutoSize = true
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoSize = true
            };

            var infoLabel = new Label
            {
                Text = "Choose how foundations and slabs are gathered. Manual prompts let you pick additional elements after closing this dialog.",
                AutoSize = true,
                MaximumSize = new Size(360, 0),
                Margin = new Padding(10, 8, 10, 4)
            };

            _includeFootingsCheck.Margin = new Padding(12, 6, 10, 0);
            _promptFootingsCheck.Margin = new Padding(12, 2, 10, 0);
            _includeSlabsCheck.Margin = new Padding(12, 2, 10, 10);

            layout.Controls.Add(infoLabel);
            layout.Controls.Add(_includeFootingsCheck);
            layout.Controls.Add(_promptFootingsCheck);
            layout.Controls.Add(_includeSlabsCheck);

            group.Controls.Add(layout);
            return group;
        }

        private static bool TryParseDouble(string text, out double value, double minimum)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                if (value >= minimum)
                {
                    return true;
                }
            }

            value = 0;
            return false;
        }

        private static bool TryParseInt(string text, out int value)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                value = Math.Max(0, Math.Min(100, value));
                return true;
            }

            value = 0;
            return false;
        }

        private static void ShowValidationError(string message)
        {
            MessageBox.Show(message, "Footing Influence Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal class FootingZoneParameters
    {
        public double ClearDepth { get; set; }
        public double SlopeRatio { get; set; }
        public double VerticalOffset { get; set; }
        public int Transparency { get; set; }
        public bool IncludeFootings { get; set; }
        public bool PromptForFootings { get; set; }
        public bool PromptForSlabs { get; set; }
    }
}
