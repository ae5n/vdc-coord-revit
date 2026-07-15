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

        private static volatile bool _executing;
        private static long _lastExecuteEndTicks;

        private RevitActionBridge()
        {
        }

        /// <summary>
        /// True while a bridge action is running on the Revit thread (or just finished),
        /// so DocumentChanged events raised by the Explorer's own operations can be told
        /// apart from genuine user edits. Transactions committed inside a bridge action
        /// raise DocumentChanged synchronously during Execute; the trailing window mops
        /// up any post-commit regeneration events.
        /// </summary>
        internal static bool IsSelfEcho =>
            _executing ||
            DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref _lastExecuteEndTicks)
                < TimeSpan.FromMilliseconds(500).Ticks;

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
            _executing = true;
            try
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
            finally
            {
                _executing = false;
                System.Threading.Interlocked.Exchange(ref _lastExecuteEndTicks, DateTime.UtcNow.Ticks);
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

            var (min, max) = ComputeUnionBounds(doc, hostIds, linkedTargets);

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

        /// <summary>
        /// Union bounding box (model coordinates) of host elements and linked elements
        /// (link boxes transformed by their instance). Returns (null, null) when nothing resolves.
        /// </summary>
        internal static (XYZ? Min, XYZ? Max) ComputeUnionBounds(
            Document doc,
            IReadOnlyCollection<long> hostIds,
            IReadOnlyCollection<LinkedTarget> linkedTargets)
        {
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

            return (min, max);
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

        // ---------- Focus mode (Forma-style "show me just this, in context") ----------

        /// <summary>Per-view state needed to undo a Focus: prior section box + links we hid.</summary>
        private sealed class FocusState
        {
            public BoundingBoxXYZ? PreviousSectionBox;
            public bool SectionBoxWasActive;
            public readonly List<ElementId> HiddenLinks = new List<ElementId>();
        }

        private static readonly Dictionary<long, FocusState> FocusStates = new Dictionary<long, FocusState>();

        /// <summary>
        /// Focuses the active 3D view on the given elements: a padded section box around their
        /// union bounds (linked geometry included via its instance transform), and — when the
        /// selection involves linked elements — hides link instances that are not involved.
        /// Per-element isolation inside a link is a Revit platform limit; this is the closest
        /// equivalent and matches Forma's "focus" feel. Undo with <see cref="ResetFocus"/>.
        /// </summary>
        public static string? FocusOnSelection(
            UIDocument uidoc,
            IReadOnlyCollection<long> hostIds,
            IReadOnlyCollection<LinkedTarget> linkedTargets)
        {
            var doc = uidoc.Document;
            if (uidoc.ActiveView is not View3D view3D)
            {
                return "Focus needs an active 3D view (it uses the section box). Open a 3D view and try again.";
            }

            var (min, max) = ComputeUnionBounds(doc, hostIds, linkedTargets);
            if (min == null || max == null)
            {
                return "None of the elements could be located in the model.";
            }

            using var tx = new Transaction(doc, "Model Explorer - Focus");
            try
            {
                tx.Start();

                // Capture restore state once per view; repeated Focus calls refine, one Reset undoes all.
                if (!FocusStates.TryGetValue(view3D.Id.Value, out var state))
                {
                    state = new FocusState
                    {
                        SectionBoxWasActive = view3D.IsSectionBoxActive,
                        PreviousSectionBox = view3D.IsSectionBoxActive ? view3D.GetSectionBox() : null
                    };
                    FocusStates[view3D.Id.Value] = state;
                }

                var padding = Math.Max(2.0, min.DistanceTo(max) * 0.08);
                view3D.SetSectionBox(new BoundingBoxXYZ
                {
                    Min = new XYZ(min.X - padding, min.Y - padding, min.Z - padding),
                    Max = new XYZ(max.X + padding, max.Y + padding, max.Z + padding)
                });

                if (linkedTargets.Count > 0)
                {
                    var involvedLinks = new HashSet<long>(linkedTargets.Select(t => t.LinkInstanceIdValue));
                    var toHide = new FilteredElementCollector(doc, view3D.Id)
                        .OfClass(typeof(RevitLinkInstance))
                        .Where(link => !involvedLinks.Contains(link.Id.Value) && link.CanBeHidden(view3D))
                        .Select(link => link.Id)
                        .ToList();

                    if (toHide.Count > 0)
                    {
                        view3D.HideElements(toHide);
                        state.HiddenLinks.AddRange(toHide);
                    }
                }

                tx.Commit();
                return null;
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Focus failed in view '{view3D.Name}'.", ex);
                return $"Could not focus (the view template may lock the section box): {ex.Message}";
            }
        }

        /// <summary>Restores the section box and link visibility captured by the first Focus in this view.</summary>
        public static string? ResetFocus(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            if (uidoc.ActiveView is not View3D view3D)
            {
                return "Reset Focus works in the 3D view that was focused.";
            }

            if (!FocusStates.TryGetValue(view3D.Id.Value, out var state))
            {
                // No recorded focus (window reopened / new Revit session — Focus hides are
                // saved with the model, memory is not): clear the section box AND restore
                // every hidden link, since hiding links is the other half of Focus.
                using var fallbackTx = new Transaction(doc, "Model Explorer - Reset Focus");
                try
                {
                    fallbackTx.Start();
                    view3D.IsSectionBoxActive = false;

                    var hiddenLinks = new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance))
                        .Where(instance => SafeIsHidden(instance, view3D))
                        .Select(instance => instance.Id)
                        .ToList();
                    if (hiddenLinks.Count > 0)
                    {
                        view3D.UnhideElements(hiddenLinks);
                    }

                    fallbackTx.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    if (fallbackTx.HasStarted() && !fallbackTx.HasEnded())
                    {
                        fallbackTx.RollBack();
                    }

                    return $"Could not reset focus: {ex.Message}";
                }
            }

            using var tx = new Transaction(doc, "Model Explorer - Reset Focus");
            try
            {
                tx.Start();

                var liveHidden = state.HiddenLinks
                    .Where(id => doc.GetElement(id) != null)
                    .ToList();
                if (liveHidden.Count > 0)
                {
                    view3D.UnhideElements(liveHidden);
                }

                if (state.SectionBoxWasActive && state.PreviousSectionBox != null)
                {
                    view3D.SetSectionBox(state.PreviousSectionBox);
                }
                else
                {
                    view3D.IsSectionBoxActive = false;
                }

                tx.Commit();
                FocusStates.Remove(view3D.Id.Value);
                return null;
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Reset focus failed in view '{view3D.Name}'.", ex);
                return $"Could not reset focus: {ex.Message}";
            }
        }

        // ---------- Visibility toolkit (per-view hides, Explorer-owned and undoable) ----------

        private static readonly Dictionary<long, HashSet<ElementId>> HiddenByExplorer =
            new Dictionary<long, HashSet<ElementId>>();

        private static HashSet<ElementId> HiddenSetFor(View view)
        {
            if (!HiddenByExplorer.TryGetValue(view.Id.Value, out var set))
            {
                HiddenByExplorer[view.Id.Value] = set = new HashSet<ElementId>();
            }

            return set;
        }

        /// <summary>
        /// Hides host elements in the active view, tracked so Unhide All can restore them.
        /// View.HideElements is all-or-nothing — a single un-hideable element (sketch member,
        /// element owned by another view) fails the whole batch — so failures bisect down
        /// until everything hideable is hidden and only true refusals are skipped.
        /// </summary>
        public static (int Hidden, int Skipped, string? Error) HideInView(
            UIDocument uidoc, IReadOnlyCollection<long> idValues)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            var candidates = idValues
                .Select(id => doc.GetElement(new ElementId(id)))
                .Where(e => e != null && CanHide(e!, view))
                .Select(e => e!.Id)
                .ToList();

            if (candidates.Count == 0)
            {
                return (0, 0, "Nothing hideable — the elements are already hidden or cannot be hidden in this view.");
            }

            using var tx = new Transaction(doc, "Model Explorer - Hide Elements");
            try
            {
                tx.Start();
                var hidden = new List<ElementId>();
                var skipped = 0;
                BisectingViewOp(batch => view.HideElements(batch), candidates, hidden, ref skipped);
                tx.Commit();
                HiddenSetFor(view).UnionWith(hidden);
                return (hidden.Count, skipped, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Hide failed in view '{view.Name}'.", ex);
                return (0, 0, $"Could not hide: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies an all-or-nothing view operation batch-first, bisecting failed batches so
        /// one refusing element can't block the rest. Successes accumulate in
        /// <paramref name="succeeded"/>; single refusing elements count as skipped.
        /// </summary>
        private static void BisectingViewOp(
            Action<ICollection<ElementId>> operation,
            List<ElementId> batch,
            List<ElementId> succeeded,
            ref int skipped)
        {
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                operation(batch);
                succeeded.AddRange(batch);
            }
            catch
            {
                if (batch.Count == 1)
                {
                    skipped++;
                    return;
                }

                var half = batch.Count / 2;
                BisectingViewOp(operation, batch.GetRange(0, half), succeeded, ref skipped);
                BisectingViewOp(operation, batch.GetRange(half, batch.Count - half), succeeded, ref skipped);
            }
        }

        /// <summary>Unhides specific host elements in the active view (eye toggle back on).</summary>
        public static (int Shown, string? Error) UnhideInView(UIDocument uidoc, IReadOnlyCollection<long> idValues)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            var candidates = idValues
                .Select(id => doc.GetElement(new ElementId(id)))
                .Where(e => e != null && SafeIsHidden(e!, view))
                .Select(e => e!.Id)
                .ToList();

            if (candidates.Count == 0)
            {
                return (0, "Those elements are not hidden in this view.");
            }

            using var tx = new Transaction(doc, "Model Explorer - Unhide Elements");
            try
            {
                tx.Start();
                var shown = new List<ElementId>();
                var skipped = 0;
                BisectingViewOp(batch => view.UnhideElements(batch), candidates, shown, ref skipped);
                tx.Commit();
                HiddenSetFor(view).ExceptWith(shown);
                return (shown.Count, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Unhide failed in view '{view.Name}'.", ex);
                return (0, $"Could not unhide: {ex.Message}");
            }
        }

        private static bool SafeIsHidden(Element element, View view)
        {
            try
            {
                return element.IsHidden(view);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Hides or shows entire links in the active view. A link placed multiple times is
        /// treated as one model: all placements of the same link type change together.
        /// </summary>
        public static (int Changed, string? Error) SetLinkVisibility(
            UIDocument uidoc,
            IReadOnlyCollection<long> linkInstanceIdValues,
            bool hide)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            // Expand each given instance to every placement of the same link type.
            var typeIds = linkInstanceIdValues
                .Select(id => doc.GetElement(new ElementId(id)))
                .OfType<RevitLinkInstance>()
                .Select(instance => instance.GetTypeId())
                .Where(id => id != ElementId.InvalidElementId)
                .ToHashSet();

            var targets = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Where(instance => typeIds.Contains(instance.GetTypeId()))
                .Where(instance => hide ? CanHide(instance, view) : instance.IsHidden(view))
                .Select(instance => instance.Id)
                .ToList();

            if (targets.Count == 0)
            {
                return (0, hide
                    ? "Those links are already hidden (or cannot be hidden in this view)."
                    : "Those links are not hidden in this view.");
            }

            using var tx = new Transaction(doc, hide ? "Model Explorer - Hide Links" : "Model Explorer - Show Links");
            try
            {
                tx.Start();
                if (hide)
                {
                    view.HideElements(targets);
                    HiddenSetFor(view).UnionWith(targets);
                }
                else
                {
                    view.UnhideElements(targets);
                    HiddenSetFor(view).ExceptWith(targets);
                }

                tx.Commit();
                return (targets.Count, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Link visibility change failed in view '{view.Name}'.", ex);
                return (0, $"Could not change link visibility: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores everything the Explorer hid in the active view, plus any hidden link
        /// instances (links are coordination-critical — bring them all back).
        /// </summary>
        public static (int Shown, string? Error) UnhideAllExplorerHidden(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            var toShow = HiddenSetFor(view)
                .Where(id => doc.GetElement(id) is { } element && element.IsHidden(view))
                .ToHashSet();

            foreach (var instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Where(instance => instance.IsHidden(view)))
            {
                toShow.Add(instance.Id);
            }

            var hiddenCategories = CategoryHiddenSetFor(view);
            if (toShow.Count == 0 && hiddenCategories.Count == 0)
            {
                return (0, "Nothing to unhide in this view.");
            }

            using var tx = new Transaction(doc, "Model Explorer - Unhide All");
            try
            {
                tx.Start();
                var shown = new List<ElementId>();
                var skipped = 0;
                if (toShow.Count > 0)
                {
                    BisectingViewOp(batch => view.UnhideElements(batch), toShow.ToList(), shown, ref skipped);
                }

                var categoriesRestored = 0;
                foreach (var categoryIdValue in hiddenCategories.ToList())
                {
                    try
                    {
                        view.SetCategoryHidden(new ElementId(categoryIdValue), false);
                        categoriesRestored++;
                    }
                    catch
                    {
                        // Category may no longer be hideable in this view; skip.
                    }
                }

                tx.Commit();
                HiddenByExplorer.Remove(view.Id.Value);
                CategoryHiddenByExplorer.Remove(view.Id.Value);
                return (shown.Count + categoriesRestored, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Unhide all failed in view '{view.Name}'.", ex);
                return (0, $"Could not unhide: {ex.Message}");
            }
        }

        // Category hides (view-wide, host + links) — the closest Revit allows to hiding a
        // category inside one link, tracked per view so Unhide All restores them.
        private static readonly Dictionary<long, HashSet<long>> CategoryHiddenByExplorer =
            new Dictionary<long, HashSet<long>>();

        private static HashSet<long> CategoryHiddenSetFor(View view)
        {
            if (!CategoryHiddenByExplorer.TryGetValue(view.Id.Value, out var set))
            {
                CategoryHiddenByExplorer[view.Id.Value] = set = new HashSet<long>();
            }

            return set;
        }

        /// <summary>
        /// Hides/shows whole categories in the active view (affects host AND all links —
        /// Revit cannot scope category visibility to a single link).
        /// </summary>
        /// <param name="trackedOnly">
        /// When showing, restrict to categories the Explorer itself hid — protects the user's
        /// own VG setup when a broad row (e.g. the Host root) is switched back on.
        /// </param>
        public static (int Changed, string? Error) SetCategoriesHidden(
            UIDocument uidoc,
            IReadOnlyCollection<string> categoryNames,
            bool hide,
            bool trackedOnly = false)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            var wanted = new HashSet<string>(categoryNames, StringComparer.OrdinalIgnoreCase);
            var tracked = CategoryHiddenSetFor(view);

            var targets = new List<ElementId>();
            foreach (Category category in doc.Settings.Categories)
            {
                if (!wanted.Contains(category.Name))
                {
                    continue;
                }

                if (!hide && trackedOnly && !tracked.Contains(category.Id.Value))
                {
                    continue;
                }

                try
                {
                    if (hide
                            ? view.CanCategoryBeHidden(category.Id) && !view.GetCategoryHidden(category.Id)
                            : view.GetCategoryHidden(category.Id))
                    {
                        targets.Add(category.Id);
                    }
                }
                catch
                {
                    // Some categories reject visibility queries per view type; skip them.
                }
            }

            if (targets.Count == 0)
            {
                return (0, hide
                    ? "That category is already hidden or cannot be hidden in this view."
                    : "That category is not hidden in this view.");
            }

            using var tx = new Transaction(doc, hide ? "Model Explorer - Hide Category" : "Model Explorer - Show Category");
            try
            {
                tx.Start();
                foreach (var id in targets)
                {
                    view.SetCategoryHidden(id, hide);
                }

                tx.Commit();

                foreach (var id in targets)
                {
                    if (hide)
                    {
                        tracked.Add(id.Value);
                    }
                    else
                    {
                        tracked.Remove(id.Value);
                    }
                }

                return (targets.Count, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Category visibility change failed in view '{view.Name}'.", ex);
                return (0, $"Could not change category visibility: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores visibility of the named view filters (VG ▸ Filters) in the active view.
        /// View-wide by nature — a filter cannot be lifted for just one element.
        /// </summary>
        public static (int Changed, string? Error) SetFiltersVisible(
            UIDocument uidoc, IReadOnlyCollection<string> filterNames)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            var wanted = new HashSet<string>(filterNames, StringComparer.OrdinalIgnoreCase);

            var targets = new List<ElementId>();
            try
            {
                foreach (var filterId in view.GetFilters())
                {
                    var name = doc.GetElement(filterId)?.Name;
                    if (name != null && wanted.Contains(name) && !view.GetFilterVisibility(filterId))
                    {
                        targets.Add(filterId);
                    }
                }
            }
            catch (Exception ex)
            {
                return (0, $"Could not read view filters: {ex.Message}");
            }

            if (targets.Count == 0)
            {
                return (0, null);
            }

            using var tx = new Transaction(doc, "Model Explorer - Show Filtered Elements");
            try
            {
                tx.Start();
                foreach (var id in targets)
                {
                    view.SetFilterVisibility(id, true);
                }

                tx.Commit();
                return (targets.Count, null);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                {
                    tx.RollBack();
                }

                LogManager.Error("explorer", $"Filter visibility change failed in view '{view.Name}'.", ex);
                return (0, $"Could not change filter visibility: {ex.Message}");
            }
        }

        /// <summary>Which of the given link instances are currently hidden in the active view.</summary>
        public static IReadOnlyList<long> GetHiddenLinkIds(
            UIDocument uidoc, IReadOnlyCollection<long> linkInstanceIdValues)
        {
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            return linkInstanceIdValues
                .Where(id => doc.GetElement(new ElementId(id)) is RevitLinkInstance instance &&
                             SafeIsHidden(instance, view))
                .ToList();
        }

        /// <summary>Drops per-view tracking after a truth-based restore has made it stale.</summary>
        public static void ClearVisibilityTracking(UIDocument uidoc)
        {
            HiddenByExplorer.Remove(uidoc.ActiveView.Id.Value);
            CategoryHiddenByExplorer.Remove(uidoc.ActiveView.Id.Value);
        }

        /// <summary>How many elements/categories the Explorer has hidden (and not yet restored) in the active view.</summary>
        public static int GetTrackedHiddenCount(UIDocument uidoc) =>
            (HiddenByExplorer.TryGetValue(uidoc.ActiveView.Id.Value, out var set) ? set.Count : 0) +
            (CategoryHiddenByExplorer.TryGetValue(uidoc.ActiveView.Id.Value, out var cats) ? cats.Count : 0);

        private static bool CanHide(Element element, View view)
        {
            try
            {
                return element.CanBeHidden(view) && !element.IsHidden(view);
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
