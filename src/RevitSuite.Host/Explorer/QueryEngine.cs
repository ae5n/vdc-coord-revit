using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitSuite.Host.Explorer
{
    /// <summary>Extracts immutable parameter DTOs from live elements. API context required.</summary>
    internal static class ParameterExtractor
    {
        public static IReadOnlyList<ParameterValueDto> ExtractAll(Element element)
        {
            var values = new List<ParameterValueDto>();

            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter?.Definition == null)
                {
                    continue;
                }

                values.Add(ToDto(parameter));
            }

            return values
                .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static ParameterValueDto ToDto(Parameter p)
        {
            var displayName = p.Definition.Name;
            var stableKey = CreateStableKey(p);

            return p.StorageType switch
            {
                StorageType.String => new ParameterValueDto(
                    displayName, stableKey, ParameterStorageKind.String,
                    null, p.AsString(), p.IsReadOnly),

                StorageType.Integer => new ParameterValueDto(
                    displayName, stableKey, ParameterStorageKind.Integer,
                    p.AsInteger(),
                    p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture),
                    p.IsReadOnly),

                StorageType.Double => new ParameterValueDto(
                    displayName, stableKey, ParameterStorageKind.Double,
                    p.AsDouble(),
                    p.AsValueString() ?? p.AsDouble().ToString("0.####", CultureInfo.InvariantCulture),
                    p.IsReadOnly),

                StorageType.ElementId => new ParameterValueDto(
                    displayName, stableKey, ParameterStorageKind.ElementId,
                    p.AsElementId().Value,
                    p.AsValueString(),
                    p.IsReadOnly),

                _ => new ParameterValueDto(
                    displayName, stableKey, ParameterStorageKind.None,
                    null, null, p.IsReadOnly)
            };
        }

        /// <summary>
        /// Stable key preference: built-in parameter enum name, then shared-parameter GUID,
        /// then definition name. Never only a localized display name for built-ins/shared.
        /// </summary>
        public static string CreateStableKey(Parameter p)
        {
            try
            {
                if (p.Definition is InternalDefinition internalDefinition &&
                    internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
                {
                    return "bip:" + internalDefinition.BuiltInParameter;
                }
            }
            catch
            {
                // Fall through to other key forms.
            }

            try
            {
                if (p.IsShared)
                {
                    return "guid:" + p.GUID.ToString("D");
                }
            }
            catch
            {
                // Fall through.
            }

            return "name:" + p.Definition.Name;
        }

        /// <summary>Resolves a stored stable key (or display name) back to a parameter on an element.</summary>
        public static Parameter? Resolve(Element element, string parameterKey, string? displayName)
        {
            try
            {
                if (parameterKey.StartsWith("bip:", StringComparison.Ordinal))
                {
                    var enumName = parameterKey.Substring(4);
#if NET8_0_OR_GREATER
                    if (Enum.TryParse<BuiltInParameter>(enumName, out var bip))
                    {
                        return element.get_Parameter(bip);
                    }
#else
                    try
                    {
                        var bip = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), enumName);
                        return element.get_Parameter(bip);
                    }
                    catch (ArgumentException)
                    {
                    }
#endif
                    return null;
                }

                if (parameterKey.StartsWith("guid:", StringComparison.Ordinal) &&
                    Guid.TryParse(parameterKey.Substring(5), out var guid))
                {
                    return element.get_Parameter(guid);
                }

                var name = parameterKey.StartsWith("name:", StringComparison.Ordinal)
                    ? parameterKey.Substring(5)
                    : parameterKey;

                return element.LookupParameter(name)
                       ?? (displayName != null ? element.LookupParameter(displayName) : null);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Parses user-typed numeric condition values. Length suffixes convert to Revit internal
    /// feet: 3' / 3ft, 36" / 36in, 900mm, 90cm, 0.9m. A bare number is used as-is.
    /// </summary>
    internal static class UnitValueParser
    {
        public static bool TryParse(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text!.Trim().ToLowerInvariant();

            foreach (var (suffix, toFeet) in Suffixes)
            {
                if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var numberPart = trimmed.Substring(0, trimmed.Length - suffix.Length).Trim();
                    if (double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        value = number * toFeet;
                        return true;
                    }

                    return false;
                }
            }

            return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static readonly (string Suffix, double ToFeet)[] Suffixes =
        {
            ("mm", 1.0 / 304.8),
            ("cm", 1.0 / 30.48),
            ("ft", 1.0),
            ("in", 1.0 / 12.0),
            ("m", 1.0 / 0.3048),
            ("\"", 1.0 / 12.0),
            ("'", 1.0)
        };
    }

    /// <summary>Evaluates query conditions against a live element. API context required.</summary>
    internal static class QueryEvaluator
    {
        public static bool Matches(Element element, QueryDefinition query)
        {
            if (query.Conditions.Count == 0)
            {
                return true;
            }

            return query.Operator == LogicalOperator.And
                ? query.Conditions.All(c => MatchesCondition(element, c))
                : query.Conditions.Any(c => MatchesCondition(element, c));
        }

        public static bool MatchesCondition(Element element, QueryCondition condition)
        {
            var parameter = ParameterExtractor.Resolve(element, condition.ParameterKey, condition.ParameterDisplayName);

            if (parameter == null)
            {
                return condition.Operator == QueryOperator.MissingParameter;
            }

            if (condition.Operator == QueryOperator.MissingParameter)
            {
                return false;
            }

            if (condition.Operator == QueryOperator.HasParameter)
            {
                return true;
            }

            var dto = ParameterExtractor.ToDto(parameter);

            return condition.Operator switch
            {
                QueryOperator.IsEmpty => string.IsNullOrWhiteSpace(dto.DisplayValue),
                QueryOperator.IsNotEmpty => !string.IsNullOrWhiteSpace(dto.DisplayValue),
                QueryOperator.Equals => EqualsValue(dto, condition.Value),
                QueryOperator.NotEquals => !EqualsValue(dto, condition.Value),
                QueryOperator.Contains => ContainsValue(dto.DisplayValue, condition.Value),
                QueryOperator.NotContains => !ContainsValue(dto.DisplayValue, condition.Value),
                QueryOperator.StartsWith => dto.DisplayValue != null && condition.Value != null &&
                    dto.DisplayValue.StartsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
                QueryOperator.EndsWith => dto.DisplayValue != null && condition.Value != null &&
                    dto.DisplayValue.EndsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
                QueryOperator.GreaterThan => CompareNumeric(dto, condition.Value) is > 0,
                QueryOperator.GreaterThanOrEqual => CompareNumeric(dto, condition.Value) is >= 0,
                QueryOperator.LessThan => CompareNumeric(dto, condition.Value) is < 0,
                QueryOperator.LessThanOrEqual => CompareNumeric(dto, condition.Value) is <= 0,
                QueryOperator.Between => CompareNumeric(dto, condition.Value) is >= 0 &&
                                         CompareNumeric(dto, condition.Value2) is <= 0,
                _ => false
            };
        }

        private static bool EqualsValue(ParameterValueDto dto, string? conditionValue)
        {
            if (dto.NumericValue.HasValue && UnitValueParser.TryParse(conditionValue, out var number))
            {
                return Math.Abs(dto.NumericValue.Value - number) < 1e-9;
            }

            return string.Equals(dto.DisplayValue ?? string.Empty, conditionValue ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsValue(string? displayValue, string? conditionValue)
        {
            if (displayValue == null || string.IsNullOrEmpty(conditionValue))
            {
                return false;
            }

            return displayValue.IndexOf(conditionValue, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Returns sign of (parameter - conditionValue); null when the pair is not numerically comparable.</summary>
        private static int? CompareNumeric(ParameterValueDto dto, string? conditionValue)
        {
            if (!dto.NumericValue.HasValue || !UnitValueParser.TryParse(conditionValue, out var number))
            {
                return null;
            }

            var difference = dto.NumericValue.Value - number;
            if (Math.Abs(difference) < 1e-9)
            {
                return 0;
            }

            return difference > 0 ? 1 : -1;
        }
    }

    /// <summary>Runs a QueryDefinition against the model. API context required.</summary>
    internal static class QueryRunner
    {
        public static IReadOnlyList<ElementRecord> Run(UIDocument uidoc, QueryDefinition query)
        {
            var doc = uidoc.Document;
            FilteredElementCollector collector;
            switch (query.Scope)
            {
                case ExplorerScope.ActiveView:
                    collector = new FilteredElementCollector(doc, uidoc.ActiveView.Id);
                    break;
                case ExplorerScope.CurrentSelection:
                    var selection = uidoc.Selection.GetElementIds();
                    if (selection.Count == 0)
                    {
                        return Array.Empty<ElementRecord>();
                    }

                    collector = new FilteredElementCollector(doc, selection);
                    break;
                default:
                    collector = new FilteredElementCollector(doc);
                    break;
            }

            var categoryIds = ResolveCategoryIds(doc, query.Categories);
            if (categoryIds.Count > 0)
            {
                collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds));
            }

            if (!query.IncludeElementTypes)
            {
                collector = collector.WhereElementIsNotElementType();
            }
            else if (categoryIds.Count == 0)
            {
                // A collector cannot be enumerated with no filter at all; this always-true
                // filter covers "all categories + element types included".
                collector = collector.WherePasses(new LogicalOrFilter(
                    new ElementIsElementTypeFilter(false),
                    new ElementIsElementTypeFilter(true)));
            }

            var context = new ElementCollectionService.RecordContext(doc, "Host", isLinked: false);
            var results = new List<ElementRecord>();

            foreach (var element in collector)
            {
                if (element?.Category == null)
                {
                    continue;
                }

                if (QueryEvaluator.Matches(element, query))
                {
                    results.Add(ElementCollectionService.CreateRecord(element, context));
                }
            }

            return results;
        }

        /// <summary>Distinct parameter names/keys available on elements of the given categories (sampled).</summary>
        public static IReadOnlyList<ParameterValueDto> DiscoverParameters(
            UIDocument uidoc,
            IReadOnlyList<string> categories,
            int samplePerCategory = 25)
        {
            var doc = uidoc.Document;
            var categoryIds = ResolveCategoryIds(doc, categories);

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (categoryIds.Count > 0)
            {
                collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds));
            }

            var seen = new Dictionary<string, ParameterValueDto>(StringComparer.Ordinal);
            var sampled = 0;
            var sampleBudget = Math.Max(samplePerCategory, samplePerCategory * Math.Max(1, categories.Count));

            foreach (var element in collector)
            {
                if (element?.Category == null)
                {
                    continue;
                }

                foreach (var dto in ParameterExtractor.ExtractAll(element))
                {
                    if (!seen.ContainsKey(dto.StableKey))
                    {
                        seen[dto.StableKey] = dto;
                    }
                }

                if (++sampled >= sampleBudget)
                {
                    break;
                }
            }

            return seen.Values
                .OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> ListModelCategories(Document doc)
        {
            var names = new List<string>();
            foreach (Category category in doc.Settings.Categories)
            {
                if (category is { CategoryType: CategoryType.Model or CategoryType.Annotation })
                {
                    names.Add(category.Name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static List<ElementId> ResolveCategoryIds(Document doc, IReadOnlyList<string> categoryNames)
        {
            var ids = new List<ElementId>();
            if (categoryNames.Count == 0)
            {
                return ids;
            }

            var wanted = new HashSet<string>(categoryNames, StringComparer.OrdinalIgnoreCase);
            foreach (Category category in doc.Settings.Categories)
            {
                if (wanted.Contains(category.Name))
                {
                    ids.Add(category.Id);
                }
            }

            return ids;
        }
    }
}
