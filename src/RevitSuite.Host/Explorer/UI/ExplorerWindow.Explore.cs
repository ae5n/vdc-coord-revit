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

        /// <summary>
        /// Authoritative checked set, keyed by origin+id so it survives regroup, search,
        /// Models-filter, and view-mode rebuilds (tree rows merely mirror it).
        /// </summary>
        private readonly HashSet<string> _checkedKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Latest passive snapshot of what the active view shows (hidden indicators + Hidden view).</summary>
        private ViewVisibilitySnapshot? _viewSnapshot;

        private ComboBox _viewModeCombo = null!;
        private TextBlock _checkedCountText = null!;

        /// <summary>Live "n checked" readout next to the action buttons.</summary>
        private void UpdateCheckedCount()
        {
            if (_checkedCountText == null)
            {
                return;
            }

            var count = _checkedKeys.Count;
            _checkedCountText.Text = count == 0 ? "none checked" : $"{count:N0} checked";
            _checkedCountText.Foreground = count == 0
                ? SystemColors.GrayTextBrush
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x15, 0x65, 0xC0));
            _checkedCountText.FontWeight = count == 0 ? FontWeights.Normal : FontWeights.SemiBold;
        }

        private static string KeyOf(ElementRecord record) => record.Origin + "|" + record.IdValue;

        /// <summary>User toggled a row's checkbox: update the authoritative key set.</summary>
        private void OnUserCheckChanged(ExplorerTreeItem item, bool isChecked)
        {
            var records = new List<ElementRecord>();
            item.CollectAllRecords(records);
            foreach (var record in records)
            {
                if (isChecked)
                {
                    _checkedKeys.Add(KeyOf(record));
                }
                else
                {
                    _checkedKeys.Remove(KeyOf(record));
                }
            }

            UpdateCheckedCount();
        }

        /// <summary>
        /// Re-captures the view's actual visibility after an operation that changed it, then
        /// refreshes the passive hidden indicators (and the Hidden view, if active).
        /// </summary>
        private void RecaptureViewState(Autodesk.Revit.UI.UIDocument uidoc)
        {
            try
            {
                var snapshot = ViewStateService.Capture(uidoc, _exploreRecords);
                OnUi(() =>
                {
                    _viewSnapshot = snapshot;
                    LogManager.Info("explorer-scroll",
                        $"recapture: mode={_viewModeCombo.SelectedIndex}");
                    if (_viewModeCombo.SelectedIndex == 2)
                    {
                        // Hidden-only view: hide/unhide DOES change what this mode lists.
                        RebuildOrRefreshIndicators();
                    }
                    else
                    {
                        foreach (var root in _exploreItems)
                        {
                            root.ApplyIndicators();
                        }
                    }
                });
            }
            catch
            {
                // Indicators self-heal on the next capture; never fail the operation.
            }
        }

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

        /// <summary>True while a rebuild re-applies selection — auto-scroll must stay put.</summary>
        private bool _suppressAutoScroll;

        /// <summary>Keys of the rows the tree currently shows — set by every rebuild.</summary>
        private HashSet<string>? _lastTreeKeys;

        /// <summary>
        /// Rebuilds only when the row set actually changed; otherwise refreshes the
        /// indicators in place. Hides/unhides (ours or Revit's) never change membership,
        /// so they must never rebuild — rebuilding is what disturbs scroll and selection.
        /// </summary>
        private void RebuildOrRefreshIndicators()
        {
            var current = ComputeTreeRecords();
            if (_lastTreeKeys != null &&
                current.Count == _lastTreeKeys.Count &&
                current.All(r => _lastTreeKeys.Contains(KeyOf(r))))
            {
                LogManager.Info("explorer-scroll", "membership unchanged — indicators refreshed in place");
                foreach (var root in _exploreItems)
                {
                    root.ApplyIndicators();
                }

                return;
            }

            RebuildExploreTree();
        }

        private ScrollViewer? _treeScrollViewer;

        private ScrollViewer? GetTreeScrollViewer()
        {
            if (_treeScrollViewer != null)
            {
                return _treeScrollViewer;
            }

            return _treeScrollViewer = FindDescendantScrollViewer(_exploreTree);
        }

        private static ScrollViewer? FindDescendantScrollViewer(System.Windows.DependencyObject? root)
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer viewer)
                {
                    return viewer;
                }

                if (FindDescendantScrollViewer(child) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }

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
            // Active View is the default: it is where the hidden-eye indicators live, so
            // the annotation and its reference point always match. (The last used scope is
            // restored from settings on later launches.)
            _scopeCombo.SelectedIndex = 1;
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
                ToolTip = "Two-way live sync: selecting in Revit highlights here, and model edits, " +
                          "hide/unhide, and view switches update the tree automatically (no F5 needed).",
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

            toolbar.Children.Add(MakeCaption("View"));
            _viewModeCombo = new ComboBox
            {
                Width = 120,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "All: everything indexed · Checked only: your current checked set together · " +
                          "Hidden only: everything hidden in the active view together"
            };
            _viewModeCombo.Items.Add("All");
            _viewModeCombo.Items.Add("Checked only");
            _viewModeCombo.Items.Add("Hidden only");
            _viewModeCombo.SelectedIndex = 0;
            _viewModeCombo.SelectionChanged += (_, _) => RebuildExploreTree();
            toolbar.Children.Add(_viewModeCombo);

            toolbar.Children.Add(BuildLegendButton());

            toolbar.Children.Add(MakeIconButton("", "Refresh (F5)", (_, _) => RefreshExplore(),
                "Re-index the model"));
            toolbar.Children.Add(MakeIconButton("", "Check All", (_, _) => SetAllChecks(true),
                "Check everything currently shown in the tree"));
            toolbar.Children.Add(MakeIconButton("", "Clear", (_, _) => SetAllChecks(false),
                "Clear all checks"));
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

            // Rows derive checkbox/hidden state from the authoritative stores, so state
            // survives rebuilds and lazily expanded rows are born correct. Hidden
            // indicators describe the ACTIVE VIEW, so they only show where that reference
            // point is unambiguous: Active View scope, or the explicit Hidden-only mode.
            ExplorerTreeItem.CheckClassifier = r => _checkedKeys.Contains(KeyOf(r));
            ExplorerTreeItem.HiddenClassifier =
                r => HiddenIndicatorsEnabled && _viewSnapshot?.Classify(r) == false;
            ExplorerTreeItem.HiddenReasonClassifier = r => _viewSnapshot?.DescribeHidden(r);
            ExplorerTreeItem.HiddenTagClassifier = r => _viewSnapshot?.HiddenTag(r);
            ExplorerTreeItem.UserCheckChanged += OnUserCheckChanged;

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

            // --- Actions: icons + paired action/reset groups so related buttons read together ---
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            actions.Children.Add(new TextBlock
            {
                Text = "Act on ☑ / highlighted:",
                ToolTip = "Actions target the checked rows. When nothing is checked, they act on " +
                          "the highlighted row instead (the status bar says so). Space toggles the " +
                          "highlighted row's checkbox. Safe Delete is checked-only.",
                Foreground = SystemColors.GrayTextBrush,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _checkedCountText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "How many elements are currently checked across the whole tree " +
                          "(survives search, grouping, and Models-filter changes)"
            };
            UpdateCheckedCount();
            actions.Children.Add(_checkedCountText);

            actions.Children.Add(MakeIconButton("", "Select", (_, _) => SelectChecked(),
                "Select the checked elements in Revit (or the highlighted row when nothing is checked)"));
            actions.Children.Add(MakeIconButton("", "Show", (_, _) => ShowChecked(),
                "Zoom to the checked elements in Revit (or the highlighted row when nothing is checked)"));

            actions.Children.Add(MakePair(
                MakeIconButton("", "Focus 3D", (_, _) => FocusChecked(),
                    "Section-box the active 3D view around the checked elements — or the highlighted row " +
                    "when nothing is checked (hides other links)"),
                MakeIconButton("", null, (_, _) => ResetFocus(),
                    "Reset Focus — restore section box and link visibility")));

            actions.Children.Add(MakePair(
                MakeIconButton("", "Isolate", (_, _) => IsolateChecked(),
                    "Temporarily isolate the checked host elements in the active view " +
                    "(or the highlighted row when nothing is checked)"),
                MakeIconButton("", null, (_, _) => ResetIsolate(),
                    "Reset Isolate — end temporary hide/isolate mode")));

            actions.Children.Add(MakePair(
                MakeIconButton("", "Hide", (_, _) => HideChecked(),
                    "Hide the checked elements in the active view (or the highlighted row when nothing is checked)"),
                MakeIconButton("", null, (_, _) => UnhideChecked(),
                    "Unhide the checked elements — or the highlighted row when nothing is checked " +
                    "(punches through link/category/element hides)")));

            actions.Children.Add(MakeIconButton("", "Unhide All", (_, _) => UnhideAll(),
                "Restore everything hidden in the active view (links, categories, elements)"));
            actions.Children.Add(MakeIconButton("", "Export CSV", (_, _) => ExportExploreCsv(),
                "Export the visible rows to CSV"));
            actions.Children.Add(MakeIconButton("", "Safe Delete…", (_, _) => SafeDeleteChecked(),
                "Delete the CHECKED elements only (never the highlighted row) after a dependency preview and confirmation",
                destructive: true));

            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);

            return layout;
        }

        /// <summary>Popup legend for the hidden-eye tags and origin markers.</summary>
        private UIElement BuildLegendButton()
        {
            var button = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = "Legend",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "What the hidden-eye icons and tags mean"
            };

            var amber = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xB4, 0x5C, 0x0F));
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Hidden indicators",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            void AddEntry(string tag, string description, bool iconFont = false, bool italic = true)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var tagBlock = new TextBlock
                {
                    Text = tag,
                    Width = 86,
                    Foreground = amber,
                    FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                    VerticalAlignment = VerticalAlignment.Top
                };
                if (iconFont)
                {
                    tagBlock.FontFamily = IconFontFamily;
                    tagBlock.FontStyle = FontStyles.Normal;
                }

                DockPanel.SetDock(tagBlock, Dock.Left);
                row.Children.Add(tagBlock);
                row.Children.Add(new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 330
                });
                panel.Children.Add(row);
            }

            AddEntry(((char)0xED1A).ToString(), "Hidden in the active view; the tag beside it says how.",
                iconFont: true);
            AddEntry("elem", "Element hidden individually (right-click ▸ Hide in View ▸ Elements).");
            AddEntry("VG", "Its category is turned off in Visibility/Graphics.");
            AddEntry("VG+elem", "Both at once — unhide must lift both.");
            AddEntry("filter", "Hidden by a view filter set to not visible (hover names the filter).");
            AddEntry("link off", "The linked model's placement is hidden in this view.");
            AddEntry("link off+VG", "Link placement hidden AND the category is off.");
            AddEntry("hidden", "Linked element hidden inside the link (individual hide, filter, or phase — " +
                "Revit doesn't say which). Unhide it via Revit's Reveal Hidden Elements.");
            AddEntry("mixed", "Group: everything under it is hidden, by different mechanisms.");
            AddEntry("n hidden", "Group: SOME items underneath are hidden — expand to find them.");
            panel.Children.Add(new TextBlock
            {
                Text = "🔗 model name = the element lives in that linked model (origin, not visibility). " +
                       "Explorer's Unhide reverses every mechanism above in one click. " +
                       "Hidden indicators always describe the ACTIVE Revit view, so they appear only in " +
                       "Active View scope (and in the Hidden-only view mode).",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = SystemColors.GrayTextBrush
            });

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = button,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = new Border
                {
                    Background = SystemColors.WindowBrush,
                    BorderBrush = SystemColors.ActiveBorderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = panel
                }
            };

            button.Checked += (_, _) => popup.IsOpen = true;
            button.Unchecked += (_, _) => popup.IsOpen = false;
            popup.Closed += (_, _) => button.IsChecked = false;

            var host = new Grid();
            host.Children.Add(button);
            host.Children.Add(popup);
            return host;
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
            panel.Children.Add(new TextBlock
            {
                Text = "Tree filter (what the Explorer shows):",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 0, 0, 2)
            });
            panel.Children.Add(presets);
            panel.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = "Active view visibility (what Revit shows):",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 0, 0, 2)
            });
            var viewButtons = new StackPanel { Orientation = Orientation.Horizontal };
            viewButtons.Children.Add(MakeButton("Hide unchecked links", (_, _) => HideUncheckedLinksInView()));
            viewButtons.Children.Add(MakeButton("Show all links", (_, _) => ShowAllLinksInView()));
            panel.Children.Add(viewButtons);

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

        /// <summary>First link-instance id per linked origin, from the current index.</summary>
        private Dictionary<string, long> OriginLinkInstanceIds() =>
            _exploreRecords
                .Where(r => r.LinkInstanceIdValue.HasValue)
                .GroupBy(r => r.Origin, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().LinkInstanceIdValue!.Value, StringComparer.OrdinalIgnoreCase);

        /// <summary>Hides (in the active Revit view) every link whose checkbox is off in the Models filter.</summary>
        private void HideUncheckedLinksInView()
        {
            var linkIdsByOrigin = OriginLinkInstanceIds();
            var uncheckedLinkIds = _originOptions
                .Where(o => !o.IsChecked && linkIdsByOrigin.ContainsKey(o.Name))
                .Select(o => linkIdsByOrigin[o.Name])
                .ToList();

            if (uncheckedLinkIds.Count == 0)
            {
                SetStatus("No unchecked links to hide — uncheck link model(s) in the list above first.");
                return;
            }

            RunOnRevit("Hiding links…", (_, uidoc) =>
            {
                var (changed, error) = RevitActions.SetLinkVisibility(uidoc, uncheckedLinkIds, hide: true);
                RecaptureViewState(uidoc);
                OnUi(() => SetStatus(error ??
                    $"Hid {changed} link placement(s) in the active view. 'Show all links' restores them."));
            });
        }

        private void ShowAllLinksInView()
        {
            var allLinkIds = OriginLinkInstanceIds().Values.ToList();
            if (allLinkIds.Count == 0)
            {
                SetStatus("No linked models in the current index.");
                return;
            }

            RunOnRevit("Showing links…", (_, uidoc) =>
            {
                var (changed, error) = RevitActions.SetLinkVisibility(uidoc, allLinkIds, hide: false);
                RecaptureViewState(uidoc);
                OnUi(() => SetStatus(error ?? $"Restored {changed} link placement(s) in the active view."));
            });
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

            // TEMP diagnostics: trace every viewport move to pin down the post-hide scroll
            // jump. Remove once the cause is confirmed.
            tree.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) =>
            {
                if (Math.Abs(args.VerticalChange) > 0.01)
                {
                    LogManager.Info("explorer-scroll",
                        $"scroll {args.VerticalOffset - args.VerticalChange:0.##} -> {args.VerticalOffset:0.##} " +
                        $"(extent {args.ExtentHeight:0.##}, viewport {args.ViewportHeight:0.##}, " +
                        $"suppress={_suppressAutoScroll})");
                }
            }));

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

            // Passive hidden indicator: an eye-off glyph appears ONLY next to rows whose
            // elements are hidden in the active view; visible rows get no icon.
            // Amber normally; a light contrasting tone on the selected row, where amber
            // on the blue highlight is unreadable (same idea as black text turning white).
            var indicatorStyle = new Style(typeof(TextBlock));
            indicatorStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB4, 0x5C, 0x0F))));
            var indicatorSelected = new DataTrigger
            {
                Binding = new Binding(nameof(ExplorerTreeItem.IsSelected)),
                Value = true
            };
            indicatorSelected.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD9, 0x99))));
            indicatorStyle.Triggers.Add(indicatorSelected);

            var hiddenFactory = new FrameworkElementFactory(typeof(TextBlock));
            hiddenFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(ExplorerTreeItem.HiddenGlyph)));
            hiddenFactory.SetValue(TextBlock.FontFamilyProperty, IconFontFamily);
            hiddenFactory.SetValue(TextBlock.FontSizeProperty, 13.0);
            hiddenFactory.SetValue(FrameworkElement.StyleProperty, indicatorStyle);
            hiddenFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            hiddenFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            // Tooltip states WHY it is hidden (VG category vs element hide vs link).
            hiddenFactory.SetBinding(FrameworkElement.ToolTipProperty,
                new Binding(nameof(ExplorerTreeItem.HiddenToolTip)));
            panelFactory.AppendChild(hiddenFactory);

            // Compact mechanism tag right after the eye — "VG", "elem", "VG+elem", "link off",
            // "mixed" — so the distinction is visible at a glance, not only on hover.
            var hiddenTagFactory = new FrameworkElementFactory(typeof(TextBlock));
            hiddenTagFactory.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(ExplorerTreeItem.HiddenTagText)));
            hiddenTagFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            hiddenTagFactory.SetValue(TextBlock.FontStyleProperty, FontStyles.Italic);
            hiddenTagFactory.SetValue(FrameworkElement.StyleProperty, indicatorStyle);
            hiddenTagFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
            hiddenTagFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            hiddenTagFactory.SetBinding(FrameworkElement.ToolTipProperty,
                new Binding(nameof(ExplorerTreeItem.HiddenToolTip)));
            panelFactory.AppendChild(hiddenTagFactory);

            // Partial-hidden count on group rows ("3 hidden") — a collapsed parent still
            // reveals that hidden items live somewhere underneath it.
            var hiddenSummaryFactory = new FrameworkElementFactory(typeof(TextBlock));
            hiddenSummaryFactory.SetBinding(TextBlock.TextProperty,
                new Binding(nameof(ExplorerTreeItem.HiddenSummaryText)));
            hiddenSummaryFactory.SetValue(TextBlock.FontSizeProperty, 10.0);
            hiddenSummaryFactory.SetValue(TextBlock.FontStyleProperty, FontStyles.Italic);
            hiddenSummaryFactory.SetValue(FrameworkElement.StyleProperty, indicatorStyle);
            hiddenSummaryFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            hiddenSummaryFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            hiddenSummaryFactory.SetBinding(FrameworkElement.ToolTipProperty,
                new Binding(nameof(ExplorerTreeItem.HiddenToolTip)));
            panelFactory.AppendChild(hiddenSummaryFactory);

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
                    // Only the item itself, not bubbled child selections — and never during
                    // a rebuild's programmatic re-selection, where BringIntoView on a freshly
                    // virtualized tree overshoots and throws the viewport around.
                    if (!_suppressAutoScroll && ReferenceEquals(s, args.OriginalSource))
                    {
                        ((TreeViewItem)s).BringIntoView();
                    }
                })));
            tree.ItemContainerStyle = itemStyle;

            // Double-click an instance row zooms straight to it — host or linked.
            // Space toggles the highlighted row's checkbox — same path as a mouse click
            // (the IsChecked setter raises UserCheckChanged, updating the checked-key set).
            tree.PreviewKeyDown += (_, args) =>
            {
                if (args.Key != System.Windows.Input.Key.Space ||
                    args.OriginalSource is TextBox ||
                    tree.SelectedItem is not ExplorerTreeItem item)
                {
                    return;
                }

                item.IsChecked = item.IsChecked != true;
                args.Handled = true;
            };

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

            AddMenuItem("Select checked in Revit", SelectChecked);
            AddMenuItem("Show / zoom checked", ShowChecked);
            AddMenuItem("Focus 3D on checked", FocusChecked);
            AddMenuItem("Isolate checked in active view", IsolateChecked);
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
            var (hostIds, linked) = Partition(GetActionRecords(out var fallbackNote));
            if (hostIds.Count == 0)
            {
                SetStatus(linked.Count > 0
                    ? "Only linked elements are targeted — Revit can only isolate host elements (hide/show the whole link instead)."
                    : NothingToActOn);
                return;
            }

            RunOnRevit("Isolating elements…", (_, uidoc) =>
            {
                var (isolated, error) = RevitActions.IsolateElements(uidoc, hostIds);
                RecaptureViewState(uidoc);
                OnUi(() => SetStatus(error ?? WithFallbackNote(fallbackNote,
                    $"Temporarily isolated {isolated:N0} element(s) in the active view. Use Reset Isolate to restore.")));
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

            // When the pick came from inside a link, reveal the linked element itself,
            // never the link-instance row that carried it.
            var first = (linked.Count > 0 ? matches.FirstOrDefault(r => r.IsLinked) : null) ?? matches[0];
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
            var records = GetActionRecords(out _);
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

        /// <summary>
        /// Forma-style focus: section-box the active 3D view to the checked/highlighted
        /// elements (host and linked) and hide links that aren't involved.
        /// </summary>
        private void FocusChecked()
        {
            var (hostIds, linked) = Partition(GetActionRecords(out var fallbackNote));
            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus(NothingToActOn);
                return;
            }

            MarkSelectionPush();
            RunOnRevit("Focusing view…", (_, uidoc) =>
            {
                var error = RevitActions.FocusOnSelection(uidoc, hostIds, linked);
                if (error == null)
                {
                    // Also select, so the focused elements are highlighted inside the box.
                    RevitActions.SelectMixed(uidoc, hostIds, linked);
                }

                RecaptureViewState(uidoc);
                OnUi(() => SetStatus(error ?? WithFallbackNote(fallbackNote,
                    $"Focused the 3D view on {hostIds.Count + linked.Count:N0} element(s)" +
                    (linked.Count > 0 ? " (other links hidden)" : string.Empty) +
                    ". Reset Focus restores the view.")));
            });
        }

        private void ResetFocus()
        {
            RunOnRevit("Restoring view…", (_, uidoc) =>
            {
                var error = RevitActions.ResetFocus(uidoc);
                RecaptureViewState(uidoc);
                OnUi(() => SetStatus(error ?? "View restored (section box and link visibility)."));
            });
        }

        /// <summary>
        /// Hides the targeted elements per-element, host and linked alike. Host ids go
        /// through View.HideElements (bisecting); linked elements go through Revit's own
        /// Hide command via pre-selected link references — the only per-element path the
        /// API offers for links.
        /// </summary>
        private void HideChecked()
        {
            var records = GetActionRecords(out var fallbackNote);
            if (records.Count == 0)
            {
                SetStatus(NothingToActOn);
                return;
            }

            var (hostIds, linkedTargets) = Partition(records);
            LogManager.Info("explorer-scroll",
                $"hide clicked: {hostIds.Count} host, {linkedTargets.Count} linked");

            RunOnRevit("Hiding elements…", (app, uidoc) =>
            {
                var messages = new List<string>();

                if (hostIds.Count > 0)
                {
                    var (hidden, skipped, error) = RevitActions.HideInView(uidoc, hostIds);
                    messages.Add(error ?? $"Hid {hidden:N0} host element(s)" +
                        (skipped > 0 ? $" ({skipped} cannot be hidden in this view)" : string.Empty));
                }

                var postedLinked = 0;
                if (linkedTargets.Count > 0)
                {
                    // Per-element parity with host hides: Revit applies the posted hide
                    // right after this action (its API has no direct call for linked ids).
                    MarkSelectionPush();
                    var (posted, error) = RevitActions.HideLinkedElements(app, uidoc, linkedTargets);
                    postedLinked = posted;
                    messages.Add(error ?? $"hiding {posted:N0} linked element(s)");
                }

                RecaptureViewState(uidoc);
                OnUi(() =>
                {
                    SetStatus(WithFallbackNote(fallbackNote,
                        string.Join("; ", messages) + ". Unhide restores them."));
                    if (postedLinked > 0)
                    {
                        // The posted command lands after this action, so the first recapture
                        // ran too early for the linked rows — recapture again shortly.
                        // In-place indicator update only; the tree is NEVER rebuilt by the
                        // Explorer's own hide (that is what kept host hides scroll-stable).
                        ScheduleRecapture(TimeSpan.FromMilliseconds(2000));
                    }
                });
            });
        }

        /// <summary>One-shot delayed recapture: refreshes eye indicators in place, no rebuild.</summary>
        private void ScheduleRecapture(TimeSpan delay)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RunOnRevit("Refreshing indicators…", (_, uidoc) => RecaptureViewState(uidoc), showBusy: false);
            };
            timer.Start();
        }

        /// <summary>
        /// Shows the checked rows' elements, punching through every blocker above them:
        /// hidden link instances, hidden categories, element hides. Showing one category of
        /// a HIDDEN link compensates — the link is revealed and its other categories hide
        /// view-wide — so the net visible result is just the requested category.
        /// </summary>
        private void UnhideChecked()
        {
            var records = GetActionRecords(out var fallbackNote);
            if (records.Count == 0)
            {
                SetStatus(NothingToActOn);
                return;
            }

            // Filters that hide any targeted record must be lifted too (view-wide by
            // nature — stated honestly), or Unhide would visibly do nothing for them.
            var hiddenFilters = _viewSnapshot?.HiddenFilterNamesFor(records)
                                ?? (IReadOnlyList<string>)Array.Empty<string>();

            // Individually hidden LINKED elements have no unhide API — count them so the
            // user learns the limitation instead of watching the button silently fail.
            var lockedLinked = _viewSnapshot == null
                ? 0
                : records.Count(r => r.IsLinked && _viewSnapshot.HiddenTag(r) == "hidden");

            var hostIds = records.Where(r => !r.IsLinked).Select(r => r.IdValue).Distinct().ToList();
            var linkedRecords = records.Where(r => r.IsLinked && r.LinkInstanceIdValue.HasValue).ToList();
            var linkIds = linkedRecords.Select(r => r.LinkInstanceIdValue!.Value).Distinct().ToList();
            var categories = records
                .Select(r => r.Category)
                .Where(c => c != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
            var singleCategory = categories.Count == 1 && records.All(r => r.Category != null);

            // Compensation data: which OTHER categories each involved link contains,
            // computed from the index so a hidden link revealed for one category can keep
            // the rest hidden view-wide.
            Dictionary<long, List<string>>? otherCategoriesByLink = null;
            if (linkIds.Count > 0 && singleCategory)
            {
                var wanted = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
                otherCategoriesByLink = linkIds.ToDictionary(
                    id => id,
                    id => _exploreRecords
                        .Where(r => r.LinkInstanceIdValue == id &&
                                    r.Category != null &&
                                    !wanted.Contains(r.Category!))
                        .Select(r => r.Category!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());
            }

            RunOnRevit("Unhiding selection…", (_, uidoc) =>
            {
                var messages = new List<string>();

                if (linkIds.Count > 0)
                {
                    // Compensation applies only to links that were hidden BEFORE this click.
                    var hiddenBefore = RevitActions.GetHiddenLinkIds(uidoc, linkIds);

                    var (count, error) = RevitActions.SetLinkVisibility(uidoc, linkIds, hide: false);
                    if (error == null && count > 0)
                    {
                        messages.Add($"showed {count} link placement(s)");
                    }

                    if (otherCategoriesByLink != null && hiddenBefore.Count > 0)
                    {
                        var toHide = hiddenBefore
                            .SelectMany(id => otherCategoriesByLink.TryGetValue(id, out var list)
                                ? list
                                : new List<string>())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (toHide.Count > 0)
                        {
                            var (hiddenCats, _) = RevitActions.SetCategoriesHidden(uidoc, toHide, hide: true);
                            if (hiddenCats > 0)
                            {
                                messages.Add($"kept hidden view-wide: {string.Join(", ", toHide)} " +
                                    "(affects host too — Revit cannot scope categories per link)");
                            }
                        }
                    }
                }

                if (categories.Count > 0)
                {
                    // Explicit single-category unhide lifts even manual VG hides; broad
                    // selections restore only Explorer-tracked ones (protects user VG setup).
                    var (count, _) = RevitActions.SetCategoriesHidden(
                        uidoc, categories, hide: false, trackedOnly: !singleCategory);
                    if (count > 0)
                    {
                        messages.Add($"restored {count} categor{(count == 1 ? "y" : "ies")}");
                    }
                }

                if (hostIds.Count > 0)
                {
                    var (shown, error) = RevitActions.UnhideInView(uidoc, hostIds);
                    if (error == null && shown > 0)
                    {
                        messages.Add($"showed {shown:N0} element(s)");
                    }
                }

                if (hiddenFilters.Count > 0)
                {
                    var (count, error) = RevitActions.SetFiltersVisible(uidoc, hiddenFilters);
                    if (error == null && count > 0)
                    {
                        messages.Add($"restored view filter(s) {string.Join(", ", hiddenFilters.Select(f => $"'{f}'"))} " +
                            "(view-wide — affects everything they match)");
                    }
                }

                RecaptureViewState(uidoc);
                var lockedHint = lockedLinked > 0
                    ? $"  ⚠ {lockedLinked} linked element(s) are individually hidden — Revit's API cannot " +
                      "unhide those; use Reveal Hidden Elements in Revit."
                    : string.Empty;
                OnUi(() => SetStatus((messages.Count == 0
                    ? lockedLinked > 0
                        ? "Nothing restorable here."
                        : "The targeted elements are not hidden in this view."
                    : WithFallbackNote(fallbackNote, $"Unhide: {string.Join("; ", messages)}.")) + lockedHint));
            });
        }

        /// <summary>
        /// Truth-based full restore: hides are saved with the model, so in-memory trackers
        /// can never be the source of record. Sweeps what is ACTUALLY hidden — links,
        /// categories the model uses, elements — and restores it.
        /// </summary>
        private void UnhideAll()
        {
            var records = _exploreRecords;

            RunOnRevit("Unhiding everything…", (_, uidoc) =>
            {
                var snapshot = ViewStateService.Capture(uidoc, records);
                var messages = new List<string>();

                if (snapshot.HiddenLinkInstanceIds.Count > 0)
                {
                    var (count, error) = RevitActions.SetLinkVisibility(
                        uidoc, snapshot.HiddenLinkInstanceIds.ToList(), hide: false);
                    if (error == null && count > 0)
                    {
                        messages.Add($"{count} link placement(s)");
                    }
                }

                var indexedCategories = new HashSet<string>(
                    records.Select(r => r.Category).Where(c => c != null)!,
                    StringComparer.OrdinalIgnoreCase);
                var hiddenCategories = snapshot.HiddenCategoryNames
                    .Where(indexedCategories.Contains)
                    .ToList();
                if (hiddenCategories.Count > 0)
                {
                    var (count, _) = RevitActions.SetCategoriesHidden(
                        uidoc, hiddenCategories, hide: false, trackedOnly: false);
                    if (count > 0)
                    {
                        messages.Add($"{count} categor{(count == 1 ? "y" : "ies")}");
                    }
                }

                if (snapshot.HiddenHostIds.Count > 0)
                {
                    var (shown, error) = RevitActions.UnhideInView(uidoc, snapshot.HiddenHostIds.ToList());
                    if (error == null && shown > 0)
                    {
                        messages.Add($"{shown:N0} element(s)");
                    }
                }

                if (snapshot.HiddenFilterNames.Count > 0)
                {
                    var (count, error) = RevitActions.SetFiltersVisible(uidoc, snapshot.HiddenFilterNames);
                    if (error == null && count > 0)
                    {
                        messages.Add($"{count} view filter(s) ({string.Join(", ", snapshot.HiddenFilterNames)})");
                    }
                }

                RevitActions.ClearVisibilityTracking(uidoc);
                RecaptureViewState(uidoc);

                var isolateHint = snapshot.TemporaryIsolateActive
                    ? "  ⚠ Temporary isolate is still active — use Reset Isolate."
                    : string.Empty;
                var lockedLinked = records.Count(r => r.IsLinked && snapshot.HiddenTag(r) == "hidden");
                var lockedHint = lockedLinked > 0
                    ? $"  ⚠ {lockedLinked} linked element(s) are individually hidden — Revit's API cannot " +
                      "unhide those; use Reveal Hidden Elements in Revit."
                    : string.Empty;
                OnUi(() => SetStatus((messages.Count == 0
                    ? "Nothing permanently hidden in this view."
                    : $"Unhid {string.Join(", ", messages)}.") + isolateHint + lockedHint));
            });
        }

        private void ResetIsolate()
        {
            RunOnRevit("Resetting isolate…", (_, uidoc) =>
            {
                var error = RevitActions.ResetIsolate(uidoc);
                RecaptureViewState(uidoc);
                OnUi(() => SetStatus(error ?? "Temporary isolate mode reset."));
            });
        }

        /// <summary>
        /// Hidden-eye indicators show only when their active-view reference point is
        /// unambiguous: Active View scope, or the explicit "Hidden only" view mode.
        /// </summary>
        private bool HiddenIndicatorsEnabled =>
            CurrentScope == ExplorerScope.ActiveView || _viewModeCombo.SelectedIndex == 2;

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

        private string? _lastIndexDocKey;
        private bool _lastIndexUncategorized;

        /// <summary>
        /// Silent mode (auto-sync) re-indexes without the busy overlay or progress updates,
        /// so it never steals focus or flashes UI while the user works in Revit.
        /// </summary>
        private void RefreshExplore(bool silent = false, string? autoSyncNote = null)
        {
            var scope = CurrentScope;
            var includeLinks = _includeLinksCheck.IsChecked == true;
            var includeUncategorized = _includeUncategorizedCheck.IsChecked == true;

            // Previous records enable warm refresh (host delta patch + link cache), valid
            // only for the same document with the same uncategorized option.
            var previous = includeUncategorized == _lastIndexUncategorized ? _exploreRecords : null;
            var lastDocKey = _lastIndexDocKey;

            RunOnRevit("Indexing model…", (_, uidoc) =>
            {
                var docKey = string.IsNullOrWhiteSpace(uidoc.Document.PathName)
                    ? uidoc.Document.Title
                    : uidoc.Document.PathName;

                var warm = ElementCollectionService.CollectWarm(
                    uidoc, scope, includeLinks, includeUncategorized,
                    previousRecords: string.Equals(docKey, lastDocKey, StringComparison.OrdinalIgnoreCase)
                        ? previous
                        : null,
                    progress: silent
                        ? null
                        : new Action<int>(count => ReportProgress($"Indexing model… {count:N0} elements")),
                    isCancelled: silent ? null : new Func<bool>(() => _cancelRequested));
                var records = warm.Records;
                var snapshot = ViewStateService.Capture(uidoc, records);
                var title = uidoc.Document.Title;
                var (loadedLinks, unloadedLinks) = ElementCollectionService.CountLinkStatus(uidoc.Document);

                OnUi(() =>
                {
                    _exploreRecords = records;
                    _exploreModelTitle = title;
                    _lastIndexDocKey = docKey;
                    _lastIndexUncategorized = includeUncategorized;
                    _viewSnapshot = snapshot;
                    // Drop checked keys for elements that no longer exist in the index.
                    var liveKeys = new HashSet<string>(records.Select(KeyOf), StringComparer.Ordinal);
                    _checkedKeys.RemoveWhere(key => !liveKeys.Contains(key));
                    UpdateCheckedCount();
                    RebuildOriginOptions();
                    RebuildOrRefreshIndicators();

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
                    // Active View scope keeps the full index warm and narrows in the tree,
                    // so report both numbers.
                    var headline = scope == ExplorerScope.ActiveView
                        ? $"{records.Count(r => snapshot.Classify(r) != null):N0} in active view ({records.Count:N0} indexed)"
                        : $"Indexed {records.Count:N0} element(s) ({ScopeLabel(scope)})";
                    var prefix = autoSyncNote == null ? string.Empty : $"Auto-synced ({autoSyncNote}) — ";
                    SetStatus($"{prefix}{headline}: {perModel} [{warm.Note}]{linkHint}");
                });
            }, showBusy: !silent);
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
            // Origin/search/view-mode filtering happens here on the UI thread (it reads UI
            // state and the checked-key set); only the pure grouping runs on the pool.
            var records = ComputeTreeRecords();
            var grouping = CurrentGrouping;
            var expandedPaths = CollectExpandedGroupPaths();
            var selectedKey = SelectedRowKey();

            IReadOnlyList<ExplorerTreeNode> nodes;
            try
            {
                nodes = await System.Threading.Tasks.Task.Run(() => TreeBuilder.Build(records, grouping, null));
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

            // Pin the viewport: repopulating a virtualized tree makes WPF move the scroll
            // position on its own, so the offset is captured here and re-asserted once
            // layout settles.
            var scrollViewer = GetTreeScrollViewer();
            var scrollOffset = scrollViewer?.VerticalOffset;
            LogManager.Info("explorer-scroll",
                $"rebuild: {nodes.Count} roots, offset {scrollOffset?.ToString("0.##") ?? "n/a"}, " +
                $"selectedKey={selectedKey ?? "none"}, expanded={expandedPaths.Count}");

            _lastTreeKeys = new HashSet<string>(records.Select(KeyOf), StringComparer.Ordinal);
            _exploreItems.Clear();
            foreach (var node in nodes)
            {
                _exploreItems.Add(new ExplorerTreeItem(node, null));
            }

            // Rows mirror the authoritative stores from the moment they exist.
            foreach (var root in _exploreItems)
            {
                root.ApplyIndicators();
            }

            // Restores must not move the viewport: containers realize (and raise Selected)
            // asynchronously during layout, so the scroll offset is re-asserted and the
            // suppression flag lifted only once the UI goes idle.
            _suppressAutoScroll = true;
            RestoreExpandedGroupPaths(expandedPaths);
            RestoreSelectedRow(selectedKey);
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (scrollOffset is { } offset && GetTreeScrollViewer() is { } viewer)
                    {
                        LogManager.Info("explorer-scroll",
                            $"rebuild idle: re-asserting offset {offset:0.##} (was {viewer.VerticalOffset:0.##})");
                        viewer.ScrollToVerticalOffset(offset);
                    }

                    _suppressAutoScroll = false;
                }),
                DispatcherPriority.ContextIdle);

            if (_viewModeCombo.SelectedIndex == 2 && _viewSnapshot == null)
            {
                SetStatus("Hidden view needs an index first — press Refresh (F5).");
            }
        }

        /// <summary>Rebuilding replaces every row, so expansion is remembered by group label path.</summary>
        private const int MaxRestoredExpansions = 500;

        private HashSet<string> CollectExpandedGroupPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);

            void Visit(ExplorerTreeItem item, string parentPath)
            {
                if (item.IsInstance || !item.IsExpanded)
                {
                    return;
                }

                var path = parentPath + "\u0001" + item.Label;
                paths.Add(path);
                foreach (var child in item.Children)
                {
                    Visit(child, path);
                }
            }

            foreach (var root in _exploreItems)
            {
                Visit(root, string.Empty);
            }

            return paths;
        }

        private void RestoreExpandedGroupPaths(HashSet<string> paths)
        {
            if (paths.Count == 0 || paths.Count > MaxRestoredExpansions)
            {
                return;
            }

            void Visit(ExplorerTreeItem item, string parentPath)
            {
                if (item.IsInstance)
                {
                    return;
                }

                var path = parentPath + "\u0001" + item.Label;
                if (!paths.Contains(path))
                {
                    return;
                }

                // Expanding materializes children, which are born with correct indicators.
                item.IsExpanded = true;
                foreach (var child in item.Children)
                {
                    Visit(child, path);
                }
            }

            foreach (var root in _exploreItems)
            {
                Visit(root, string.Empty);
            }
        }

        private string? SelectedRowKey()
        {
            if (_exploreTree?.SelectedItem is not ExplorerTreeItem item)
            {
                return null;
            }

            if (item.Record != null)
            {
                return "r:" + KeyOf(item.Record);
            }

            var path = string.Empty;
            for (var current = item; current != null; current = current.Parent)
            {
                path = "\u0001" + current.Label + path;
            }

            return "g:" + path;
        }

        /// <summary>Re-selects the previously highlighted row without scrolling the viewport.</summary>
        private void RestoreSelectedRow(string? selectedKey)
        {
            if (selectedKey == null)
            {
                return;
            }

            bool Visit(ExplorerTreeItem item, string parentPath)
            {
                var path = parentPath + "\u0001" + item.Label;
                var key = item.Record != null ? "r:" + KeyOf(item.Record) : "g:" + path;
                if (key == selectedKey)
                {
                    item.IsSelected = true;
                    return true;
                }

                // Only rows the restore pass has already materialized can match.
                if (!item.IsInstance && item.IsExpanded)
                {
                    foreach (var child in item.Children)
                    {
                        if (Visit(child, path))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            foreach (var root in _exploreItems)
            {
                if (Visit(root, string.Empty))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Checked records come from the persistent key set — the same set no matter how the
        /// tree is currently grouped, searched, or view-filtered.
        /// </summary>
        private IReadOnlyList<ElementRecord> GetCheckedRecords() =>
            _checkedKeys.Count == 0
                ? Array.Empty<ElementRecord>()
                : _exploreRecords.Where(r => _checkedKeys.Contains(KeyOf(r))).ToList();

        /// <summary>The record set the tree currently shows: scope + origin filter + search + view mode.</summary>
        private IReadOnlyList<ElementRecord> ComputeTreeRecords()
        {
            IEnumerable<ElementRecord> records = OriginFilteredRecords();

            // Active View scope: the index holds the full federation (kept warm); the view
            // narrows it here via the visibility snapshot. Hidden-in-view records classify
            // false and STAY listed (with the hidden-eye indicator) — only records with no
            // presence in the view at all (out of range/crop, view-specific elsewhere,
            // non-graphical) classify null and drop out.
            if (CurrentScope == ExplorerScope.ActiveView && _viewSnapshot is { } scopeSnapshot)
            {
                records = records.Where(r => scopeSnapshot.Classify(r) != null);
            }

            var search = _searchBox.Text;
            if (!string.IsNullOrWhiteSpace(search))
            {
                records = TreeBuilder.Filter(records, search);
            }

            switch (_viewModeCombo.SelectedIndex)
            {
                case 1: // Checked only — see the whole checked set together.
                    records = records.Where(r => _checkedKeys.Contains(KeyOf(r)));
                    break;
                case 2: // Hidden only — see everything hidden in the active view together.
                    var snapshot = _viewSnapshot;
                    records = snapshot == null
                        ? Enumerable.Empty<ElementRecord>()
                        : records.Where(r => snapshot.Classify(r) == false);
                    break;
            }

            return records.ToList();
        }

        private const string NothingToActOn =
            "Nothing checked or highlighted — tick checkbox(es) or click a row first.";

        /// <summary>
        /// The records an action button targets: the checked set always wins; when NOTHING
        /// is checked, the highlighted tree row is used instead and <paramref name="fallbackNote"/>
        /// says so — an announced fallback, never a silent guess (the old silent fallback
        /// "selected Levels while it was unchecked" because a Level happened to be highlighted).
        /// Safe Delete deliberately does NOT use this — it stays checked-only.
        /// </summary>
        private IReadOnlyList<ElementRecord> GetActionRecords(out string? fallbackNote)
        {
            fallbackNote = null;
            var checkedRecords = GetCheckedRecords();
            if (checkedRecords.Count > 0)
            {
                return checkedRecords;
            }

            if (_exploreTree?.SelectedItem is ExplorerTreeItem highlighted)
            {
                var records = new List<ElementRecord>();
                highlighted.CollectAllRecords(records);
                if (records.Count > 0)
                {
                    fallbackNote = $"Nothing checked — acting on highlighted '{highlighted.Label}' " +
                                   $"({records.Count:N0} element{(records.Count == 1 ? string.Empty : "s")}).";
                    return records;
                }
            }

            return Array.Empty<ElementRecord>();
        }

        private static string WithFallbackNote(string? note, string message) =>
            note == null ? message : note + " " + message;

        /// <summary>Splits records into host ids and linked targets.</summary>
        private static (List<long> HostIds, List<RevitActions.LinkedTarget> Linked) Partition(
            IReadOnlyList<ElementRecord> records)
        {
            var hostIds = records.Where(r => !r.IsLinked).Select(r => r.IdValue).Distinct().ToList();
            var linked = records
                .Where(r => r.IsLinked && r.LinkInstanceIdValue.HasValue)
                .Select(r => new RevitActions.LinkedTarget(r.LinkInstanceIdValue!.Value, r.IdValue))
                .Distinct()
                .ToList();
            return (hostIds, linked);
        }

        private void SelectChecked()
        {
            var (hostIds, linked) = Partition(GetActionRecords(out var fallbackNote));
            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus(NothingToActOn);
                return;
            }

            MarkSelectionPush();
            RunOnRevit("Selecting elements…", (_, uidoc) =>
            {
                var (selected, failed, error) = RevitActions.SelectMixed(uidoc, hostIds, linked);
                OnUi(() => SetStatus(error ?? WithFallbackNote(fallbackNote, BuildActionStatus(
                    $"Selected {selected:N0} element(s)", failed, linked.Count))));
            });
        }

        private void ShowChecked()
        {
            var (hostIds, linked) = Partition(GetActionRecords(out var fallbackNote));
            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus(NothingToActOn);
                return;
            }

            MarkSelectionPush();
            RunOnRevit("Showing elements…", (_, uidoc) =>
            {
                var (shown, error) = RevitActions.ShowMixed(uidoc, hostIds, linked);
                OnUi(() => SetStatus(error ?? WithFallbackNote(fallbackNote, BuildActionStatus(
                    $"Showing {shown:N0} element(s)", 0, linked.Count))));
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
            // Export exactly what the tree shows: scope, Models filter, search, and view mode.
            var visible = ComputeTreeRecords();
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
            // Deliberately NO highlighted-row fallback: deleting is destructive, and a group
            // row (e.g. "Levels") merely being highlighted must never become a delete target.
            var (hostIds, linked) = Partition(GetCheckedRecords());
            if (hostIds.Count == 0)
            {
                SetStatus(linked.Count > 0
                    ? "Only linked elements are checked — elements inside links can only be deleted in the link's own file."
                    : "Safe Delete acts on checked rows only — tick checkbox(es) in the tree first.");
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
            if (!value)
            {
                _checkedKeys.Clear();
            }
            else
            {
                // Check what the tree currently shows (origin + search + view-mode filters).
                foreach (var record in ComputeTreeRecords())
                {
                    _checkedKeys.Add(KeyOf(record));
                }
            }

            foreach (var root in _exploreItems)
            {
                root.ApplyIndicators();
            }

            UpdateCheckedCount();
            SetStatus(value
                ? $"Checked everything currently shown in the tree ({_checkedKeys.Count:N0} total checked)."
                : "Cleared all checks.");
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
            // View state distinguishes the hide mechanism — VG category off vs element
            // hidden vs hidden link — because each needs a different unhide.
            AddRow("View state", _viewSnapshot == null
                ? null
                : _viewSnapshot.Classify(record) switch
                {
                    true => "Visible in the active view",
                    false => _viewSnapshot.DescribeHidden(record),
                    null => "Not applicable in the active view (nothing to draw here)"
                });
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
