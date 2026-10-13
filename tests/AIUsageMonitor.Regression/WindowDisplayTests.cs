using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        vm.Update(CompactProviderCard("Claude", "TEST"), CompactProviderCard("Codex", "TEST"), "");
        var window = new MainWindow(vm, new AppSettings { ShowCharacters = false, Topmost = false })
        {
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false, AllowClose = true,
        };
        try
        {
            window.Show();
            PumpDisplay();
            CheckFits("initial window fits work area", window);
            var initialSize = new Size(window.Width, window.Height);
            double initialScale = ((ScaleTransform)((FrameworkElement)window.Content).LayoutTransform).ScaleX;
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
                CheckStable("monitor movement preserves size and content scale", window, initialSize, initialScale);
            }

            foreach (var message in new[] { 0x007E, 0x001A })
            {
                var parameter = message == 0x001A ? new IntPtr(0x002F) : IntPtr.Zero;
                for (int i = 0; i < 5; i++) SendMessage(hwnd, message, parameter, IntPtr.Zero);
                PumpDisplay();
                CheckStable("display notifications preserve size and content scale", window, initialSize, initialScale);
            }

            // Exercise the DPI callback without changing the user's Windows display settings.
            var dpi = VisualTreeHelper.GetDpi(window);
            typeof(MainWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [dpi, dpi]);
            PumpDisplay();
            CheckStable("DPI callback does not change app zoom", window, initialSize, initialScale);

            window.WindowState = WindowState.Minimized;
            PumpDisplay();
            SendMessage(hwnd, 0x007E, IntPtr.Zero, IntPtr.Zero);
            PumpDisplay();
            window.WindowState = WindowState.Normal;
            PumpDisplay();
            CheckStable("restore preserves size and content scale", window, initialSize, initialScale);
            window.Hide();
            window.Show();
            PumpDisplay();
            CheckStable("show preserves size and content scale", window, initialSize, initialScale);
            window.ApplyCharacters(true);
            PumpDisplay();
            CheckStable("character toggle preserves size and content scale", window, initialSize, initialScale);
        }
        finally { window.Close(); }
        var settingsAfter = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        Check("monitor movement does not write user settings",
            settingsBefore == null ? settingsAfter == null : settingsAfter != null && settingsBefore.SequenceEqual(settingsAfter),
            "settings file changed");
    }

    static void CheckStable(string name, MainWindow window, Size size, double scale) =>
        Check(name, window.Width == size.Width && window.Height == size.Height &&
            ((ScaleTransform)((FrameworkElement)window.Content).LayoutTransform).ScaleX == scale,
            $"size={window.Width}/{window.Height}, expected={size}, scale={scale}");

    static void CheckFits(string name, MainWindow window)
    {
        var area = (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        double expectedHeight = Math.Ceiling(Math.Min(860 * 1.5, area.Height - 24));
        Check(name, window.Left >= area.Left + 11 && window.Top >= area.Top + 11 &&
            window.Left + window.Width <= area.Right - 11 && window.Top + window.Height <= area.Bottom - 11 &&
            Math.Abs(window.Height - expectedHeight) <= 1,
            $"window={window.Left}/{window.Top}/{window.Width}/{window.Height}, area={area}");
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
        vm.Update(CompactProviderCard("Claude", "TEST"), CompactProviderCard("Codex", "TEST"), "");
        var window = new MainWindow(vm, new AppSettings { ShowCharacters = false, Topmost = false })
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
            double scale = ((ScaleTransform)root.LayoutTransform).ScaleX;
            int geometryChanges = 0;
            window.LocationChanged += (_, _) => geometryChanges++;
            window.SizeChanged += (_, _) => geometryChanges++;
            foreach (bool onLeft in new[] { true, false })
            {
                window.Left = onLeft ? area.Right - baseline.Width - 12 : area.Left + 12;
                PumpDisplay();
                var position = new Point(window.Left, window.Top);
                geometryChanges = 0;
                window.SetAccountsOpen(true);
                PumpDisplay();
                flyout = window.OwnedWindows.OfType<AccountsFlyout>().Single();
                Check("floating panel opens on the available side", window.IsAccountsOpen && flyout.IsVisible &&
                    (onLeft ? flyout.Left + flyout.Width < window.Left : flyout.Left > window.Left + window.Width),
                    $"left={onLeft}, panel={flyout.Left}/{flyout.Width}, main={window.Left}/{window.Width}");
                Check("account panel uses an owned independent native window", !flyout.ShowInTaskbar &&
                    PresentationSource.FromVisual(window.Accounts) != PresentationSource.FromVisual(window), "shared HWND or taskbar entry");
                window.SetAccountsOpen(false);
                PumpDisplay();
                CheckStable("open and close preserve main size and scale", window, baseline, scale);
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
            Check("floating panel follows owner movement", Math.Abs(flyout.Left - panelLeft - 30) <= 1, flyout.Left.ToString());
            double movedMainLeft = window.Left;
            Button("CloseButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpDisplay();
            CheckStable("rapid toggles do not accumulate main window width", window, baseline, scale);
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

            var accounts = Enumerable.Range(0, 6).Select(i => new AccountUsageCard
            {
                Id = i.ToString(), ProviderName = (i < 3 ? "Claude" : "Codex") + (i % 3 == 0 ? " · 주 계정" : " · 서브 계정"),
                DisplayName = "ACCOUNT_" + i, Plan = "Max 20x", Status = "연결됨 · 화면 검증용 데이터",
                IsSecondary = i % 3 != 0, CanLogin = true, LoginText = "다시 로그인",
                Usage = [new UsageRow { Label = i == 5 ? "5시간 한도" : "주간 한도", Value = 25,
                    UsedText = "25%", RemainText = "사용 · 75% 남음", ResetText = "리셋까지 5일 12시간 · 10-19(월) 08:09", LevelBrush = Palette.Ok }],
            }).ToList();
            window.Accounts.Update(accounts, false);
            PumpDisplay();
            var scroll = (ScrollViewer)window.Accounts.FindName("AccountsScroll");
            var items = (ItemsControl)window.Accounts.FindName("AccountItems");
            var last = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(5);
            Check("all six compact account cards fit without scrolling", scroll.ScrollableHeight == 0 &&
                last.TransformToAncestor(scroll).Transform(new Point(0, last.ActualHeight)).Y <= scroll.ActualHeight + 1,
                $"extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, overflow={scroll.ScrollableHeight}");

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
}
