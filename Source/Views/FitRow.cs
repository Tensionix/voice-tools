using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace AudionVoiceTools.Views;

/// <summary>
/// A row that shows as many of its children, in order, as fit its width, and leaves the rest out - never clipped in the
/// middle of a badge. The versions of the header (MainWindow.Components.cs): a narrow window loses the last badges first.
/// </summary>
public sealed class FitRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<FitRow, double>(nameof(Spacing), 6);

    static FitRow() => AffectsMeasure<FitRow>(SpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    int _fits;

    protected override Size MeasureOverride(Size availableSize)
    {
        double used = 0, height = 0;
        _fits = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            double next = used + (_fits > 0 ? Spacing : 0) + child.DesiredSize.Width;
            if (next > availableSize.Width + 0.5) break;
            used = next;
            height = Math.Max(height, child.DesiredSize.Height);
            _fits++;
        }
        return new Size(used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        int at = 0;
        foreach (var child in Children)
        {
            if (at++ >= _fits)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }
        return finalSize;
    }
}
