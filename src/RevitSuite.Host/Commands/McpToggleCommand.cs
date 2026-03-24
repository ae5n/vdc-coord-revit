using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Mcp.Core;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class McpToggleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var service = SocketService.Instance;

            if (service.IsRunning)
            {
                service.Stop();
                TaskDialog.Show("RevitSuite MCP", "MCP server stopped.");
            }
            else
            {
                service.Initialize(commandData.Application);
                service.Start();
                TaskDialog.Show("RevitSuite MCP", $"MCP server started on port {service.Port}.");
            }

            return Result.Succeeded;
        }
    }
}
