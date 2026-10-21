using System.Windows;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    static void TestTaskbarWidget()
    {
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        var rows = Presenter.AccountUsageRows([
            new UsageWindow("five_hour", "5시간", 0, now.AddHours(1)),
            new UsageWindow("seven_day", "주간", 100, now.AddDays(1)),
        ], asOf: now);
        Check("taskbar quota renders 100% remaining", TaskbarWidget.QuotaText(rows[0], now) == "100%", TaskbarWidget.QuotaText(rows[0], now));
        Check("taskbar quota renders 0% remaining", TaskbarWidget.QuotaText(rows[1], now) == "0%", TaskbarWidget.QuotaText(rows[1], now));

        var unknown = Presenter.AccountUsageRows([], asOf: now);
        Check("taskbar unknown quota has no filled value", TaskbarWidget.QuotaText(unknown[0], now) == "—" && TaskbarWidget.ResetCountdownText(unknown[0], now) == "—",
            $"quota={TaskbarWidget.QuotaText(unknown[0], now)}, reset={TaskbarWidget.ResetCountdownText(unknown[0], now)}");

        var unlimited = Presenter.AccountUsageRows([], fiveHourUnlimited: true, asOf: now);
        Check("taskbar unlimited quota is explicit", TaskbarWidget.QuotaText(unlimited[0], now) == "∞" && TaskbarWidget.ResetCountdownText(unlimited[0], now) == "—",
            $"quota={TaskbarWidget.QuotaText(unlimited[0], now)}, reset={TaskbarWidget.ResetCountdownText(unlimited[0], now)}");

        var future = Presenter.AccountUsageRows([
            new UsageWindow("seven_day", "주간", 25, now.AddMinutes(1)),
        ], asOf: now)[1];
        var crossed = Presenter.AccountUsageRows([
            new UsageWindow("seven_day", "주간", 25, now.AddSeconds(-1)),
        ], asOf: now)[1];
        Check("taskbar reset countdown is short before crossing", TaskbarWidget.ResetCountdownText(future, now) == "1m",
            TaskbarWidget.ResetCountdownText(future, now));
        Check("taskbar reset crossing becomes pending", TaskbarWidget.QuotaText(crossed, now) == "—" &&
            TaskbarWidget.ResetCountdownText(crossed, now) == "확인 중",
            $"quota={TaskbarWidget.QuotaText(crossed, now)}, reset={TaskbarWidget.ResetCountdownText(crossed, now)}");

        Check("taskbar timer invalidates an existing row when reset time passes",
            TaskbarWidget.QuotaText(future, now.AddMinutes(1)) == "—" &&
            TaskbarWidget.ResetCountdownText(future, now.AddMinutes(1)) == "확인 중", "cached row kept old percentage");
        var stale = new StatusChip(StatusKind.None, now.AddMinutes(-20), TimeSpan.FromMinutes(5), false);
        Check("taskbar stale cards are dimmed", stale.IsDimmedAt(now), stale.TextAt(now));

        var taskbar = new Rect(-1920, 1040, 3840, 40);
        var workArea = new Rect(-1920, 0, 3840, 1040);
        var notification = new Rect(1500, 1040, 420, 40);
        var occupied = new[]
        {
            new Rect(-1920, 1040, 2200, 40),
            new Rect(800, 1040, 200, 40),
            notification,
        };
        var inline = TaskbarWidget.FindPlacement(taskbar, workArea, new Size(480, 32), occupied, notification);
        Check("taskbar placement chooses the rightmost verified gap", inline is { } inlineTarget &&
            inlineTarget.Left > 1000 && inlineTarget.Right <= notification.Left - 4 &&
            occupied.All(x => Rect.Intersect(inlineTarget, x).IsEmpty),
            inline?.ToString() ?? "null");

        var fallback = TaskbarWidget.FindPlacement(taskbar, workArea, new Size(500, 76),
            [new Rect(-1920, 1040, 3840, 40)], notification);
        Check("taskbar placement falls back above a blocked bar", fallback is { } fallbackTarget &&
            fallbackTarget.Bottom <= taskbar.Top && workArea.Contains(fallbackTarget),
            fallback?.ToString() ?? "null");

        var converted = TaskbarWidget.PhysicalToDip(
            new Rect(-1600, 1500, 300, 60),
            new Rect(-1920, 0, 3840, 2160),
            new Rect(-1280, 0, 2560, 1440),
            1.5, 1.5);
        Check("taskbar physical to DIP conversion preserves negative origin and scale",
            Nearly(converted.Left, -1066.6666667) && Nearly(converted.Top, 1000) &&
            Nearly(converted.Width, 200) && Nearly(converted.Height, 40), converted.ToString());

        var narrow = TaskbarWidget.FindPlacement(
            new Rect(-300, 600, 300, 40), new Rect(-300, 0, 300, 600), new Size(500, 76),
            Array.Empty<Rect>(), notificationArea: null);
        Check("taskbar placement rejects a narrow work area", narrow == null, narrow?.ToString() ?? "null");

        using var widget = new TaskbarWidget();
        widget.Update(new[] { "Claude", "Codex", "Gemini", "Grok" }.Select(name => new ProviderCard
        {
            Name = name, Accent = Palette.Claude, AppStatus = "fixture", AppStatusBrush = Palette.Muted,
            Footnote = "fixture", SessionsHeader = "", AccountUsage = Presenter.AccountUsageRows([
                new UsageWindow("five_hour", "5시간", 23, DateTimeOffset.Now.AddHours(2)),
                new UsageWindow("seven_day", "주간", 61, DateTimeOffset.Now.AddDays(3)),
            ]),
        }).ToArray());
        widget.Tick(); // Disabled: render snapshots without probing or changing the real taskbar.
        var content = (FrameworkElement)widget.Content;
        content.Measure(new Size(widget.Width, widget.Height));
        content.Arrange(new Rect(0, 0, widget.Width, widget.Height));
        content.UpdateLayout();
        Check("four-provider taskbar strip fits a compact taskbar height", widget.Height <= 30 && widget.Width <= 500,
            $"{widget.Width}x{widget.Height}");
        var compactBar = new Rect(0, 1410, 3440, 30);
        var compactPlacement = TaskbarWidget.FindPlacement(compactBar, new Rect(0, 0, 3440, 1410),
            new Size(widget.Width, widget.Height), [new Rect(0, 1410, 1000, 30)], new Rect(3100, 1410, 340, 30));
        Check("compact taskbar uses the bar itself when a verified gap fits", compactPlacement is { } placed && compactBar.Contains(placed),
            compactPlacement?.ToString() ?? "null");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)widget.Width, (int)widget.Height,
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIUsageMonitor-taskbar-fixture.png");
        using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
        Console.WriteLine($"INFO rendered taskbar fixture: {path}");
    }

    static bool Nearly(double actual, double expected) => Math.Abs(actual - expected) < 0.01;
}
