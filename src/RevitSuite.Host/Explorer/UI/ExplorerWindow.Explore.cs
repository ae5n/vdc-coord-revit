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

        /// <summary>One checkbox row in the Models filter dropdown.</summary>
        public sealed class OriginOption : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isChecked = true;

            public OriginOption(string name, int count)
            {
                Name = name;
                Count = count;
            }

            public string Name { get; }
            public int Count { get; }
            public string Label => $"{Name} ({Count:N0})";

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value)
                    {
                        return;
                    }

                    _isChecked = value;
                    PropertyChanged?.Invoke(this,
                        new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
                    CheckedChanged?.Invoke();
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            public event Action? CheckedChanged;
        }

        private readonly ObservableCollection<OriginOption> _originOptions =
            new ObservableCollection<OriginOption>();

        private ComboBox _scopeCombo = null!;
        private ComboBox _groupingCombo = null!;
        private CheckBox _includeLinksCheck = null!;
        private CheckBox _includeUncategorizedCheck = null!;
        private System.Windows.Controls.Primitives.ToggleButton _modelsFilterButton = null!;
        private int _treeBuildGeneration;
        private bool _suppressOriginRebuild;
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

            var syncCheck = new CheckBox
            {
                Content = "Sync from Revit",
                IsChecked = true,
                ToolTip = "When you select elements in Revit, find and highlight them here automatically.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            syncCheck.Checked += (_, _) => _syncFromRevitEnabled = true;
            syncCheck.Unchecked += (_, _) => _syncFromRevitEnabled = false;
            toolbar.Children.Add(syncCheck);

            toolbar.Children.Add(BuildModelsFilter());

            toolbar.Children.Add(MakeCaption("Search"));
            _searchBox = new TextBox
            {
                Width = 220,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Search category, family, type, name, level, workset, or element id. Ctrl+F focuses, Esc clears."
            };
            _searchBox.TextChanged += OnSearchChanged;
            toolbar.Children.Add(_searchBox);

            toolbar.Children.Add(MakeButton("Refresh (F5)", (_, _) => RefreshExplore()));
            toolbar.Children.Add(MakeButton("Check All", (_, _) => SetAllChecks(true)));
            toolbar.Children.Add(MakeButton("Clear Checks", (_, _) => SetAllChecks(false)));
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

        /// <summary>
        /// "Models: …" dropdown — checkbox per origin (Host + each loaded link) with
        /// Host-only / Links-only presets, so the tree can show any subset of the federation.
        /// </summary>
        private UIElement BuildModelsFilter()
        {
            _modelsFilterButton = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = "Models: All",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 0)
            };

            var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            presets.Children.Add(MakeButton("All", (_, _) => SetOriginPreset(_ => true)));
            presets.Children.Add(MakeButton("Host only", (_, _) => SetOriginPreset(o => o.Name == "Host")));
            presets.Children.Add(MakeButton("Links only", (_, _) => SetOriginPreset(o => o.Name != "Host")));

            var list = new ItemsControl { ItemsSource = _originOptions };
            var itemTemplate = new DataTemplate(typeof(OriginOption));
            var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(nameof(OriginOption.IsChecked)) { Mode = BindingMode.TwoWay });
            checkFactory.SetBinding(ContentControl.ContentProperty, new Binding(nameof(OriginOption.Label)));
            checkFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
            itemTemplate.VisualTree = checkFactory;
            list.ItemTemplate = itemTemplate;

            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(presets);
            panel.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = _modelsFilterButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                MinWidth = 260,
                Child = new Border
                {
                    Background = SystemColors.WindowBrush,
                    BorderBrush = SystemColors.ActiveBorderBrush,
                    BorderThickness = new Thickness(1),
                    Child = panel
                }
            };

            _modelsFilterButton.Checked += (_, _) => popup.IsOpen = true;
            _modelsFilterButton.Unchecked += (_, _) => popup.IsOpen = false;
            popup.Closed += (_, _) => _modelsFilterButton.IsChecked = false;

            return _modelsFilterButton;
        }

        private void SetOriginPreset(Func<OriginOption, bool> shouldCheck)
        {
            _suppressOriginRebuild = true;
            foreach (var option in _originOptions)
            {
                option.IsChecked = shouldCheck(option);
            }

            _suppressOriginRebuild = false;
            OnOriginFilterChanged();
        }

        private void OnOriginFilterChanged()
        {
            if (_suppressOriginRebuild)
            {
                return;
            }

            UpdateModelsFilterCaption();
            RebuildExploreTree();
        }

        private void UpdateModelsFilterCaption()
        {
            var total = _originOptions.Count;
            var selected = _originOptions.Count(o => o.IsChecked);
            _modelsFilterButton.Content = total == 0 || selected == total
                ? "Models: All"
                : selected == 1
                    ? $"Models: {_originOptions.First(o => o.IsChecked).Name}"
                    : $"Models: {selected}/{total}";
        }

        private void RebuildOriginOptions()
        {
            var previous = _originOptions.ToDictionary(o => o.Name, o => o.IsChecked, StringComparer.OrdinalIgnoreCase);

            _suppressOriginRebuild = true;
            _originOptions.Clear();
            foreach (var group in _exploreRecords
                         .GroupBy(r => r.Origin, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key == "Host" ? 0 : 1)
                         .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var option = new OriginOption(group.Key, group.Count())
                {
                    IsChecked = !previous.TryGetValue(group.Key, out var wasChecked) || wasChecked
                };
                option.CheckedChanged += OnOriginFilterChanged;
                _originOptions.Add(option);
            }

            _suppressOriginRebuild = false;
            UpdateModelsFilterCaption();
        }

        /// <summary>Records passing the Models filter (search applies later in TreeBuilder).</summary>
        private IReadOnlyList<ElementRecord> OriginFilteredRecords()
        {
            var selected = new HashSet<string>(
                _originOptions.Where(o => o.IsChecked).Select(o => o.Name),
                StringComparer.OrdinalIgnoreCase);

            if (_originOptions.Count == 0 || selected.Count == _originOptions.Count)
            {
                return _exploreRecords;
            }

            return _exploreRecords.Where(r => selected.Contains(r.Origin)).ToList();
        }

        private TreeView BuildTree()
        {
            var tree = new TreeView { ItemsSource = _exploreItems };
            tree.SetValue(VirtualizingStackPanel.IsVirtualizingProperty, true);
            tree.SetValue(VirtualizingStackPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);

            // Keep the selected row's background fully highlighted (blue) even while Revit has
            // focus — otherwise sync-from-Revit selections render as barely-visible inactive
            // gray. The text brush is NOT overridden here (Revit's theme applies it to all
            // rows, blanking them); selected-row text color is handled by a style trigger below.
            tree.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = SystemColors.HighlightBrush;
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
            itemStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty,
                new Binding("IsSelected") { Mode = BindingMode.TwoWay }));
            itemStyle.Setters.Add(new Setter(TreeViewItem.ForegroundProperty, SystemColors.ControlTextBrush));

            // White text only on the selected row, so it reads on the blue highlight.
            var selectedTrigger = new Trigger { Property = TreeViewItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(TreeViewItem.ForegroundProperty, SystemColors.HighlightTextBrush));
            itemStyle.Triggers.Add(selectedTrigger);
            itemStyle.Setters.Add(new EventSetter(TreeViewItem.SelectedEvent,
                new RoutedEventHandler((s, args) =>
                {
                    // Only the item itself, not bubbled child selections.
                    if (ReferenceEquals(s, args.OriginalSource))
                    {
                        ((TreeViewItem)s).BringIntoView();
                    }
                })));
            tree.ItemContainerStyle = itemStyle;

            // Double-click an instance row zooms straight to it — host or linked.
            tree.MouseDoubleClick += (_, _) =>
            {
                if (tree.SelectedItem is not ExplorerTreeItem { Record: { } record })
                {
                    return;
                }

                if (record.IsLinked && record.LinkInstanceIdValue.HasValue)
                {
                    ShowMixedByRecords(
                        Array.Empty<long>(),
                        new[] { new RevitActions.LinkedTarget(record.LinkInstanceIdValue.Value, record.IdValue) });
                }
                else if (!record.IsLinked)
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
            MarkSelectionPush();
            RunOnRevit("Showing element…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowElements(uidoc, ids);
                OnUi(() => SetStatus(error ?? $"Showing {shown:N0} element(s)."));
            });
        }

        private void ShowMixedByRecords(IReadOnlyList<long> hostIds, IReadOnlyList<RevitActions.LinkedTarget> linked)
        {
            MarkSelectionPush();
            RunOnRevit("Showing element…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowMixed(uidoc, hostIds, linked);
                OnUi(() => SetStatus(error ?? $"Showing {shown:N0} element(s) (zoomed to linked geometry)."));
            });
        }

        private void IsolateChecked()
        {
            var (hostIds, linked) = PartitionChecked();
            if (hostIds.Count == 0)
            {
                SetStatus(linked.Count > 0
                    ? "Only linked elements are checked — Revit can only isolate host elements (hide/show the whole link instead)."
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

        /// <summary>
        /// Reveals the Revit selection in the tree: matches records (host and linked),
        /// expands the group path under the current grouping, highlights the first hit.
        /// </summary>
        private void RevealRevitSelection(IReadOnlyList<long> hostIds, IReadOnlyList<RevitActions.LinkedTarget> linked)
        {
            if (_exploreRecords.Count == 0)
            {
                return;
            }

            var hostSet = new HashSet<long>(hostIds);
            var linkedSet = new HashSet<long>(linked.Select(t => t.ElementIdValue));

            var matches = _exploreRecords
                .Where(r => r.IsLinked ? linkedSet.Contains(r.IdValue) : hostSet.Contains(r.IdValue))
                .ToList();

            var totalSelected = hostIds.Count + linked.Count;
            if (matches.Count == 0)
            {
                SetStatus($"Revit selection: {totalSelected} element(s) — none in the current index (try Refresh).");
                return;
            }

            var first = matches[0];
            var revealed = FindAndReveal(first);
            var more = matches.Count > 1 ? $" (+{matches.Count - 1} more selected)" : string.Empty;
            SetStatus(revealed
                ? $"Revit selection: revealed {first.DisplayName}{(first.IsLinked ? $" from {first.Origin}" : string.Empty)}{more}."
                : $"Revit selection: {first.DisplayName} is hidden by the current search/Models filter{more}.");
        }

        /// <summary>Expands the record's group path, selects its row, and scrolls to it. Returns false when filtered out.</summary>
        private bool FindAndReveal(ElementRecord record)
        {
            var keys = TreeBuilder.GetGroupKeys(record, CurrentGrouping);
            var chain = new List<ExplorerTreeItem>();

            var current = _exploreItems.FirstOrDefault(i =>
                string.Equals(i.Label, keys[0], StringComparison.OrdinalIgnoreCase));
            if (current == null)
            {
                return false;
            }

            current.IsExpanded = true;
            chain.Add(current);
            foreach (var key in new[] { keys[1], keys[2] })
            {
                current = current.Children.FirstOrDefault(c =>
                    string.Equals(c.Label, key, StringComparison.OrdinalIgnoreCase));
                if (current == null)
                {
                    return false;
                }

                current.IsExpanded = true;
                chain.Add(current);
            }

            var instance = current.Children.FirstOrDefault(c =>
                c.Record != null &&
                c.Record.IdValue == record.IdValue &&
                c.Record.IsLinked == record.IsLinked &&
                string.Equals(c.Record.Origin, record.Origin, StringComparison.OrdinalIgnoreCase));
            if (instance == null)
            {
                return false;
            }

            instance.IsSelected = true;

            // The tree is virtualized, so the row's container may not exist yet. After the
            // expansion layout pass, walk the container chain, forcing each level to realize,
            // then physically scroll the instance row into view.
            Dispatcher.BeginInvoke(
                new Action(() => ScrollPathIntoView(chain, instance)),
                DispatcherPriority.Background);
            return true;
        }

        private void ScrollPathIntoView(IReadOnlyList<ExplorerTreeItem> chain, ExplorerTreeItem instance)
        {
            try
            {
                ItemsControl parent = _exploreTree;
                foreach (var item in chain)
                {
                    var container = RealizeContainer(parent, item);
                    if (container == null)
                    {
                        return;
                    }

                    parent = container;
                }

                RealizeContainer(parent, instance)?.BringIntoView();
            }
            catch (Exception ex)
            {
                LogManager.Error("explorer", "Scroll-to-selection failed.", ex);
            }
        }

        /// <summary>
        /// Forces the virtualized container for an item to be generated (via
        /// VirtualizingStackPanel.BringIndexIntoViewPublic) and returns it.
        /// </summary>
        private static TreeViewItem? RealizeContainer(ItemsControl parent, ExplorerTreeItem item)
        {
            parent.ApplyTemplate();
            parent.UpdateLayout();

            var index = parent.Items.IndexOf(item);
            if (index < 0)
            {
                return null;
            }

            if (FindItemsHostPanel(parent) is VirtualizingStackPanel panel)
            {
                panel.BringIndexIntoViewPublic(index);
                parent.UpdateLayout();
            }

            return parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
        }

        private static Panel? FindItemsHostPanel(ItemsControl control)
        {
            var presenter = FindVisualChild<ItemsPresenter>(control);
            if (presenter == null)
            {
                control.UpdateLayout();
                presenter = FindVisualChild<ItemsPresenter>(control);
            }

            if (presenter == null || System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter) == 0)
            {
                return null;
            }

            return System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0) as Panel;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                {
                    return typed;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
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

        /// <summary>Changing scope or the include toggles re-indexes immediately — no stale tree.</summary>
        private void WireExploreAutoRefresh()
        {
            _scopeCombo.SelectionChanged += (_, _) => RefreshExplore();
            _includeLinksCheck.Checked += (_, _) => RefreshExplore();
            _includeLinksCheck.Unchecked += (_, _) => RefreshExplore();
            _includeUncategorizedCheck.Checked += (_, _) => RefreshExplore();
            _includeUncategorizedCheck.Unchecked += (_, _) => RefreshExplore();
        }

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
                var (loadedLinks, unloadedLinks) = ElementCollectionService.CountLinkStatus(uidoc.Document);

                OnUi(() =>
                {
                    _exploreRecords = records;
                    _exploreModelTitle = title;
                    RebuildOriginOptions();
                    RebuildExploreTree();

                    var perModel = string.Join(" · ", records
                        .GroupBy(r => r.Origin, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.Key == "Host" ? 0 : 1)
                        .ThenByDescending(g => g.Count())
                        .Take(6)
                        .Select(g => $"{g.Key} {g.Count():N0}"));
                    var linkNotes = new List<string>();
                    if (!includeLinks && loadedLinks > 0)
                    {
                        linkNotes.Add($"⚠ {loadedLinks} loaded link(s) NOT indexed — check 'Include linked models'");
                    }

                    if (unloadedLinks > 0)
                    {
                        linkNotes.Add($"⚠ {unloadedLinks} link(s) unloaded in Revit — reload via Manage Links to index them");
                    }

                    var linkHint = linkNotes.Count > 0 ? "  " + string.Join("  ", linkNotes) + "." : string.Empty;
                    SetStatus($"Indexed {records.Count:N0} element(s) ({ScopeLabel(scope)}): {perModel}{linkHint}");
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
            var records = OriginFilteredRecords();
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
        /// Splits checked records into host ids and linked targets. When nothing is checked,
        /// falls back to the highlighted tree item so right-click and toolbar actions work
        /// on the row under the cursor.
        /// </summary>
        private (List<long> HostIds, List<RevitActions.LinkedTarget> Linked) PartitionChecked()
        {
            var checkedRecords = GetCheckedRecords();
            if (checkedRecords.Count == 0 && _exploreTree.SelectedItem is ExplorerTreeItem highlighted)
            {
                var fallback = new List<ElementRecord>();
                highlighted.CollectAllRecords(fallback);
                checkedRecords = fallback;
            }

            var hostIds = checkedRecords.Where(r => !r.IsLinked).Select(r => r.IdValue).Distinct().ToList();
            var linked = checkedRecords
                .Where(r => r.IsLinked && r.LinkInstanceIdValue.HasValue)
                .Select(r => new RevitActions.LinkedTarget(r.LinkInstanceIdValue!.Value, r.IdValue))
                .Distinct()
                .ToList();
            return (hostIds, linked);
        }

        private void SelectChecked()
        {
            var (hostIds, linked) = PartitionChecked();
            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus("Nothing is checked or highlighted.");
                return;
            }

            MarkSelectionPush();
            RunOnRevit("Selecting elements…", (_, uidoc) =>
            {
                var (selected, failed, error) = RevitActions.SelectMixed(uidoc, hostIds, linked);
                OnUi(() => SetStatus(error ?? BuildActionStatus(
                    $"Selected {selected:N0} element(s)", failed, linked.Count)));
            });
        }

        private void ShowChecked()
        {
            var (hostIds, linked) = PartitionChecked();
            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus("Nothing is checked or highlighted.");
                return;
            }

            MarkSelectionPush();
            RunOnRevit("Showing elements…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowMixed(uidoc, hostIds, linked);
                OnUi(() => SetStatus(error ?? BuildActionStatus(
                    $"Showing {shown:N0} element(s)", 0, linked.Count)));
            });
        }

        private static string BuildActionStatus(string headline, int failed, int linkedCount)
        {
            var notes = new List<string>();
            if (failed > 0)
            {
                notes.Add($"{failed} could not be resolved");
            }

            if (linkedCount > 0)
            {
                notes.Add($"{linkedCount} from linked models");
            }

            return notes.Count == 0 ? $"{headline}." : $"{headline} ({string.Join(", ", notes)}).";
        }

        private void ExportExploreCsv()
        {
            // Export exactly what the tree shows: Models filter plus search filter.
            var visible = TreeBuilder.Filter(OriginFilteredRecords(), _searchBox.Text ?? string.Empty).ToList();
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
            var (hostIds, linked) = PartitionChecked();
            if (hostIds.Count == 0)
            {
                SetStatus(linked.Count > 0
                    ? "Only linked elements are checked — elements inside links can only be deleted in the link's own file."
                    : "Nothing is checked.");
                return;
            }

            if (linked.Count > 0)
            {
                SetStatus($"Note: {linked.Count} linked element(s) are excluded — deletion only applies to host elements.");
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

        private ElementRecord? _detailsRecord;

        private void SetAllChecks(bool value)
        {
            foreach (var item in _exploreItems)
            {
                item.IsChecked = value;
            }

            SetStatus(value ? "Checked everything currently in the tree." : "Cleared all checks.");
        }

        private void ShowDetails(ExplorerTreeItem? item)
        {
            _detailsPanel.Children.Clear();
            _detailsRecord = item?.Record;
            if (item == null)
            {
                return;
            }

            // Rows render as borderless read-only TextBoxes so any value can be selected
            // and copied directly, and Copy All grabs the whole panel at once.
            void AddRow(string label, string? value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var caption = new TextBlock
                {
                    Text = label + ":",
                    FontWeight = FontWeights.SemiBold,
                    Width = 96,
                    VerticalAlignment = VerticalAlignment.Top
                };
                DockPanel.SetDock(caption, Dock.Left);
                row.Children.Add(caption);
                row.Children.Add(new TextBox
                {
                    Text = value,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(0)
                });
                _detailsPanel.Children.Add(row);
            }

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var copyButton = MakeButton("Copy All", (_, _) => CopyDetails());
            copyButton.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(copyButton, Dock.Right);
            header.Children.Add(copyButton);
            header.Children.Add(new TextBox
            {
                Text = item.Record == null ? item.Label : item.Record.DisplayName,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            _detailsPanel.Children.Add(header);

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

            var loadButton = MakeButton("Load Parameters", (_, _) => LoadParameters(record));
            loadButton.Margin = new Thickness(0, 8, 0, 0);
            loadButton.HorizontalAlignment = HorizontalAlignment.Left;
            _detailsPanel.Children.Add(loadButton);
        }

        private void CopyDetails()
        {
            if (_detailsRecord == null)
            {
                SetStatus("Highlight an element row to copy its details.");
                return;
            }

            var r = _detailsRecord;
            var lines = new List<string>
            {
                $"Element Id\t{r.IdValue}",
                $"Unique Id\t{r.UniqueId}",
                $"Category\t{r.Category}",
                $"Family\t{r.Family}",
                $"Type\t{r.TypeName}",
                $"Name\t{r.InstanceName}",
                $"Level\t{r.LevelName}",
                $"Workset\t{r.WorksetName}",
                $"Owner View\t{r.OwnerViewName}",
                $"Design Option\t{r.DesignOptionName}",
                $"Origin\t{r.Origin}"
            };

            if (_detailsPanel.Children.OfType<DataGrid>().FirstOrDefault()?.ItemsSource
                is IEnumerable<ParameterValueDto> parameters)
            {
                lines.Add(string.Empty);
                lines.AddRange(parameters.Select(p => $"{p.DisplayName}\t{p.DisplayValue}"));
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            SetStatus($"Copied details of element {r.IdValue} to the clipboard (tab-separated, Excel-ready).");
        }

        private void LoadParameters(ElementRecord record)
        {
            RunOnRevit("Loading parameters…", (_, uidoc) =>
            {
                // Linked records resolve inside their link document; host records in the host.
                Autodesk.Revit.DB.Element? element;
                if (record.IsLinked && record.LinkInstanceIdValue.HasValue)
                {
                    var link = uidoc.Document.GetElement(
                            new Autodesk.Revit.DB.ElementId(record.LinkInstanceIdValue.Value))
                        as Autodesk.Revit.DB.RevitLinkInstance;
                    element = link?.GetLinkDocument()?.GetElement(new Autodesk.Revit.DB.ElementId(record.IdValue));
                }
                else
                {
                    element = uidoc.Document.GetElement(new Autodesk.Revit.DB.ElementId(record.IdValue));
                }

                if (element == null)
                {
                    OnUi(() => SetStatus("Element no longer exists (or its link is unloaded)."));
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
                        ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
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
