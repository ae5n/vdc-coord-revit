using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        private readonly ObservableCollection<ExplorerTreeItem> _exploreItems =
            new ObservableCollection<ExplorerTreeItem>();

        private IReadOnlyList<ElementRecord> _exploreRecords = Array.Empty<ElementRecord>();
        private string _exploreModelTitle = string.Empty;

        private ComboBox _scopeCombo = null!;
        private ComboBox _groupingCombo = null!;
        private CheckBox _includeLinksCheck = null!;
        private CheckBox _includeUncategorizedCheck = null!;
        private int _treeBuildGeneration;
        private TextBox _searchBox = null!;
        private TreeView _exploreTree = null!;
        private StackPanel _detailsPanel = null!;
        private DispatcherTimer? _searchDebounce;

        private UIElement BuildExploreTab()
        {
            var layout = new Grid { Margin = new Thickness(8) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // --- Toolbar ---
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

            toolbar.Children.Add(MakeCaption("Scope"));
            _scopeCombo = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 12, 0) };
            _scopeCombo.Items.Add("Entire Project");
            _scopeCombo.Items.Add("Active View");
            _scopeCombo.Items.Add("Current Selection");
            _scopeCombo.SelectedIndex = 0;
            toolbar.Children.Add(_scopeCombo);

            toolbar.Children.Add(MakeCaption("Group by"));
            _groupingCombo = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 12, 0) };
            foreach (var mode in Enum.GetNames(typeof(GroupingMode)))
            {
                _groupingCombo.Items.Add(mode);
            }

            _groupingCombo.SelectedIndex = 0;
            _groupingCombo.SelectionChanged += (_, _) => RebuildExploreTree();
            toolbar.Children.Add(_groupingCombo);

            _includeLinksCheck = new CheckBox
            {
                Content = "Include linked models",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            toolbar.Children.Add(_includeLinksCheck);

            _includeUncategorizedCheck = new CheckBox
            {
                Content = "Include uncategorized",
                ToolTip = "Also index elements with no category (sketch lines, internal elements). Off by default to reduce noise.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            toolbar.Children.Add(_includeUncategorizedCheck);

            toolbar.Children.Add(MakeCaption("Search"));
            _searchBox = new TextBox { Width = 220, Margin = new Thickness(0, 0, 12, 0) };
            _searchBox.TextChanged += OnSearchChanged;
            toolbar.Children.Add(_searchBox);

            toolbar.Children.Add(MakeButton("Refresh", (_, _) => RefreshExplore()));
            Grid.SetRow(toolbar, 0);
            layout.Children.Add(toolbar);

            // --- Tree + details ---
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            _exploreTree = BuildTree();
            Grid.SetColumn(_exploreTree, 0);
            body.Children.Add(_exploreTree);

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(splitter, 1);
            body.Children.Add(splitter);

            _detailsPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            var detailsScroll = new ScrollViewer
            {
                Content = _detailsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(detailsScroll, 2);
            body.Children.Add(detailsScroll);

            Grid.SetRow(body, 1);
            layout.Children.Add(body);

            // --- Actions ---
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton("Select in Revit", (_, _) => SelectChecked()));
            actions.Children.Add(MakeButton("Show / Zoom", (_, _) => ShowChecked()));
            actions.Children.Add(MakeButton("Isolate", (_, _) => IsolateChecked()));
            actions.Children.Add(MakeButton("Reset Isolate", (_, _) => ResetIsolate()));
            actions.Children.Add(MakeButton("Export CSV", (_, _) => ExportExploreCsv()));
            actions.Children.Add(MakeButton("Safe Delete…", (_, _) => SafeDeleteChecked(), destructive: true));
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);

            return layout;
        }

        private TreeView BuildTree()
        {
            var tree = new TreeView { ItemsSource = _exploreItems };
            tree.SetValue(VirtualizingStackPanel.IsVirtualizingProperty, true);
            tree.SetValue(VirtualizingStackPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            tree.SelectedItemChanged += (_, args) => ShowDetails(args.NewValue as ExplorerTreeItem);

            var template = new HierarchicalDataTemplate(typeof(ExplorerTreeItem))
            {
                ItemsSource = new Binding("Children")
            };

            var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
            panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkFactory.SetValue(CheckBox.IsThreeStateProperty, true);
            checkFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("IsChecked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            panelFactory.AppendChild(checkFactory);

            var labelFactory = new FrameworkElementFactory(typeof(TextBlock));
            labelFactory.SetBinding(TextBlock.TextProperty, new Binding("Label"));
            labelFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
            panelFactory.AppendChild(labelFactory);

            var countFactory = new FrameworkElementFactory(typeof(TextBlock));
            countFactory.SetBinding(TextBlock.TextProperty, new Binding("CountText"));
            countFactory.SetValue(TextBlock.ForegroundProperty, SystemColors.GrayTextBrush);
            panelFactory.AppendChild(countFactory);

            template.VisualTree = panelFactory;
            tree.ItemTemplate = template;

            var itemStyle = new Style(typeof(TreeViewItem));
            itemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
            tree.ItemContainerStyle = itemStyle;

            // Double-click an instance row zooms straight to it.
            tree.MouseDoubleClick += (_, _) =>
            {
                if (tree.SelectedItem is ExplorerTreeItem { Record: { IsLinked: false } record })
                {
                    ShowByIds(new[] { record.IdValue });
                }
            };

            var menu = new ContextMenu();
            void AddMenuItem(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }

            AddMenuItem("Select in Revit", SelectChecked);
            AddMenuItem("Show / zoom", ShowChecked);
            AddMenuItem("Isolate in active view", IsolateChecked);
            AddMenuItem("Reset isolate", ResetIsolate);
            menu.Items.Add(new Separator());
            AddMenuItem("Copy Element Id(s)", CopyElementIds);
            AddMenuItem("Copy UniqueId", CopyUniqueId);
            menu.Items.Add(new Separator());
            AddMenuItem("Safe delete…", SafeDeleteChecked);
            tree.ContextMenu = menu;

            return tree;
        }

        private void ShowByIds(IReadOnlyList<long> ids)
        {
            RunOnRevit("Showing element…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowElements(uidoc, ids);
                OnUi(() => SetStatus(error ?? $"Showing {shown:N0} element(s)."));
            });
        }

        private void IsolateChecked()
        {
            var (hostIds, linkedSkipped) = PartitionChecked();
            if (hostIds.Count == 0)
            {
                SetStatus(linkedSkipped > 0
                    ? "Only linked elements are checked — linked elements cannot be isolated in the host view."
                    : "Nothing is checked.");
                return;
            }

            RunOnRevit("Isolating elements…", (_, uidoc) =>
            {
                var (isolated, error) = RevitActions.IsolateElements(uidoc, hostIds);
                OnUi(() => SetStatus(error ??
                    $"Temporarily isolated {isolated:N0} element(s) in the active view. Use Reset Isolate to restore."));
            });
        }

        private void CopyElementIds()
        {
            var records = GetCheckedRecords();
            if (records.Count == 0 && _exploreTree.SelectedItem is ExplorerTreeItem highlighted)
            {
                var fallback = new List<ElementRecord>();
                highlighted.CollectAllRecords(fallback);
                records = fallback;
            }

            if (records.Count == 0)
            {
                SetStatus("Nothing checked or highlighted to copy.");
                return;
            }

            Clipboard.SetText(string.Join(",", records.Select(r => r.IdValue).Distinct()));
            SetStatus($"Copied {records.Count:N0} element id(s) to the clipboard.");
        }

        private void CopyUniqueId()
        {
            if (_exploreTree.SelectedItem is not ExplorerTreeItem { Record: { } record })
            {
                SetStatus("Highlight an element row to copy its UniqueId.");
                return;
            }

            Clipboard.SetText(record.UniqueId);
            SetStatus($"Copied UniqueId of element {record.IdValue}.");
        }

        private void ResetIsolate()
        {
            RunOnRevit("Resetting isolate…", (_, uidoc) =>
            {
                var error = RevitActions.ResetIsolate(uidoc);
                OnUi(() => SetStatus(error ?? "Temporary isolate mode reset."));
            });
        }

        private ExplorerScope CurrentScope => _scopeCombo.SelectedIndex switch
        {
            1 => ExplorerScope.ActiveView,
            2 => ExplorerScope.CurrentSelection,
            _ => ExplorerScope.EntireProject
        };

        private GroupingMode CurrentGrouping =>
            (GroupingMode)Enum.Parse(typeof(GroupingMode), (string)_groupingCombo.SelectedItem);

        private void RefreshExplore()
        {
            var scope = CurrentScope;
            var includeLinks = _includeLinksCheck.IsChecked == true;
            var includeUncategorized = _includeUncategorizedCheck.IsChecked == true;

            RunOnRevit("Indexing model…", (_, uidoc) =>
            {
                var records = ElementCollectionService.Collect(
                    uidoc, scope, includeLinks, includeUncategorized,
                    progress: count => ReportProgress($"Indexing model… {count:N0} elements"),
                    isCancelled: () => _cancelRequested);
                var title = uidoc.Document.Title;

                OnUi(() =>
                {
                    _exploreRecords = records;
                    _exploreModelTitle = title;
                    RebuildExploreTree();
                    SetStatus($"Indexed {records.Count:N0} element(s) from '{title}' ({ScopeLabel(scope)}).");
                });
            });
        }

        private static string ScopeLabel(ExplorerScope scope) => scope switch
        {
            ExplorerScope.ActiveView => "Active View",
            ExplorerScope.CurrentSelection => "Current Selection",
            _ => "Entire Project"
        };

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Stop();
            _searchDebounce.Tick -= OnSearchDebounceTick;
            _searchDebounce.Tick += OnSearchDebounceTick;
            _searchDebounce.Start();
        }

        private void OnSearchDebounceTick(object? sender, EventArgs e)
        {
            _searchDebounce?.Stop();
            RebuildExploreTree();
        }

        private async void RebuildExploreTree()
        {
            if (_exploreTree == null)
            {
                return;
            }

            // Grouping/search over immutable DTOs runs off the UI thread so typing stays
            // responsive on 100k+ element indexes. A generation counter drops stale results
            // when the user changes the search/grouping before the previous build finishes.
            var generation = ++_treeBuildGeneration;
            var records = _exploreRecords;
            var grouping = CurrentGrouping;
            var search = _searchBox.Text;

            IReadOnlyList<ExplorerTreeNode> nodes;
            try
            {
                nodes = await System.Threading.Tasks.Task.Run(() => TreeBuilder.Build(records, grouping, search));
            }
            catch (Exception ex)
            {
                LogManager.Error("explorer", "Tree build failed.", ex);
                SetStatus($"Error building tree: {ex.Message}");
                return;
            }

            if (generation != _treeBuildGeneration)
            {
                return;
            }

            _exploreItems.Clear();
            foreach (var node in nodes)
            {
                _exploreItems.Add(new ExplorerTreeItem(node, null));
            }
        }

        private IReadOnlyList<ElementRecord> GetCheckedRecords()
        {
            var records = new List<ElementRecord>();
            foreach (var item in _exploreItems)
            {
                item.CollectCheckedRecords(records);
            }

            return records;
        }

        /// <summary>
        /// Splits checked records into selectable host ids and skipped linked count. When
        /// nothing is checked, falls back to the highlighted tree item so right-click and
        /// toolbar actions work on the row under the cursor.
        /// </summary>
        private (List<long> HostIds, int LinkedSkipped) PartitionChecked()
        {
            var checkedRecords = GetCheckedRecords();
            if (checkedRecords.Count == 0 && _exploreTree.SelectedItem is ExplorerTreeItem highlighted)
            {
                var fallback = new List<ElementRecord>();
                highlighted.CollectAllRecords(fallback);
                checkedRecords = fallback;
            }

            var hostIds = checkedRecords.Where(r => !r.IsLinked).Select(r => r.IdValue).Distinct().ToList();
            return (hostIds, checkedRecords.Count(r => r.IsLinked));
        }

        private void SelectChecked()
        {
            var (hostIds, linkedSkipped) = PartitionChecked();
            if (hostIds.Count == 0)
            {
                SetStatus(linkedSkipped > 0
                    ? "Only linked elements are checked — linked elements cannot be selected in the host model."
                    : "Nothing is checked.");
                return;
            }

            RunOnRevit("Selecting elements…", (_, uidoc) =>
            {
                var selected = RevitActions.SelectElements(uidoc, hostIds);
                OnUi(() => SetStatus(BuildActionStatus($"Selected {selected:N0} element(s)", hostIds.Count - selected, linkedSkipped)));
            });
        }

        private void ShowChecked()
        {
            var (hostIds, linkedSkipped) = PartitionChecked();
            if (hostIds.Count == 0)
            {
                SetStatus(linkedSkipped > 0
                    ? "Only linked elements are checked — Revit cannot zoom to elements inside links."
                    : "Nothing is checked.");
                return;
            }

            RunOnRevit("Showing elements…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowElements(uidoc, hostIds);
                OnUi(() => SetStatus(error ?? BuildActionStatus($"Showing {shown:N0} element(s)", hostIds.Count - shown, linkedSkipped)));
            });
        }

        private static string BuildActionStatus(string headline, int missing, int linkedSkipped)
        {
            var notes = new List<string>();
            if (missing > 0)
            {
                notes.Add($"{missing} no longer exist");
            }

            if (linkedSkipped > 0)
            {
                notes.Add($"{linkedSkipped} linked skipped");
            }

            return notes.Count == 0 ? $"{headline}." : $"{headline} ({string.Join(", ", notes)}).";
        }

        private void ExportExploreCsv()
        {
            var visible = TreeBuilder.Filter(_exploreRecords, _searchBox.Text ?? string.Empty).ToList();
            if (visible.Count == 0)
            {
                SetStatus("Nothing to export — refresh the model index first.");
                return;
            }

            var path = PromptSavePath("Export Elements", $"{SafeTitle()}_Elements.csv", "CSV files (*.csv)|*.csv");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                ExportService.WriteCsv(path, ExportService.BuildElementsTable(visible));
                SetStatus($"Exported {visible.Count:N0} row(s) to {path}");
            });
        }

        private string SafeTitle() =>
            string.IsNullOrWhiteSpace(_exploreModelTitle) ? "Model" : _exploreModelTitle.Replace(' ', '_');

        private void SafeDeleteChecked()
        {
            var (hostIds, linkedSkipped) = PartitionChecked();
            if (hostIds.Count == 0)
            {
                SetStatus(linkedSkipped > 0
                    ? "Only linked elements are checked — elements inside links cannot be deleted from the host model."
                    : "Nothing is checked.");
                return;
            }

            RunOnRevit("Checking elements before delete…", (_, uidoc) =>
            {
                var preflight = RevitActions.BuildDeletePreflight(uidoc.Document, hostIds);

                OnUi(() =>
                {
                    if (preflight.ElementCount == 0)
                    {
                        SetStatus("The checked elements no longer exist.");
                        return;
                    }

                    if (!DeleteConfirmationDialog.Confirm(this, preflight))
                    {
                        SetStatus("Delete cancelled.");
                        return;
                    }

                    RunOnRevit("Deleting elements…", (_, uidoc2) =>
                    {
                        var (deleted, error) = RevitActions.DeleteElements(uidoc2.Document, hostIds);
                        OnUi(() =>
                        {
                            SetStatus(error ?? $"Deleted {deleted:N0} element(s) (including dependents removed by Revit).");
                            if (error == null)
                            {
                                RefreshExplore();
                            }
                        });
                    });
                });
            });
        }

        private void ShowDetails(ExplorerTreeItem? item)
        {
            _detailsPanel.Children.Clear();
            if (item == null)
            {
                return;
            }

            void AddRow(string label, string? value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                block.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { FontWeight = FontWeights.SemiBold });
                block.Inlines.Add(new System.Windows.Documents.Run(value));
                _detailsPanel.Children.Add(block);
            }

            _detailsPanel.Children.Add(new TextBlock
            {
                Text = item.Record == null ? item.Label : item.Record.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            if (item.Record == null)
            {
                AddRow("Elements", item.Count.ToString("N0"));
                return;
            }

            var record = item.Record;
            AddRow("Element Id", record.IdValue.ToString());
            AddRow("Unique Id", record.UniqueId);
            AddRow("Category", record.Category);
            AddRow("Family", record.Family);
            AddRow("Type", record.TypeName);
            AddRow("Level", record.LevelName);
            AddRow("Workset", record.WorksetName);
            AddRow("Owner View", record.OwnerViewName);
            AddRow("Design Option", record.DesignOptionName);
            AddRow("Origin", record.Origin);
            AddRow("Flags", string.Join(", ", new[]
            {
                record.IsViewSpecific ? "View-specific" : null,
                record.IsPinned ? "Pinned" : null,
                record.IsInGroup ? "In group" : null,
                record.IsLinked ? "Linked" : null
            }.Where(f => f != null)));

            if (!record.IsLinked)
            {
                var loadButton = MakeButton("Load Parameters", (_, _) => LoadParameters(record));
                loadButton.Margin = new Thickness(0, 8, 0, 0);
                loadButton.HorizontalAlignment = HorizontalAlignment.Left;
                _detailsPanel.Children.Add(loadButton);
            }
        }

        private void LoadParameters(ElementRecord record)
        {
            RunOnRevit("Loading parameters…", (_, uidoc) =>
            {
                var element = uidoc.Document.GetElement(new Autodesk.Revit.DB.ElementId(record.IdValue));
                if (element == null)
                {
                    OnUi(() => SetStatus("Element no longer exists."));
                    return;
                }

                var parameters = ParameterExtractor.ExtractAll(element);
                OnUi(() =>
                {
                    // Remove a previous parameter list if present, then append the new one.
                    var existing = _detailsPanel.Children.OfType<DataGrid>().FirstOrDefault();
                    if (existing != null)
                    {
                        _detailsPanel.Children.Remove(existing);
                    }

                    var grid = new DataGrid
                    {
                        AutoGenerateColumns = false,
                        IsReadOnly = true,
                        HeadersVisibility = DataGridHeadersVisibility.Column,
                        Margin = new Thickness(0, 8, 0, 0),
                        MaxHeight = 400,
                        ItemsSource = parameters
                    };
                    grid.Columns.Add(new DataGridTextColumn
                    {
                        Header = "Parameter",
                        Binding = new Binding("DisplayName"),
                        Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                    });
                    grid.Columns.Add(new DataGridTextColumn
                    {
                        Header = "Value",
                        Binding = new Binding("DisplayValue"),
                        Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                    });
                    _detailsPanel.Children.Add(grid);
                    SetStatus($"Loaded {parameters.Count} parameter(s) for element {record.IdValue}.");
                });
            });
        }
    }
}
