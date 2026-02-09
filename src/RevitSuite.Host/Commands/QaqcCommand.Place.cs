using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    public partial class QaqcCommand
    {
        private Result ExecutePlace(string correlationId, UIDocument uiDoc, Document doc, QaqcConfig config, string category, int pourNumber)
        {
            try
            {
                // Auto-detect all footings from host and linked models
                var isSogCategory = string.Equals(category, SogCategoryName, StringComparison.OrdinalIgnoreCase);
                XYZ sogStartPoint = null;
                Transform sogTransform = Transform.Identity;
                RevitLinkInstance sogLinkInstance = null;
                string sogSourceModel = "Host";
                var footings = new List<FootingInfo>();
                if (isSogCategory)
                {
                    var selection = uiDoc.Selection;
                    var selectionMode = PromptSogSelectionMode();
                    if (!selectionMode.HasValue)
                    {
                        LogManager.Info(correlationId, "QAQC cancelled by user.");
                        return Result.Cancelled;
                    }

                    Floor slab = null;
                    if (selectionMode.Value == SogSelectionMode.Linked)
                    {
                        var slabRef = selection.PickObject(
                            ObjectType.LinkedElement,
                            new LinkedFloorSelectionFilter(doc),
                            "Select slab-on-grade floor from a linked model.");
                        var linkInstance = doc.GetElement(slabRef.ElementId) as RevitLinkInstance;
                        var linkDoc = linkInstance?.GetLinkDocument();
                        slab = linkDoc?.GetElement(slabRef.LinkedElementId) as Floor;
                        sogTransform = linkInstance?.GetTotalTransform() ?? Transform.Identity;
                        sogLinkInstance = linkInstance;
                        sogSourceModel = linkDoc?.Title ?? "Linked";
                    }
                    else
                    {
                        var slabRef = selection.PickObject(
                            ObjectType.Element,
                            new SogFloorSelectionFilter(),
                            "Select slab-on-grade floor for control points.");
                        slab = doc.GetElement(slabRef) as Floor;
                    }

                    if (slab == null)
                    {
                        TaskDialog.Show("RevitSuite", "Selected element is not a floor.");
                        LogManager.Warn(correlationId, "Place cancelled - selected element is not a floor.");
                        return Result.Cancelled;
                    }

                    sogStartPoint = selection.PickPoint("Pick the starting corner for numbering (counter-clockwise).");
                    footings.Add(new FootingInfo
                    {
                        Footing = slab,
                        Transform = sogTransform,
                        SourceModel = sogSourceModel,
                        LinkInstance = sogLinkInstance
                    });
                }
                else
                {
                    footings = AutoDetectFootings(doc, correlationId);
                }

                if (footings == null || footings.Count == 0)
                {
                    var notFoundMessage = isSogCategory
                        ? "No slab-on-grade floor selected."
                        : "No Footings found in host or linked models.";
                    TaskDialog.Show("RevitSuite", notFoundMessage);
                    LogManager.Warn(correlationId, $"Place cancelled - {notFoundMessage}");
                    return Result.Cancelled;
                }

                // Get Control Point family symbol
                var controlPointSymbol = FindControlPointSymbol(doc, config);
                if (controlPointSymbol == null)
                {
                    TaskDialog.Show("RevitSuite", $"Control Point family '{config.DefaultFamilyName}' not found in project.\nPlease load the family first.");
                    LogManager.Error(correlationId, "Control Point family not found.");
                    return Result.Failed;
                }

                // Get survey point transform to convert internal coords to survey coords
                var projectLocation = doc.ActiveProjectLocation;
                var sharedToProject = projectLocation?.GetTransform() ?? Transform.Identity;
                var projectToShared = sharedToProject.Inverse;

                int pointsPlaced = 0;
                int duplicatesSkipped = 0;
                int sogPointIndex = 1;
                var placedLocations = new List<XYZ>(); // Track all placed point locations
                const double minDistance = 0.01; // Minimum distance between points (0.01 ft = ~1/8 inch)

                using (var tx = new Transaction(doc, "RevitSuite: Place Control Points"))
                {
                    tx.Start();

                    try
                    {
                        if (!controlPointSymbol.IsActive)
                        {
                            controlPointSymbol.Activate();
                        }

                        foreach (var footingInfo in footings)
                        {
                            var corners = GetFootingCorners(footingInfo.Footing, footingInfo.Transform);
                            if (corners == null || corners.Count == 0)
                            {
                                LogManager.Warn(correlationId, $"Could not extract corners from Footing {footingInfo.Footing.Id.IntegerValue} in {footingInfo.SourceModel} - skipped.");
                                continue;
                            }

                            // De-duplicate corners within this footing
                            corners = DeduplicatePoints(corners, minDistance);

                            if (isSogCategory && sogStartPoint != null)
                            {
                                corners = OrderCornersCounterClockwise(corners, sogStartPoint);
                            }

                            int cornerIndex = 0;
                            foreach (var corner in corners)
                            {
                                // Check if a point has already been placed at this location
                                bool isDuplicate = false;
                                foreach (var placedLocation in placedLocations)
                                {
                                    if (corner.DistanceTo(placedLocation) < minDistance)
                                    {
                                        isDuplicate = true;
                                        break;
                                    }
                                }

                                if (isDuplicate)
                                {
                                    duplicatesSkipped++;
                                    LogManager.Info(correlationId, $"Skipped duplicate corner at ({corner.X:F3}, {corner.Y:F3}, {corner.Z:F3}) for Footing {footingInfo.Footing.Id.IntegerValue}");
                                    cornerIndex++;
                                    continue;
                                }

                                var instance = doc.Create.NewFamilyInstance(corner, controlPointSymbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                                placedLocations.Add(corner); // Track this location

                                string pointNumber;
                                string typeName = null;
                                if (isSogCategory)
                                {
                                    pointNumber = $"SOG-P{pourNumber}-{sogPointIndex}";
                                    sogPointIndex++;
                                }
                                else
                                {
                                    // Determine prefix based on element type
                                    string prefix;
                                    if (footingInfo.Footing is Floor)
                                    {
                                        prefix = "SLB";
                                        typeName = "Foundation Slab";
                                    }
                                    else if (footingInfo.Footing is FamilyInstance fi)
                                    {
                                        var familyName = (fi.Symbol?.Family?.Name ?? "").ToLowerInvariant();
                                        if (familyName.Contains("slab") || familyName.Contains("mat"))
                                        {
                                            prefix = "SLB";
                                            typeName = "Foundation Slab";
                                        }
                                        else
                                        {
                                            prefix = "FTG";
                                            typeName = "Footing";
                                        }
                                    }
                                    else
                                    {
                                        prefix = "FTG";
                                        typeName = "Foundation";
                                    }

                                    // Generate Point Number with element ID reference
                                    var cornerLabel = GetCornerLabel(cornerIndex, corners.Count);
                                    pointNumber = $"{prefix}-{footingInfo.Footing.Id.IntegerValue}-{cornerLabel}";
                                }

                                // Set Point Number
                                SetParameterByGuid(instance, PointNumberGuid, pointNumber);

                                // Store element type, ID, IFC GUID, and source model in Comments
                                var commentsParam = instance.LookupParameter("Comments");
                                if (commentsParam != null && !commentsParam.IsReadOnly && !isSogCategory)
                                {
                                    // Try to get IFC GUID
                                    string ifcGuid = null;
                                    var ifcGuidParam = footingInfo.Footing.get_Parameter(BuiltInParameter.IFC_GUID);
                                    if (ifcGuidParam != null && ifcGuidParam.HasValue)
                                    {
                                        ifcGuid = ifcGuidParam.AsString();
                                    }

                                    var commentText = ifcGuid != null
                                        ? $"{typeName}: {footingInfo.Footing.Id.IntegerValue} | IFC: {ifcGuid} | Model: {footingInfo.SourceModel}"
                                        : $"{typeName}: {footingInfo.Footing.Id.IntegerValue} | Model: {footingInfo.SourceModel}";

                                    commentsParam.Set(commentText);
                                }
                                else if (commentsParam != null && !commentsParam.IsReadOnly && isSogCategory)
                                {
                                    commentsParam.Set(string.Empty);
                                }

                                // Convert corner from internal coordinates to survey/shared coordinates
                                var sharedCorner = projectToShared.OfPoint(corner);

                                // Set survey/shared coordinates (Easting, Northing, Elevation)
                                SetParameterByGuid(instance, CsEastingGuid, sharedCorner.X);
                                SetParameterByGuid(instance, CsNorthingGuid, sharedCorner.Y);
                                SetParameterByGuid(instance, CsElevationGuid, sharedCorner.Z);

                                pointsPlaced++;
                                cornerIndex++;
                            }
                        }

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        LogManager.Error(correlationId, "Transaction failed during placement.", ex);
                        throw;
                    }
                }

                var summaryMessage = duplicatesSkipped > 0
                    ? $"Placement completed: {pointsPlaced} Control Points placed for {footings.Count} footings. {duplicatesSkipped} duplicate locations skipped."
                    : $"Placement completed: {pointsPlaced} Control Points placed for {footings.Count} footings.";

                LogManager.Info(correlationId, summaryMessage);

                var dialogMessage = duplicatesSkipped > 0
                    ? $"Placement successful!\n\n{pointsPlaced} Control Points placed for {footings.Count} footings.\n\n{duplicatesSkipped} duplicate locations were skipped."
                    : $"Placement successful!\n\n{pointsPlaced} Control Points placed for {footings.Count} footings.";

                TaskDialog.Show("RevitSuite", dialogMessage);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                LogManager.Info(correlationId, "Placement cancelled by user.");
                return Result.Cancelled;
            }
            catch (OperationCanceledException)
            {
                LogManager.Info(correlationId, "Placement cancelled by user.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, "Placement failed.", ex);
                throw;
            }
        }

        private List<FootingInfo> AutoDetectFootings(Document doc, string correlationId)
        {
            var footingInfos = new List<FootingInfo>();

            // Collect from host model - all Structural Foundations (FamilyInstance + Floor slabs)
            var hostFoundations = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();

            foreach (var element in hostFoundations)
            {
                footingInfos.Add(new FootingInfo
                {
                    Footing = element,
                    Transform = Transform.Identity,
                    SourceModel = "Host",
                    LinkInstance = null
                });
            }

            LogManager.Info(correlationId, $"Found {hostFoundations.Count} foundation elements in host model.");

            // Collect from linked models
            var linkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var linkInstance in linkInstances)
            {
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null)
                {
                    continue;
                }

                var transform = linkInstance.GetTotalTransform() ?? Transform.Identity;
                var linkFoundations = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .ToList();

                foreach (var element in linkFoundations)
                {
                    footingInfos.Add(new FootingInfo
                    {
                        Footing = element,
                        Transform = transform,
                        SourceModel = linkDoc.Title,
                        LinkInstance = linkInstance
                    });
                }

                LogManager.Info(correlationId, $"Found {linkFoundations.Count} foundation elements in linked model '{linkDoc.Title}'.");
            }

            LogManager.Info(correlationId, $"Total auto-detected: {footingInfos.Count} foundation elements across all models.");
            return footingInfos;
        }

        private List<XYZ> OrderCornersCounterClockwise(List<XYZ> corners, XYZ startPoint)
        {
            if (corners == null || corners.Count == 0)
                return new List<XYZ>();

            var centerX = corners.Average(p => p.X);
            var centerY = corners.Average(p => p.Y);

            var ordered = corners
                .OrderBy(p => Math.Atan2(p.Y - centerY, p.X - centerX))
                .ToList();

            var startIndex = 0;
            var minDist = double.MaxValue;
            for (var i = 0; i < ordered.Count; i++)
            {
                var dist = ordered[i].DistanceTo(startPoint);
                if (dist < minDist)
                {
                    minDist = dist;
                    startIndex = i;
                }
            }

            if (startIndex == 0)
                return ordered;

            return ordered.Skip(startIndex).Concat(ordered.Take(startIndex)).ToList();
        }

        private FamilySymbol FindControlPointSymbol(Document doc, QaqcConfig config)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.FamilyName == config.DefaultFamilyName)
                .ToList();

            if (symbols.Count == 0)
                return null;

            var namedSymbol = symbols.FirstOrDefault(s => s.Name == config.DefaultTypeName);
            return namedSymbol ?? symbols.FirstOrDefault();
        }

        private FamilySymbol GetOrCreateFieldPointSymbol(Document doc, QaqcConfig config, string modelTypeName, string fieldTypeName)
        {
            // Find all symbols of the Control Point family
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.FamilyName == config.DefaultFamilyName)
                .ToList();

            // Check if field type already exists
            var fieldSymbol = symbols.FirstOrDefault(s => s.Name == fieldTypeName);
            if (fieldSymbol != null)
            {
                if (!fieldSymbol.IsActive)
                    fieldSymbol.Activate();
                return fieldSymbol;
            }

            // Get the model type to duplicate
            var modelSymbol = symbols.FirstOrDefault(s => s.Name == modelTypeName);
            if (modelSymbol == null)
            {
                // If specific model type not found, use any symbol from the family
                modelSymbol = symbols.FirstOrDefault();
            }

            if (modelSymbol == null)
                return null;

            // Duplicate and rename
            var newSymbol = modelSymbol.Duplicate(fieldTypeName) as FamilySymbol;
            if (newSymbol != null && !newSymbol.IsActive)
                newSymbol.Activate();

            return newSymbol;
        }

        private List<XYZ> GetFootingCorners(Element footing, Transform transform)
        {
            var corners = new List<XYZ>();

            try
            {
                // Get geometry options - exclude rebar and other reinforcement
                var options = new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    IncludeNonVisibleObjects = false
                };

                var geomElement = footing.get_Geometry(options);
                if (geomElement == null)
                    return corners;

                // Extract solid geometry (skip rebar and reinforcement)
                Solid topSolid = null;
                double maxZ = double.MinValue;
                double maxVolume = 0;

                foreach (GeometryObject geomObj in geomElement)
                {
                    if (geomObj is Solid solid && solid.Volume > 0.0001)
                    {
                        // Skip small solids (likely rebar or reinforcement)
                        // Main footing/slab solid should be significantly larger
                        if (solid.Volume < 0.1) // Skip solids smaller than 0.1 cubic feet
                            continue;

                        // Get the solid with the largest volume (main footing/slab)
                        if (solid.Volume > maxVolume)
                        {
                            maxVolume = solid.Volume;
                            topSolid = solid;
                        }
                    }
                    else if (geomObj is GeometryInstance geomInst)
                    {
                        var instGeom = geomInst.GetInstanceGeometry();
                        if (instGeom != null)
                        {
                            foreach (GeometryObject instObj in instGeom)
                            {
                                if (instObj is Solid instSolid && instSolid.Volume > 0.1) // Skip small solids (rebar)
                                {
                                    if (instSolid.Volume > maxVolume)
                                    {
                                        maxVolume = instSolid.Volume;
                                        topSolid = instSolid;
                                    }
                                }
                            }
                        }
                    }
                }

                if (topSolid == null)
                    return corners;

                // Find the top face (horizontal face with highest Z)
                Face topFace = null;
                maxZ = double.MinValue;

                foreach (Face face in topSolid.Faces)
                {
                    // Get a point on the face to check its elevation
                    var bboxUV = face.GetBoundingBox();
                    var midUV = (bboxUV.Min + bboxUV.Max) * 0.5;
                    var pointOnFace = face.Evaluate(midUV);

                    var normal = face.ComputeNormal(midUV);
                    // Check if face is roughly horizontal (normal pointing up)
                    if (normal.Z > 0.9) // Horizontal and pointing up
                    {
                        if (pointOnFace.Z > maxZ)
                        {
                            maxZ = pointOnFace.Z;
                            topFace = face;
                        }
                    }
                }

                if (topFace == null)
                    return corners;

                // Extract vertices from ONLY the outer edge loop (skip inner loops = rebar holes)
                // EdgeLoops[0] is the outer boundary, other loops are holes/voids
                var edgeLoops = topFace.EdgeLoops;
                if (edgeLoops.Size == 0)
                    return corners;

                var outerLoop = edgeLoops.get_Item(0); // Get only the outer boundary
                var vertices = new List<XYZ>();

                // Extract ONLY corner points (endpoints where edges meet)
                // This avoids tessellation artifacts and produces clean, predictable results
                foreach (Edge edge in outerLoop)
                {
                    var curve = edge.AsCurve();
                    vertices.Add(curve.GetEndPoint(0));
                }

                // Transform vertices to host space
                foreach (var vertex in vertices)
                {
                    corners.Add(transform.OfPoint(vertex));
                }

                return corners;
            }
            catch (Exception)
            {
                // Fallback: return empty list if geometry extraction fails
                return corners;
            }
        }

        /// <summary>
        /// Removes duplicate points that are closer than the specified tolerance.
        /// </summary>
        private List<XYZ> DeduplicatePoints(List<XYZ> points, double tolerance)
        {
            if (points == null || points.Count == 0)
                return new List<XYZ>();

            var deduplicated = new List<XYZ>();

            foreach (var point in points)
            {
                bool isDuplicate = false;
                foreach (var existing in deduplicated)
                {
                    if (point.DistanceTo(existing) < tolerance)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    deduplicated.Add(point);
                }
            }

            return deduplicated;
        }

        /// <summary>
        /// Simplifies a list of vertices by removing duplicates and points that are too close together.
        /// NOTE: Currently unused - retained for potential future use.
        /// </summary>
        private List<XYZ> SimplifyVertices(List<XYZ> vertices, double tolerance)
        {
            if (vertices == null || vertices.Count == 0)
                return new List<XYZ>();

            var simplified = new List<XYZ>();

            foreach (var vertex in vertices)
            {
                // Check if this vertex is too close to any already-added vertex
                bool tooClose = false;
                foreach (var existing in simplified)
                {
                    if (vertex.DistanceTo(existing) < tolerance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                // Only add if it's far enough from all existing vertices
                if (!tooClose)
                {
                    simplified.Add(vertex);
                }
            }

            return simplified;
        }

        // Helper class to compare XYZ points with tolerance
        private class XYZComparer : IEqualityComparer<XYZ>
        {
            private const double Tolerance = 0.001; // 0.001 feet tolerance

            public bool Equals(XYZ p1, XYZ p2)
            {
                if (p1 == null || p2 == null)
                    return p1 == p2;

                return p1.DistanceTo(p2) < Tolerance;
            }

            public int GetHashCode(XYZ p)
            {
                if (p == null)
                    return 0;

                // Round to tolerance for hash code
                int x = (int)(p.X / Tolerance);
                int y = (int)(p.Y / Tolerance);
                int z = (int)(p.Z / Tolerance);

                return x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
            }
        }

        private string GetCornerLabel(int index, int total)
        {
            return $"C{index + 1}";
        }

        private void SetParameterByGuid(Element element, Guid guid, string value)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.String)
            {
                param.Set(value);
            }
        }

    }
}
