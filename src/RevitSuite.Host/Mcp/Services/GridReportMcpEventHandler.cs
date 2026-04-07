using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Host.Commands;
using System;
using System.IO;
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

                if (!string.IsNullOrWhiteSpace(OutputPath))
                {
                    var exportResult = GridReportCommand.RunCore(
                        app,
                        OutputPath,
                        IncludeLinkedModels,
                        Precision);

                    if (exportResult == null)
                    {
                        Result = new { success = false, error = "No grids found." };
                        return;
                    }

                    var (csvPath, htmlPath, rowCount, discrepancyCount) = exportResult.Value;
                    Result = new
                    {
                        success = true,
                        rowCount,
                        discrepancyCount,
                        csvPath,
                        htmlPath
                    };
                    return;
                }

                var reportData = GridReportCommand.BuildMcpReportData(app, IncludeLinkedModels, Precision);
                if (reportData == null)
                {
                    Result = new { success = false, error = "No grids found." };
                    return;
                }

                Result = new
                {
                    success = true,
                    rowCount = reportData.RowCount,
                    discrepancyCount = reportData.DiscrepancyCount,
                    htmlContent = reportData.HtmlContent
                };
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

    }
}
