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

            return new DeletePreflight(total, categoryCounts, viewSpecific, pinned, ownedByOthers, isWorkshared);
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
