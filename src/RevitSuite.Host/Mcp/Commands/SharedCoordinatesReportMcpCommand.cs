using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitSuite.Host.Config;
using RevitSuite.Host.Mcp.Services;
using System;

namespace RevitSuite.Host.Mcp.Commands
{
    public class SharedCoordinatesReportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private SharedCoordinatesReportMcpEventHandler _handler => (SharedCoordinatesReportMcpEventHandler)Handler;

        public override string CommandName => "get_shared_coordinates_report";

        public SharedCoordinatesReportMcpCommand(UIApplication uiApp)
            : base(new SharedCoordinatesReportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var config = SharedCoordinatesReportConfig.Load();

                _handler.IncludeLinkedModels = parameters?["includeLinkedModels"]?.Value<bool>()
                    ?? config.IncludeLinkedModels;
                _handler.Precision = parameters?["precision"]?.Value<int>()
                    ?? config.Precision;
                _handler.AnglePrecision = parameters?["anglePrecision"]?.Value<int>()
                    ?? config.AnglePrecision;
                _handler.MaxPreviewRows = parameters?["maxPreviewRows"]?.Value<int>()
                    ?? config.MaxPreviewRows;
                _handler.OutputPath = parameters?["outputPath"]?.Value<string>();

                if (RaiseAndWaitForCompletion(30000))
                    return _handler.Result;
                else
                    throw new TimeoutException("get_shared_coordinates_report timed out.");
            }
        }
    }
}
