using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitSuite.Host.Mcp.Services;
using System;

namespace RevitSuite.Host.Mcp.Commands
{
    public class NwcBatchExportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private NwcBatchExportMcpEventHandler _handler => (NwcBatchExportMcpEventHandler)Handler;

        public override string CommandName => "export_nwc_views";

        public NwcBatchExportMcpCommand(UIApplication uiApp)
            : base(new NwcBatchExportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                _handler.OutputDirectory = parameters?["outputDirectory"]?.Value<string>();
                _handler.ViewNames = parameters?["viewNames"]?.ToObject<string[]>();

                if (RaiseAndWaitForCompletion(120000))
                    return _handler.Result;
                else
                    throw new TimeoutException("export_nwc_views timed out.");
            }
        }
    }
}
