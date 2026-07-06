using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        /// <summary>One editable condition row in the query builder.</summary>
        public sealed class ConditionRowVm
        {
            public string? ParameterText { get; set; }
            public string OperatorText { get; set; } = nameof(QueryOperator.Equals);
            public string? Value { get; set; }
            public string? Value2 { get; set; }

            public bool NeedsValue => !TryParseOperator(out var op) ||
                op is not (QueryOperator.IsEmpty or QueryOperator.IsNotEmpty
                    or QueryOperator.HasParameter or QueryOperator.MissingParameter);

            public bool NeedsValue2 => TryParseOperator(out var op) && op == QueryOperator.Between;

            private bool TryParseOperator(out QueryOperator op) =>
                Enum.TryParse(OperatorText, out op);
        }

        /// <summary>One checkbox row in the category picker.</summary>
        public sealed class CategoryOptionVm : INotifyPropertyChanged
        {
            private bool _isChecked;
            private bool _isVisible = true;

            public CategoryOptionVm(string name)
            {
                Name = name;
            }

            public string Name { get; }

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
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                    CheckedChanged?.Invoke();
                }
            }

            public bool IsVisible
            {
                get => _isVisible;
                set
                {
                    if (_isVisible == value)
                    {
                        return;
                    }

                    _isVisible = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public event Action? CheckedChanged;
        }

        private readonly List<(ConditionRowVm Vm, FrameworkElement Row)> _conditionRows =
            new List<(ConditionRowVm, FrameworkElement)>();

        private readonly ObservableCollection<CategoryOptionVm> _categoryOptions =
            new ObservableCollection<CategoryOptionVm>();

        private readonly ObservableCollection<string> _parameterOptions = new ObservableCollection<string>();
        private readonly ObservableCollection<FilterStore.LoadedFilter> _savedFilters =
            new ObservableCollection<FilterStore.LoadedFilter>();
        private readonly ObservableCollection<ElementRecord> _queryResults = new ObservableCollection<ElementRecord>();

        private Dictionary<string, ParameterValueDto> _discoveredParameters =
            new Dictionary<string, ParameterValueDto>(StringComparer.OrdinalIgnoreCase);

        private ComboBox _queryScopeCombo = null!;
        private ComboBox _logicCombo = null!;
        private CheckBox _includeTypesCheck = null!;
        private CheckBox _queryIncludeLinksCheck = null!;
        private TextBox _queryNameBox = null!;
        private TextBox _categorySearchBox = null!;
        private TextBlock _categoryCountText = null!;
        private TextBlock _parameterStatusText = null!;
        private TextBlock _explainText = null!;
        private TextBlock _resultCountText = null!;
        private StackPanel _conditionsPanel = null!;
        private ListBox _savedFilterList = null!;
        private DataGrid _queryResultsGrid = null!;
        private DispatcherTimer? _parameterDiscoveryDebounce;
        private bool _queryTabInitialized;

        private UIElement BuildQueryTab()
        {
            var layout = new Grid { Margin = new Thickness(8) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });

            var builder = BuildQueryBuilderPanel();
            Grid.SetColumn(builder, 0);
            layout.Children.Add(builder);

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(splitter, 1);
            layout.Children.Add(splitter);

            var results = BuildQueryResultsPanel();
            Grid.SetColumn(results, 2);
            layout.Children.Add(results);
            return layout;
        }

        /// <summary>Categories auto-load the first time the tab is opened; no manual "load" step.</summary>
        private void EnsureQueryTabInitialized()
        {
            if (_queryTabInitialized)
            {
                return;
            }

            _queryTabInitialized = true;
            ReloadCategories();
        }

        private static TextBlock MakeSectionHeader(string text) => new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4)
        };

        private FrameworkElement BuildQueryBuilderPanel()
        {
            var stack = new StackPanel();

            // ---- 1 · Scope ----
            stack.Children.Add(MakeSectionHeader("1 · Where to search"));
            var scopeRow = new WrapPanel();
            scopeRow.Children.Add(MakeCaption("Scope"));
            _queryScopeCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 12, 0) };
            _queryScopeCombo.Items.Add("Entire Project");
            _queryScopeCombo.Items.Add("Active View");
            _queryScopeCombo.Items.Add("Current Selection");
            _queryScopeCombo.SelectedIndex = 0;
            scopeRow.Children.Add(_queryScopeCombo);

            scopeRow.Children.Add(MakeCaption("Match"));
            _logicCombo = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 12, 0) };
            _logicCombo.Items.Add("All conditions");
            _logicCombo.Items.Add("Any condition");
            _logicCombo.SelectedIndex = 0;
            _logicCombo.SelectionChanged += (_, _) => UpdateExplanation();
            scopeRow.Children.Add(_logicCombo);

            _includeTypesCheck = new CheckBox
            {
                Content = "Element types",
                ToolTip = "Also match element types (families/symbols), not just placed instances.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            scopeRow.Children.Add(_includeTypesCheck);

            _queryIncludeLinksCheck = new CheckBox
            {
                Content = "Linked models",
                ToolTip = "Also search inside every loaded linked model.",
                VerticalAlignment = VerticalAlignment.Center
            };
            scopeRow.Children.Add(_queryIncludeLinksCheck);
            stack.Children.Add(scopeRow);

            // ---- 2 · Categories ----
            stack.Children.Add(MakeSectionHeader("2 · Categories"));
            var categoryHeader = new DockPanel();
            _categoryCountText = new TextBlock
            {
                Text = "None checked = all categories",
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var categoryButtons = new StackPanel { Orientation = Orientation.Horizontal };
            categoryButtons.Children.Add(MakeButton("Clear", (_, _) => ClearCategoryChecks()));
            categoryButtons.Children.Add(MakeButton("↻ Reload", (_, _) => ReloadCategories()));
            DockPanel.SetDock(categoryButtons, Dock.Right);
            categoryHeader.Children.Add(categoryButtons);
            categoryHeader.Children.Add(_categoryCountText);
            stack.Children.Add(categoryHeader);

            _categorySearchBox = new TextBox
            {
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = "Type to filter the category list."
            };
            _categorySearchBox.TextChanged += (_, _) => FilterCategoryList();
            stack.Children.Add(_categorySearchBox);

            var categoryItems = new ItemsControl { ItemsSource = _categoryOptions };
            var categoryTemplate = new DataTemplate(typeof(CategoryOptionVm));
            var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(nameof(CategoryOptionVm.IsChecked)) { Mode = BindingMode.TwoWay });
            checkFactory.SetBinding(ContentControl.ContentProperty, new Binding(nameof(CategoryOptionVm.Name)));
            checkFactory.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(CategoryOptionVm.IsVisible))
            {
                Converter = new BooleanToVisibilityConverter()
            });
            checkFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));
            categoryTemplate.VisualTree = checkFactory;
            categoryItems.ItemTemplate = categoryTemplate;

            stack.Children.Add(new Border
            {
                BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                Height = 150,
                Child = new ScrollViewer
                {
                    Content = categoryItems,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            });

            _parameterStatusText = new TextBlock
            {
                Text = "Parameters discover automatically when you check categories.",
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            stack.Children.Add(_parameterStatusText);

            // ---- 3 · Conditions ----
            var conditionsHeader = new DockPanel();
            var addButton = MakeButton("+ Add condition", (_, _) => AddConditionRow(new ConditionRowVm()));
            DockPanel.SetDock(addButton, Dock.Right);
            conditionsHeader.Children.Add(addButton);
            conditionsHeader.Children.Add(MakeSectionHeader("3 · Conditions (optional)"));
            stack.Children.Add(conditionsHeader);

            _conditionsPanel = new StackPanel();
            stack.Children.Add(_conditionsPanel);

            stack.Children.Add(new TextBlock
            {
                Text = "Numbers accept units: 3'  36in  900mm  0.9m — a bare number is Revit internal feet. Press Enter to run.",
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

            _explainText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(_explainText);

            // ---- 4 · Run / Save ----
            stack.Children.Add(MakeSectionHeader("4 · Run"));
            var runRow = new WrapPanel();
            var runButton = MakeButton("▶ Run Query", (_, _) => RunQuery());
            runButton.FontWeight = FontWeights.SemiBold;
            runRow.Children.Add(runButton);
            _resultCountText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            runRow.Children.Add(_resultCountText);
            runRow.Children.Add(MakeCaption("Save as"));
            _queryNameBox = new TextBox
            {
                Width = 170,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Name for the saved filter."
            };
            runRow.Children.Add(_queryNameBox);
            runRow.Children.Add(MakeButton("Save Filter", (_, _) => SaveFilter()));
            stack.Children.Add(runRow);

            // ---- Saved filters ----
            stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 0) });
            stack.Children.Add(MakeSectionHeader("Saved filters"));
            var filtersButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            filtersButtons.Children.Add(MakeButton("Apply (load + run)", (_, _) => ApplySelectedFilter()));
            filtersButtons.Children.Add(MakeButton("Edit in builder", (_, _) => LoadSelectedFilter(runAfterLoad: false)));
            filtersButtons.Children.Add(MakeButton("Import…", (_, _) => ImportFilter()));
            filtersButtons.Children.Add(MakeButton("Export…", (_, _) => ExportFilter()));
            filtersButtons.Children.Add(MakeButton("Delete", (_, _) => DeleteFilter(), destructive: true));
            stack.Children.Add(filtersButtons);

            _savedFilterList = new ListBox
            {
                Height = 110,
                ItemsSource = _savedFilters,
                ToolTip = "Double-click a filter to apply it."
            };
            _savedFilterList.ItemTemplate = BuildSavedFilterTemplate();
            _savedFilterList.MouseDoubleClick += (_, _) => ApplySelectedFilter();
            stack.Children.Add(_savedFilterList);
            ReloadSavedFilters();

            AddConditionRow(new ConditionRowVm());
            UpdateExplanation();

            return new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        // ---------- Categories ----------

        private void ReloadCategories()
        {
            RunOnRevit("Reading categories…", (_, uidoc) =>
            {
                var categories = QueryRunner.ListModelCategories(uidoc.Document);
                OnUi(() =>
                {
                    var previouslyChecked = new HashSet<string>(
                        _categoryOptions.Where(o => o.IsChecked).Select(o => o.Name),
                        StringComparer.OrdinalIgnoreCase);

                    _categoryOptions.Clear();
                    foreach (var name in categories)
                    {
                        var option = new CategoryOptionVm(name) { IsChecked = previouslyChecked.Contains(name) };
                        option.CheckedChanged += OnCategoryChecksChanged;
                        _categoryOptions.Add(option);
                    }

                    FilterCategoryList();
                    UpdateCategoryCount();

                    // Seed the parameter dropdown right away (model-wide sample); it re-discovers
                    // with sharper results as soon as categories are checked.
                    DiscoverParametersSilently();
                });
            }, showBusy: false);
        }

        private void FilterCategoryList()
        {
            var term = (_categorySearchBox.Text ?? string.Empty).Trim();
            foreach (var option in _categoryOptions)
            {
                option.IsVisible = term.Length == 0 ||
                    option.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private void ClearCategoryChecks()
        {
            foreach (var option in _categoryOptions)
            {
                option.IsChecked = false;
            }
        }

        private IReadOnlyList<string> CheckedCategories() =>
            _categoryOptions.Where(o => o.IsChecked).Select(o => o.Name).ToList();

        private void OnCategoryChecksChanged()
        {
            UpdateCategoryCount();
            UpdateExplanation();

            // Parameters re-discover automatically, debounced so rapid checking is smooth.
            _parameterDiscoveryDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _parameterDiscoveryDebounce.Stop();
            _parameterDiscoveryDebounce.Tick -= OnParameterDiscoveryTick;
            _parameterDiscoveryDebounce.Tick += OnParameterDiscoveryTick;
            _parameterDiscoveryDebounce.Start();
        }

        private void UpdateCategoryCount()
        {
            var count = _categoryOptions.Count(o => o.IsChecked);
            _categoryCountText.Text = count == 0
                ? "None checked = all categories"
                : $"{count} categor{(count == 1 ? "y" : "ies")} checked";
        }

        private void OnParameterDiscoveryTick(object? sender, EventArgs e)
        {
            _parameterDiscoveryDebounce?.Stop();
            DiscoverParametersSilently();
        }

        private void DiscoverParametersSilently()
        {
            var categories = CheckedCategories();
            _parameterStatusText.Text = "Discovering parameters…";

            RunOnRevit("Discovering parameters…", (_, uidoc) =>
            {
                var parameters = QueryRunner.DiscoverParameters(uidoc, categories);
                OnUi(() =>
                {
                    _discoveredParameters = parameters
                        .GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    _parameterOptions.Clear();
                    foreach (var name in _discoveredParameters.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    {
                        _parameterOptions.Add(name);
                    }

                    _parameterStatusText.Text = categories.Count == 0
                        ? $"{parameters.Count} parameter(s) discovered from a model-wide sample."
                        : $"{parameters.Count} parameter(s) discovered from {string.Join(", ", categories.Take(4))}{(categories.Count > 4 ? "…" : "")} (sampled).";
                });
            }, showBusy: false);
        }

        // ---------- Conditions ----------

        private void AddConditionRow(ConditionRowVm vm)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var removeButton = new Button
            {
                Content = "✕",
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Remove this condition"
            };
            DockPanel.SetDock(removeButton, Dock.Right);

            var parameterCombo = new ComboBox
            {
                IsEditable = true,
                Width = 190,
                Margin = new Thickness(0, 0, 6, 0),
                ItemsSource = _parameterOptions,
                Text = vm.ParameterText ?? string.Empty,
                ToolTip = "Pick a discovered parameter or type any parameter name."
            };
            parameterCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, _) =>
                {
                    vm.ParameterText = parameterCombo.Text;
                    UpdateExplanation();
                }));

            var operatorCombo = new ComboBox
            {
                Width = 150,
                Margin = new Thickness(0, 0, 6, 0),
                ItemsSource = Enum.GetNames(typeof(QueryOperator)),
                SelectedItem = vm.OperatorText
            };

            var valueBox = new TextBox
            {
                Width = 120,
                Margin = new Thickness(0, 0, 6, 0),
                Text = vm.Value ?? string.Empty
            };
            var value2Box = new TextBox
            {
                Width = 80,
                Text = vm.Value2 ?? string.Empty,
                ToolTip = "Upper bound (Between only)"
            };

            void SyncValueBoxVisibility()
            {
                valueBox.Visibility = vm.NeedsValue ? Visibility.Visible : Visibility.Collapsed;
                value2Box.Visibility = vm.NeedsValue2 ? Visibility.Visible : Visibility.Collapsed;
            }

            operatorCombo.SelectionChanged += (_, _) =>
            {
                vm.OperatorText = operatorCombo.SelectedItem as string ?? vm.OperatorText;
                SyncValueBoxVisibility();
                UpdateExplanation();
            };
            valueBox.TextChanged += (_, _) =>
            {
                vm.Value = valueBox.Text;
                UpdateExplanation();
            };
            value2Box.TextChanged += (_, _) =>
            {
                vm.Value2 = value2Box.Text;
                UpdateExplanation();
            };

            void RunOnEnter(object _, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    RunQuery();
                    args.Handled = true;
                }
            }

            valueBox.KeyDown += RunOnEnter;
            value2Box.KeyDown += RunOnEnter;

            var inputs = new StackPanel { Orientation = Orientation.Horizontal };
            inputs.Children.Add(parameterCombo);
            inputs.Children.Add(operatorCombo);
            inputs.Children.Add(valueBox);
            inputs.Children.Add(value2Box);

            row.Children.Add(removeButton);
            row.Children.Add(inputs);

            removeButton.Click += (_, _) =>
            {
                _conditionRows.RemoveAll(entry => ReferenceEquals(entry.Row, row));
                _conditionsPanel.Children.Remove(row);
                UpdateExplanation();
            };

            SyncValueBoxVisibility();
            _conditionRows.Add((vm, row));
            _conditionsPanel.Children.Add(row);
        }

        private void ClearConditionRows()
        {
            _conditionRows.Clear();
            _conditionsPanel.Children.Clear();
        }

        // ---------- Build / run / explain ----------

        private QueryDefinition? BuildQueryFromUi(out string? error)
        {
            error = null;
            var name = string.IsNullOrWhiteSpace(_queryNameBox.Text) ? "Ad-hoc query" : _queryNameBox.Text.Trim();
            var conditions = new List<QueryCondition>();

            foreach (var (vm, _) in _conditionRows)
            {
                if (string.IsNullOrWhiteSpace(vm.ParameterText))
                {
                    continue;
                }

                if (!Enum.TryParse<QueryOperator>(vm.OperatorText, out var op))
                {
                    error = $"Unknown operator '{vm.OperatorText}'.";
                    return null;
                }

                var parameterText = vm.ParameterText!.Trim();
                var key = _discoveredParameters.TryGetValue(parameterText, out var dto)
                    ? dto.StableKey
                    : "name:" + parameterText;

                conditions.Add(new QueryCondition(key, parameterText, op, vm.Value, vm.Value2));
            }

            var query = new QueryDefinition(
                Id: MakeFilterId(name),
                Name: name,
                Scope: _queryScopeCombo.SelectedIndex switch
                {
                    1 => ExplorerScope.ActiveView,
                    2 => ExplorerScope.CurrentSelection,
                    _ => ExplorerScope.EntireProject
                },
                Categories: CheckedCategories(),
                Conditions: conditions,
                Operator: _logicCombo.SelectedIndex == 1 ? LogicalOperator.Or : LogicalOperator.And,
                IncludeElementTypes: _includeTypesCheck.IsChecked == true,
                IncludeLinkedDocuments: _queryIncludeLinksCheck.IsChecked == true);

            var validationError = FilterStore.Validate(query);
            if (validationError != null)
            {
                error = validationError;
                return null;
            }

            return query;
        }

        /// <summary>Live plain-language readback of the query, updated on every edit.</summary>
        private void UpdateExplanation()
        {
            if (_explainText == null)
            {
                return;
            }

            var query = BuildQueryFromUi(out var error);
            if (query == null)
            {
                _explainText.Text = $"⚠ {error}";
                _explainText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xB4, 0x5C, 0x0F));
                return;
            }

            _explainText.Text = FilterStore.Explain(query);
            _explainText.Foreground = SystemColors.GrayTextBrush;
        }

        private static string MakeFilterId(string name) =>
            new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

        private void RunQuery()
        {
            var query = BuildQueryFromUi(out var error);
            if (query == null)
            {
                SetStatus($"Cannot run query: {error}");
                return;
            }

            RunOnRevit("Running query…", (_, uidoc) =>
            {
                var results = QueryRunner.Run(uidoc, query);
                OnUi(() =>
                {
                    _queryResults.Clear();
                    foreach (var record in results)
                    {
                        _queryResults.Add(record);
                    }

                    _resultCountText.Text = $"{results.Count:N0} matched";
                    SetStatus($"Query matched {results.Count:N0} element(s). {FilterStore.Explain(query)}");
                });
            });
        }

        private void SaveFilter()
        {
            if (string.IsNullOrWhiteSpace(_queryNameBox.Text))
            {
                SetStatus("Give the filter a name (step 4) before saving.");
                _queryNameBox.Focus();
                return;
            }

            var query = BuildQueryFromUi(out var error);
            if (query == null)
            {
                SetStatus($"Cannot save filter: {error}");
                return;
            }

            TryFileOperation("Save filter", () =>
            {
                var path = FilterStore.Save(query);
                ReloadSavedFilters();
                SetStatus($"Filter saved to {path}");
            });
        }

        // ---------- Saved filters ----------

        private static DataTemplate BuildSavedFilterTemplate()
        {
            var template = new DataTemplate(typeof(FilterStore.LoadedFilter));
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding { Converter = SavedFilterLabelConverter.Instance });
            factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            template.VisualTree = factory;
            return template;
        }

        private sealed class SavedFilterLabelConverter : IValueConverter
        {
            public static readonly SavedFilterLabelConverter Instance = new SavedFilterLabelConverter();

            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                if (value is FilterStore.LoadedFilter loaded)
                {
                    var sourceTag = loaded.Source == "Company" ? "[Company] " : string.Empty;
                    return loaded.Query != null
                        ? $"{sourceTag}{loaded.Query.Name} — {FilterStore.Explain(loaded.Query)}"
                        : $"{sourceTag}INVALID: {System.IO.Path.GetFileName(loaded.FilePath)} — {loaded.Error}";
                }

                return string.Empty;
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                throw new NotSupportedException();
        }

        private void ReloadSavedFilters()
        {
            _savedFilters.Clear();
            foreach (var filter in FilterStore.LoadAll())
            {
                _savedFilters.Add(filter);
            }
        }

        private void ApplySelectedFilter() => LoadSelectedFilter(runAfterLoad: true);

        private void LoadSelectedFilter(bool runAfterLoad)
        {
            if (_savedFilterList.SelectedItem is not FilterStore.LoadedFilter { Query: { } query })
            {
                SetStatus("Select a valid saved filter first.");
                return;
            }

            EnsureQueryTabInitialized();

            _queryNameBox.Text = query.Name;
            _queryScopeCombo.SelectedIndex = query.Scope switch
            {
                ExplorerScope.ActiveView => 1,
                ExplorerScope.CurrentSelection => 2,
                _ => 0
            };
            _logicCombo.SelectedIndex = query.Operator == LogicalOperator.Or ? 1 : 0;
            _includeTypesCheck.IsChecked = query.IncludeElementTypes;
            _queryIncludeLinksCheck.IsChecked = query.IncludeLinkedDocuments;

            var wanted = new HashSet<string>(query.Categories, StringComparer.OrdinalIgnoreCase);
            foreach (var category in query.Categories.Where(c =>
                         !_categoryOptions.Any(o => string.Equals(o.Name, c, StringComparison.OrdinalIgnoreCase))))
            {
                // Category from another model — keep it selectable rather than dropping it.
                var option = new CategoryOptionVm(category);
                option.CheckedChanged += OnCategoryChecksChanged;
                _categoryOptions.Add(option);
            }

            foreach (var option in _categoryOptions)
            {
                option.IsChecked = wanted.Contains(option.Name);
            }

            ClearConditionRows();
            foreach (var condition in query.Conditions)
            {
                AddConditionRow(new ConditionRowVm
                {
                    ParameterText = condition.ParameterDisplayName ?? condition.ParameterKey,
                    OperatorText = condition.Operator.ToString(),
                    Value = condition.Value,
                    Value2 = condition.Value2
                });
            }

            if (_conditionRows.Count == 0)
            {
                AddConditionRow(new ConditionRowVm());
            }

            UpdateExplanation();

            if (runAfterLoad)
            {
                RunQuery();
            }
            else
            {
                SetStatus($"Loaded filter '{query.Name}' into the builder.");
            }
        }

        private void ImportFilter()
        {
            var path = PromptOpenPath("Import Filter", "Filter JSON (*.json)|*.json");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Import", () =>
            {
                var imported = FilterStore.Import(path);
                ReloadSavedFilters();
                SetStatus(imported.Query != null
                    ? $"Imported filter '{imported.Query.Name}'."
                    : $"Import failed: {imported.Error}");
            });
        }

        private void ExportFilter()
        {
            if (_savedFilterList.SelectedItem is not FilterStore.LoadedFilter { Query: { } query })
            {
                SetStatus("Select a valid saved filter first.");
                return;
            }

            var path = PromptSavePath("Export Filter", query.Id + ".json", "Filter JSON (*.json)|*.json");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                FilterStore.ExportTo(query, path);
                SetStatus($"Filter exported to {path}");
            });
        }

        private void DeleteFilter()
        {
            if (_savedFilterList.SelectedItem is not FilterStore.LoadedFilter loaded)
            {
                SetStatus("Select a saved filter first.");
                return;
            }

            if (loaded.Source == "Company")
            {
                SetStatus("Company standards are read-only here — remove them from the ProgramData folder instead.");
                return;
            }

            if (loaded.Query != null)
            {
                FilterStore.Delete(loaded.Query);
            }
            else if (System.IO.File.Exists(loaded.FilePath))
            {
                System.IO.File.Delete(loaded.FilePath);
            }

            ReloadSavedFilters();
            SetStatus("Filter deleted.");
        }

        // ---------- Results ----------

        private FrameworkElement BuildQueryResultsPanel()
        {
            var layout = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = MakeCaption("Results");
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            _queryResultsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                EnableRowVirtualization = true,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
                ItemsSource = _queryResults
            };
            AddElementColumns(_queryResultsGrid);
            Grid.SetRow(_queryResultsGrid, 1);
            layout.Children.Add(_queryResultsGrid);

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton("Select in Revit", (_, _) => ActOnQueryResults(selectOnly: true)));
            actions.Children.Add(MakeButton("Show / Zoom", (_, _) => ActOnQueryResults(selectOnly: false)));
            actions.Children.Add(MakeButton("Export CSV", (_, _) => ExportQueryResults()));
            Grid.SetRow(actions, 2);
            layout.Children.Add(actions);

            return layout;
        }

        private static void AddElementColumns(DataGrid grid)
        {
            void AddColumn(string header, string path, double weight)
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding(path),
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star)
                });
            }

            AddColumn("Id", nameof(ElementRecord.IdValue), 0.6);
            AddColumn("Category", nameof(ElementRecord.Category), 1);
            AddColumn("Family", nameof(ElementRecord.Family), 1);
            AddColumn("Type", nameof(ElementRecord.TypeName), 1);
            AddColumn("Name", nameof(ElementRecord.InstanceName), 1);
            AddColumn("Level", nameof(ElementRecord.LevelName), 0.8);
            AddColumn("Model", nameof(ElementRecord.Origin), 0.8);
        }

        private void ActOnQueryResults(bool selectOnly)
        {
            var records = (_queryResultsGrid.SelectedItems.Count > 0
                    ? _queryResultsGrid.SelectedItems.Cast<ElementRecord>()
                    : _queryResults)
                .ToList();

            var hostIds = records.Where(r => !r.IsLinked).Select(r => r.IdValue).Distinct().ToList();
            var linked = records
                .Where(r => r.IsLinked && r.LinkInstanceIdValue.HasValue)
                .Select(r => new RevitActions.LinkedTarget(r.LinkInstanceIdValue!.Value, r.IdValue))
                .Distinct()
                .ToList();

            if (hostIds.Count == 0 && linked.Count == 0)
            {
                SetStatus("No query results to act on.");
                return;
            }

            MarkSelectionPush();
            RunOnRevit(selectOnly ? "Selecting elements…" : "Showing elements…", (_, uidoc) =>
            {
                if (selectOnly)
                {
                    var (selected, _, error) = RevitActions.SelectMixed(uidoc, hostIds, linked);
                    OnUi(() => SetStatus(error ?? $"Selected {selected:N0} element(s)."));
                }
                else
                {
                    var (shown, error) = RevitActions.ShowMixed(uidoc, hostIds, linked);
                    OnUi(() => SetStatus(error ?? $"Showing {shown:N0} element(s)."));
                }
            });
        }

        private void ExportQueryResults()
        {
            if (_queryResults.Count == 0)
            {
                SetStatus("Run a query first.");
                return;
            }

            var path = PromptSavePath("Export Query Results", "QueryResults.csv", "CSV files (*.csv)|*.csv");
            if (path == null)
            {
                return;
            }

            TryFileOperation("Export", () =>
            {
                ExportService.WriteCsv(path, ExportService.BuildElementsTable(_queryResults));
                SetStatus($"Exported {_queryResults.Count:N0} row(s) to {path}");
            });
        }
    }
}
