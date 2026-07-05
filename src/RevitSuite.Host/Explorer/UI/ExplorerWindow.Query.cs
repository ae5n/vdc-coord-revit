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
        /// <summary>Editable condition row bound in the query builder.</summary>
        public sealed class ConditionRow
        {
            public string? ParameterText { get; set; }
            public string OperatorText { get; set; } = nameof(QueryOperator.Equals);
            public string? Value { get; set; }
            public string? Value2 { get; set; }
        }

        private readonly ObservableCollection<ConditionRow> _conditionRows = new ObservableCollection<ConditionRow>();
        private readonly ObservableCollection<string> _categoryOptions = new ObservableCollection<string>();
        private readonly ObservableCollection<string> _parameterOptions = new ObservableCollection<string>();
        private readonly ObservableCollection<FilterStore.LoadedFilter> _savedFilters =
            new ObservableCollection<FilterStore.LoadedFilter>();
        private readonly ObservableCollection<ElementRecord> _queryResults = new ObservableCollection<ElementRecord>();

        private Dictionary<string, ParameterValueDto> _discoveredParameters =
            new Dictionary<string, ParameterValueDto>(StringComparer.OrdinalIgnoreCase);

        private ListBox _categoryList = null!;
        private ComboBox _queryScopeCombo = null!;
        private ComboBox _logicCombo = null!;
        private CheckBox _includeTypesCheck = null!;
        private CheckBox _queryIncludeLinksCheck = null!;
        private TextBox _queryNameBox = null!;
        private TextBlock _explainText = null!;
        private ListBox _savedFilterList = null!;
        private DataGrid _queryResultsGrid = null!;
        private DataGrid _conditionsGrid = null!;

        private UIElement BuildQueryTab()
        {
            var layout = new Grid { Margin = new Thickness(8) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });

            layout.Children.Add(WrapColumn(BuildQueryBuilderPanel(), 0));

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(splitter, 1);
            layout.Children.Add(splitter);

            layout.Children.Add(WrapColumn(BuildQueryResultsPanel(), 2));
            return layout;
        }

        private static UIElement WrapColumn(UIElement content, int column)
        {
            Grid.SetColumn((FrameworkElement)content, column);
            return content;
        }

        private UIElement BuildQueryBuilderPanel()
        {
            var stack = new StackPanel();

            var headerRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            headerRow.Children.Add(MakeCaption("Filter name"));
            _queryNameBox = new TextBox { Width = 200, Margin = new Thickness(0, 0, 12, 0) };
            headerRow.Children.Add(_queryNameBox);

            headerRow.Children.Add(MakeCaption("Scope"));
            _queryScopeCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 12, 0) };
            _queryScopeCombo.Items.Add("Entire Project");
            _queryScopeCombo.Items.Add("Active View");
            _queryScopeCombo.Items.Add("Current Selection");
            _queryScopeCombo.SelectedIndex = 0;
            headerRow.Children.Add(_queryScopeCombo);

            headerRow.Children.Add(MakeCaption("Match"));
            _logicCombo = new ComboBox { Width = 70, Margin = new Thickness(0, 0, 12, 0) };
            _logicCombo.Items.Add("All");
            _logicCombo.Items.Add("Any");
            _logicCombo.SelectedIndex = 0;
            headerRow.Children.Add(_logicCombo);

            _includeTypesCheck = new CheckBox
            {
                Content = "Include element types",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            headerRow.Children.Add(_includeTypesCheck);

            _queryIncludeLinksCheck = new CheckBox
            {
                Content = "Include linked models",
                VerticalAlignment = VerticalAlignment.Center
            };
            headerRow.Children.Add(_queryIncludeLinksCheck);
            stack.Children.Add(headerRow);

            // Categories
            var categoryRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var categoryHeader = new StackPanel { Orientation = Orientation.Horizontal };
            categoryHeader.Children.Add(MakeCaption("Categories (none = all)"));
            categoryHeader.Children.Add(MakeButton("Load from model", (_, _) => LoadCategories()));
            categoryHeader.Children.Add(MakeButton("Discover parameters", (_, _) => DiscoverParameters()));
            DockPanel.SetDock(categoryHeader, Dock.Top);
            categoryRow.Children.Add(categoryHeader);

            _categoryList = new ListBox
            {
                SelectionMode = SelectionMode.Extended,
                Height = 120,
                ItemsSource = _categoryOptions,
                Margin = new Thickness(0, 4, 0, 0)
            };
            categoryRow.Children.Add(_categoryList);
            stack.Children.Add(categoryRow);

            // Conditions
            var conditionsHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            conditionsHeader.Children.Add(MakeCaption("Conditions"));
            conditionsHeader.Children.Add(MakeButton("Add condition", (_, _) => _conditionRows.Add(new ConditionRow())));
            conditionsHeader.Children.Add(MakeButton("Clear", (_, _) => _conditionRows.Clear()));
            stack.Children.Add(conditionsHeader);

            var conditionsGrid = _conditionsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                MinHeight = 110,
                MaxHeight = 220,
                ItemsSource = _conditionRows
            };

            var parameterColumn = new DataGridComboBoxColumn
            {
                Header = "Parameter",
                Width = new DataGridLength(2, DataGridLengthUnitType.Star),
                TextBinding = new Binding("ParameterText") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            };
            parameterColumn.ElementStyle = new Style(typeof(ComboBox))
            {
                Setters =
                {
                    new Setter(ItemsControl.ItemsSourceProperty, _parameterOptions),
                    new Setter(ComboBox.IsEditableProperty, true)
                }
            };
            parameterColumn.EditingElementStyle = parameterColumn.ElementStyle;
            conditionsGrid.Columns.Add(parameterColumn);

            var operatorColumn = new DataGridComboBoxColumn
            {
                Header = "Operator",
                Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
                TextBinding = new Binding("OperatorText") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            };
            var operatorStyle = new Style(typeof(ComboBox))
            {
                Setters =
                {
                    new Setter(ItemsControl.ItemsSourceProperty, Enum.GetNames(typeof(QueryOperator))),
                    new Setter(ComboBox.IsEditableProperty, false)
                }
            };
            operatorColumn.ElementStyle = operatorStyle;
            operatorColumn.EditingElementStyle = operatorStyle;
            conditionsGrid.Columns.Add(operatorColumn);

            conditionsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
                Binding = new Binding("Value") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            });
            conditionsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value 2",
                Width = new DataGridLength(0.8, DataGridLengthUnitType.Star),
                Binding = new Binding("Value2") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            });
            stack.Children.Add(conditionsGrid);

            stack.Children.Add(new TextBlock
            {
                Text = "Numeric values accept units: 3' | 36in | 900mm | 0.9m. A bare number compares in Revit internal units (feet).",
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            });

            _explainText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 2, 0, 6)
            };
            stack.Children.Add(_explainText);

            var runRow = new WrapPanel();
            runRow.Children.Add(MakeButton("Run Query", (_, _) => RunQuery()));
            runRow.Children.Add(MakeButton("Save as Filter", (_, _) => SaveFilter()));
            stack.Children.Add(runRow);

            // Saved filters
            stack.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 6) });
            var filtersHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            filtersHeader.Children.Add(MakeCaption("Saved filters"));
            filtersHeader.Children.Add(MakeButton("Reload", (_, _) => ReloadSavedFilters()));
            filtersHeader.Children.Add(MakeButton("Load into builder", (_, _) => LoadSelectedFilter()));
            filtersHeader.Children.Add(MakeButton("Import…", (_, _) => ImportFilter()));
            filtersHeader.Children.Add(MakeButton("Export…", (_, _) => ExportFilter()));
            filtersHeader.Children.Add(MakeButton("Delete", (_, _) => DeleteFilter(), destructive: true));
            stack.Children.Add(filtersHeader);

            _savedFilterList = new ListBox { Height = 110, ItemsSource = _savedFilters };
            _savedFilterList.ItemTemplate = BuildSavedFilterTemplate();
            stack.Children.Add(_savedFilterList);
            ReloadSavedFilters();

            var scroll = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            return scroll;
        }

        private static DataTemplate BuildSavedFilterTemplate()
        {
            var template = new DataTemplate(typeof(FilterStore.LoadedFilter));
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding
            {
                Converter = SavedFilterLabelConverter.Instance
            });
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

        private UIElement BuildQueryResultsPanel()
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

        private void LoadCategories()
        {
            RunOnRevit("Reading categories…", (_, uidoc) =>
            {
                var categories = QueryRunner.ListModelCategories(uidoc.Document);
                OnUi(() =>
                {
                    var previouslySelected = _categoryList.SelectedItems.Cast<string>().ToList();
                    _categoryOptions.Clear();
                    foreach (var category in categories)
                    {
                        _categoryOptions.Add(category);
                    }

                    foreach (var item in previouslySelected.Where(_categoryOptions.Contains))
                    {
                        _categoryList.SelectedItems.Add(item);
                    }

                    SetStatus($"Loaded {categories.Count} categories.");
                });
            });
        }

        private void DiscoverParameters()
        {
            var categories = _categoryList.SelectedItems.Cast<string>().ToList();
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

                    SetStatus($"Discovered {parameters.Count} parameter(s) from sampled elements" +
                              (categories.Count > 0 ? $" in {string.Join(", ", categories)}." : "."));
                });
            });
        }

        private QueryDefinition? BuildQueryFromUi(out string? error)
        {
            error = null;
            // Commit any in-progress cell edit so a value typed just before clicking Run is not lost.
            _conditionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var name = string.IsNullOrWhiteSpace(_queryNameBox.Text) ? "Ad-hoc query" : _queryNameBox.Text.Trim();
            var conditions = new List<QueryCondition>();

            foreach (var row in _conditionRows)
            {
                if (string.IsNullOrWhiteSpace(row.ParameterText))
                {
                    continue;
                }

                if (!Enum.TryParse<QueryOperator>(row.OperatorText, out var op))
                {
                    error = $"Unknown operator '{row.OperatorText}'.";
                    return null;
                }

                var parameterText = row.ParameterText!.Trim();
                var key = _discoveredParameters.TryGetValue(parameterText, out var dto)
                    ? dto.StableKey
                    : "name:" + parameterText;

                conditions.Add(new QueryCondition(key, parameterText, op, row.Value, row.Value2));
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
                Categories: _categoryList.SelectedItems.Cast<string>().ToList(),
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

            _explainText.Text = FilterStore.Explain(query);

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

                    SetStatus($"Query matched {results.Count:N0} element(s).");
                });
            });
        }

        private void SaveFilter()
        {
            var query = BuildQueryFromUi(out var error);
            if (query == null)
            {
                SetStatus($"Cannot save filter: {error}");
                return;
            }

            if (string.IsNullOrWhiteSpace(_queryNameBox.Text))
            {
                SetStatus("Give the filter a name before saving.");
                return;
            }

            try
            {
                var path = FilterStore.Save(query);
                ReloadSavedFilters();
                SetStatus($"Filter saved to {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"Could not save filter: {ex.Message}");
            }
        }

        private void ReloadSavedFilters()
        {
            _savedFilters.Clear();
            foreach (var filter in FilterStore.LoadAll())
            {
                _savedFilters.Add(filter);
            }
        }

        private void LoadSelectedFilter()
        {
            if (_savedFilterList.SelectedItem is not FilterStore.LoadedFilter { Query: { } query })
            {
                SetStatus("Select a valid saved filter first.");
                return;
            }

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

            _categoryList.SelectedItems.Clear();
            foreach (var category in query.Categories)
            {
                if (!_categoryOptions.Contains(category))
                {
                    _categoryOptions.Add(category);
                }

                _categoryList.SelectedItems.Add(category);
            }

            _conditionRows.Clear();
            foreach (var condition in query.Conditions)
            {
                _conditionRows.Add(new ConditionRow
                {
                    ParameterText = condition.ParameterDisplayName ?? condition.ParameterKey,
                    OperatorText = condition.Operator.ToString(),
                    Value = condition.Value,
                    Value2 = condition.Value2
                });
            }

            _explainText.Text = FilterStore.Explain(query);
            SetStatus($"Loaded filter '{query.Name}' into the builder.");
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
