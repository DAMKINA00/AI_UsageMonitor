using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

/// <summary>
/// A small, read-only usage window placed near the Windows taskbar.
/// It never owns provider services or asks for fresh data.
/// </summary>
public sealed class TaskbarWidget : Window, IDisposable
{
    const int TileCount = 4;
    const double TileWidth = 120;
    const double TileHeight = 28;
    const double WidgetMargin = 1;
    const double PlacementGap = 4;
    const int RepositionSeconds = 2;
    const int DpiDefault = 96;

    static readonly Brush BackgroundBrush = Brush(Color.FromRgb(27, 28, 33));
    static readonly Brush TileBrush = Brush(Color.FromRgb(34, 36, 43));
    static readonly Brush TrackBrush = Brush(Color.FromRgb(54, 57, 65));
    static readonly Brush MutedBrush = Palette.Muted;

    readonly DispatcherTimer _timer;
    readonly SemaphoreSlim _probeGate = new(1, 1);
    readonly TileVisual[] _tiles;
    readonly Grid _root;
    IReadOnlyList<ProviderCard> _cards = [];
    IntPtr _hwnd;
    bool _enabled;
    bool _disposed;
    bool _probeQueued;
    DateTimeOffset _nextPlacement = DateTimeOffset.MinValue;

    /// <summary>Vertical DIP space occupied above a taskbar fallback placement.</summary>
    public double DesktopInset { get; private set; }

    public event Action? OpenRequested;
    public event Action? RefreshRequested;
    public event Action? HideRequested;
    public event Action? PlacementChanged;

    public TaskbarWidget()
    {
        Title = "AI 사용량 · 작업표시줄";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Background = BackgroundBrush;
        Width = WidgetMargin * 2 + TileCount * TileWidth;
        Height = TileHeight + WidgetMargin * 2;
        Opacity = 0;

        _root = new Grid
        {
            Background = BackgroundBrush,
            Margin = new Thickness(WidgetMargin),
        };
        _tiles = Enumerable.Range(0, TileCount).Select(i => new TileVisual()).ToArray();
        for (int i = 0; i < _tiles.Length; i++)
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileWidth) });
            Grid.SetColumn(_tiles[i].Host, i);
            _root.Children.Add(_tiles[i].Host);
        }
        Content = _root;
        ContextMenu = BuildContextMenu();

        SourceInitialized += (_, _) => InitializeNativeWindow();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        MouseLeftButtonUp += Widget_MouseLeftButtonUp;
    }

    /// <summary>Turns the widget and its taskbar probe on or off. No external probe runs while disabled.</summary>
    public void SetEnabled(bool enabled)
    {
        if (InvokeOnUi(() => SetEnabled(enabled))) return;
        if (_disposed || _enabled == enabled) return;

        _enabled = enabled;
        if (!enabled)
        {
            _timer.Stop();
            HideWidget();
            return;
        }

        // Create the HWND without showing a misplaced frame. The first valid probe makes it opaque.
        Opacity = 0;
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        Show();
        _timer.Start();
        _nextPlacement = DateTimeOffset.MinValue;
        RenderCards(DateTimeOffset.Now);
        QueuePlacement();
    }

    /// <summary>Replaces the displayed snapshot. This method does not query any service.</summary>
    public void Update(IReadOnlyList<ProviderCard> cards)
    {
        if (InvokeOnUi(() => Update(cards))) return;
        if (_disposed) return;
        _cards = cards?.ToArray() ?? [];
        if (_enabled) RenderCards(DateTimeOffset.Now);
    }

    /// <summary>Refreshes countdown text and status dimming without replacing the cards.</summary>
    public void Tick()
    {
        if (InvokeOnUi(Tick)) return;
        if (_disposed) return;
        RenderCards(DateTimeOffset.Now);
        if (_enabled && DateTimeOffset.UtcNow >= _nextPlacement) QueuePlacement();
    }

    public void Dispose()
    {
        if (InvokeOnUi(Dispose, synchronous: true)) return;
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _timer.Stop();
        HideWidget();
        if (IsLoaded) Close();
    }

    bool InvokeOnUi(Action action, bool synchronous = false)
    {
        if (Dispatcher.CheckAccess()) return false;
        if (Dispatcher.HasShutdownStarted) return true;
        try
        {
            if (synchronous) Dispatcher.Invoke(action);
            else Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException) { }
        return true;
    }

    /// <summary>Returns the compact remaining value used in a taskbar row.</summary>
    public static string QuotaText(UsageRow? row, DateTimeOffset? asOf = null)
    {
        if (row == null || row.IsUnknown || !double.IsFinite(row.Value) || IsPending(row, asOf ?? DateTimeOffset.Now)) return "—";
        if (row.IsUnlimited) return "∞";
        return Fmt.Pct(row.Remaining);
    }

    /// <summary>Returns a non-negative short reset label, including the unknown/expired states.</summary>
    public static string ResetCountdownText(UsageRow? row, DateTimeOffset now)
    {
        if (row == null || row.IsUnknown) return "—";
        if (IsPending(row, now)) return "확인 중";
        if (row.ResetsAt is not { } reset) return "—";
        var minutes = Math.Max(1, (long)Math.Ceiling((reset - now).TotalMinutes));
        if (minutes >= 1440) return $"{minutes / 1440}d{minutes % 1440 / 60}h";
        if (minutes >= 60) return $"{minutes / 60}h{minutes % 60}m";
        return $"{minutes}m";
    }

    static bool IsPending(UsageRow row, DateTimeOffset now) => row.IsResetPending || row.ResetsAt <= now;

    /// <summary>Converts a physical-screen rectangle into the target monitor's WPF DIP coordinates.</summary>
    public static Rect PhysicalToDip(Rect physical, Rect monitorPhysical, Rect monitorDip, double scaleX, double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0 || physical.IsEmpty || monitorPhysical.IsEmpty || monitorDip.IsEmpty)
            return Rect.Empty;
        return new Rect(
            monitorDip.Left + (physical.Left - monitorPhysical.Left) / scaleX,
            monitorDip.Top + (physical.Top - monitorPhysical.Top) / scaleY,
            physical.Width / scaleX,
            physical.Height / scaleY);
    }

    /// <summary>
    /// Finds a free horizontal interval, preferring the rightmost interval immediately left of the
    /// notification area when that area was verified. If none is verifiable, it falls back to the
    /// adjacent work area so the widget cannot cover app buttons or the notification area.
    /// </summary>
    public static Rect? FindPlacement(Rect taskbar, Rect workArea, Size widgetSize, IReadOnlyList<Rect> occupied,
        Rect? notificationArea = null, double gap = PlacementGap)
    {
        if (taskbar.IsEmpty || workArea.IsEmpty || widgetSize.Width <= 0 || widgetSize.Height <= 0 || gap < 0 ||
            taskbar.Width < taskbar.Height || workArea.Width < widgetSize.Width || workArea.Height < widgetSize.Height)
            return null;

        double left = taskbar.Left + gap;
        double right = taskbar.Right - gap;
        if (notificationArea is { } notification &&
            Rect.Intersect(taskbar, notification) is { IsEmpty: false } notificationHit &&
            notificationHit.Width > 0 && notificationHit.Height > 0)
            right = Math.Min(right, notificationHit.Left - gap);

        if (right - left >= widgetSize.Width && taskbar.Height >= widgetSize.Height)
        {
            var intervals = FreeIntervals(left, right, taskbar, occupied, gap);
            if (intervals.Count > 0)
            {
                var chosen = intervals
                    .Where(x => x.Right - x.Left >= widgetSize.Width)
                    .OrderByDescending(x => x.Right)
                    .ThenByDescending(x => x.Right - x.Left)
                    .FirstOrDefault();
                if (chosen.Right - chosen.Left >= widgetSize.Width)
                {
                    double x = chosen.Right - widgetSize.Width;
                    double y = taskbar.Top + Math.Max(0, (taskbar.Height - widgetSize.Height) / 2);
                    return new Rect(x, y, widgetSize.Width, widgetSize.Height);
                }
            }
        }

        bool bottom = taskbar.Top >= workArea.Top + workArea.Height / 2;
        double fallbackTop = bottom
            ? taskbar.Top - widgetSize.Height - gap
            : taskbar.Bottom + gap;
        if (fallbackTop < workArea.Top || fallbackTop + widgetSize.Height > workArea.Bottom) return null;

        double fallbackLeft = taskbar.Left + (taskbar.Width - widgetSize.Width) / 2;
        fallbackLeft = Math.Clamp(fallbackLeft, workArea.Left, workArea.Right - widgetSize.Width);
        return new Rect(fallbackLeft, fallbackTop, widgetSize.Width, widgetSize.Height);
    }

    static List<Rect> FreeIntervals(double left, double right, Rect taskbar, IReadOnlyList<Rect> occupied, double gap)
    {
        var blocks = occupied
            .Where(x => !x.IsEmpty)
            .Select(x => (Rect: x, Hit: Rect.Intersect(taskbar, x)))
            .Where(x => !x.Hit.IsEmpty && x.Hit.Width > 0 && x.Hit.Height > 0)
            .Select(x => (Left: Math.Max(left, x.Rect.Left - gap), Right: Math.Min(right, x.Rect.Right + gap)))
            .Where(x => x.Right > x.Left)
            .OrderBy(x => x.Left)
            .ToList();

        var result = new List<Rect>();
        double cursor = left;
        foreach (var block in blocks)
        {
            if (block.Left > cursor) result.Add(new Rect(cursor, taskbar.Top, block.Left - cursor, taskbar.Height));
            cursor = Math.Max(cursor, block.Right);
            if (cursor >= right) break;
        }
        if (cursor < right) result.Add(new Rect(cursor, taskbar.Top, right - cursor, taskbar.Height));
        return result;
    }

    void InitializeNativeWindow()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WindowMessage);
        Native.MakeToolWindow(_hwnd);
    }

    ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "지금 새로고침" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        var hide = new MenuItem { Header = "작업 표시줄 위젯 숨기기" };
        hide.Click += (_, _) => HideRequested?.Invoke();
        menu.Items.Add(refresh);
        menu.Items.Add(hide);
        return menu;
    }

    void Widget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        OpenRequested?.Invoke();
        e.Handled = true;
    }

    void RenderCards(DateTimeOffset now)
    {
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (i >= _cards.Count)
            {
                _tiles[i].Host.Visibility = Visibility.Collapsed;
                continue;
            }
            _tiles[i].Update(_cards[i], RowsFor(_cards[i]), now);
        }
    }

    static IReadOnlyList<UsageRow> RowsFor(ProviderCard card)
    {
        var rows = card.AccountUsage.Take(2).ToList();
        if (rows.Count == 2) return rows;
        var fallback = Presenter.AccountUsageRows(card.RawWindows);
        rows.AddRange(fallback.Skip(rows.Count).Take(2 - rows.Count));
        while (rows.Count < 2) rows.AddRange(Presenter.AccountUsageRows([]).Take(2 - rows.Count));
        return rows;
    }

    void QueuePlacement()
    {
        if (!_enabled || _disposed || _probeQueued) return;
        _probeQueued = true;
        _nextPlacement = DateTimeOffset.UtcNow.AddSeconds(RepositionSeconds);

        var hwnd = _hwnd;
        var widgetLeft = Left;
        var widgetTop = Top;
        _ = Task.Run(() =>
        {
            PlacementProbe? probe = null;
            bool entered = false;
            try
            {
                _probeGate.Wait();
                entered = true;
                probe = ProbeTaskbar(hwnd, widgetLeft, widgetTop);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or ExternalException)
            {
                probe = null;
            }
            finally
            {
                if (entered) _probeGate.Release();
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _probeQueued = false;
                    if (_enabled && !_disposed) ApplyProbe(probe);
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    void ApplyProbe(PlacementProbe? probe)
    {
        if (!_enabled || _disposed) return;
        var placement = probe is null
            ? null
            : FindPlacement(probe.Taskbar, probe.WorkArea, new Size(Width, Height), probe.Occupied, probe.Notification);
        if (placement is not { } target)
        {
            HideWidget();
            return;
        }

        bool inline = target.Top >= probe!.Taskbar.Top && target.Bottom <= probe.Taskbar.Bottom;
        bool bottom = probe.Taskbar.Top >= probe.WorkArea.Top + probe.WorkArea.Height / 2;
        double inset = inline
            ? 0
            : bottom
                ? Math.Max(0, probe.Taskbar.Top - target.Top)
                : Math.Max(0, target.Bottom - probe.Taskbar.Bottom);
        SetDesktopInset(inset);
        Left = target.Left;
        Top = target.Top;
        if (!IsVisible) Show();
        Opacity = 1;
    }

    void HideWidget()
    {
        SetDesktopInset(0);
        Opacity = 0;
        if (IsVisible) Hide();
    }

    void SetDesktopInset(double inset)
    {
        inset = Math.Max(0, inset);
        if (Math.Abs(DesktopInset - inset) < 0.01) return;
        DesktopInset = inset;
        PlacementChanged?.Invoke();
    }

    PlacementProbe? ProbeTaskbar(IntPtr widgetHwnd, double widgetLeft, double widgetTop)
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray) || !Native.GetWindowRect(tray, out var trayRect)) return null;
        var monitor = Native.MonitorFromWindow(tray, Native.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, out var monitorInfo)) return null;

        var taskbar = trayRect.ToRect();
        var monitorBounds = monitorInfo.Monitor.ToRect();
        var workArea = monitorInfo.Work.ToRect();
        if (taskbar.Width < taskbar.Height || taskbar.Height < 16 ||
            Rect.Intersect(taskbar, monitorBounds) is not { IsEmpty: false } visible || visible.Width < 16 || visible.Height < 8 ||
            IsFullscreenOnMonitor(tray, monitor, monitorBounds)) return null;

        var occupied = new List<Rect>();
        Rect? notifyPhysical = null;
        var notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var notifyRect))
        {
            notifyPhysical = notifyRect.ToRect();
            occupied.Add(notifyPhysical.Value);
        }
        var automation = AutomationOccupied(tray, taskbar, notifyPhysical);
        if (automation.Verified) occupied.AddRange(automation.Occupied);
        else occupied.Add(taskbar); // UIA could not prove the app-button interval is free.

        double scaleX = 1, scaleY = 1;
        if (widgetHwnd != IntPtr.Zero)
        {
            var dpi = Native.GetDpiForWindow(widgetHwnd);
            if (dpi > 0) scaleX = scaleY = dpi / (double)DpiDefault;
        }

        var widgetMonitor = widgetHwnd == IntPtr.Zero
            ? IntPtr.Zero
            : Native.MonitorFromWindow(widgetHwnd, Native.MonitorDefaultToNearest);
        Rect monitorDip;
        if (widgetMonitor == monitor && widgetHwnd != IntPtr.Zero && Native.GetWindowRect(widgetHwnd, out var widgetRect))
        {
            var current = widgetRect.ToRect();
            monitorDip = new Rect(
                widgetLeft - (current.Left - monitorBounds.Left) / scaleX,
                widgetTop - (current.Top - monitorBounds.Top) / scaleY,
                monitorBounds.Width / scaleX,
                monitorBounds.Height / scaleY);
        }
        else
        {
            // The primary taskbar is normally the widget's monitor. This fallback keeps negative origins valid.
            monitorDip = new Rect(monitorBounds.Left / scaleX, monitorBounds.Top / scaleY,
                monitorBounds.Width / scaleX, monitorBounds.Height / scaleY);
        }

        var taskbarDip = PhysicalToDip(taskbar, monitorBounds, monitorDip, scaleX, scaleY);
        var workAreaDip = PhysicalToDip(workArea, monitorBounds, monitorDip, scaleX, scaleY);
        var notifyDip = notifyPhysical is { } notifyRectDip
            ? PhysicalToDip(notifyRectDip, monitorBounds, monitorDip, scaleX, scaleY)
            : (Rect?)null;
        return new PlacementProbe(
            taskbarDip,
            workAreaDip,
            occupied.Select(x => PhysicalToDip(x, monitorBounds, monitorDip, scaleX, scaleY)).Where(x => !x.IsEmpty).ToArray(),
            notifyDip is { IsEmpty: false } ? notifyDip : null);
    }

    static AutomationScan AutomationOccupied(IntPtr tray, Rect taskbar, Rect? notification)
    {
        var result = new List<Rect>();
        bool failed = false;
        try
        {
            var root = AutomationElement.FromHandle(tray);
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true));
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
            {
                try
                {
                    var current = element.Current;
                    if (current.IsOffscreen) continue;
                    var bounds = current.BoundingRectangle;
                    if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
                        Rect.Intersect(taskbar, bounds) is { IsEmpty: false } hit && hit.Width > 0 && hit.Height > 0)
                        result.Add(bounds);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
                {
                    failed = true;
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            failed = true;
        }
        bool hasAppAreaElement = result.Any(x => notification is not { } area || Rect.Intersect(area, x).IsEmpty);
        return new AutomationScan(result, !failed && hasAppAreaElement);
    }

    bool IsFullscreenOnMonitor(IntPtr tray, IntPtr monitor, Rect monitorBounds)
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd || foreground == tray || !Native.IsWindowVisible(foreground) || Native.IsIconic(foreground))
            return false;
        var className = new StringBuilder(256);
        Native.GetClassName(foreground, className, className.Capacity);
        // Clicking the desktop must not be mistaken for entering a fullscreen application.
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_SecondaryTrayWnd") return false;
        if (Native.MonitorFromWindow(foreground, Native.MonitorDefaultToNearest) != monitor || !Native.GetWindowRect(foreground, out var rect))
            return false;
        var intersection = Rect.Intersect(monitorBounds, rect.ToRect());
        return !intersection.IsEmpty && intersection.Width >= monitorBounds.Width - 2 && intersection.Height >= monitorBounds.Height - 2;
    }

    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WmMouseActivate)
        {
            handled = true;
            return new IntPtr(Native.MaNoActivate);
        }
        return IntPtr.Zero;
    }

    static Brush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    sealed class TileVisual
    {
        public Border Host { get; }
        readonly TextBlock _glyph;
        readonly TextBlock _name;
        readonly RowVisual[] _rows;

        public TileVisual()
        {
            Host = new Border
            {
                Background = TileBrush,
                BorderBrush = TrackBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(3, 0, 3, 0),
                Height = TileHeight,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _glyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Malgun Gothic"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Height = 17,
            };
            _name = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            identity.Children.Add(_glyph);
            identity.Children.Add(_name);
            Grid.SetColumn(identity, 0);
            body.Children.Add(identity);
            _rows = [new RowVisual(), new RowVisual()];
            var usage = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var row in _rows) usage.Children.Add(row.Host);
            Grid.SetColumn(usage, 1);
            body.Children.Add(usage);
            Host.Child = body;
        }

        public void Update(ProviderCard card, IReadOnlyList<UsageRow> rows, DateTimeOffset now)
        {
            Host.Visibility = Visibility.Visible;
            Host.BorderBrush = card.Accent;
            Host.Opacity = card.Status.IsDimmedAt(now) ? 0.45 : 1;
            _glyph.Text = Glyph(card.Name);
            _glyph.Foreground = card.Accent;
            _name.Text = card.Name;
            _name.Foreground = card.Accent;
            Host.ToolTip = BuildTooltip(card, rows, now);
            for (int i = 0; i < _rows.Length; i++) _rows[i].Update(rows.ElementAtOrDefault(i), now);
        }

        static string Glyph(string name)
        {
            var text = name.Trim();
            return text.Length == 0 ? "·" : text[..1].ToUpperInvariant();
        }

        static string BuildTooltip(ProviderCard card, IReadOnlyList<UsageRow> rows, DateTimeOffset now)
        {
            var lines = new List<string> { card.Name };
            if (!string.IsNullOrWhiteSpace(card.Plan)) lines.Add(card.Plan!);
            foreach (var row in rows) lines.Add(RowTooltip(row, now));
            var status = card.Status.TextAt(now);
            if (!string.IsNullOrWhiteSpace(status)) lines.Add($"상태: {status}");
            foreach (var text in new[] { card.Notice, card.UsageWarning, card.UsageEmpty, card.Extras, card.Footnote })
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text!);
            return string.Join('\n', lines);
        }

        static string RowTooltip(UsageRow? row, DateTimeOffset now)
        {
            if (row == null) return "한도: 데이터 없음";
            if (row.IsUnlimited) return $"{row.Label}: 무제한";
            if (row.IsUnknown) return $"{row.Label}: 확인 중";
            if (row.IsResetPending) return $"{row.Label}: 확인 중\n리셋: 확인 중";
            var reset = row.ResetTextAt(now);
            return $"{row.Label}: {row.UsedText} 사용 · {QuotaText(row, now)} 남음\n" +
                (string.IsNullOrWhiteSpace(reset) ? "리셋: —" : reset);
        }
    }

    sealed class RowVisual
    {
        public Grid Host { get; }
        readonly TextBlock _label;
        readonly TextBlock _quota;
        readonly TextBlock _reset;
        readonly ProgressBar _bar;

        public RowVisual()
        {
            Host = new Grid { Height = 12, Margin = new Thickness(0, 0, 0, 1) };
            Host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(11) });
            Host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(23) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
            _label = new TextBlock { FontSize = 8, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            _quota = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right };
            _reset = new TextBlock { FontSize = 8, Foreground = MutedBrush, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(_label, 0);
            Grid.SetColumn(_quota, 1);
            Grid.SetColumn(_reset, 2);
            line.Children.Add(_label);
            line.Children.Add(_quota);
            line.Children.Add(_reset);
            Grid.SetRow(line, 0);
            Host.Children.Add(line);
            _bar = new ProgressBar
            {
                Height = 1,
                Minimum = 0,
                Maximum = 100,
                Background = TrackBrush,
                BorderThickness = new Thickness(0),
            };
            Grid.SetRow(_bar, 1);
            Host.Children.Add(_bar);
        }

        public void Update(UsageRow? row, DateTimeOffset now)
        {
            _label.Text = row?.Label.Replace(" 한도", "") ?? "한도";
            _quota.Text = QuotaText(row, now);
            _quota.Foreground = row?.LevelBrush ?? MutedBrush;
            _reset.Text = ResetCountdownText(row, now);
            bool filled = row is { IsUnknown: false } && double.IsFinite(row.Value) && !IsPending(row, now);
            _bar.Visibility = filled ? Visibility.Visible : Visibility.Hidden;
            _bar.Value = filled ? Math.Clamp(row!.Remaining, 0, 100) : 0;
            _bar.Foreground = row?.LevelBrush ?? MutedBrush;
        }
    }

    sealed record PlacementProbe(Rect Taskbar, Rect WorkArea, IReadOnlyList<Rect> Occupied, Rect? Notification);
    sealed record AutomationScan(IReadOnlyList<Rect> Occupied, bool Verified);

    static class Native
    {
        public const uint MonitorDefaultToNearest = 2;
        public const int WmMouseActivate = 0x0021;
        public const int MaNoActivate = 3;

        const int GwlExStyle = -20;
        const long WsExAppWindow = 0x00040000;
        const long WsExNoActivate = 0x08000000;
        const long WsExToolWindow = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        public struct RectI
        {
            public int Left, Top, Right, Bottom;
            public Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public RectI Monitor;
            public RectI Work;
            public uint Flags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfo", SetLastError = true)]
        static extern bool GetMonitorInfoNative(IntPtr monitor, ref MonitorInfo info);

        public static bool GetMonitorInfo(IntPtr monitor, out MonitorInfo info)
        {
            info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfoNative(monitor, ref info);
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        public static void MakeToolWindow(IntPtr hwnd)
        {
            var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        }
    }
}
