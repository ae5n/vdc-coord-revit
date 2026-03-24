using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.RevitSuite;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.RevitSuite
{
    public class QaqcMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private QaqcMcpEventHandler _handler => (QaqcMcpEventHandler)Handler;

        public override string CommandName => "run_qaqc";

        public QaqcMcpCommand(UIApplication uiApp)
            : base(new QaqcMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var props = RevitSuiteSchemaLoader.LoadProperties("qaqc.schema.json");
                var defaults = props["defaults"] as JObject;

                _handler.Mode = parameters?["mode"]?.Value<string>() ?? "export";
                _handler.CsvPath = parameters?["csvPath"]?.Value<string>();

                // Tolerance overrides (fall back to schema defaults)
                _handler.ToleranceGreen = parameters?["toleranceGreen"]?.Value<double>()
                    ?? (defaults != null ? RevitSuiteSchemaLoader.GetDouble(defaults, "toleranceGreen", 0.01) : 0.01);
                _handler.ToleranceYellow = parameters?["toleranceYellow"]?.Value<double>()
                    ?? (defaults != null ? RevitSuiteSchemaLoader.GetDouble(defaults, "toleranceYellow", 0.05) : 0.05);
                _handler.ComparisonMethod = parameters?["comparisonMethod"]?.Value<string>()
                    ?? (defaults != null ? RevitSuiteSchemaLoader.GetString(defaults, "comparisonMethod", "horizontal") : "horizontal");

                if (RaiseAndWaitForCompletion(120000))
                    return _handler.Result;
                else
                    throw new TimeoutException("run_qaqc timed out.");
            }
        }
    }
}
