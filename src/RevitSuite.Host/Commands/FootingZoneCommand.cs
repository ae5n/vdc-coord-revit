using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitSuite.Host.Config;
using RevitSuite.Host.Logging;
using RevitSuite.Host.UI;

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

                var defaults = LoadDefaults();
                var parameters = PromptForParameters(data.Application, defaults);
                if (parameters == null)
                {
                    LogManager.Info(correlationId, "FootingZoneCommand cancelled by user.");
                    return Result.Cancelled;
                }

                var doc = uiDoc.Document;

                var slabs = parameters.PromptForSlabs
                    ? PromptForSlabs(uiDoc, "Select slabs/floors to include (Esc to finish).")
                    : new List<Element>();

                var footings = parameters.IncludeFootings
                    ? new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                        .WhereElementIsNotElementType()
                        .OfType<FamilyInstance>()
                        .ToList()
                    : new List<FamilyInstance>();

                if (footings.Count == 0 && slabs.Count == 0)
                {
                    TaskDialog.Show("RevitSuite", "No footings or slabs selected to process.");
                    LogManager.Warn(correlationId, "No elements available for footing zones.");
                    return Result.Cancelled;
                }

                var createdFootingZones = new List<ElementId>();
                var createdSlabZones = new List<ElementId>();
                var skippedElements = new List<ElementId>();

                using (var tx = new Transaction(doc, "RevitSuite: Footing Influence Zones"))
                {
                    tx.Start();

                    var materialId = EnsureMaterial(doc, "Zone Influence – Transparent", parameters.Transparency,
                        new Autodesk.Revit.DB.Color((byte)0, (byte)170, (byte)255));
                    var hostCategoryId = new ElementId(BuiltInCategory.OST_GenericModel);

                    foreach (var footing in footings)
                    {
                        if (!TryCreateFootingZone(doc, footing, hostCategoryId, materialId, parameters, createdFootingZones, skippedElements))
                        {
                            skippedElements.Add(footing.Id);
                        }
                    }

                    foreach (var slab in slabs)
                    {
                        if (!TryCreateSlabZone(doc, slab, hostCategoryId, materialId, parameters, createdSlabZones))
                        {
                            skippedElements.Add(slab.Id);
                        }
                    }

                    tx.Commit();
                }

                var summary = $"Created {createdFootingZones.Count} footing zone(s) and {createdSlabZones.Count} slab zone(s).";
                if (skippedElements.Count > 0)
                {
                    summary += $"\nSkipped {skippedElements.Count} element(s).";
                }

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

        private static FootingZoneConfig LoadDefaults() => FootingZoneConfig.Load();

        private static FootingZoneParameters? PromptForParameters(UIApplication application, FootingZoneConfig defaults)
        {
            using var form = new FootingZoneForm();
            form.SetDefaults(
                defaults.ClearDepth,
                defaults.SlopeRatio,
                defaults.VerticalOffset,
                defaults.Transparency,
                defaults.IncludeFootings,
                defaults.PromptForSlabs);

            var owner = new RevitWindow(application.MainWindowHandle);
            var result = form.ShowDialog(owner);
            return result == DialogResult.OK ? form.Parameters : null;
        }

        private static List<Element> PromptForSlabs(UIDocument uiDoc, string prompt)
        {
            var slabs = new List<Element>();
            try
            {
                var references = uiDoc.Selection.PickObjects(ObjectType.Element, new FloorSelectionFilter(), prompt);
                foreach (var reference in references)
                {
                    var element = uiDoc.Document.GetElement(reference);
                    if (element != null)
                    {
                        slabs.Add(element);
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User cancelled selection; that's acceptable.
            }

            return slabs;
        }

        private static bool TryCreateFootingZone(
            Document doc,
            FamilyInstance footing,
            ElementId categoryId,
            ElementId materialId,
            FootingZoneParameters parameters,
            IList<ElementId> created,
            IList<ElementId> skipped)
        {
            var boundingBox = footing.get_BoundingBox(null);
            if (boundingBox == null)
            {
                return false;
            }

            var (length, width) = GetFootingLengthWidth(footing, boundingBox);
            if (length == null || width == null)
            {
                return false;
            }

            var (center, rotation) = GetFootingCenterAndRotation(footing, boundingBox);

            var topOffset = (boundingBox.Min.Z - center.Z) - parameters.VerticalOffset;
            var bottomOffset = topOffset - parameters.ClearDepth;

            var bottomLength = length.Value + 2.0 * parameters.SlopeRatio * parameters.ClearDepth;
            var bottomWidth = width.Value + 2.0 * parameters.SlopeRatio * parameters.ClearDepth;

            var topLoopLocal = CreateRectangleLoop(length.Value, width.Value, topOffset);
            var bottomLoopLocal = CreateRectangleLoop(bottomLength, bottomWidth, bottomOffset);

            var solid = CreateLoft(topLoopLocal, bottomLoopLocal, materialId);
            var transform = Transform.CreateTranslation(center)
                .Multiply(Transform.CreateRotation(XYZ.BasisZ, rotation));

            var directShape = DirectShape.CreateElement(doc, categoryId);
            if (solid != null)
            {
                var transformed = SolidUtils.CreateTransformed(solid, transform);
                directShape.SetShape(new List<GeometryObject> { transformed });
            }
            else
            {
                var geometry = CreateFrustumMesh(topLoopLocal, bottomLoopLocal, transform, materialId);
                if (geometry == null || geometry.Count == 0)
                {
                    return false;
                }

                directShape.SetShape(geometry);
            }

            SetComments(directShape, "Footing Influence (transparent)");
            created.Add(directShape.Id);
            return true;
        }

        private static bool TryCreateSlabZone(
            Document doc,
            Element slab,
            ElementId categoryId,
            ElementId materialId,
            FootingZoneParameters parameters,
            IList<ElementId> created)
        {
            var boundingBox = slab.get_BoundingBox(null);
            if (boundingBox == null)
            {
                return false;
            }

            var length = boundingBox.Max.X - boundingBox.Min.X;
            var width = boundingBox.Max.Y - boundingBox.Min.Y;
            var center = new XYZ(
                (boundingBox.Min.X + boundingBox.Max.X) / 2.0,
                (boundingBox.Min.Y + boundingBox.Max.Y) / 2.0,
                (boundingBox.Min.Z + boundingBox.Max.Z) / 2.0);

            var bottom = GetParameterDouble(slab, new object[] { "Elevation at Bottom" }) ?? boundingBox.Min.Z;

            var topOffset = (bottom - center.Z) - parameters.VerticalOffset;
            var bottomOffset = topOffset - parameters.ClearDepth;

            var bottomLength = length + 2.0 * parameters.SlopeRatio * parameters.ClearDepth;
            var bottomWidth = width + 2.0 * parameters.SlopeRatio * parameters.ClearDepth;

            var topLoopLocal = CreateRectangleLoop(length, width, topOffset);
            var bottomLoopLocal = CreateRectangleLoop(bottomLength, bottomWidth, bottomOffset);

            var solid = CreateLoft(topLoopLocal, bottomLoopLocal, materialId);
            var transform = Transform.CreateTranslation(center);

            var directShape = DirectShape.CreateElement(doc, categoryId);
            if (solid != null)
            {
                var transformed = SolidUtils.CreateTransformed(solid, transform);
                directShape.SetShape(new List<GeometryObject> { transformed });
            }
            else
            {
                var geometry = CreateFrustumMesh(topLoopLocal, bottomLoopLocal, transform, materialId);
                if (geometry == null || geometry.Count == 0)
                {
                    return false;
                }

                directShape.SetShape(geometry);
            }

            SetComments(directShape, "Slab Influence (transparent, bbox)");
            created.Add(directShape.Id);
            return true;
        }

        private static Solid? CreateLoft(CurveLoop topLoop, CurveLoop bottomLoop, ElementId materialId)
        {
            var loops = new List<CurveLoop> { topLoop, bottomLoop };
            var options = new SolidOptions(materialId, ElementId.InvalidElementId);

            try
            {
                var loft = GeometryCreationUtilities.CreateLoftGeometry(loops, options);
                if (loft is Solid solid)
                {
                    return solid;
                }

                if (loft is IList<Solid> solidList && solidList.Count > 0)
                {
                    return solidList[0];
                }
            }
            catch
            {
                // Ignore and fall back to tessellated geometry.
            }

            return null;
        }

        private static IList<GeometryObject>? CreateFrustumMesh(
            CurveLoop topLoop,
            CurveLoop bottomLoop,
            Transform transform,
            ElementId materialId)
        {
            var topPoints = TransformCurveLoop(topLoop, transform).ToList();
            var bottomPoints = TransformCurveLoop(bottomLoop, transform).ToList();
            if (topPoints.Count < 3 || topPoints.Count != bottomPoints.Count)
            {
                return null;
            }

            var builder = new TessellatedShapeBuilder
            {
                Target = TessellatedShapeBuilderTarget.AnyGeometry,
                Fallback = TessellatedShapeBuilderFallback.Mesh
            };

            builder.OpenConnectedFaceSet(false);

            for (int i = 1; i < topPoints.Count - 1; i++)
            {
                builder.AddFace(new TessellatedFace(new List<XYZ> { topPoints[0], topPoints[i], topPoints[i + 1] }, materialId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { bottomPoints[i + 1], bottomPoints[i], bottomPoints[0] }, materialId));
            }

            for (int i = 0; i < topPoints.Count; i++)
            {
                var i2 = (i + 1) % topPoints.Count;
                builder.AddFace(new TessellatedFace(new List<XYZ> { topPoints[i], topPoints[i2], bottomPoints[i2] }, materialId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { topPoints[i], bottomPoints[i2], bottomPoints[i] }, materialId));
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
                        // ignore and try next
                    }
                }
            }

            return null;
        }

        private static ElementId EnsureMaterial(Document doc, string name, int transparency, Autodesk.Revit.DB.Color color)
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
                    // ignore updates
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
                    // ignore updates
                }
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
                    // ignore
                }
            }
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

    private sealed class RevitWindow : IWin32Window
    {
        public IntPtr Handle { get; }

        public RevitWindow(IntPtr handle)
        {
            Handle = handle;
        }
    }
}
}
