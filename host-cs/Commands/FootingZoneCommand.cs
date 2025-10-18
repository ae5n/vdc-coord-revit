using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Microsoft.VisualBasic;
using RevitSuite.Host.Logging;

namespace RevitSuite.Host.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class FootingZoneCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            LogManager.Info(correlationId, "FootingZoneCommand started.");

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

                if (!TryPromptForParameters("Enter clear depth (feet):", 5.0, out var clearDepth))
                {
                    return Result.Cancelled;
                }

                if (!TryPromptForParameters("Enter slope ratio (horizontal per vertical):", 1.0, out var slopeRatio))
                {
                    return Result.Cancelled;
                }

                if (!TryPromptForParameters("Enter vertical offset from footing/slab bottom (feet):", 0.0, out var zOffset))
                {
                    return Result.Cancelled;
                }

                if (!TryPromptForInt("Enter transparency (0-100):", 50, 0, 100, out var transparency))
                {
                    return Result.Cancelled;
                }

                var includeFootings = PromptYesNo("Include structural foundations?", true);
                var includeSlabs = PromptYesNo("Select slabs/floors to include?", false);

                var slabs = new List<Element>();
                if (includeSlabs)
                {
                    try
                    {
                        var references = uiDoc.Selection.PickObjects(ObjectType.Element, new FloorSelectionFilter(),
                            "Select slabs/floors to include (Esc to finish).");
                        foreach (var reference in references)
                        {
                            var element = doc.GetElement(reference);
                            if (element != null)
                            {
                                slabs.Add(element);
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // User cancelled selection – proceed with whatever was selected.
                    }
                }

                var footings = includeFootings
                    ? new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                        .WhereElementIsNotElementType()
                        .OfType<FamilyInstance>()
                        .ToList()
                    : new List<FamilyInstance>();

                if (footings.Count == 0 && slabs.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No footings or slabs selected to process.");
                    LogManager.Warn(correlationId, "No elements to process for footing zones.");
                    return Result.Cancelled;
                }

                var createdFootingZones = new List<ElementId>();
                var createdSlabZones = new List<ElementId>();
                var skippedElements = new List<ElementId>();

                using (var tx = new Transaction(doc, "RevitSuite: Footing Influence Zones"))
                {
                    tx.Start();

                    var materialId = EnsureMaterial(doc, "Zone Influence – Transparent", transparency, new Color((byte)0, (byte)170, (byte)255));
                    var hostCategoryId = new ElementId(BuiltInCategory.OST_GenericModel);

                    if (includeFootings)
                    {
                        foreach (var footing in footings)
                        {
                            if (!TryCreateFootingZone(doc, footing, hostCategoryId, materialId, clearDepth, slopeRatio,
                                    zOffset, createdFootingZones, skippedElements))
                            {
                                skippedElements.Add(footing.Id);
                            }
                        }
                    }

                    foreach (var slab in slabs)
                    {
                        if (!TryCreateSlabZone(doc, slab, hostCategoryId, materialId, clearDepth, slopeRatio, zOffset,
                                createdSlabZones, skippedElements))
                        {
                            skippedElements.Add(slab.Id);
                        }
                    }

                    tx.Commit();
                }

                var summary = $"Created {createdFootingZones.Count} footing zone(s) and {createdSlabZones.Count} slab zone(s)." +
                              (skippedElements.Count > 0
                                  ? $"\nSkipped {skippedElements.Count} element(s)."
                                  : string.Empty);

                TaskDialog.Show("RevitSuite", summary);
                LogManager.Info(correlationId, summary);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("RevitSuite", "Footing zone command failed. See log for details.");
                LogManager.Error(correlationId, "FootingZoneCommand failed.", ex);
                return Result.Failed;
            }
        }

        private static bool TryCreateFootingZone(
            Document doc,
            FamilyInstance footing,
            ElementId categoryId,
            ElementId materialId,
            double clearDepth,
            double slopeRatio,
            double zOffset,
            IList<ElementId> created,
            IList<ElementId> skipped)
        {
            var box = footing.get_BoundingBox(null);
            if (box == null)
            {
                return false;
            }

            var (length, width) = GetFootingLengthWidth(footing, box);
            if (length == null || width == null)
            {
                return false;
            }

            var (center, rotation) = GetFootingCenterAndRotation(footing, box);

            var topOffset = (box.Min.Z - center.Z) - zOffset;
            var bottomOffset = topOffset - clearDepth;

            var bottomLength = length.Value + 2.0 * slopeRatio * clearDepth;
            var bottomWidth = width.Value + 2.0 * slopeRatio * clearDepth;

            var topLoopLocal = CreateRectangleLoop(length.Value, width.Value, topOffset);
            var bottomLoopLocal = CreateRectangleLoop(bottomLength, bottomWidth, bottomOffset);

            var solid = CreateLoft(topLoopLocal, bottomLoopLocal, materialId);
            var transform = Transform.CreateTranslation(center)
                .Multiply(Transform.CreateRotation(XYZ.BasisZ, rotation));

            var ds = DirectShape.CreateElement(doc, categoryId);
            if (solid != null)
            {
                var transformedSolid = SolidUtils.CreateTransformed(solid, transform);
                ds.SetShape(new List<GeometryObject> { transformedSolid });
            }
            else
            {
                var geometry = CreateFrustumMesh(topLoopLocal, bottomLoopLocal, transform, materialId);
                if (geometry == null || geometry.Count == 0)
                {
                    return false;
                }

                ds.SetShape(geometry);
            }

            SetComments(ds, "Footing Influence (transparent)");
            created.Add(ds.Id);
            return true;
        }

        private static bool TryCreateSlabZone(
            Document doc,
            Element slab,
            ElementId categoryId,
            ElementId materialId,
            double clearDepth,
            double slopeRatio,
            double zOffset,
            IList<ElementId> created,
            IList<ElementId> skipped)
        {
            var box = slab.get_BoundingBox(null);
            if (box == null)
            {
                return false;
            }

            var length = box.Max.X - box.Min.X;
            var width = box.Max.Y - box.Min.Y;
            var center = new XYZ(
                (box.Min.X + box.Max.X) / 2.0,
                (box.Min.Y + box.Max.Y) / 2.0,
                (box.Min.Z + box.Max.Z) / 2.0);

            var bottomElevation = GetParameterDouble(slab, new object[] { "Elevation at Bottom" }) ?? box.Min.Z;

            var topOffset = (bottomElevation - center.Z) - zOffset;
            var bottomOffset = topOffset - clearDepth;

            var bottomLength = length + 2.0 * slopeRatio * clearDepth;
            var bottomWidth = width + 2.0 * slopeRatio * clearDepth;

            var topLoopLocal = CreateRectangleLoop(length, width, topOffset);
            var bottomLoopLocal = CreateRectangleLoop(bottomLength, bottomWidth, bottomOffset);

            var solid = CreateLoft(topLoopLocal, bottomLoopLocal, materialId);
            var transform = Transform.CreateTranslation(center);

            var ds = DirectShape.CreateElement(doc, categoryId);
            if (solid != null)
            {
                var transformedSolid = SolidUtils.CreateTransformed(solid, transform);
                ds.SetShape(new List<GeometryObject> { transformedSolid });
            }
            else
            {
                var geometry = CreateFrustumMesh(topLoopLocal, bottomLoopLocal, transform, materialId);
                if (geometry == null || geometry.Count == 0)
                {
                    return false;
                }

                ds.SetShape(geometry);
            }

            SetComments(ds, "Slab Influence (transparent, bbox)");
            created.Add(ds.Id);
            return true;
        }

        private static Solid? CreateLoft(CurveLoop topLoop, CurveLoop bottomLoop, ElementId materialId)
        {
            var loops = new List<CurveLoop> { topLoop, bottomLoop };
            var options = new SolidOptions(materialId, ElementId.InvalidElementId);

            try
            {
                var loft = GeometryCreationUtilities.CreateLoftGeometry(loops, options);
                if (loft is Solid singleSolid)
                {
                    return singleSolid;
                }

                if (loft is IList<Solid> solidList && solidList.Count > 0)
                {
                    return solidList[0];
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static IList<GeometryObject>? CreateFrustumMesh(
            CurveLoop topLoop,
            CurveLoop bottomLoop,
            Transform transform,
            ElementId materialId)
        {
            var builder = new TessellatedShapeBuilder
            {
                Target = TessellatedShapeBuilderTarget.AnyGeometry,
                Fallback = TessellatedShapeBuilderFallback.Mesh
            };
            builder.OpenConnectedFaceSet(false);

            var topWorld = TransformCurveLoop(topLoop, transform).ToList();
            var bottomWorld = TransformCurveLoop(bottomLoop, transform).ToList();

            if (topWorld.Count != bottomWorld.Count || topWorld.Count < 3)
            {
                return null;
            }

            for (int i = 1; i < topWorld.Count - 1; i++)
            {
                builder.AddFace(new TessellatedFace(new List<XYZ> { topWorld[0], topWorld[i], topWorld[i + 1] }, materialId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { bottomWorld[i + 1], bottomWorld[i], bottomWorld[0] }, materialId));
            }

            for (int i = 0; i < topWorld.Count; i++)
            {
                var i2 = (i + 1) % topWorld.Count;
                builder.AddFace(new TessellatedFace(new List<XYZ> { topWorld[i], topWorld[i2], bottomWorld[i2] }, materialId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { topWorld[i], bottomWorld[i2], bottomWorld[i] }, materialId));
            }

            builder.CloseConnectedFaceSet();
            builder.Build();

            return builder.GetBuildResult().GetGeometricalObjects();
        }

        private static IEnumerable<XYZ> TransformCurveLoop(CurveLoop loop, Transform transform)
        {
            var curves = loop.ToList();
            var points = new List<XYZ>(curves.Count + 1);

            foreach (var curve in curves)
            {
                points.Add(transform.OfPoint(curve.GetEndPoint(0)));
            }

            if (curves.Count > 0)
            {
                var lastEnd = transform.OfPoint(curves[curves.Count - 1].GetEndPoint(1));
                if (!lastEnd.IsAlmostEqualTo(points[0]))
                {
                    points.Add(lastEnd);
                }
            }

            return points;
        }

        private static CurveLoop CreateRectangleLoop(double length, double width, double elevation)
        {
            var halfX = length / 2.0;
            var halfY = width / 2.0;

            var p1 = new XYZ(-halfX, -halfY, elevation);
            var p2 = new XYZ(halfX, -halfY, elevation);
            var p3 = new XYZ(halfX, halfY, elevation);
            var p4 = new XYZ(-halfX, halfY, elevation);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));
            return loop;
        }

        private static (double? Length, double? Width) GetFootingLengthWidth(FamilyInstance footing, BoundingBoxXYZ box)
        {
            var length = GetParameterDouble(footing, new object[]
            {
                BuiltInParameter.INSTANCE_LENGTH_PARAM,
                "Length",
                "L",
                "Foundation Length",
                "b_length",
                "B Length"
            });

            var width = GetParameterDouble(footing, new object[]
            {
                "Width",
                "W",
                "Foundation Width",
                "b",
                "B"
            });

            if (length == null || width == null)
            {
                var sizeX = Math.Abs(box.Max.X - box.Min.X);
                var sizeY = Math.Abs(box.Max.Y - box.Min.Y);

                length ??= sizeX;
                width ??= sizeY;
            }

            return (length, width);
        }

        private static (XYZ Center, double Rotation) GetFootingCenterAndRotation(FamilyInstance footing, BoundingBoxXYZ box)
        {
            var location = footing.Location;
            if (location is LocationPoint point)
            {
                return (point.Point, point.Rotation);
            }

            var center = new XYZ(
                (box.Min.X + box.Max.X) / 2.0,
                (box.Min.Y + box.Max.Y) / 2.0,
                (box.Min.Z + box.Max.Z) / 2.0);

            return (center, 0.0);
        }

        private static double? GetParameterDouble(Element element, IEnumerable<object> parameterKeys)
        {
            foreach (var key in parameterKeys)
            {
                Parameter? parameter = key switch
                {
                    BuiltInParameter bip => element.get_Parameter(bip),
                    string name => element.LookupParameter(name),
                    _ => null
                };

                if (parameter != null && parameter.HasValue)
                {
                    try
                    {
                        return parameter.AsDouble();
                    }
                    catch
                    {
                        // Ignore conversion failures, try next key.
                    }
                }
            }

            return null;
        }

        private static ElementId EnsureMaterial(Document doc, string name, int transparency, Color color)
        {
            var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
            foreach (Material material in collector)
            {
                if (material.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        material.Transparency = transparency;
                        material.Color = color;
                    }
                    catch
                    {
                        // Ignore material property update issues.
                    }

                    return material.Id;
                }
            }

            var materialId = Material.Create(doc, name);
            var created = (Material)doc.GetElement(materialId);
            try
            {
                created.Transparency = transparency;
                created.Color = color;
            }
            catch
            {
                // Ignore material property update issues.
            }

            return materialId;
        }

        private static void SetComments(Element element, string value)
        {
            var parameter = element.LookupParameter("Comments");
            if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.String)
            {
                try
                {
                    parameter.Set(value);
                }
                catch
                {
                    // Ignore comment set failures.
                }
            }
        }

        private static bool TryPromptForParameters(string prompt, double defaultValue, out double value)
        {
            while (true)
            {
                var input = Interaction.InputBox(prompt, "RevitSuite", defaultValue.ToString(CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(input))
                {
                    value = defaultValue;
                    return false;
                }

                if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }

                TaskDialog.Show("RevitSuite", "Please enter a valid number.");
            }
        }

        private static bool TryPromptForInt(string prompt, int defaultValue, int min, int max, out int value)
        {
            while (true)
            {
                var input = Interaction.InputBox(prompt, "RevitSuite", defaultValue.ToString(CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(input))
                {
                    value = defaultValue;
                    return false;
                }

                if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    value = Math.Max(min, Math.Min(max, value));
                    return true;
                }

                TaskDialog.Show("RevitSuite", "Please enter a valid integer.");
            }
        }

        private static bool PromptYesNo(string question, bool defaultYes)
        {
            var dialog = new TaskDialog("RevitSuite")
            {
                MainInstruction = question,
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = defaultYes ? TaskDialogResult.Yes : TaskDialogResult.No
            };

            var result = dialog.Show();
            return result == TaskDialogResult.Yes;
        }

        private class FloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is Floor)
                {
                    return true;
                }

                return elem.Category != null &&
                       elem.Category.Id.Value == (int)BuiltInCategory.OST_Floors;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
