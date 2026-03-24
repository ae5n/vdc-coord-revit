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
    public class GridReportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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

                var records = CollectGrids(doc, IncludeLinkedModels);
                if (records.Count == 0) { Result = new { success = false, error = "No grids found." }; return; }

                var outputPath = ResolveOutputPath(OutputPath, doc.Title, "Grids");
                WriteCsv(outputPath, records, Precision);

                var preview = records.Take(MaxPreviewRows)
                    .Select(r => new { r.Model, r.GridName, r.OriginX, r.OriginY })
                    .ToList();

                Result = new { success = true, outputPath, rowCount = records.Count, preview };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally { _resetEvent.Set(); }
        }

        public string GetName() => "Grid Report MCP";

        private static List<GridRecord> CollectGrids(Document doc, bool includeLinked)
        {
            var result = new List<GridRecord>();
            AppendGrids(doc, "Host", result);

            if (includeLinked)
            {
                foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc != null) AppendGrids(linkDoc, linkDoc.Title, result);
                }
            }
            return result;
        }

        private static void AppendGrids(Document doc, string model, IList<GridRecord> sink)
        {
            foreach (var grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                var curve = grid.Curve;
                var origin = curve?.GetEndPoint(0) ?? XYZ.Zero;
                var end = curve?.GetEndPoint(1) ?? XYZ.Zero;
                var angle = Math.Atan2(end.Y - origin.Y, end.X - origin.X) * 180.0 / Math.PI;
                sink.Add(new GridRecord(model, grid.Name, origin.X, origin.Y, angle));
            }
        }

        private static void WriteCsv(string path, IEnumerable<GridRecord> records, int precision)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var sb = new StringBuilder();
            sb.AppendLine("Model,Grid,Origin X (ft),Origin Y (ft),Angle (deg)");
            var fmt = $"F{precision}";
            foreach (var r in records)
                sb.AppendLine($"{Escape(r.Model)},{Escape(r.GridName)},{r.OriginX.ToString(fmt, CultureInfo.InvariantCulture)},{r.OriginY.ToString(fmt, CultureInfo.InvariantCulture)},{r.AngleDeg.ToString("F4", CultureInfo.InvariantCulture)}");
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

        private class GridRecord
        {
            public string Model { get; }
            public string GridName { get; }
            public double OriginX { get; }
            public double OriginY { get; }
            public double AngleDeg { get; }
            public GridRecord(string model, string gridName, double originX, double originY, double angleDeg)
            { Model = model; GridName = gridName; OriginX = originX; OriginY = originY; AngleDeg = angleDeg; }
        }
    }
}
