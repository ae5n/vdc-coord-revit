using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Host.Commands;
using System;
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

                if (!string.IsNullOrWhiteSpace(OutputPath))
                {
                    var exportResult = SharedCoordinatesReportCommand.RunCore(
                        app,
                        OutputPath,
                        IncludeLinkedModels,
                        Precision,
                        AnglePrecision);

                    if (exportResult == null)
                    {
                        Result = new { success = false, error = "No shared coordinate data found." };
                        return;
                    }

                    var (csvPath, htmlPath, rowCount, pointCount) = exportResult.Value;
                    Result = new
                    {
                        success = true,
                        rowCount,
                        pointCount,
                        csvPath,
                        htmlPath
                    };
                    return;
                }

                var reportData = SharedCoordinatesReportCommand.BuildMcpReportData(app, IncludeLinkedModels, Precision, AnglePrecision);
                if (reportData == null)
                {
                    Result = new { success = false, error = "No shared coordinate data found." };
                    return;
                }

                Result = new
                {
                    success = true,
                    rowCount = reportData.RowCount,
                    pointCount = reportData.PointCount,
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

        public string GetName() => "Shared Coordinates Report MCP";

    }
}
