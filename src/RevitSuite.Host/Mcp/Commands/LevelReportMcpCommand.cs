using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitSuite.Host.Config;
using RevitSuite.Host.Mcp.Services;
using System;

namespace RevitSuite.Host.Mcp.Commands
{
    public class LevelReportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private LevelReportMcpEventHandler _handler => (LevelReportMcpEventHandler)Handler;

        public override string CommandName => "export_level_report";

        public LevelReportMcpCommand(UIApplication uiApp)
            : base(new LevelReportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var config = LevelReportConfig.Load();

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
                    throw new TimeoutException("export_level_report timed out.");
            }
        }
    }
}
