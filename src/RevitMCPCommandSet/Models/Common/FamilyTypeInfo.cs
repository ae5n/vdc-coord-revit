// Copyright (c) 2026 sparx-fire (https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
// Licensed under the MIT License.

namespace RevitMCPCommandSet.Models.Common
{
    public class FamilyTypeInfo
    {
        public long FamilyTypeId { get; set; }
        public string UniqueId { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string Category { get; set; }
    }
}