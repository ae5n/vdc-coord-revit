using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.RevitSuite;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.RevitSuite
{
    public class SharedCoordinatesReportMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private SharedCoordinatesReportMcpEventHandler _handler => (SharedCoordinatesReportMcpEventHandler)Handler;

        public override string CommandName => "run_shared_coordinates_report";

        public SharedCoordinatesReportMcpCommand(UIApplication uiApp)
            : base(new SharedCoordinatesReportMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var props = RevitSuiteSchemaLoader.LoadProperties("shared_coordinates_report.schema.json");

                _handler.IncludeLinkedModels = parameters?["includeLinkedModels"]?.Value<bool>()
                    ?? RevitSuiteSchemaLoader.GetBool(props, "includeLinkedModels", true);
                _handler.Precision = parameters?["precision"]?.Value<int>()
                    ?? RevitSuiteSchemaLoader.GetInt(props, "precision", 3);
                _handler.AnglePrecision = parameters?["anglePrecision"]?.Value<int>()
                    ?? RevitSuiteSchemaLoader.GetInt(props, "anglePrecision", 4);
                _handler.MaxPreviewRows = parameters?["maxPreviewRows"]?.Value<int>()
                    ?? RevitSuiteSchemaLoader.GetInt(props, "maxPreviewRows", 5);
                _handler.OutputPath = parameters?["outputPath"]?.Value<string>();

                if (RaiseAndWaitForCompletion(30000))
                    return _handler.Result;
                else
                    throw new TimeoutException("run_shared_coordinates_report timed out.");
            }
        }
    }
}
