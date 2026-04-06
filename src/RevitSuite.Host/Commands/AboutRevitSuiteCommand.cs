using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Versioning;
using System.Reflection;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AboutRevitSuiteCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var dialog = new TaskDialog(ProductVersionInfo.ProductName)
            {
                MainInstruction = ProductVersionInfo.ProductName,
                MainContent =
                    $"Version: {ProductVersionInfo.DisplayVersion}\n" +
                    $"Assembly: {assembly.Location}\n\n" +
                    "Use this dialog to confirm which installed build of RevitSuite is running in Revit.",
                TitleAutoPrefix = false
            };

            dialog.Show();
            return Result.Succeeded;
        }
    }
}
