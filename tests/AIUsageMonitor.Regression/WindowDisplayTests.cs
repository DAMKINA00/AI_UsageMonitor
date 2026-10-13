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
        try
        {
            window.Show();
            PumpDisplay();
            var area = (Rect)typeof(MainWindow).GetMethod("CurrentWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null)!;
            var baseline = new Size(window.Width, window.Height);
            var root = (FrameworkElement)window.Content;
            double scale = ((ScaleTransform)root.LayoutTransform).ScaleX;
            var dashboard = (FrameworkElement)window.FindName("Dashboard");
            double dashboardWidth = dashboard.ActualWidth;
            var host = (Border)window.FindName("AccountDrawerHost");
            foreach (bool onLeft in new[] { true, false })
            {
                window.Left = onLeft ? area.Right - baseline.Width - 12 : area.Left + 12;
                PumpDisplay();
                double mainLeft = window.Left;
                window.SetAccountsOpen(true);
                PumpDisplay(350);
                var mainPosition = dashboard.TranslatePoint(new Point(), window);
                Check("account drawer opens beside the dashboard", window.IsAccountsOpen &&
                    host.Visibility == Visibility.Visible && Grid.GetColumn(host) == (onLeft ? 0 : 2) &&
                    window.Width > baseline.Width && window.Height == baseline.Height &&
                    dashboard.ActualWidth == dashboardWidth && ((ScaleTransform)root.LayoutTransform).ScaleX == scale &&
                    Math.Abs(window.Left + mainPosition.X - mainLeft) <= 1,
                    $"left={onLeft}, column={Grid.GetColumn(host)}, width={window.Width}, dashboard={dashboard.ActualWidth}");
                Check("account management shares the main native window",
                    PresentationSource.FromVisual(window.Accounts) == PresentationSource.FromVisual(window), "separate window");
                window.SetAccountsOpen(false);
                PumpDisplay(350);
                CheckStable("closing the drawer restores main width without rescaling", window, baseline, scale);
                Check("closing preserves dashboard position", Math.Abs(window.Left - mainLeft) <= 1 &&
                    host.Visibility == Visibility.Collapsed, $"left={window.Left}, expected={mainLeft}");
            }

            int claudeAdds = 0, codexAdds = 0, refreshes = 0;
            window.Accounts.AddClaudeRequested += () => claudeAdds++;
            window.Accounts.AddCodexRequested += () => codexAdds++;
            window.Accounts.RefreshRequested += () => refreshes++;
            Button Button(string name) => (Button)window.Accounts.FindName(name);
            window.Accounts.Update([], false);
            foreach (var name in new[] { "AddClaudeButton", "AddCodexButton", "RefreshButton" })
                Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check("embedded panel forwards account and refresh actions", claudeAdds == 1 && codexAdds == 1 && refreshes == 1,
                $"actions={claudeAdds}/{codexAdds}/{refreshes}");
            window.Accounts.Update([], true);
            Check("login in progress disables add buttons", !Button("AddClaudeButton").IsEnabled && !Button("AddCodexButton").IsEnabled,
                "login guard missing");
            window.SetAccountsOpen(true);
            PumpDisplay(350);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key);
            PumpDisplay(350);
            Check("Escape closes the account drawer", !window.IsAccountsOpen && host.Visibility == Visibility.Collapsed && key.Handled,
                "Escape not handled");
            window.SetAccountsOpen(true);
            window.SetAccountsOpen(false);
            window.SetAccountsOpen(true);
            PumpDisplay(350);
            Check("rapid toggles finish in the last requested state", window.IsAccountsOpen && host.Width == 360 &&
                host.Visibility == Visibility.Visible, $"width={host.Width}");
            window.Left += 30;
            PumpDisplay();
            double movedMainLeft = window.Left + dashboard.TranslatePoint(new Point(), window).X;
            Button("CloseButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpDisplay(350);
            CheckStable("rapid toggles do not accumulate window width", window, baseline, scale);
            Check("panel close after moving keeps the moved dashboard position", !window.IsAccountsOpen &&
                Math.Abs(window.Left - movedMainLeft) <= 1, $"left={window.Left}, expected={movedMainLeft}");

            // Simulate a dashboard too wide for an adjacent panel without changing display settings.
            double narrowWidth = area.Width - 24;
            window.Width = narrowWidth;
            dashboard.Width = narrowWidth / scale;
            root.Width = dashboard.Width;
            typeof(MainWindow).GetField("_dashboardWindowWidth", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, narrowWidth);
            window.SetAccountsOpen(true);
            PumpDisplay(350);
            Check("insufficient side space uses an overlay without changing dashboard scale",
                Grid.GetColumn(host) == 1 && window.Width == narrowWidth && host.Width == 360 &&
                ((ScaleTransform)root.LayoutTransform).ScaleX == scale, $"width={window.Width}, column={Grid.GetColumn(host)}");
            window.SetAccountsOpen(false);
            PumpDisplay(350);
            Check("closing overlay keeps the existing window width", window.Width == narrowWidth, window.Width.ToString());
        }
        finally { window.Close(); }
    }
}
