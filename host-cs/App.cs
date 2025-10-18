using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using Autodesk.Revit.UI;
using RevitSuite.Host.UI;

namespace RevitSuite.Host
{
    public class App : IExternalApplication
    {
        private const string TabName = "Revit Suite";
        private const string AutomationPanelName = "Automation";
        private const string ReportsPanelName = "Reports";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                TryCreateTab(application);
                var automationPanel = GetOrCreatePanel(application, AutomationPanelName);
                var reportsPanel = GetOrCreatePanel(application, ReportsPanelName);

                var assemblyPath = Assembly.GetExecutingAssembly().Location;

                var createViewsButton = AddButton(
                    automationPanel,
                    new PushButtonData(
                        "RevitSuite_CreateViews",
                        "Create Views",
                        assemblyPath,
                        "RevitSuite.Host.Commands.CreateViewsCommand")
                    {
                        ToolTip = "Create floor or ceiling plan views using the Python engine."
                    });
                SetButtonIcon(createViewsButton, "CV", Color.FromRgb(0x2B, 0x7B, 0xBA));

                var footingZoneButton = AddButton(
                    automationPanel,
                    new PushButtonData(
                        "RevitSuite_FootingZone",
                        "Footing Zones",
                        assemblyPath,
                        "RevitSuite.Host.Commands.FootingZoneCommand")
                    {
                        ToolTip = "Create transparent influence zones for foundations and selected slabs."
                    });
                SetButtonIcon(footingZoneButton, "FZ", Color.FromRgb(0x38, 0x8E, 0x3C));

                var levelReportButton = AddButton(
                    reportsPanel,
                    new PushButtonData(
                        "RevitSuite_LevelReport",
                        "Level Report",
                        assemblyPath,
                        "RevitSuite.Host.Commands.LevelReportCommand")
                    {
                        ToolTip = "Export a sorted CSV of levels across host and linked models."
                    });
                SetButtonIcon(levelReportButton, "LR", Color.FromRgb(0x87, 0x52, 0xB0));

                var gridReportButton = AddButton(
                    reportsPanel,
                    new PushButtonData(
                        "RevitSuite_GridReport",
                        "Grid Report",
                        assemblyPath,
                        "RevitSuite.Host.Commands.GridReportCommand")
                    {
                        ToolTip = "Export grid line geometry details across host and linked models."
                    });
                SetButtonIcon(gridReportButton, "GR", Color.FromRgb(0xF2, 0x7F, 0x0C));

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

        private static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string panelName)
        {
            var panel = application
                .GetRibbonPanels(TabName)
                .FirstOrDefault(p => p.Name.Equals(panelName, StringComparison.OrdinalIgnoreCase));

            return panel ?? application.CreateRibbonPanel(TabName, panelName);
        }

        private static PushButton AddButton(RibbonPanel panel, PushButtonData data)
        {
            return panel.AddItem(data) as PushButton
                   ?? throw new InvalidOperationException("Failed to add ribbon button.");
        }

        private static void SetButtonIcon(PushButton button, string glyph, Color background)
        {
            button.LargeImage = RibbonIconFactory.CreateLargeIcon(glyph, background);
            button.Image = RibbonIconFactory.CreateSmallIcon(glyph, background);
        }
    }
}
