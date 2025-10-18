using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitSuite.Host
{
    public class App : IExternalApplication
    {
        private const string TabName = "Revit Suite";
        private const string PanelName = "Automation";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                TryCreateTab(application);
                var panel = GetOrCreatePanel(application);

                var assemblyPath = Assembly.GetExecutingAssembly().Location;

                var createViewsButton = new PushButtonData(
                    "RevitSuite_CreateViews",
                    "Create Views",
                    assemblyPath,
                    "RevitSuite.Host.Commands.CreateViewsCommand")
                {
                    ToolTip = "Create floor or ceiling plan views using the Python engine."
                };

                var levelReportButton = new PushButtonData(
                    "RevitSuite_LevelReport",
                    "Level Report",
                    assemblyPath,
                    "RevitSuite.Host.Commands.LevelReportCommand")
                {
                    ToolTip = "Export a sorted CSV of levels across host and linked models."
                };

                panel.AddItem(createViewsButton);
                panel.AddItem(levelReportButton);

                return Result.Succeeded;
            }
            catch (Exception)
            {
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static void TryCreateTab(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch
            {
                // Tab may already exist. Ignore.
            }
        }

        private static RibbonPanel GetOrCreatePanel(UIControlledApplication application)
        {
            var panel = application
                .GetRibbonPanels(TabName)
                .FirstOrDefault(p => p.Name.Equals(PanelName, StringComparison.OrdinalIgnoreCase));

            return panel ?? application.CreateRibbonPanel(TabName, PanelName);
        }
    }
}
