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
    public class SharedCoordinatesReportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public bool IncludeLinkedModels { get; set; } = true;
        public int Precision { get; set; } = 3;
        public int AnglePrecision { get; set; } = 4;
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

                var records = CollectRecords(doc, IncludeLinkedModels);
                if (records.Count == 0) { Result = new { success = false, error = "No shared coordinate data found." }; return; }

                var outputPath = ResolveOutputPath(OutputPath, doc.Title, "SharedCoordinates");
                WriteCsv(outputPath, records, Precision, AnglePrecision);

                var preview = records.Take(MaxPreviewRows)
                    .Select(r => new { r.ModelName, r.PointLabel, r.SharedEastWest, r.SharedNorthSouth, r.SharedElevation, r.AngleDeg })
                    .ToList();

                Result = new { success = true, outputPath, rowCount = records.Count, preview };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally { _resetEvent.Set(); }
        }

        public string GetName() => "Shared Coordinates Report MCP";

        private static List<CoordRecord> CollectRecords(Document doc, bool includeLinked)
        {
            var result = new List<CoordRecord>();
            AppendRecords(doc, "Host", null, result);

            if (includeLinked)
            {
                foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc != null) AppendRecords(linkDoc, "Link", link, result);
                }
            }
            return result;
        }

        private static void AppendRecords(Document doc, string modelType, RevitLinkInstance link, IList<CoordRecord> sink)
        {
            var location = doc.ActiveProjectLocation;
            var projectToShared = location?.GetTransform()?.Inverse ?? Transform.Identity;
            var angle = location != null
                ? location.GetProjectPosition(XYZ.Zero)?.Angle * 180.0 / Math.PI ?? double.NaN
                : double.NaN;

            var points = new FilteredElementCollector(doc).OfClass(typeof(BasePoint)).Cast<BasePoint>().ToList();
            if (!points.Any())
            {
                var pbp = BasePoint.GetProjectBasePoint(doc);
                var sp = BasePoint.GetSurveyPoint(doc);
                if (pbp != null) points.Add(pbp);
                if (sp != null) points.Add(sp);
            }

            foreach (var pt in points)
            {
                var sharedPos = projectToShared.OfPoint(pt.Position);
                var label = pt.IsShared ? "Survey Point" : "Project Base Point";
                sink.Add(new CoordRecord(doc.Title, modelType, label, sharedPos.X, sharedPos.Y, sharedPos.Z, angle));
            }
        }

        private static void WriteCsv(string path, IEnumerable<CoordRecord> records, int precision, int anglePrecision)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var sb = new StringBuilder();
            sb.AppendLine("Model,Type,Point,East/West (ft),North/South (ft),Elevation (ft),Angle to True North (deg)");
            foreach (var r in records)
            {
                var fmt = $"F{precision}";
                sb.AppendLine($"{Escape(r.ModelName)},{Escape(r.ModelType)},{Escape(r.PointLabel)}," +
                              $"{r.SharedEastWest.ToString(fmt, CultureInfo.InvariantCulture)}," +
                              $"{r.SharedNorthSouth.ToString(fmt, CultureInfo.InvariantCulture)}," +
                              $"{r.SharedElevation.ToString(fmt, CultureInfo.InvariantCulture)}," +
                              $"{(double.IsNaN(r.AngleDeg) ? "" : r.AngleDeg.ToString($"F{anglePrecision}", CultureInfo.InvariantCulture))}");
            }
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

        private class CoordRecord
        {
            public string ModelName { get; }
            public string ModelType { get; }
            public string PointLabel { get; }
            public double SharedEastWest { get; }
            public double SharedNorthSouth { get; }
            public double SharedElevation { get; }
            public double AngleDeg { get; }
            public CoordRecord(string modelName, string modelType, string pointLabel,
                double ew, double ns, double elev, double angle)
            { ModelName = modelName; ModelType = modelType; PointLabel = pointLabel;
              SharedEastWest = ew; SharedNorthSouth = ns; SharedElevation = elev; AngleDeg = angle; }
        }
    }
}
