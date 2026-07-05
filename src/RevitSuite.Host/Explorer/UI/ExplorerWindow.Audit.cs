using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        public sealed record FindingRow(
            string Severity,
            string Rule,
            int Count,
            string Summary,
            AuditFinding Finding);

        public sealed record HealthRow(string Component, string Severity, int Count, double Deduction);

        private readonly ObservableCollection<FindingRow> _findingRows = new ObservableCollection<FindingRow>();
        private readonly ObservableCollection<HealthRow> _healthRows = new ObservableCollection<HealthRow>();

        private IReadOnlyList<AuditFinding> _currentFindings = Array.Empty<AuditFinding>();
        private HealthScore? _currentHealth;

        private TextBlock _healthScoreText = null!;
        private TextBlock _packSummaryText = null!;
        private TextBlock _guidanceText = null!;
        private DataGrid _findingsGrid = null!;

        private UIElement BuildAuditTab()
        {
            var layout = new Grid { Margin = new Thickness(8) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.Children.Add(MakeButton("Run Audit", (_, _) => RunAudit()));
            toolbar.Children.Add(MakeButton("Save Snapshot", (_, _) => SaveAuditSnapshot()));
            toolbar.Children.Add(MakeButton("Export Package (XLSX)", (_, _) => ExportAuditPackage("xlsx")));
            toolbar.Children.Add(MakeButton("Export Package (JSON)", (_, _) => ExportAuditPackage("json")));
            toolbar.Children.Add(MakeButton("Open Rules Folder", (_, _) => OpenRulesFolder()));
            Grid.SetRow(toolbar, 0);
            layout.Children.Add(toolbar);

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _healthScoreText = new TextBlock
            {
                Text = "Health Score: —",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(_healthScoreText);

            _packSummaryText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Text = "Rule packs load from the Explorer rules folder (*.rules.json). " +
                       "The built-in RevitSuite Core pack is created there on first run and can be edited or extended."
            };
            headerPanel.Children.Add(_packSummaryText);
            Grid.SetRow(headerPanel, 1);
            layout.Children.Add(headerPanel);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            _findingsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                EnableRowVirtualization = true,
                ItemsSource = _findingRows
            };

            void AddFindingColumn(string header, string path, double weight)
            {
                _findingsGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(path),
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star)
                });
            }

            AddFindingColumn("Severity", nameof(FindingRow.Severity), 0.7);
            AddFindingColumn("Rule", nameof(FindingRow.Rule), 1.5);
            AddFindingColumn("Count", nameof(FindingRow.Count), 0.5);
            AddFindingColumn("Summary", nameof(FindingRow.Summary), 2);
            _findingsGrid.SelectionChanged += (_, _) => ShowFindingGuidance();
            Grid.SetColumn(_findingsGrid, 0);
            body.Children.Add(_findingsGrid);

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(splitter, 1);
            body.Children.Add(splitter);

            var rightPanel = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var guidanceScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _guidanceText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Select a finding to see why it matters and how to fix it safely."
            };
            guidanceScroll.Content = _guidanceText;
            Grid.SetRow(guidanceScroll, 0);
            rightPanel.Children.Add(guidanceScroll);

            var healthGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Margin = new Thickness(0, 8, 0, 0),
                ItemsSource = _healthRows
            };

            void AddHealthColumn(string header, string path, double weight)
            {
                healthGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(path),
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star)
                });
            }

            AddHealthColumn("Score Component", nameof(HealthRow.Component), 2);
            AddHealthColumn("Severity", nameof(HealthRow.Severity), 0.8);
            AddHealthColumn("Count", nameof(HealthRow.Count), 0.5);
            AddHealthColumn("−Points", nameof(HealthRow.Deduction), 0.6);
            Grid.SetRow(healthGrid, 1);
            rightPanel.Children.Add(healthGrid);

            Grid.SetColumn(rightPanel, 2);
            body.Children.Add(rightPanel);

            Grid.SetRow(body, 2);
            layout.Children.Add(body);

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton("Select Elements", (_, _) => ActOnFindings(selectOnly: true)));
            actions.Children.Add(MakeButton("Show Elements", (_, _) => ActOnFindings(selectOnly: false)));
            actions.Children.Add(MakeButton("Export Findings CSV", (_, _) => ExportFindingsCsv()));
            Grid.SetRow(actions, 3);
            layout.Children.Add(actions);

            return layout;
        }

        private void RunAudit()
        {
            RunOnRevit("Running audit…", (_, uidoc) =>
            {
                var loadedPacks = AuditService.LoadPacks();
                var validPacks = loadedPacks.Where(p => p.Pack != null).Select(p => p.Pack!).ToList();
                var packErrors = loadedPacks.Where(p => p.Error != null)
                    .Select(p => $"{System.IO.Path.GetFileName(p.FilePath)}: {p.Error}")
                    .ToList();

                var findings = AuditService.Run(uidoc, validPacks);
                var warnings = WarningService.Extract(uidoc.Document, WarningService.LoadRankings());
                var health = AuditService.ComputeHealth(findings, warnings);

                OnUi(() =>
                {
                    _currentFindings = findings;
                    _currentHealth = health;

                    _findingRows.Clear();
                    foreach (var finding in findings)
                    {
                        _findingRows.Add(new FindingRow(
                            finding.Severity.ToString(),
                            finding.RuleName,
                            finding.ElementIds.Count,
                            finding.Summary,
                            finding));
                    }

                    _healthRows.Clear();
                    foreach (var component in health.Components)
                    {
                        _healthRows.Add(new HealthRow(
                            component.Label, component.Severity.ToString(), component.Count, component.Deduction));
                    }

                    _healthScoreText.Text = $"Health Score: {health.Score:0.#} / 100";
                    _healthScoreText.Foreground = health.Score switch
                    {
                        >= 80 => new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)),
                        >= 50 => new SolidColorBrush(Color.FromRgb(0xB4, 0x5C, 0x0F)),
                        _ => new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
                    };

                    var packSummary =
                        $"{validPacks.Count} rule pack(s), {validPacks.Sum(p => p.Rules.Count(r => r.Enabled))} active rule(s). " +
                        $"{findings.Count} rule(s) produced findings.";
                    if (packErrors.Count > 0)
                    {
                        packSummary += $" Pack errors: {string.Join(" | ", packErrors)}";
                    }

                    _packSummaryText.Text = packSummary;
                    SetStatus($"Audit complete — health score {health.Score:0.#}.");
                });
            });
        }

        private void ShowFindingGuidance()
        {
            if (_findingsGrid.SelectedItem is not FindingRow row)
            {
                return;
            }

            _guidanceText.Inlines.Clear();
            void AddSection(string title, string body)
            {
                _guidanceText.Inlines.Add(new System.Windows.Documents.Run(title + "\n")
                {
                    FontWeight = FontWeights.SemiBold
                });
                _guidanceText.Inlines.Add(new System.Windows.Documents.Run(body + "\n\n"));
            }

            AddSection(row.Rule, row.Summary);
            AddSection("Why it matters", row.Finding.WhyItMatters);
            AddSection("Safe fix guidance", row.Finding.SafeFixGuidance);
        }

        private void ActOnFindings(bool selectOnly)
        {
            var ids = (_findingsGrid.SelectedItems.Count > 0
                    ? _findingsGrid.SelectedItems.Cast<FindingRow>()
                    : _findingRows)
                .SelectMany(r => r.Finding.ElementIds)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                SetStatus("No audit findings to act on. Run an audit first.");
                return;
            }

            RunOnRevit(selectOnly ? "Selecting elements…" : "Showing elements…", (_, uidoc) =>
            {
                if (selectOnly)
                {
                    var selected = RevitActions.SelectElements(uidoc, ids);
                    OnUi(() => SetStatus($"Selected {selected:N0} element(s)."));
                }
                else
                {
                    var (shown, error) = RevitActions.ShowElements(uidoc, ids);
                    OnUi(() => SetStatus(error ?? $"Showing {shown:N0} element(s)."));
                }
            });
        }

        private void SaveAuditSnapshot()
        {
            if (_currentHealth == null)
            {
                SetStatus("Run an audit before saving a snapshot.");
                return;
            }

            RunOnRevit("Saving audit snapshot…", (_, uidoc) =>
            {
                var path = AuditService.SaveSnapshot(uidoc.Document, _currentFindings, _currentHealth!);
                OnUi(() => SetStatus($"Audit snapshot saved: {path}"));
            });
        }

        private void ExportFindingsCsv()
        {
            if (_currentFindings.Count == 0)
            {
                SetStatus("Run an audit first.");
                return;
            }

            var path = PromptSavePath("Export Audit Findings", "AuditFindings.csv", "CSV files (*.csv)|*.csv");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                ExportService.WriteCsv(path, ExportService.BuildFindingsTable(_currentFindings));
                SetStatus($"Exported {_currentFindings.Count} finding(s) to {path}");
            });
        }

        private void ExportAuditPackage(string format)
        {
            if (_currentHealth == null)
            {
                SetStatus("Run an audit first.");
                return;
            }

            var path = PromptSavePath("Export Audit Package", $"AuditPackage.{format}",
                format == "xlsx" ? "Excel workbook (*.xlsx)|*.xlsx" : "JSON (*.json)|*.json");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                if (format == "xlsx")
                {
                    var tables = new List<ExportService.Table>
                    {
                        ExportService.BuildRunMetadataTable(_exploreModelTitle, "Full Audit Package"),
                        ExportService.BuildHealthTable(_currentHealth!),
                        ExportService.BuildFindingsTable(_currentFindings)
                    };

                    if (_currentWarnings.Count > 0)
                    {
                        tables.Add(ExportService.BuildWarningsTable(_currentWarnings));
                    }

                    if (_exploreRecords.Count > 0)
                    {
                        tables.Add(ExportService.BuildElementsTable(_exploreRecords));
                    }

                    ExportService.WriteXlsx(path, tables);
                }
                else
                {
                    ExportService.WriteJson(path, "FullAuditPackage", _exploreModelTitle, new
                    {
                        healthScore = _currentHealth,
                        findings = _currentFindings,
                        warnings = _currentWarnings
                    });
                }

                SetStatus($"Audit package exported to {path}");
            });
        }

        private void OpenRulesFolder()
        {
            TryFileOperation("Open rules folder", () =>
            {
                AuditService.LoadPacks(); // Ensures the built-in pack exists before opening the folder.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ExplorerPaths.RulesDirectory,
                    UseShellExecute = true
                });
            });
        }
    }
}
