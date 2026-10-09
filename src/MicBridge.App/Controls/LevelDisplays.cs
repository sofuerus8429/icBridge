using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MicBridge.App.Controls;

public sealed class VerticalLevelMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(VerticalLevelMeter),
        new FrameworkPropertyMetadata(0d, (element, change) => { if (SegmentCount((double)change.OldValue) != SegmentCount((double)change.NewValue)) ((VerticalLevelMeter)element).InvalidateVisual(); }));
    private static readonly Brush Well = Frozen(new SolidColorBrush(Color.FromArgb(65, 0, 0, 0)));
    private static readonly Color[] Colors = [Color.FromRgb(111, 226, 135), Color.FromRgb(246, 196, 78), Color.FromRgb(255, 83, 92)];
    private static readonly Brush[] Lamps = Colors.Select(color => Frozen(new LinearGradientBrush(color, Color.FromRgb((byte)(color.R * 0.72), (byte)(color.G * 0.72), (byte)(color.B * 0.72)), 90))).ToArray();
    private static readonly Brush[] Glows = Colors.Select(color => Frozen(new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)))).ToArray();
    private static readonly Brush[] OffLamps = Colors.Select(color => Frozen(new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)))).ToArray();
    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
    private static int SegmentCount(double value) => (int)Math.Floor((Platform.IsFinite(value) ? Platform.Clamp(value, 0, 100) : 0) * 32 / 100);
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        const int segments = 32;
        double gap = Math.Min(3, ActualHeight / (segments * 3));
        double segment = Math.Max(0, (ActualHeight - gap * (segments - 1)) / segments);
        int activeSegments = SegmentCount(Value);
        drawing.DrawRoundedRectangle(Well, null, new Rect(-3, -4, ActualWidth + 6, ActualHeight + 8), 5, 5);
        for (int i = 0; i < segments; i++)
        {
            bool active = i < activeSegments;
            var rect = new Rect(0, ActualHeight - (i + 1) * segment - i * gap, ActualWidth, segment);
            double db = -60 + (i + 1) * 60d / segments;
            int color = db >= -3 ? 2 : db >= -12 ? 1 : 0;
            if (active)
            {
                drawing.DrawRoundedRectangle(Glows[color], null,
                    new Rect(rect.X - 2, rect.Y - 1, rect.Width + 4, rect.Height + 2), 3, 3);
                drawing.DrawRoundedRectangle(Lamps[color], null, rect, 1.2, 1.2);
            }
            else drawing.DrawRoundedRectangle(OffLamps[color], null, rect, 1.2, 1.2);
        }
    }
}

public sealed class DecibelScale : FrameworkElement
{
    protected override void OnRender(DrawingContext drawing)
    {
        var muted = new SolidColorBrush(Color.FromRgb(135, 135, 144));
        int[] ticks = ActualHeight < 100 ? [0, -20, -40, -60] : ActualHeight < 150 ? [0, -12, -24, -36, -48, -60] : [0, -6, -12, -24, -36, -48, -60];
        foreach (int db in ticks)
        {
            double y = ActualHeight * -db / 60;
            var text = new FormattedText(db.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(6, Platform.Clamp(y - text.Height / 2, 0, Math.Max(0, ActualHeight - text.Height))));
            drawing.DrawLine(new Pen(muted, 0.7), new Point(0, y), new Point(3, y));
        }
    }
}

public sealed class FaderScale : FrameworkElement
{
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(FaderScale), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        double maximum = Math.Max(100, Maximum), span = Math.Max(0, ActualHeight - 30);
        var muted = new SolidColorBrush(Color.FromRgb(135, 135, 144));
        foreach (double tick in maximum > 100 ? new double[] { 0, 50, 100, 200, 300 } : [0, 25, 50, 75, 100])
        {
            double y = 15 + span * (1 - tick / maximum);
            var text = new FormattedText(tick.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, tick == 100 ? Brushes.White : muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point(Math.Max(0, ActualWidth - text.Width - 9), y - text.Height / 2));
            drawing.DrawLine(new Pen(tick == 100 ? Brushes.White : muted, 1), new Point(ActualWidth - 5, y), new Point(ActualWidth, y));
        }
    }
}
