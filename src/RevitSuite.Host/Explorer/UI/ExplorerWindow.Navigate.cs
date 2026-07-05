using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        private readonly ObservableCollection<ViewRecord> _navViews = new ObservableCollection<ViewRecord>();
        private readonly ObservableCollection<ViewRecord> _navSheets = new ObservableCollection<ViewRecord>();
        private readonly ObservableCollection<ViewRecord> _navSchedules = new ObservableCollection<ViewRecord>();

        private NavigateService.NavigateInventory? _navInventory;

        private CheckBox _onlyUnplacedCheck = null!;
        private CheckBox _onlyDuplicatesCheck = null!;
        private TextBox _navSearchBox = null!;
        private TabControl _navTabs = null!;

        private UIElement BuildNavigateTab()
        {
            var layout = new Grid { Margin = new Thickness(8) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.Children.Add(MakeButton("Refresh", (_, _) => RefreshNavigate()));

            _onlyUnplacedCheck = new CheckBox
            {
                Content = "Only not on sheets",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            _onlyUnplacedCheck.Checked += (_, _) => ApplyNavigateFilters();
            _onlyUnplacedCheck.Unchecked += (_, _) => ApplyNavigateFilters();
            toolbar.Children.Add(_onlyUnplacedCheck);

            _onlyDuplicatesCheck = new CheckBox
            {
                Content = "Only duplicate names",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            _onlyDuplicatesCheck.Checked += (_, _) => ApplyNavigateFilters();
            _onlyDuplicatesCheck.Unchecked += (_, _) => ApplyNavigateFilters();
            toolbar.Children.Add(_onlyDuplicatesCheck);

            toolbar.Children.Add(MakeCaption("Search"));
            _navSearchBox = new TextBox { Width = 200, Margin = new Thickness(0, 0, 12, 0) };
            _navSearchBox.TextChanged += (_, _) => ApplyNavigateFilters();
            toolbar.Children.Add(_navSearchBox);

            Grid.SetRow(toolbar, 0);
            layout.Children.Add(toolbar);

            _navTabs = new TabControl();
            _navTabs.Items.Add(new TabItem { Header = "Views", Content = BuildViewGrid(_navViews) });
            _navTabs.Items.Add(new TabItem { Header = "Sheets", Content = BuildViewGrid(_navSheets) });
            _navTabs.Items.Add(new TabItem { Header = "Schedules", Content = BuildViewGrid(_navSchedules) });
            Grid.SetRow(_navTabs, 1);
            layout.Children.Add(_navTabs);

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton("Open", (_, _) => OpenSelectedView()));
            actions.Children.Add(MakeButton("Export Inventory CSV", (_, _) => ExportNavigateCsv()));
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);

            return layout;
        }

        private DataGrid BuildViewGrid(ObservableCollection<ViewRecord> source)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                EnableRowVirtualization = true,
                ItemsSource = source
            };

            void AddColumn(string header, string path, double weight)
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(path),
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star)
                });
            }

            AddColumn("Name", nameof(ViewRecord.Name), 2);
            AddColumn("Kind", nameof(ViewRecord.ViewKind), 0.8);
            AddColumn("On Sheet", nameof(ViewRecord.IsPlacedOnSheet), 0.5);
            AddColumn("Sheets", nameof(ViewRecord.SheetNumbersText), 0.8);
            AddColumn("Template", nameof(ViewRecord.ViewTemplateName), 1);
            AddColumn("Level", nameof(ViewRecord.LevelName), 0.7);
            AddColumn("Duplicate", nameof(ViewRecord.HasDuplicateName), 0.5);

            grid.MouseDoubleClick += (_, _) => OpenSelectedView();
            return grid;
        }

        private void RefreshNavigate()
        {
            RunOnRevit("Reading views, sheets, and schedules…", (_, uidoc) =>
            {
                var inventory = NavigateService.Collect(uidoc.Document);
                OnUi(() =>
                {
                    _navInventory = inventory;
                    ApplyNavigateFilters();
                    var unplaced = inventory.Views.Count(v => !v.IsPlacedOnSheet);
                    var duplicates = inventory.Views.Count(v => v.HasDuplicateName);
                    SetStatus($"{inventory.Views.Count} view(s) ({unplaced} not on sheets, {duplicates} duplicate names), " +
                              $"{inventory.Sheets.Count} sheet(s), {inventory.Schedules.Count} schedule(s).");
                });
            });
        }

        private void ApplyNavigateFilters()
        {
            if (_navInventory == null)
            {
                return;
            }

            var search = (_navSearchBox.Text ?? string.Empty).Trim();
            var onlyUnplaced = _onlyUnplacedCheck.IsChecked == true;
            var onlyDuplicates = _onlyDuplicatesCheck.IsChecked == true;

            IEnumerable<ViewRecord> Apply(IEnumerable<ViewRecord> records) => records.Where(v =>
                (!onlyUnplaced || !v.IsPlacedOnSheet) &&
                (!onlyDuplicates || v.HasDuplicateName) &&
                (search.Length == 0 ||
                 v.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 v.ViewKind.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));

            Fill(_navViews, Apply(_navInventory.Views));
            Fill(_navSheets, Apply(_navInventory.Sheets));
            Fill(_navSchedules, Apply(_navInventory.Schedules));
        }

        private static void Fill(ObservableCollection<ViewRecord> target, IEnumerable<ViewRecord> records)
        {
            target.Clear();
            foreach (var record in records)
            {
                target.Add(record);
            }
        }

        private void OpenSelectedView()
        {
            var grid = (_navTabs.SelectedItem as TabItem)?.Content as DataGrid;
            if (grid?.SelectedItem is not ViewRecord record)
            {
                SetStatus("Select a view, sheet, or schedule to open.");
                return;
            }

            RunOnRevit($"Opening '{record.Name}'…", (_, uidoc) =>
            {
                var error = RevitActions.OpenView(uidoc, record.IdValue);
                OnUi(() => SetStatus(error ?? $"Opening '{record.Name}'."));
            });
        }

        private void ExportNavigateCsv()
        {
            if (_navInventory == null)
            {
                SetStatus("Refresh the inventory first.");
                return;
            }

            var path = PromptSavePath("Export View Inventory", "ViewInventory.xlsx",
                "Excel workbook (*.xlsx)|*.xlsx|CSV (views only) (*.csv)|*.csv");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    ExportService.WriteCsv(path, ExportService.BuildViewsTable(_navInventory.Views, "Views"));
                }
                else
                {
                    ExportService.WriteXlsx(path, new List<ExportService.Table>
                    {
                        ExportService.BuildRunMetadataTable(_exploreModelTitle, "View Inventory"),
                        ExportService.BuildViewsTable(_navInventory.Views, "Views"),
                        ExportService.BuildViewsTable(_navInventory.Sheets, "Sheets"),
                        ExportService.BuildViewsTable(_navInventory.Schedules, "Schedules")
                    });
                }

                SetStatus($"View inventory exported to {path}");
            });
        }
    }
}
