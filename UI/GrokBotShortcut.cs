using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AIUsageMonitor.Core;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.UI;

internal sealed class GrokBotShortcut : IDisposable
{
    const int RootOwner = 3;
    const int Hide = 0, Show = 5, ShowWithoutActivating = 8, Restore = 9;
    readonly DispatcherTimer _trayWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    WinForms.NotifyIcon? _tray;
    Icon? _trayImage;
    nint _hiddenWindow;
    uint _hiddenProcess;
    bool _opening, _disposed;

    public GrokBotShortcut()
    {
        _trayWatch.Tick += (_, _) =>
        {
            if (!HiddenWindowIsAvailable() || IsWindowVisible(_hiddenWindow)) ClearTray();
        };
    }

    public void Toggle() => ChangeWindow(allowHide: true);

    async void ChangeWindow(bool allowHide)
    {
        if (_opening || _disposed) return;
        try
        {
            if (_hiddenWindow != 0 && !HiddenWindowIsAvailable()) ClearTray();
            var window = _hiddenWindow != 0 ? _hiddenWindow : FindWindow();
            if (allowHide && _hiddenWindow == 0 && window != 0 && GetAncestor(GetForegroundWindow(), RootOwner) == window)
            {
                HideToTray(window);
                return;
            }

            _opening = true;
            if (window == 0)
            {
                try { Open("grokbot://app/v1/prompt?text="); }
                catch (Win32Exception) { Open("https://x.ai/bot#download"); return; }
                for (int attempt = 0; attempt < 48 && window == 0 && !_disposed; attempt++)
                {
                    await Task.Delay(125);
                    window = FindWindow();
                }
            }
            if (window == 0 || _disposed) return;
            if (!IsWindowVisible(window) || IsIconic(window))
            {
                if (!ShowWindowAsync(window, IsIconic(window) ? Restore : Show)) return;
                for (int attempt = 0; attempt < 12 && !_disposed && (!IsWindowVisible(window) || IsIconic(window)); attempt++)
                    await Task.Delay(40);
            }
            if (_disposed || !IsWindowVisible(window) || IsIconic(window)) return;
            if (window == _hiddenWindow) ClearTray();
            Activate(window);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            if (_hiddenWindow == 0) ClearTray();
            Log.Write($"Grok Bot window could not be shown or hidden: {error.Message}");
        }
        finally { _opening = false; }
    }

    void HideToTray(nint window)
    {
        if (GetWindowThreadProcessId(window, out var pid) == 0) return;
        _trayImage = ReadIcon(pid);
        _tray = new WinForms.NotifyIcon { Icon = _trayImage ?? SystemIcons.Application, Text = "Grok Bot · 클릭하여 열기" };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ChangeWindow(allowHide: false); };
        _tray.Visible = true;
        if (!ShowWindowAsync(window, Hide)) { ClearTray(); return; }
        _hiddenWindow = window;
        _hiddenProcess = pid;
        _trayWatch.Start();
    }

    static Icon? ReadIcon(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName is { } path ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch (Exception error) when (error is ArgumentException or Win32Exception or InvalidOperationException) { return null; }
    }

    bool HiddenWindowIsAvailable() => _hiddenWindow != 0
        && GetWindowThreadProcessId(_hiddenWindow, out var pid) != 0 && pid == _hiddenProcess;

    void ClearTray()
    {
        _trayWatch.Stop();
        _tray?.Dispose();
        _trayImage?.Dispose();
        _tray = null;
        _trayImage = null;
        _hiddenWindow = 0;
        _hiddenProcess = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (HiddenWindowIsAvailable()) ShowWindowAsync(_hiddenWindow, ShowWithoutActivating);
        ClearTray();
    }

    static void Open(string address) => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();

    static nint FindWindow()
    {
        var processes = Process.GetProcessesByName("Grok Bot");
        try
        {
            var foreground = GetAncestor(GetForegroundWindow(), RootOwner);
            nint candidate = 0;
            foreach (var process in processes)
            {
                nint window;
                try { window = process.MainWindowHandle; }
                catch (InvalidOperationException) { continue; }
                if (window == 0) continue;
                if (window == foreground) return window;
                if (candidate == 0 || IsIconic(candidate)) candidate = window;
            }
            return candidate;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static void Activate(nint window)
    {
        if (SetForegroundWindow(window)) return;
        // A global hook can receive the shortcut while another thread still owns keyboard input.
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var thread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == thread || !AttachThreadInput(thread, foregroundThread, true)) return;
        try { SetForegroundWindow(window); }
        finally { AttachThreadInput(thread, foregroundThread, false); }
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint other, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
