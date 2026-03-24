using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.RevitSuite;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.RevitSuite
{
    public class FootingZonesMcpCommand : ExternalEventCommandBase
    {
        private static readonly object _lock = new object();
        private FootingZonesMcpEventHandler _handler => (FootingZonesMcpEventHandler)Handler;

        public override string CommandName => "run_footing_zones";

        public FootingZonesMcpCommand(UIApplication uiApp)
            : base(new FootingZonesMcpEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_lock)
            {
                var props = RevitSuiteSchemaLoader.LoadProperties("footing_zone.schema.json");

                _handler.ClearDepth = parameters?["clearDepth"]?.Value<double>()
                    ?? RevitSuiteSchemaLoader.GetDouble(props, "clearDepth", 5.0);
                _handler.SlopeRatio = parameters?["slopeRatio"]?.Value<double>()
                    ?? RevitSuiteSchemaLoader.GetDouble(props, "slopeRatio", 1.0);
                _handler.VerticalOffset = parameters?["verticalOffset"]?.Value<double>()
                    ?? RevitSuiteSchemaLoader.GetDouble(props, "verticalOffset", 0.0);
                _handler.Transparency = parameters?["transparency"]?.Value<int>()
                    ?? RevitSuiteSchemaLoader.GetInt(props, "transparency", 50);
                _handler.IncludeFootings = parameters?["includeFootings"]?.Value<bool>()
                    ?? RevitSuiteSchemaLoader.GetBool(props, "includeFootings", true);

                if (RaiseAndWaitForCompletion(60000))
                    return _handler.Result;
                else
                    throw new TimeoutException("run_footing_zones timed out.");
            }
        }
    }
}
