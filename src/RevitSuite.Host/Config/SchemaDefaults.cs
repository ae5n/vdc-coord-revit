using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RevitSuite.Host.Config
{
    internal static class SchemaDefaults
    {
        private static readonly Lazy<string> SchemaDirectory = new Lazy<string>(() =>
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var baseDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrEmpty(baseDir))
            {
                return Path.Combine(baseDir, "schemas");
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schemas");
        });

        public static JObject LoadProperties(string schemaFileName)
        {
            try
            {
                var schemaPath = Path.Combine(SchemaDirectory.Value, schemaFileName);
                if (!File.Exists(schemaPath))
                {
                    return new JObject();
                }

                var json = JObject.Parse(File.ReadAllText(schemaPath));
                return json["properties"] as JObject ?? new JObject();
            }
            catch
            {
                return new JObject();
            }
        }

        public static string GetString(JObject properties, string name, string fallback)
        {
            var token = properties[name]? ["default"] ?? properties[name]? ["const"];
            var value = token?.ToString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        public static double GetDouble(JObject properties, string name, double fallback)
        {
            var token = properties[name]? ["default"];
            if (token != null && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return fallback;
        }

        public static int GetInt(JObject properties, string name, int fallback)
        {
            var token = properties[name]? ["default"];
            if (token != null && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return fallback;
        }

        public static bool GetBool(JObject properties, string name, bool fallback)
        {
            var token = properties[name]? ["default"];
            if (token != null && bool.TryParse(token.ToString(), out var value))
            {
                return value;
            }

            return fallback;
        }
    }
}
