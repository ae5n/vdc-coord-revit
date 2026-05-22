// Copyright (c) 2026 sparx-fire (https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
// Licensed under the MIT License.

using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Access
{
    public class PickLinkedElementsCommand : ExternalEventCommandBase
    {
        private static readonly object ExecutionLock = new object();
        private PickLinkedElementsEventHandler HandlerInstance => (PickLinkedElementsEventHandler)Handler;

        public override string CommandName => "pick_linked_revit_elements";

        public PickLinkedElementsCommand(UIApplication uiApp)
            : base(new PickLinkedElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (ExecutionLock)
            {
                try
                {
                    bool multiple = parameters?["multiple"]?.Value<bool>() ?? true;
                    string prompt = parameters?["prompt"]?.Value<string>();

                    HandlerInstance.SetSelectionParameters(multiple, prompt);

                    if (RaiseAndWaitForCompletion(300000))
                    {
                        return HandlerInstance.Result;
                    }

                    throw new TimeoutException("Linked element picking timed out.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Pick linked Revit elements failed: {ex.Message}", ex);
                }
            }
        }
    }
}
