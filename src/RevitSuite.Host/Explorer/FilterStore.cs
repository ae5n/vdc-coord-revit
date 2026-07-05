using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RevitSuite.Host.Explorer
{
    /// <summary>JSON persistence for saved filters (QueryDefinition) under %AppData%\RevitSuite\Explorer\filters.</summary>
    internal static class FilterStore
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() }
        };

        public sealed record LoadedFilter(QueryDefinition? Query, string FilePath, string? Error, string Source = "User");

        public static IReadOnlyList<LoadedFilter> LoadAll()
        {
            var company = new List<LoadedFilter>();
            var user = new List<LoadedFilter>();

            // Company standards (ProgramData, deployed by IT) are read-only from the UI.
            // A user filter with the same id shadows the company one.
            var companyDirectory = ExplorerPaths.CompanyFiltersDirectory;
            if (companyDirectory != null)
            {
                try
                {
                    foreach (var path in Directory.GetFiles(companyDirectory, "*.json")
                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        company.Add(LoadFrom(path) with { Source = "Company" });
                    }
                }
                catch (Exception ex)
                {
                    // An unreadable company share is a warning entry, never a crash.
                    company.Add(new LoadedFilter(null, companyDirectory,
                        $"Could not read company filters: {ex.Message}", "Company"));
                }
            }

            foreach (var path in Directory.GetFiles(ExplorerPaths.FiltersDirectory, "*.json")
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                user.Add(LoadFrom(path));
            }

            var userIds = new HashSet<string>(
                user.Where(f => f.Query != null).Select(f => f.Query!.Id),
                StringComparer.OrdinalIgnoreCase);

            return company
                .Where(f => f.Query == null || !userIds.Contains(f.Query.Id))
                .Concat(user)
                .ToList();
        }

        public static LoadedFilter LoadFrom(string path)
        {
            try
            {
                var query = JsonConvert.DeserializeObject<QueryDefinition>(File.ReadAllText(path), Settings);
                var error = query == null ? "File does not contain a filter definition." : Validate(query);
                return new LoadedFilter(error == null ? query : null, path, error);
            }
            catch (Exception ex)
            {
                return new LoadedFilter(null, path, $"Invalid filter JSON: {ex.Message}");
            }
        }

        public static string Save(QueryDefinition query)
        {
            var error = Validate(query);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }

            var path = Path.Combine(ExplorerPaths.FiltersDirectory, SanitizeFileName(query.Id) + ".json");

            // Overwriting the same filter (same name) is an update; a different name that
            // normalizes to the same id would silently destroy someone else's filter.
            if (File.Exists(path))
            {
                var existing = LoadFrom(path).Query;
                if (existing != null && !string.Equals(existing.Name, query.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"A filter with id '{query.Id}' already exists (named '{existing.Name}'). Choose a different name.");
                }
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(query, Settings));
            return path;
        }

        public static void Delete(QueryDefinition query)
        {
            var path = Path.Combine(ExplorerPaths.FiltersDirectory, SanitizeFileName(query.Id) + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <summary>Copies an external filter file into the store after validating it.</summary>
        public static LoadedFilter Import(string sourcePath)
        {
            var loaded = LoadFrom(sourcePath);
            if (loaded.Query == null)
            {
                return loaded;
            }

            var savedPath = Save(loaded.Query);
            return loaded with { FilePath = savedPath };
        }

        public static void ExportTo(QueryDefinition query, string destinationPath)
        {
            File.WriteAllText(destinationPath, JsonConvert.SerializeObject(query, Settings));
        }

        public static string? Validate(QueryDefinition query)
        {
            if (string.IsNullOrWhiteSpace(query.Id))
            {
                return "Filter id is required.";
            }

            if (string.IsNullOrWhiteSpace(query.Name))
            {
                return "Filter name is required.";
            }

            if (query.Conditions == null || query.Categories == null)
            {
                return "Filter must define categories and conditions collections (they may be empty).";
            }

            foreach (var condition in query.Conditions)
            {
                if (string.IsNullOrWhiteSpace(condition.ParameterKey))
                {
                    return "Every condition needs a parameter.";
                }

                var needsValue = condition.Operator is not (QueryOperator.IsEmpty or QueryOperator.IsNotEmpty
                    or QueryOperator.HasParameter or QueryOperator.MissingParameter);
                if (needsValue && string.IsNullOrWhiteSpace(condition.Value))
                {
                    return $"Condition on '{condition.ParameterDisplayName ?? condition.ParameterKey}' needs a value.";
                }

                if (condition.Operator == QueryOperator.Between && string.IsNullOrWhiteSpace(condition.Value2))
                {
                    return $"Between condition on '{condition.ParameterDisplayName ?? condition.ParameterKey}' needs a second value.";
                }
            }

            return null;
        }

        /// <summary>Plain-language explanation of a filter, shown in the UI.</summary>
        public static string Explain(QueryDefinition query)
        {
            var categories = query.Categories.Count == 0
                ? "all categories"
                : string.Join(", ", query.Categories);

            if (query.Conditions.Count == 0)
            {
                return $"Find all elements in {categories}.";
            }

            var joiner = query.Operator == LogicalOperator.And ? " and " : " or ";
            var clauses = query.Conditions.Select(ExplainCondition);
            return $"Find elements in {categories} where {string.Join(joiner, clauses)}.";
        }

        private static string ExplainCondition(QueryCondition condition)
        {
            var name = condition.ParameterDisplayName ?? condition.ParameterKey;
            return condition.Operator switch
            {
                QueryOperator.Equals => $"{name} equals '{condition.Value}'",
                QueryOperator.NotEquals => $"{name} does not equal '{condition.Value}'",
                QueryOperator.Contains => $"{name} contains '{condition.Value}'",
                QueryOperator.NotContains => $"{name} does not contain '{condition.Value}'",
                QueryOperator.StartsWith => $"{name} starts with '{condition.Value}'",
                QueryOperator.EndsWith => $"{name} ends with '{condition.Value}'",
                QueryOperator.Regex => $"{name} matches pattern '{condition.Value}'",
                QueryOperator.IsEmpty => $"{name} is empty",
                QueryOperator.IsNotEmpty => $"{name} is not empty",
                QueryOperator.GreaterThan => $"{name} is greater than {condition.Value}",
                QueryOperator.GreaterThanOrEqual => $"{name} is at least {condition.Value}",
                QueryOperator.LessThan => $"{name} is less than {condition.Value}",
                QueryOperator.LessThanOrEqual => $"{name} is at most {condition.Value}",
                QueryOperator.Between => $"{name} is between {condition.Value} and {condition.Value2}",
                QueryOperator.HasParameter => $"{name} exists",
                QueryOperator.MissingParameter => $"{name} is missing",
                _ => name
            };
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        }
    }
}
