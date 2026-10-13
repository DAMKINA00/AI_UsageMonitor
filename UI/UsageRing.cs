using System.Windows;
using System.Windows.Media;

namespace AIUsageMonitor.UI;

public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty RemainingProperty = DependencyProperty.Register(
        nameof(Remaining), typeof(double), typeof(UsageRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Remaining { get => (double)GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        const double thickness = 4;
        double radius = Math.Max(0, (Math.Min(ActualWidth, ActualHeight) - thickness) / 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(54, 57, 65)), thickness), center, radius, radius);
        double remaining = double.IsFinite(Remaining) ? Math.Clamp(Remaining, 0, 100) : 0;
        if (remaining == 0 || radius == 0) return;
        var pen = new Pen(Palette.Level(100 - remaining), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (remaining == 100)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        double angle = remaining / 100 * 2 * Math.PI;
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, remaining > 50, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
