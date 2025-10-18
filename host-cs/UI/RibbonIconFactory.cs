using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitSuite.Host.UI
{
    internal static class RibbonIconFactory
    {
        public static ImageSource CreateLargeIcon(string glyph, Color background) =>
            CreateIcon(glyph, background, 32, 32, 16);

        public static ImageSource CreateSmallIcon(string glyph, Color background) =>
            CreateIcon(glyph, background, 16, 16, 9);

        private static ImageSource CreateIcon(string glyph, Color background, int width, int height, int fontSize)
        {
            var visual = new DrawingVisual();

            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));

                var text = new FormattedText(
                    glyph,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"),
                    fontSize,
                    Brushes.White,
                    1.0);

                var textLocation = new Point(
                    (width - text.Width) / 2.0,
                    (height - text.Height) / 2.0);

                dc.DrawText(text, textLocation);
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
