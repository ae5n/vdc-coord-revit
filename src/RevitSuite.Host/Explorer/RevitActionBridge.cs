using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// Marshals work from the modeless Explorer window onto the Revit API thread.
    /// Independent of the MCP ExternalEventManager, which only initializes when the
    /// MCP socket server is started — the Explorer must work without MCP running.
    /// The ExternalEvent is created lazily from a valid API context (the launching command).
    /// </summary>
    internal sealed class RevitActionBridge : IExternalEventHandler
    {
        private static RevitActionBridge? _instance;

        private readonly ConcurrentQueue<Action<UIApplication>> _queue =
            new ConcurrentQueue<Action<UIApplication>>();

        private ExternalEvent? _event;

        private RevitActionBridge()
        {
        }

        public static RevitActionBridge Instance => _instance ??= new RevitActionBridge();

        /// <summary>Must be called from a valid Revit API context (e.g. IExternalCommand.Execute).</summary>
        public void EnsureEventCreated()
        {
            _event ??= ExternalEvent.Create(this);
        }

        /// <summary>
        /// Queues an action to run on the Revit API thread. The action must not touch WPF
        /// controls directly; marshal results back via Dispatcher from inside the action.
        /// </summary>
        public void Post(Action<UIApplication> action)
        {
            if (_event == null)
            {
                throw new InvalidOperationException("RevitActionBridge event has not been created.");
            }

            _queue.Enqueue(action);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action(app);
                }
                catch (Exception ex)
                {
                    LogManager.Error("explorer-bridge", "Explorer bridge action failed.", ex);
                }
            }
        }

        public string GetName() => "RevitSuite Model Explorer Bridge";
    }

    /// <summary>Revit-thread helpers used by bridge actions. API context required.</summary>
    internal static class RevitActions
    {
        public static ICollection<ElementId> ToElementIds(IEnumerable<long> idValues) =>
            idValues.Select(value => new ElementId(value)).ToList();

        /// <summary>An element that lives inside a Revit link, addressed via its link instance.</summary>
        public sealed record LinkedTarget(long LinkInstanceIdValue, long ElementIdValue);

        /// <summary>
        /// Sets the Revit selection to the given host-model element ids.
        /// Returns the number of ids that resolved to live elements.
        /// </summary>
        public static int SelectElements(UIDocument uidoc, IReadOnlyCollection<long> idValues)
        {
            var valid = FilterToLiveElements(uidoc.Document, idValues);
            uidoc.Selection.SetElementIds(valid);
            return valid.Count;
        }

        /// <summary>
        /// Selects host and linked elements together. Linked elements are selected through
        /// link references (Revit 2023+); host elements become references too so one
        /// SetReferences call covers everything.
        /// </summary>
        public static (int Selected, int Failed, string? Error) SelectMixed(
            UIDocument uidoc,
            IReadOnlyCollection<long> hostIds,
            IReadOnlyCollection<LinkedTarget> linkedTargets)
        {
            if (linkedTargets.Count == 0)
            {
                return (SelectElements(uidoc, hostIds), 0, null);
            }

            var references = BuildReferences(uidoc.Document, hostIds, linkedTargets, out var failed);
            if (references.Count == 0)
            {
                return (0, failed, "None of the elements could be resolved for selection.");
            }

            try
            {
                uidoc.Selection.SetReferences(references);
                return (references.Count, failed, null);
            }
            catch (Exception ex)
            {
                LogManager.Error("explorer", "Mixed selection failed.", ex);
                return (0, failed + references.Count, $"Selection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows host and linked elements. Pure-host falls back to Revit's ShowElements
        /// (which can open a suitable view); with linked elements involved, everything is
        /// selected via references and the active view zooms to the combined bounding box
        /// (linked boxes transformed by their link instance).
        /// </summary>
        public static (int Shown, string? Error) ShowMixed(
            UIDocument uidoc,
            IReadOnlyCollection<long> hostIds,
            IReadOnlyCollection<LinkedTarget> linkedTargets)
        {
            if (linkedTargets.Count == 0)
            {
                return ShowElements(uidoc, hostIds);
            }

            var doc = uidoc.Document;
            var references = BuildReferences(doc, hostIds, linkedTargets, out _);
            if (references.Count == 0)
            {
                return (0, "None of the elements could be resolved.");
            }

            XYZ? min = null, max = null;
            void Extend(BoundingBoxXYZ? box, Transform? transform)
            {
                if (box == null)
                {
                    return;
                }

                foreach (var corner in Corners(box))
                {
                    var point = transform?.OfPoint(corner) ?? corner;
                    min = min == null
                        ? point
                        : new XYZ(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                    max = max == null
                        ? point
                        : new XYZ(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
                }
            }

            foreach (var id in hostIds)
            {
                Extend(doc.GetElement(new ElementId(id))?.get_BoundingBox(null), null);
            }

            foreach (var target in linkedTargets)
            {
                if (doc.GetElement(new ElementId(target.LinkInstanceIdValue)) is not RevitLinkInstance link)
                {
                    continue;
                }

                var linkedElement = link.GetLinkDocument()?.GetElement(new ElementId(target.ElementIdValue));
                Extend(linkedElement?.get_BoundingBox(null), link.GetTotalTransform());
            }

            try
            {
                uidoc.Selection.SetReferences(references);

                if (min != null && max != null)
                {
                    var uiView = uidoc.GetOpenUIViews()
                        .FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
                    // Pad the box slightly so elements aren't glued to the viewport edge.
                    var padding = Math.Max(1.0, max.DistanceTo(min) * 0.1);
                    uiView?.ZoomAndCenterRectangle(
                        new XYZ(min.X - padding, min.Y - padding, min.Z - padding),
                        new XYZ(max.X + padding, max.Y + padding, max.Z + padding));
                }

                return (references.Count, null);
            }
            catch (Exception ex)
            {
                LogManager.Error("explorer", "Show linked elements failed.", ex);
                return (0, $"Could not show linked elements: {ex.Message}");
            }
        }

        private static IList<Reference> BuildReferences(
            Document doc,
            IReadOnlyCollection<long> hostIds,
            IReadOnlyCollection<LinkedTarget> linkedTargets,
            out int failed)
        {
            var references = new List<Reference>();
            failed = 0;

            foreach (var id in hostIds)
            {
                var element = doc.GetElement(new ElementId(id));
                if (element == null)
                {
                    failed++;
                    continue;
                }

                try
                {
                    references.Add(new Reference(element));
                }
                catch
                {
                    failed++;
                }
            }

            foreach (var target in linkedTargets)
            {
                try
                {
                    if (doc.GetElement(new ElementId(target.LinkInstanceIdValue)) is not RevitLinkInstance link)
                    {
                        failed++;
                        continue;
                    }

                    var linkedElement = link.GetLinkDocument()?.GetElement(new ElementId(target.ElementIdValue));
                    if (linkedElement == null)
                    {
                        failed++;
                        continue;
                    }

                    references.Add(new Reference(linkedElement).CreateLinkReference(link));
                }
                catch
                {
                    failed++;
                }
            }

            return references;
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            var boxTransform = box.Transform ?? Transform.Identity;
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
            {
                yield return boxTransform.OfPoint(new XYZ(x, y, z));
            }
        }

        /// <summary>Selects and zooms to elements. Returns (shownCount, error message or null).</summary>
        public static (int Shown, string? Error) ShowElements(UIDocument uidoc, IReadOnlyCollection<long> idValues)
        {
            var valid = FilterToLiveElements(uidoc.Document, idValues);
            if (valid.Count == 0)
            {
                return (0, "None of the elements exist in the host model anymore.");
            }

            try
            {
                uidoc.Selection.SetElementIds(valid);
                uidoc.ShowElements(valid);
                return (valid.Count, null);
            }
            catch (Exception ex)
            {
                return (0, $"Revit could not show these elements: {ex.Message}");
            }
        }

        public static DeletePreflight BuildDeletePreflight(Document doc, IReadOnlyCollection<long> idValues)
        {
            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var viewSpecific = 0;
            var pinned = 0;
            var ownedByOthers = 0;
            var total = 0;
            var isWorkshared = doc.IsWorkshared;
            var currentUser = isWorkshared ? doc.Application.Username : null;

            foreach (var idValue in idValues)
            {
                var element = doc.GetElement(new ElementId(idValue));
                if (element == null)
                {
                    continue;
                }

                total++;
                var category = element.Category?.Name ?? "(No Category)";
                categoryCounts[category] = categoryCounts.TryGetValue(category, out var count) ? count + 1 : 1;

                if (element.ViewSpecific)
                {
                    viewSpecific++;
                }

                if (element.Pinned)
                {
                    pinned++;
                }

                if (isWorkshared)
                {
                    try
                    {
                        var owner = WorksharingUtils.GetWorksharingTooltipInfo(doc, element.Id)?.Owner;
                        if (!string.IsNullOrEmpty(owner) &&
                            !string.Equals(owner, currentUser, StringComparison.OrdinalIgnoreCase))
                        {
                            ownedByOthers++;
                        }
                    }
                    catch
                    {
                        // Worksharing info is advisory; ignore failures.
                    }
                }
            }

            var (dependentCount, dependentCategories) = AnalyzeDependents(doc, idValues);
            return new DeletePreflight(
                total, categoryCounts, viewSpecific, pinned, ownedByOthers, isWorkshared,
                dependentCount, dependentCategories);
        }

        /// <summary>
        /// Trial-deletes inside a transaction that is always rolled back, so Revit reports the
        /// full cascade (tags, dimensions, hosted elements) without changing the model. After
        /// rollback the dependents exist again, so their categories can be enumerated for the
        /// confirmation dialog. Count is -1 when the preview itself fails.
        /// </summary>
        private static (int Count, IReadOnlyDictionary<string, int> Categories) AnalyzeDependents(
            Document doc,
            IReadOnlyCollection<long> idValues)
        {
            var empty = new Dictionary<string, int>();
            var requested = FilterToLiveElements(doc, idValues);
            if (requested.Count == 0)
            {
                return (0, empty);
            }

            var requestedSet = new HashSet<long>(requested.Select(id => id.Value));
            List<long> dependentIds;

            using (var tx = new Transaction(doc, "Model Explorer - Delete Preview"))
            {
                try
                {
                    tx.Start();
                    var deleted = doc.Delete(requested);
                    dependentIds = (deleted ?? (ICollection<ElementId>)Array.Empty<ElementId>())
                        .Select(id => id.Value)
                        .Where(value => !requestedSet.Contains(value))
                        .ToList();
                }
                catch
                {
                    return (-1, empty);
                }
                finally
                {
                    if (tx.HasStarted() && !tx.HasEnded())
                    {
                        tx.RollBack();
                    }
                }
            }

            var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var idValue in dependentIds)
            {
                string name;
                try
                {
                    name = doc.GetElement(new ElementId(idValue))?.Category?.Name ?? "(No Category)";
                }
                catch
                {
                    name = "(No Category)";
                }

                categories[name] = categories.TryGetValue(name, out var count) ? count + 1 : 1;
            }

            return (dependentIds.Count, categories);
        }

        /// <summary>Deletes elements inside a transaction. Returns (deletedCount, error message or null).</summary>
        public static (int Deleted, string? Error) DeleteElements(Document doc, IReadOnlyCollection<long> idValues)
        {
            var valid = FilterToLiveElements(doc, idValues);
            if (valid.Count == 0)
            {
                return (0, "None of the elements exist in the host model anymore.");
            }

            using var tx = new Transaction(doc, "Model Explorer - Delete Elements");
            try
            {
                tx.Start();
                var deleted = doc.Delete(valid);
                tx.Commit();
                return (deleted?.Count ?? valid.Count, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                return (0, $"Delete failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Temporarily isolates elements in the active view. Temporary view modes are still
        /// model changes as far as the API is concerned, so this must run inside a Transaction.
        /// </summary>
        public static (int Isolated, string? Error) IsolateElements(UIDocument uidoc, IReadOnlyCollection<long> idValues)
        {
            var view = uidoc.ActiveView;
            if (!SupportsTemporaryModes(view))
            {
                return (0, $"View '{view.Name}' does not support temporary hide/isolate (sheets, schedules, and locked view templates don't).");
            }

            var valid = FilterToLiveElements(uidoc.Document, idValues);
            if (valid.Count == 0)
            {
                return (0, "None of the elements exist in the host model anymore.");
            }

            using var tx = new Transaction(uidoc.Document, "Model Explorer - Isolate Elements");
            try
            {
                tx.Start();
                view.IsolateElementsTemporary(valid);
                tx.Commit();
                return (valid.Count, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Isolate failed in view '{view.Name}'.", ex);
                return (0, $"Could not isolate in the active view: {ex.Message}");
            }
        }

        /// <summary>Ends temporary hide/isolate in the active view (transaction required, as above).</summary>
        public static string? ResetIsolate(UIDocument uidoc)
        {
            var view = uidoc.ActiveView;
            if (!SupportsTemporaryModes(view))
            {
                return $"View '{view.Name}' does not support temporary view modes.";
            }

            using var tx = new Transaction(uidoc.Document, "Model Explorer - Reset Isolate");
            try
            {
                tx.Start();
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
                return null;
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Reset isolate failed in view '{view.Name}'.", ex);
                return $"Could not reset isolate: {ex.Message}";
            }
        }

        private static bool SupportsTemporaryModes(View view)
        {
            try
            {
                return view.CanUseTemporaryVisibilityModes();
            }
            catch
            {
                return false;
            }
        }

        public static string? OpenView(UIDocument uidoc, long viewIdValue)
        {
            if (uidoc.Document.GetElement(new ElementId(viewIdValue)) is not View view)
            {
                return "The view no longer exists in the model.";
            }

            try
            {
                // RequestViewChange is safe from modeless contexts; ActiveView setter is not.
                uidoc.RequestViewChange(view);
                return null;
            }
            catch (Exception ex)
            {
                return $"Could not open view '{view.Name}': {ex.Message}";
            }
        }

        private static ICollection<ElementId> FilterToLiveElements(Document doc, IEnumerable<long> idValues)
        {
            return idValues
                .Select(value => new ElementId(value))
                .Where(id => doc.GetElement(id) != null)
                .ToList();
        }
    }
}
