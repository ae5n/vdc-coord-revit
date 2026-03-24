using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitSuite.Host.Config;
using RevitSuite.Host.Mcp.Services;
using System;

namespace RevitSuite.Host.Mcp.Commands
{
    public class GridReportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private GridReportMcpEventHandler _handler => (GridReportMcpEventHandler)Handler;

        public override string CommandName => "export_grid_report";

        public GridReportMcpCommand(UIApplication uiApp)
            : base(new GridReportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var config = GridReportConfig.Load();

                _handler.IncludeLinkedModels = parameters?["includeLinkedModels"]?.Value<bool>()
                    ?? config.IncludeLinkedModels;
                _handler.Precision = parameters?["precision"]?.Value<int>()
                    ?? config.Precision;
                _handler.MaxPreviewRows = parameters?["maxPreviewRows"]?.Value<int>()
                    ?? config.MaxPreviewRows;
                _handler.OutputPath = parameters?["outputPath"]?.Value<string>();

                if (RaiseAndWaitForCompletion(30000))
                    return _handler.Result;
                else
                    throw new TimeoutException("export_grid_report timed out.");
            }
        }
    }
}
