using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using System;
using System.IO;
using System.Threading;

namespace RevitMCPCommandSet.Services.RevitSuite
{
    /// <summary>
    /// MCP event handler for QAQC workflow.
    ///
    /// Modes:
    ///   export  — collect control points from the model and write a CSV template
    ///   import  — read a field survey CSV and calculate deviations (places annotations)
    ///
    /// Full wiring into QaqcCommand.Export/Import/Place logic is a follow-up task.
    /// This handler validates the pipeline end-to-end and returns actionable status.
    /// </summary>
    public class QaqcMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string Mode { get; set; } = "export";
        public string CsvPath { get; set; }
        public double ToleranceGreen { get; set; } = 0.01;
        public double ToleranceYellow { get; set; } = 0.05;
        public string ComparisonMethod { get; set; } = "horizontal";
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

                switch (Mode.ToLowerInvariant())
                {
                    case "export":
                        Result = RunExport(doc);
                        break;
                    case "import":
                        Result = RunImport(doc);
                        break;
                    default:
                        Result = new { success = false, error = $"Unknown mode '{Mode}'. Use 'export' or 'import'." };
                        break;
                }
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally { _resetEvent.Set(); }
        }

        public string GetName() => "QAQC MCP";

        private object RunExport(Document doc)
        {
            // TODO: Wire into QaqcCommand.Export logic
            // This requires calling the headless export methods from QaqcCommand.Export.cs.
            // For now, return a meaningful stub that shows the config was received.
            var outputPath = string.IsNullOrWhiteSpace(CsvPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitSuite", $"{SanitizeName(doc.Title)}_QAQC_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv")
                : CsvPath;

            return new
            {
                success = false,
                mode = "export",
                outputPath,
                note = "Full QAQC export wiring is pending. Configure outputPath and run via the ribbon for now.",
                config = new { ToleranceGreen, ToleranceYellow, ComparisonMethod }
            };
        }

        private object RunImport(Document doc)
        {
            // TODO: Wire into QaqcCommand.Import + QaqcCommand.Place logic
            if (string.IsNullOrWhiteSpace(CsvPath) || !File.Exists(CsvPath))
                return new { success = false, error = "csvPath is required for import mode and must point to an existing file." };

            return new
            {
                success = false,
                mode = "import",
                csvPath = CsvPath,
                note = "Full QAQC import wiring is pending.",
                config = new { ToleranceGreen, ToleranceYellow, ComparisonMethod }
            };
        }

        private static string SanitizeName(string value)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return new string(System.Array.ConvertAll(value.ToCharArray(),
                c => System.Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        }
    }
}
