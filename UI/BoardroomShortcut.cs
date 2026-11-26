using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AIUsageMonitor.UI;

public enum SpaceAction { None, Boardroom, GrokBot }

public sealed class WindowsSpaceChord
{
    bool _down, _captured;

    public (bool Suppress, SpaceAction Action) Space(bool down, bool windows, bool alt, bool otherModifier)
    {
        if (!down)
        {
            var captured = _captured;
            _down = _captured = false;
            return (captured, SpaceAction.None);
        }
        if (_down) return (_captured, SpaceAction.None);
        _down = true;
        _captured = windows != alt && !otherModifier;
        return (_captured, !_captured ? SpaceAction.None : windows ? SpaceAction.Boardroom : SpaceAction.GrokBot);
    }
}

/// <summary>Handle the Boardroom and Grok Bot shortcuts while the hosted monitor runs.</summary>
internal sealed class BoardroomShortcut : IDisposable
{
    const int LowLevelKeyboard = 13, SpaceKey = 0x20, LeftWindows = 0x5B, RightWindows = 0x5C;
    const int Shift = 0x10, Control = 0x11, Alt = 0x12, Injected = 0x10;
    const int KeyDown = 0x100, SystemKeyDown = 0x104, KeyUp = 0x101, SystemKeyUp = 0x105;
    readonly WindowsSpaceChord _chord = new();
    readonly HookProc _callback;
    readonly Dispatcher _dispatcher;
    readonly Action _toggle;
    readonly Action _openGrokBot;
    IntPtr _hook;

    public BoardroomShortcut(Dispatcher dispatcher, Action toggle, Action openGrokBot)
    {
        _dispatcher = dispatcher;
        _toggle = toggle;
        _openGrokBot = openGrokBot;
        _callback = OnKey;
        _hook = SetWindowsHookEx(LowLevelKeyboard, _callback, GetModuleHandle(null), 0);
    }

    public bool IsActive => _hook != IntPtr.Zero;

    IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        int kind = message.ToInt32();
        if (code < 0 || Marshal.ReadInt32(data) != SpaceKey || (Marshal.ReadInt32(data, 8) & Injected) != 0 ||
            kind is not (KeyDown or SystemKeyDown or KeyUp or SystemKeyUp))
            return CallNextHookEx(_hook, code, message, data);

        bool windows = Held(LeftWindows) || Held(RightWindows);
        bool alt = Held(Alt) || (Marshal.ReadInt32(data, 8) & 0x20) != 0;
        var (suppress, action) = _chord.Space(kind is KeyDown or SystemKeyDown, windows, alt, Held(Shift) || Held(Control));
        if (action == SpaceAction.Boardroom)
        {
            // Mark this Win press as part of a chord so releasing it doesn't open the Start menu.
            // Windows PowerToys uses the same unassigned VK 0xFF release for custom Win chords.
            var input = new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = 0xFF, Flags = 0x0002 } } };
            SendInput(1, ref input, Marshal.SizeOf<Input>());
            _dispatcher.BeginInvoke(DispatcherPriority.Input, _toggle);
        }
        else if (action == SpaceAction.GrokBot)
            _dispatcher.BeginInvoke(DispatcherPriority.Input, _openGrokBot);
        return suppress ? new IntPtr(1) : CallNextHookEx(_hook, code, message, data);
    }

    static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint Type; public InputData Data; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, ref Input input, int size);
}
