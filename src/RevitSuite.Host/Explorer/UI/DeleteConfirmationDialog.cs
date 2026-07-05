using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitSuite.Host.Explorer.UI
{
    /// <summary>
    /// Confirmation gate for bulk delete. Shows the preflight facts and requires an
    /// explicit acknowledgement before the Delete button enables.
    /// </summary>
    internal sealed class DeleteConfirmationDialog : Window
    {
        private bool _confirmed;

        public static bool Confirm(Window owner, DeletePreflight preflight)
        {
            var dialog = new DeleteConfirmationDialog(preflight) { Owner = owner };
            dialog.ShowDialog();
            return dialog._confirmed;
        }

        private DeleteConfirmationDialog(DeletePreflight preflight)
        {
            Title = "Confirm Delete";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var stack = new StackPanel { Margin = new Thickness(16) };

            stack.Children.Add(new TextBlock
            {
                Text = $"You are about to delete {preflight.ElementCount:N0} element(s).",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var categories = string.Join(", ",
                preflight.CategoryCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Key} ({kv.Value})"));

            void AddLine(string text, bool warn = false)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1),
                    Foreground = warn
                        ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
                        : SystemColors.ControlTextBrush
                });
            }

            AddLine($"Categories: {categories}");
            if (preflight.ViewSpecificCount > 0)
            {
                AddLine($"View-specific elements: {preflight.ViewSpecificCount:N0}");
            }

            if (preflight.PinnedCount > 0)
            {
                AddLine($"Pinned elements: {preflight.PinnedCount:N0}", warn: true);
            }

            if (preflight.IsWorkshared && preflight.OwnedByOthersCount > 0)
            {
                AddLine($"Owned by other users: {preflight.OwnedByOthersCount:N0} — delete may fail for these.", warn: true);
            }

            AddLine("Revit may also delete dependent elements (tags, dimensions, hosted items).", warn: true);

            var acknowledge = new CheckBox
            {
                Content = "I understand this cannot be undone from this window.",
                Margin = new Thickness(0, 12, 0, 12)
            };
            stack.Children.Add(acknowledge);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var deleteButton = new Button
            {
                Content = $"Delete {preflight.ElementCount:N0} element(s)",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = false,
                Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)),
                Foreground = Brushes.White
            };
            deleteButton.Click += (_, _) =>
            {
                _confirmed = true;
                Close();
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(12, 5, 12, 5),
                IsCancel = true
            };
            cancelButton.Click += (_, _) => Close();

            acknowledge.Checked += (_, _) => deleteButton.IsEnabled = true;
            acknowledge.Unchecked += (_, _) => deleteButton.IsEnabled = false;

            buttons.Children.Add(deleteButton);
            buttons.Children.Add(cancelButton);
            stack.Children.Add(buttons);

            Content = stack;
        }
    }
}
