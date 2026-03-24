using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;
using RevitSuite.Host.UI;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class NwcBatchExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "NwcBatchExportCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var document = uiDoc.Document;
                var config = NwcBatchExportConfig.Load();
                var viewGroups = BuildViewGroups(document, config.GroupParameterName);

                if (viewGroups.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No non-template 3D views found to export.");
                    LogManager.Warn(correlationId, "Batch export aborted: no 3D views available.");
                    return Result.Cancelled;
                }

                var defaultFolder = ResolveDefaultFolder(document);

                using var form = new NwcBatchExportForm(viewGroups, defaultFolder);
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    LogManager.Info(correlationId, "Batch export cancelled by user.");
                    return Result.Cancelled;
                }

                var targetFolder = form.TargetFolder;
                var selectedViews = form.SelectedViews;

                if (selectedViews.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No views were selected for export.");
                    LogManager.Warn(correlationId, "Batch export cancelled: no selected views.");
                    return Result.Cancelled;
                }

                var runResult = RunCore(data.Application, targetFolder, selectedViews);
                if (runResult == null)
                {
                    TaskDialog.Show("RevitSuite", "No views were exported.");
                    return Result.Cancelled;
                }

                var (resolvedFolder, successCount, failCount, successPaths, failedNames) = runResult.Value;
                var summary = BuildSummaryMessage(resolvedFolder, successCount, failCount, failedNames);
                TaskDialog.Show("RevitSuite - NWC Export", summary);

                LogManager.Info(correlationId,
                    $"NWC batch export finished. Success={successCount}, Failed={failCount}, Folder='{resolvedFolder}'");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Batch NWC export failed. See log for details.");
                LogManager.Error(correlationId, "NwcBatchExportCommand failed.", ex);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Executes the core NWC export logic without any UI dialogs.
        /// Returns null if no views were available; throws on error.
        /// </summary>
        internal static (string targetFolder, int successCount, int failCount, IReadOnlyList<string> successPaths, IReadOnlyList<string> failedViewNames)? RunCore(
            UIApplication app,
            string? outputDirectory,
            IReadOnlyList<View> views)
        {
            var document = (app.ActiveUIDocument
                ?? throw new InvalidOperationException("No active document.")).Document;

            if (views.Count == 0)
                return null;

            var correlationId = Guid.NewGuid().ToString("N");
            var config = NwcBatchExportConfig.Load();

            var targetFolder = string.IsNullOrWhiteSpace(outputDirectory)
                ? ResolveDefaultFolder(document)
                : outputDirectory;
            Directory.CreateDirectory(targetFolder);

            var results = ExportViews(document, targetFolder, views, config, correlationId);

            LogManager.Info(correlationId,
                $"NWC batch export finished. Success={results.Successful.Count}, Failed={results.Failed.Count}, Folder='{targetFolder}'");

            return (
                targetFolder,
                results.Successful.Count,
                results.Failed.Count,
                results.Successful.Select(r => r.Path).ToList(),
                results.Failed.Select(r => r.View.Name).ToList()
            );
        }

        private static List<NwcBatchExportForm.ViewGroup> BuildViewGroups(Document document, string groupParameterName)
        {
            var views = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && view.ViewType == ViewType.ThreeD)
                .ToList();

            var groups = views
                .GroupBy(view => GetViewGroupName(view, groupParameterName), StringComparer.OrdinalIgnoreCase)
                .Select(group => new NwcBatchExportForm.ViewGroup(group.Key, group.ToList()))
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return groups;
        }

        private static string GetViewGroupName(View view, string groupParameterName)
        {
            if (string.IsNullOrWhiteSpace(groupParameterName))
            {
                return "Uncategorized";
            }

            foreach (Parameter parameter in view.Parameters)
            {
                if (TryMatchParameter(parameter, groupParameterName, out var value))
                {
                    return value;
                }
            }

            return "Uncategorized";
        }

        private static string ResolveDefaultFolder(Document document)
        {
            if (!string.IsNullOrWhiteSpace(document.PathName))
            {
                var directory = Path.GetDirectoryName(document.PathName);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        private static bool TryMatchParameter(Parameter parameter, string groupParameterName, out string value)
        {
            value = string.Empty;

            try
            {
                if (!parameter.HasValue || parameter.StorageType != StorageType.String)
                {
                    return false;
                }

                var definitionName = parameter.Definition?.Name;
                if (!string.IsNullOrWhiteSpace(definitionName)
                    && definitionName.Equals(groupParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = parameter.AsString();
                    if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        value = candidate.Trim();
                        return true;
                    }
                }
            }
            catch
            {
                // ignore parameter exceptions
            }

            return false;
        }

        private static ExportResults ExportViews(
            Document document,
            string targetFolder,
            IReadOnlyList<View> views,
            NwcBatchExportConfig config,
            string correlationId)
        {
            var successful = new List<ExportResult>();
            var failed = new List<ExportResult>();

            foreach (var view in views)
            {
                try
                {
                    var fileName = $"{SanitizeFileName(view.Name)}.nwc";
                    var fullPath = Path.Combine(targetFolder, fileName);

                    LogManager.Info(correlationId, $"Exporting view '{view.Name}' to '{fullPath}'.");

                    var options = CreateNavisworksOptions(view.Id, config);
                    document.Export(targetFolder, fileName, options);

                    if (File.Exists(fullPath))
                    {
                        var fileSize = new FileInfo(fullPath).Length;
                        successful.Add(new ExportResult(view, fullPath, fileSize));
                    }
                    else
                    {
                        failed.Add(new ExportResult(view, fullPath, 0));
                        LogManager.Warn(correlationId, $"Export failed for view '{view.Name}' (file missing after export).");
                    }
                }
                catch (Autodesk.Revit.Exceptions.OptionalFunctionalityNotAvailableException ex)
                {
                    failed.Add(new ExportResult(view, string.Empty, 0));
                    var message = "Navisworks Exporter is not available in this Revit installation.";
                    LogManager.Error(correlationId, message, ex);
                    TaskDialog.Show("RevitSuite - NWC Export", message);
                    break;
                }
                catch (Exception ex)
                {
                    failed.Add(new ExportResult(view, string.Empty, 0));
                    LogManager.Error(correlationId, $"Export failed for view '{view.Name}'.", ex);
                }
            }

            return new ExportResults(successful, failed);
        }

        private static NavisworksExportOptions CreateNavisworksOptions(ElementId viewId, NwcBatchExportConfig config)
        {
            var options = new NavisworksExportOptions
            {
                ViewId = viewId,
                ExportScope = config.ExportScope,
                Coordinates = config.Coordinates,
                ExportLinks = config.ExportLinks,
                ExportParts = config.ExportParts,
                ExportElementIds = config.ExportElementIds,
                ConvertElementProperties = config.ConvertElementProperties,
                ExportRoomAsAttribute = config.ExportRoomAsAttribute,
                ExportRoomGeometry = config.ExportRoomGeometry,
                ExportUrls = config.ExportUrls,
                DivideFileIntoLevels = config.DivideFileIntoLevels,
                FindMissingMaterials = config.FindMissingMaterials,
                ConvertLights = config.ConvertLights,
                ConvertLinkedCADFormats = config.ConvertLinkedCadFormats,
                FacetingFactor = config.FacetingFactor
            };

            options.Parameters = config.Parameters;

            return options;
        }

        private static string BuildSummaryMessage(string folder, int successCount, int failCount, IReadOnlyList<string> failedViewNames)
        {
            var summary = $"Export complete.{Environment.NewLine}{Environment.NewLine}" +
                          $"Successful: {successCount}{Environment.NewLine}" +
                          $"Failed: {failCount}{Environment.NewLine}{Environment.NewLine}" +
                          $"Folder: {folder}";

            if (failCount > 0)
            {
                var failedNamesStr = string.Join(Environment.NewLine, failedViewNames.Select(n => $"• {n}"));
                summary += $"{Environment.NewLine}{Environment.NewLine}Failed Views:{Environment.NewLine}{failedNamesStr}";
            }

            return summary;
        }

        private static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = name
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray();

            var result = new string(sanitized).Trim();
            return string.IsNullOrWhiteSpace(result) ? "View" : result;
        }

        private sealed class ExportResult
        {
            public ExportResult(View view, string path, long size)
            {
                View = view;
                Path = path;
                Size = size;
            }

            public View View { get; }
            public string Path { get; }
            public long Size { get; }
        }

        private sealed class ExportResults
        {
            public ExportResults(IReadOnlyList<ExportResult> successful, IReadOnlyList<ExportResult> failed)
            {
                Successful = successful;
                Failed = failed;
            }

            public IReadOnlyList<ExportResult> Successful { get; }
            public IReadOnlyList<ExportResult> Failed { get; }
        }
    }
}
