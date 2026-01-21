using System;
using System.Collections.Generic;
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
            var properties = SchemaDefaults.LoadProperties("qaqc.schema.json");

            // Load global settings
            var defaultFamilyName = SchemaDefaults.GetString(properties, "defaultFamilyName", "Control Point");
            var defaultTypeName = SchemaDefaults.GetString(properties, "defaultTypeName", "Coordination Point");

            // Load defaults section
            var defaults = properties.ContainsKey("defaults") && properties["defaults"] is JObject defaultsObj
                ? defaultsObj
                : new JObject();

            var defaultToleranceGreen = GetDoubleFromSection(defaults, "toleranceGreen", 0.01);
            var defaultToleranceYellow = GetDoubleFromSection(defaults, "toleranceYellow", 0.05);
            var defaultComparisonMethod = GetStringFromSection(defaults, "comparisonMethod", "horizontal");
            var coordinatePrecision = Math.Max(0, Math.Min(6, GetIntFromSection(defaults, "coordinatePrecision", 3)));
            var visualizationTransparency = Math.Max(0, Math.Min(100, GetIntFromSection(defaults, "visualizationTransparency", 50)));
            var createDeviationArrows = GetBoolFromSection(defaults, "createDeviationArrows", true);
            var arrowScaleFactor = Math.Max(1.0, GetDoubleFromSection(defaults, "arrowScaleFactor", 10.0));

            // Load category-specific settings
            var categorySettings = new Dictionary<string, CategorySettings>();
            if (properties.ContainsKey("categories") && properties["categories"] is JObject categoriesObj)
            {
                foreach (var kvp in categoriesObj)
                {
                    var categoryName = kvp.Key;
                    if (kvp.Value is JObject categoryObj)
                    {
                        categorySettings[categoryName] = new CategorySettings(
                            GetDoubleFromSection(categoryObj, "toleranceGreen", defaultToleranceGreen),
                            GetDoubleFromSection(categoryObj, "toleranceYellow", defaultToleranceYellow),
                            GetStringFromSection(categoryObj, "comparisonMethod", defaultComparisonMethod)
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

        private static double GetDoubleFromSection(JObject obj, string key, double defaultValue)
        {
            if (obj.ContainsKey(key) && obj[key] is JValue jval && jval.Value != null)
            {
                if (double.TryParse(jval.Value.ToString(), out var result))
                    return result;
            }
            return defaultValue;
        }

        private static int GetIntFromSection(JObject obj, string key, int defaultValue)
        {
            if (obj.ContainsKey(key) && obj[key] is JValue jval && jval.Value != null)
            {
                if (int.TryParse(jval.Value.ToString(), out var result))
                    return result;
            }
            return defaultValue;
        }

        private static bool GetBoolFromSection(JObject obj, string key, bool defaultValue)
        {
            if (obj.ContainsKey(key) && obj[key] is JValue jval && jval.Value != null)
            {
                if (bool.TryParse(jval.Value.ToString(), out var result))
                    return result;
            }
            return defaultValue;
        }

        private static string GetStringFromSection(JObject obj, string key, string defaultValue)
        {
            if (obj.ContainsKey(key) && obj[key] is JValue jval && jval.Value != null)
            {
                return jval.Value.ToString();
            }
            return defaultValue;
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
