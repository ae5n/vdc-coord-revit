using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitSuite.Host.Config
{
    internal class QaqcConfig
    {
        // Global settings
        public string DefaultFamilyName { get; }
        public string DefaultTypeName { get; }
        public int CoordinatePrecision { get; }
        public int VisualizationTransparency { get; }
        public bool CreateDeviationArrows { get; }
        public double ArrowScaleFactor { get; }

        // Default tolerances and comparison method
        private readonly double _defaultToleranceGreen;
        private readonly double _defaultToleranceYellow;
        private readonly string _defaultComparisonMethod;

        // Category-specific overrides
        private readonly Dictionary<string, CategorySettings> _categorySettings;

        private QaqcConfig(
            string defaultFamilyName,
            string defaultTypeName,
            int coordinatePrecision,
            int visualizationTransparency,
            bool createDeviationArrows,
            double arrowScaleFactor,
            double defaultToleranceGreen,
            double defaultToleranceYellow,
            string defaultComparisonMethod,
            Dictionary<string, CategorySettings> categorySettings)
        {
            DefaultFamilyName = defaultFamilyName;
            DefaultTypeName = defaultTypeName;
            CoordinatePrecision = coordinatePrecision;
            VisualizationTransparency = visualizationTransparency;
            CreateDeviationArrows = createDeviationArrows;
            ArrowScaleFactor = arrowScaleFactor;
            _defaultToleranceGreen = defaultToleranceGreen;
            _defaultToleranceYellow = defaultToleranceYellow;
            _defaultComparisonMethod = defaultComparisonMethod;
            _categorySettings = categorySettings;
        }

        public static QaqcConfig Load()
        {
            var schema = SchemaDefaults.LoadSchema("qaqc.schema.json");
            var properties = schema["properties"] as JObject
                ?? throw new InvalidDataException("QAQC schema is missing top-level 'properties'.");

            // Load global settings
            var defaultFamilyName = SchemaDefaults.GetString(properties, "defaultFamilyName", "Master Control Point");
            var defaultTypeName = SchemaDefaults.GetString(properties, "defaultTypeName", "Model");

            // Load defaults from properties.defaults.properties
            var defaults = GetSectionProperties(properties, "defaults", required: true);

            var defaultToleranceGreen = SchemaDefaults.GetDouble(defaults, "toleranceGreen", 0.01);
            var defaultToleranceYellow = SchemaDefaults.GetDouble(defaults, "toleranceYellow", 0.05);
            var defaultComparisonMethod = SchemaDefaults.GetString(defaults, "comparisonMethod", "horizontal");
            var coordinatePrecision = Math.Max(0, Math.Min(6, SchemaDefaults.GetInt(defaults, "coordinatePrecision", 3)));
            var visualizationTransparency = Math.Max(0, Math.Min(100, SchemaDefaults.GetInt(defaults, "visualizationTransparency", 50)));
            var createDeviationArrows = SchemaDefaults.GetBool(defaults, "createDeviationArrows", true);
            var arrowScaleFactor = Math.Max(1.0, SchemaDefaults.GetDouble(defaults, "arrowScaleFactor", 10.0));

            // Load category-specific settings
            var categorySettings = new Dictionary<string, CategorySettings>();
            var categories = GetSectionProperties(properties, "categories", required: false);
            if (categories != null)
            {
                foreach (var kvp in categories)
                {
                    var categoryName = kvp.Key;
                    if (kvp.Value is JObject categorySchema)
                    {
                        var categoryProperties = categorySchema["properties"] as JObject;
                        if (categoryProperties == null)
                        {
                            continue;
                        }

                        categorySettings[categoryName] = new CategorySettings(
                            GetOptionalDouble(categoryProperties, "toleranceGreen", defaultToleranceGreen),
                            GetOptionalDouble(categoryProperties, "toleranceYellow", defaultToleranceYellow),
                            GetOptionalString(categoryProperties, "comparisonMethod", defaultComparisonMethod)
                        );
                    }
                }
            }

            return new QaqcConfig(
                defaultFamilyName,
                defaultTypeName,
                coordinatePrecision,
                visualizationTransparency,
                createDeviationArrows,
                arrowScaleFactor,
                defaultToleranceGreen,
                defaultToleranceYellow,
                defaultComparisonMethod,
                categorySettings);
        }

        public CategorySettings GetCategorySettings(string category)
        {
            if (_categorySettings.TryGetValue(category, out var settings))
            {
                return settings;
            }

            // Return default settings
            return new CategorySettings(_defaultToleranceGreen, _defaultToleranceYellow, _defaultComparisonMethod);
        }

        private static JObject GetSectionProperties(JObject rootProperties, string sectionName, bool required)
        {
            if (!(rootProperties[sectionName] is JObject sectionObj))
            {
                if (required)
                {
                    throw new InvalidDataException($"QAQC schema is missing section '{sectionName}'.");
                }

                return null;
            }

            if (!(sectionObj["properties"] is JObject sectionProperties))
            {
                if (required)
                {
                    throw new InvalidDataException($"QAQC schema section '{sectionName}' is missing its 'properties' object.");
                }

                return null;
            }

            return sectionProperties;
        }

        private static double GetOptionalDouble(JObject properties, string name, double fallback)
        {
            if (!(properties[name] is JObject propertyObj))
            {
                return fallback;
            }

            var token = propertyObj["default"];
            if (token != null && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return fallback;
        }

        private static string GetOptionalString(JObject properties, string name, string fallback)
        {
            if (!(properties[name] is JObject propertyObj))
            {
                return fallback;
            }

            var token = propertyObj["default"] ?? propertyObj["const"];
            var value = token?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Trim();
        }

        public class CategorySettings
        {
            public double ToleranceGreen { get; }
            public double ToleranceYellow { get; }
            public string ComparisonMethod { get; } // "horizontal", "vertical", or "total"

            public CategorySettings(double toleranceGreen, double toleranceYellow, string comparisonMethod)
            {
                ToleranceGreen = toleranceGreen;
                ToleranceYellow = toleranceYellow;
                ComparisonMethod = comparisonMethod ?? "horizontal";
            }
        }
    }
}
