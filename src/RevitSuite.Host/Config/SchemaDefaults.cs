using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RevitSuite.Host.Config
{
    internal static class SchemaDefaults
    {
        private static readonly Lazy<IReadOnlyList<string>> SchemaDirectories = new Lazy<IReadOnlyList<string>>(() =>
        {
            var candidates = new List<string>();
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var baseDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrEmpty(baseDir))
            {
                candidates.Add(Path.Combine(baseDir, "schemas"));
            }

            var appBase = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(appBase))
            {
                candidates.Add(Path.Combine(appBase, "schemas"));
            }

            var envBase = Environment.GetEnvironmentVariable("REVIT_SUITE_SCHEMA_DIR");
            if (!string.IsNullOrWhiteSpace(envBase))
            {
                candidates.Insert(0, envBase.Trim());
            }

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        public static JObject LoadSchema(string schemaFileName)
        {
            var schemaPath = ResolveSchemaPath(schemaFileName);

            try
            {
                var json = JObject.Parse(File.ReadAllText(schemaPath));
                return json;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to parse schema '{schemaFileName}' at '{schemaPath}'.", ex);
            }
        }

        public static JObject LoadProperties(string schemaFileName)
        {
            var schema = LoadSchema(schemaFileName);
            if (!(schema["properties"] is JObject properties))
            {
                throw new InvalidDataException($"Schema '{schemaFileName}' does not contain a valid top-level 'properties' object.");
            }

            return properties;
        }

        public static string GetString(JObject properties, string name, string fallback)
        {
            var token = GetDefaultToken(properties, name, allowConst: true);
            var value = token.Type == JTokenType.String
                ? token.Value<string>()
                : token is JValue jValue && jValue.Value != null
                    ? Convert.ToString(jValue.Value, CultureInfo.InvariantCulture)
                    : token.ToString();

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"Schema property '{name}' has an empty default value.");
            }

            return value.Trim();
        }

        public static double GetDouble(JObject properties, string name, double fallback)
        {
            var token = GetDefaultToken(properties, name, allowConst: false);
            if (double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            throw new InvalidDataException($"Schema property '{name}' has non-numeric default '{token}'.");
        }

        public static int GetInt(JObject properties, string name, int fallback)
        {
            var token = GetDefaultToken(properties, name, allowConst: false);
            if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            throw new InvalidDataException($"Schema property '{name}' has non-integer default '{token}'.");
        }

        public static bool GetBool(JObject properties, string name, bool fallback)
        {
            var token = GetDefaultToken(properties, name, allowConst: false);
            if (bool.TryParse(token.ToString(), out var value))
            {
                return value;
            }

            throw new InvalidDataException($"Schema property '{name}' has non-boolean default '{token}'.");
        }

        private static string ResolveSchemaPath(string schemaFileName)
        {
            var attempts = new List<string>();
            foreach (var directory in SchemaDirectories.Value)
            {
                var candidatePath = Path.Combine(directory, schemaFileName);
                attempts.Add(candidatePath);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            throw new FileNotFoundException(
                $"Schema file '{schemaFileName}' not found. Checked: {string.Join("; ", attempts)}");
        }

        private static JToken GetDefaultToken(JObject properties, string name, bool allowConst)
        {
            if (properties == null)
            {
                throw new ArgumentNullException(nameof(properties));
            }

            if (!properties.TryGetValue(name, out var propertyToken) || propertyToken == null)
            {
                throw new InvalidDataException($"Schema property '{name}' not found.");
            }

            if (!(propertyToken is JObject propertyObject))
            {
                throw new InvalidDataException($"Schema property '{name}' is not an object.");
            }

            var token = propertyObject["default"] ?? (allowConst ? propertyObject["const"] : null);
            if (token != null)
            {
                return token;
            }

            throw new InvalidDataException($"Schema property '{name}' does not define a default value.");
        }
    }
}
