using System.Windows;
using System.Windows.Media;
using BadWolfAIPlayground.Core;

namespace BadWolfAIPlayground.App.Controls;

/// <summary>Displays signed, weighted connections of the current network snapshot.</summary>
public sealed class NeuralNetworkView : FrameworkElement
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(NetworkSnapshot), typeof(NeuralNetworkView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public NetworkSnapshot? Snapshot
    {
        get => (NetworkSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        NetworkSnapshot? snapshot = Snapshot;
        if (snapshot is null || ActualWidth < 100 || ActualHeight < 100)
        {
            return;
        }

        int layers = snapshot.LayerSizes.Length;
        double left = 45;
        double right = ActualWidth - 45;
        double top = 25;
        double bottom = ActualHeight - 30;
        var nodePositions = new Point[layers][];

        for (int layer = 0; layer < layers; layer++)
        {
            int count = snapshot.LayerSizes[layer];
            nodePositions[layer] = new Point[count];
            double x = left + (right - left) * layer / (layers - 1);

            for (int neuron = 0; neuron < count; neuron++)
            {
                double y = count == 1
                    ? (top + bottom) / 2
                    : top + (bottom - top) * neuron / (count - 1);
                nodePositions[layer][neuron] = new Point(x, y);
            }
        }

        for (int layer = 0; layer < layers - 1; layer++)
        {
            for (int neuron = 0; neuron < nodePositions[layer + 1].Length; neuron++)
            {
                for (int input = 0; input < nodePositions[layer].Length; input++)
                {
                    double weight = snapshot.Weights[layer][neuron][input];
                    byte alpha = (byte)(55 + 145 * Math.Min(1, Math.Abs(weight) / 3));
                    Color color = weight >= 0
                        ? Color.FromArgb(alpha, 33, 198, 216)
                        : Color.FromArgb(alpha, 187, 107, 252);
                    var pen = new Pen(new SolidColorBrush(color),
                        1 + Math.Min(1.8, Math.Abs(weight) / 2.0));
                    drawingContext.DrawLine(pen,
                        nodePositions[layer][input], nodePositions[layer + 1][neuron]);
                }
            }
        }

        for (int layer = 0; layer < layers; layer++)
        {
            for (int neuron = 0; neuron < nodePositions[layer].Length; neuron++)
            {
                Point center = nodePositions[layer][neuron];
                bool isInput = layer == 0;
                bool isOutput = layer == layers - 1;
                double radius = isInput || isOutput ? 16 : 10;
                Brush fill = isInput ? new SolidColorBrush(Color.FromRgb(38, 122, 242))
                    : isOutput ? new SolidColorBrush(Color.FromRgb(242, 158, 55))
                    : new SolidColorBrush(Color.FromRgb(21, 179, 164));

                drawingContext.DrawEllipse(
                    new SolidColorBrush(Color.FromArgb(32, 72, 156, 255)),
                    null, center, radius + 5, radius + 5);
                drawingContext.DrawEllipse(fill,
                    new Pen(new SolidColorBrush(Color.FromRgb(178, 228, 255)), 0.8),
                    center, radius, radius);

                if (isInput || isOutput)
                {
                    string label = isInput ? $"x{neuron + 1}" : "y";
                    var text = new FormattedText(label, CultureInfoForText(),
                        FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
                        11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    drawingContext.DrawText(text,
                        new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                }
            }
        }
    }

    private static System.Globalization.CultureInfo CultureInfoForText() =>
        System.Globalization.CultureInfo.InvariantCulture;
}
