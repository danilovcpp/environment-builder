using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EnvironmentBuilder.App.Components;

/// <summary>Clips full-bleed panel content to the same corners as its border.</summary>
public sealed class RoundedClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double width, double height, CornerRadius radius] || width <= 0 || height <= 0)
        {
            return Geometry.Empty;
        }

        // Scale all corners together when the panel is smaller than their combined radii.
        double scale = Math.Min(1, Math.Min(
            Math.Min(Ratio(width, radius.TopLeft + radius.TopRight), Ratio(width, radius.BottomLeft + radius.BottomRight)),
            Math.Min(Ratio(height, radius.TopLeft + radius.BottomLeft), Ratio(height, radius.TopRight + radius.BottomRight))));
        double tl = radius.TopLeft * scale, tr = radius.TopRight * scale;
        double br = radius.BottomRight * scale, bl = radius.BottomLeft * scale;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(tl, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - tr, 0), true, false);
            Corner(context, new Point(width, tr), tr);
            context.LineTo(new Point(width, height - br), true, false);
            Corner(context, new Point(width - br, height), br);
            context.LineTo(new Point(bl, height), true, false);
            Corner(context, new Point(0, height - bl), bl);
            context.LineTo(new Point(0, tl), true, false);
            Corner(context, new Point(tl, 0), tl);
        }

        geometry.Freeze();
        return geometry;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double Ratio(double length, double radii) => radii > 0 ? length / radii : 1;

    private static void Corner(StreamGeometryContext context, Point end, double radius)
    {
        if (radius > 0)
        {
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        else
        {
            context.LineTo(end, true, false);
        }
    }
}
