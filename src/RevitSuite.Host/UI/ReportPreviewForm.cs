using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using WinForms = System.Windows.Forms;

namespace RevitSuite.Host.UI
{
    internal sealed class ReportPreviewModel
    {
        public string Title { get; set; } = "RevitSuite Report";
        public string Summary { get; set; } = string.Empty;
        public string? CsvPreviewPath { get; set; }
        public string? HtmlPreviewPath { get; set; }
        public string SaveDialogTitle { get; set; } = "Export Report";
        public string DefaultExportFileName { get; set; } = "Report.csv";
        public Func<string, string> ExportAction { get; set; } = _ => string.Empty;
    }

    internal sealed class ReportPreviewForm : WinForms.Form
    {
        private readonly ReportPreviewModel _model;

        public ReportPreviewForm(ReportPreviewModel model)
        {
            _model = model;

            Text = model.Title;
            Width = 1200;
            Height = 800;
            StartPosition = WinForms.FormStartPosition.CenterScreen;
            FormBorderStyle = WinForms.FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            MinimizeBox = true;
            MaximizeBox = true;

            var summaryLabel = new WinForms.Label
            {
                Dock = WinForms.DockStyle.Top,
                Height = 48,
                Padding = new WinForms.Padding(12, 12, 12, 8),
                Text = model.Summary
            };

            var tabControl = new WinForms.TabControl
            {
                Dock = WinForms.DockStyle.Fill
            };

            if (!string.IsNullOrWhiteSpace(model.HtmlPreviewPath) && File.Exists(model.HtmlPreviewPath))
            {
                var browser = new WinForms.WebBrowser
                {
                    Dock = WinForms.DockStyle.Fill,
                    ScriptErrorsSuppressed = true
                };
                browser.Navigate(model.HtmlPreviewPath);

                var reportTab = new WinForms.TabPage("Report");
                reportTab.Controls.Add(browser);
                tabControl.TabPages.Add(reportTab);
            }

            if (!string.IsNullOrWhiteSpace(model.CsvPreviewPath) && File.Exists(model.CsvPreviewPath))
            {
                var grid = new WinForms.DataGridView
                {
                    Dock = WinForms.DockStyle.Fill,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AutoSizeColumnsMode = WinForms.DataGridViewAutoSizeColumnsMode.DisplayedCells,
                    DataSource = LoadCsv(model.CsvPreviewPath)
                };

                var dataTab = new WinForms.TabPage("Data");
                dataTab.Controls.Add(grid);
                tabControl.TabPages.Add(dataTab);
            }

            var exportButton = new WinForms.Button
            {
                Text = "Export...",
                AutoSize = true
            };
            exportButton.Click += (_, _) => Export();

            var closeButton = new WinForms.Button
            {
                Text = "Close",
                AutoSize = true
            };
            closeButton.Click += (_, _) => Close();

            var buttonPanel = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Bottom,
                Height = 48,
                FlowDirection = WinForms.FlowDirection.RightToLeft,
                Padding = new WinForms.Padding(8)
            };
            buttonPanel.Controls.Add(closeButton);
            buttonPanel.Controls.Add(exportButton);

            Controls.Add(tabControl);
            Controls.Add(buttonPanel);
            Controls.Add(summaryLabel);
        }

        private void Export()
        {
            using var dialog = new WinForms.SaveFileDialog
            {
                Title = _model.SaveDialogTitle,
                Filter = "CSV Files (*.csv)|*.csv",
                FileName = _model.DefaultExportFileName,
                AddExtension = true,
                DefaultExt = "csv",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != WinForms.DialogResult.OK)
            {
                return;
            }

            try
            {
                var message = _model.ExportAction(dialog.FileName);
                WinForms.MessageBox.Show(this, message, _model.Title, WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show(this, ex.Message, _model.Title, WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
            }
        }

        private static DataTable LoadCsv(string path)
        {
            var table = new DataTable();
            var text = File.ReadAllText(path);
            var rows = ParseCsvRows(text);
            if (rows.Count == 0)
            {
                return table;
            }

            var headers = rows[0];
            foreach (var header in headers)
            {
                table.Columns.Add(header);
            }

            foreach (var values in rows.Skip(1))
            {
                while (values.Count < table.Columns.Count)
                {
                    values.Add(string.Empty);
                }

                table.Rows.Add(values.Take(table.Columns.Count).ToArray());
            }

            return table;
        }

        private static List<List<string>> ParseCsvRows(string text)
        {
            var rows = new List<List<string>>();
            var values = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                else if ((ch == '\r' || ch == '\n') && !inQuotes)
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    values.Add(current.ToString());
                    current.Clear();
                    rows.Add(values);
                    values = new List<string>();
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (current.Length > 0 || values.Count > 0)
            {
                values.Add(current.ToString());
                rows.Add(values);
            }

            return rows;
        }
    }

    internal static class ReportPreviewHost
    {
        private static readonly List<ReportPreviewForm> OpenForms = new List<ReportPreviewForm>();

        public static void Show(UIApplication app, ReportPreviewModel model)
        {
            var form = new ReportPreviewForm(model);
            form.FormClosed += (_, _) => OpenForms.Remove(form);
            OpenForms.Add(form);
            form.Show(new RevitWindowHandle(app.MainWindowHandle));
        }

        private sealed class RevitWindowHandle : WinForms.IWin32Window
        {
            public RevitWindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
