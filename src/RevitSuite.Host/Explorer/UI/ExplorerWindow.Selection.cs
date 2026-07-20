using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        /// <summary>Keys of the records currently selected in Revit — drives the tree markers.</summary>
        private readonly HashSet<string> _revitSelectionKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Elements beyond this cap are dropped from the parameters window (with a note).</summary>
        private const int MaxMatrixElements = 50;

        /// <summary>
        /// True while the multi-select table owns the details area. The tree's deferred row
        /// realization re-raises SelectedItemChanged AFTER the table renders (fast for small
        /// trees — which is why 2-element selections lost the table), so single-element
        /// details are suppressed until the reveal fully settles.
        /// </summary>
        private bool _suppressDetailsForSelectionTable;

        private void SetRevitSelectionMarkers(IEnumerable<ElementRecord> records)
        {
            _revitSelectionKeys.Clear();
            foreach (var record in records)
            {
                _revitSelectionKeys.Add(KeyOf(record));
            }

            foreach (var root in _exploreItems)
            {
                root.ApplyIndicators();
            }
        }

        /// <summary>Revit selection emptied — drop the markers (and the table, if showing).</summary>
        private void ClearRevitSelectionMarkers()
        {
            if (_revitSelectionKeys.Count == 0)
            {
                return;
            }

            _revitSelectionKeys.Clear();
            foreach (var root in _exploreItems)
            {
                root.ApplyIndicators();
            }
        }

        /// <summary>True while the details table is driven by the tree's checked set (not Revit's selection).</summary>
        private bool _checksTableActive;

        /// <summary>
        /// Checked rows feed the same multi-select table as a Revit selection: it appears at
        /// 2+ checked records, tracks every check change while active, and releases the
        /// details area when the last check is cleared.
        /// </summary>
        private void UpdateCheckedSelectionTable()
        {
            var records = GetCheckedRecords();
            if (records.Count >= 2 || (_checksTableActive && records.Count == 1))
            {
                _checksTableActive = true;
                _suppressDetailsForSelectionTable = true;
                ShowSelectionTable(records, "Checked");
                _ = Dispatcher.BeginInvoke(
                    new Action(() => _suppressDetailsForSelectionTable = false),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            else if (_checksTableActive && records.Count == 0)
            {
                _checksTableActive = false;
                _detailsPanel.Children.Clear();
                _detailsRecord = null;
            }
        }

        /// <summary>
        /// Multi-select table in the details area: every element of the Revit selection
        /// (or the checked set), with copy tools and a parameter matrix for comparison.
        /// </summary>
        private void ShowSelectionTable(IReadOnlyList<ElementRecord> records, string title = "Revit selection")
        {
            _checksTableActive = title == "Checked";
            _detailsPanel.Children.Clear();
            _detailsRecord = null;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var matrixButton = MakeButton("Parameters…", (_, _) => ShowParameterMatrix(records));
            matrixButton.Margin = new Thickness(8, 0, 0, 0);
            matrixButton.ToolTip = "Load every parameter of every selected element into one comparison table";
            DockPanel.SetDock(matrixButton, Dock.Right);
            header.Children.Add(matrixButton);

            var copyAllButton = MakeButton("Copy All", (_, _) => CopySelectionRows(records, "all"));
            copyAllButton.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(copyAllButton, Dock.Right);
            header.Children.Add(copyAllButton);

            header.Children.Add(new TextBlock
            {
                Text = $"{title} ({records.Count:N0})",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            _detailsPanel.Children.Add(header);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                SelectionMode = DataGridSelectionMode.Extended,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
                MaxHeight = 480,
                ItemsSource = records
            };

            void AddColumn(string headerText, string path, double star)
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = headerText,
                    Binding = new Binding(path),
                    Width = new DataGridLength(star, DataGridLengthUnitType.Star)
                });
            }

            AddColumn("Id", nameof(ElementRecord.IdValue), 0.6);
            AddColumn("Category", nameof(ElementRecord.Category), 1);
            AddColumn("Family", nameof(ElementRecord.Family), 1);
            AddColumn("Type", nameof(ElementRecord.TypeName), 1);
            AddColumn("Name", nameof(ElementRecord.InstanceName), 1);
            AddColumn("Level", nameof(ElementRecord.LevelName), 0.7);
            AddColumn("Model", nameof(ElementRecord.Origin), 0.8);

            IReadOnlyList<ElementRecord> SelectedRows() =>
                grid.SelectedItems.Count > 0
                    ? grid.SelectedItems.Cast<ElementRecord>().ToList()
                    : Array.Empty<ElementRecord>();

            var menu = new ContextMenu();
            void AddMenu(string headerText, Action action)
            {
                var item = new MenuItem { Header = headerText };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }

            AddMenu("Copy row(s)", () => CopySelectionRows(
                SelectedRows().Count > 0 ? SelectedRows() : records, "selected"));
            AddMenu("Copy Element Id(s)", () =>
            {
                var rows = SelectedRows().Count > 0 ? SelectedRows() : records;
                System.Windows.Clipboard.SetText(string.Join(",", rows.Select(r => r.IdValue).Distinct()));
                SetStatus($"Copied {rows.Count:N0} element id(s).");
            });
            AddMenu("Copy all", () => CopySelectionRows(records, "all"));
            AddMenu("Parameters for row(s)…", () => ShowParameterMatrix(
                SelectedRows().Count > 0 ? SelectedRows() : records));
            grid.ContextMenu = menu;

            // Double-click a row → focus its single-element details (tree reveal included).
            grid.MouseDoubleClick += (_, _) =>
            {
                if (grid.SelectedItem is ElementRecord record)
                {
                    FindAndReveal(record);
                }
            };

            _detailsPanel.Children.Add(grid);
            _detailsPanel.Children.Add(new TextBlock
            {
                Text = "Right-click for copy options · double-click a row to reveal it in the tree · " +
                       "Parameters… opens a per-element comparison table",
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        private void CopySelectionRows(IReadOnlyList<ElementRecord> rows, string what)
        {
            if (rows.Count == 0)
            {
                SetStatus("Nothing to copy.");
                return;
            }

            var lines = new List<string>
            {
                "ElementId\tCategory\tFamily\tType\tName\tLevel\tModel"
            };
            lines.AddRange(rows.Select(r =>
                $"{r.IdValue}\t{r.Category}\t{r.Family}\t{r.TypeName}\t{r.InstanceName}\t{r.LevelName}\t{r.Origin}"));
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            SetStatus($"Copied {rows.Count:N0} row(s) ({what}, tab-separated with header).");
        }

        /// <summary>Resolves a record back to its live element — host directly, linked via its link instance.</summary>
        private static Autodesk.Revit.DB.Element? ResolveRecordElement(
            Autodesk.Revit.UI.UIDocument uidoc, ElementRecord record)
        {
            if (record.IsLinked)
            {
                if (!record.LinkInstanceIdValue.HasValue)
                {
                    return null;
                }

                var link = uidoc.Document.GetElement(
                        new Autodesk.Revit.DB.ElementId(record.LinkInstanceIdValue.Value))
                    as Autodesk.Revit.DB.RevitLinkInstance;
                return link?.GetLinkDocument()?.GetElement(new Autodesk.Revit.DB.ElementId(record.IdValue));
            }

            return uidoc.Document.GetElement(new Autodesk.Revit.DB.ElementId(record.IdValue));
        }

        /// <summary>Loads all parameters of the given elements (host and linked) into a matrix window.</summary>
        private void ShowParameterMatrix(IReadOnlyList<ElementRecord> records)
        {
            if (records.Count == 0)
            {
                SetStatus("Nothing selected.");
                return;
            }

            var targets = records.Take(MaxMatrixElements).ToList();
            var totalRequested = records.Count;

            RunOnRevit("Loading parameters…", (_, uidoc) =>
            {
                var sets = new List<(ElementRecord Record, IReadOnlyList<ParameterValueDto> Parameters)>();
                foreach (var record in targets)
                {
                    var element = ResolveRecordElement(uidoc, record);
                    if (element != null)
                    {
                        sets.Add((record, ParameterExtractor.ExtractAll(element)));
                    }
                }

                OnUi(() =>
                {
                    if (sets.Count == 0)
                    {
                        SetStatus("None of the selected elements could be resolved (links unloaded?).");
                        return;
                    }

                    ShowParameterMatrixWindow(sets, totalRequested);
                });
            }, showBusy: false);
        }

        /// <summary>
        /// One tab per element, each a plain Parameter/Value table — no cross-element grid
        /// to decipher. Cross-element copying survives as explicit commands: "Copy All
        /// Elements" (comparison TSV for Excel) and per-parameter "across all elements".
        /// </summary>
        private void ShowParameterMatrixWindow(
            IReadOnlyList<(ElementRecord Record, IReadOnlyList<ParameterValueDto> Parameters)> sets,
            int totalRequested)
        {
            var elementIds = sets.Select(s => s.Record.IdValue.ToString()).ToList();

            string LookupValue(IReadOnlyList<ParameterValueDto> parameters, string name) =>
                parameters.FirstOrDefault(p =>
                    string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase))?.DisplayValue
                ?? string.Empty;

            void CopyTsv(IEnumerable<string> lines, string what)
            {
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
                SetStatus($"Copied {what} (tab-separated).");
            }

            // Comparison TSV across every element — for Excel, not for on-screen reading.
            void CopyAllElements()
            {
                var names = sets
                    .SelectMany(s => s.Parameters.Select(p => p.DisplayName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var lines = new List<string> { "Parameter\t" + string.Join("\t", elementIds) };
                lines.AddRange(names.Select(name =>
                    name + "\t" + string.Join("\t", sets.Select(s => LookupValue(s.Parameters, name)))));
                CopyTsv(lines, $"all {sets.Count} element(s) ({names.Count:N0} parameter(s), one column each)");
            }

            // Per KEY: the named parameter(s), one column per element.
            void CopyAcrossElements(IReadOnlyList<string> parameterNames)
            {
                if (parameterNames.Count == 0)
                {
                    SetStatus("Select parameter row(s) first.");
                    return;
                }

                var lines = new List<string> { "Parameter\t" + string.Join("\t", elementIds) };
                lines.AddRange(parameterNames.Select(name =>
                    name + "\t" + string.Join("\t", sets.Select(s => LookupValue(s.Parameters, name)))));
                CopyTsv(lines, $"{parameterNames.Count:N0} parameter(s) across {sets.Count} element(s)");
            }

            var tabs = new TabControl();
            foreach (var (record, parameters) in sets)
            {
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false,
                    IsReadOnly = true,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    SelectionMode = DataGridSelectionMode.Extended,
                    ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
                    ItemsSource = parameters
                };
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = "Parameter",
                    Binding = new Binding(nameof(ParameterValueDto.DisplayName)),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = "Value",
                    Binding = new Binding(nameof(ParameterValueDto.DisplayValue)),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                });

                IReadOnlyList<ParameterValueDto> SelectedParameters() =>
                    grid.SelectedItems.Count > 0
                        ? grid.SelectedItems.Cast<ParameterValueDto>().ToList()
                        : Array.Empty<ParameterValueDto>();

                var elementParameters = parameters;
                var elementRecord = record;

                void CopyPairs(IReadOnlyList<ParameterValueDto> items, string what)
                {
                    if (items.Count == 0)
                    {
                        SetStatus("Select parameter row(s) first.");
                        return;
                    }

                    CopyTsv(items.Select(p => $"{p.DisplayName}\t{p.DisplayValue}"),
                        $"{what} of element {elementRecord.IdValue} ({items.Count:N0} parameter(s))");
                }

                var menu = new ContextMenu();
                void AddMenu(string headerText, Action action)
                {
                    var item = new MenuItem { Header = headerText };
                    item.Click += (_, _) => action();
                    menu.Items.Add(item);
                }

                AddMenu("Copy value(s)", () =>
                {
                    var items = SelectedParameters();
                    if (items.Count == 0)
                    {
                        SetStatus("Select parameter row(s) first.");
                        return;
                    }

                    System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
                        items.Select(p => p.DisplayValue ?? string.Empty)));
                    SetStatus($"Copied {items.Count:N0} value(s).");
                });
                AddMenu("Copy parameter + value", () => CopyPairs(SelectedParameters(), "selection"));
                AddMenu("Copy all (this element)", () => CopyPairs(elementParameters, "all parameters"));
                AddMenu("Copy parameter(s) across all elements", () =>
                    CopyAcrossElements(SelectedParameters().Select(p => p.DisplayName).Distinct().ToList()));
                grid.ContextMenu = menu;

                var tabContent = new DockPanel();
                var tabHeader = new DockPanel { Margin = new Thickness(4, 4, 4, 4) };
                var copyElementButton = MakeButton("Copy Element", (_, _) => CopyPairs(elementParameters, "all parameters"));
                copyElementButton.ToolTip = "Copy every parameter + value of this element (tab-separated)";
                copyElementButton.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(copyElementButton, Dock.Right);
                tabHeader.Children.Add(copyElementButton);
                tabHeader.Children.Add(new TextBlock
                {
                    Text = $"{record.DisplayName} · {record.Category} · {record.Origin}" +
                           (record.IsLinked ? " (linked)" : string.Empty),
                    Foreground = SystemColors.GrayTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                DockPanel.SetDock(tabHeader, Dock.Top);
                tabContent.Children.Add(tabHeader);
                tabContent.Children.Add(grid);

                tabs.Items.Add(new TabItem
                {
                    Header = record.IdValue.ToString(),
                    ToolTip = $"{record.DisplayName}\n{record.Category} · {record.Origin}",
                    Content = tabContent
                });
            }

            var toolbar = new DockPanel { Margin = new Thickness(8, 8, 8, 4) };
            var copyAllButton = MakeButton("Copy All Elements", (_, _) => CopyAllElements());
            copyAllButton.ToolTip = "One comparison table for Excel: parameters as rows, one column per element";
            DockPanel.SetDock(copyAllButton, Dock.Right);
            toolbar.Children.Add(copyAllButton);
            var capNote = totalRequested > sets.Count
                ? $"  (first {sets.Count} of {totalRequested:N0} elements)"
                : string.Empty;
            toolbar.Children.Add(new TextBlock
            {
                Text = "One tab per element · right-click rows to copy values, this element, or a parameter across all elements" + capNote,
                Foreground = SystemColors.GrayTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var layout = new DockPanel();
            DockPanel.SetDock(toolbar, Dock.Top);
            layout.Children.Add(toolbar);
            layout.Children.Add(tabs);

            new Window
            {
                Title = $"Parameters — {sets.Count} element(s)",
                Width = 640,
                Height = 680,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = layout
            }.Show();

            SetStatus($"Parameters loaded for {sets.Count} element(s), one tab each." +
                      (totalRequested > sets.Count ? $" Showing the first {sets.Count} of {totalRequested:N0}." : string.Empty));
        }
    }
}
