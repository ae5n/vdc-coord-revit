using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services.RevitSuite
{
    public class NwcBatchExportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string Coordinates { get; set; } = "Shared";
        public bool ExportLinks { get; set; } = true;
        public bool DivideFileIntoLevels { get; set; } = true;
        public bool ExportElementIds { get; set; } = true;
        public string OutputDirectory { get; set; }
        public string[] ViewNames { get; set; }
        public object Result { get; private set; }

        public bool WaitForCompletion(int timeoutMs = 120000)
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

                var outputDir = string.IsNullOrWhiteSpace(OutputDirectory)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitSuite", "NWC")
                    : OutputDirectory;

                Directory.CreateDirectory(outputDir);

                var views = Collect3DViews(doc, ViewNames);
                if (views.Count == 0) { Result = new { success = false, error = "No 3D views found matching criteria." }; return; }

                var options = BuildExportOptions();
                var exported = new List<string>();

                foreach (var view in views)
                {
                    try
                    {
                        var fileName = $"{SanitizeName(view.Name)}.nwc";
                        doc.Export(outputDir, fileName, options);
                        exported.Add(Path.Combine(outputDir, fileName));
                    }
                    catch { /* skip individual view failures */ }
                }

                Result = new { success = true, outputDirectory = outputDir, exportedFiles = exported };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally { _resetEvent.Set(); }
        }

        public string GetName() => "NWC Batch Export MCP";

        private static List<View3D> Collect3DViews(Document doc, string[] viewNames)
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate)
                .ToList();

            if (viewNames == null || viewNames.Length == 0) return all;

            return all.Where(v => viewNames.Contains(v.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        private NavisworksExportOptions BuildExportOptions()
        {
            var coordMode = Coordinates switch
            {
                "Project" => NavisworksCoordinates.Internal,
                "Internal" => NavisworksCoordinates.Internal,
                _ => NavisworksCoordinates.Shared
            };

            return new NavisworksExportOptions
            {
                Coordinates = coordMode,
                ExportLinks = ExportLinks,
                DivideFileIntoLevels = DivideFileIntoLevels,
                ExportElementIds = ExportElementIds,
            };
        }

        private static string SanitizeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }
    }
}
