using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIUsageMonitor.Core;
using AIUsageMonitor.UI;

namespace AIUsageMonitor.Regression;

internal static partial class Program
{
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    static void TestWindowDisplay()
    {
        var settingsBefore = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        var vm = new MainViewModel();
        UpdateViewModel(vm, CompactProviderCard("Claude", "TEST"), CompactProviderCard("Codex", "TEST"),
            CompactProviderCard("Gemini", "TEST"), CompactProviderCard("Grok", "TEST"), "");
        var window = new MainWindow(vm, new AppSettings { ShowCharacters = false, Topmost = false }, _ => { })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        try
        {
            window.Show();
            PumpDisplay();
            CheckFits("initial window fits work area", window);
            var hwnd = new WindowInteropHelper(window).Handle;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice;
                var position = transform.Transform(new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40));
                window.Left = position.X;
                window.Top = position.Y;
                PumpDisplay();
                Check("window movement selects the destination monitor",
                    System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName == screen.DeviceName, screen.DeviceName);
                CheckDocked("monitor movement re-docks without content zoom", window);
            }

            foreach (var message in new[] { 0x007E, 0x001A })
            {
                var parameter = message == 0x001A ? new IntPtr(0x002F) : IntPtr.Zero;
                for (int i = 0; i < 5; i++) SendMessage(hwnd, message, parameter, IntPtr.Zero);
                PumpDisplay();
                CheckDocked("display notifications re-dock without content zoom", window);
            }

            // Exercise the DPI callback without changing the user's Windows display settings.
            var dpi = VisualTreeHelper.GetDpi(window);
            typeof(MainWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [dpi, dpi]);
            PumpDisplay();
            CheckDocked("DPI callback does not change app zoom", window);

            window.WindowState = WindowState.Minimized;
            PumpDisplay();
            SendMessage(hwnd, 0x007E, IntPtr.Zero, IntPtr.Zero);
            PumpDisplay();
            window.WindowState = WindowState.Normal;
            PumpDisplay();
            CheckDocked("restore re-docks without content zoom", window);
            window.Hide();
            window.Show();
            PumpDisplay();
            CheckDocked("show re-docks without content zoom", window);
            window.ApplyCharacters(true);
            PumpDisplay();
            CheckDocked("character toggle preserves docked size without content zoom", window);
        }
        finally { window.Close(); }
        var settingsAfter = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        Check("monitor movement does not write user settings",
            settingsBefore == null ? settingsAfter == null : settingsAfter != null && settingsBefore.SequenceEqual(settingsAfter),
            "settings file changed");
    }

    /// <summary>앱이 스스로 창 크기를 바꾸지 않았고 콘텐츠 배율도 건드리지 않았는지 확인한다.</summary>
    static void CheckStable(string name, MainWindow window, Size size) =>
        Check(name, window.Width == size.Width && window.Height == size.Height &&
            ((FrameworkElement)window.Content).LayoutTransform.Value.IsIdentity,
            $"size={window.Width}/{window.Height}, expected={size}");

    static void CheckDocked(string name, MainWindow window)
    {
        var area = (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        var expected = MainWindow.DockBounds(area, 4);
        Check(name, Math.Abs(window.Width - expected.Width) < 1 && Math.Abs(window.Height - expected.Height) < 1 &&
            Math.Abs(window.Left - expected.Left) < 1 && Math.Abs(window.Top - expected.Top) < 1 &&
            ((FrameworkElement)window.Content).LayoutTransform.Value.IsIdentity,
            $"window={window.Left}/{window.Top}/{window.Width}/{window.Height}, expected={expected}");
    }

    /// <summary>기본 스테이지가 작업 영역 오른쪽 아래에 투명하게 놓이는지 확인한다.</summary>
    static void CheckFits(string name, MainWindow window)
    {
        var area = (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        var root = (FrameworkElement)window.Content;
        var expected = MainWindow.DockBounds(area, 4);
        Check(name, window.Left >= area.Left - 0.5 && window.Top >= area.Top - 0.5 &&
            window.Left + window.Width <= area.Right + 0.5 && window.Top + window.Height <= area.Bottom + 0.5 &&
            root.LayoutTransform.Value.IsIdentity && window.ResizeMode == ResizeMode.NoResize && window.AllowsTransparency &&
            window.Width == MainWindow.DefaultWidth && window.Height == MainWindow.DefaultHeight &&
            window.MinWidth == MainWindow.MinWindowWidth && window.MinHeight == MainWindow.MinWindowHeight &&
            Math.Abs(window.Left - expected.Left) < 1 && Math.Abs(window.Top - expected.Top) < 1,
            $"window={window.Left}/{window.Top}/{window.Width}/{window.Height}, expected={expected}, area={area}");
    }

    static void PumpDisplay(int milliseconds = 150)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void TestAccountsPanel()
    {
        var vm = new MainViewModel();
        UpdateViewModel(vm, CompactProviderCard("Claude", "TEST"), CompactProviderCard("Codex", "TEST"),
            CompactProviderCard("Gemini", "TEST"), CompactProviderCard("Grok", "TEST"), "");
        var window = new MainWindow(vm, new AppSettings { ShowCharacters = false, Topmost = false }, _ => { })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        AccountsFlyout? flyout = null;
        try
        {
            window.Show();
            PumpDisplay();
            var area = (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null)!;
            var baseline = new Size(window.Width, window.Height);
            var root = (FrameworkElement)window.Content;
            int geometryChanges = 0;
            window.LocationChanged += (_, _) => geometryChanges++;
            window.SizeChanged += (_, _) => geometryChanges++;
            foreach (bool onLeft in new[] { true, false })
            {
                window.Left = onLeft ? area.Right - baseline.Width - 12 : area.Left + 12;
                PumpDisplay();
                var position = new Point(window.Left, window.Top);
                bool expectLeft = position.X - area.Left >= area.Right - position.X - window.Width;
                geometryChanges = 0;
                window.SetAccountsOpen(true);
                PumpDisplay();
                flyout = window.OwnedWindows.OfType<AccountsFlyout>().Single();
                Check("floating panel opens on the available side", window.IsAccountsOpen && flyout.IsVisible &&
                    (expectLeft ? flyout.Left + flyout.Width < window.Left : flyout.Left > window.Left + window.Width),
                    $"left={expectLeft}, panel={flyout.Left}/{flyout.Width}, main={window.Left}/{window.Width}");
                Check("account panel uses an owned independent native window", !flyout.ShowInTaskbar &&
                    PresentationSource.FromVisual(window.Accounts) != PresentationSource.FromVisual(window), "shared HWND or taskbar entry");
                window.SetAccountsOpen(false);
                PumpDisplay();
                CheckStable("open and close preserve main size", window, baseline);
                Check("open and close never move or resize the owner", geometryChanges == 0 &&
                    new Point(window.Left, window.Top) == position && !flyout.IsVisible,
                    $"geometry events={geometryChanges}");
            }

            int claudeAdds = 0, codexAdds = 0, refreshes = 0;
            window.Accounts.AddClaudeRequested += () => claudeAdds++;
            window.Accounts.AddCodexRequested += () => codexAdds++;
            window.Accounts.RefreshRequested += () => refreshes++;
            Button Button(string name) => (Button)window.Accounts.FindName(name);
            window.Accounts.Update([], false);
            foreach (var name in new[] { "AddClaudeButton", "AddCodexButton", "RefreshButton" })
                Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check("floating panel forwards account and refresh actions", claudeAdds == 1 && codexAdds == 1 && refreshes == 1,
                $"actions={claudeAdds}/{codexAdds}/{refreshes}");
            window.Accounts.Update([], true);
            Check("login in progress disables add buttons", !Button("AddClaudeButton").IsEnabled && !Button("AddCodexButton").IsEnabled,
                "login guard missing");

            foreach (var target in new Window[] { window, flyout! })
            {
                window.SetAccountsOpen(true);
                PumpDisplay();
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(key);
                Check("Escape in either window closes the floating panel", !window.IsAccountsOpen && !flyout!.IsVisible && key.Handled,
                    target.GetType().Name);
            }
            window.SetAccountsOpen(true);
            window.SetAccountsOpen(false);
            window.SetAccountsOpen(true);
            PumpDisplay();
            Check("rapid toggles reuse a single floating panel", window.IsAccountsOpen && flyout!.IsVisible &&
                window.OwnedWindows.Count == 1, $"owned windows={window.OwnedWindows.Count}");
            double panelLeft = flyout!.Left;
            window.Left += 30;
            PumpDisplay();
            Check("floating panel stays attached when the owner re-docks", Math.Abs(flyout.Left - panelLeft) <= 1, flyout.Left.ToString());
            double movedMainLeft = window.Left;
            Button("CloseButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpDisplay();
            CheckStable("rapid toggles do not accumulate main window width", window, baseline);
            Check("closing after movement preserves owner position", !window.IsAccountsOpen && window.Left == movedMainLeft,
                window.Left.ToString());

            window.SetAccountsOpen(true);
            var positionBefore = new Point(window.Left, window.Top);
            var narrowArea = new Rect(window.Left, window.Top, 420, 920);
            flyout.PositionBeside(window, narrowArea);
            Check("limited side space only repositions the floating panel", window.Width == baseline.Width &&
                new Point(window.Left, window.Top) == positionBefore && flyout.Left >= narrowArea.Left &&
                flyout.Left + flyout.Width <= narrowArea.Right, $"panel={flyout.Left}/{flyout.Width}");
            flyout.PositionBeside(window, area);

            var usageNow = DateTimeOffset.Now;
            var accounts = Enumerable.Range(0, 6).Select(i => new AccountUsageCard
            {
                Id = i.ToString(), ProviderName = (i < 3 ? "Claude" : "Codex") + (i % 3 == 0 ? " · 주 계정" : " · 서브 계정"),
                DisplayName = "ACCOUNT_" + i, Plan = "Max 20x", Status = "연결됨 · 화면 검증용 데이터",
                IsSecondary = i % 3 != 0, CanLogin = true, LoginText = "다시 로그인",
                Usage = Presenter.AccountUsageRows([
                    new UsageWindow($"account{i}:w300", "5시간 한도", 25, usageNow.AddHours(5)),
                    new UsageWindow($"account{i}:w10080", "주간 한도", 25, usageNow.AddDays(5)),
                ], asOf: usageNow),
            }).ToList();
            window.Accounts.Update(accounts, false);
            PumpDisplay();
            var scroll = (ScrollViewer)window.Accounts.FindName("AccountsScroll");
            var items = (ItemsControl)window.Accounts.FindName("AccountItems");
            var containers = Enumerable.Range(0, 6)
                .Select(i => (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
            var cardHeights = containers.Select(c => Descendants(c).OfType<Border>().First(b => b.MinHeight >= 170).ActualHeight).ToList();
            var last = containers[^1];
            var lastBottom = last.TransformToAncestor(scroll).Transform(new Point(0, last.ActualHeight)).Y;
            Check("six account cards keep equal fixed heights and require scrolling", cardHeights.Count == 6 &&
                cardHeights.Max() - cardHeights.Min() <= 1 && scroll.ScrollableHeight > 0 &&
                lastBottom > scroll.ActualHeight + 1,
                $"heights={string.Join(",", cardHeights.Select(x => x.ToString("0.#")))}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, overflow={scroll.ScrollableHeight}");
            RenderAccountsFixture(flyout, "accounts-two-rows");

            window.Hide();
            Check("hiding owner hides the account panel", !window.IsAccountsOpen && !flyout.IsVisible, "orphan panel");
            window.Show();
            window.SetAccountsOpen(true);
            window.WindowState = WindowState.Minimized;
            PumpDisplay();
            Check("minimizing owner dismisses the account panel", !window.IsAccountsOpen && !flyout.IsVisible, "panel stayed open");
            window.WindowState = WindowState.Normal;
            window.SetAccountsOpen(true);
            flyout.Close();
            Check("native panel close preserves reusable accounts", !window.IsAccountsOpen && !flyout.IsVisible, "panel state stale");
            window.SetAccountsOpen(true);
            Check("closed floating panel reopens with account data", flyout.IsVisible && items.Items.Count == 6, "data lost");
        }
        finally { window.Close(); }
        Check("closing owner cleans up floating panel", flyout == null || !flyout.IsVisible, "orphan window");
    }

    static void RenderAccountsFixture(AccountsFlyout flyout, string name)
    {
        if (flyout.Content is not FrameworkElement root) return;
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var path = Path.Combine(Path.GetTempPath(), $"AIUsageMonitor-Regression-{name}-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        Check("two-row account panel fixture renders to PNG", File.Exists(path) && new FileInfo(path).Length > 0,
            $"path={path}, size={width}x{height}");
        Console.WriteLine($"INFO rendered {name} fixture: {path}");
    }
}
