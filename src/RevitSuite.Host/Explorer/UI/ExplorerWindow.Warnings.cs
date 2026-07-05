using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        /// <summary>Row shown in the warnings grid; wraps a WarningRecord with display fields.</summary>
        public sealed record WarningRow(
            string Rank,
            string Description,
            int ElementCount,
            string Categories,
            string Elements,
            string DiffStatus,
            WarningRecord Record);

        private readonly ObservableCollection<WarningRow> _warningRows = new ObservableCollection<WarningRow>();
        private IReadOnlyList<WarningRecord> _currentWarnings = Array.Empty<WarningRecord>();

        private ComboBox _warningGroupCombo = null!;
        private ComboBox _snapshotCombo = null!;
        private DataGrid _warningsGrid = null!;
        private TextBlock _warningsSummary = null!;
        private IReadOnlyList<(string Path, DateTimeOffset CreatedUtc)> _snapshotEntries =
            Array.Empty<(string, DateTimeOffset)>();

        private UIElement BuildWarningsTab()
        {
            var layout = new Grid { Margin = new Thickness(8) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.Children.Add(MakeButton("Refresh Warnings", (_, _) => RefreshWarnings()));

            toolbar.Children.Add(MakeCaption("Group by"));
            _warningGroupCombo = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 12, 0) };
            _warningGroupCombo.Items.Add("Rank");
            _warningGroupCombo.Items.Add("Description");
            _warningGroupCombo.Items.Add("Category");
            _warningGroupCombo.SelectedIndex = 0;
            _warningGroupCombo.SelectionChanged += (_, _) => ApplyWarningGrouping();
            toolbar.Children.Add(_warningGroupCombo);

            toolbar.Children.Add(MakeButton("Save Snapshot", (_, _) => SaveWarningSnapshot()));
            toolbar.Children.Add(MakeCaption("Compare to"));
            _snapshotCombo = new ComboBox { Width = 190, Margin = new Thickness(0, 0, 8, 0) };
            toolbar.Children.Add(_snapshotCombo);
            toolbar.Children.Add(MakeButton("Diff", (_, _) => DiffAgainstSnapshot()));
            Grid.SetRow(toolbar, 0);
            layout.Children.Add(toolbar);

            _warningsSummary = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
                Text = "Rankings come from warning-rankings.json (High/Medium/Low by failure id or description pattern). " +
                       "Refresh to read warnings from the model."
            };
            Grid.SetRow(_warningsSummary, 1);
            layout.Children.Add(_warningsSummary);

            _warningsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                EnableRowVirtualization = true,
                ItemsSource = _warningRows
            };

            void AddColumn(string header, string path, double weight)
            {
                _warningsGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(path),
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star)
                });
            }

            AddColumn("Rank", nameof(WarningRow.Rank), 0.5);
            AddColumn("Diff", nameof(WarningRow.DiffStatus), 0.5);
            AddColumn("Description", nameof(WarningRow.Description), 3);
            AddColumn("Elements", nameof(WarningRow.ElementCount), 0.5);
            AddColumn("Categories", nameof(WarningRow.Categories), 1);
            AddColumn("Element Names", nameof(WarningRow.Elements), 1.5);

            Grid.SetRow(_warningsGrid, 2);
            layout.Children.Add(_warningsGrid);

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton("Select Affected", (_, _) => ActOnWarnings(selectOnly: true)));
            actions.Children.Add(MakeButton("Show Affected", (_, _) => ActOnWarnings(selectOnly: false)));
            actions.Children.Add(MakeButton("Export CSV", (_, _) => ExportWarnings("csv")));
            actions.Children.Add(MakeButton("Export XLSX", (_, _) => ExportWarnings("xlsx")));
            actions.Children.Add(MakeButton("Export JSON", (_, _) => ExportWarnings("json")));
            Grid.SetRow(actions, 3);
            layout.Children.Add(actions);

            return layout;
        }

        private void RefreshWarnings()
        {
            RunOnRevit("Reading warnings…", (_, uidoc) =>
            {
                var rankings = WarningService.LoadRankings();
                var warnings = WarningService.Extract(uidoc.Document, rankings);
                var snapshots = WarningService.ListSnapshots(uidoc.Document);

                OnUi(() =>
                {
                    _currentWarnings = warnings;
                    PopulateWarningRows(warnings.Select(w => (w, "")));
                    _snapshotEntries = snapshots;
                    _snapshotCombo.Items.Clear();
                    foreach (var (_, createdUtc) in snapshots)
                    {
                        _snapshotCombo.Items.Add(createdUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    }

                    if (_snapshotCombo.Items.Count > 0)
                    {
                        _snapshotCombo.SelectedIndex = 0;
                    }

                    _warningsSummary.Text = BuildWarningSummary(warnings);
                });
            });
        }

        private static string BuildWarningSummary(IReadOnlyList<WarningRecord> warnings)
        {
            if (warnings.Count == 0)
            {
                return "No warnings in the model.";
            }

            var byRank = warnings
                .GroupBy(w => w.Rank)
                .OrderByDescending(g => g.Key)
                .Select(g => $"{g.Key}: {g.Count()}");
            return $"{warnings.Count:N0} warning(s) — {string.Join(", ", byRank)}.";
        }

        private void PopulateWarningRows(IEnumerable<(WarningRecord Record, string DiffStatus)> rows)
        {
            _warningRows.Clear();
            foreach (var (record, diffStatus) in rows)
            {
                _warningRows.Add(new WarningRow(
                    record.Rank.ToString(),
                    record.Description,
                    record.FailingElementIds.Count + record.AdditionalElementIds.Count,
                    string.Join("; ", record.Categories),
                    string.Join("; ", record.ElementNames),
                    diffStatus,
                    record));
            }

            ApplyWarningGrouping();
        }

        private void ApplyWarningGrouping()
        {
            if (_warningsGrid == null)
            {
                return;
            }

            var view = CollectionViewSource.GetDefaultView(_warningRows);
            view.GroupDescriptions.Clear();
            view.SortDescriptions.Clear();

            switch (_warningGroupCombo.SelectedIndex)
            {
                case 1:
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WarningRow.Description)));
                    break;
                case 2:
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WarningRow.Categories)));
                    break;
                default:
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WarningRow.Rank)));
                    break;
            }

            view.SortDescriptions.Add(new SortDescription(nameof(WarningRow.Description), ListSortDirection.Ascending));

            if (_warningsGrid.GroupStyle.Count == 0)
            {
                _warningsGrid.GroupStyle.Add(BuildGroupStyle());
            }
        }

        private static GroupStyle BuildGroupStyle()
        {
            var headerTemplate = new DataTemplate();
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 6, 0, 2));
            var binding = new MultiBinding { StringFormat = "{0}  ({1})" };
            binding.Bindings.Add(new Binding("Name"));
            binding.Bindings.Add(new Binding("ItemCount"));
            textFactory.SetBinding(TextBlock.TextProperty, binding);
            headerTemplate.VisualTree = textFactory;
            return new GroupStyle { HeaderTemplate = headerTemplate };
        }

        private IReadOnlyList<long> GetWarningElementIds()
        {
            var rows = _warningsGrid.SelectedItems.Count > 0
                ? _warningsGrid.SelectedItems.Cast<WarningRow>()
                : _warningRows;

            return rows
                .SelectMany(r => r.Record.FailingElementIds.Concat(r.Record.AdditionalElementIds))
                .Distinct()
                .ToList();
        }

        private void ActOnWarnings(bool selectOnly)
        {
            var ids = GetWarningElementIds();
            if (ids.Count == 0)
            {
                SetStatus("No warning elements to act on. Refresh warnings first.");
                return;
            }

            RunOnRevit(selectOnly ? "Selecting warning elements…" : "Showing warning elements…", (_, uidoc) =>
            {
                if (selectOnly)
                {
                    var selected = RevitActions.SelectElements(uidoc, ids);
                    OnUi(() => SetStatus(selected == 0
                        ? "None of the warning elements can be selected (they may be sketch/internal elements)."
                        : $"Selected {selected:N0} warning element(s)."));
                }
                else
                {
                    var (shown, error) = RevitActions.ShowElements(uidoc, ids);
                    OnUi(() => SetStatus(error ?? $"Showing {shown:N0} warning element(s)."));
                }
            });
        }

        private void SaveWarningSnapshot()
        {
            if (_currentWarnings.Count == 0)
            {
                SetStatus("Refresh warnings before saving a snapshot.");
                return;
            }

            RunOnRevit("Saving warning snapshot…", (_, uidoc) =>
            {
                var path = WarningService.SaveSnapshot(uidoc.Document, _currentWarnings);
                var snapshots = WarningService.ListSnapshots(uidoc.Document);
                OnUi(() =>
                {
                    _snapshotEntries = snapshots;
                    _snapshotCombo.Items.Clear();
                    foreach (var (_, createdUtc) in snapshots)
                    {
                        _snapshotCombo.Items.Add(createdUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    }

                    if (_snapshotCombo.Items.Count > 0)
                    {
                        _snapshotCombo.SelectedIndex = 0;
                    }

                    SetStatus($"Warning snapshot saved: {path}");
                });
            });
        }

        private void DiffAgainstSnapshot()
        {
            var index = _snapshotCombo.SelectedIndex;
            if (index < 0 || index >= _snapshotEntries.Count)
            {
                SetStatus("Select a snapshot to compare against (save one first if none exist).");
                return;
            }

            if (_currentWarnings.Count == 0)
            {
                SetStatus("Refresh warnings before comparing.");
                return;
            }

            var baseline = WarningService.LoadSnapshot(_snapshotEntries[index].Path);
            if (baseline == null)
            {
                SetStatus("Could not read the selected snapshot.");
                return;
            }

            var diff = WarningService.Diff(baseline, _currentWarnings);
            var rows = diff.NewWarnings.Select(w => (w, "NEW"))
                .Concat(diff.ResolvedWarnings.Select(w => (w, "RESOLVED")));
            PopulateWarningRows(rows);

            _warningsSummary.Text =
                $"Compared to snapshot {baseline.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}: " +
                $"{diff.NewWarnings.Count} new, {diff.ResolvedWarnings.Count} resolved. " +
                "Refresh Warnings to return to the live list.";
        }

        private void ExportWarnings(string format)
        {
            if (_warningRows.Count == 0)
            {
                SetStatus("Nothing to export — refresh warnings first.");
                return;
            }

            var records = _warningRows.Select(r => r.Record).ToList();
            var path = PromptSavePath("Export Warnings", $"Warnings.{format}",
                format switch
                {
                    "xlsx" => "Excel workbook (*.xlsx)|*.xlsx",
                    "json" => "JSON (*.json)|*.json",
                    _ => "CSV files (*.csv)|*.csv"
                });
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                var table = ExportService.BuildWarningsTable(records);
                switch (format)
                {
                    case "xlsx":
                        ExportService.WriteXlsx(path, new List<ExportService.Table>
                        {
                            ExportService.BuildRunMetadataTable(_exploreModelTitle, "Warnings"),
                            table
                        });
                        break;
                    case "json":
                        ExportService.WriteJson(path, "Warnings", _exploreModelTitle, records);
                        break;
                    default:
                        ExportService.WriteCsv(path, table);
                        break;
                }

                SetStatus($"Exported {records.Count:N0} warning(s) to {path}");
            });
        }
    }
}
