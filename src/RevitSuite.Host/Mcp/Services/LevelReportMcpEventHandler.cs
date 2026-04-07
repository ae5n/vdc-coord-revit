using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Host.Commands;
using System;
using System.Threading;

namespace RevitSuite.Host.Mcp.Services
{
    public class LevelReportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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
                    var exportResult = LevelReportCommand.RunCore(
                        app,
                        OutputPath,
                        IncludeLinkedModels,
                        Precision,
                        MaxPreviewRows);

                    if (exportResult == null)
                    {
                        Result = new { success = false, error = "No levels found." };
                        return;
                    }

                    var (csvPath, htmlPath, rowCount) = exportResult.Value;
                    Result = new
                    {
                        success = true,
                        rowCount,
                        csvPath,
                        htmlPath
                    };
                    return;
                }

                var reportData = LevelReportCommand.BuildMcpReportData(app, IncludeLinkedModels, Precision);
                if (reportData == null)
                {
                    Result = new { success = false, error = "No levels found." };
                    return;
                }

                Result = new
                {
                    success = true,
                    rowCount = reportData.RowCount,
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

        public string GetName() => "Level Report MCP";

    }
}
