using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Explorer.UI
{
    /// <summary>
    /// Modeless Model Explorer window. All Revit API access is marshaled through
    /// <see cref="RevitActionBridge"/>; view models only ever hold immutable DTOs.
    /// </summary>
    public sealed partial class ExplorerWindow : Window
    {
        private static ExplorerWindow? _instance;

        private readonly Grid _busyOverlay;
        private readonly TextBlock _statusText;
        private readonly TextBlock _busyText;

        /// <summary>Single-instance entry point. Must be called from a valid Revit API context.</summary>
        public static void ShowWindow(UIApplication app)
        {
            RevitActionBridge.Instance.EnsureEventCreated();

            if (_instance != null)
            {
                if (_instance.WindowState == WindowState.Minimized)
                {
                    _instance.WindowState = WindowState.Normal;
                }

                _instance.Activate();
                return;
            }

            _instance = new ExplorerWindow();
            new WindowInteropHelper(_instance) { Owner = app.MainWindowHandle };
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }

        private ExplorerWindow()
        {
            Title = "Model Explorer";
            Width = 1180;
            Height = 760;
            MinWidth = 900;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tabs = new TabControl { Margin = new Thickness(8) };
            tabs.Items.Add(new TabItem { Header = "Explore", Content = BuildExploreTab() });
            tabs.Items.Add(new TabItem { Header = "Query + Filters", Content = BuildQueryTab() });
            tabs.Items.Add(new TabItem { Header = "Warnings", Content = BuildWarningsTab() });
            tabs.Items.Add(new TabItem { Header = "Audit", Content = BuildAuditTab() });
            tabs.Items.Add(new TabItem { Header = "Navigate", Content = BuildNavigateTab() });
            Grid.SetRow(tabs, 0);
            root.Children.Add(tabs);

            _statusText = new TextBlock
            {
                Margin = new Thickness(12, 4, 12, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(0x52, 0x60, 0x6D)),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_statusText, 1);
            root.Children.Add(_statusText);

            _busyText = new TextBlock
            {
                Text = "Working…",
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _busyOverlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(0x88, 0x10, 0x18, 0x27)),
                Visibility = Visibility.Collapsed
            };
            _busyOverlay.Children.Add(_busyText);
            Grid.SetRowSpan(_busyOverlay, 2);
            root.Children.Add(_busyOverlay);

            Content = root;
            SetStatus("Ready. Use Refresh on the Explore tab to index the model.");
        }

        /// <summary>
        /// Runs work on the Revit API thread with the busy overlay up. The callback receives
        /// a live UIApplication and must marshal any UI updates back via <see cref="OnUi"/>.
        /// A null ActiveUIDocument is reported instead of crashing (document may have closed).
        /// </summary>
        private void RunOnRevit(string busyMessage, Action<UIApplication, UIDocument> work)
        {
            SetBusy(busyMessage);
            RevitActionBridge.Instance.Post(app =>
            {
                try
                {
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc == null)
                    {
                        OnUi(() => SetStatus("No active Revit document. Open a model and try again."));
                        return;
                    }

                    work(app, uidoc);
                }
                catch (Exception ex)
                {
                    LogManager.Error("explorer", $"Explorer action failed: {busyMessage}", ex);
                    OnUi(() => SetStatus($"Error: {ex.Message}"));
                }
                finally
                {
                    OnUi(ClearBusy);
                }
            });
        }

        private void OnUi(Action action) => Dispatcher.BeginInvoke(action);

        private void SetBusy(string message)
        {
            _busyText.Text = message;
            _busyOverlay.Visibility = Visibility.Visible;
        }

        private void ClearBusy() => _busyOverlay.Visibility = Visibility.Collapsed;

        private void SetStatus(string message) => _statusText.Text = message;

        /// <summary>
        /// Runs a file operation (export/import) so an IOException — e.g. the target file is
        /// open in Excel — lands in the status bar instead of escaping to Revit's dispatcher.
        /// </summary>
        private void TryFileOperation(string description, Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                LogManager.Error("explorer", $"{description} failed.", ex);
                SetStatus($"{description} failed: {ex.Message}");
            }
        }

        // ---------- shared small helpers ----------

        private static Button MakeButton(string caption, RoutedEventHandler onClick, bool destructive = false)
        {
            var button = new Button
            {
                Content = caption,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 5, 12, 5)
            };

            if (destructive)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                button.Foreground = Brushes.White;
            }

            button.Click += onClick;
            return button;
        }

        private static TextBlock MakeCaption(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        private static string? PromptSavePath(string title, string defaultName, string filter)
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                FileName = defaultName,
                Filter = filter,
                AddExtension = true
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static string? PromptOpenPath(string title, string filter)
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
