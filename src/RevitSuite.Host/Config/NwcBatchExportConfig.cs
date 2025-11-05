using System;
using Autodesk.Revit.DB;

namespace RevitSuite.Host.Config
{
    internal sealed class NwcBatchExportConfig
    {
        private NwcBatchExportConfig(
            string groupParameterName,
            NavisworksCoordinates coordinates,
            NavisworksExportScope exportScope,
            NavisworksParameters parameters,
            bool exportLinks,
            bool exportParts,
            bool exportElementIds,
            bool convertElementProperties,
            bool exportRoomAsAttribute,
            bool exportRoomGeometry,
            bool exportUrls,
            bool divideFileIntoLevels,
            bool findMissingMaterials,
            bool convertLights,
            bool convertLinkedCadFormats,
            double facetingFactor)
        {
            GroupParameterName = groupParameterName;
            Coordinates = coordinates;
            ExportScope = exportScope;
            Parameters = parameters;
            ExportLinks = exportLinks;
            ExportParts = exportParts;
            ExportElementIds = exportElementIds;
            ConvertElementProperties = convertElementProperties;
            ExportRoomAsAttribute = exportRoomAsAttribute;
            ExportRoomGeometry = exportRoomGeometry;
            ExportUrls = exportUrls;
            DivideFileIntoLevels = divideFileIntoLevels;
            FindMissingMaterials = findMissingMaterials;
            ConvertLights = convertLights;
            ConvertLinkedCadFormats = convertLinkedCadFormats;
            FacetingFactor = facetingFactor;
        }

        public string GroupParameterName { get; }
        public NavisworksCoordinates Coordinates { get; }
        public NavisworksExportScope ExportScope { get; }
        public NavisworksParameters Parameters { get; }
        public bool ExportLinks { get; }
        public bool ExportParts { get; }
        public bool ExportElementIds { get; }
        public bool ConvertElementProperties { get; }
        public bool ExportRoomAsAttribute { get; }
        public bool ExportRoomGeometry { get; }
        public bool ExportUrls { get; }
        public bool DivideFileIntoLevels { get; }
        public bool FindMissingMaterials { get; }
        public bool ConvertLights { get; }
        public bool ConvertLinkedCadFormats { get; }
        public double FacetingFactor { get; }

        public static NwcBatchExportConfig Load()
        {
            var properties = SchemaDefaults.LoadProperties("nwc_batch_export.schema.json");

            var groupParameter = SchemaDefaults.GetString(properties, "groupParameterName", "Type");
            var coordinates = ParseCoordinates(SchemaDefaults.GetString(properties, "coordinates", "Shared"));
            var exportScope = ParseScope(SchemaDefaults.GetString(properties, "exportScope", "View"));
            var parameters = ParseParameters(SchemaDefaults.GetString(properties, "parameters", "Elements"));

            var exportLinks = SchemaDefaults.GetBool(properties, "exportLinks", true);
            var exportParts = SchemaDefaults.GetBool(properties, "exportParts", false);
            var exportElementIds = SchemaDefaults.GetBool(properties, "exportElementIds", true);
            var convertElementProperties = SchemaDefaults.GetBool(properties, "convertElementProperties", false);
            var exportRoomAsAttribute = SchemaDefaults.GetBool(properties, "exportRoomAsAttribute", false);
            var exportRoomGeometry = SchemaDefaults.GetBool(properties, "exportRoomGeometry", false);
            var exportUrls = SchemaDefaults.GetBool(properties, "exportUrls", false);
            var divideFileIntoLevels = SchemaDefaults.GetBool(properties, "divideFileIntoLevels", true);
            var findMissingMaterials = SchemaDefaults.GetBool(properties, "findMissingMaterials", false);
            var convertLights = SchemaDefaults.GetBool(properties, "convertLights", false);
            var convertLinkedCadFormats = SchemaDefaults.GetBool(properties, "convertLinkedCadFormats", true);
            var facetingFactor = Math.Max(0.1, Math.Min(20.0, SchemaDefaults.GetDouble(properties, "facetingFactor", 1.0)));

            return new NwcBatchExportConfig(
                groupParameter,
                coordinates,
                exportScope,
                parameters,
                exportLinks,
                exportParts,
                exportElementIds,
                convertElementProperties,
                exportRoomAsAttribute,
                exportRoomGeometry,
                exportUrls,
                divideFileIntoLevels,
                findMissingMaterials,
                convertLights,
                convertLinkedCadFormats,
                facetingFactor);
        }

        private static NavisworksCoordinates ParseCoordinates(string value)
        {
            if (Enum.TryParse(value, true, out NavisworksCoordinates parsed))
            {
                return parsed;
            }

            return NavisworksCoordinates.Shared;
        }

        private static NavisworksExportScope ParseScope(string value)
        {
            if (Enum.TryParse(value, true, out NavisworksExportScope parsed))
            {
                return parsed;
            }

            return NavisworksExportScope.View;
        }

        private static NavisworksParameters ParseParameters(string value)
        {
            if (Enum.TryParse(value, true, out NavisworksParameters parsed))
            {
                return parsed;
            }

            return NavisworksParameters.Elements;
        }
    }
}
