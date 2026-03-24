using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.RevitSuite;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.RevitSuite
{
    public class NwcBatchExportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private NwcBatchExportMcpEventHandler _handler => (NwcBatchExportMcpEventHandler)Handler;

        public override string CommandName => "run_nwc_batch_export";

        public NwcBatchExportMcpCommand(UIApplication uiApp)
            : base(new NwcBatchExportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var props = RevitSuiteSchemaLoader.LoadProperties("nwc_batch_export.schema.json");

                _handler.Coordinates = parameters?["coordinates"]?.Value<string>()
                    ?? RevitSuiteSchemaLoader.GetString(props, "coordinates", "Shared");
                _handler.ExportLinks = parameters?["exportLinks"]?.Value<bool>()
                    ?? RevitSuiteSchemaLoader.GetBool(props, "exportLinks", true);
                _handler.DivideFileIntoLevels = parameters?["divideFileIntoLevels"]?.Value<bool>()
                    ?? RevitSuiteSchemaLoader.GetBool(props, "divideFileIntoLevels", true);
                _handler.ExportElementIds = parameters?["exportElementIds"]?.Value<bool>()
                    ?? RevitSuiteSchemaLoader.GetBool(props, "exportElementIds", true);
                _handler.OutputDirectory = parameters?["outputDirectory"]?.Value<string>();
                _handler.ViewNames = parameters?["viewNames"]?.ToObject<string[]>();

                if (RaiseAndWaitForCompletion(120000))
                    return _handler.Result;
                else
                    throw new TimeoutException("run_nwc_batch_export timed out.");
            }
        }
    }
}
