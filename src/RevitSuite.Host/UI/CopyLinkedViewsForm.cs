using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Autodesk.Revit.DB;
using RevitSuite.Host.Commands;
using WinForms = System.Windows.Forms;

namespace RevitSuite.Host.UI
{
    internal sealed class CopyLinkedViewsForm : WinForms.Form
    {
        private readonly IReadOnlyList<LinkedModelOption> _linkedModels;
        private readonly HashSet<ViewType> _supportedViewTypes;
        private readonly HashSet<ViewType> _activeViewTypes;

        private readonly WinForms.CheckedListBox _linkedModelsList = new WinForms.CheckedListBox();
        private readonly WinForms.TreeView _treeView = new WinForms.TreeView();
        private readonly WinForms.CheckedListBox _viewTypeFilterList = new WinForms.CheckedListBox();
        private readonly WinForms.Button _selectAllModelsButton = new WinForms.Button();
        private readonly WinForms.Button _clearModelsButton = new WinForms.Button();
        private readonly WinForms.Button _selectAllFiltersButton = new WinForms.Button();
        private readonly WinForms.Button _clearFiltersButton = new WinForms.Button();
        private readonly WinForms.Button _expandAllButton = new WinForms.Button();
        private readonly WinForms.Button _collapseAllButton = new WinForms.Button();
        private readonly WinForms.Button _okButton = new WinForms.Button();
        private readonly WinForms.Button _cancelButton = new WinForms.Button();

        private bool _suppressTreeEvents;

        private readonly HashSet<string> _activeModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _persistedViewSetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _persistedViewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CopyLinkedViewsForm(IReadOnlyList<LinkedModelOption> linkedModels, IReadOnlyCollection<ViewType> supportedViewTypes)
        {
            _linkedModels = linkedModels;
            _supportedViewTypes = new HashSet<ViewType>(supportedViewTypes);
            _activeViewTypes = new HashSet<ViewType>(_supportedViewTypes);

            Text = "Copy Views From Linked Models";
            StartPosition = WinForms.FormStartPosition.CenterParent;
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(1080, 640);

            InitializeLayout();
            PopulateLinkedModelList();
            _linkedModelsList.ItemCheck += LinkedModelsListOnItemCheck;
            PopulateFilterList();
            _viewTypeFilterList.ItemCheck += ViewTypeFilterListOnItemCheck;
            RefreshTree();
            UpdateOkButtonState();
        }

        public CopyLinkedViewsSelection GetSelection() => CollectSelectionFromTree();

        private void InitializeLayout()
        {
            var rootLayout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new WinForms.Padding(10)
            };
            rootLayout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Absolute, 260));
            rootLayout.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100));
            Controls.Add(rootLayout);

            var leftColumn = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                RowCount = 2,
                Padding = new WinForms.Padding(0, 0, 8, 0)
            };
            leftColumn.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 45));
            leftColumn.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 55));
            rootLayout.Controls.Add(leftColumn, 0, 0);

            var linkedModelsGroup = new WinForms.GroupBox
            {
                Text = "Linked Models",
                Dock = WinForms.DockStyle.Fill
            };
            var linkedModelsLayout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                RowCount = 2
            };
            linkedModelsLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100));
            linkedModelsLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 42));
            linkedModelsGroup.Controls.Add(linkedModelsLayout);

            _linkedModelsList.Dock = WinForms.DockStyle.Fill;
            _linkedModelsList.CheckOnClick = true;
            linkedModelsLayout.Controls.Add(_linkedModelsList, 0, 0);

            var modelButtons = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                FlowDirection = WinForms.FlowDirection.LeftToRight,
                Padding = new WinForms.Padding(0, 6, 0, 0)
            };
            _selectAllModelsButton.Text = "Select All";
            _selectAllModelsButton.AutoSize = true;
            _selectAllModelsButton.Click += (_, __) => SetAllModelItems(true);

            _clearModelsButton.Text = "Clear";
            _clearModelsButton.AutoSize = true;
            _clearModelsButton.Click += (_, __) => SetAllModelItems(false);

            modelButtons.Controls.Add(_selectAllModelsButton);
            modelButtons.Controls.Add(_clearModelsButton);
            linkedModelsLayout.Controls.Add(modelButtons, 0, 1);
            leftColumn.Controls.Add(linkedModelsGroup, 0, 0);

            var filterGroup = new WinForms.GroupBox
            {
                Text = "View Type Filter",
                Dock = WinForms.DockStyle.Fill
            };
            var filterLayout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                RowCount = 2
            };
            filterLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100));
            filterLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 42));
            filterGroup.Controls.Add(filterLayout);

            _viewTypeFilterList.Dock = WinForms.DockStyle.Fill;
            _viewTypeFilterList.CheckOnClick = true;
            filterLayout.Controls.Add(_viewTypeFilterList, 0, 0);

            var filterButtons = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                FlowDirection = WinForms.FlowDirection.LeftToRight,
                Padding = new WinForms.Padding(0, 6, 0, 0)
            };
            _selectAllFiltersButton.Text = "Select All";
            _selectAllFiltersButton.AutoSize = true;
            _selectAllFiltersButton.Click += (_, __) => SetAllFilterItems(true);

            _clearFiltersButton.Text = "Clear";
            _clearFiltersButton.AutoSize = true;
            _clearFiltersButton.Click += (_, __) => SetAllFilterItems(false);

            filterButtons.Controls.Add(_selectAllFiltersButton);
            filterButtons.Controls.Add(_clearFiltersButton);
            filterLayout.Controls.Add(filterButtons, 0, 1);

            leftColumn.Controls.Add(filterGroup, 0, 1);

            var rightLayout = new WinForms.TableLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                RowCount = 3
            };
            rightLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 36));
            rightLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100));
            rightLayout.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Absolute, 46));
            rootLayout.Controls.Add(rightLayout, 1, 0);

            var toolbar = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                FlowDirection = WinForms.FlowDirection.LeftToRight
            };
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

            toolbar.Controls.Add(_expandAllButton);
            toolbar.Controls.Add(_collapseAllButton);
            rightLayout.Controls.Add(toolbar, 0, 0);

            _treeView.Dock = WinForms.DockStyle.Fill;
            _treeView.BorderStyle = WinForms.BorderStyle.FixedSingle;
            _treeView.CheckBoxes = true;
            _treeView.HideSelection = false;
            _treeView.FullRowSelect = true;
            _treeView.ShowNodeToolTips = true;
            _treeView.AfterCheck += TreeViewOnAfterCheck;
            rightLayout.Controls.Add(_treeView, 0, 1);

            var buttonPanel = new WinForms.FlowLayoutPanel
            {
                Dock = WinForms.DockStyle.Fill,
                FlowDirection = WinForms.FlowDirection.RightToLeft,
                Padding = new WinForms.Padding(0, 8, 0, 0)
            };

            _okButton.Text = "Copy";
            _okButton.Width = 120;
            _okButton.DialogResult = WinForms.DialogResult.OK;
            _okButton.Click += (_, __) => DialogResult = WinForms.DialogResult.OK;

            _cancelButton.Text = "Cancel";
            _cancelButton.Width = 100;
            _cancelButton.DialogResult = WinForms.DialogResult.Cancel;

            buttonPanel.Controls.Add(_okButton);
            buttonPanel.Controls.Add(_cancelButton);
            rightLayout.Controls.Add(buttonPanel, 0, 2);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void PopulateLinkedModelList()
        {
            _linkedModelsList.DisplayMember = nameof(LinkedModelOption.DisplayName);
            _linkedModelsList.Items.Clear();
            _activeModelIds.Clear();

            foreach (var model in _linkedModels)
            {
                var index = _linkedModelsList.Items.Add(model);
                _linkedModelsList.SetItemChecked(index, true);
                _activeModelIds.Add(model.LinkInstance.UniqueId);
            }
        }

        private void LinkedModelsListOnItemCheck(object? sender, WinForms.ItemCheckEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }

            if (_linkedModelsList.Items[e.Index] is not LinkedModelOption)
            {
                return;
            }

            if (IsHandleCreated)
            {
                BeginInvoke(new Action(UpdateActiveModelsAndRefresh));
            }
            else
            {
                UpdateActiveModelsAndRefresh();
            }
        }

        private void SetAllModelItems(bool isChecked)
        {
            for (var i = 0; i < _linkedModelsList.Items.Count; i++)
            {
                _linkedModelsList.SetItemChecked(i, isChecked);
            }

            if (IsHandleCreated)
            {
                BeginInvoke(new Action(UpdateActiveModelsAndRefresh));
            }
            else
            {
                UpdateActiveModelsAndRefresh();
            }
        }

        private void UpdateActiveModelsAndRefresh()
        {
            _activeModelIds.Clear();

            for (var i = 0; i < _linkedModelsList.Items.Count; i++)
            {
                if (!_linkedModelsList.GetItemChecked(i))
                {
                    continue;
                }

                if (_linkedModelsList.Items[i] is LinkedModelOption model)
                {
                    _activeModelIds.Add(model.LinkInstance.UniqueId);
                }
            }

            RefreshTree();
        }

        private void PopulateFilterList()
        {
            _viewTypeFilterList.Items.Clear();
            _viewTypeFilterList.Format += (_, args) =>
            {
                if (args.ListItem is ViewType type)
                {
                    args.Value = GetViewTypeDisplay(type);
                }
            };

            foreach (var viewType in _supportedViewTypes
                         .OrderBy(GetViewTypeDisplay, StringComparer.OrdinalIgnoreCase))
            {
                var index = _viewTypeFilterList.Items.Add(viewType);
                _viewTypeFilterList.SetItemChecked(index, true);
            }
        }

        private void RefreshTree()
        {
            PersistSelection();

            _treeView.BeginUpdate();
            _treeView.Nodes.Clear();

            foreach (var linkedModel in _linkedModels)
            {
                if (!_activeModelIds.Contains(linkedModel.LinkInstance.UniqueId))
                {
                    continue;
                }

                var rootNode = new WinForms.TreeNode(linkedModel.DisplayName)
                {
                    Tag = new LinkedModelNodeTag(linkedModel)
                };

                var viewsById = linkedModel.Views
                    .ToDictionary(v => v.ViewId, v => v, ElementIdEqualityComparer.Instance);

                var usedInSets = new HashSet<ElementId>(ElementIdEqualityComparer.Instance);

                foreach (var viewSet in linkedModel.ViewSets
                             .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var eligibleViews = new List<ViewOption>();
                    foreach (var id in viewSet.ViewIds)
                    {
                        if (viewsById.TryGetValue(id, out var view) &&
                            _activeViewTypes.Contains(view.ViewType))
                        {
                            eligibleViews.Add(view);
                        }
                    }

                    if (eligibleViews.Count == 0)
                    {
                        continue;
                    }

                    var setNode = new WinForms.TreeNode($"{viewSet.Name} ({eligibleViews.Count})")
                    {
                        Tag = new ViewSetNodeTag(linkedModel, viewSet, eligibleViews)
                    };

                    foreach (var view in eligibleViews
                                 .OrderBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase))
                    {
                        var viewNode = new WinForms.TreeNode($"{view.ViewName} [{GetViewTypeDisplay(view.ViewType)}]")
                        {
                            Tag = new ViewNodeTag(linkedModel, view),
                            ToolTipText = $"{GetViewTypeDisplay(view.ViewType)}"
                        };
                        setNode.Nodes.Add(viewNode);
                        usedInSets.Add(view.ViewId);
                    }

                    rootNode.Nodes.Add(setNode);
                }

                var individualViews = linkedModel.Views
                    .Where(v => !usedInSets.Contains(v.ViewId) && _activeViewTypes.Contains(v.ViewType))
                    .OrderBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (individualViews.Count > 0)
                {
                    var groupNode = new WinForms.TreeNode("Individual Views")
                    {
                        Tag = new GroupNodeTag(linkedModel)
                    };

                    foreach (var view in individualViews)
                    {
                        var viewNode = new WinForms.TreeNode($"{view.ViewName} [{GetViewTypeDisplay(view.ViewType)}]")
                        {
                            Tag = new ViewNodeTag(linkedModel, view),
                            ToolTipText = $"{GetViewTypeDisplay(view.ViewType)}"
                        };
                        groupNode.Nodes.Add(viewNode);
                    }

                    rootNode.Nodes.Add(groupNode);
                }

                if (rootNode.Nodes.Count > 0)
                {
                    _treeView.Nodes.Add(rootNode);
                }
            }

            ApplyPersistedSelection();
            _treeView.EndUpdate();
            UpdateOkButtonState();
        }

        private void ViewTypeFilterListOnItemCheck(object? sender, WinForms.ItemCheckEventArgs e)
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(UpdateActiveViewTypesAndRefresh));
            }
            else
            {
                UpdateActiveViewTypesAndRefresh();
            }
        }

        private void SetAllFilterItems(bool isChecked)
        {
            for (var i = 0; i < _viewTypeFilterList.Items.Count; i++)
            {
                _viewTypeFilterList.SetItemChecked(i, isChecked);
            }

            if (IsHandleCreated)
            {
                BeginInvoke(new Action(UpdateActiveViewTypesAndRefresh));
            }
            else
            {
                UpdateActiveViewTypesAndRefresh();
            }
        }

        private void UpdateActiveViewTypesAndRefresh()
        {
            _activeViewTypes.Clear();
            for (var i = 0; i < _viewTypeFilterList.Items.Count; i++)
            {
                if (_viewTypeFilterList.GetItemChecked(i) &&
                    _viewTypeFilterList.Items[i] is ViewType type)
                {
                    _activeViewTypes.Add(type);
                }
            }

            if (_activeViewTypes.Count == 0)
            {
                // Leave the set empty to show an empty tree rather than forcing a selection.
            }

            RefreshTree();
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
                switch (e.Node.Tag)
                {
                    case LinkedModelNodeTag:
                        PropagateToChildren(e.Node, e.Node.Checked);
                        break;
                    case ViewSetNodeTag:
                        PropagateToChildren(e.Node, e.Node.Checked);
                        break;
                    case GroupNodeTag:
                        PropagateToChildren(e.Node, e.Node.Checked);
                        break;
                    case ViewNodeTag:
                        if (e.Node.Parent != null)
                        {
                            UpdateParentCheckState(e.Node.Parent);
                        }
                        break;
                    default:
                        PropagateToChildren(e.Node, e.Node.Checked);
                        break;
                }

                if (e.Node.Parent != null &&
                    e.Node.Tag is not ViewSetNodeTag)
                {
                    UpdateParentCheckState(e.Node.Parent);
                }
            }
            finally
            {
                _suppressTreeEvents = false;
            }

            UpdateOkButtonState();
        }

        private void PersistSelection()
        {
            var selection = CollectSelectionFromTree();
            _persistedViewSetKeys.Clear();
            _persistedViewIds.Clear();

            foreach (var viewSet in selection.SelectedViewSets)
            {
                _persistedViewSetKeys.Add(BuildViewSetKey(viewSet.LinkedModel, viewSet.Name));
            }

            foreach (var view in selection.SelectedViews)
            {
                _persistedViewIds.Add(view.ViewUniqueId);
            }
        }

        private void ApplyPersistedSelection()
        {
            _suppressTreeEvents = true;
            try
            {
                foreach (WinForms.TreeNode root in _treeView.Nodes)
                {
                    ApplySelectionToNode(root);
                }
            }
            finally
            {
                _suppressTreeEvents = false;
            }

            foreach (WinForms.TreeNode root in _treeView.Nodes)
            {
                root.Expand();
            }
        }

        private void ApplySelectionToNode(WinForms.TreeNode node)
        {
            switch (node.Tag)
            {
                case ViewSetNodeTag setTag:
                    var key = BuildViewSetKey(setTag.LinkedModel, setTag.ViewSet.Name);
                    var setChecked = _persistedViewSetKeys.Contains(key);
                    SetNodeChecked(node, setChecked);

                    if (setChecked)
                    {
                        PropagateToChildren(node, true);
                    }
                    else
                    {
                        foreach (WinForms.TreeNode child in node.Nodes)
                        {
                            ApplySelectionToNode(child);
                        }
                    }

                    UpdateParentCheckState(node);
                    break;

                case ViewNodeTag viewTag:
                    var viewChecked = _persistedViewIds.Contains(viewTag.View.ViewUniqueId);
                    SetNodeChecked(node, viewChecked);
                    break;

                case LinkedModelNodeTag:
                case GroupNodeTag:
                    foreach (WinForms.TreeNode child in node.Nodes)
                    {
                        ApplySelectionToNode(child);
                    }
                    UpdateParentCheckState(node);
                    break;

                default:
                    foreach (WinForms.TreeNode child in node.Nodes)
                    {
                        ApplySelectionToNode(child);
                    }
                    break;
            }
        }

        private void PropagateToChildren(WinForms.TreeNode node, bool isChecked)
        {
            foreach (WinForms.TreeNode child in node.Nodes)
            {
                SetNodeChecked(child, isChecked);
                PropagateToChildren(child, isChecked);
            }
        }

        private void SetNodeChecked(WinForms.TreeNode node, bool isChecked)
        {
            if (node.Checked == isChecked)
            {
                return;
            }

            node.Checked = isChecked;
        }

        private void UpdateParentCheckState(WinForms.TreeNode parent)
        {
            if (parent.Nodes.Count == 0)
            {
                return;
            }

            var children = parent.Nodes.Cast<WinForms.TreeNode>().ToList();
            var allChecked = children.All(n => n.Checked);
            var anyChecked = children.Any(n => n.Checked);

            if (allChecked)
            {
                SetNodeChecked(parent, true);
            }
            else if (!anyChecked)
            {
                SetNodeChecked(parent, false);
            }
            else
            {
                SetNodeChecked(parent, false);
            }

            if (parent.Parent != null)
            {
                UpdateParentCheckState(parent.Parent);
            }
        }

        private CopyLinkedViewsSelection CollectSelectionFromTree()
        {
            var result = new CopyLinkedViewsSelection();

            foreach (WinForms.TreeNode root in _treeView.Nodes)
            {
                if (root.Tag is not LinkedModelNodeTag modelTag)
                {
                    continue;
                }

                foreach (WinForms.TreeNode child in root.Nodes)
                {
                    if (child.Tag is ViewSetNodeTag setTag)
                    {
                        if (child.Checked)
                        {
                            var viewIds = setTag.Views
                                .Select(v => v.ViewId)
                                .ToList();

                            result.SelectedViewSets.Add(new ViewSetSelection(
                                modelTag.LinkedModel,
                                setTag.ViewSet.Name,
                                viewIds,
                                setTag.ViewSet.IsSynthetic));
                        }
                        else
                        {
                            AppendCheckedViewChildren(child, modelTag.LinkedModel, result);
                        }
                    }
                    else if (child.Tag is GroupNodeTag)
                    {
                        AppendCheckedViewChildren(child, modelTag.LinkedModel, result);
                    }
                    else if (child.Tag is ViewNodeTag viewTag)
                    {
                        if (child.Checked)
                        {
                            result.SelectedViews.Add(CreateViewSelection(modelTag.LinkedModel, viewTag.View));
                        }
                    }
                }
            }

            return result;
        }

        private static void AppendCheckedViewChildren(WinForms.TreeNode parent, LinkedModelOption linkedModel, CopyLinkedViewsSelection selection)
        {
            foreach (WinForms.TreeNode child in parent.Nodes)
            {
                if (child.Tag is ViewNodeTag viewTag)
                {
                    if (child.Checked)
                    {
                        selection.SelectedViews.Add(CreateViewSelection(linkedModel, viewTag.View));
                    }
                }
                else
                {
                    AppendCheckedViewChildren(child, linkedModel, selection);
                }
            }
        }

        private static ViewSelectionEntry CreateViewSelection(LinkedModelOption linkedModel, ViewOption view)
        {
            return new ViewSelectionEntry(
                linkedModel,
                view.ViewId,
                view.ViewUniqueId,
                view.ViewName,
                view.ViewType);
        }

        private void UpdateOkButtonState()
        {
            var selection = CollectSelectionFromTree();
            _okButton.Enabled = selection.SelectedViews.Count > 0 || selection.SelectedViewSets.Count > 0;
        }

        private static string GetViewTypeDisplay(ViewType viewType)
        {
            return viewType switch
            {
                ViewType.FloorPlan => "Plan – Floor",
                ViewType.CeilingPlan => "Plan – Ceiling",
                ViewType.EngineeringPlan => "Plan – Structural",
                ViewType.ThreeD => "3D View",
                _ => viewType.ToString()
            };
        }

        private static string BuildViewSetKey(LinkedModelOption linkedModel, string viewSetName)
        {
            return $"{linkedModel.LinkInstance.Id.IntegerValue}:{viewSetName}";
        }

        private sealed class LinkedModelNodeTag : NodeTag
        {
            public LinkedModelNodeTag(LinkedModelOption linkedModel)
                : base(linkedModel)
            {
            }
        }

        private sealed class ViewSetNodeTag : NodeTag
        {
            public ViewSetNodeTag(LinkedModelOption linkedModel, ViewSetOption viewSet, IReadOnlyList<ViewOption> views)
                : base(linkedModel)
            {
                ViewSet = viewSet;
                Views = views;
            }

            public ViewSetOption ViewSet { get; }
            public IReadOnlyList<ViewOption> Views { get; }
        }

        private sealed class ViewNodeTag : NodeTag
        {
            public ViewNodeTag(LinkedModelOption linkedModel, ViewOption view)
                : base(linkedModel)
            {
                View = view;
            }

            public ViewOption View { get; }
        }

        private sealed class GroupNodeTag : NodeTag
        {
            public GroupNodeTag(LinkedModelOption linkedModel)
                : base(linkedModel)
            {
            }
        }

        private abstract class NodeTag
        {
            protected NodeTag(LinkedModelOption linkedModel)
            {
                LinkedModel = linkedModel;
            }

            public LinkedModelOption LinkedModel { get; }
        }

        private sealed class ElementIdEqualityComparer : IEqualityComparer<ElementId>
        {
            public static readonly ElementIdEqualityComparer Instance = new ElementIdEqualityComparer();

            public bool Equals(ElementId? x, ElementId? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x is null || y is null)
                {
                    return false;
                }

                return x.Value == y.Value;
            }

            public int GetHashCode(ElementId obj)
            {
                return obj.Value.GetHashCode();
            }
        }
    }
}
