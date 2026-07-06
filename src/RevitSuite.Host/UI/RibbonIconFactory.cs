using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitSuite.Host.UI
{
    internal static class RibbonIconFactory
    {
        internal sealed class IconSet
        {
            public IconSet(ImageSource largeImage, ImageSource smallImage)
            {
                LargeImage = largeImage;
                SmallImage = smallImage;
            }

            public ImageSource LargeImage { get; }
            public ImageSource SmallImage { get; }
        }

        public static IconSet FootingZones => _footingZones ??= CreateFootingZonesIconSet();
        public static IconSet WallFraming => _wallFraming ??= CreateWallFramingIconSet();
        public static IconSet QAQC => _qaqc ??= CreateQaqcIconSet();
        public static IconSet ReportsHub => _reportsHub ??= CreateReportsHubIconSet();
        public static IconSet LevelReport => _levelReport ??= CreateLevelReportIconSet();
        public static IconSet GridReport => _gridReport ??= CreateGridReportIconSet();
        public static IconSet SharedCoordinatesReport => _sharedCoordinatesReport ??= CreateSharedCoordinatesReportIconSet();
        public static IconSet NwcBatchExport => _nwcBatchExport ??= CreateNwcBatchExportIconSet();
        public static IconSet CopyLinkedViews => _copyLinkedViews ??= CreateCopyLinkedViewsIconSet();
        public static IconSet ModelExplorer => _modelExplorer ??= CreateModelExplorerIconSet();
        public static IconSet Mcp => _mcp ??= CreateMcpIconSet();
        public static IconSet McpSettings => _mcpSettings ??= CreateMcpSettingsIconSet();
        public static IconSet About => _about ??= CreateAboutIconSet();

        private static IconSet? _footingZones;
        private static IconSet? _wallFraming;
        private static IconSet? _qaqc;
        private static IconSet? _reportsHub;
        private static IconSet? _levelReport;
        private static IconSet? _gridReport;
        private static IconSet? _sharedCoordinatesReport;
        private static IconSet? _nwcBatchExport;
        private static IconSet? _copyLinkedViews;
        private static IconSet? _modelExplorer;
        private static IconSet? _mcp;
        private static IconSet? _mcpSettings;
        private static IconSet? _about;

        private static IconSet CreateFootingZonesIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x1E, 0x6B, 0x5A),
                Color.FromRgb(0x3C, 0xB1, 0x88),
                DrawFootingZoneContent);
        }

        private static IconSet CreateQaqcIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x15, 0x80, 0x3D),
                Color.FromRgb(0x22, 0xC5, 0x5E),
                DrawQaqcContent);
        }

        private static IconSet CreateWallFramingIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x1F, 0x4E, 0x5F),
                Color.FromRgb(0x4F, 0x86, 0x8E),
                DrawWallFramingContent);
        }

        private static IconSet CreateReportsHubIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x2B, 0x4A, 0x7F),
                Color.FromRgb(0x4F, 0x7E, 0xB5),
                DrawReportsHubContent);
        }

        private static IconSet CreateLevelReportIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x5B, 0x3C, 0x95),
                Color.FromRgb(0xA1, 0x6D, 0xD6),
                DrawLevelReportContent);
        }

        private static IconSet CreateGridReportIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0xB5, 0x33, 0x1A),
                Color.FromRgb(0xF2, 0x86, 0x3E),
                DrawGridReportContent);
        }

        private static IconSet CreateSharedCoordinatesReportIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x1D, 0x4F, 0x7A),
                Color.FromRgb(0x3D, 0xA3, 0xC8),
                DrawSharedCoordinatesReportContent);
        }

        private static IconSet CreateNwcBatchExportIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x23, 0x3A, 0x4E),
                Color.FromRgb(0x3B, 0x68, 0x8F),
                DrawNwcBatchExportContent);
        }

        private static IconSet CreateCopyLinkedViewsIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x1E, 0x3C, 0x5A),
                Color.FromRgb(0x45, 0x8A, 0xD1),
                DrawCopyLinkedViewsContent);
        }

        private static IconSet CreateModelExplorerIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x9A, 0x34, 0x12),
                Color.FromRgb(0xF9, 0x73, 0x16),
                DrawModelExplorerContent);
        }

        private static void DrawModelExplorerContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);

            // Tree structure: three rows with indent lines, suggesting a category tree.
            var treePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), min * 0.07)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            treePen.Freeze();

            dc.DrawLine(treePen, new Point(width * 0.2, height * 0.26), new Point(width * 0.5, height * 0.26));
            dc.DrawLine(treePen, new Point(width * 0.3, height * 0.44), new Point(width * 0.56, height * 0.44));
            dc.DrawLine(treePen, new Point(width * 0.3, height * 0.62), new Point(width * 0.5, height * 0.62));

            var spinePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), min * 0.05);
            spinePen.Freeze();
            dc.DrawLine(spinePen, new Point(width * 0.22, height * 0.30), new Point(width * 0.22, height * 0.62));

            // Magnifier over the lower right.
            var lensPen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), min * 0.09);
            lensPen.Freeze();
            var lensBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            lensBrush.Freeze();
            var lensCenter = new Point(width * 0.62, height * 0.6);
            dc.DrawEllipse(lensBrush, lensPen, lensCenter, min * 0.18, min * 0.18);

            var handlePen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), min * 0.1)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            handlePen.Freeze();
            dc.DrawLine(handlePen,
                new Point(width * 0.74, height * 0.74),
                new Point(width * 0.86, height * 0.86));
        }

        private static IconSet CreateMcpIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x3B, 0x1F, 0x7A),
                Color.FromRgb(0x7C, 0x4D, 0xE8),
                DrawMcpContent);
        }

        private static IconSet CreateMcpSettingsIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x3B, 0x1F, 0x7A),
                Color.FromRgb(0x7C, 0x4D, 0xE8),
                DrawMcpSettingsContent);
        }

        private static IconSet CreateAboutIconSet()
        {
            return CreateIconSet(
                Color.FromRgb(0x3E, 0x4A, 0x1F),
                Color.FromRgb(0x8E, 0xB7, 0x3D),
                DrawAboutContent);
        }

        private static IconSet CreateIconSet(
            Color gradientStart,
            Color gradientEnd,
            Action<DrawingContext, double, double> drawContent)
        {
            return new IconSet(
                CreateIcon(32, 32, gradientStart, gradientEnd, drawContent),
                CreateIcon(16, 16, gradientStart, gradientEnd, drawContent));
        }

        private static ImageSource CreateIcon(
            int width,
            int height,
            Color gradientStart,
            Color gradientEnd,
            Action<DrawingContext, double, double> drawContent)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, width, height);
                var radius = Math.Min(width, height) * 0.25;

                var background = new LinearGradientBrush(gradientStart, gradientEnd, new Point(0, 0), new Point(1, 1));
                background.Freeze();
                dc.DrawRoundedRectangle(background, null, rect, radius, radius);

                var sheen = new LinearGradientBrush(
                    Color.FromArgb(90, 255, 255, 255),
                    Color.FromArgb(30, 255, 255, 255),
                    new Point(0, 0),
                    new Point(0, 1));
                sheen.Freeze();
                dc.DrawRoundedRectangle(sheen, null, new Rect(1, 1, width - 2, height - 2), radius * 0.85, radius * 0.85);

                drawContent(dc, width, height);
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static void DrawFootingZoneContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);

            var zoneBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
            zoneBrush.Freeze();
            var zonePen = new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), min * 0.07)
            {
                LineJoin = PenLineJoin.Round
            };
            zonePen.Freeze();

            var zoneGeometry = new StreamGeometry();
            using (var ctx = zoneGeometry.Open())
            {
                ctx.BeginFigure(new Point(width * 0.2, height * 0.52), true, true);
                ctx.LineTo(new Point(width * 0.35, height * 0.2), true, false);
                ctx.LineTo(new Point(width * 0.65, height * 0.2), true, false);
                ctx.LineTo(new Point(width * 0.8, height * 0.52), true, false);
                ctx.LineTo(new Point(width * 0.8, height * 0.72), true, false);
                ctx.LineTo(new Point(width * 0.2, height * 0.72), true, false);
            }
            zoneGeometry.Freeze();
            dc.DrawGeometry(zoneBrush, zonePen, zoneGeometry);

            var baseBrush = new SolidColorBrush(Color.FromArgb(220, 240, 193, 94));
            baseBrush.Freeze();
            var basePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), min * 0.06);
            basePen.Freeze();
            var baseRect = new Rect(width * 0.24, height * 0.62, width * 0.52, height * 0.22);
            dc.DrawRoundedRectangle(baseBrush, basePen, baseRect, min * 0.15, min * 0.15);

            var columnPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), min * 0.10)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            columnPen.Freeze();
            dc.DrawLine(columnPen, new Point(width * 0.38, height * 0.32), new Point(width * 0.38, height * 0.62));
            dc.DrawLine(columnPen, new Point(width * 0.62, height * 0.32), new Point(width * 0.62, height * 0.62));

            var dashedPen = new Pen(new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)), min * 0.05)
            {
                DashStyle = new DashStyle(new[] { 1.2, 1.2 }, 0),
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dashedPen.Freeze();
            dc.DrawLine(dashedPen, new Point(width * 0.26, height * 0.52), new Point(width * 0.74, height * 0.52));
        }

        private static void DrawWallFramingContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);

            var wallPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 18, 54, 66)), min * 0.02)
            {
                LineJoin = PenLineJoin.Round
            };
            wallPen.Freeze();

            var wallBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
            wallBrush.Freeze();
            dc.DrawRoundedRectangle(wallBrush, wallPen, new Rect(width * 0.12, height * 0.12, width * 0.76, height * 0.76), min * 0.08, min * 0.08);

            var woodBrush = new SolidColorBrush(Color.FromArgb(245, 214, 170, 92));
            woodBrush.Freeze();

            var woodPen = new Pen(woodBrush, min * 0.085)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            woodPen.Freeze();

            dc.DrawLine(woodPen, new Point(width * 0.18, height * 0.22), new Point(width * 0.82, height * 0.22));
            dc.DrawLine(woodPen, new Point(width * 0.18, height * 0.82), new Point(width * 0.82, height * 0.82));
            dc.DrawLine(woodPen, new Point(width * 0.18, height * 0.22), new Point(width * 0.18, height * 0.82));
            dc.DrawLine(woodPen, new Point(width * 0.82, height * 0.22), new Point(width * 0.82, height * 0.82));

            var jambPen = new Pen(new SolidColorBrush(Color.FromArgb(248, 232, 194, 118)), min * 0.08)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            jambPen.Freeze();
            dc.DrawLine(jambPen, new Point(width * 0.34, height * 0.22), new Point(width * 0.34, height * 0.82));
            dc.DrawLine(jambPen, new Point(width * 0.66, height * 0.22), new Point(width * 0.66, height * 0.82));

            var headerPen = new Pen(new SolidColorBrush(Color.FromArgb(248, 223, 178, 84)), min * 0.09)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            headerPen.Freeze();
            dc.DrawLine(headerPen, new Point(width * 0.36, height * 0.38), new Point(width * 0.64, height * 0.38));

            var doorBrush = new SolidColorBrush(Color.FromArgb(245, 72, 40, 18));
            doorBrush.Freeze();
            var doorPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 244, 230)), min * 0.012)
            {
                LineJoin = PenLineJoin.Round
            };
            doorPen.Freeze();
            dc.DrawRoundedRectangle(doorBrush, doorPen, new Rect(width * 0.39, height * 0.42, width * 0.22, height * 0.38), min * 0.035, min * 0.035);

            var handleBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            handleBrush.Freeze();
            dc.DrawEllipse(handleBrush, null, new Point(width * 0.565, height * 0.61), min * 0.028, min * 0.028);
        }

        private static void DrawReportsHubContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var sheetBrush = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255));
            sheetBrush.Freeze();
            var sheetPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), min * 0.05);
            sheetPen.Freeze();

            var backRect = new Rect(width * 0.18, height * 0.20, width * 0.54, height * 0.54);
            dc.PushOpacity(0.55);
            dc.DrawRoundedRectangle(sheetBrush, null, backRect, min * 0.12, min * 0.12);
            dc.Pop();

            var frontRect = new Rect(width * 0.28, height * 0.28, width * 0.54, height * 0.54);
            dc.DrawRoundedRectangle(sheetBrush, sheetPen, frontRect, min * 0.12, min * 0.12);

            var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 35, 49, 68)), min * 0.04)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Round
            };
            axisPen.Freeze();
            dc.DrawLine(axisPen, new Point(width * 0.40, height * 0.68), new Point(width * 0.72, height * 0.68));
            dc.DrawLine(axisPen, new Point(width * 0.40, height * 0.68), new Point(width * 0.40, height * 0.42));

            var chartPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 193, 92)), min * 0.11)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            chartPen.Freeze();

            var chartGeometry = new StreamGeometry();
            using (var ctx = chartGeometry.Open())
            {
                ctx.BeginFigure(new Point(width * 0.44, height * 0.60), false, false);
                ctx.LineTo(new Point(width * 0.52, height * 0.50), true, false);
                ctx.LineTo(new Point(width * 0.60, height * 0.56), true, false);
                ctx.LineTo(new Point(width * 0.68, height * 0.44), true, false);
            }
            chartGeometry.Freeze();
            dc.DrawGeometry(null, chartPen, chartGeometry);
        }

        private static void DrawLevelReportContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var linePen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), min * 0.10)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            linePen.Freeze();

            var yPositions = new[] { 0.30, 0.50, 0.70 };
            foreach (var y in yPositions)
            {
                dc.DrawLine(linePen, new Point(width * 0.38, height * y), new Point(width * 0.74, height * y));
            }

            var markerBrush = new SolidColorBrush(Color.FromRgb(255, 222, 109));
            markerBrush.Freeze();
            var markerOutline = new Pen(new SolidColorBrush(Color.FromArgb(180, 64, 48, 96)), min * 0.04);
            markerOutline.Freeze();

            var markerRadius = min * 0.12;
            foreach (var y in yPositions)
            {
                dc.DrawEllipse(markerBrush, markerOutline, new Point(width * 0.30, height * y), markerRadius, markerRadius);
            }
        }

        private static void DrawGridReportContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), min * 0.08)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            gridPen.Freeze();

            var offsets = new[] { 0.36, 0.50, 0.64 };
            foreach (var offset in offsets)
            {
                dc.DrawLine(gridPen, new Point(width * offset, height * 0.34), new Point(width * offset, height * 0.66));
                dc.DrawLine(gridPen, new Point(width * 0.34, height * offset), new Point(width * 0.66, height * offset));
            }

            var highlightPen = new Pen(new SolidColorBrush(Color.FromArgb(240, 78, 36, 0)), min * 0.05)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            highlightPen.Freeze();
            dc.DrawRectangle(null, highlightPen, new Rect(width * 0.34, height * 0.34, width * 0.32, height * 0.32));
        }

        private static void DrawSharedCoordinatesReportContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var center = new Point(width * 0.52, height * 0.54);

            var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), min * 0.06)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            axisPen.Freeze();
            dc.DrawLine(axisPen, new Point(center.X, height * 0.30), new Point(center.X, height * 0.80));
            dc.DrawLine(axisPen, new Point(width * 0.26, center.Y), new Point(width * 0.78, center.Y));

            var orbitPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), min * 0.04)
            {
                DashStyle = new DashStyle(new[] { 1.0, 1.05 }, 0),
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            orbitPen.Freeze();
            dc.DrawEllipse(null, orbitPen, center, min * 0.30, min * 0.30);

            var hostPoint = new Point(center.X - min * 0.16, center.Y - min * 0.12);
            var linkPoint = new Point(center.X + min * 0.20, center.Y + min * 0.16);

            var connectorPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 64, 198, 247)), min * 0.05)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            connectorPen.Freeze();
            dc.DrawLine(connectorPen, hostPoint, linkPoint);

            var hostHalo = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            hostHalo.Freeze();
            var hostBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            hostBrush.Freeze();
            dc.DrawEllipse(hostHalo, null, hostPoint, min * 0.12, min * 0.12);
            dc.DrawEllipse(hostBrush, null, hostPoint, min * 0.07, min * 0.07);

            var linkHalo = new SolidColorBrush(Color.FromArgb(120, 64, 198, 247));
            linkHalo.Freeze();
            var linkBrush = new SolidColorBrush(Color.FromRgb(64, 198, 247));
            linkBrush.Freeze();
            dc.DrawEllipse(linkHalo, null, linkPoint, min * 0.11, min * 0.11);
            dc.DrawEllipse(linkBrush, null, linkPoint, min * 0.06, min * 0.06);

            var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), min * 0.045)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            tickPen.Freeze();
            dc.DrawLine(tickPen, new Point(center.X, height * 0.34), new Point(center.X + min * 0.14, height * 0.34));
            dc.DrawLine(tickPen, new Point(width * 0.30, center.Y), new Point(width * 0.30, center.Y - min * 0.14));
        }

        private static void DrawNwcBatchExportContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var gridBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
            gridBrush.Freeze();
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), min * 0.05)
            {
                DashStyle = new DashStyle(new[] { 1.0, 1.2 }, 0)
            };
            gridPen.Freeze();

            for (var i = 0; i < 4; i++)
            {
                var offset = width * (0.24 + i * 0.18);
                dc.DrawLine(gridPen, new Point(offset, height * 0.28), new Point(offset, height * 0.74));
            }

            for (var i = 0; i < 4; i++)
            {
                var offset = height * (0.28 + i * 0.18);
                dc.DrawLine(gridPen, new Point(width * 0.24, offset), new Point(width * 0.76, offset));
            }

            var cubeBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            cubeBrush.Freeze();
            var cubePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 35, 56, 79)), min * 0.05);
            cubePen.Freeze();

            var baseRect = new Rect(width * 0.36, height * 0.40, width * 0.32, height * 0.24);
            dc.DrawRoundedRectangle(cubeBrush, cubePen, baseRect, min * 0.08, min * 0.08);

            var topRect = new Rect(width * 0.30, height * 0.32, width * 0.32, height * 0.24);
            dc.PushOpacity(0.85);
            dc.DrawRoundedRectangle(cubeBrush, cubePen, topRect, min * 0.08, min * 0.08);
            dc.Pop();

            var arrowPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 214, 94)), min * 0.09)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            arrowPen.Freeze();

            var arrowStart = new Point(width * 0.28, height * 0.76);
            var arrowEnd = new Point(width * 0.72, height * 0.76);
            dc.DrawLine(arrowPen, arrowStart, arrowEnd);

            var arrowHead = new StreamGeometry();
            using (var ctx = arrowHead.Open())
            {
                ctx.BeginFigure(new Point(width * 0.72, height * 0.76), true, true);
                ctx.LineTo(new Point(width * 0.66, height * 0.70), true, false);
                ctx.LineTo(new Point(width * 0.66, height * 0.82), true, false);
            }
            arrowHead.Freeze();
            var arrowFill = new SolidColorBrush(Color.FromRgb(255, 214, 94));
            arrowFill.Freeze();
            dc.DrawGeometry(arrowFill, null, arrowHead);

            var dotBrush = new SolidColorBrush(Color.FromRgb(96, 205, 255));
            dotBrush.Freeze();
            var dotRadius = min * 0.05;
            dc.DrawEllipse(dotBrush, null, new Point(width * 0.36, height * 0.76), dotRadius, dotRadius);
            dc.DrawEllipse(dotBrush, null, new Point(width * 0.52, height * 0.76), dotRadius, dotRadius);
        }

        private static void DrawCopyLinkedViewsContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);

            var backBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            backBrush.Freeze();
            var backRect = new Rect(width * 0.18, height * 0.34, width * 0.40, height * 0.40);
            dc.DrawRoundedRectangle(backBrush, null, backRect, min * 0.10, min * 0.10);

            var cardBrush = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255));
            cardBrush.Freeze();
            var cardPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), min * 0.05);
            cardPen.Freeze();
            var cardRect = new Rect(width * 0.34, height * 0.20, width * 0.44, height * 0.54);
            dc.DrawRoundedRectangle(cardBrush, cardPen, cardRect, min * 0.12, min * 0.12);

            var headerBrush = new SolidColorBrush(Color.FromArgb(140, 88, 133, 196));
            headerBrush.Freeze();
            dc.DrawRoundedRectangle(headerBrush, null, new Rect(width * 0.38, height * 0.26, width * 0.32, height * 0.10), min * 0.05, min * 0.05);

            var rowBrush = new SolidColorBrush(Color.FromArgb(110, 71, 166, 255));
            rowBrush.Freeze();
            dc.DrawRoundedRectangle(rowBrush, null, new Rect(width * 0.38, height * 0.40, width * 0.20, height * 0.08), min * 0.04, min * 0.04);
            dc.DrawRoundedRectangle(rowBrush, null, new Rect(width * 0.38, height * 0.52, width * 0.24, height * 0.08), min * 0.04, min * 0.04);

            var arrowPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 213, 94)), min * 0.12)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            arrowPen.Freeze();
            dc.DrawLine(arrowPen, new Point(width * 0.26, height * 0.42), new Point(width * 0.46, height * 0.34));

            var arrowHead = new StreamGeometry();
            using (var ctx = arrowHead.Open())
            {
                ctx.BeginFigure(new Point(width * 0.46, height * 0.34), true, true);
                ctx.LineTo(new Point(width * 0.40, height * 0.30), true, false);
                ctx.LineTo(new Point(width * 0.44, height * 0.44), true, false);
            }
            arrowHead.Freeze();
            var arrowFill = new SolidColorBrush(Color.FromRgb(255, 213, 94));
            arrowFill.Freeze();
            dc.DrawGeometry(arrowFill, null, arrowHead);
        }

        private static void DrawMcpContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var center = new Point(width * 0.50, height * 0.50);

            // Spoke endpoints: top, bottom-left, bottom-right
            var nodes = new[]
            {
                new Point(width * 0.50, height * 0.20),
                new Point(width * 0.22, height * 0.72),
                new Point(width * 0.78, height * 0.72),
            };

            // Spokes
            var spokePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), min * 0.07)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            spokePen.Freeze();
            foreach (var node in nodes)
                dc.DrawLine(spokePen, center, node);

            // Outer nodes
            var nodeBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
            nodeBrush.Freeze();
            var nodeRadius = min * 0.11;
            foreach (var node in nodes)
                dc.DrawEllipse(nodeBrush, null, node, nodeRadius, nodeRadius);

            // Centre hub
            var hubHalo = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            hubHalo.Freeze();
            var hubBrush = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
            hubBrush.Freeze();
            dc.DrawEllipse(hubHalo, null, center, min * 0.22, min * 0.22);
            dc.DrawEllipse(hubBrush, null, center, min * 0.14, min * 0.14);
        }

        private static void DrawQaqcContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);

            // Draw circle background for checkmark
            var circleBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
            circleBrush.Freeze();
            var circlePen = new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), min * 0.06);
            circlePen.Freeze();
            var center = new Point(width * 0.50, height * 0.50);
            var radius = min * 0.32;
            dc.DrawEllipse(circleBrush, circlePen, center, radius, radius);

            // Draw checkmark
            var checkPen = new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), min * 0.14)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            checkPen.Freeze();

            var checkGeometry = new StreamGeometry();
            using (var ctx = checkGeometry.Open())
            {
                ctx.BeginFigure(new Point(width * 0.32, height * 0.50), false, false);
                ctx.LineTo(new Point(width * 0.44, height * 0.62), true, false);
                ctx.LineTo(new Point(width * 0.68, height * 0.38), true, false);
            }
            checkGeometry.Freeze();
            dc.DrawGeometry(null, checkPen, checkGeometry);

            // Draw small deviation arrows at corners
            var arrowPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 213, 94)), min * 0.06)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            arrowPen.Freeze();

            // Top-left arrow
            dc.DrawLine(arrowPen, new Point(width * 0.20, height * 0.24), new Point(width * 0.28, height * 0.26));
            // Bottom-right arrow
            dc.DrawLine(arrowPen, new Point(width * 0.72, height * 0.74), new Point(width * 0.80, height * 0.76));
        }

        private static void DrawMcpSettingsContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var center = new Point(width * 0.50, height * 0.50);
            var outerR = min * 0.36;
            var innerR = min * 0.20;
            var toothHalf = Math.PI / 8.0;   // half-width of each tooth in radians
            var toothCount = 8;

            // Build gear geometry
            var gear = new StreamGeometry();
            using (var ctx = gear.Open())
            {
                var angleStep = (2.0 * Math.PI) / toothCount;
                for (var i = 0; i < toothCount; i++)
                {
                    var baseAngle = i * angleStep - Math.PI / 2.0;
                    var p0 = new Point(center.X + innerR * Math.Cos(baseAngle - toothHalf),
                                      center.Y + innerR * Math.Sin(baseAngle - toothHalf));
                    var p1 = new Point(center.X + outerR * Math.Cos(baseAngle - toothHalf * 0.6),
                                      center.Y + outerR * Math.Sin(baseAngle - toothHalf * 0.6));
                    var p2 = new Point(center.X + outerR * Math.Cos(baseAngle + toothHalf * 0.6),
                                      center.Y + outerR * Math.Sin(baseAngle + toothHalf * 0.6));
                    var p3 = new Point(center.X + innerR * Math.Cos(baseAngle + toothHalf),
                                      center.Y + innerR * Math.Sin(baseAngle + toothHalf));

                    if (i == 0)
                        ctx.BeginFigure(p0, true, true);
                    else
                        ctx.LineTo(p0, true, false);

                    ctx.LineTo(p1, true, false);
                    ctx.LineTo(p2, true, false);
                    ctx.LineTo(p3, true, false);
                }
            }
            gear.Freeze();

            var gearBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            gearBrush.Freeze();
            dc.DrawGeometry(gearBrush, null, gear);

            // Centre hole (cut out with white-ish overlay)
            var holeBrush = new SolidColorBrush(Color.FromArgb(255, 58, 125, 160));
            holeBrush.Freeze();
            dc.DrawEllipse(holeBrush, null, center, min * 0.11, min * 0.11);
        }

        private static void DrawAboutContent(DrawingContext dc, double width, double height)
        {
            var min = Math.Min(width, height);
            var ringBrush = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255));
            ringBrush.Freeze();
            var ringPen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), min * 0.06);
            ringPen.Freeze();

            var center = new Point(width * 0.50, height * 0.50);
            dc.DrawEllipse(ringBrush, ringPen, center, min * 0.28, min * 0.28);

            var dotBrush = new SolidColorBrush(Color.FromArgb(245, 79, 96, 20));
            dotBrush.Freeze();
            dc.DrawEllipse(dotBrush, null, new Point(width * 0.50, height * 0.30), min * 0.05, min * 0.05);

            var stemPen = new Pen(new SolidColorBrush(Color.FromArgb(245, 79, 96, 20)), min * 0.10)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            stemPen.Freeze();
            dc.DrawLine(stemPen, new Point(width * 0.50, height * 0.42), new Point(width * 0.50, height * 0.66));
        }
    }
}
