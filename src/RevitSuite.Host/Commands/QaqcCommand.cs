using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class QaqcCommand : IExternalCommand
    {
        private const string SogCategoryName = "Floor - Slab on Grade";
        // Shared parameter GUIDs
        private static readonly Guid PointNumberGuid = Guid.Parse("7b436883-9c3e-4a23-b014-f3ed5c5cf91d");
        private static readonly Guid CsEastingGuid = Guid.Parse("8e5a10e0-7c84-443f-a368-985247c7cd95");
        private static readonly Guid CsNorthingGuid = Guid.Parse("99272d59-c0c6-47b9-982e-48da2ff7b42f");
        private static readonly Guid CsElevationGuid = Guid.Parse("750d5407-b38e-4955-9338-30ec456be859");
        private static readonly Guid DeviationEastingGuid = Guid.Parse("3ed84adf-d4e6-4b84-8ea5-367762e5052e");
        private static readonly Guid DeviationNorthingGuid = Guid.Parse("3bf56bcc-b5f9-4ed6-97a3-b50e78d0d574");

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "QaqcCommand started.");

            try
            {
                var uiDoc = data.Application.ActiveUIDocument;
                if (uiDoc == null)
                {
                    message = "No active document.";
                    LogManager.Warn(correlationId, message);
                    return Result.Failed;
                }

                var doc = uiDoc.Document;
                var config = QaqcConfig.Load();

                // Show category & mode selection dialog
                string selectedCategory;
                QaqcMode selectedMode;
                int selectedPourNumber;
                using (var form = new QaqcDialog())
                {
                    if (form.ShowDialog() != DialogResult.OK)
                    {
                        LogManager.Info(correlationId, "QAQC cancelled by user.");
                        return Result.Cancelled;
                    }
                    selectedCategory = form.SelectedCategory;
                    selectedMode = form.SelectedMode;
                    selectedPourNumber = form.SelectedPourNumber;
                }

                LogManager.Info(correlationId, $"QAQC mode: {selectedMode}, Category: {selectedCategory}");

                if (selectedMode == QaqcMode.Place)
                {
                    return ExecutePlace(correlationId, uiDoc, doc, config, selectedCategory, selectedPourNumber);
                }
                else if (selectedMode == QaqcMode.Export)
                {
                    return ExecuteExport(correlationId, uiDoc, doc, config, selectedCategory);
                }
                else
                {
                    return ExecuteImport(correlationId, uiDoc, doc, config, selectedCategory);
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                LogManager.Error(correlationId, "QAQC command failed.", ex);
                TaskDialog.Show("RevitSuite", $"QAQC failed: {ex.Message}");
                return Result.Failed;
            }
        }

        #region Place Mode

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

        #endregion

        #region Export Mode

        private Result ExecuteExport(string correlationId, UIDocument uiDoc, Document doc, QaqcConfig config, string category)
        {
            try
            {
                // Auto-collect all Control Point instances
                var controlPoints = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Site)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Symbol?.Family?.Name == config.DefaultFamilyName)
                    .ToList();

                if (controlPoints == null || controlPoints.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No Control Points found in the model.");
                    LogManager.Warn(correlationId, "Export cancelled - no Control Points found.");
                    return Result.Cancelled;
                }

                LogManager.Info(correlationId, $"Found {controlPoints.Count} Control Points for export.");

                // Extract model coordinates
                var records = ExtractModelCoordinates(doc, controlPoints, correlationId);
                if (records.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No valid Control Points with required parameters found.");
                    LogManager.Warn(correlationId, "Export cancelled - no valid Control Points.");
                    return Result.Cancelled;
                }

                // Prompt for save location
                var saveDialog = new SaveFileDialog
                {
                    Title = "Export QAQC Control Points",
                    Filter = "CSV Files (*.csv)|*.csv",
                    FileName = $"QAQC_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                    AddExtension = true,
                    DefaultExt = "csv",
                    OverwritePrompt = true
                };

                if (saveDialog.ShowDialog() != DialogResult.OK)
                {
                    LogManager.Info(correlationId, "Export cancelled by user.");
                    return Result.Cancelled;
                }

                // Write CSV
                WriteExportCsv(saveDialog.FileName, records, config.CoordinatePrecision);

                LogManager.Info(correlationId, $"Export completed: {records.Count} points written to '{saveDialog.FileName}'");
                TaskDialog.Show("RevitSuite", $"Export successful!\n\n{records.Count} Control Points exported to:\n{saveDialog.FileName}");

                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                LogManager.Info(correlationId, "Export cancelled by user.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, "Export failed.", ex);
                throw;
            }
        }

        private List<ControlPointRecord> ExtractModelCoordinates(Document doc, List<FamilyInstance> instances, string correlationId)
        {
            var records = new List<ControlPointRecord>();

            foreach (var instance in instances)
            {
                try
                {
                    var pointNumber = GetParameterValueString(instance, PointNumberGuid);
                    if (string.IsNullOrWhiteSpace(pointNumber))
                    {
                        LogManager.Warn(correlationId, $"Control Point {instance.Id.IntegerValue} missing Point Number - skipped.");
                        continue;
                    }

                    var easting = GetParameterValueDouble(instance, CsEastingGuid);
                    var northing = GetParameterValueDouble(instance, CsNorthingGuid);
                    var elevation = GetParameterValueDouble(instance, CsElevationGuid);

                    if (!easting.HasValue || !northing.HasValue || !elevation.HasValue)
                    {
                        LogManager.Warn(correlationId, $"Control Point {pointNumber} missing coordinate parameters - skipped.");
                        continue;
                    }

                    string description = null;
                    var commentsParam = instance.LookupParameter("Comments");
                    if (commentsParam != null && commentsParam.StorageType == StorageType.String)
                    {
                        description = commentsParam.AsString();
                    }

                    records.Add(new ControlPointRecord
                    {
                        PointNumber = pointNumber,
                        Description = description,
                        ModelEasting = easting.Value,
                        ModelNorthing = northing.Value,
                        ModelElevation = elevation.Value,
                        ElementId = instance.Id,
                        UniqueId = instance.UniqueId
                    });
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to extract coordinates from Control Point {instance.Id.IntegerValue}: {ex.Message}");
                }
            }

            return records;
        }

        private void WriteExportCsv(string path, List<ControlPointRecord> records, int precision)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                // Header
                writer.WriteLine("Point Number,Northing,Easting,Elevation,Description");

                // Data rows
                foreach (var record in records.OrderBy(r => r.PointNumber))
                {
                    var format = $"F{precision}";
                    var description = record.Description ?? "";
                    writer.WriteLine($"{EscapeCsvValue(record.PointNumber)}," +
                        $"{record.ModelNorthing.ToString(format, CultureInfo.InvariantCulture)}," +
                        $"{record.ModelEasting.ToString(format, CultureInfo.InvariantCulture)}," +
                        $"{record.ModelElevation.ToString(format, CultureInfo.InvariantCulture)}," +
                        $"{EscapeCsvValue(description)}");
                }
            }
        }

        #endregion

        #region Import Mode

        private Result ExecuteImport(string correlationId, UIDocument uiDoc, Document doc, QaqcConfig config, string category)
        {
            try
            {
                // Prompt for CSV file
                var openDialog = new OpenFileDialog
                {
                    Title = "Import QAQC Field Data",
                    Filter = "CSV Files (*.csv)|*.csv",
                    CheckFileExists = true
                };

                if (openDialog.ShowDialog() != DialogResult.OK)
                {
                    LogManager.Info(correlationId, "Import cancelled by user.");
                    return Result.Cancelled;
                }

                // Parse CSV
                var records = ParseCsvImport(openDialog.FileName, correlationId);
                if (records.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No valid data found in CSV file.");
                    LogManager.Warn(correlationId, "Import cancelled - no valid data.");
                    return Result.Cancelled;
                }

                // Match to elements and calculate deviations
                var deviations = CalculateDeviations(doc, records, config, category, correlationId);
                if (deviations.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No matching Control Points found in model.");
                    LogManager.Warn(correlationId, "Import cancelled - no matches.");
                    return Result.Cancelled;
                }

                // Update parameters and create visualizations
                using (var tx = new Transaction(doc, "RevitSuite: QAQC Import"))
                {
                    tx.Start();

                    try
                    {
                        UpdateSharedParameters(doc, deviations, correlationId);
                        PlaceFieldPoints(doc, deviations, config, correlationId);
                        // CreateDeviationLines(doc, deviations, correlationId); // Temporarily disabled for performance
                        ApplyGraphicOverrides(doc, deviations, config, correlationId);
                        if (config.CreateDeviationArrows)
                        {
                            CreateDeviationIndicators(doc, deviations, config, correlationId);
                        }
                        CreateDeviationAnnotations(doc, deviations, correlationId);

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        LogManager.Error(correlationId, "Transaction failed during import.", ex);
                        throw;
                    }
                }

                var greenCount = deviations.Count(d => d.Status == ToleranceStatus.Green);
                var yellowCount = deviations.Count(d => d.Status == ToleranceStatus.Yellow);
                var redCount = deviations.Count(d => d.Status == ToleranceStatus.Red);

                LogManager.Info(correlationId, $"Import completed: {deviations.Count} points analyzed. Green: {greenCount}, Yellow: {yellowCount}, Red: {redCount}");
                TaskDialog.Show("RevitSuite",
                    $"Import successful!\n\n" +
                    $"{deviations.Count} Control Points analyzed:\n" +
                    $"  Green (within tolerance): {greenCount}\n" +
                    $"  Yellow (warning): {yellowCount}\n" +
                    $"  Red (exceeds tolerance): {redCount}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                LogManager.Error(correlationId, "Import failed.", ex);
                throw;
            }
        }

        private List<ControlPointRecord> ParseCsvImport(string path, string correlationId)
        {
            var records = new List<ControlPointRecord>();

            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                // Read header
                var headerLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(headerLine))
                {
                    throw new InvalidDataException("CSV file is empty.");
                }

                // Validate header (should have 8 columns with Footing ID)
                var headers = headerLine.Split(',');
                if (headers.Length < 8)
                {
                    throw new InvalidDataException($"Invalid CSV format. Expected 8 columns, found {headers.Length}.");
                }

                int lineNumber = 1;
                while (!reader.EndOfStream)
                {
                    lineNumber++;
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var values = line.Split(',');
                    if (values.Length < 8)
                    {
                        LogManager.Warn(correlationId, $"CSV line {lineNumber} has insufficient columns - skipped.");
                        continue;
                    }

                    try
                    {
                        var pointNumber = values[0].Trim();
                        if (string.IsNullOrWhiteSpace(pointNumber))
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has empty Point Number - skipped.");
                            continue;
                        }

                        // Parse Field coordinates (columns 5, 6, 7 - after Footing ID)
                        if (!double.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldEasting) ||
                            !double.TryParse(values[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldNorthing) ||
                            !double.TryParse(values[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var fieldElevation))
                        {
                            LogManager.Warn(correlationId, $"CSV line {lineNumber} has invalid Field coordinates - skipped.");
                            continue;
                        }

                        records.Add(new ControlPointRecord
                        {
                            PointNumber = pointNumber,
                            FieldEasting = fieldEasting,
                            FieldNorthing = fieldNorthing,
                            FieldElevation = fieldElevation
                        });
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"CSV line {lineNumber} parse error: {ex.Message} - skipped.");
                    }
                }
            }

            return records;
        }

        private List<DeviationResult> CalculateDeviations(Document doc, List<ControlPointRecord> records, QaqcConfig config, string category, string correlationId)
        {
            var deviations = new List<DeviationResult>();

            // Get category-specific settings
            var categorySettings = config.GetCategorySettings(category);
            LogManager.Info(correlationId, $"Using {category} settings: ToleranceGreen={categorySettings.ToleranceGreen}, ToleranceYellow={categorySettings.ToleranceYellow}, ComparisonMethod={categorySettings.ComparisonMethod}");

            // Collect all Control Points from document
            var allControlPoints = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => fi.Symbol?.Family?.Name == config.DefaultFamilyName)
                .ToList();

            // Build dictionary of control points by Point Number for fast O(1) lookup
            var controlPointsDict = new Dictionary<string, FamilyInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var cp in allControlPoints)
            {
                var pointNum = GetParameterValueString(cp, PointNumberGuid);
                if (!string.IsNullOrEmpty(pointNum) && !controlPointsDict.ContainsKey(pointNum))
                {
                    controlPointsDict[pointNum] = cp;
                }
            }

            LogManager.Info(correlationId, $"Built control points dictionary: {controlPointsDict.Count} entries for fast matching.");

            foreach (var record in records)
            {
                // Fast dictionary lookup instead of linear search
                if (!controlPointsDict.TryGetValue(record.PointNumber, out var matchingElement))
                {
                    LogManager.Warn(correlationId, $"Point Number '{record.PointNumber}' not found in model - skipped.");
                    continue;
                }

                // Get model coordinates
                var modelEasting = GetParameterValueDouble(matchingElement, CsEastingGuid);
                var modelNorthing = GetParameterValueDouble(matchingElement, CsNorthingGuid);
                var modelElevation = GetParameterValueDouble(matchingElement, CsElevationGuid);

                if (!modelEasting.HasValue || !modelNorthing.HasValue || !modelElevation.HasValue)
                {
                    LogManager.Warn(correlationId, $"Point Number '{record.PointNumber}' missing model coordinates - skipped.");
                    continue;
                }

                // Calculate deviations
                var devEasting = record.FieldEasting.Value - modelEasting.Value;
                var devNorthing = record.FieldNorthing.Value - modelNorthing.Value;
                var devElevation = record.FieldElevation.Value - modelElevation.Value;
                var horizontalDev = Math.Sqrt(devEasting * devEasting + devNorthing * devNorthing);
                var totalDev = Math.Sqrt(devEasting * devEasting + devNorthing * devNorthing + devElevation * devElevation);

                // Determine which deviation value to use based on comparison method
                double comparisonValue;
                switch (categorySettings.ComparisonMethod.ToLower())
                {
                    case "vertical":
                        comparisonValue = Math.Abs(devElevation);
                        break;
                    case "total":
                        comparisonValue = totalDev;
                        break;
                    case "horizontal":
                    default:
                        comparisonValue = horizontalDev;
                        break;
                }

                // Determine tolerance status using category-specific tolerances
                var status = ToleranceStatus.Green;
                if (comparisonValue > categorySettings.ToleranceYellow)
                    status = ToleranceStatus.Red;
                else if (comparisonValue > categorySettings.ToleranceGreen)
                    status = ToleranceStatus.Yellow;

                // Get model point location
                XYZ modelPoint = null;
                if (matchingElement.Location is LocationPoint locPoint)
                {
                    modelPoint = locPoint.Point;
                }

                deviations.Add(new DeviationResult
                {
                    PointNumber = record.PointNumber,
                    ElementId = matchingElement.Id,
                    UniqueId = matchingElement.UniqueId,
                    DeviationEasting = devEasting,
                    DeviationNorthing = devNorthing,
                    DeviationElevation = devElevation,
                    HorizontalDeviation = horizontalDev,
                    TotalDeviation = totalDev,
                    Status = status,
                    ModelPoint = modelPoint
                });
            }

            return deviations;
        }

        private void UpdateSharedParameters(Document doc, List<DeviationResult> deviations, string correlationId)
        {
            foreach (var deviation in deviations)
            {
                var element = doc.GetElement(deviation.ElementId);
                if (element == null)
                    continue;

                try
                {
                    SetParameterByGuid(element, DeviationEastingGuid, deviation.DeviationEasting);
                    SetParameterByGuid(element, DeviationNorthingGuid, deviation.DeviationNorthing);
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to update parameters for Point {deviation.PointNumber}: {ex.Message}");
                }
            }
        }

        private void PlaceFieldPoints(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            // Get or create the "Field" type
            var fieldSymbol = GetOrCreateFieldPointSymbol(doc, config, "Coordination", "Field");
            if (fieldSymbol == null)
            {
                LogManager.Error(correlationId, "Failed to create or find 'Field' type for Control Point family.");
                return;
            }

            // Collect existing field points by Point Number for smart update
            var existingFieldPoints = new Dictionary<string, FamilyInstance>(StringComparer.OrdinalIgnoreCase);
            var existingFieldPointsList = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => fi.Symbol?.Name == "Field")
                .ToList();

            foreach (var fp in existingFieldPointsList)
            {
                var pointNum = GetParameterValueString(fp, PointNumberGuid);
                if (!string.IsNullOrEmpty(pointNum))
                {
                    existingFieldPoints[pointNum] = fp;
                }
            }

            LogManager.Info(correlationId, $"Found {existingFieldPoints.Count} existing field points.");

            // Get survey point transform
            var projectLocation = doc.ActiveProjectLocation;
            var sharedToProject = projectLocation?.GetTransform() ?? Transform.Identity;
            var projectToShared = sharedToProject.Inverse;

            int fieldPointsPlaced = 0;
            int fieldPointsUpdated = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    // Calculate field point location (model point + deviations)
                    var fieldPoint = new XYZ(
                        deviation.ModelPoint.X + deviation.DeviationEasting,
                        deviation.ModelPoint.Y + deviation.DeviationNorthing,
                        deviation.ModelPoint.Z + deviation.DeviationElevation);

                    FamilyInstance fieldInstance;

                    // Check if field point exists - update vs create
                    if (existingFieldPoints.TryGetValue(deviation.PointNumber, out var existingInstance))
                    {
                        // UPDATE existing field point
                        fieldInstance = existingInstance;

                        // Move to new location
                        if (fieldInstance.Location is LocationPoint locPoint)
                        {
                            locPoint.Point = fieldPoint;
                        }

                        fieldPointsUpdated++;
                    }
                    else
                    {
                        // CREATE new field point
                        fieldInstance = doc.Create.NewFamilyInstance(fieldPoint, fieldSymbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                        // Set Point Number (same as model point for easy matching)
                        SetParameterByGuid(fieldInstance, PointNumberGuid, deviation.PointNumber);

                        fieldPointsPlaced++;
                    }

                    // Convert to survey/shared coordinates
                    var sharedFieldPoint = projectToShared.OfPoint(fieldPoint);

                    // Update coordinates (for both new and existing)
                    SetParameterByGuid(fieldInstance, CsEastingGuid, sharedFieldPoint.X);
                    SetParameterByGuid(fieldInstance, CsNorthingGuid, sharedFieldPoint.Y);
                    SetParameterByGuid(fieldInstance, CsElevationGuid, sharedFieldPoint.Z);

                    // Update deviation parameters (for both new and existing)
                    SetParameterByGuid(fieldInstance, DeviationEastingGuid, deviation.DeviationEasting);
                    SetParameterByGuid(fieldInstance, DeviationNorthingGuid, deviation.DeviationNorthing);

                    // Update comments (for both new and existing)
                    var commentsParam = fieldInstance.LookupParameter("Comments");
                    if (commentsParam != null && !commentsParam.IsReadOnly)
                    {
                        commentsParam.Set($"Field measurement for {deviation.PointNumber}");
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to place/update field point for {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Field points: {fieldPointsPlaced} created, {fieldPointsUpdated} updated.");
        }

        private void CreateDeviationLines(Document doc, List<DeviationResult> deviations, string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend)
            {
                LogManager.Warn(correlationId, "Active view not suitable for detail lines - skipping deviation lines.");
                return;
            }

            int linesCreated = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    // Calculate field point location
                    var fieldPoint = new XYZ(
                        deviation.ModelPoint.X + deviation.DeviationEasting,
                        deviation.ModelPoint.Y + deviation.DeviationNorthing,
                        deviation.ModelPoint.Z + deviation.DeviationElevation);

                    // Create a line between model point and field point
                    var line = Line.CreateBound(deviation.ModelPoint, fieldPoint);

                    // Create detail line in the view
                    var detailLine = doc.Create.NewDetailCurve(view, line);

                    if (detailLine != null)
                    {
                        linesCreated++;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create deviation line for {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Created {linesCreated} deviation lines.");
        }

        private void ApplyGraphicOverrides(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule)
            {
                LogManager.Warn(correlationId, "Active view not suitable for graphic overrides - skipping visualization.");
                return;
            }

            var greenColor = new Autodesk.Revit.DB.Color(34, 197, 94);
            var yellowColor = new Autodesk.Revit.DB.Color(234, 179, 8);
            var redColor = new Autodesk.Revit.DB.Color(239, 68, 68);

            // Build dictionary of field points by Point Number for fast lookup
            var fieldPointsDict = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            var fieldPoints = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Site)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => fi.Symbol?.Name == "Field")
                .ToList();

            foreach (var fp in fieldPoints)
            {
                var pointNum = GetParameterValueString(fp, PointNumberGuid);
                if (!string.IsNullOrEmpty(pointNum) && !fieldPointsDict.ContainsKey(pointNum))
                {
                    fieldPointsDict[pointNum] = fp.Id;
                }
            }

            LogManager.Info(correlationId, $"Built field points dictionary: {fieldPointsDict.Count} entries.");

            foreach (var deviation in deviations)
            {
                var color = deviation.Status switch
                {
                    ToleranceStatus.Green => greenColor,
                    ToleranceStatus.Yellow => yellowColor,
                    ToleranceStatus.Red => redColor,
                    _ => greenColor
                };

                var overrides = new OverrideGraphicSettings();
                overrides.SetProjectionLineColor(color);
                overrides.SetProjectionLineWeight(5);

                // Apply to model point
                var modelElement = doc.GetElement(deviation.ElementId);
                if (modelElement != null)
                {
                    try
                    {
                        view.SetElementOverrides(modelElement.Id, overrides);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"Failed to apply graphic override for model point {deviation.PointNumber}: {ex.Message}");
                    }
                }

                // Apply to corresponding field point using fast dictionary lookup
                if (fieldPointsDict.TryGetValue(deviation.PointNumber, out var fieldPointId))
                {
                    try
                    {
                        view.SetElementOverrides(fieldPointId, overrides);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warn(correlationId, $"Failed to apply graphic override for field point {deviation.PointNumber}: {ex.Message}");
                    }
                }
            }
        }

        private void CreateDeviationIndicators(Document doc, List<DeviationResult> deviations, QaqcConfig config, string correlationId)
        {
            var greenMaterialId = EnsureMaterial(doc, "QAQC_Green", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(34, 197, 94));
            var yellowMaterialId = EnsureMaterial(doc, "QAQC_Yellow", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(234, 179, 8));
            var redMaterialId = EnsureMaterial(doc, "QAQC_Red", config.VisualizationTransparency, new Autodesk.Revit.DB.Color(239, 68, 68));

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                try
                {
                    var materialId = deviation.Status switch
                    {
                        ToleranceStatus.Green => greenMaterialId,
                        ToleranceStatus.Yellow => yellowMaterialId,
                        ToleranceStatus.Red => redMaterialId,
                        _ => greenMaterialId
                    };

                    var fieldPoint = new XYZ(
                        deviation.ModelPoint.X + deviation.DeviationEasting,
                        deviation.ModelPoint.Y + deviation.DeviationNorthing,
                        deviation.ModelPoint.Z + deviation.DeviationElevation);

                    var arrow = BuildArrowGeometry(deviation.ModelPoint, fieldPoint, config.ArrowScaleFactor, materialId);
                    if (arrow != null && arrow.Count > 0)
                    {
                        var directShape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        directShape.SetShape(arrow);
                        directShape.Name = $"Deviation Indicator - {deviation.PointNumber}";
                        directShape.ApplicationId = "RevitSuite.QAQC";
                        directShape.ApplicationDataId = deviation.UniqueId;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create deviation indicator for Point {deviation.PointNumber}: {ex.Message}");
                }
            }
        }

        #endregion

        #region Helper Methods

        private bool IsPointInCropRegion(XYZ point, XYZ min, XYZ max)
        {
            if (min == null || max == null)
                return true;

            return point.X >= min.X && point.X <= max.X &&
                   point.Y >= min.Y && point.Y <= max.Y &&
                   point.Z >= min.Z && point.Z <= max.Z;
        }

        private Parameter GetParameterByGuid(Element element, Guid guid)
        {
            foreach (Parameter param in element.Parameters)
            {
                if (param.IsShared && param.GUID == guid)
                    return param;
            }
            return null;
        }

        private string GetParameterValueString(Element element, Guid guid)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && param.StorageType == StorageType.String)
                return param.AsString();
            return null;
        }

        private double? GetParameterValueDouble(Element element, Guid guid)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && param.StorageType == StorageType.Double && param.HasValue)
                return param.AsDouble();
            return null;
        }

        private void SetParameterByGuid(Element element, Guid guid, double value)
        {
            var param = GetParameterByGuid(element, guid);
            if (param != null && !param.IsReadOnly && param.StorageType == StorageType.Double)
            {
                param.Set(value);
            }
        }

        private string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }

        private ElementId EnsureMaterial(Document doc, string name, int transparency, Autodesk.Revit.DB.Color color)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                try
                {
                    existing.Transparency = transparency;
                    existing.Color = color;
                }
                catch
                {
                    // Ignore updates
                }

                return existing.Id;
            }

            var materialId = Material.Create(doc, name);
            if (doc.GetElement(materialId) is Material created)
            {
                try
                {
                    created.Transparency = transparency;
                    created.Color = color;
                }
                catch
                {
                    // Ignore updates
                }
            }

            return materialId;
        }

        private List<GeometryObject> BuildArrowGeometry(XYZ start, XYZ end, double scaleFactor, ElementId materialId)
        {
            var geometryList = new List<GeometryObject>();

            var deviation = end - start;
            var deviationLength = deviation.GetLength();

            if (deviationLength < 1e-6)
                return geometryList; // Too small

            // Scale the arrow
            var scaledLength = Math.Min(Math.Max(deviationLength * scaleFactor, 0.1), 10.0); // Min 0.1 ft, Max 10 ft
            var direction = deviation.Normalize();
            var scaledEnd = start + (direction * scaledLength);

            // Create simple line-based arrow (cylinder shaft)
            var shaftRadius = 0.05; // 0.05 ft diameter
            var line = Line.CreateBound(start, scaledEnd);

            // Note: Full arrow geometry with cone would require more complex solid creation
            // For now, creating a simple representation
            // In production, you would create a cylinder and cone using GeometryCreationUtilities

            return geometryList;
        }

        private void CreateDeviationAnnotations(Document doc, List<DeviationResult> deviations, string correlationId)
        {
            var view = doc.ActiveView;
            if (view == null || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.Legend || view.ViewType == ViewType.ThreeD)
            {
                LogManager.Warn(correlationId, "Active view not suitable for text notes - skipping annotations. Use a plan, section, or elevation view.");
                return;
            }

            // Delete all existing text notes in this view to prevent duplicates
            try
            {
                var existingTextNotes = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote))
                    .ToList();

                int deletedCount = 0;
                foreach (var textNote in existingTextNotes)
                {
                    try
                    {
                        doc.Delete(textNote.Id);
                        deletedCount++;
                    }
                    catch
                    {
                        // Some text notes might be locked or system-owned, skip them
                    }
                }

                LogManager.Info(correlationId, $"Deleted {deletedCount} existing annotations in current view.");
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to delete old annotations: {ex.Message}");
            }

            // Delete all existing detail lines in this view (leader lines)
            try
            {
                var existingDetailLines = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(CurveElement))
                    .OfType<DetailCurve>()
                    .ToList();

                int deletedLines = 0;
                foreach (var line in existingDetailLines)
                {
                    try
                    {
                        doc.Delete(line.Id);
                        deletedLines++;
                    }
                    catch
                    {
                        // Skip locked elements
                    }
                }

                LogManager.Info(correlationId, $"Deleted {deletedLines} existing detail lines in current view.");
            }
            catch (Exception ex)
            {
                LogManager.Warn(correlationId, $"Failed to delete old detail lines: {ex.Message}");
            }

            // Get a valid TextNoteType from the document
            var textNoteType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .FirstOrDefault() as TextNoteType;

            if (textNoteType == null)
            {
                LogManager.Warn(correlationId, "No TextNoteType found in document - skipping annotations.");
                return;
            }

            // Get crop region bounds for filtering
            var cropBox = view.CropBox;
            var hasCropRegion = cropBox != null;
            var minCorner = hasCropRegion ? cropBox.Min : null;
            var maxCorner = hasCropRegion ? cropBox.Max : null;

            LogManager.Info(correlationId, $"Using TextNoteType: {textNoteType.Name}");

            int annotationsCreated = 0;
            int skippedOutsideCrop = 0;

            foreach (var deviation in deviations)
            {
                if (deviation.ModelPoint == null)
                    continue;

                // Skip points outside crop region
                if (hasCropRegion && !IsPointInCropRegion(deviation.ModelPoint, minCorner, maxCorner))
                {
                    skippedOutsideCrop++;
                    continue;
                }

                try
                {
                    // Convert deviations to feet-inches format (Revit standard)
                    var eastingFtIn = FormatFeetInches(Math.Abs(deviation.DeviationEasting));
                    var northingFtIn = FormatFeetInches(Math.Abs(deviation.DeviationNorthing));

                    // Add +/- signs
                    var eastingSign = deviation.DeviationEasting >= 0 ? "+" : "-";
                    var northingSign = deviation.DeviationNorthing >= 0 ? "+" : "-";

                    // Simple format: E and N deviations only (no point number)
                    var annotationText = $"E: {eastingSign}{eastingFtIn}\n" +
                        $"N: {northingSign}{northingFtIn}";

                    // Offset annotation point slightly to the right and up from Control Point
                    var annotationPoint = new XYZ(
                        deviation.ModelPoint.X + 2.0,  // 2 feet to the right
                        deviation.ModelPoint.Y + 1.0,  // 1 foot up
                        deviation.ModelPoint.Z);

                    var textNote = TextNote.Create(doc, view.Id, annotationPoint, annotationText, textNoteType.Id);

                    if (textNote != null)
                    {
                        // Create a detail line from annotation to model point as a leader
                        try
                        {
                            var leaderLine = Line.CreateBound(annotationPoint, deviation.ModelPoint);
                            doc.Create.NewDetailCurve(view, leaderLine);
                        }
                        catch
                        {
                            // Leader line creation might fail, continue anyway
                        }

                        annotationsCreated++;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Warn(correlationId, $"Failed to create annotation for Point {deviation.PointNumber}: {ex.Message}");
                }
            }

            LogManager.Info(correlationId, $"Created {annotationsCreated} annotations. Skipped {skippedOutsideCrop} points outside crop region.");
        }

        private string FormatFeetInches(double feet)
        {
            // Convert feet to feet and inches
            int wholeFeet = (int)Math.Floor(feet);
            double remainingInches = (feet - wholeFeet) * 12.0;

            // Round to nearest 1/8 inch
            double eighths = Math.Round(remainingInches * 8.0);
            int wholeInches = (int)(eighths / 8.0);
            int fractionalEighths = (int)(eighths % 8);

            // Build string
            if (wholeFeet == 0 && wholeInches == 0 && fractionalEighths == 0)
                return "0\"";

            var result = "";
            if (wholeFeet > 0)
                result += $"{wholeFeet}'-";

            if (wholeInches > 0 || wholeFeet > 0)
                result += $"{wholeInches}";

            // Add fraction if needed
            if (fractionalEighths > 0)
            {
                // Simplify fraction
                var (num, den) = SimplifyFraction(fractionalEighths, 8);
                result += $" {num}/{den}";
            }

            result += "\"";

            return result.Replace("'-0\"", "'"); // Clean up cases like "1'-0"" to "1'"
        }

        private (int numerator, int denominator) SimplifyFraction(int num, int den)
        {
            // Simplify fraction (e.g., 4/8 -> 1/2)
            int gcd = GCD(num, den);
            return (num / gcd, den / gcd);
        }

        private int GCD(int a, int b)
        {
            while (b != 0)
            {
                int temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        #endregion

        #region Nested Classes

        private enum QaqcMode
        {
            Place,
            Export,
            ImportAndAnalyze
        }

        private enum ToleranceStatus
        {
            Green,
            Yellow,
            Red
        }

        private class FootingInfo
        {
            public Element Footing { get; set; }
            public Transform Transform { get; set; }
            public string SourceModel { get; set; }
            public RevitLinkInstance LinkInstance { get; set; }
        }

        private class ControlPointRecord
        {
            public string PointNumber { get; set; }
            public string Description { get; set; }
            public double ModelEasting { get; set; }
            public double ModelNorthing { get; set; }
            public double ModelElevation { get; set; }
            public double? FieldEasting { get; set; }
            public double? FieldNorthing { get; set; }
            public double? FieldElevation { get; set; }
            public ElementId ElementId { get; set; }
            public string UniqueId { get; set; }
        }

        private class DeviationResult
        {
            public string PointNumber { get; set; }
            public ElementId ElementId { get; set; }
            public string UniqueId { get; set; }
            public double DeviationEasting { get; set; }
            public double DeviationNorthing { get; set; }
            public double DeviationElevation { get; set; }
            public double HorizontalDeviation { get; set; }
            public double TotalDeviation { get; set; }
            public ToleranceStatus Status { get; set; }
            public XYZ ModelPoint { get; set; }
        }

        private enum SogSelectionMode
        {
            Host,
            Linked
        }

        private class SogFloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is Floor)
                {
                    return true;
                }

                return elem.Category != null &&
                       elem.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private class LinkedFloorSelectionFilter : ISelectionFilter
        {
            private readonly Document _doc;

            public LinkedFloorSelectionFilter(Document doc)
            {
                _doc = doc;
            }

            public bool AllowElement(Element elem) => elem is RevitLinkInstance;

            public bool AllowReference(Reference reference, XYZ position)
            {
                if (reference.LinkedElementId == ElementId.InvalidElementId)
                    return false;

                var linkInstance = _doc.GetElement(reference.ElementId) as RevitLinkInstance;
                var linkDoc = linkInstance?.GetLinkDocument();
                if (linkDoc == null)
                    return false;

                var linkedElement = linkDoc.GetElement(reference.LinkedElementId);
                return linkedElement is Floor;
            }
        }

        private SogSelectionMode? PromptSogSelectionMode()
        {
            var dialog = new TaskDialog("RevitSuite")
            {
                MainInstruction = "Select slab-on-grade location",
                MainContent = "Choose whether the slab is in the host model or a linked model.",
                AllowCancellation = true,
                CommonButtons = TaskDialogCommonButtons.Cancel
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Host model");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Linked model");

            var result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1)
                return SogSelectionMode.Host;
            if (result == TaskDialogResult.CommandLink2)
                return SogSelectionMode.Linked;

            return null;
        }

        private class QaqcDialog : System.Windows.Forms.Form
        {
            private System.Windows.Forms.ComboBox categoryComboBox;
            private System.Windows.Forms.RadioButton exportRadioButton;
            private System.Windows.Forms.RadioButton importRadioButton;
            private System.Windows.Forms.Button okButton;
            private System.Windows.Forms.Button cancelButton;
            private System.Windows.Forms.RadioButton placeRadioButton;
            private System.Windows.Forms.NumericUpDown pourNumericUpDown;
            private System.Windows.Forms.Label pourLabel;

            public string SelectedCategory => categoryComboBox.SelectedItem?.ToString() ?? "Footings";
            public int SelectedPourNumber => (int)(pourNumericUpDown?.Value ?? 1);
            public QaqcMode SelectedMode
            {
                get
                {
                    foreach (System.Windows.Forms.Control control in this.Controls)
                    {
                        if (control is System.Windows.Forms.RadioButton rb && rb.Checked && rb.Tag is QaqcMode mode)
                            return mode;
                    }
                    return QaqcMode.Place;
                }
            }

            public QaqcDialog()
            {
                InitializeComponent();
            }

            private void InitializeComponent()
            {
                this.Text = "QAQC - Control Point Verification";
                this.Size = new System.Drawing.Size(400, 300);
                this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                this.MaximizeBox = false;
                this.MinimizeBox = false;

                var categoryLabel = new System.Windows.Forms.Label
                {
                    Text = "Category:",
                    Location = new System.Drawing.Point(20, 20),
                    Size = new System.Drawing.Size(100, 20)
                };
                this.Controls.Add(categoryLabel);

                categoryComboBox = new System.Windows.Forms.ComboBox
                {
                    Location = new System.Drawing.Point(120, 18),
                    Size = new System.Drawing.Size(240, 25),
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
                };
                categoryComboBox.Items.AddRange(new object[] { "Footings", "Columns", "Walls", SogCategoryName });
                categoryComboBox.SelectedIndex = 0;
                this.Controls.Add(categoryComboBox);

                pourLabel = new System.Windows.Forms.Label
                {
                    Text = "Pour #:",
                    Location = new System.Drawing.Point(20, 50),
                    Size = new System.Drawing.Size(100, 20),
                    Visible = false
                };
                this.Controls.Add(pourLabel);

                pourNumericUpDown = new System.Windows.Forms.NumericUpDown
                {
                    Location = new System.Drawing.Point(120, 48),
                    Size = new System.Drawing.Size(80, 25),
                    Minimum = 1,
                    Maximum = 99,
                    Value = 1,
                    Visible = false
                };
                this.Controls.Add(pourNumericUpDown);

                var modeLabel = new System.Windows.Forms.Label
                {
                    Text = "Mode:",
                    Location = new System.Drawing.Point(20, 85),
                    Size = new System.Drawing.Size(100, 20)
                };
                this.Controls.Add(modeLabel);

                var placeRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Place Control Points",
                    Location = new System.Drawing.Point(120, 85),
                    Size = new System.Drawing.Size(240, 25),
                    Checked = true,
                    Tag = QaqcMode.Place
                };
                this.Controls.Add(placeRadioButton);

                exportRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Export Model Points",
                    Location = new System.Drawing.Point(120, 110),
                    Size = new System.Drawing.Size(240, 25),
                    Tag = QaqcMode.Export
                };
                this.Controls.Add(exportRadioButton);

                importRadioButton = new System.Windows.Forms.RadioButton
                {
                    Text = "Import && Analyze Field Data",
                    Location = new System.Drawing.Point(120, 135),
                    Size = new System.Drawing.Size(240, 25),
                    Tag = QaqcMode.ImportAndAnalyze
                };
                this.Controls.Add(importRadioButton);

                okButton = new System.Windows.Forms.Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(190, 210),
                    Size = new System.Drawing.Size(80, 30),
                    DialogResult = System.Windows.Forms.DialogResult.OK
                };
                this.Controls.Add(okButton);

                cancelButton = new System.Windows.Forms.Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(280, 210),
                    Size = new System.Drawing.Size(80, 30),
                    DialogResult = System.Windows.Forms.DialogResult.Cancel
                };
                this.Controls.Add(cancelButton);

                this.AcceptButton = okButton;
                this.CancelButton = cancelButton;

                categoryComboBox.SelectedIndexChanged += (sender, args) => UpdatePourVisibility();
                UpdatePourVisibility();
            }

            private void UpdatePourVisibility()
            {
                var showPour = string.Equals(categoryComboBox.SelectedItem?.ToString(), SogCategoryName, StringComparison.OrdinalIgnoreCase);
                pourLabel.Visible = showPour;
                pourNumericUpDown.Visible = showPour;
            }
        }

        #endregion
    }
}
