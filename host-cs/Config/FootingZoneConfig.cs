using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitSuite.Host.Config
{
    internal class FootingZoneConfig
    {
        public double ClearDepth { get; }
        public double SlopeRatio { get; }
        public double VerticalOffset { get; }
        public int Transparency { get; }
        public bool IncludeFootings { get; }
        public bool PromptForSlabs { get; }

        private FootingZoneConfig(
            double clearDepth,
            double slopeRatio,
            double verticalOffset,
            int transparency,
            bool includeFootings,
            bool promptForSlabs)
        {
            ClearDepth = clearDepth;
            SlopeRatio = slopeRatio;
            VerticalOffset = verticalOffset;
            Transparency = transparency;
            IncludeFootings = includeFootings;
            PromptForSlabs = promptForSlabs;
        }

        public static FootingZoneConfig LoadFromSchema(string schemaPath)
        {
            if (!File.Exists(schemaPath))
            {
                return Default();
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(schemaPath));
                var properties = json["properties"] as JObject ?? new JObject();

                double clearDepth = GetDouble(properties, "clearDepth", 5.0);
                double slopeRatio = GetDouble(properties, "slopeRatio", 1.0);
                double verticalOffset = GetDouble(properties, "verticalOffset", 0.0);
                int transparency = (int)Math.Round(GetDouble(properties, "transparency", 50.0));
                bool includeFootings = GetBoolean(properties, "includeFootings", true);
                bool promptForSlabs = GetBoolean(properties, "promptForSlabs", false);

                transparency = Math.Max(0, Math.Min(100, transparency));

                return new FootingZoneConfig(
                    clearDepth,
                    slopeRatio,
                    verticalOffset,
                    transparency,
                    includeFootings,
                    promptForSlabs);
            }
            catch
            {
                return Default();
            }
        }

        private static double GetDouble(JObject properties, string name, double fallback)
        {
            if (properties.TryGetValue(name, out var token))
            {
                var @default = token?["default"];
                if (@default != null && double.TryParse(@default.ToString(), out var value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private static bool GetBoolean(JObject properties, string name, bool fallback)
        {
            if (properties.TryGetValue(name, out var token))
            {
                var @default = token?["default"];
                if (@default != null && bool.TryParse(@default.ToString(), out var value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private static FootingZoneConfig Default() => new FootingZoneConfig(5.0, 1.0, 0.0, 50, true, false);
    }
}
