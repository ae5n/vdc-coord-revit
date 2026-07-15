using System;
using System.Windows;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Explorer.UI
{
    public sealed partial class ExplorerWindow
    {
        // --- Revit → Explorer model/view auto-sync ---
        // Window-scoped push subscriptions (unsubscribed on Close), independent of the
        // session-lifetime DocumentChangeTracker which only accumulates warm-index deltas.
        private static EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs>? _documentChangedHandler;
        private static EventHandler<Autodesk.Revit.UI.Events.ViewActivatedEventArgs>? _viewActivatedHandler;

        private DispatcherTimer? _autoSyncDebounce;
        private int _pendingAutoSyncChanges;
        private bool _autoSyncViewChanged;

        /// <summary>Set when a sync was due while the window was minimized/hidden; flushed on return.</summary>
        private bool _autoSyncDirty;

        private static readonly TimeSpan DocumentChangeDelay = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan ViewSwitchDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan DirtyFlushDelay = TimeSpan.FromMilliseconds(300);

        /// <summary>Runs on Revit's event thread — extract plain data, then hop to the UI thread.</summary>
        private void OnRevitDocumentChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            // Self-echo: transactions committed by the Explorer's own bridge actions raise
            // DocumentChanged too; those ops already recapture view state themselves.
            if (!_syncFromRevitEnabled || RevitActionBridge.IsSelfEcho)
            {
                return;
            }

            int changes;
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsLinked)
                {
                    return;
                }

                changes = e.GetAddedElementIds().Count
                          + e.GetModifiedElementIds().Count
                          + e.GetDeletedElementIds().Count;
                if (changes == 0)
                {
                    return;
                }
            }
            catch
            {
                // Event args can be finicky mid-edit; a missed sync beats a crash.
                return;
            }

            OnUi(() =>
            {
                _pendingAutoSyncChanges += changes;
                RequestAutoSync(DocumentChangeDelay);
            });
        }

        /// <summary>Runs on Revit's event thread.</summary>
        private void OnRevitViewActivated(object? sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            if (!_syncFromRevitEnabled)
            {
                return;
            }

            OnUi(() =>
            {
                _autoSyncViewChanged = true;
                RequestAutoSync(ViewSwitchDelay);
            });
        }

        /// <summary>UI thread. Restarts the debounce; the most recent request's delay wins.</summary>
        private void RequestAutoSync(TimeSpan delay)
        {
            _autoSyncDebounce ??= new DispatcherTimer();
            _autoSyncDebounce.Stop();
            _autoSyncDebounce.Interval = delay;
            _autoSyncDebounce.Tick -= OnAutoSyncTick;
            _autoSyncDebounce.Tick += OnAutoSyncTick;
            _autoSyncDebounce.Start();
        }

        private void OnAutoSyncTick(object? sender, EventArgs e)
        {
            _autoSyncDebounce?.Stop();

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                _autoSyncDirty = true;
                return;
            }

            if (_busyOverlay.Visibility == Visibility.Visible)
            {
                // A manual operation is in flight — retry after it instead of piling on.
                RequestAutoSync(DocumentChangeDelay);
                return;
            }

            var changes = _pendingAutoSyncChanges;
            var viewChanged = _autoSyncViewChanged;
            _pendingAutoSyncChanges = 0;
            _autoSyncViewChanged = false;

            var note = viewChanged
                ? changes > 0 ? $"view + {changes:N0} change(s)" : "view changed"
                : $"{changes:N0} change(s)";
            RefreshExplore(silent: true, autoSyncNote: note);
        }

        /// <summary>Wired in the constructor: a sync deferred while minimized fires on return.</summary>
        private void WireAutoSyncWindowEvents()
        {
            Activated += (_, _) => FlushAutoSyncIfDirty();
            StateChanged += (_, _) => FlushAutoSyncIfDirty();
            IsVisibleChanged += (_, _) => FlushAutoSyncIfDirty();
        }

        private void FlushAutoSyncIfDirty()
        {
            if (!_autoSyncDirty || WindowState == WindowState.Minimized || !IsVisible)
            {
                return;
            }

            _autoSyncDirty = false;
            RequestAutoSync(DirtyFlushDelay);
        }

        /// <summary>Called from ShowWindow (valid API context).</summary>
        private static void SubscribeAutoSync(UIApplication app, ExplorerWindow window)
        {
            try
            {
                _documentChangedHandler = window.OnRevitDocumentChanged;
                app.Application.DocumentChanged += _documentChangedHandler;
                _viewActivatedHandler = window.OnRevitViewActivated;
                app.ViewActivated += _viewActivatedHandler;
            }
            catch (Exception ex)
            {
                LogManager.Error("explorer", "Could not subscribe to Revit model/view changes.", ex);
            }
        }

        /// <summary>Called from the Closed handler while <c>_subscribedApp</c> is still set.</summary>
        private static void UnsubscribeAutoSync(UIApplication app)
        {
            try
            {
                if (_documentChangedHandler != null)
                {
                    app.Application.DocumentChanged -= _documentChangedHandler;
                }

                if (_viewActivatedHandler != null)
                {
                    app.ViewActivated -= _viewActivatedHandler;
                }
            }
            catch
            {
                // Best effort; Revit may be shutting down.
            }
            finally
            {
                _documentChangedHandler = null;
                _viewActivatedHandler = null;
            }
        }
    }
}
