using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services.RevitSuite
{
    public class FootingZonesMcpEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public double ClearDepth { get; set; } = 5.0;
        public double SlopeRatio { get; set; } = 1.0;
        public double VerticalOffset { get; set; } = 0.0;
        public int Transparency { get; set; } = 50;
        public bool IncludeFootings { get; set; } = true;
        public object Result { get; private set; }

        public bool WaitForCompletion(int timeoutMs = 60000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMs);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Result = new { success = false, error = "No active document." }; return; }

                var footings = new List<Element>();

                if (IncludeFootings)
                {
                    footings.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                        .WhereElementIsNotElementType()
                        .Cast<Element>());
                }

                if (footings.Count == 0)
                {
                    Result = new { success = false, error = "No structural foundations found in active document." };
                    return;
                }

                int created = 0;
                using (var tx = new Transaction(doc, "MCP: Create Footing Influence Zones"))
                {
                    tx.Start();
                    foreach (var footing in footings)
                    {
                        try
                        {
                            var zone = CreateInfluenceZone(doc, footing);
                            if (zone != null) created++;
                        }
                        catch { /* skip individual failures */ }
                    }
                    tx.Commit();
                }

                Result = new { success = true, footingsProcessed = footings.Count, zonesCreated = created };
            }
            catch (Exception ex)
            {
                Result = new { success = false, error = ex.Message };
            }
            finally { _resetEvent.Set(); }
        }

        public string GetName() => "Footing Zones MCP";

        private Element CreateInfluenceZone(Document doc, Element footing)
        {
            var bbox = footing.get_BoundingBox(null);
            if (bbox == null) return null;

            var minPt = bbox.Min;
            var maxPt = bbox.Max;
            var bottomZ = minPt.Z - VerticalOffset;
            var depthZ = bottomZ - ClearDepth;

            var expansion = ClearDepth * SlopeRatio;
            var corners = new[]
            {
                new XYZ(minPt.X - expansion, minPt.Y - expansion, depthZ),
                new XYZ(maxPt.X + expansion, minPt.Y - expansion, depthZ),
                new XYZ(maxPt.X + expansion, maxPt.Y + expansion, depthZ),
                new XYZ(minPt.X - expansion, maxPt.Y + expansion, depthZ),
            };

            var bottomLoop = new CurveLoop();
            for (int i = 0; i < 4; i++)
                bottomLoop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));

            var solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                new[] { bottomLoop }, XYZ.BasisZ, ClearDepth + Math.Abs(VerticalOffset));

            var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.SetShape(new GeometryObject[] { solid });

            var og = new OverrideGraphicSettings()
                .SetSurfaceTransparency(Transparency);
            doc.ActiveView?.SetElementOverrides(ds.Id, og);

            return ds;
        }
    }
}
