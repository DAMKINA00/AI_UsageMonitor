using System.Windows;

namespace AIUsageMonitor.UI;

public static class CharacterPlacement
{
    public static Point Clamp(Point point, Rect limits) => new(
        Math.Clamp(point.X, limits.Left, limits.Right), Math.Clamp(point.Y, limits.Top, limits.Bottom));

    public static Point DragHeight(Point origin, double verticalChange, Rect limits) =>
        new(origin.X, Math.Clamp(origin.Y + verticalChange, limits.Top, limits.Bottom));

    public static Rect At(Rect body, Point origin)
    {
        if (body.IsEmpty) return body;
        body.Offset(origin.X, origin.Y);
        return body;
    }

    public static Point Separate(Point desired, Rect body, Rect limits, IReadOnlyList<Rect> occupied)
    {
        desired = Clamp(desired, limits);
        bool Fits(Point origin) => occupied.All(other =>
        {
            var overlap = Rect.Intersect(At(body, origin), other);
            return overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0;
        });
        if (Fits(desired)) return desired;

        var xs = new List<double> { desired.X, limits.Left, limits.Right };
        var ys = new List<double> { desired.Y, limits.Top, limits.Bottom };
        foreach (var other in occupied)
        {
            xs.Add(other.Left - body.Right);
            xs.Add(other.Right - body.Left);
            ys.Add(other.Top - body.Bottom);
            ys.Add(other.Bottom - body.Top);
        }
        return (from x in xs.Distinct() where x >= limits.Left && x <= limits.Right
                from y in ys.Distinct() where y >= limits.Top && y <= limits.Bottom
                let candidate = new Point(x, y) where Fits(candidate)
                orderby (candidate - desired).LengthSquared
                select (Point?)candidate).FirstOrDefault() ?? desired;
    }
}
