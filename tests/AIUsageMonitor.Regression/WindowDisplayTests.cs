using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
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
                CheckFits("moved window refits without title bar dragging", window);
            }

            foreach (var message in new[] { 0x007E, 0x001A })
            {
                window.Width = 1200;
                window.Height = 2000;
                var parameter = message == 0x001A ? new IntPtr(0x002F) : IntPtr.Zero;
                for (int i = 0; i < 5; i++) SendMessage(hwnd, message, parameter, IntPtr.Zero);
                Check("display notifications queue a refit", DisplayFitQueued(window), message.ToString("X"));
                PumpDisplay();
                CheckFits("display or work area notification repairs window size", window);
                Check("display notifications finish without a refit loop", !DisplayFitQueued(window), message.ToString("X"));
            }

            // Exercise the DPI callback without changing the user's Windows display settings.
            var dpi = VisualTreeHelper.GetDpi(window);
            typeof(MainWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [dpi, dpi]);
            Check("DPI callback queues a refit", DisplayFitQueued(window), "no queued fit");
            PumpDisplay();

            window.WindowState = WindowState.Minimized;
            PumpDisplay();
            SendMessage(hwnd, 0x007E, IntPtr.Zero, IntPtr.Zero);
            PumpDisplay();
            window.WindowState = WindowState.Normal;
            PumpDisplay();
            CheckFits("restoring a minimized window refits the display", window);
            window.Hide();
            window.Width = 1200;
            window.Height = 2000;
            window.Show();
            PumpDisplay();
            CheckFits("showing a hidden window refits the display", window);
        }
        finally { window.Close(); }
        var settingsAfter = File.Exists(Paths.SettingsFile) ? File.ReadAllBytes(Paths.SettingsFile) : null;
        Check("automatic display fitting does not write user settings",
            settingsBefore == null ? settingsAfter == null : settingsAfter != null && settingsBefore.SequenceEqual(settingsAfter),
            "settings file changed");
    }

    static bool DisplayFitQueued(MainWindow window) => (bool)typeof(MainWindow)
        .GetField("_displayFitQueued", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

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

    static void PumpDisplay()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
