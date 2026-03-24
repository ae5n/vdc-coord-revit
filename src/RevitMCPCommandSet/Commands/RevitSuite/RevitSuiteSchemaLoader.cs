using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Reflection;

namespace RevitMCPCommandSet.Commands.RevitSuite
{
    /// <summary>
    /// Loads RevitSuite JSON schema files from the schemas/ folder adjacent to the payload DLL.
    /// RevitMCPCommandSet.dll lives in RevitSuite/Commands/ so schemas are one level up.
    /// </summary>
    internal static class RevitSuiteSchemaLoader
    {
        public static JObject LoadProperties(string schemaFileName)
        {
            var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                         ?? AppDomain.CurrentDomain.BaseDirectory;

            // Commands/ → parent = RevitSuite/ → schemas/
            var schemasDir = Path.Combine(dllDir, "..", "schemas");
            var schemaPath = Path.GetFullPath(Path.Combine(schemasDir, schemaFileName));

            if (!File.Exists(schemaPath))
                throw new FileNotFoundException($"RevitSuite schema not found at '{schemaPath}'.");

            var root = JObject.Parse(File.ReadAllText(schemaPath));

            return root["properties"] as JObject
                   ?? throw new InvalidDataException($"Schema '{schemaFileName}' has no 'properties' object.");
        }

        public static string GetString(JObject props, string name, string fallback)
        {
            if (props[name] is JObject p && p["default"] != null)
                return p["default"].Value<string>() ?? fallback;
            return fallback;
        }

        public static double GetDouble(JObject props, string name, double fallback)
        {
            if (props[name] is JObject p && p["default"] != null)
                return p["default"].Value<double>();
            return fallback;
        }

        public static int GetInt(JObject props, string name, int fallback)
        {
            if (props[name] is JObject p && p["default"] != null)
                return p["default"].Value<int>();
            return fallback;
        }

        public static bool GetBool(JObject props, string name, bool fallback)
        {
            if (props[name] is JObject p && p["default"] != null)
                return p["default"].Value<bool>();
            return fallback;
        }
    }
}
