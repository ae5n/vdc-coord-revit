using System;

namespace RevitSuite.Host.Config
{
    internal sealed class SharedCoordinatesReportConfig
    {
        public bool IncludeLinkedModels { get; }
        public int Precision { get; }
        public int AnglePrecision { get; }
        public int MaxPreviewRows { get; }

        private SharedCoordinatesReportConfig(
            bool includeLinkedModels,
            int precision,
            int anglePrecision,
            int maxPreviewRows)
        {
            IncludeLinkedModels = includeLinkedModels;
            Precision = precision;
            AnglePrecision = anglePrecision;
            MaxPreviewRows = maxPreviewRows;
        }

        public static SharedCoordinatesReportConfig Load()
        {
            var properties = SchemaDefaults.LoadProperties("shared_coordinates_report.schema.json");

            var includeLinked = SchemaDefaults.GetBool(properties, "includeLinkedModels", true);
            var precision = Math.Max(0, Math.Min(6, SchemaDefaults.GetInt(properties, "precision", 3)));
            var anglePrecision = Math.Max(0, Math.Min(6, SchemaDefaults.GetInt(properties, "anglePrecision", 4)));
            var previewRows = Math.Max(0, Math.Min(20, SchemaDefaults.GetInt(properties, "maxPreviewRows", 5)));

            return new SharedCoordinatesReportConfig(includeLinked, precision, anglePrecision, previewRows);
        }
    }
}
