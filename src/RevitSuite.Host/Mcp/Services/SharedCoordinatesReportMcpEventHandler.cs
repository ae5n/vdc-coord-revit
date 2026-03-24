using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Host.Commands;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace RevitSuite.Host.Mcp.Services
{
    public class SharedCoordinatesReportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public bool IncludeLinkedModels { get; set; }
        public int Precision { get; set; }
        public int AnglePrecision { get; set; }
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

                var runResult = SharedCoordinatesReportCommand.RunCore(app, OutputPath, IncludeLinkedModels, Precision, AnglePrecision);
                if (runResult == null)
                {
                    Result = new { success = false, error = "No shared coordinate data found." };
                    return;
                }

                var (csvPath, htmlPath, rowCount, pointCount) = runResult.Value;
                var preview = ReadCsvPreview(csvPath, MaxPreviewRows);
                Result = new { success = true, csvPath, htmlPath, rowCount, pointCount, preview };
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

        public string GetName() => "Shared Coordinates Report MCP";

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
