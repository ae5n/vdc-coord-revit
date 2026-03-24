using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitSuite.Host.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitSuite.Host.Mcp.Services
{
    public class NwcBatchExportMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string? OutputDirectory { get; set; }
        public string[]? ViewNames { get; set; }
        public object Result { get; private set; } = new object();

        public bool WaitForCompletion(int timeoutMs = 120000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMs);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Result = new { success = false, error = "No active document." };
                    return;
                }

                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => !v.IsTemplate)
                    .Cast<View>()
                    .ToList();

                if (allViews.Count == 0)
                {
                    Result = new { success = false, error = "No 3D views found in the document." };
                    return;
                }

                IReadOnlyList<View> views;
                if (ViewNames == null || ViewNames.Length == 0)
                {
                    views = allViews;
                }
                else
                {
                    views = allViews
                        .Where(v => ViewNames.Contains(v.Name, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (views.Count == 0)
                    {
                        var availableNames = allViews.Select(v => v.Name).OrderBy(n => n).ToArray();
                        Result = new
                        {
                            success = false,
                            error = "No views matched the specified names.",
                            availableViews = availableNames
                        };
                        return;
                    }
                }

                var runResult = NwcBatchExportCommand.RunCore(app, OutputDirectory, views);
                if (runResult == null)
                {
                    Result = new { success = false, error = "No views were exported." };
                    return;
                }

                var (targetFolder, successCount, failCount, successPaths, failedNames) = runResult.Value;
                Result = new
                {
                    success = true,
                    targetFolder,
                    successCount,
                    failCount,
                    exportedFiles = successPaths,
                    failedViews = failedNames
                };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName() => "NWC Batch Export MCP";
    }
}
