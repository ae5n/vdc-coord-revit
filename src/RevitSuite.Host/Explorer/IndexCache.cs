using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace RevitSuite.Host.Explorer
{
    /// <summary>
    /// Accumulates host-document element changes (via DocumentChanged) so a refresh can
    /// patch the existing index instead of re-sweeping the whole model. All access happens
    /// on the Revit thread (the event and the bridge both run there) — no locking needed.
    /// </summary>
    internal static class DocumentChangeTracker
    {
        private const int MaxTracked = 25_000;

        private sealed class State
        {
            public readonly HashSet<long> Upserts = new HashSet<long>();
            public readonly HashSet<long> Deletes = new HashSet<long>();
            public bool Overflow;
        }

        private static readonly Dictionary<string, State> States =
            new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        private static bool _subscribed;

        /// <summary>Idempotent; must be called from a valid API context (e.g. the launching command).</summary>
        public static void EnsureSubscribed(UIApplication app)
        {
            if (_subscribed)
            {
                return;
            }

            app.Application.DocumentChanged += OnDocumentChanged;
            _subscribed = true;
        }

        private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || doc.IsLinked)
                {
                    return;
                }

                var state = StateFor(doc);
                if (state.Overflow)
                {
                    return;
                }

                foreach (var id in e.GetAddedElementIds().Concat(e.GetModifiedElementIds()))
                {
                    state.Upserts.Add(id.Value);
                }

                foreach (var id in e.GetDeletedElementIds())
                {
                    state.Deletes.Add(id.Value);
                    state.Upserts.Remove(id.Value);
                }

                if (state.Upserts.Count + state.Deletes.Count > MaxTracked)
                {
                    // Too much churn (e.g. a sync) — cheaper to re-sweep than to patch.
                    state.Overflow = true;
                    state.Upserts.Clear();
                    state.Deletes.Clear();
                }
            }
            catch
            {
                // Never let index bookkeeping disturb a model transaction.
            }
        }

        /// <summary>Snapshot-and-clear the pending changes for a document.</summary>
        public static (IReadOnlyCollection<long> Upserts, IReadOnlyCollection<long> Deletes, bool Overflow)
            TakeChanges(Document doc)
        {
            var state = StateFor(doc);
            var result = (state.Upserts.ToList(), (IReadOnlyCollection<long>)state.Deletes.ToList(), state.Overflow);
            MarkClean(doc);
            return ((IReadOnlyCollection<long>)result.Item1, result.Item2, result.Item3);
        }

        /// <summary>Call after a full sweep: pending changes are baked into the new baseline.</summary>
        public static void MarkClean(Document doc)
        {
            var state = StateFor(doc);
            state.Upserts.Clear();
            state.Deletes.Clear();
            state.Overflow = false;
        }

        private static State StateFor(Document doc)
        {
            var key = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
            if (!States.TryGetValue(key, out var state))
            {
                States[key] = state = new State();
            }

            return state;
        }
    }

    /// <summary>
    /// Session cache of linked-model indexes. Linked documents cannot change while loaded,
    /// so their records are reusable until the link is reloaded — detected via Revit's
    /// document version stamp (VersionGUID + save count). Forma does the same thing at
    /// service scale: index once, re-process only models that changed.
    /// </summary>
    internal static class LinkIndexCache
    {
        private sealed class Entry
        {
            public Entry(Guid versionGuid, int saves, bool includeUncategorized, long instanceId,
                IReadOnlyList<ElementRecord> records)
            {
                VersionGuid = versionGuid;
                Saves = saves;
                IncludeUncategorized = includeUncategorized;
                InstanceId = instanceId;
                Records = records;
            }

            public Guid VersionGuid { get; }
            public int Saves { get; }
            public bool IncludeUncategorized { get; }
            public long InstanceId { get; set; }
            public IReadOnlyList<ElementRecord> Records { get; set; }
        }

        private static readonly Dictionary<string, Entry> Cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<ElementRecord>? TryGet(Document linkDoc, long instanceIdValue, bool includeUncategorized)
        {
            try
            {
                var key = KeyFor(linkDoc);
                if (!Cache.TryGetValue(key, out var entry))
                {
                    return null;
                }

                var version = Document.GetDocumentVersion(linkDoc);
                if (entry.VersionGuid != version.VersionGUID ||
                    entry.Saves != version.NumberOfSaves ||
                    entry.IncludeUncategorized != includeUncategorized)
                {
                    Cache.Remove(key);
                    return null;
                }

                if (entry.InstanceId != instanceIdValue)
                {
                    // Link instance was recreated (reload keeps content stamp but may swap ids).
                    entry.Records = entry.Records
                        .Select(r => r with { LinkInstanceIdValue = instanceIdValue })
                        .ToList();
                    entry.InstanceId = instanceIdValue;
                }

                return entry.Records;
            }
            catch
            {
                return null;
            }
        }

        public static void Store(Document linkDoc, long instanceIdValue, bool includeUncategorized,
            IReadOnlyList<ElementRecord> records)
        {
            try
            {
                var version = Document.GetDocumentVersion(linkDoc);
                Cache[KeyFor(linkDoc)] = new Entry(
                    version.VersionGUID, version.NumberOfSaves, includeUncategorized, instanceIdValue, records);
            }
            catch
            {
                // Cache misses are always safe.
            }
        }

        private static string KeyFor(Document linkDoc) =>
            string.IsNullOrWhiteSpace(linkDoc.PathName) ? linkDoc.Title : linkDoc.PathName;
    }
}
