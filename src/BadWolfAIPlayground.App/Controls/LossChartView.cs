using System.Windows;
using System.Windows.Media;

namespace BadWolfAIPlayground.App.Controls;

/// <summary>Plots MSE on a logarithmic vertical scale during training.</summary>
public sealed class LossChartView : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<double>), typeof(LossChartView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Points
    {
        get => (IReadOnlyList<double>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 60 || height < 50)
        {
            return;
        }

        double left = 14;
        double right = width - 10;
        double top = 12;
        double bottom = height - 15;
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(42, 62, 88)), 1);

        for (int row = 0; row <= 4; row++)
        {
            double y = top + (bottom - top) * row / 4;
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(right, y));
        }

        if (Points is not { Count: > 0 } points)
        {
            return;
        }

        double[] values = points.Select(value => Math.Log10(Math.Max(value, 1e-8))).ToArray();
        double min = Math.Min(-2, values.Min());
        double max = Math.Max(0, values.Max());
        double range = Math.Max(0.001, max - min);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int index = 0; index < values.Length; index++)
            {
                double x = left + (right - left) * index / Math.Max(1, values.Length - 1);
                double y = bottom - (values[index] - min) / range * (bottom - top);
                var point = new Point(x, y);
                if (index == 0)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null,
            new Pen(new SolidColorBrush(Color.FromRgb(49, 177, 254)), 2.5), geometry);

        double lastY = bottom - (values[^1] - min) / range * (bottom - top);
        drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(67, 214, 208)),
            null, new Point(right, lastY), 4, 4);
    }
}
