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

        /// <summary>
        /// Window-provided classifiers so tree rows can derive their state from the
        /// authoritative stores (the persistent checked-key set / the latest view snapshot)
        /// instead of only remembering clicks — this is what lets checks survive
        /// regroup/search rebuilds and lazily expanded rows appear with correct state.
        /// Set once by the Explorer window; cleared when it closes.
        /// </summary>
        internal static Func<ElementRecord, bool>? CheckClassifier;
        internal static Func<ElementRecord, bool>? HiddenClassifier;

        /// <summary>WHY a record is hidden (VG category vs element hide vs link) — eye tooltip.</summary>
        internal static Func<ElementRecord, string?>? HiddenReasonClassifier;

        /// <summary>Compact hide-mechanism tag ("VG", "elem", …) shown inline next to the eye.</summary>
        internal static Func<ElementRecord, string?>? HiddenTagClassifier;

        /// <summary>Raised when the USER toggles a row's checkbox (not on programmatic re-application).</summary>
        internal static event Action<ExplorerTreeItem, bool>? UserCheckChanged;

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
            set
            {
                var effective = value ?? false;
                SetChecked(effective, updateChildren: true, updateParent: true);
                UserCheckChanged?.Invoke(this, effective);
            }
        }

        private bool _isHiddenIndicated;

        /// <summary>
        /// True when everything under this row is hidden in the active view (passive
        /// indicator, computed from the last snapshot — not a live state engine).
        /// </summary>
        public bool IsHiddenIndicated
        {
            get => _isHiddenIndicated;
            private set
            {
                if (_isHiddenIndicated == value)
                {
                    return;
                }

                _isHiddenIndicated = value;
                OnPropertyChanged(nameof(IsHiddenIndicated));
                OnPropertyChanged(nameof(HiddenGlyph));
            }
        }

        private string? _hiddenReason;
        private string? _hiddenTag;

        /// <summary>Eye-glyph tooltip: the specific hide mechanism when known.</summary>
        public string HiddenToolTip => _hiddenReason ?? "Hidden in the active view";

        /// <summary>Inline hide-mechanism tag next to the eye ("VG", "elem", …); empty when visible.</summary>
        public string HiddenTagText => _hiddenTag ?? string.Empty;

        private void SetHiddenReason(string? reason, string? tag)
        {
            if (_hiddenReason != reason)
            {
                _hiddenReason = reason;
                OnPropertyChanged(nameof(HiddenToolTip));
            }

            if (_hiddenTag != tag)
            {
                _hiddenTag = tag;
                OnPropertyChanged(nameof(HiddenTagText));
            }
        }

        /// <summary>Segoe MDL2 "Hide" glyph shown only next to hidden rows; empty otherwise.</summary>
        public string HiddenGlyph => _isHiddenIndicated ? "" : string.Empty;

        /// <summary>
        /// Re-derives this subtree's checkbox and hidden-indicator state from the window's
        /// classifiers. Instance rows classify directly; group rows aggregate over every
        /// record under their node (materialized or not).
        /// </summary>
        internal void ApplyIndicators()
        {
            var checkClassifier = CheckClassifier;
            var hiddenClassifier = HiddenClassifier;

            if (Record != null)
            {
                if (checkClassifier != null)
                {
                    SetChecked(checkClassifier(Record), updateChildren: false, updateParent: false);
                }

                IsHiddenIndicated = hiddenClassifier?.Invoke(Record) == true;
                SetHiddenReason(
                    _isHiddenIndicated ? HiddenReasonClassifier?.Invoke(Record) : null,
                    _isHiddenIndicated ? HiddenTagClassifier?.Invoke(Record) : null);
            }
            else if (_node != null)
            {
                var records = new List<ElementRecord>();
                CollectAllRecords(records);

                if (checkClassifier != null)
                {
                    var anyChecked = false;
                    var allChecked = records.Count > 0;
                    foreach (var record in records)
                    {
                        if (checkClassifier(record))
                        {
                            anyChecked = true;
                        }
                        else
                        {
                            allChecked = false;
                        }

                        if (anyChecked && !allChecked)
                        {
                            break;
                        }
                    }

                    SetChecked(allChecked ? true : anyChecked ? (bool?)null : false,
                        updateChildren: false, updateParent: false);
                }

                if (hiddenClassifier != null)
                {
                    IsHiddenIndicated = records.Count > 0 && records.All(r => hiddenClassifier(r));
                    if (_isHiddenIndicated)
                    {
                        // One shared mechanism shows its tag; a mix is labeled as such.
                        string? tag = null;
                        if (HiddenTagClassifier is { } tagClassifier)
                        {
                            var tags = records.Select(tagClassifier).Distinct().ToList();
                            tag = tags.Count == 1 ? tags[0] : "mixed";
                        }

                        SetHiddenReason(
                            $"All {records.Count:N0} element(s) under this group are hidden in the active view " +
                            "(expand for per-element reasons)",
                            tag);
                    }
                    else
                    {
                        SetHiddenReason(null, null);
                    }
                }
            }

            if (_childrenMaterialized)
            {
                foreach (var child in Children)
                {
                    if (!ReferenceEquals(child, Placeholder))
                    {
                        child.ApplyIndicators();
                    }
                }
            }
        }

        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
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

            // Fresh rows derive their true state (a partially-checked parent's children
            // must not all appear unchecked, and hidden rows need their indicator).
            if (CheckClassifier != null || HiddenClassifier != null)
            {
                foreach (var child in Children)
                {
                    child.ApplyIndicators();
                }
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
