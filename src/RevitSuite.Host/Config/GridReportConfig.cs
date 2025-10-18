using System;

namespace RevitSuite.Host.Config
{
    internal class GridReportConfig
    {
        public bool IncludeLinkedModels { get; }
        public int Precision { get; }
        public int MaxPreviewRows { get; }

        private GridReportConfig(bool includeLinkedModels, int precision, int maxPreviewRows)
        {
            IncludeLinkedModels = includeLinkedModels;
            Precision = precision;
            MaxPreviewRows = maxPreviewRows;
        }

        public static GridReportConfig Load()
        {
            var properties = SchemaDefaults.LoadProperties("grid_report.schema.json");

            var includeLinked = SchemaDefaults.GetBool(properties, "includeLinkedModels", true);
            var precision = Math.Max(0, Math.Min(6, SchemaDefaults.GetInt(properties, "precision", 2)));
            var previewRows = Math.Max(0, Math.Min(20, SchemaDefaults.GetInt(properties, "maxPreviewRows", 5)));

            return new GridReportConfig(includeLinked, precision, previewRows);
        }
    }
}
