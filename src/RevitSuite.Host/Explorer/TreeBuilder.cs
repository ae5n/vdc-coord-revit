using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitSuite.Host.Explorer
{
    /// <summary>UI-agnostic grouped tree over <see cref="ElementRecord"/>s.</summary>
    internal sealed class ExplorerTreeNode
    {
        public ExplorerTreeNode(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public int Count { get; set; }
        public List<ExplorerTreeNode> Children { get; } = new List<ExplorerTreeNode>();

        /// <summary>Populated only on leaf group nodes; instances render one level below as rows.</summary>
        public List<ElementRecord> Instances { get; } = new List<ElementRecord>();
    }

    internal static class TreeBuilder
    {
        public static IReadOnlyList<ExplorerTreeNode> Build(
            IEnumerable<ElementRecord> records,
            GroupingMode mode,
            string? searchFilter)
        {
            var filtered = string.IsNullOrWhiteSpace(searchFilter)
                ? records
                : Filter(records, searchFilter!);

            return mode switch
            {
                GroupingMode.Category => Group(filtered,
                    r => r.Category ?? "(No Category)",
                    r => r.Family ?? "(No Family)",
                    r => r.TypeName ?? "(No Type)"),
                GroupingMode.Level => Group(filtered,
                    r => r.LevelName ?? "(No Level)",
                    r => r.Category ?? "(No Category)",
                    r => r.TypeName ?? "(No Type)"),
                GroupingMode.Workset => Group(filtered,
                    r => r.WorksetName ?? "(No Workset)",
                    r => r.Category ?? "(No Category)",
                    r => r.TypeName ?? "(No Type)"),
                GroupingMode.OwnerView => Group(filtered,
                    r => r.OwnerViewName ?? "(Model / Not View-Specific)",
                    r => r.Category ?? "(No Category)",
                    r => r.TypeName ?? "(No Type)"),
                GroupingMode.DesignOption => Group(filtered,
                    r => r.DesignOptionName ?? "(Main Model)",
                    r => r.Category ?? "(No Category)",
                    r => r.TypeName ?? "(No Type)"),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
            };
        }

        public static IEnumerable<ElementRecord> Filter(IEnumerable<ElementRecord> records, string searchFilter)
        {
            var terms = searchFilter
                .ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            return records.Where(r => terms.All(term => r.SearchText.Contains(term)));
        }

        private static IReadOnlyList<ExplorerTreeNode> Group(
            IEnumerable<ElementRecord> records,
            Func<ElementRecord, string> level1,
            Func<ElementRecord, string> level2,
            Func<ElementRecord, string> level3)
        {
            var result = new List<ExplorerTreeNode>();

            foreach (var group1 in records
                         .GroupBy(level1, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var node1 = new ExplorerTreeNode(group1.Key);

                foreach (var group2 in group1
                             .GroupBy(level2, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var node2 = new ExplorerTreeNode(group2.Key);

                    foreach (var group3 in group2
                                 .GroupBy(level3, StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        var node3 = new ExplorerTreeNode(group3.Key);
                        node3.Instances.AddRange(group3
                            .OrderBy(r => r.InstanceName, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(r => r.IdValue));
                        node3.Count = node3.Instances.Count;

                        node2.Children.Add(node3);
                        node2.Count += node3.Count;
                    }

                    node1.Children.Add(node2);
                    node1.Count += node2.Count;
                }

                result.Add(node1);
            }

            return result;
        }
    }
}
