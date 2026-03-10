using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using RevitSuite.Host.UI;

namespace RevitSuite.Host
{
    public class App : IExternalApplication
    {
        private const string TabName = "Lewis VDC";
        private const string AutomationPanelName = "Automation";
        private const string ReportsPanelName = "Reports";
        private const string ViewsPanelName = "Views";
        private const string ExportsPanelName = "Exports";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                TryCreateTab(application);
                var automationPanel = GetOrCreatePanel(application, AutomationPanelName);
                var reportsPanel = GetOrCreatePanel(application, ReportsPanelName);
                var viewsPanel = GetOrCreatePanel(application, ViewsPanelName);
                var exportsPanel = GetOrCreatePanel(application, ExportsPanelName);

                var assemblyPath = Assembly.GetExecutingAssembly().Location;

                var footingZoneButton = AddButton(
                    automationPanel,
                    new PushButtonData(
                        "RevitSuite_FootingZone",
                        "Footing Zones",
                        assemblyPath,
                        "RevitSuite.Host.Commands.FootingZoneCommand")
                    {
                        ToolTip = "Create transparent influence zones for foundations and selected slabs.",
                        LongDescription = "Generate transparent influence zones around foundations and slabs using schema defaults."
                    });
                ApplyIcons(footingZoneButton, RibbonIconFactory.FootingZones);

                var qaqcButton = AddButton(
                    automationPanel,
                    new PushButtonData(
                        "RevitSuite_QAQC",
                        "QA/QC",
                        assemblyPath,
                        "RevitSuite.Host.Commands.QaqcCommand")
                    {
                        ToolTip = "QA/QC - Verify field survey measurements against model coordinates.",
                        LongDescription = "Export control points to CSV template, import field data from layout team, calculate deviations, and visualize results with color-coding and deviation indicators."
                    });
                ApplyIcons(qaqcButton, RibbonIconFactory.QAQC);

                var reportsDropdown = AddPulldownButton(
                    reportsPanel,
                    new PulldownButtonData("RevitSuite_ModelReports", "Model Reports")
                    {
                        ToolTip = "Export CSV reports for levels and grids from a single menu.",
                        LongDescription = "Access all model reports from one dropdown. Each report uses schema defaults for filtering and formatting."
                    });
                ApplyIcons(reportsDropdown, RibbonIconFactory.ReportsHub);

                var levelReportButton = AddButton(
                    reportsDropdown,
                    new PushButtonData(
                        "RevitSuite_LevelReport",
                        "Level Report",
                        assemblyPath,
                        "RevitSuite.Host.Commands.LevelReportCommand")
                    {
                        ToolTip = "Export a sorted CSV of levels across host and linked models.",
                        LongDescription = "Collect host and linked level data using schema-driven filters. Output is sorted by model then elevation for quick QA."
                    });
                ApplyIcons(levelReportButton, RibbonIconFactory.LevelReport);

                var gridReportButton = AddButton(
                    reportsDropdown,
                    new PushButtonData(
                        "RevitSuite_GridReport",
                        "Grid Report",
                        assemblyPath,
                        "RevitSuite.Host.Commands.GridReportCommand")
                    {
                        ToolTip = "Export grid line geometry details across host and linked models.",
                        LongDescription = "Capture grid names, origins, and orientations from host plus linked models using schema defaults."
                    });
                ApplyIcons(gridReportButton, RibbonIconFactory.GridReport);

                var sharedCoordinatesReportButton = AddButton(
                    reportsDropdown,
                    new PushButtonData(
                        "RevitSuite_SharedCoordinatesReport",
                        "Shared Coordinates",
                        assemblyPath,
                        "RevitSuite.Host.Commands.SharedCoordinatesReportCommand")
                    {
                        ToolTip = "Export shared coordinate data for host and linked models.",
                        LongDescription =
                            "Report project base point and survey point values for the host model plus any loaded links, including shared offsets."
                    });
                ApplyIcons(sharedCoordinatesReportButton, RibbonIconFactory.SharedCoordinatesReport);

                var nwcBatchExportButton = AddButton(
                    exportsPanel,
                    new PushButtonData(
                        "RevitSuite_NwcBatchExport",
                        "NWC Batch Export",
                        assemblyPath,
                        "RevitSuite.Host.Commands.NwcBatchExportCommand")
                    {
                        ToolTip = "Export selected 3D views or entire view sets to Navisworks NWC files.",
                        LongDescription = "Choose view-type groupings and individual 3D views, then export them to NWC with shared coordinates and link conversion enabled."
                    });
                ApplyIcons(nwcBatchExportButton, RibbonIconFactory.NwcBatchExport);

                var copyLinkedViewsButton = AddButton(
                    viewsPanel,
                    new PushButtonData(
                        "RevitSuite_CopyLinkedViews",
                        "Copy Linked Views",
                        assemblyPath,
                        "RevitSuite.Host.Commands.CopyLinkedViewsCommand")
                    {
                        ToolTip = "Copy plan and 3D views from linked models into the host project.",
                        LongDescription = "Select loaded link models, filter by supported view types, and copy either individual views or entire view sets into the host document."
                    });
                ApplyIcons(copyLinkedViewsButton, RibbonIconFactory.CopyLinkedViews);

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

        private static PushButton AddButton(PulldownButton dropdown, PushButtonData data)
        {
            return dropdown.AddPushButton(data) as PushButton
                   ?? throw new InvalidOperationException("Failed to add dropdown button.");
        }

        private static PulldownButton AddPulldownButton(RibbonPanel panel, PulldownButtonData data)
        {
            return panel.AddItem(data) as PulldownButton
                   ?? throw new InvalidOperationException("Failed to add ribbon dropdown.");
        }

        private static void ApplyIcons(PushButton button, RibbonIconFactory.IconSet icons)
        {
            button.LargeImage = icons.LargeImage;
            button.Image = icons.SmallImage;
        }

        private static void ApplyIcons(PulldownButton button, RibbonIconFactory.IconSet icons)
        {
            button.LargeImage = icons.LargeImage;
            button.Image = icons.SmallImage;
        }
    }
}
