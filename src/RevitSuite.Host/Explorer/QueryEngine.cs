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

        /// <summary>
        /// Resolves a stored stable key (or display name) back to a parameter on an element,
        /// falling through every avenue before giving up. This matters because the stable key
        /// is captured from SAMPLED elements: a display name like "Value" may map to a
        /// built-in parameter on one category and a shared/project parameter on another —
        /// the earlier version returned null on a key miss instead of trying the name, which
        /// silently produced zero query results ("first query shows nothing").
        /// </summary>
        public static Parameter? Resolve(Element element, string parameterKey, string? displayName)
        {
            // 1. The stable key's own route (built-in id / shared guid / definition name).
            try
            {
                if (parameterKey.StartsWith("bip:", StringComparison.Ordinal))
                {
                    var enumName = parameterKey.Substring(4);
#if NET8_0_OR_GREATER
                    if (Enum.TryParse<BuiltInParameter>(enumName, out var bip))
                    {
                        var byBip = element.get_Parameter(bip);
                        if (byBip != null)
                        {
                            return byBip;
                        }
                    }
#else
                    try
                    {
                        var bip = (BuiltInParameter)Enum.Parse(typeof(BuiltInParameter), enumName);
                        var byBip = element.get_Parameter(bip);
                        if (byBip != null)
                        {
                            return byBip;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
#endif
                }
                else if (parameterKey.StartsWith("guid:", StringComparison.Ordinal) &&
                         Guid.TryParse(parameterKey.Substring(5), out var guid))
                {
                    var byGuid = element.get_Parameter(guid);
                    if (byGuid != null)
                    {
                        return byGuid;
                    }
                }
                else
                {
                    var name = parameterKey.StartsWith("name:", StringComparison.Ordinal)
                        ? parameterKey.Substring(5)
                        : parameterKey;
                    var byName = element.LookupParameter(name);
                    if (byName != null)
                    {
                        return byName;
                    }
                }
            }
            catch
            {
                // Fall through to the name-based routes.
            }

            // 2. Exact display-name lookup (covers key-type mismatches across categories).
            try
            {
                if (displayName != null)
                {
                    var byDisplay = element.LookupParameter(displayName);
                    if (byDisplay != null)
                    {
                        return byDisplay;
                    }
                }
            }
            catch
            {
                // Fall through to the scan.
            }

            // 3. Case-insensitive scan (LookupParameter is case-sensitive; users type freely,
            // and same-named shared parameters can carry different guids per category).
            try
            {
                var target = displayName ?? (parameterKey.StartsWith("name:", StringComparison.Ordinal)
                    ? parameterKey.Substring(5)
                    : parameterKey.Contains(":") ? null : parameterKey);
                if (target != null)
                {
                    foreach (Parameter candidate in element.Parameters)
                    {
                        if (candidate?.Definition != null &&
                            string.Equals(candidate.Definition.Name, target, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
                // Unresolvable.
            }

            return null;
        }
    }

    /// <summary>
    /// Parses user-typed numeric condition values. Length suffixes convert to Revit internal
    /// feet: 3' / 3ft, 36" / 36in, 900mm, 90cm, 0.9m. A bare number is used as-is.
    /// </summary>
    internal static class UnitValueParser
    {
        public static bool TryParse(string? text, out double value) => TryParse(text, null, out value);

        /// <summary>
        /// Parses a user-typed numeric condition into Revit INTERNAL units. An explicit
        /// length suffix (3', 36in, 900mm…) converts via the suffix table. A bare number is
        /// interpreted in the PARAMETER'S OWN DISPLAY UNIT when one is available — a user
        /// filtering dimensions by "2032" means 2032 mm (whatever the project displays),
        /// not 2032 internal feet, which is why unitless comparisons used to never match.
        /// </summary>
        public static bool TryParse(string? text, Parameter? parameter, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text!.Trim().ToLowerInvariant();

            // Revit's native imperial format: feet-and-inches, e.g. 3'-6", 3' 6", 3'6 1/2".
            var apostrophe = trimmed.IndexOf('\'');
            if (apostrophe > 0 && apostrophe < trimmed.Length - 1)
            {
                var feetPart = trimmed.Substring(0, apostrophe).Trim();
                var inchPart = trimmed.Substring(apostrophe + 1).Trim().TrimStart('-').TrimEnd('"').Trim();
                if (inchPart.Length > 0 &&
                    TryParseNumber(feetPart, out var feet) &&
                    TryParseNumber(inchPart, out var inches))
                {
                    value = feet + inches / 12.0;
                    return true;
                }
            }

            foreach (var (suffix, toFeet) in Suffixes)
            {
                if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var numberPart = trimmed.Substring(0, trimmed.Length - suffix.Length).Trim();
                    if (TryParseNumber(numberPart, out var number))
                    {
                        value = number * toFeet;
                        return true;
                    }

                    return false;
                }
            }

            if (!TryParseNumber(trimmed, out var bare))
            {
                return false;
            }

            if (parameter != null)
            {
                try
                {
                    var unit = parameter.GetUnitTypeId();
                    if (unit != null && !string.IsNullOrEmpty(unit.TypeId))
                    {
                        value = UnitUtils.ConvertToInternalUnits(bare, unit);
                        return true;
                    }
                }
                catch
                {
                    // Non-measurable parameter (plain numbers, integers): use the value as-is.
                }
            }

            value = bare;
            return true;
        }

        /// <summary>Parses "36", "36.5", "1/2", and "6 1/2" (whole plus fraction).</summary>
        private static bool TryParseNumber(string text, out double value)
        {
            value = 0;
            text = text.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            var slash = text.IndexOf('/');
            if (slash > 0)
            {
                // Optional whole part before the fraction: "6 1/2".
                var whole = 0.0;
                var fractionPart = text;
                var space = text.LastIndexOf(' ', slash);
                if (space > 0)
                {
                    if (!double.TryParse(text.Substring(0, space).Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out whole))
                    {
                        return false;
                    }

                    fractionPart = text.Substring(space + 1).Trim();
                }

                var parts = fractionPart.Split('/');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
                    denominator != 0)
                {
                    value = whole + numerator / denominator;
                    return true;
                }

                return false;
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
                QueryOperator.Equals => EqualsValue(parameter, dto, condition.Value),
                QueryOperator.NotEquals => !EqualsValue(parameter, dto, condition.Value),
                QueryOperator.Contains => ContainsValue(dto.DisplayValue, condition.Value),
                QueryOperator.NotContains => !ContainsValue(dto.DisplayValue, condition.Value),
                QueryOperator.StartsWith => dto.DisplayValue != null && condition.Value != null &&
                    dto.DisplayValue.StartsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
                QueryOperator.EndsWith => dto.DisplayValue != null && condition.Value != null &&
                    dto.DisplayValue.EndsWith(condition.Value, StringComparison.OrdinalIgnoreCase),
                QueryOperator.Regex => MatchesRegex(dto.DisplayValue, condition.Value),
                QueryOperator.GreaterThan => CompareNumeric(parameter, dto, condition.Value) is > 0,
                QueryOperator.GreaterThanOrEqual => CompareNumeric(parameter, dto, condition.Value) is >= 0,
                QueryOperator.LessThan => CompareNumeric(parameter, dto, condition.Value) is < 0,
                QueryOperator.LessThanOrEqual => CompareNumeric(parameter, dto, condition.Value) is <= 0,
                QueryOperator.Between => CompareNumeric(parameter, dto, condition.Value) is >= 0 &&
                                         CompareNumeric(parameter, dto, condition.Value2) is <= 0,
                _ => false
            };
        }

        private static bool EqualsValue(Parameter parameter, ParameterValueDto dto, string? conditionValue)
        {
            if (dto.NumericValue.HasValue && UnitValueParser.TryParse(conditionValue, parameter, out var number))
            {
                // Relative tolerance: a dimension displayed "2032" may actually be
                // 2031.9999…; exact double equality would wrongly reject it.
                var tolerance = Math.Max(1e-9, Math.Abs(number) * 1e-6);
                if (Math.Abs(dto.NumericValue.Value - number) <= tolerance)
                {
                    return true;
                }
            }

            // Display-string match covers display rounding and formatted values outright.
            return string.Equals(dto.DisplayValue?.Trim() ?? string.Empty,
                conditionValue?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesRegex(string? displayValue, string? pattern)
        {
            if (displayValue == null || string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    displayValue, pattern!,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                // Invalid pattern or timeout: treat as no match rather than failing the query.
                return false;
            }
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
        private static int? CompareNumeric(Parameter parameter, ParameterValueDto dto, string? conditionValue)
        {
            if (!dto.NumericValue.HasValue || !UnitValueParser.TryParse(conditionValue, parameter, out var number))
            {
                return null;
            }

            var difference = dto.NumericValue.Value - number;
            var tolerance = Math.Max(1e-9, Math.Abs(number) * 1e-6);
            if (Math.Abs(difference) <= tolerance)
            {
                return 0;
            }

            return difference > 0 ? 1 : -1;
        }
    }

    /// <summary>A query hit: the element plus the evaluated condition-parameter display values.</summary>
    public sealed record QueryMatch(ElementRecord Record, IReadOnlyDictionary<string, string> ConditionValues);

    /// <summary>Runs a QueryDefinition against the model. API context required.</summary>
    internal static class QueryRunner
    {
        private static readonly IReadOnlyDictionary<string, string> NoValues =
            new Dictionary<string, string>();

        public static IReadOnlyList<ElementRecord> Run(UIDocument uidoc, QueryDefinition query) =>
            RunDetailed(uidoc, query).Select(m => m.Record).ToList();

        /// <summary>
        /// Like <see cref="Run"/> but keeps each match's condition-parameter display values,
        /// so results can show WHAT matched (e.g. the actual Value of every dimension found).
        /// </summary>
        public static IReadOnlyList<QueryMatch> RunDetailed(UIDocument uidoc, QueryDefinition query)
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
                        return Array.Empty<QueryMatch>();
                    }

                    collector = new FilteredElementCollector(doc, selection);
                    break;
                default:
                    collector = new FilteredElementCollector(doc);
                    break;
            }

            var results = new List<QueryMatch>();
            Evaluate(collector, doc, query, new ElementCollectionService.RecordContext(doc, "Host", isLinked: false), results);

            // Linked documents are always queried whole-model (view/selection scopes are host concepts).
            if (query.IncludeLinkedDocuments)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var instance in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance))
                             .Cast<RevitLinkInstance>())
                {
                    var linkDoc = instance.GetLinkDocument();
                    if (linkDoc == null)
                    {
                        continue;
                    }

                    var key = string.IsNullOrWhiteSpace(linkDoc.PathName) ? linkDoc.Title : linkDoc.PathName;
                    if (!visited.Add(key))
                    {
                        continue;
                    }

                    Evaluate(
                        new FilteredElementCollector(linkDoc),
                        linkDoc,
                        query,
                        new ElementCollectionService.RecordContext(linkDoc, linkDoc.Title, isLinked: true, instance.Id.Value),
                        results);
                }
            }

            return results;
        }

        private static void Evaluate(
            FilteredElementCollector collector,
            Document doc,
            QueryDefinition query,
            ElementCollectionService.RecordContext context,
            List<QueryMatch> results)
        {
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

            foreach (var element in collector)
            {
                if (element?.Category == null)
                {
                    continue;
                }

                if (QueryEvaluator.Matches(element, query))
                {
                    results.Add(new QueryMatch(
                        ElementCollectionService.CreateRecord(element, context),
                        ExtractConditionValues(element, query)));
                }
            }
        }

        /// <summary>Display values of every condition parameter, extracted only for matches.</summary>
        private static IReadOnlyDictionary<string, string> ExtractConditionValues(Element element, QueryDefinition query)
        {
            if (query.Conditions.Count == 0)
            {
                return NoValues;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var condition in query.Conditions)
            {
                var name = condition.ParameterDisplayName ?? condition.ParameterKey;
                if (values.ContainsKey(name))
                {
                    continue;
                }

                var parameter = ParameterExtractor.Resolve(element, condition.ParameterKey, condition.ParameterDisplayName);
                values[name] = parameter == null
                    ? string.Empty
                    : ParameterExtractor.ToDto(parameter).DisplayValue ?? string.Empty;
            }

            return values;
        }

        /// <summary>Distinct parameter names/keys available on elements of the given categories (sampled).</summary>
        public static IReadOnlyList<ParameterValueDto> DiscoverParameters(
            UIDocument uidoc,
            IReadOnlyList<string> categories,
            int samplePerCategory = 100)
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
