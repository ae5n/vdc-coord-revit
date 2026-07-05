using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace RevitSuite.Host.Explorer.UI
{
    /// <summary>
    /// Tree item view model with tri-state checkboxes. Group items materialize their
    /// children lazily on expand; checked-state still covers unmaterialized descendants
    /// because checked records are resolved from the underlying tree node data.
    /// </summary>
    public sealed class ExplorerTreeItem : INotifyPropertyChanged
    {
        private static readonly ExplorerTreeItem Placeholder = new ExplorerTreeItem("(loading)", 0);

        private readonly ExplorerTreeNode? _node;
        private bool? _isChecked = false;
        private bool _isExpanded;
        private bool _childrenMaterialized;
        private bool _suppressPropagation;

        internal ExplorerTreeItem(ExplorerTreeNode node, ExplorerTreeItem? parent)
        {
            _node = node;
            Parent = parent;
            Label = node.Label;
            Count = node.Count;

            if (node.Children.Count > 0 || node.Instances.Count > 0)
            {
                Children.Add(Placeholder);
            }
            else
            {
                _childrenMaterialized = true;
            }
        }

        internal ExplorerTreeItem(ElementRecord record, ExplorerTreeItem? parent)
        {
            Record = record;
            Parent = parent;
            // Linked elements are visibly marked so their origin is obvious at a glance.
            Label = record.IsLinked ? $"{record.DisplayName}  🔗 {record.Origin}" : record.DisplayName;
            Count = 1;
            _childrenMaterialized = true;
        }

        private ExplorerTreeItem(string label, int count)
        {
            Label = label;
            Count = count;
            _childrenMaterialized = true;
        }

        public string Label { get; }
        public int Count { get; }
        public ElementRecord? Record { get; }
        public ExplorerTreeItem? Parent { get; }
        public ObservableCollection<ExplorerTreeItem> Children { get; } = new ObservableCollection<ExplorerTreeItem>();

        public bool IsInstance => Record != null;

        public string CountText => IsInstance ? string.Empty : $"({Count})";

        public bool? IsChecked
        {
            get => _isChecked;
            set => SetChecked(value ?? false, updateChildren: true, updateParent: true);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }

                _isExpanded = value;
                if (value)
                {
                    MaterializeChildren();
                }

                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        private void MaterializeChildren()
        {
            if (_childrenMaterialized || _node == null)
            {
                return;
            }

            _childrenMaterialized = true;
            Children.Clear();

            foreach (var childNode in _node.Children)
            {
                var child = new ExplorerTreeItem(childNode, this);
                child.SetChecked(_isChecked == true, updateChildren: false, updateParent: false);
                Children.Add(child);
            }

            foreach (var record in _node.Instances)
            {
                var child = new ExplorerTreeItem(record, this);
                child.SetChecked(_isChecked == true, updateChildren: false, updateParent: false);
                Children.Add(child);
            }
        }

        private void SetChecked(bool? value, bool updateChildren, bool updateParent)
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;

            if (updateChildren && value.HasValue && _childrenMaterialized)
            {
                _suppressPropagation = true;
                foreach (var child in Children)
                {
                    child.SetChecked(value, updateChildren: true, updateParent: false);
                }

                _suppressPropagation = false;
            }

            if (updateParent && Parent != null && !Parent._suppressPropagation)
            {
                Parent.RecalculateFromChildren();
            }

            OnPropertyChanged(nameof(IsChecked));
        }

        private void RecalculateFromChildren()
        {
            bool? state = null;
            var allChecked = Children.All(c => c._isChecked == true);
            var allUnchecked = Children.All(c => c._isChecked == false);
            if (allChecked)
            {
                state = true;
            }
            else if (allUnchecked)
            {
                state = false;
            }

            SetChecked(state, updateChildren: false, updateParent: true);
        }

        /// <summary>
        /// All records covered by the checked state of this subtree. A fully checked group
        /// contributes every record under its node, even if children were never materialized.
        /// </summary>
        internal void CollectCheckedRecords(List<ElementRecord> sink)
        {
            if (_isChecked == false)
            {
                return;
            }

            if (IsInstance)
            {
                if (_isChecked == true)
                {
                    sink.Add(Record!);
                }

                return;
            }

            if (_isChecked == true && !_childrenMaterialized)
            {
                // Fully checked group whose children were never expanded: take every record
                // under the underlying node.
                CollectAllRecords(sink);
                return;
            }

            foreach (var child in Children)
            {
                child.CollectCheckedRecords(sink);
            }
        }

        internal void CollectAllRecords(List<ElementRecord> sink)
        {
            if (Record != null)
            {
                sink.Add(Record);
                return;
            }

            if (_node != null)
            {
                CollectNodeRecords(_node, sink);
            }
        }

        private static void CollectNodeRecords(ExplorerTreeNode node, List<ElementRecord> sink)
        {
            sink.AddRange(node.Instances);
            foreach (var child in node.Children)
            {
                CollectNodeRecords(child, sink);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
