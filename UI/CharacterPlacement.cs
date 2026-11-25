using System.Windows;

namespace AIUsageMonitor.UI;

public static class CharacterPlacement
{
    public static Point Clamp(Point point, Rect limits) => new(
        Math.Clamp(point.X, limits.Left, limits.Right), Math.Clamp(point.Y, limits.Top, limits.Bottom));

    public static Point Drag(Point origin, Vector change, Rect limits) =>
        new(Math.Clamp(origin.X + change.X, limits.Left, limits.Right), limits.Bottom);

    public static Rect At(Rect body, Point origin)
    {
        if (body.IsEmpty) return body;
        body.Offset(origin.X, origin.Y);
        return body;
    }

    public static Point Separate(Point desired, Rect body, Rect limits, IReadOnlyList<Rect> occupied)
    {
        desired = Drag(desired, default, limits);
        bool Fits(Point origin) => occupied.All(other =>
        {
            var overlap = Rect.Intersect(At(body, origin), other);
            return overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0;
        });
        if (Fits(desired)) return desired;

        var xs = new List<double> { desired.X, limits.Left, limits.Right };
        foreach (var other in occupied)
        {
            xs.Add(other.Left - body.Right);
            xs.Add(other.Right - body.Left);
        }
        return (from x in xs.Distinct() where x >= limits.Left && x <= limits.Right
                let candidate = new Point(x, limits.Bottom) where Fits(candidate)
                orderby Math.Abs(x - desired.X)
                select (Point?)candidate).FirstOrDefault() ?? desired;
    }
}
