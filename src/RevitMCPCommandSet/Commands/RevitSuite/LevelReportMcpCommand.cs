using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.RevitSuite;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.RevitSuite
{
    public class LevelReportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private LevelReportMcpEventHandler _handler => (LevelReportMcpEventHandler)Handler;

        public override string CommandName => "run_level_report";

        public LevelReportMcpCommand(UIApplication uiApp)
            : base(new LevelReportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var props = RevitSuiteSchemaLoader.LoadProperties("level_report.schema.json");

                _handler.IncludeLinkedModels = parameters?["includeLinkedModels"]?.Value<bool>()
                    ?? RevitSuiteSchemaLoader.GetBool(props, "includeLinkedModels", true);
                _handler.Precision = parameters?["precision"]?.Value<int>()
                    ?? RevitSuiteSchemaLoader.GetInt(props, "precision", 2);
                _handler.MaxPreviewRows = parameters?["maxPreviewRows"]?.Value<int>()
                    ?? RevitSuiteSchemaLoader.GetInt(props, "maxPreviewRows", 5);
                _handler.OutputPath = parameters?["outputPath"]?.Value<string>();

                if (RaiseAndWaitForCompletion(30000))
                    return _handler.Result;
                else
                    throw new TimeoutException("run_level_report timed out.");
            }
        }
    }
}
