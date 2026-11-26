using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AIUsageMonitor.Core;

namespace AIUsageMonitor.UI;

internal sealed class GrokBotShortcut
{
    const int RootOwner = 3, Restore = 9;
    const uint Close = 0x0010;
    bool _opening;

    public async void Toggle()
    {
        if (_opening) return;
        try
        {
            var window = FindWindow();
            if (window != 0 && GetAncestor(GetForegroundWindow(), RootOwner) == window)
            {
                PostMessage(window, Close, 0, 0);
                return;
            }

            _opening = true;
            if (window == 0)
            {
                try { Open("grokbot://app/v1/prompt?text="); }
                catch (Win32Exception) { Open("https://x.ai/bot#download"); return; }
                for (int attempt = 0; attempt < 48 && window == 0; attempt++)
                {
                    await Task.Delay(125);
                    window = FindWindow();
                }
            }
            if (window == 0) return;
            if (IsIconic(window))
            {
                ShowWindowAsync(window, Restore);
                await Task.Delay(40);
            }
            Activate(window);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Log.Write($"Grok Bot could not be opened: {error.Message}");
        }
        finally { _opening = false; }
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
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint other, bool attach);
    [DllImport("user32.dll")] static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
