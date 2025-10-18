using System;

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

        public static FootingZoneConfig Load()
        {
            var properties = SchemaDefaults.LoadProperties("footing_zone.schema.json");

            var clearDepth = SchemaDefaults.GetDouble(properties, "clearDepth", 5.0);
            var slopeRatio = SchemaDefaults.GetDouble(properties, "slopeRatio", 1.0);
            var verticalOffset = SchemaDefaults.GetDouble(properties, "verticalOffset", 0.0);
            var transparency = Math.Max(0, Math.Min(100, SchemaDefaults.GetInt(properties, "transparency", 50)));
            var includeFootings = SchemaDefaults.GetBool(properties, "includeFootings", true);
            var promptForSlabs = SchemaDefaults.GetBool(properties, "promptForSlabs", false);

            return new FootingZoneConfig(
                clearDepth,
                slopeRatio,
                verticalOffset,
                transparency,
                includeFootings,
                promptForSlabs);
        }
    }
}
