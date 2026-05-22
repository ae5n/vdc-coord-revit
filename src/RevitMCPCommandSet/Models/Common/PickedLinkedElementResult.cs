// Copyright (c) 2026 sparx-fire (https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
// Licensed under the MIT License.

namespace RevitMCPCommandSet.Models.Common
{
    public class PickedLinkedElementResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public string Message { get; set; }
        public int Count { get; set; }
        public List<PickedLinkedElementInfo> Elements { get; set; } = new List<PickedLinkedElementInfo>();
    }

    public class PickedLinkedElementInfo
    {
        public string HostDocumentTitle { get; set; }
        public long? LinkInstanceId { get; set; }
        public string LinkInstanceUniqueId { get; set; }
        public string LinkInstanceName { get; set; }
        public string LinkedDocumentTitle { get; set; }
        public long? LinkedElementId { get; set; }
        public string LinkedElementUniqueId { get; set; }
        public string LinkedElementName { get; set; }
        public string LinkedElementType { get; set; }
        public string LinkedElementCategory { get; set; }
    }
}
