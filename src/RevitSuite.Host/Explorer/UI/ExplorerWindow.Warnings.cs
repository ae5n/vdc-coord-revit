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
        /// <summary>Row shown in the warnings grid. Status/AssignedTo are editable triage fields.</summary>
        public sealed class WarningRow
        {
            public WarningRow(string rank, string description, int elementCount, string categories,
                string elements, string diffStatus, WarningRecord record, WarningMetadataStore.WarningMetadata? metadata)
            {
                Rank = rank;
                Description = description;
                ElementCount = elementCount;
                Categories = categories;
                Elements = elements;
                DiffStatus = diffStatus;
                Record = record;
                Status = metadata?.Status ?? "Open";
                AssignedTo = metadata?.AssignedTo;
            }

            public string Rank { get; set; }
            public string Description { get; }
            public int ElementCount { get; }
            public string Categories { get; }
            public string Elements { get; }
            public string DiffStatus { get; }
            public WarningRecord Record { get; }
            public string Status { get; set; }
            public string? AssignedTo { get; set; }

            public string Origin => Record.Origin;

            public string ElementIds =>
                string.Join(",", Record.FailingElementIds.Concat(Record.AdditionalElementIds));
        }

        private readonly ObservableCollection<WarningRow> _warningRows = new ObservableCollection<WarningRow>();
        private IReadOnlyList<WarningRecord> _currentWarnings = Array.Empty<WarningRecord>();
        private string _warningModelIdentity = string.Empty;
        private int _warningNavigateIndex = -1;

        private ComboBox _warningGroupCombo = null!;
        private ComboBox _snapshotCombo = null!;
        private ComboBox _setRankCombo = null!;
        private CheckBox _warningsIncludeLinksCheck = null!;
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
            _warningsIncludeLinksCheck = new CheckBox
            {
                Content = "Include linked models",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            toolbar.Children.Add(_warningsIncludeLinksCheck);

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

            toolbar.Children.Add(MakeCaption("Set rank"));
            _setRankCombo = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 4, 0) };
            foreach (var rank in Enum.GetNames(typeof(WarningRank)))
            {
                _setRankCombo.Items.Add(rank);
            }

            _setRankCombo.SelectedIndex = 3; // High
            toolbar.Children.Add(_setRankCombo);
            toolbar.Children.Add(MakeButton("Apply to Selected Type", (_, _) => ApplyRankToSelected()));
            toolbar.Children.Add(MakeButton("Save Triage", (_, _) => SaveWarningTriage()));
            toolbar.Children.Add(MakeButton("◀ Prev", (_, _) => NavigateWarning(-1)));
            toolbar.Children.Add(MakeButton("Next ▶", (_, _) => NavigateWarning(1)));
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
                SelectionMode = DataGridSelectionMode.Extended,
                EnableRowVirtualization = true,
                CanUserAddRows = false,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
                ItemsSource = _warningRows
            };
            _warningsGrid.MouseDoubleClick += (_, _) =>
            {
                if (_warningsGrid.SelectedItem is WarningRow)
                {
                    ActOnWarnings(selectOnly: false);
                }
            };

            void AddColumn(string header, string path, double weight)
            {
                _warningsGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(path),
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star),
                    IsReadOnly = true
                });
            }

            AddColumn("Rank", nameof(WarningRow.Rank), 0.5);
            AddColumn("Model", nameof(WarningRow.Origin), 0.7);
            AddColumn("Diff", nameof(WarningRow.DiffStatus), 0.4);
            AddColumn("Description", nameof(WarningRow.Description), 2.4);
            AddColumn("Elements", nameof(WarningRow.ElementCount), 0.4);
            AddColumn("Categories", nameof(WarningRow.Categories), 0.9);
            AddColumn("Element Names", nameof(WarningRow.Elements), 1.2);
            AddColumn("Element Ids", nameof(WarningRow.ElementIds), 0.9);

            var statusColumn = new DataGridComboBoxColumn
            {
                Header = "Status",
                Width = new DataGridLength(0.7, DataGridLengthUnitType.Star),
                ItemsSource = WarningMetadataStore.Statuses,
                SelectedItemBinding = new Binding(nameof(WarningRow.Status))
            };
            _warningsGrid.Columns.Add(statusColumn);

            _warningsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Assigned To",
                Binding = new Binding(nameof(WarningRow.AssignedTo)),
                Width = new DataGridLength(0.8, DataGridLengthUnitType.Star)
            });

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
            var includeLinks = _warningsIncludeLinksCheck.IsChecked == true;

            RunOnRevit("Reading warnings…", (_, uidoc) =>
            {
                var rankings = WarningService.LoadRankings();
                var warnings = WarningService.Extract(uidoc.Document, rankings, includeLinks);
                var snapshots = WarningService.ListSnapshots(uidoc.Document);
                var identity = ExplorerPaths.GetModelIdentity(uidoc.Document);

                OnUi(() =>
                {
                    _currentWarnings = warnings;
                    _warningModelIdentity = identity;
                    _warningNavigateIndex = -1;
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
            var metadata = string.IsNullOrEmpty(_warningModelIdentity)
                ? new Dictionary<string, WarningMetadataStore.WarningMetadata>()
                : WarningMetadataStore.Load(_warningModelIdentity);

            _warningRows.Clear();
            foreach (var (record, diffStatus) in rows)
            {
                metadata.TryGetValue(record.WarningKey, out var meta);
                _warningRows.Add(new WarningRow(
                    record.Rank.ToString(),
                    record.Description,
                    record.FailingElementIds.Count + record.AdditionalElementIds.Count,
                    string.Join("; ", record.Categories),
                    string.Join("; ", record.ElementNames),
                    diffStatus,
                    record,
                    meta));
            }

            ApplyWarningGrouping();
        }

        private void SaveWarningTriage()
        {
            if (string.IsNullOrEmpty(_warningModelIdentity))
            {
                SetStatus("Refresh warnings before saving triage.");
                return;
            }

            _warningsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            TryFileOperation("Save triage", () =>
            {
                // Merge with what's on disk so triage of warnings not in the current view survives.
                var merged = WarningMetadataStore.Load(_warningModelIdentity);

                foreach (var row in _warningRows)
                {
                    merged[row.Record.WarningKey] =
                        new WarningMetadataStore.WarningMetadata(row.Status, row.AssignedTo, null);
                }

                WarningMetadataStore.Save(_warningModelIdentity, merged);
                SetStatus($"Saved triage for {_warningRows.Count} warning(s).");
            });
        }

        /// <summary>
        /// Applies the chosen rank to every warning sharing the selected row's failure type,
        /// persists it to warning-rankings.json, and re-ranks the current list.
        /// </summary>
        private void ApplyRankToSelected()
        {
            if (_warningsGrid.SelectedItem is not WarningRow row)
            {
                SetStatus("Select a warning first — the rank applies to its whole warning type.");
                return;
            }

            if (!Enum.TryParse<WarningRank>((string)_setRankCombo.SelectedItem, out var rank))
            {
                return;
            }

            TryFileOperation("Set rank", () =>
            {
                var rankings = WarningService.LoadRankings()
                    .Where(r => !string.Equals(r.FailureDefinitionId, row.Record.FailureDefinitionId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                rankings.Insert(0, new WarningRanking(row.Record.FailureDefinitionId, null, rank));
                WarningService.SaveRankings(rankings);

                foreach (var other in _warningRows.Where(r =>
                             string.Equals(r.Record.FailureDefinitionId, row.Record.FailureDefinitionId,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    other.Rank = rank.ToString();
                }

                // Rebuild the view so grouping and cell text reflect the new rank.
                _currentWarnings = _currentWarnings
                    .Select(w => string.Equals(w.FailureDefinitionId, row.Record.FailureDefinitionId,
                        StringComparison.OrdinalIgnoreCase)
                        ? w with { Rank = rank }
                        : w)
                    .ToList();
                PopulateWarningRows(_currentWarnings.Select(w => (w, "")));
                SetStatus($"Ranked '{Truncate(row.Description, 60)}' as {rank} (saved to warning-rankings.json).");
            });
        }

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max) + "…";

        /// <summary>Steps through warnings one at a time, selecting and zooming to each.</summary>
        private void NavigateWarning(int delta)
        {
            if (_warningRows.Count == 0)
            {
                SetStatus("Refresh warnings first.");
                return;
            }

            _warningNavigateIndex = ((_warningNavigateIndex + delta) % _warningRows.Count + _warningRows.Count)
                                    % _warningRows.Count;
            var row = _warningRows[_warningNavigateIndex];

            _warningsGrid.SelectedItems.Clear();
            _warningsGrid.SelectedItem = row;
            _warningsGrid.ScrollIntoView(row);

            var (hostIds, linked) = GetWarningElementTargets(new[] { row });
            var position = $"{_warningNavigateIndex + 1}/{_warningRows.Count}";

            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus($"Warning {position}: no selectable elements. {Truncate(row.Description, 80)}");
                return;
            }

            RunOnRevit("Showing warning elements…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowMixed(uidoc, hostIds, linked);
                OnUi(() => SetStatus(error != null
                    ? $"Warning {position}: {error}"
                    : $"Warning {position} — showing {shown} element(s): {Truncate(row.Description, 80)}"));
            });
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

        private (List<long> HostIds, List<RevitActions.LinkedTarget> Linked) GetWarningElementTargets(
            IEnumerable<WarningRow>? source = null)
        {
            var rows = (source ?? (_warningsGrid.SelectedItems.Count > 0
                    ? _warningsGrid.SelectedItems.Cast<WarningRow>()
                    : _warningRows))
                .ToList();

            var hostIds = rows
                .Where(r => r.Record.LinkInstanceIdValue == null)
                .SelectMany(r => r.Record.FailingElementIds.Concat(r.Record.AdditionalElementIds))
                .Distinct()
                .ToList();
            var linked = rows
                .Where(r => r.Record.LinkInstanceIdValue != null)
                .SelectMany(r => r.Record.FailingElementIds.Concat(r.Record.AdditionalElementIds)
                    .Select(id => new RevitActions.LinkedTarget(r.Record.LinkInstanceIdValue!.Value, id)))
                .Distinct()
                .ToList();

            return (hostIds, linked);
        }

        private void ActOnWarnings(bool selectOnly)
        {
            var (hostIds, linked) = GetWarningElementTargets();
            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus("No warning elements to act on. Refresh warnings first.");
                return;
            }

            RunOnRevit(selectOnly ? "Selecting warning elements…" : "Showing warning elements…", (_, uidoc) =>
            {
                if (selectOnly)
                {
                    var (selected, _, error) = RevitActions.SelectMixed(uidoc, hostIds, linked);
                    OnUi(() => SetStatus(error ?? (selected == 0
                        ? "None of the warning elements can be selected (they may be sketch/internal elements)."
                        : $"Selected {selected:N0} warning element(s).")));
                }
                else
                {
                    var (shown, error) = RevitActions.ShowMixed(uidoc, hostIds, linked);
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
                var triage = _warningRows.ToDictionary(
                    r => r.Record.WarningKey,
                    r => (r.Status, r.AssignedTo),
                    StringComparer.Ordinal);
                var table = ExportService.BuildWarningsTable(records, triage);
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
