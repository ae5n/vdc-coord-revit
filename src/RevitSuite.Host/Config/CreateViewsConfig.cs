using System;

namespace RevitSuite.Host.Config
{
    internal class CreateViewsConfig
    {
        public string LevelName { get; }
        public string ViewType { get; }
        public int Scale { get; }

        private CreateViewsConfig(string levelName, string viewType, int scale)
        {
            LevelName = levelName;
            ViewType = viewType;
            Scale = scale;
        }

        public static CreateViewsConfig Load()
        {
            var properties = SchemaDefaults.LoadProperties("create_views.schema.json");

            var levelName = SchemaDefaults.GetString(properties, "levelName", "LEVEL 01");
            var viewType = SchemaDefaults.GetString(properties, "viewType", "FloorPlan");
            var scale = Math.Max(1, SchemaDefaults.GetInt(properties, "scale", 96));

            return new CreateViewsConfig(levelName, viewType, scale);
        }
    }
}
