using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Explorer.UI;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ModelExplorerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "ModelExplorerCommand started.");

            try
            {
                if (data.Application.ActiveUIDocument == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                ExplorerWindow.ShowWindow(data.Application);
                LogManager.Info(correlationId, "Model Explorer window opened.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Model Explorer failed to open. See log for details.");
                LogManager.Error(correlationId, "ModelExplorerCommand failed.", ex);
                return Result.Failed;
            }
        }
    }
}
