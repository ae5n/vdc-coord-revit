// Copyright (c) 2026 sparx-fire (https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
// Licensed under the MIT License.

namespace RevitMCPCommandSet.Models.Common
{
    public class ViewElementsResult
    {
        public long ViewId { get; set; }
        public string ViewName { get; set; }
        public int TotalElementsInView { get; set; }
        public int FilteredElementCount { get; set; }
        public List<ElementInfo> Elements { get; set; } = new List<ElementInfo>();
    }
}