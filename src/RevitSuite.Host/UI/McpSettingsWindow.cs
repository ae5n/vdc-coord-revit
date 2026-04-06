using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitSuite.Mcp.Configuration;
using RevitSuite.Mcp.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitSuite.Host.UI
{
    public sealed class McpSettingsWindow : Window
    {
        private readonly ObservableCollection<CommandItem> _items = new ObservableCollection<CommandItem>();
        private ListView _listView;

        public McpSettingsWindow()
        {
            Title = "MCP Tool Settings";
            Width = 720;
            Height = 520;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            BuildUi();
            LoadCommands();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(new TextBlock
            {
                Text = "MCP Tool Settings",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "Enable or disable tools exposed to Claude. Restart the MCP server (toggle off/on) for changes to take effect.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var setupSection = BuildSetupSection();
            Grid.SetRow(setupSection, 1);
            root.Children.Add(setupSection);

            // ListView
            _listView = new ListView { BorderThickness = new Thickness(1) };
            _listView.ItemsSource = _items;

            var gridView = new GridView();

            // Enabled column
            var enabledColumn = new GridViewColumn { Header = "Enabled", Width = 65 };
            var enabledTemplate = new DataTemplate();
            var checkBoxFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkBoxFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            checkBoxFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            enabledTemplate.VisualTree = checkBoxFactory;
            enabledColumn.CellTemplate = enabledTemplate;
            gridView.Columns.Add(enabledColumn);

            // Command name column
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Tool Name",
                Width = 250,
                DisplayMemberBinding = new Binding("CommandName")
            });

            // Group column
            gridView.Columns.Add(new GridViewColumn
            {
                Header = "Source",
                Width = 150,
                DisplayMemberBinding = new Binding("Group")
            });

            _listView.View = gridView;

            // Group by Source
            var view = CollectionViewSource.GetDefaultView(_items);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            view.SortDescriptions.Add(new SortDescription("Group", ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription("CommandName", ListSortDirection.Ascending));

            _listView.GroupStyle.Add(new GroupStyle
            {
                HeaderTemplate = CreateGroupHeaderTemplate()
            });

            Grid.SetRow(_listView, 2);
            root.Children.Add(_listView);

            // Button bar
            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var selectAllBtn = new Button { Content = "Select All", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            selectAllBtn.Click += (s, e) => { foreach (var item in _items) item.Enabled = true; };

            var deselectAllBtn = new Button { Content = "Deselect All", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            deselectAllBtn.Click += (s, e) => { foreach (var item in _items) item.Enabled = false; };

            var saveBtn = new Button
            {
                Content = "Save",
                Width = 80,
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                Foreground = Brushes.White
            };
            saveBtn.Click += SaveButton_Click;

            buttonBar.Children.Add(selectAllBtn);
            buttonBar.Children.Add(deselectAllBtn);
            buttonBar.Children.Add(saveBtn);

            Grid.SetRow(buttonBar, 3);
            root.Children.Add(buttonBar);

            Content = root;
        }

        private static Border BuildSetupSection()
        {
            var section = new StackPanel();

            section.Children.Add(new TextBlock
            {
                Text = "Setup",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });

            section.Children.Add(new TextBlock
            {
                Text =
                    "1. In Revit, click MCP Server to start the local socket server." + Environment.NewLine +
                    "2. In Codex or Claude Code, point the MCP config to one deployed RevitSuite mcp-server path." + Environment.NewLine +
                    "3. Restart the MCP client after saving the config." + Environment.NewLine + Environment.NewLine +
                    "RevitSuite can be installed for multiple Revit years, but MCP only needs one configured path. " +
                    "The deployed mcp-server is version-agnostic and connects to the running Revit instance hosting the socket server.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Margin = new Thickness(0, 6, 0, 10)
            });

            section.Children.Add(CreateCodeBlock(
@"Codex / Claude Code example
command: node
args:
  C:\Users\<you>\AppData\Roaming\Autodesk\Revit\Addins\2026\RevitSuite\mcp-server\build\index.js"));

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xE1, 0xE8)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = section
            };
        }

        private static Border CreateCodeBlock(string text)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x33)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private static DataTemplate CreateGroupHeaderTemplate()
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            factory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            factory.SetValue(TextBlock.FontSizeProperty, 13.0);
            factory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 4));
            factory.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x1f, 0x29, 0x33)));
            template.VisualTree = factory;
            return template;
        }

        private void LoadCommands()
        {
            try
            {
                var registryPath = PathManager.GetCommandRegistryFilePath(createIfNotExists: false);
                if (!File.Exists(registryPath))
                    return;

                var json = JObject.Parse(File.ReadAllText(registryPath));
                var commands = json["commands"]?.ToObject<List<CommandConfig>>();
                if (commands == null) return;

                foreach (var cmd in commands)
                {
                    var group = cmd.AssemblyPath?.Contains("RevitSuite.Host") == true
                        ? "RevitSuite Tools"
                        : "General MCP Tools";

                    _items.Add(new CommandItem
                    {
                        CommandName = cmd.CommandName,
                        Enabled = cmd.Enabled,
                        Group = group,
                        AssemblyPath = cmd.AssemblyPath
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading commands: {ex.Message}", "MCP Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var registryPath = PathManager.GetCommandRegistryFilePath(createIfNotExists: false);
                if (!File.Exists(registryPath))
                {
                    MessageBox.Show("Command registry file not found.", "MCP Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var root = JObject.Parse(File.ReadAllText(registryPath));
                var commands = root["commands"] as JArray;
                if (commands == null) return;

                var enabledMap = _items.ToDictionary(i => i.CommandName, i => i.Enabled, StringComparer.OrdinalIgnoreCase);

                foreach (var token in commands)
                {
                    var name = token["commandName"]?.Value<string>();
                    if (name != null && enabledMap.TryGetValue(name, out var enabled))
                        token["enabled"] = enabled;
                }

                File.WriteAllText(registryPath, root.ToString(Formatting.Indented));

                MessageBox.Show(
                    "Settings saved.\n\nToggle the MCP server off and on for changes to take effect.",
                    "MCP Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "MCP Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public sealed class CommandItem : INotifyPropertyChanged
        {
            private bool _enabled;

            public string CommandName { get; set; }
            public string Group { get; set; }
            public string AssemblyPath { get; set; }

            public bool Enabled
            {
                get => _enabled;
                set
                {
                    if (_enabled == value) return;
                    _enabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
