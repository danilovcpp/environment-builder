using System.Windows;
using System.Windows.Controls;

namespace EnvironmentBuilder.App.Components;

/// <summary>A wrapping row with consistent gaps, including between wrapped lines.</summary>
public sealed class SpacedWrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(SpacedWrapPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double spacing && double.IsFinite(spacing) && spacing >= 0);

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) { child.Measure(availableSize); }
        return Layout(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        var row = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) { continue; }
            double gap = row.Count > 0 ? Spacing : 0;
            if (row.Count > 0 && x + gap + child.DesiredSize.Width > width)
            {
                FinishRow();
                y += rowHeight + Spacing;
                row.Clear();
                x = 0; rowHeight = 0; gap = 0;
            }

            row.Add(child);
            x += gap + child.DesiredSize.Width;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        FinishRow();
        return new Size(usedWidth, y + rowHeight);

        void FinishRow()
        {
            usedWidth = Math.Max(usedWidth, x);
            if (!arrange) { return; }
            double left = 0;
            foreach (UIElement item in row)
            {
                Size size = item.DesiredSize;
                item.Arrange(new Rect(left, y + (rowHeight - size.Height) / 2, size.Width, size.Height));
                left += size.Width + Spacing;
            }
        }
    }
}
