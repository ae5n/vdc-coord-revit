using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitSuite.Host.Logging;
using RevitSuite.Host.UI;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class WallFramingCommand : IExternalCommand
    {
        private const string WallFramingFamilyName = "OFW_STR_Wall-Framing";
        private const string DoorFramingFamilyName = "OFW_STR_Door-Framing";
        private const string WindowFramingFamilyName = "OFW_STR_Window-Framing-TripleHeader";

        private static readonly Dictionary<string, string> TypeSuffixMapping = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Jack Stud"] = "JST",
            ["Trimmer"] = "TRI",
            ["Top Cripple"] = "TPC",
            ["Stud"] = "STD",
            ["Top Plate"] = "TTP",
            ["Bottom Cripple"] = "BTC",
            ["Bottom Plate"] = "BTP",
            ["Header LVL"] = "HEA",
            ["Sill Studs"] = "SIL"
        };

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "WallFramingCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    return Result.Failed;
                }

                var options = PromptForOptions(data.Application);
                if (options == null)
                {
                    LogManager.Info(correlationId, "WallFramingCommand cancelled by user.");
                    return Result.Cancelled;
                }

                if (!options.FrameWall && !options.FrameOpenings)
                {
                    TaskDialog.Show("RevitSuite", "Select at least one action: Frame Wall or Frame Openings.");
                    return Result.Cancelled;
                }

                var doc = uiDoc.Document;
                var walls = GetTargetWalls(uiDoc);
                if (walls.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No walls selected.");
                    return Result.Cancelled;
                }

                var wallSymbol = options.FrameWall ? FindFamilySymbol(doc, WallFramingFamilyName) : null;
                var doorSymbol = options.FrameOpenings ? FindFamilySymbol(doc, DoorFramingFamilyName) : null;
                var windowSymbol = options.FrameOpenings ? FindFamilySymbol(doc, WindowFramingFamilyName) : null;

                if (options.FrameWall && wallSymbol == null)
                {
                    message = $"Could not find family '{WallFramingFamilyName}'.";
                    return Result.Failed;
                }

                if (options.FrameOpenings && (doorSymbol == null || windowSymbol == null))
                {
                    message = $"Could not find opening framing families '{DoorFramingFamilyName}' and/or '{WindowFramingFamilyName}'.";
                    return Result.Failed;
                }

                var createdWallFrames = 0;
                var createdOpeningFrames = 0;
                var warnings = new List<string>();

                using (var tx = new Transaction(doc, "RevitSuite: Wall Framing"))
                {
                    tx.Start();

                    ActivateIfNeeded(wallSymbol);
                    ActivateIfNeeded(doorSymbol);
                    ActivateIfNeeded(windowSymbol);

                    foreach (var wallSource in walls)
                    {
                        var wall = wallSource.Wall;
                        if (wall.Location is not LocationCurve locationCurve)
                        {
                            warnings.Add($"{wallSource.DisplayName}: no usable location curve and was skipped.");
                            continue;
                        }

                        var hostCurve = wallSource.GetHostCurve();
                        var level = wallSource.ResolveHostLevel(doc);
                        if (level == null)
                        {
                            warnings.Add($"{wallSource.DisplayName}: no valid host level was found and it was skipped.");
                            continue;
                        }

                        var wallMark = GetWallMark(wall);
                        var wallHeight = GetWallHeight(wall);
                        var depthResult = ResolveDepth(wall, options);
                        if (!string.IsNullOrWhiteSpace(depthResult.Warning))
                        {
                            warnings.Add($"{wallSource.DisplayName}: {depthResult.Warning}");
                        }

                        var placementOffsets = ResolvePlacementOffsets(wall);

                        var resolvedOpenings = ResolveOpeningSpans(wallSource, hostCurve, level.Elevation, warnings);

                        if (options.FrameWall && wallSymbol != null)
                        {
                            var panelPoint = GetWallPlacementPoint(hostCurve, level.Elevation, placementOffsets.WallOffset);
                            if (HasExistingFamilyAtPoint(doc, WallFramingFamilyName, panelPoint))
                            {
                                warnings.Add($"{wallSource.DisplayName}: existing wall framing was found at the same location, so a duplicate was skipped.");
                            }
                            else
                            {
                                var wallFrame = CreateWallFrameInstance(doc, wallSymbol, panelPoint);
                                if (wallFrame != null)
                                {
                                    SetDoubleParameter(wallFrame, "Length", hostCurve.Length);
                                    SetDoubleParameter(wallFrame, "Height", wallHeight);
                                    SetDoubleParameter(wallFrame, "Structural Depth", depthResult.Depth);
                                    SetStringParameter(wallFrame, "Mark", $"{wallMark}-P01");
                                    RotateToWallDirection(doc, wallFrame, panelPoint, hostCurve);
                                    createdWallFrames++;
                                }
                                else
                                {
                                    warnings.Add($"{wallSource.DisplayName}: unable to place '{WallFramingFamilyName}'.");
                                }
                            }
                        }

                        if (!options.FrameOpenings || doorSymbol == null || windowSymbol == null)
                        {
                            continue;
                        }

                        var doorNumber = 1;
                        var windowNumber = 1;

                        foreach (var openingSpan in resolvedOpenings)
                        {
                            var symbol = ResolveOpeningSymbol(openingSpan, doorSymbol, windowSymbol);

                            if (symbol == null)
                            {
                                continue;
                            }

                            var openingPoint = GetOpeningPlacementPoint(
                                openingSpan,
                                hostCurve,
                                level.Elevation,
                                placementOffsets.OpeningOffset);
                            var openingWidth = openingSpan.Width;
                            var openingHeight = openingSpan.Height;
                            var sillHeight = openingSpan.SillHeight;

                            if (openingWidth <= 1e-6 || openingHeight <= 1e-6)
                            {
                                warnings.Add($"{openingSpan.DisplayName}: width/height could not be resolved and was skipped.");
                                continue;
                            }

                            if (HasExistingFamilyAtPoint(doc, symbol.FamilyName, openingPoint))
                            {
                                warnings.Add($"{openingSpan.DisplayName}: existing opening framing was found at the same location, so a duplicate was skipped.");
                                continue;
                            }

                            var frame = CreateLevelBasedInstance(doc, symbol, openingPoint, level);
                            if (frame == null)
                            {
                                warnings.Add($"{openingSpan.DisplayName}: unable to place '{symbol.FamilyName}'.");
                                continue;
                            }

                            SetDoubleParameter(frame, "Opening Length", openingWidth);
                            SetDoubleParameter(frame, "Opening Height", openingHeight);
                            SetDoubleParameter(frame, "Structural Depth", depthResult.Depth);
                            SetDoubleParameter(frame, "Wall Height", wallHeight);
                            SetDoubleParameter(frame, "Elevation from Level", 0.0);
                            SetDoubleParameter(frame, "Sill Height", sillHeight);
                            SetStringParameter(frame, "Wall Mark", wallMark);

                            string openingMark;
                            string framingUse;

                            if (openingSpan.Kind == OpeningKind.Door)
                            {
                                openingMark = $"DR{doorNumber:00}";
                                framingUse = $"Opening: Door {doorNumber}";
                                doorNumber++;
                            }
                            else
                            {
                                openingMark = $"WN{windowNumber:00}";
                                framingUse = $"Opening: Window {windowNumber}";
                                windowNumber++;
                            }

                            SetStringParameter(frame, "Opening Number", openingMark);
                            RotateToWallDirection(doc, frame, openingPoint, hostCurve);
                            doc.Regenerate();
                            ProcessOpeningSubcomponents(doc, frame, wallMark, openingMark, framingUse);
                            createdOpeningFrames++;
                        }
                    }

                    tx.Commit();
                }

                var summary = $"Created {createdWallFrames} wall frame(s) and {createdOpeningFrames} opening frame(s) across {walls.Count} wall(s).";
                if (warnings.Count > 0)
                {
                    summary += $"\n\nWarnings ({warnings.Count}):\n- " + string.Join("\n- ", warnings.Take(12));
                    if (warnings.Count > 12)
                    {
                        summary += "\n- Additional warnings omitted for brevity.";
                    }
                }

                TaskDialog.Show("RevitSuite", summary);
                LogManager.Info(correlationId, summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Wall framing command failed. See log for details.");
                LogManager.Error(correlationId, "WallFramingCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static WallFramingOptions? PromptForOptions(UIApplication application)
        {
            using var form = new WallFramingForm();
            var owner = new RevitWindow(application.MainWindowHandle);
            return form.ShowDialog(owner) == DialogResult.OK ? form.Options : null;
        }

        private static List<WallSource> GetTargetWalls(UIDocument uiDoc)
        {
            var selectedWalls = uiDoc.Selection
                .GetElementIds()
                .Select(id => uiDoc.Document.GetElement(id))
                .OfType<Wall>()
                .Select(wall => new WallSource(wall, uiDoc.Document, Transform.Identity, null))
                .ToList();

            if (selectedWalls.Count > 0)
            {
                return selectedWalls;
            }

            var selectionMode = PromptWallSelectionMode();
            if (selectionMode == null)
            {
                return new List<WallSource>();
            }

            var results = new List<WallSource>();
            if (selectionMode == WallSelectionMode.Host || selectionMode == WallSelectionMode.Both)
            {
                results.AddRange(PickHostWalls(uiDoc));
            }

            if (selectionMode == WallSelectionMode.Linked || selectionMode == WallSelectionMode.Both)
            {
                results.AddRange(PickLinkedWalls(uiDoc));
            }

            return results;
        }

        private static FamilySymbol? FindFamilySymbol(Document doc, string familyName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(symbol => symbol.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase));
        }

        private static void ActivateIfNeeded(FamilySymbol? symbol)
        {
            if (symbol != null && !symbol.IsActive)
            {
                symbol.Activate();
            }
        }

        private static FamilyInstance? CreateLevelBasedInstance(Document doc, FamilySymbol symbol, XYZ point, Level level)
        {
            try
            {
                return doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
            }
            catch
            {
                try
                {
                    return doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static FamilyInstance? CreateWallFrameInstance(Document doc, FamilySymbol symbol, XYZ point)
        {
            try
            {
                return doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            }
            catch
            {
                return null;
            }
        }

        private static string GetWallMark(Wall wall)
        {
            var mark = wall.LookupParameter("Mark")?.AsString();
            if (string.IsNullOrWhiteSpace(mark))
            {
                return $"W{wall.Id.Value}";
            }

            return mark.Trim();
        }

        private static double GetWallHeight(Wall wall)
        {
            var explicitHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble();
            if (explicitHeight.HasValue && explicitHeight.Value > 0)
            {
                return explicitHeight.Value;
            }

            var box = wall.get_BoundingBox(null);
            if (box != null)
            {
                return Math.Abs(box.Max.Z - box.Min.Z);
            }

            return 0.0;
        }

        private static DepthResolutionResult ResolveDepth(Wall wall, WallFramingOptions options)
        {
            if (options.DepthSource == WallFramingDepthSource.Manual)
            {
                return new DepthResolutionResult(options.ManualDepthFeet, null);
            }

            var structuralDepth = GetStructuralLayerDepth(wall);
            var overallDepth = wall.Width;

            return options.DepthSource switch
            {
                WallFramingDepthSource.StructuralLayerOnly => structuralDepth.HasValue
                    ? new DepthResolutionResult(structuralDepth.Value, null)
                    : new DepthResolutionResult(overallDepth, "no structural layer was found, so overall wall thickness was used"),
                WallFramingDepthSource.OverallWallThickness => new DepthResolutionResult(overallDepth, null),
                _ => structuralDepth.HasValue
                    ? new DepthResolutionResult(structuralDepth.Value, null)
                    : new DepthResolutionResult(overallDepth, "no structural layer was found, so overall wall thickness was used")
            };
        }

        private static double? GetStructuralLayerDepth(Wall wall)
        {
            if (wall.WallType.Kind != WallKind.Basic)
            {
                return null;
            }

            var structure = wall.WallType.GetCompoundStructure();
            if (structure == null)
            {
                return null;
            }

            var structuralLayers = structure.GetLayers()
                .Where(layer => layer.Function == MaterialFunctionAssignment.Structure && layer.Width > 0)
                .Select(layer => layer.Width)
                .ToList();

            return structuralLayers.Count > 0 ? structuralLayers.Max() : null;
        }

        private static List<OpeningSpan> ResolveOpeningSpans(
            WallSource wallSource,
            Curve wallCurve,
            double levelElevation,
            ICollection<string> warnings)
        {
            var rawSpans = wallSource.Wall.FindInserts(true, true, true, true)
                .Select(id => wallSource.SourceDocument.GetElement(id))
                .Where(element => element != null)
                .Select(element => OpeningSource.Create(element!, wallSource))
                .Where(source => source != null)
                .Cast<OpeningSource>()
                .Select(source => TryCreateOpeningSpan(source, wallCurve, levelElevation, out var span) ? span : null)
                .Where(span => span != null)
                .Cast<OpeningSpan>()
                .OrderBy(span => span.Start)
                .ToList();

            return DeduplicateOpeningSpans(rawSpans, warnings, wallSource.DisplayName);
        }

        private static XYZ GetOpeningPlacementPoint(
            OpeningSpan openingSpan,
            Curve wallCurve,
            double levelElevation,
            double offsetFromCenter)
        {
            var start = wallCurve.GetEndPoint(0);
            var direction = (wallCurve.GetEndPoint(1) - start).Normalize();
            var centerDistance = (openingSpan.Start + openingSpan.End) / 2.0;
            var centerPoint = start + (direction * centerDistance);
            var flattenedPoint = new XYZ(centerPoint.X, centerPoint.Y, levelElevation);
            return TranslateAlongWallNormal(flattenedPoint, wallCurve, offsetFromCenter);
        }

        private static bool TryCreateOpeningSpan(
            OpeningSource openingSource,
            Curve wallCurve,
            double levelElevation,
            out OpeningSpan span)
        {
            span = null!;
            if (!TryGetOpeningSpanDistances(openingSource, wallCurve, out var start, out var end))
            {
                return false;
            }

            var height = GetOpeningHeight(openingSource);
            if (!height.HasValue || height.Value <= 1e-6)
            {
                return false;
            }

            var sillHeight = GetSillHeight(openingSource, levelElevation);
            span = new OpeningSpan(openingSource, start, end, height.Value, sillHeight);
            return true;
        }

        private static bool TryGetOpeningSpanDistances(OpeningSource openingSource, Curve wallCurve, out double start, out double end)
        {
            start = 0.0;
            end = 0.0;

            var origin = wallCurve.GetEndPoint(0);
            var direction = (wallCurve.GetEndPoint(1) - origin).Normalize();

            if (openingSource.Element is FamilyInstance familyInstance)
            {
                // Prefer one consistent source of truth for compound/asymmetric openings:
                // use the projected family geometry extents when available so king studs
                // land on the true outer opening width instead of a leaf-only width.
                if (TryGetProjectedSolidSpanDistances(familyInstance, openingSource.WallSource, origin, direction, out start, out end))
                {
                    return end > start;
                }

                var width = GetPreferredOpeningDimension(familyInstance, new[]
                {
                    "Rough Width",
                    "Width",
                    "Window Width",
                    "Door Width"
                });

                if (!width.HasValue || width.Value <= 1e-6)
                {
                    return false;
                }

                var projected = TryGetProjectedSolidCenterDistance(familyInstance, openingSource.WallSource, origin, direction)
                    ?? TryGetProjectedLocationDistance(familyInstance, openingSource.WallSource, origin, direction);

                if (!projected.HasValue)
                {
                    return false;
                }

                start = projected.Value - (width.Value / 2.0);
                end = projected.Value + (width.Value / 2.0);
                return end > start;
            }

            var boxForProjection = openingSource.Element.get_BoundingBox(null);
            if (boxForProjection == null)
            {
                return false;
            }

            var distances = new[]
            {
                new XYZ(boxForProjection.Min.X, boxForProjection.Min.Y, boxForProjection.Min.Z),
                new XYZ(boxForProjection.Min.X, boxForProjection.Min.Y, boxForProjection.Max.Z),
                new XYZ(boxForProjection.Min.X, boxForProjection.Max.Y, boxForProjection.Min.Z),
                new XYZ(boxForProjection.Min.X, boxForProjection.Max.Y, boxForProjection.Max.Z),
                new XYZ(boxForProjection.Max.X, boxForProjection.Min.Y, boxForProjection.Min.Z),
                new XYZ(boxForProjection.Max.X, boxForProjection.Min.Y, boxForProjection.Max.Z),
                new XYZ(boxForProjection.Max.X, boxForProjection.Max.Y, boxForProjection.Min.Z),
                new XYZ(boxForProjection.Max.X, boxForProjection.Max.Y, boxForProjection.Max.Z)
            }
            .Select(point => openingSource.WallSource.Transform.OfPoint(point))
            .Select(point => direction.DotProduct(point - origin))
            .ToList();

            if (distances.Count == 0)
            {
                return false;
            }

            start = distances.Min();
            end = distances.Max();
            return end > start;
        }

        private static double? TryGetProjectedLocationDistance(
            FamilyInstance familyInstance,
            WallSource wallSource,
            XYZ origin,
            XYZ direction)
        {
            XYZ centerPoint;
            if (familyInstance.Location is LocationPoint locationPoint)
            {
                centerPoint = wallSource.Transform.OfPoint(locationPoint.Point);
            }
            else
            {
                var box = familyInstance.get_BoundingBox(null);
                if (box == null)
                {
                    return null;
                }

                centerPoint = wallSource.Transform.OfPoint(
                    new XYZ(
                        (box.Min.X + box.Max.X) / 2.0,
                        (box.Min.Y + box.Max.Y) / 2.0,
                        (box.Min.Z + box.Max.Z) / 2.0));
            }

            return direction.DotProduct(centerPoint - origin);
        }

        private static bool TryGetProjectedSolidSpanDistances(
            FamilyInstance familyInstance,
            WallSource wallSource,
            XYZ origin,
            XYZ direction,
            out double start,
            out double end)
        {
            start = 0.0;
            end = 0.0;

            var options = new Options
            {
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement symbolGeometry;
            try
            {
                symbolGeometry = familyInstance.Symbol.get_Geometry(options);
            }
            catch
            {
                return false;
            }

            if (symbolGeometry == null)
            {
                return false;
            }

            var distances = new List<double>();
            CollectProjectedSolidDistances(symbolGeometry, familyInstance.GetTotalTransform(), wallSource, origin, direction, distances);
            if (distances.Count == 0)
            {
                return false;
            }

            start = distances.Min();
            end = distances.Max();
            return end > start;
        }

        private static double? TryGetProjectedSolidCenterDistance(
            FamilyInstance familyInstance,
            WallSource wallSource,
            XYZ origin,
            XYZ direction)
        {
            var options = new Options
            {
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement symbolGeometry;
            try
            {
                symbolGeometry = familyInstance.Symbol.get_Geometry(options);
            }
            catch
            {
                return null;
            }

            if (symbolGeometry == null)
            {
                return null;
            }

            var distances = new List<double>();
            CollectProjectedSolidDistances(symbolGeometry, familyInstance.GetTotalTransform(), wallSource, origin, direction, distances);
            if (distances.Count == 0)
            {
                return null;
            }

            return (distances.Min() + distances.Max()) / 2.0;
        }

        private static void CollectProjectedSolidDistances(
            GeometryElement geometry,
            Transform currentTransform,
            WallSource wallSource,
            XYZ origin,
            XYZ direction,
            ICollection<double> distances)
        {
            foreach (var geometryObject in geometry)
            {
                if (geometryObject is GeometryInstance geometryInstance)
                {
                    CollectProjectedSolidDistances(
                        geometryInstance.GetSymbolGeometry(),
                        currentTransform.Multiply(geometryInstance.Transform),
                        wallSource,
                        origin,
                        direction,
                        distances);
                    continue;
                }

                if (geometryObject is not Solid solid || solid.Volume <= 1e-6)
                {
                    continue;
                }

                foreach (Face face in solid.Faces)
                {
                    var bounds = face.GetBoundingBox();
                    var u0 = bounds.Min.U;
                    var u1 = bounds.Max.U;
                    var v0 = bounds.Min.V;
                    var v1 = bounds.Max.V;
                    var samplePoints = new[]
                    {
                        face.Evaluate(new UV(u0, v0)),
                        face.Evaluate(new UV(u0, v1)),
                        face.Evaluate(new UV(u1, v0)),
                        face.Evaluate(new UV(u1, v1))
                    };

                    foreach (var point in samplePoints)
                    {
                        var worldPoint = wallSource.Transform.OfPoint(currentTransform.OfPoint(point));
                        distances.Add(direction.DotProduct(worldPoint - origin));
                    }
                }
            }
        }

        private static XYZ GetWallPlacementPoint(Curve wallCurve, double levelElevation, double offsetFromCenter)
        {
            var wallEndPoint = wallCurve.GetEndPoint(1);
            var flattenedPoint = new XYZ(wallEndPoint.X, wallEndPoint.Y, levelElevation);
            return TranslateAlongWallNormal(flattenedPoint, wallCurve, offsetFromCenter);
        }

        private static XYZ TranslateAlongWallNormal(XYZ point, Curve wallCurve, double offsetFromCenter)
        {
            if (Math.Abs(offsetFromCenter) < 1e-9)
            {
                return point;
            }

            var direction = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
            var leftNormal = new XYZ(-direction.Y, direction.X, 0.0).Normalize();
            return point + (leftNormal * offsetFromCenter);
        }

        private static double? GetOpeningHeight(OpeningSource openingSource)
        {
            if (openingSource.Element is FamilyInstance familyInstance)
            {
                return GetPreferredOpeningDimension(familyInstance, new[]
                {
                    "Rough Height",
                    "Height",
                    "Window Height",
                    "Door Height"
                });
            }

            var box = openingSource.Element.get_BoundingBox(null);
            if (box == null)
            {
                return null;
            }

            var min = openingSource.WallSource.Transform.OfPoint(box.Min);
            var max = openingSource.WallSource.Transform.OfPoint(box.Max);
            return Math.Abs(max.Z - min.Z);
        }

        private static double GetSillHeight(OpeningSource openingSource, double levelElevation)
        {
            if (openingSource.Element is FamilyInstance familyInstance)
            {
                return GetInstanceOrTypeDouble(familyInstance, "Sill Height") ?? 0.0;
            }

            var box = openingSource.Element.get_BoundingBox(null);
            if (box == null)
            {
                return 0.0;
            }

            var min = openingSource.WallSource.Transform.OfPoint(box.Min);
            return Math.Max(0.0, min.Z - levelElevation);
        }

        private static FamilySymbol? ResolveOpeningSymbol(OpeningSpan span, FamilySymbol? doorSymbol, FamilySymbol? windowSymbol)
        {
            return span.Kind == OpeningKind.Door ? doorSymbol : windowSymbol;
        }

        private static List<OpeningSpan> DeduplicateOpeningSpans(
            IReadOnlyList<OpeningSpan> spans,
            ICollection<string> warnings,
            string wallDisplayName)
        {
            var unique = new List<OpeningSpan>();
            const double duplicateTolerance = 0.05;

            foreach (var span in spans.OrderBy(item => item.Start))
            {
                var existingIndex = unique.FindIndex(existing =>
                    Math.Abs(existing.Start - span.Start) <= duplicateTolerance &&
                    Math.Abs(existing.End - span.End) <= duplicateTolerance &&
                    Math.Abs(existing.SillHeight - span.SillHeight) <= duplicateTolerance &&
                    Math.Abs(existing.Height - span.Height) <= duplicateTolerance);

                if (existingIndex < 0)
                {
                    unique.Add(span);
                    continue;
                }

                var replacement = ChoosePreferredSpan(unique[existingIndex], span);
                unique[existingIndex] = replacement;
            }

            if (unique.Count < spans.Count)
            {
                warnings.Add($"{wallDisplayName}: duplicate opening definitions were reduced from {spans.Count} to {unique.Count} resolved span(s).");
            }

            return unique;
        }

        private static OpeningSpan ChoosePreferredSpan(OpeningSpan current, OpeningSpan candidate)
        {
            if (current.Kind == candidate.Kind)
            {
                return current.Sources.Count >= candidate.Sources.Count ? current : candidate;
            }

            if (current.Kind == OpeningKind.Door)
            {
                return current;
            }

            if (candidate.Kind == OpeningKind.Door)
            {
                return candidate;
            }

            return current.Sources.Count >= candidate.Sources.Count ? current : candidate;
        }

        private static PlacementOffsetResult ResolvePlacementOffsets(Wall wall)
        {
            var totalWidth = wall.Width;
            if (wall.WallType.Kind != WallKind.Basic)
            {
                return new PlacementOffsetResult(totalWidth / 2.0, -totalWidth / 2.0);
            }

            var structure = wall.WallType.GetCompoundStructure();
            var layers = structure?.GetLayers();
            if (layers == null || layers.Count == 0)
            {
                return new PlacementOffsetResult(totalWidth / 2.0, -totalWidth / 2.0);
            }

            var totalLayerWidth = layers.Sum(layer => layer.Width);
            if (totalLayerWidth <= 1e-9)
            {
                return new PlacementOffsetResult(totalWidth / 2.0, -totalWidth / 2.0);
            }

            var structuralIndexes = layers
                .Select((layer, index) => new { layer, index })
                .Where(item => item.layer.Function == MaterialFunctionAssignment.Structure && item.layer.Width > 0)
                .Select(item => item.index)
                .ToList();

            if (structuralIndexes.Count == 0)
            {
                return new PlacementOffsetResult(totalLayerWidth / 2.0, -totalLayerWidth / 2.0);
            }

            var firstStructuralIndex = structuralIndexes.Min();
            var lastStructuralIndex = structuralIndexes.Max();
            var widthBeforeStructural = layers.Take(firstStructuralIndex).Sum(layer => layer.Width);
            var widthThroughStructural = layers.Take(lastStructuralIndex + 1).Sum(layer => layer.Width);
            var halfTotalWidth = totalLayerWidth / 2.0;

            return new PlacementOffsetResult(
                widthThroughStructural - halfTotalWidth,
                widthBeforeStructural - halfTotalWidth);
        }

        private static double? GetInstanceOrTypeDouble(FamilyInstance instance, string parameterName)
        {
            var parameter = instance.LookupParameter(parameterName) ?? instance.Symbol.LookupParameter(parameterName);
            if (parameter == null || !parameter.HasValue)
            {
                return null;
            }

            try
            {
                return parameter.AsDouble();
            }
            catch
            {
                return null;
            }
        }

        private static double? GetPreferredOpeningDimension(FamilyInstance instance, IEnumerable<string> parameterNames)
        {
            foreach (var parameterName in parameterNames)
            {
                var value = GetInstanceOrTypeDouble(instance, parameterName);
                if (value.HasValue && value.Value > 1e-6)
                {
                    return value.Value;
                }
            }

            return null;
        }

        private static void RotateToWallDirection(Document doc, FamilyInstance instance, XYZ origin, Curve wallCurve)
        {
            var direction = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
            var targetAngle = Math.Atan2(direction.Y, direction.X);

            var transform = instance.GetTotalTransform();
            var currentAngle = Math.Atan2(transform.BasisX.Y, transform.BasisX.X);
            var delta = targetAngle - currentAngle;

            if (Math.Abs(delta) < 1e-9)
            {
                return;
            }

            var axis = Line.CreateUnbound(origin, XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, instance.Id, axis, delta);
        }

        private static void ProcessOpeningSubcomponents(
            Document doc,
            FamilyInstance frame,
            string wallMark,
            string openingMark,
            string framingUse)
        {
            var grouped = new Dictionary<string, List<FamilyInstance>>(StringComparer.OrdinalIgnoreCase);

            foreach (var subId in frame.GetSubComponentIds())
            {
                if (doc.GetElement(subId) is not FamilyInstance subComponent)
                {
                    continue;
                }

                var typeName = subComponent.Symbol.LookupParameter("Type Name")?.AsString() ?? subComponent.Symbol.Name;
                if (!TypeSuffixMapping.ContainsKey(typeName))
                {
                    continue;
                }

                if (!grouped.TryGetValue(typeName, out var items))
                {
                    items = new List<FamilyInstance>();
                    grouped[typeName] = items;
                }

                items.Add(subComponent);
                SetStringParameter(subComponent, "Framing Use", framingUse);
            }

            foreach (var pair in grouped)
            {
                var prefix = TypeSuffixMapping[pair.Key];
                var count = 1;
                foreach (var item in pair.Value.OrderBy(i => i.Id.Value))
                {
                    SetStringParameter(item, "Mark", $"{wallMark}-{openingMark}-{prefix}{count:00}");
                    SetStringParameter(item, "Lumber Mark", $"{wallMark}-{openingMark}-{prefix}");
                    count++;
                }
            }
        }

        private static void SetDoubleParameter(Element element, string parameterName, double value)
        {
            var parameter = element.LookupParameter(parameterName);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Double)
            {
                return;
            }

            parameter.Set(value);
        }

        private static void SetStringParameter(Element element, string parameterName, string value)
        {
            var parameter = element.LookupParameter(parameterName);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
            {
                return;
            }

            parameter.Set(value);
        }

        private static bool HasExistingFamilyAtPoint(Document doc, string familyName, XYZ point)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(instance => instance.Symbol != null &&
                                   instance.Symbol.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
                .Any(instance => TryGetPlacementPoint(instance, out var existingPoint) && existingPoint.DistanceTo(point) < 0.01);
        }

        private static bool TryGetPlacementPoint(FamilyInstance instance, out XYZ point)
        {
            if (instance.Location is LocationPoint locationPoint)
            {
                point = locationPoint.Point;
                return true;
            }

            var box = instance.get_BoundingBox(null);
            if (box != null)
            {
                point = new XYZ(
                    (box.Min.X + box.Max.X) / 2.0,
                    (box.Min.Y + box.Max.Y) / 2.0,
                    (box.Min.Z + box.Max.Z) / 2.0);
                return true;
            }

            point = XYZ.Zero;
            return false;
        }

        private static WallSelectionMode? PromptWallSelectionMode()
        {
            var dialog = new TaskDialog("RevitSuite")
            {
                MainInstruction = "Choose wall source",
                MainContent = "You can frame walls from the host model, a linked model, or both. Framing will still be created in the active host model.",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Host walls only");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Linked walls only");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Host and linked walls");

            var result = dialog.Show();
            return result switch
            {
                TaskDialogResult.CommandLink1 => WallSelectionMode.Host,
                TaskDialogResult.CommandLink2 => WallSelectionMode.Linked,
                TaskDialogResult.CommandLink3 => WallSelectionMode.Both,
                _ => null
            };
        }

        private static IEnumerable<WallSource> PickHostWalls(UIDocument uiDoc)
        {
            try
            {
                TaskDialog.Show("RevitSuite", "Select host walls to frame and press Finish.");
                return uiDoc.Selection
                    .PickObjects(ObjectType.Element, new WallSelectionFilter(), "Select host walls to frame")
                    .Select(reference => uiDoc.Document.GetElement(reference))
                    .OfType<Wall>()
                    .Select(wall => new WallSource(wall, uiDoc.Document, Transform.Identity, null))
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Enumerable.Empty<WallSource>();
            }
        }

        private static IEnumerable<WallSource> PickLinkedWalls(UIDocument uiDoc)
        {
            try
            {
                TaskDialog.Show("RevitSuite", "Select linked walls to frame and press Finish.");
                return uiDoc.Selection
                    .PickObjects(ObjectType.LinkedElement, new LinkedWallSelectionFilter(uiDoc.Document), "Select linked walls to frame")
                    .Select(reference =>
                    {
                        var linkInstance = uiDoc.Document.GetElement(reference.ElementId) as RevitLinkInstance;
                        var linkDoc = linkInstance?.GetLinkDocument();
                        var wall = linkDoc?.GetElement(reference.LinkedElementId) as Wall;
                        return wall == null || linkDoc == null || linkInstance == null
                            ? null
                            : new WallSource(wall, linkDoc, linkInstance.GetTotalTransform() ?? Transform.Identity, linkInstance);
                    })
                    .Where(source => source != null)
                    .Cast<WallSource>()
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Enumerable.Empty<WallSource>();
            }
        }

        private sealed class DepthResolutionResult
        {
            public DepthResolutionResult(double depth, string? warning)
            {
                Depth = depth;
                Warning = warning;
            }

            public double Depth { get; }

            public string? Warning { get; }
        }

        private sealed class PlacementOffsetResult
        {
            public PlacementOffsetResult(double openingOffset, double wallOffset)
            {
                OpeningOffset = openingOffset;
                WallOffset = wallOffset;
            }

            public double OpeningOffset { get; }

            public double WallOffset { get; }
        }

        private sealed class WallSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Wall;

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private sealed class LinkedWallSelectionFilter : ISelectionFilter
        {
            private readonly Document _hostDocument;

            public LinkedWallSelectionFilter(Document hostDocument)
            {
                _hostDocument = hostDocument;
            }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference.LinkedElementId == ElementId.InvalidElementId)
                {
                    return false;
                }

                var linkInstance = _hostDocument.GetElement(reference.ElementId) as RevitLinkInstance;
                var linkDoc = linkInstance?.GetLinkDocument();
                if (linkDoc == null)
                {
                    return false;
                }

                return linkDoc.GetElement(reference.LinkedElementId) is Wall;
            }
        }

        private enum WallSelectionMode
        {
            Host,
            Linked,
            Both
        }

        private enum OpeningKind
        {
            Unknown,
            Door,
            Window,
            CurtainWallInsert
        }

        private sealed class OpeningSource
        {
            private OpeningSource(Element element, WallSource wallSource, OpeningKind kind)
            {
                Element = element;
                WallSource = wallSource;
                Kind = kind;
                DisplayName = wallSource.IsLinked
                    ? $"Linked opening {element.Id.Value} ({wallSource.SourceDocument.Title})"
                    : $"Opening {element.Id.Value}";
            }

            public static OpeningSource? Create(Element element, WallSource wallSource)
            {
                var kind = element switch
                {
                    FamilyInstance familyInstance when familyInstance.Category?.Id.Value == (long) BuiltInCategory.OST_Doors => OpeningKind.Door,
                    FamilyInstance familyInstance when familyInstance.Category?.Id.Value == (long) BuiltInCategory.OST_Windows => OpeningKind.Window,
                    Wall insertedWall when insertedWall.WallType.Kind == WallKind.Curtain => OpeningKind.CurtainWallInsert,
                    _ => OpeningKind.Unknown
                };

                return kind == OpeningKind.Unknown ? null : new OpeningSource(element, wallSource, kind);
            }

            public Element Element { get; }

            public WallSource WallSource { get; }

            public OpeningKind Kind { get; }

            public string DisplayName { get; }
        }

        private sealed class OpeningSpan
        {
            public OpeningSpan(OpeningSource source, double start, double end, double height, double sillHeight)
                : this(new List<OpeningSource> { source }, source.Kind, start, end, height, sillHeight)
            {
            }

            private OpeningSpan(
                IReadOnlyList<OpeningSource> sources,
                OpeningKind kind,
                double start,
                double end,
                double height,
                double sillHeight)
            {
                Sources = sources;
                Kind = kind;
                Start = start;
                End = end;
                Height = height;
                SillHeight = sillHeight;
                DisplayName = sources.Count == 1
                    ? sources[0].DisplayName
                    : $"Merged opening span ({sources.Count} sources)";
            }

            public IReadOnlyList<OpeningSource> Sources { get; }

            public OpeningKind Kind { get; }

            public double Start { get; }

            public double End { get; }

            public double Width => End - Start;

            public double Height { get; }

            public double SillHeight { get; }

            public string DisplayName { get; }

            public OpeningSpan Merge(OpeningSpan other)
            {
                var sources = Sources.Concat(other.Sources).ToList();
                var sillHeight = Math.Min(SillHeight, other.SillHeight);
                var heightTop = Math.Max(SillHeight + Height, other.SillHeight + other.Height);
                var mergedKind = sillHeight <= 0.25 || Kind == OpeningKind.Door || other.Kind == OpeningKind.Door
                    ? OpeningKind.Door
                    : OpeningKind.Window;

                return new OpeningSpan(
                    sources,
                    mergedKind,
                    Math.Min(Start, other.Start),
                    Math.Max(End, other.End),
                    Math.Max(0.0, heightTop - sillHeight),
                    sillHeight);
            }
        }

        private sealed class WallSource
        {
            public WallSource(Wall wall, Document sourceDocument, Transform transform, RevitLinkInstance? linkInstance)
            {
                Wall = wall;
                SourceDocument = sourceDocument;
                Transform = transform;
                LinkInstance = linkInstance;
                IsLinked = linkInstance != null;
                DisplayName = IsLinked
                    ? $"Linked wall {wall.Id.Value} ({sourceDocument.Title})"
                    : $"Wall {wall.Id.Value}";
            }

            public Wall Wall { get; }

            public Document SourceDocument { get; }

            public Transform Transform { get; }

            public RevitLinkInstance? LinkInstance { get; }

            public bool IsLinked { get; }

            public string DisplayName { get; }

            public Curve GetHostCurve()
            {
                var curve = ((LocationCurve) Wall.Location).Curve;
                return Line.CreateBound(
                    Transform.OfPoint(curve.GetEndPoint(0)),
                    Transform.OfPoint(curve.GetEndPoint(1)));
            }

            public Level? ResolveHostLevel(Document hostDocument)
            {
                var sourceLevel = SourceDocument.GetElement(Wall.LevelId) as Level;
                if (sourceLevel != null)
                {
                    var sameNameLevel = new FilteredElementCollector(hostDocument)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(level => level.Name.Equals(sourceLevel.Name, StringComparison.OrdinalIgnoreCase));
                    if (sameNameLevel != null)
                    {
                        return sameNameLevel;
                    }
                }

                var hostBasePoint = Transform.OfPoint(((LocationCurve) Wall.Location).Curve.GetEndPoint(0));
                return new FilteredElementCollector(hostDocument)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(level => Math.Abs(level.Elevation - hostBasePoint.Z))
                    .FirstOrDefault();
            }
        }

        private sealed class RevitWindow : IWin32Window
        {
            public RevitWindow(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
