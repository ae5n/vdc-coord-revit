using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RevitMCPCommandSet.Services.RevitSuite
{
    public class LevelReportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public bool IncludeLinkedModels { get; set; } = true;
        public int Precision { get; set; } = 2;
        public int MaxPreviewRows { get; set; } = 5;
        public string OutputPath { get; set; }
        public object Result { get; private set; }

        public bool WaitForCompletion(int timeoutMs = 30000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMs);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Result = new { success = false, error = "No active document." }; return; }

                var records = CollectLevels(doc, IncludeLinkedModels);
                if (records.Count == 0) { Result = new { success = false, error = "No levels found." }; return; }

                var outputPath = ResolveOutputPath(OutputPath, doc.Title, "Levels");
                WriteCsv(outputPath, records, Precision);

                var preview = records.Take(MaxPreviewRows)
                    .Select(r => new { r.Model, r.LevelName, elevationFt = Math.Round(r.ElevationFt, Precision) })
                    .ToList();

                Result = new { success = true, outputPath, rowCount = records.Count, preview };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally { _resetEvent.Set(); }
        }

        public string GetName() => "Level Report MCP";

        private static List<LevelRecord> CollectLevels(Document doc, bool includeLinked)
        {
            var result = new List<LevelRecord>();
            AppendLevels(doc, "Host", result);

            if (includeLinked)
            {
                foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc != null) AppendLevels(linkDoc, linkDoc.Title, result);
                }
            }
            return result;
        }

        private static void AppendLevels(Document doc, string model, IList<LevelRecord> sink)
        {
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
                sink.Add(new LevelRecord(model, level.Name, level.Elevation));
        }

        private static void WriteCsv(string path, IEnumerable<LevelRecord> records, int precision)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var sb = new StringBuilder();
            sb.AppendLine("Model,Level,Elevation (ft)");
            foreach (var r in records)
                sb.AppendLine($"{Escape(r.Model)},{Escape(r.LevelName)},{Math.Round(r.ElevationFt, precision).ToString(CultureInfo.InvariantCulture)}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string ResolveOutputPath(string given, string docTitle, string suffix)
        {
            if (!string.IsNullOrWhiteSpace(given)) return given;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitSuite");
            var name = $"{SanitizeName(docTitle)}_{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            return Path.Combine(dir, name);
        }

        private static string SanitizeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }

        private static string Escape(string value) =>
            value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

        private class LevelRecord
        {
            public string Model { get; }
            public string LevelName { get; }
            public double ElevationFt { get; }
            public LevelRecord(string model, string levelName, double elevationFt)
            { Model = model; LevelName = levelName; ElevationFt = elevationFt; }
        }
    }
}
