using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;

namespace RevitSuite.Mcp.Core
{
    public class ExternalEventManager
    {
        private static ExternalEventManager _instance;
        private Dictionary<string, ExternalEventWrapper> _events = new Dictionary<string, ExternalEventWrapper>();
        private bool _isInitialized = false;
        private UIApplication _uiApp;
        private ILogger _logger;

        public static ExternalEventManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new ExternalEventManager();
                return _instance;
            }
        }

        private ExternalEventManager() { }

        public void Initialize(UIApplication uiApp, ILogger logger)
        {
            _uiApp = uiApp;
            _logger = logger;
            _isInitialized = true;
        }

        public ExternalEvent GetOrCreateEvent(IWaitableExternalEventHandler handler, string key)
        {
            if (!_isInitialized)
                throw new InvalidOperationException($"{nameof(ExternalEventManager)} has not been initialized.");

            if (_events.TryGetValue(key, out var wrapper) && wrapper.Handler == handler)
                return wrapper.Event;

            ExternalEvent externalEvent = null;

            _uiApp.ActiveUIDocument.Document.Application.ExecuteCommand(
                (uiApp) => { externalEvent = ExternalEvent.Create(handler); }
            );

            if (externalEvent == null)
                throw new InvalidOperationException("Unable to create external event.");

            _events[key] = new ExternalEventWrapper { Event = externalEvent, Handler = handler };
            _logger.Info($"Created a new external event for key {key}.");

            return externalEvent;
        }

        public void ClearEvents()
        {
            _events.Clear();
        }

        private class ExternalEventWrapper
        {
            public ExternalEvent Event { get; set; }
            public IWaitableExternalEventHandler Handler { get; set; }
        }
    }
}

namespace Autodesk.Revit.DB
{
    public static class McpApplicationExtensions
    {
        public delegate void CommandDelegate(UIApplication uiApp);

        public static void ExecuteCommand(this Autodesk.Revit.ApplicationServices.Application app, CommandDelegate command)
        {
            command?.Invoke(new UIApplication(app));
        }
    }
}
