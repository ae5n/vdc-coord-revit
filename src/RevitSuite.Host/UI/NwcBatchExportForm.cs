using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using WinForms = System.Windows.Forms;
using Autodesk.Revit.DB;
using DrawingColor = System.Drawing.Color;

namespace RevitSuite.Host.UI
{
    internal sealed class NwcBatchExportForm : WinForms.Form
    {
        internal sealed class ViewGroup
        {
            public ViewGroup(string name, IReadOnlyList<View> views)
            {
                Name = name;
                Views = views;
            }

            public string Name { get; }
            public IReadOnlyList<View> Views { get; }
        }

        private readonly IReadOnlyList<ViewGroup> _groups;

        private readonly WinForms.TreeView _treeView = new WinForms.TreeView();
        private readonly WinForms.TextBox _folderTextBox = new WinForms.TextBox();
        private readonly WinForms.Button _browseButton = new WinForms.Button();
        private readonly WinForms.Button _selectAllButton = new WinForms.Button();
        private readonly WinForms.Button _clearAllButton = new WinForms.Button();
        private readonly WinForms.Button _expandAllButton = new WinForms.Button();
        private readonly WinForms.Button _collapseAllButton = new WinForms.Button();
        private readonly WinForms.Button _okButton = new WinForms.Button();
        private readonly WinForms.Button _cancelButton = new WinForms.Button();
        private readonly WinForms.Label _statusLabel = new WinForms.Label();

        private bool _suppressTreeEvents;

        public NwcBatchExportForm(IReadOnlyList<ViewGroup> groups, string initialFolder)
        {
            _groups = groups;

            Text = "Batch NWC Export";
            StartPosition = WinForms.FormStartPosition.CenterParent;
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(720, 560);

            InitializeLayout();
            _folderTextBox.Text = initialFolder;
            PopulateTree();
            UpdateStatus();
        }

        public string TargetFolder => _folderTextBox.Text.Trim();

        public IReadOnlyList<View> SelectedViews => CollectSelectedViews();

        private void InitializeLayout()
        {
            var rootLayout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new WinForms.Padding(12)
            };
            rootLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 84));
            rootLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 44));
            rootLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100));
            rootLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 48));
            Controls.Add(rootLayout);

            var folderGroup = new WinForms.GroupBox
            {
                Text = "Export Folder",
                Dock = WinForms.DockStyle.Fill
            };
            var folderLayout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2
            };
            folderLayout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100));
            folderLayout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Absolute, 32));
            folderLayout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Absolute, 100));
            folderLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 32));
            folderLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100));
            folderGroup.Controls.Add(folderLayout);

            _folderTextBox.Dock = WinForms.DockStyle.Fill;
            folderLayout.Controls.Add(_folderTextBox, 0, 0);
            folderLayout.SetColumnSpan(_folderTextBox, 2);

            _browseButton.Text = "Browse…";
            _browseButton.AutoSize = true;
            _browseButton.Dock = WinForms.DockStyle.Fill;
            _browseButton.Click += (_, __) => BrowseForFolder();
            folderLayout.Controls.Add(_browseButton, 2, 0);

            _statusLabel.Dock = WinForms.DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = DrawingColor.FromArgb(70, 70, 70);
            folderLayout.Controls.Add(_statusLabel, 0, 1);
            folderLayout.SetColumnSpan(_statusLabel, 3);

            rootLayout.Controls.Add(folderGroup, 0, 0);

            var toolbar = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                FlowDirection = WinForms.FlowDirection.LeftToRight
            };

            _selectAllButton.Text = "Select All";
            _selectAllButton.AutoSize = true;
            _selectAllButton.Click += (_, __) => SetAllNodesChecked(true);

            _clearAllButton.Text = "Clear";
            _clearAllButton.AutoSize = true;
            _clearAllButton.Click += (_, __) => SetAllNodesChecked(false);

            _expandAllButton.Text = "Expand All";
            _expandAllButton.AutoSize = true;
            _expandAllButton.Click += (_, __) =>
            {
                _treeView.BeginUpdate();
                _treeView.ExpandAll();
                _treeView.EndUpdate();
            };

            _collapseAllButton.Text = "Collapse All";
            _collapseAllButton.AutoSize = true;
            _collapseAllButton.Click += (_, __) =>
            {
                _treeView.BeginUpdate();
                foreach (WinForms.TreeNode node in _treeView.Nodes)
                {
                    node.Collapse();
                }
                _treeView.EndUpdate();
            };

            toolbar.Controls.AddRange(new WinForms.Control[]
            {
                _selectAllButton,
                _clearAllButton,
                _expandAllButton,
                _collapseAllButton
            });

            rootLayout.Controls.Add(toolbar, 0, 1);

            _treeView.Dock = WinForms.DockStyle.Fill;
            _treeView.BorderStyle = WinForms.BorderStyle.FixedSingle;
            _treeView.HideSelection = false;
            _treeView.CheckBoxes = true;
            _treeView.AfterCheck += TreeViewOnAfterCheck;
            rootLayout.Controls.Add(_treeView, 0, 2);

            var buttonsPanel = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                FlowDirection = WinForms.FlowDirection.RightToLeft
            };

            _okButton.Text = "Export";
            _okButton.AutoSize = true;
            _okButton.Click += (_, __) =>
            {
                if (!ValidateSelections())
                {
                    return;
                }

                DialogResult = WinForms.DialogResult.OK;
                Close();
            };

            _cancelButton.Text = "Cancel";
            _cancelButton.AutoSize = true;
            _cancelButton.Click += (_, __) =>
            {
                DialogResult = WinForms.DialogResult.Cancel;
                Close();
            };

            buttonsPanel.Controls.Add(_okButton);
            buttonsPanel.Controls.Add(_cancelButton);

            rootLayout.Controls.Add(buttonsPanel, 0, 3);
        }

        private void PopulateTree()
        {
            _treeView.BeginUpdate();
            _treeView.Nodes.Clear();

            foreach (var group in _groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                var groupNode = new WinForms.TreeNode($"{group.Name} ({group.Views.Count})")
                {
                    Tag = group
                };

                foreach (var view in group.Views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var childNode = new WinForms.TreeNode(view.Name)
                    {
                        Tag = view
                    };
                    groupNode.Nodes.Add(childNode);
                }

                groupNode.Expand();
                _treeView.Nodes.Add(groupNode);
            }

            _treeView.EndUpdate();
            UpdateOkButtonState();
        }

        private void BrowseForFolder()
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Select folder for Navisworks exports",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(_folderTextBox.Text)
                    ? _folderTextBox.Text
                    : string.Empty
            };

            if (dialog.ShowDialog(this) == WinForms.DialogResult.OK)
            {
                _folderTextBox.Text = dialog.SelectedPath;
                UpdateStatus();
            }
        }

        private void TreeViewOnAfterCheck(object? sender, WinForms.TreeViewEventArgs e)
        {
            if (_suppressTreeEvents)
            {
                return;
            }

            _suppressTreeEvents = true;

            try
            {
                if (e.Node.Nodes.Count > 0)
                {
                    foreach (WinForms.TreeNode child in e.Node.Nodes)
                    {
                        child.Checked = e.Node.Checked;
                    }
                }

                if (e.Node.Parent != null)
                {
                    var parent = e.Node.Parent;
                    var checkedChildren = parent.Nodes.Cast<WinForms.TreeNode>().Count(n => n.Checked);
                    parent.Checked = checkedChildren == parent.Nodes.Count;
                }
            }
            finally
            {
                _suppressTreeEvents = false;
                UpdateOkButtonState();
            }
        }

        private void SetAllNodesChecked(bool isChecked)
        {
            _suppressTreeEvents = true;
            try
            {
                foreach (WinForms.TreeNode node in _treeView.Nodes)
                {
                    node.Checked = isChecked;
                    foreach (WinForms.TreeNode child in node.Nodes)
                    {
                        child.Checked = isChecked;
                    }
                }
            }
            finally
            {
                _suppressTreeEvents = false;
                UpdateOkButtonState();
            }
        }

        private IReadOnlyList<View> CollectSelectedViews()
        {
            var result = new List<View>();

            foreach (WinForms.TreeNode groupNode in _treeView.Nodes)
            {
                foreach (WinForms.TreeNode viewNode in groupNode.Nodes)
                {
                    if (viewNode.Checked && viewNode.Tag is View view)
                    {
                        result.Add(view);
                    }
                }
            }

            return result;
        }

        private bool ValidateSelections()
        {
            var folder = TargetFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                WinForms.MessageBox.Show(this, "Select a target folder for the exports.", "Folder Required",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
                return false;
            }

            if (!Directory.Exists(folder))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    WinForms.MessageBox.Show(this, $"Unable to create folder:\n{ex.Message}", "Folder Error",
                        WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                    return false;
                }
            }

            if (CollectSelectedViews().Count == 0)
            {
                WinForms.MessageBox.Show(this, "Select at least one 3D view to export.", "No Views Selected",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
                return false;
            }

            return true;
        }

        private void UpdateOkButtonState()
        {
            _okButton.Enabled = CollectSelectedViews().Count > 0;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var selectedCount = CollectSelectedViews().Count;
            var folder = TargetFolder;
            var folderStatus = Directory.Exists(folder) ? "✓" : "⚠";
            _statusLabel.Text = $"Views selected: {selectedCount}    |    Folder: {folderStatus} {folder}";
        }
    }
}
