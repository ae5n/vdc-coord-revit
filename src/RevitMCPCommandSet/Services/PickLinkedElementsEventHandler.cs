// Copyright (c) 2026 sparx-fire (https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
// Licensed under the MIT License.

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class PickLinkedElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private bool _multiple = true;
        private string _prompt = "Select linked model element(s)";

        public PickedLinkedElementResult Result { get; private set; }
        public bool TaskCompleted { get; private set; }

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetSelectionParameters(bool multiple, string prompt)
        {
            _multiple = multiple;
            _prompt = string.IsNullOrWhiteSpace(prompt) ? "Select linked model element(s)" : prompt;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (uidoc == null || doc == null)
                {
                    Result = new PickedLinkedElementResult
                    {
                        Success = false,
                        Message = "No active Revit document is available."
                    };
                    return;
                }

                var references = _multiple
                    ? uidoc.Selection.PickObjects(ObjectType.LinkedElement, _prompt).ToList()
                    : new List<Reference> { uidoc.Selection.PickObject(ObjectType.LinkedElement, _prompt) };

                var elements = references
                    .Where(reference => reference != null)
                    .Select(reference => BuildLinkedElementInfo(doc, reference))
                    .Where(info => info != null)
                    .ToList();

                Result = new PickedLinkedElementResult
                {
                    Success = true,
                    Cancelled = false,
                    Count = elements.Count,
                    Elements = elements,
                    Message = $"Picked {elements.Count} linked model element(s)."
                };
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                Result = new PickedLinkedElementResult
                {
                    Success = false,
                    Cancelled = true,
                    Message = "Selection cancelled."
                };
            }
            catch (Exception ex)
            {
                Result = new PickedLinkedElementResult
                {
                    Success = false,
                    Cancelled = false,
                    Message = ex.Message
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private PickedLinkedElementInfo BuildLinkedElementInfo(Document hostDoc, Reference reference)
        {
            var linkInstance = hostDoc.GetElement(reference.ElementId) as RevitLinkInstance;
            if (linkInstance == null)
            {
                var hostElement = hostDoc.GetElement(reference.ElementId);
                return new PickedLinkedElementInfo
                {
                    HostDocumentTitle = hostDoc.Title,
                    LinkInstanceId = GetElementIdValue(reference.ElementId),
                    LinkInstanceName = hostElement?.Name
                };
            }

            var linkDoc = linkInstance.GetLinkDocument();
            var linkedElement = linkDoc?.GetElement(reference.LinkedElementId);

            return new PickedLinkedElementInfo
            {
                HostDocumentTitle = hostDoc.Title,
                LinkInstanceId = GetElementIdValue(linkInstance.Id),
                LinkInstanceUniqueId = linkInstance.UniqueId,
                LinkInstanceName = linkInstance.Name,
                LinkedDocumentTitle = linkDoc?.Title,
                LinkedElementId = GetElementIdValue(reference.LinkedElementId),
                LinkedElementUniqueId = linkedElement?.UniqueId,
                LinkedElementName = linkedElement?.Name,
                LinkedElementType = linkedElement?.GetType().FullName,
                LinkedElementCategory = linkedElement?.Category?.Name
            };
        }

        private static long GetElementIdValue(ElementId elementId)
        {
#if REVIT2024_OR_GREATER
            return elementId.Value;
#else
            return elementId.IntegerValue;
#endif
        }

        public string GetName()
        {
            return "Pick Linked Revit Elements";
        }
    }
}
