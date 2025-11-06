using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSuite.Host.Logging;
using RevitSuite.Host.UI;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CopyLinkedViewsCommand : IExternalCommand
    {
        private static readonly char[] InvalidNameCharacters = new[]
        {
            '\\', '/', ':', ';', '|', ',', '[', ']', '{', '}', '<', '>', '?', '"'
        };

        private static readonly HashSet<ViewType> SupportedViewTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan,
            ViewType.CeilingPlan,
            ViewType.EngineeringPlan,
            ViewType.ThreeD
        };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "CopyLinkedViewsCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var hostDocument = uiDoc.Document;
                var linkedModels = CollectLinkedModels(hostDocument).ToList();

                if (linkedModels.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No loaded Revit links were found to source views from.");
                    LogManager.Warn(correlationId, "Copy linked views cancelled: no link documents.");
                    return Result.Cancelled;
                }

                using var form = new CopyLinkedViewsForm(linkedModels, SupportedViewTypes);
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    LogManager.Info(correlationId, "Copy linked views cancelled by user.");
                    return Result.Cancelled;
                }

                var selection = form.GetSelection();
                if (selection.SelectedViews.Count == 0 && selection.SelectedViewSets.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No views or view sets were selected.");
                    LogManager.Warn(correlationId, "Copy linked views cancelled: nothing selected.");
                    return Result.Cancelled;
                }

                var mergedSelections = MergeSelections(selection);
                if (mergedSelections.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No eligible views were selected.");
                    LogManager.Warn(correlationId, "Copy linked views cancelled: no eligible selections after merge.");
                    return Result.Cancelled;
                }

                var summary = new CopySummary();
                using (var transaction = new Transaction(hostDocument, "RevitSuite: Copy Linked Views"))
                {
                    transaction.Start();

                    var resourceResolver = new HostResourceResolver(hostDocument);
                    var templateCache = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

                    foreach (var viewSelection in mergedSelections.Values)
                    {
                        if (!TryCopyView(
                                hostDocument,
                                viewSelection.LinkedModel,
                                viewSelection.ViewId,
                                resourceResolver,
                                templateCache,
                                summary))
                        {
                            continue;
                        }
                    }

                    CreateViewSets(hostDocument, selection.SelectedViewSets, summary);

                    transaction.Commit();
                }

                var dialogText = summary.BuildDialogText();
                TaskDialog.Show("RevitSuite", dialogText);
                LogManager.Info(correlationId, summary.BuildLogEntry());
                return summary.CopiedCount > 0 ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Copy Linked Views failed. See log for details.");
                LogManager.Error(correlationId, "CopyLinkedViewsCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static IEnumerable<LinkedModelOption> CollectLinkedModels(Document hostDocument)
        {
            var options = new List<LinkedModelOption>();
            var linkInstances = new FilteredElementCollector(hostDocument)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(instance => instance.GetLinkDocument() != null)
                .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var instance in linkInstances)
            {
                var linkDocument = instance.GetLinkDocument();
                if (linkDocument == null)
                {
                    continue;
                }

                var displayName = $"{linkDocument.Title} ({instance.Name})";
                var views = CollectViews(linkDocument, displayName);
                var viewSets = CollectViewSets(linkDocument, views);

                if (views.Count == 0 && viewSets.Count == 0)
                {
                    continue;
                }

                options.Add(new LinkedModelOption(displayName, instance, linkDocument, views, viewSets));
            }

            return options;
        }

        private static List<ViewOption> CollectViews(Document linkDocument, string linkDisplayName)
        {
            var result = new List<ViewOption>();
            var orderedViews = new FilteredElementCollector(linkDocument)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && SupportedViewTypes.Contains(view.ViewType))
                .OrderBy(view => view.ViewType)
                .ThenBy(view => view.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var view in orderedViews)
            {
                var syntheticKey = GetSyntheticViewSetKey(view);
                result.Add(new ViewOption(
                    linkDisplayName,
                    linkDocument,
                    view.Id,
                    view.UniqueId,
                    view.Name,
                    view.ViewType,
                    syntheticKey));
            }

            return result;
        }

        private static string? GetSyntheticViewSetKey(View view)
        {
            foreach (Parameter parameter in view.Parameters)
            {
                if (!parameter.HasValue ||
                    parameter.StorageType != StorageType.String)
                {
                    continue;
                }

                var definitionName = parameter.Definition?.Name;
                if (string.IsNullOrWhiteSpace(definitionName) ||
                    !definitionName.Equals("Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = parameter.AsString();
                if (!string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static List<ViewSetOption> CollectViewSets(Document linkDocument, IReadOnlyCollection<ViewOption> availableViews)
        {
            var results = new List<ViewSetOption>();
            if (availableViews.Count == 0)
            {
                return results;
            }

            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var viewLookup = availableViews
                .ToDictionary(v => v.ViewId, v => v, new ElementIdComparer());

            try
            {
                var printManager = linkDocument.PrintManager;
                printManager.PrintRange = PrintRange.Select;
                var viewSheetSetting = printManager.ViewSheetSetting;

                foreach (var snapshot in ViewSheetSetUtilities.EnumerateSnapshots(viewSheetSetting))
                {
                    var viewIds = new List<ElementId>();
                    var viewNames = new List<string>();

                    foreach (var viewId in snapshot.ViewIds)
                    {
                        if (!viewLookup.TryGetValue(viewId, out var option))
                        {
                            continue;
                        }

                        viewIds.Add(option.ViewId);
                        viewNames.Add(option.ViewName);
                    }

                    if (viewIds.Count == 0)
                    {
                        continue;
                    }

                    existingNames.Add(snapshot.Name);
                    results.Add(new ViewSetOption(snapshot.Name, linkDocument, viewIds, viewNames, false));
                }
            }
            catch
            {
                // Accessing print manager on a link may fail under some Revit configurations.
            }

            AddSyntheticViewSets(linkDocument, availableViews, results, existingNames);
            return results;
        }

        private static void AddSyntheticViewSets(
            Document linkDocument,
            IReadOnlyCollection<ViewOption> availableViews,
            List<ViewSetOption> results,
            ISet<string> existingNames)
        {
            var groups = new Dictionary<string, List<ViewOption>>(StringComparer.OrdinalIgnoreCase);

            foreach (var view in availableViews)
            {
                if (string.IsNullOrWhiteSpace(view.SyntheticGroup))
                {
                    continue;
                }

                if (!groups.TryGetValue(view.SyntheticGroup, out var list))
                {
                    list = new List<ViewOption>();
                    groups[view.SyntheticGroup] = list;
                }

                list.Add(view);
            }

            foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var views = group.Value;
                if (views.Count == 0)
                {
                    continue;
                }

                var baseName = SanitizeName(group.Key);
                var displayName = $"Type - {baseName}";
                var suffix = 2;
                while (existingNames.Contains(displayName))
                {
                    displayName = $"Type - {baseName} ({suffix})";
                    suffix++;
                }

                existingNames.Add(displayName);
                var viewIds = views.Select(v => v.ViewId).ToList();
                var viewNames = views.Select(v => v.ViewName).ToList();
                results.Add(new ViewSetOption(displayName, linkDocument, viewIds, viewNames, true));
            }
        }

        private static Dictionary<string, ViewSelection> MergeSelections(CopyLinkedViewsSelection selection)
        {
            var result = new Dictionary<string, ViewSelection>(StringComparer.OrdinalIgnoreCase);

            foreach (var view in selection.SelectedViews)
            {
                if (!SupportedViewTypes.Contains(view.ViewType))
                {
                    continue;
                }

                result[view.ViewUniqueId] = new ViewSelection(view.LinkedModel, view.ViewId, view.ViewUniqueId, view.ViewType, view.ViewName);
            }

            foreach (var set in selection.SelectedViewSets)
            {
                foreach (var viewId in set.ViewIds)
                {
                    var view = set.LinkedModel.LinkDocument.GetElement(viewId) as View;
                    if (view == null || view.IsTemplate || !SupportedViewTypes.Contains(view.ViewType))
                    {
                        continue;
                    }

                    result[view.UniqueId] = new ViewSelection(
                        set.LinkedModel,
                        view.Id,
                        view.UniqueId,
                        view.ViewType,
                        view.Name);
                }
            }

            return result;
        }

        private static bool TryCopyView(
            Document hostDocument,
            LinkedModelOption linkedModel,
            ElementId viewId,
            HostResourceResolver resourceResolver,
            Dictionary<string, ElementId> templateCache,
            CopySummary summary)
        {
            var linkDocument = linkedModel.LinkDocument;
            var sourceView = linkDocument.GetElement(viewId) as View;
            if (sourceView == null)
            {
                summary.AddWarning($"View with id {viewId.Value} could not be resolved in {linkedModel.DisplayName}.");
                return false;
            }

            if (!SupportedViewTypes.Contains(sourceView.ViewType))
            {
                summary.AddWarning($"View '{sourceView.Name}' skipped: unsupported type {sourceView.ViewType}.");
                return false;
            }

            var usedNames = summary.GetNameSet(hostDocument);
            var baseName = $"{sourceView.Name} ({linkedModel.DisplayName})";
            var targetName = BuildUniqueName(baseName, usedNames);

            var newViewId = sourceView.ViewType switch
            {
                ViewType.FloorPlan => CopyPlanLikeView(hostDocument, sourceView as ViewPlan, linkDocument, targetName, resourceResolver, templateCache, summary),
                ViewType.CeilingPlan => CopyPlanLikeView(hostDocument, sourceView as ViewPlan, linkDocument, targetName, resourceResolver, templateCache, summary),
                ViewType.EngineeringPlan => CopyPlanLikeView(hostDocument, sourceView as ViewPlan, linkDocument, targetName, resourceResolver, templateCache, summary),
                ViewType.ThreeD => CopyThreeDView(hostDocument, sourceView as View3D, targetName, resourceResolver, templateCache, summary),
                _ => null
            };

            if (newViewId != null && newViewId != ElementId.InvalidElementId)
            {
                summary.RegisterCopiedView(sourceView, hostDocument.GetElement(newViewId) as View);
                return true;
            }

            return false;
        }

        private static ElementId? CopyPlanLikeView(
            Document hostDocument,
            ViewPlan? sourcePlan,
            Document linkDocument,
            string targetName,
            HostResourceResolver resourceResolver,
            Dictionary<string, ElementId> templateCache,
            CopySummary summary)
        {
            if (sourcePlan == null)
            {
                summary.AddWarning("Encountered plan view that could not be cast to ViewPlan.");
                return null;
            }

            var sourceType = linkDocument.GetElement(sourcePlan.GetTypeId()) as ViewFamilyType;
            if (sourceType == null)
            {
                summary.AddWarning($"View '{sourcePlan.Name}' skipped: missing view family type.");
                return null;
            }

            var hostViewFamilyType = resourceResolver.ResolveViewFamilyType(sourceType.ViewFamily);
            if (hostViewFamilyType == null)
            {
                summary.AddWarning($"View '{sourcePlan.Name}' skipped: host document lacks a '{sourceType.ViewFamily}' view family type.");
                return null;
            }

            var sourceLevel = sourcePlan.GenLevel;
            if (sourceLevel == null)
            {
                summary.AddWarning($"View '{sourcePlan.Name}' skipped: linked view has no associated level.");
                return null;
            }

            var hostLevel = resourceResolver.ResolveLevelByName(sourceLevel.Name);
            if (hostLevel == null)
            {
                summary.AddWarning($"View '{sourcePlan.Name}' skipped: host document has no level named '{sourceLevel.Name}'.");
                return null;
            }

            var newView = ViewPlan.Create(hostDocument, hostViewFamilyType.Id, hostLevel.Id);
            ApplyTemplate(templateCache, newView, sourcePlan, hostDocument, linkDocument, summary);
            ApplyCommonViewSettings(newView, sourcePlan, targetName);
            CopyPlanViewSpecifics(newView, sourcePlan);
            return newView.Id;
        }

        private static ElementId? CopyThreeDView(
            Document hostDocument,
            View3D? sourceView,
            string targetName,
            HostResourceResolver resourceResolver,
            Dictionary<string, ElementId> templateCache,
            CopySummary summary)
        {
            if (sourceView == null)
            {
                summary.AddWarning("Encountered 3D view that could not be cast to View3D.");
                return null;
            }

            var hostViewFamilyType = resourceResolver.ResolveViewFamilyType(ViewFamily.ThreeDimensional);
            if (hostViewFamilyType == null)
            {
                summary.AddWarning($"3D View '{sourceView.Name}' skipped: host document has no 3D view family type.");
                return null;
            }

            View3D newView;
            try
            {
                newView = sourceView.IsPerspective
                    ? View3D.CreatePerspective(hostDocument, hostViewFamilyType.Id)
                    : View3D.CreateIsometric(hostDocument, hostViewFamilyType.Id);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                summary.AddWarning($"3D View '{sourceView.Name}' skipped: host document cannot create this view type.");
                return null;
            }

            ApplyTemplate(templateCache, newView, sourceView, hostDocument, sourceView.Document, summary);
            ApplyCommonViewSettings(newView, sourceView, targetName);
            CopyThreeDViewSpecifics(newView, sourceView);
            return newView.Id;
        }

        private static void ApplyTemplate(
            Dictionary<string, ElementId> templateCache,
            View targetView,
            View sourceView,
            Document hostDocument,
            Document linkDocument,
            CopySummary summary)
        {
            if (sourceView.ViewTemplateId == ElementId.InvalidElementId)
            {
                return;
            }

            var template = linkDocument.GetElement(sourceView.ViewTemplateId) as View;
            if (template == null)
            {
                return;
            }

            if (templateCache.TryGetValue(template.UniqueId, out var existingTemplateId))
            {
                targetView.ViewTemplateId = existingTemplateId;
                return;
            }

            var hostTemplate = new FilteredElementCollector(hostDocument)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));

            if (hostTemplate != null)
            {
                templateCache[template.UniqueId] = hostTemplate.Id;
                targetView.ViewTemplateId = hostTemplate.Id;
                return;
            }

            try
            {
                var copiedIds = ElementTransformUtils.CopyElements(
                    linkDocument,
                    new[] { template.Id },
                    hostDocument,
                    Transform.Identity,
                    new CopyPasteOptions());

                foreach (var copiedId in copiedIds)
                {
                    var copiedTemplate = hostDocument.GetElement(copiedId) as View;
                    if (copiedTemplate == null || !copiedTemplate.IsTemplate)
                    {
                        continue;
                    }

                    templateCache[template.UniqueId] = copiedTemplate.Id;
                    targetView.ViewTemplateId = copiedTemplate.Id;
                    return;
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Copy may fail if elements are not compatible between documents.
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Copy may fail if template depends on other elements not available.
            }

            summary.AddWarning($"View template '{template.Name}' could not be copied to the host document.");
        }

        private static void ApplyCommonViewSettings(View targetView, View sourceView, string targetName)
        {
            targetView.Name = targetName;
            SetParameterValue(targetView, BuiltInParameter.VIEW_DETAIL_LEVEL, () => (int)sourceView.DetailLevel);
            SetParameterValue(targetView, BuiltInParameter.VIEW_DISCIPLINE, () => sourceView.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE)?.AsInteger());
            SetParameterValue(targetView, BuiltInParameter.VIEW_SCALE, () => sourceView.Scale);

            try
            {
                targetView.DisplayStyle = sourceView.DisplayStyle;
            }
            catch
            {
                // Some plan types lock display style (e.g., ceiling plans with templates). Ignore if read-only.
            }
            targetView.PartsVisibility = sourceView.PartsVisibility;

            try
            {
                targetView.CropBoxActive = sourceView.CropBoxActive;
                targetView.CropBoxVisible = sourceView.CropBoxVisible;
                if (sourceView.CropBox != null)
                {
                    targetView.CropBox = CloneBoundingBox(sourceView.CropBox);
                }
            }
            catch
            {
                // Crop box operations may fail for certain view configurations; ignore gracefully.
            }
        }

        private static void CopyPlanViewSpecifics(ViewPlan targetView, ViewPlan sourceView)
        {
            try
            {
                targetView.PartsVisibility = sourceView.PartsVisibility;
                targetView.CropBoxActive = sourceView.CropBoxActive;
                targetView.CropBoxVisible = sourceView.CropBoxVisible;
                if (sourceView.CropBox != null)
                {
                    targetView.CropBox = CloneBoundingBox(sourceView.CropBox);
                }
            }
            catch
            {
                // Some parameters are template-controlled; ignore failures.
            }
        }

        private static void CopyThreeDViewSpecifics(View3D targetView, View3D sourceView)
        {
            try
            {
                targetView.IsSectionBoxActive = sourceView.IsSectionBoxActive;
                targetView.DetailLevel = sourceView.DetailLevel;
                targetView.SetOrientation(sourceView.GetOrientation());
                if (sourceView.GetSectionBox() is BoundingBoxXYZ sectionBox)
                {
                    targetView.SetSectionBox(CloneBoundingBox(sectionBox));
                }
            }
            catch
            {
                // Orientation or section box copy can fail for perspective views with locked conditions.
            }
        }

        private static void CreateViewSets(Document hostDocument, IReadOnlyCollection<ViewSetSelection> selectedSets, CopySummary summary)
        {
            if (selectedSets.Count == 0)
            {
                return;
            }

            foreach (var set in selectedSets)
            {
                if (set.IsSynthetic)
                {
                    continue;
                }

                try
                {
                    var views = new ViewSet();
                    foreach (var sourceId in set.ViewIds)
                    {
                        var sourceView = set.LinkedModel.LinkDocument.GetElement(sourceId) as View;
                        if (sourceView == null)
                        {
                            continue;
                        }

                        if (summary.TryGetMappedViewId(sourceView.UniqueId, out var mappedId))
                        {
                            if (hostDocument.GetElement(mappedId) is View mappedView)
                            {
                                views.Insert(mappedView);
                                continue;
                            }
                        }

                        var matchingView = FindHostViewByName(hostDocument, $"{sourceView.Name} ({set.LinkedModel.DisplayName})");
                        if (matchingView != null)
                        {
                            views.Insert(matchingView);
                        }
                    }

                    if (views.IsEmpty)
                    {
                        summary.AddWarning($"View set '{set.Name}' skipped: none of its views were copied.");
                        continue;
                    }

                    var printManager = hostDocument.PrintManager;
                    printManager.PrintRange = PrintRange.Select;
                    var viewSheetSetting = printManager.ViewSheetSetting;
                    viewSheetSetting.CurrentViewSheetSet.Views = views;

                    var targetName = BuildUniqueName(set.Name, summary.GetViewSetNameSet(hostDocument));
                    viewSheetSetting.SaveAs(targetName);
                    summary.RegisterViewSet(targetName, views.Size);
                }
                catch (Exception ex)
                {
                    summary.AddWarning($"View set '{set.Name}' could not be created: {ex.Message}");
                }
            }
        }

        private static View? FindHostViewByName(Document hostDocument, string name)
        {
            var sanitizedName = SanitizeName(name);
            return new FilteredElementCollector(hostDocument)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(view => !view.IsTemplate && view.Name.Equals(sanitizedName, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildUniqueName(string baseName, ISet<string> usedNames)
        {
            baseName = SanitizeName(baseName);

            if (!usedNames.Contains(baseName))
            {
                usedNames.Add(baseName);
                return baseName;
            }

            var index = 2;
            string candidate;
            do
            {
                candidate = $"{baseName} ({index})";
                index++;
            } while (usedNames.Contains(candidate));

            usedNames.Add(candidate);
            return candidate;
        }

        private static void SetParameterValue(View view, BuiltInParameter parameterId, Func<int?> valueFactory)
        {
            try
            {
                var parameter = view.get_Parameter(parameterId);
                if (parameter == null || parameter.IsReadOnly)
                {
                    return;
                }

                var value = valueFactory();
                if (value.HasValue)
                {
                    parameter.Set(value.Value);
                }
            }
            catch
            {
                // Ignore parameter copy failures; templates can lock parameters.
            }
        }

        private static BoundingBoxXYZ CloneBoundingBox(BoundingBoxXYZ source)
        {
            return new BoundingBoxXYZ
            {
                Min = source.Min,
                Max = source.Max,
                Transform = source.Transform
            };
        }

        private sealed class ElementIdComparer : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId? x, ElementId? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x is null || y is null)
                {
                    return false;
                }

                return x.Value == y.Value;
            }

            public int GetHashCode(ElementId obj)
            {
                return obj.Value.GetHashCode();
            }
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Untitled";
            }

            var sanitized = value.Trim();
            foreach (var ch in InvalidNameCharacters)
            {
                sanitized = sanitized.Replace(ch, '_');
            }

            return sanitized;
        }

    }

    internal sealed class LinkedModelOption
    {
        public LinkedModelOption(
            string displayName,
            RevitLinkInstance linkInstance,
            Document linkDocument,
            IReadOnlyList<ViewOption> views,
            IReadOnlyList<ViewSetOption> viewSets)
        {
            DisplayName = displayName;
            LinkInstance = linkInstance;
            LinkDocument = linkDocument;
            Views = views;
            ViewSets = viewSets;
        }

        public string DisplayName { get; }
        public RevitLinkInstance LinkInstance { get; }
        public Document LinkDocument { get; }
        public IReadOnlyList<ViewOption> Views { get; }
        public IReadOnlyList<ViewSetOption> ViewSets { get; }
    }

    internal sealed class ViewOption
    {
        public ViewOption(
            string linkDisplayName,
            Document linkDocument,
            ElementId viewId,
            string viewUniqueId,
            string viewName,
            ViewType viewType,
            string? syntheticGroup)
        {
            LinkDisplayName = linkDisplayName;
            LinkDocument = linkDocument;
            ViewId = viewId;
            ViewUniqueId = viewUniqueId;
            ViewName = viewName;
            ViewType = viewType;
            SyntheticGroup = syntheticGroup;
        }

        public string LinkDisplayName { get; }
        public Document LinkDocument { get; }
        public ElementId ViewId { get; }
        public string ViewUniqueId { get; }
        public string ViewName { get; }
        public ViewType ViewType { get; }
        public string? SyntheticGroup { get; }
    }

    internal sealed class ViewSetOption
    {
        public ViewSetOption(
            string name,
            Document linkDocument,
            IReadOnlyList<ElementId> viewIds,
            IReadOnlyList<string> viewNames,
            bool isSynthetic)
        {
            Name = name;
            LinkDocument = linkDocument;
            ViewIds = viewIds;
            ViewNames = viewNames;
            IsSynthetic = isSynthetic;
        }

        public string Name { get; }
        public Document LinkDocument { get; }
        public IReadOnlyList<ElementId> ViewIds { get; }
        public IReadOnlyList<string> ViewNames { get; }
        public bool IsSynthetic { get; }
    }

    internal sealed class CopyLinkedViewsSelection
    {
        public List<ViewSelectionEntry> SelectedViews { get; } = new List<ViewSelectionEntry>();
        public List<ViewSetSelection> SelectedViewSets { get; } = new List<ViewSetSelection>();
    }

    internal sealed class ViewSelectionEntry
    {
        public ViewSelectionEntry(
            LinkedModelOption linkedModel,
            ElementId viewId,
            string viewUniqueId,
            string viewName,
            ViewType viewType)
        {
            LinkedModel = linkedModel;
            ViewId = viewId;
            ViewUniqueId = viewUniqueId;
            ViewName = viewName;
            ViewType = viewType;
        }

        public LinkedModelOption LinkedModel { get; }
        public ElementId ViewId { get; }
        public string ViewUniqueId { get; }
        public string ViewName { get; }
        public ViewType ViewType { get; }
    }

    internal sealed class ViewSetSelection
    {
        public ViewSetSelection(
            LinkedModelOption linkedModel,
            string name,
            IReadOnlyList<ElementId> viewIds,
            bool isSynthetic)
        {
            LinkedModel = linkedModel;
            Name = name;
            ViewIds = viewIds;
            IsSynthetic = isSynthetic;
        }

        public LinkedModelOption LinkedModel { get; }
        public string Name { get; }
        public IReadOnlyList<ElementId> ViewIds { get; }
        public bool IsSynthetic { get; }
    }

    internal sealed class ViewSelection
    {
        public ViewSelection(
            LinkedModelOption linkedModel,
            ElementId viewId,
            string viewUniqueId,
            ViewType viewType,
            string viewName)
        {
            LinkedModel = linkedModel;
            ViewId = viewId;
            ViewUniqueId = viewUniqueId;
            ViewType = viewType;
            ViewName = viewName;
        }

        public LinkedModelOption LinkedModel { get; }
        public ElementId ViewId { get; }
        public string ViewUniqueId { get; }
        public ViewType ViewType { get; }
        public string ViewName { get; }
    }

    internal sealed class HostResourceResolver
    {
        private readonly Document _hostDocument;
        private readonly Dictionary<string, Level> _levelsByName = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ViewFamily, ViewFamilyType> _viewFamilyTypes = new Dictionary<ViewFamily, ViewFamilyType>();

        public HostResourceResolver(Document hostDocument)
        {
            _hostDocument = hostDocument;
            CacheLevels();
            CacheViewFamilyTypes();
        }

        private void CacheLevels()
        {
            foreach (var level in new FilteredElementCollector(_hostDocument)
                         .OfClass(typeof(Level))
                         .Cast<Level>())
            {
                if (!_levelsByName.ContainsKey(level.Name))
                {
                    _levelsByName[level.Name] = level;
                }
            }
        }

        private void CacheViewFamilyTypes()
        {
            foreach (var type in new FilteredElementCollector(_hostDocument)
                         .OfClass(typeof(ViewFamilyType))
                         .Cast<ViewFamilyType>())
            {
                if (!_viewFamilyTypes.ContainsKey(type.ViewFamily))
                {
                    _viewFamilyTypes[type.ViewFamily] = type;
                }
            }
        }

        public Level? ResolveLevelByName(string levelName)
        {
            return _levelsByName.TryGetValue(levelName, out var level) ? level : null;
        }

        public ViewFamilyType? ResolveViewFamilyType(ViewFamily viewFamily)
        {
            return _viewFamilyTypes.TryGetValue(viewFamily, out var type) ? type : null;
        }
    }

    internal sealed class CopySummary
    {
        private readonly List<string> _warnings = new List<string>();
        private readonly HashSet<string> _usedViewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _usedViewSetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ElementId> _viewMappings = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

        public int CopiedCount { get; private set; }
        public int ViewSetCount { get; private set; }

        public void RegisterCopiedView(View sourceView, View? targetView)
        {
            CopiedCount++;
            if (targetView != null)
            {
                _usedViewNames.Add(targetView.Name);
                _viewMappings[sourceView.UniqueId] = targetView.Id;
            }
        }

        public void RegisterViewSet(string name, int viewCount)
        {
            ViewSetCount++;
            _usedViewSetNames.Add(name);
            AddInfo($"View set '{name}' created with {viewCount} view(s).");
        }

        public void AddWarning(string warning)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                _warnings.Add(warning);
            }
        }

        private void AddInfo(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _warnings.Add($"INFO: {message}");
            }
        }

        public ISet<string> GetNameSet(Document hostDocument)
        {
            if (_usedViewNames.Count == 0)
            {
                foreach (var name in new FilteredElementCollector(hostDocument)
                             .OfClass(typeof(View))
                             .Cast<View>()
                             .Where(v => !v.IsTemplate)
                             .Select(v => v.Name))
                {
                    _usedViewNames.Add(name);
                }
            }

            return _usedViewNames;
        }

        public ISet<string> GetViewSetNameSet(Document hostDocument)
        {
            if (_usedViewSetNames.Count == 0)
            {
                try
                {
                    var printManager = hostDocument.PrintManager;
                    printManager.PrintRange = PrintRange.Select;
                    var setting = printManager.ViewSheetSetting;
                    foreach (var snapshot in ViewSheetSetUtilities.EnumerateSnapshots(setting))
                    {
                        _usedViewSetNames.Add(snapshot.Name);
                    }
                }
                catch
                {
                    // Accessing print settings can fail if no printers are configured; ignore and continue.
                }
            }

            return _usedViewSetNames;
        }

        public bool TryGetMappedViewId(string sourceUniqueId, out ElementId targetId)
        {
            return _viewMappings.TryGetValue(sourceUniqueId, out targetId);
        }

        public string BuildDialogText()
        {
            var lines = new List<string>
            {
                $"{CopiedCount} view(s) copied.",
                $"{ViewSetCount} view set(s) created."
            };

            if (_warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Notes:");
                lines.AddRange(_warnings.Select(w => $"- {w}"));
            }

            return string.Join(Environment.NewLine, lines);
        }

        public string BuildLogEntry()
        {
            return $"Copied {CopiedCount} view(s); created {ViewSetCount} view set(s). " +
                   (_warnings.Count == 0
                       ? "No warnings."
                       : $"Warnings: {string.Join(" | ", _warnings)}");
        }
    }


    internal static class ViewSheetSetUtilities
    {
        internal sealed class ViewSheetSetSnapshot
        {
            public ViewSheetSetSnapshot(string name, IReadOnlyList<ElementId> viewIds)
            {
                Name = name;
                ViewIds = viewIds;
            }

            public string Name { get; }
            public IReadOnlyList<ElementId> ViewIds { get; }
        }

        public static IEnumerable<ViewSheetSetSnapshot> EnumerateSnapshots(ViewSheetSetting? setting)
        {
            if (setting == null)
            {
                yield break;
            }

            var property = setting.GetType().GetProperty("ViewSheetSets");
            if (property == null)
            {
                yield break;
            }

            var iteratorObject = property.GetValue(setting);
            if (iteratorObject == null)
            {
                yield break;
            }

            if (iteratorObject is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    foreach (var snapshot in CreateSnapshots(item))
                    {
                        yield return snapshot;
                    }
                }

                yield break;
            }

            if (iteratorObject is System.Collections.IEnumerator enumerator)
            {
                while (enumerator.MoveNext())
                {
                    foreach (var snapshot in CreateSnapshots(enumerator.Current))
                    {
                        yield return snapshot;
                    }
                }
            }
        }

        private static IEnumerable<ViewSheetSetSnapshot> CreateSnapshots(object? setObject)
        {
            if (setObject == null)
            {
                yield break;
            }

            var setType = setObject.GetType();
            var nameProp = setType.GetProperty("Name");
            var viewsProp = setType.GetProperty("Views");

            if (nameProp == null || viewsProp == null)
            {
                yield break;
            }

            var name = nameProp.GetValue(setObject) as string ?? string.Empty;
            var viewsObj = viewsProp.GetValue(setObject);
            var viewIds = new List<ElementId>();

            if (viewsObj is System.Collections.IEnumerable viewEnumerable)
            {
                foreach (var viewObj in viewEnumerable)
                {
                    switch (viewObj)
                    {
                        case View view:
                            viewIds.Add(view.Id);
                            break;
                        case ElementId id:
                            viewIds.Add(id);
                            break;
                    }
                }
            }

            if (viewIds.Count > 0)
            {
                yield return new ViewSheetSetSnapshot(name, viewIds);
            }
        }
    }
}
