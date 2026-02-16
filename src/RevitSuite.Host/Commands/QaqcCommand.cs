using System;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public partial class QaqcCommand : IExternalCommand
    {
        private const string SogCategoryName = "Floor - Slab on Grade";
        private const string ReadyPointsCategoryName = "Ready Points (CSV)";

        // Shared parameter GUIDs
        private static readonly Guid PointNumberGuid = Guid.Parse("7b436883-9c3e-4a23-b014-f3ed5c5cf91d");
        private static readonly Guid CsEastingGuid = Guid.Parse("8e5a10e0-7c84-443f-a368-985247c7cd95");
        private static readonly Guid CsNorthingGuid = Guid.Parse("99272d59-c0c6-47b9-982e-48da2ff7b42f");
        private static readonly Guid CsElevationGuid = Guid.Parse("750d5407-b38e-4955-9338-30ec456be859");
        private static readonly Guid DeviationEastingGuid = Guid.Parse("3ed84adf-d4e6-4b84-8ea5-367762e5052e");
        private static readonly Guid DeviationNorthingGuid = Guid.Parse("3bf56bcc-b5f9-4ed6-97a3-b50e78d0d574");

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "QaqcCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var doc = uiDoc.Document;
                var config = QaqcConfig.Load();

                string selectedCategory;
                QaqcMode selectedMode;
                int selectedPourNumber;
                double selectedHorizontalVerifiedThreshold;
                double selectedHorizontalCriticalThreshold;
                double selectedElevationVerifiedThreshold;
                double selectedElevationCriticalThreshold;
                bool selectedUseHorizontalThreshold;
                bool selectedUseElevationThreshold;
                bool selectedUseSelectedPointThresholds;
                PairingMode selectedPairingMode;
                double selectedTagOffsetEast;
                double selectedTagOffsetNorth;
                using (var form = new QaqcDialog())
                {
                    if (form.ShowDialog() != DialogResult.OK)
                    {
                        LogManager.Info(correlationId, "QAQC cancelled by user.");
                        return Result.Cancelled;
                    }

                    selectedCategory = form.SelectedCategory;
                    selectedMode = form.SelectedMode;
                    selectedPourNumber = form.SelectedPourNumber;
                    selectedHorizontalVerifiedThreshold = form.SelectedHorizontalVerifiedThreshold;
                    selectedHorizontalCriticalThreshold = form.SelectedHorizontalCriticalThreshold;
                    selectedElevationVerifiedThreshold = form.SelectedElevationVerifiedThreshold;
                    selectedElevationCriticalThreshold = form.SelectedElevationCriticalThreshold;
                    selectedUseHorizontalThreshold = form.SelectedUseHorizontalThreshold;
                    selectedUseElevationThreshold = form.SelectedUseElevationThreshold;
                    selectedUseSelectedPointThresholds = form.SelectedUseSelectedPointThresholds;
                    selectedPairingMode = form.SelectedPairingMode;
                    selectedTagOffsetEast = form.SelectedTagOffsetEast;
                    selectedTagOffsetNorth = form.SelectedTagOffsetNorth;
                }

                LogManager.Info(correlationId, $"QAQC mode: {selectedMode}, Category: {selectedCategory}");

                if (selectedMode == QaqcMode.Place)
                {
                    return ExecutePlace(correlationId, uiDoc, doc, config, selectedCategory, selectedPourNumber);
                }

                if (selectedMode == QaqcMode.Export)
                {
                    if (string.Equals(selectedCategory, ReadyPointsCategoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        TaskDialog.Show("RevitSuite", "Export is not used for Ready Points workflow.");
                        LogManager.Info(correlationId, "Export skipped for Ready Points category.");
                        return Result.Cancelled;
                    }

                    return ExecuteExport(correlationId, uiDoc, doc, config, selectedCategory);
                }

                return ExecuteImport(
                    correlationId,
                    uiDoc,
                    doc,
                    config,
                    selectedCategory,
                    selectedHorizontalVerifiedThreshold,
                    selectedHorizontalCriticalThreshold,
                    selectedElevationVerifiedThreshold,
                    selectedElevationCriticalThreshold,
                    selectedUseHorizontalThreshold,
                    selectedUseElevationThreshold,
                    selectedUseSelectedPointThresholds,
                    selectedPairingMode,
                    selectedTagOffsetEast,
                    selectedTagOffsetNorth);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                LogManager.Error(correlationId, "QAQC command failed.", ex);
                TaskDialog.Show("RevitSuite", $"QAQC failed: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
