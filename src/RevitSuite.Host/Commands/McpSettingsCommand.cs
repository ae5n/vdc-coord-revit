using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.UI;
using System.Diagnostics;
using System.Windows.Interop;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class McpSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var window = new McpSettingsWindow();
            new WindowInteropHelper(window)
            {
                Owner = Process.GetCurrentProcess().MainWindowHandle
            };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
