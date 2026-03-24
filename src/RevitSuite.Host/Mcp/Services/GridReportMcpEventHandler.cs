using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Host.Commands;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace RevitSuite.Host.Mcp.Services
{
    public class GridReportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public bool IncludeLinkedModels { get; set; }
        public int Precision { get; set; }
        public int MaxPreviewRows { get; set; }
        public string? OutputPath { get; set; }
        public object Result { get; private set; } = new object();

        public bool WaitForCompletion(int timeoutMs = 30000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMs);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                if (app.ActiveUIDocument == null)
                {
                    Result = new { success = false, error = "No active document." };
                    return;
                }

                var runResult = GridReportCommand.RunCore(app, OutputPath, IncludeLinkedModels, Precision);
                if (runResult == null)
                {
                    Result = new { success = false, error = "No grids found." };
                    return;
                }

                var (outputPath, discrepancyPath, reportPath, rowCount, discrepancyCount) = runResult.Value;
                var preview = ReadCsvPreview(outputPath, MaxPreviewRows);
                Result = new { success = true, outputPath, discrepancyPath, reportPath, rowCount, discrepancyCount, preview };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName() => "Grid Report MCP";

        private static string[] ReadCsvPreview(string csvPath, int maxRows)
        {
            try
            {
                return File.ReadAllLines(csvPath).Skip(1).Take(maxRows).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
