using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// CSV / XLSX / JSON exports. XLSX is written directly as minimal OpenXML
    /// (zip + inline-string worksheets) to avoid Excel COM and heavy dependencies.
    /// </summary>
    internal static class ExportService
    {
        public const int ExportSchemaVersion = 1;

        public sealed record Table(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

        // ---------- CSV ----------

        public static void WriteCsv(string path, Table table)
        {
            EnsureDirectory(path);
            var builder = new StringBuilder();
            AppendCsvLine(builder, table.Headers);
            foreach (var row in table.Rows)
            {
                AppendCsvLine(builder, row);
            }

            // UTF-8 with BOM so Excel opens it correctly.
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static void AppendCsvLine(StringBuilder builder, IReadOnlyList<string> values)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(EscapeCsv(values[i]));
            }

            builder.AppendLine();
        }

        private static string EscapeCsv(string? value)
        {
            value ??= string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        // ---------- XLSX ----------

        public static void WriteXlsx(string path, IReadOnlyList<Table> tables)
        {
            if (tables.Count == 0)
            {
                throw new ArgumentException("At least one sheet is required.", nameof(tables));
            }

            EnsureDirectory(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            var sheetNames = MakeUniqueSheetNames(tables.Select(t => t.Name).ToList());

            WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(tables.Count));
            WriteEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            WriteEntry(archive, "xl/workbook.xml", BuildWorkbook(sheetNames));
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(tables.Count));
            WriteEntry(archive, "xl/styles.xml", MinimalStyles);

            for (var i = 0; i < tables.Count; i++)
            {
                WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", BuildWorksheet(tables[i]));
            }
        }

        private static string BuildContentTypes(int sheetCount)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            builder.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            builder.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            builder.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            builder.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (var i = 1; i <= sheetCount; i++)
            {
                builder.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            }

            builder.Append("</Types>");
            return builder.ToString();
        }

        private static string BuildWorkbook(IReadOnlyList<string> sheetNames)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                           "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (var i = 0; i < sheetNames.Count; i++)
            {
                builder.Append($"<sheet name=\"{XmlEscape(sheetNames[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            }

            builder.Append("</sheets></workbook>");
            return builder.ToString();
        }

        private static string BuildWorkbookRels(int sheetCount)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 1; i <= sheetCount; i++)
            {
                builder.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            }

            builder.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            builder.Append("</Relationships>");
            return builder.ToString();
        }

        private const string MinimalStyles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf/><xf fontId=\"1\" applyFont=\"1\"/></cellXfs>" +
            "</styleSheet>";

        private static string BuildWorksheet(Table table)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            AppendXlsxRow(builder, table.Headers, headerStyle: true);
            foreach (var row in table.Rows)
            {
                AppendXlsxRow(builder, row, headerStyle: false);
            }

            builder.Append("</sheetData></worksheet>");
            return builder.ToString();
        }

        private static void AppendXlsxRow(StringBuilder builder, IReadOnlyList<string> values, bool headerStyle)
        {
            builder.Append("<row>");
            foreach (var value in values)
            {
                var text = value ?? string.Empty;
                // Emit numeric cells for plain numbers; keep leading-zero strings (e.g. "007") as text.
                double number = 0;
                var looksNumeric = !headerStyle &&
                    text.Length > 0 && text.Length <= 15 &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                    (text == "0" ||
                     text.StartsWith("0.", StringComparison.Ordinal) ||
                     !text.StartsWith("0", StringComparison.Ordinal));
                if (looksNumeric)
                {
                    builder.Append("<c t=\"n\"><v>")
                        .Append(number.ToString("R", CultureInfo.InvariantCulture))
                        .Append("</v></c>");
                }
                else
                {
                    builder.Append(headerStyle ? "<c t=\"inlineStr\" s=\"1\"><is><t xml:space=\"preserve\">" : "<c t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                        .Append(XmlEscape(text))
                        .Append("</t></is></c>");
                }
            }

            builder.Append("</row>");
        }

        private static IReadOnlyList<string> MakeUniqueSheetNames(IReadOnlyList<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var raw in names)
            {
                var invalid = new[] { '\\', '/', '*', '?', '[', ']', ':' };
                var cleaned = new string((raw ?? "Sheet").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
                if (cleaned.Length > 31)
                {
                    cleaned = cleaned.Substring(0, 31);
                }

                var candidate = cleaned;
                var suffix = 2;
                while (!seen.Add(candidate))
                {
                    var tail = suffix.ToString(CultureInfo.InvariantCulture);
                    candidate = cleaned.Substring(0, Math.Min(cleaned.Length, 31 - tail.Length - 1)) + "_" + tail;
                    suffix++;
                }

                result.Add(candidate);
            }

            return result;
        }

        private static string XmlEscape(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");

        private static void WriteEntry(ZipArchive archive, string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        // ---------- JSON ----------

        public static void WriteJson(string path, string packageName, string modelTitle, object payload)
        {
            EnsureDirectory(path);
            var envelope = new
            {
                schemaVersion = ExportSchemaVersion,
                product = "RevitSuite Model Explorer",
                package = packageName,
                model = modelTitle,
                createdUtc = DateTimeOffset.UtcNow,
                data = payload
            };

            File.WriteAllText(path, JsonConvert.SerializeObject(envelope, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                Converters = { new StringEnumConverter() }
            }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // ---------- Table builders ----------

        public static Table BuildElementsTable(IEnumerable<ElementRecord> records)
        {
            var headers = new[]
            {
                "ElementId", "UniqueId", "Category", "Family", "Type", "Name",
                "Level", "Workset", "OwnerView", "Origin", "ViewSpecific", "Pinned", "InGroup"
            };

            var rows = records.Select(r => (IReadOnlyList<string>)new[]
            {
                r.IdValue.ToString(CultureInfo.InvariantCulture),
                r.UniqueId,
                r.Category ?? string.Empty,
                r.Family ?? string.Empty,
                r.TypeName ?? string.Empty,
                r.InstanceName ?? string.Empty,
                r.LevelName ?? string.Empty,
                r.WorksetName ?? string.Empty,
                r.OwnerViewName ?? string.Empty,
                r.Origin,
                r.IsViewSpecific ? "Yes" : "No",
                r.IsPinned ? "Yes" : "No",
                r.IsInGroup ? "Yes" : "No"
            }).ToList();

            return new Table("Elements", headers, rows);
        }

        /// <summary>One normalized row per warning-element relationship, for pivot/Power BI workflows.</summary>
        public static Table BuildWarningsTable(IEnumerable<WarningRecord> warnings)
        {
            var headers = new[]
            {
                "WarningKey", "Rank", "Description", "Role", "ElementId", "ElementName", "Categories"
            };

            var rows = new List<IReadOnlyList<string>>();
            foreach (var warning in warnings)
            {
                var categories = string.Join("; ", warning.Categories);
                var nameById = warning.ElementNames
                    .Select(n => n)
                    .ToList();

                if (warning.FailingElementIds.Count == 0 && warning.AdditionalElementIds.Count == 0)
                {
                    rows.Add(new[] { warning.WarningKey, warning.Rank.ToString(), warning.Description, "None", string.Empty, string.Empty, categories });
                    continue;
                }

                for (var i = 0; i < warning.FailingElementIds.Count; i++)
                {
                    rows.Add(new[]
                    {
                        warning.WarningKey, warning.Rank.ToString(), warning.Description, "Failing",
                        warning.FailingElementIds[i].ToString(CultureInfo.InvariantCulture),
                        i < nameById.Count ? nameById[i] : string.Empty,
                        categories
                    });
                }

                foreach (var id in warning.AdditionalElementIds)
                {
                    rows.Add(new[]
                    {
                        warning.WarningKey, warning.Rank.ToString(), warning.Description, "Additional",
                        id.ToString(CultureInfo.InvariantCulture), string.Empty, categories
                    });
                }
            }

            return new Table("Warnings", headers, rows);
        }

        public static Table BuildFindingsTable(IEnumerable<AuditFinding> findings)
        {
            var headers = new[] { "RuleId", "Rule", "Severity", "Count", "Summary", "WhyItMatters", "FixGuidance", "ElementIds" };
            var rows = findings.Select(f => (IReadOnlyList<string>)new[]
            {
                f.RuleId,
                f.RuleName,
                f.Severity.ToString(),
                f.ElementIds.Count.ToString(CultureInfo.InvariantCulture),
                f.Summary,
                f.WhyItMatters,
                f.SafeFixGuidance,
                string.Join(" ", f.ElementIds)
            }).ToList();

            return new Table("AuditFindings", headers, rows);
        }

        public static Table BuildHealthTable(HealthScore health)
        {
            var headers = new[] { "Component", "Severity", "Count", "Deduction" };
            var rows = new List<IReadOnlyList<string>>
            {
                new[] { "TOTAL SCORE", string.Empty, string.Empty, health.Score.ToString(CultureInfo.InvariantCulture) }
            };

            rows.AddRange(health.Components.Select(c => (IReadOnlyList<string>)new[]
            {
                c.Label,
                c.Severity.ToString(),
                c.Count.ToString(CultureInfo.InvariantCulture),
                c.Deduction.ToString(CultureInfo.InvariantCulture)
            }));

            return new Table("HealthScore", headers, rows);
        }

        public static Table BuildViewsTable(IEnumerable<ViewRecord> records, string sheetName)
        {
            var headers = new[] { "Id", "Name", "Kind", "OnSheet", "Sheets", "ViewTemplate", "Level", "DuplicateName" };
            var rows = records.Select(v => (IReadOnlyList<string>)new[]
            {
                v.IdValue.ToString(CultureInfo.InvariantCulture),
                v.Name,
                v.ViewKind,
                v.IsPlacedOnSheet ? "Yes" : "No",
                string.Join("; ", v.SheetNumbers),
                v.ViewTemplateName ?? string.Empty,
                v.LevelName ?? string.Empty,
                v.HasDuplicateName ? "Yes" : "No"
            }).ToList();

            return new Table(sheetName, headers, rows);
        }

        public static Table BuildRunMetadataTable(string modelTitle, string scopeDescription)
        {
            var headers = new[] { "Key", "Value" };
            var rows = new List<IReadOnlyList<string>>
            {
                new[] { "Product", "RevitSuite Model Explorer" },
                new[] { "SchemaVersion", ExportSchemaVersion.ToString(CultureInfo.InvariantCulture) },
                new[] { "Model", modelTitle },
                new[] { "Scope", scopeDescription },
                new[] { "CreatedUtc", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };

            return new Table("RunMetadata", headers, rows);
        }

        private static void EnsureDirectory(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
