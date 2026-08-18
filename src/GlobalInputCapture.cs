using System.Runtime.InteropServices;

namespace RamMacros;

public sealed record CapturedInput(MacroEvent Event, nint WindowHandle, int ClientX, int ClientY, bool Injected,
    int ScreenX = 0, int ScreenY = 0);

/// <summary>
/// Captures low-level input without activating or sending input to any window.
/// The callback runs on the WPF dispatcher thread's hook pump.
/// </summary>
public sealed class GlobalInputCapture : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlkhfLowerIlInjected = 0x00000002;
    private const uint LlkhfUp = 0x00000080;
    private const uint LlMhfInjected = 0x00000001;

    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private Action<CapturedInput>? _callback;
    private nint _keyboardHook;
    private nint _mouseHook;
    private nint _ignoredWindow;
    private bool _recordPanelKeyboard;
    private POINT _lastMousePoint;
    private bool _hasLastMousePoint;

    public GlobalInputCapture()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
    }

    public void Start(Action<CapturedInput> callback, nint ignoredWindow, bool recordPanelKeyboard = false)
    {
        if (_keyboardHook != nint.Zero || _mouseHook != nint.Zero)
            throw new InvalidOperationException("Input capture is already running.");
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _ignoredWindow = ignoredWindow;
        _recordPanelKeyboard = recordPanelKeyboard;
        _hasLastMousePoint = false;
        var module = GetModuleHandle(null);
        _keyboardHook = SetKeyboardHook(WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = SetMouseHook(WhMouseLl, _mouseProc, module, 0);
        if (_keyboardHook == nint.Zero || _mouseHook == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Stop();
            throw new InvalidOperationException($"Could not start background input capture (Win32 error {error}).");
        }
    }

    public void Stop()
    {
        if (_keyboardHook != nint.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != nint.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = nint.Zero;
        _mouseHook = nint.Zero;
        _callback = null;
        _ignoredWindow = nint.Zero;
        _recordPanelKeyboard = false;
        _hasLastMousePoint = false;
    }

    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != nint.Zero)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (data.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) != 0;
            var window = GetForegroundWindow();
            if (window != nint.Zero && (_recordPanelKeyboard || window != _ignoredWindow))
            {
                try
                {
                    _callback?.Invoke(new CapturedInput(
                        new MacroEvent
                        {
                            Kind = (data.Flags & LlkhfUp) != 0 ? MacroEventKind.KeyUp : MacroEventKind.KeyDown,
                            VirtualKey = unchecked((int)data.VirtualKeyCode),
                            ScanCode = unchecked((int)data.ScanCode),
                            Extended = (data.Flags & 0x01) != 0
                        },
                        window,
                        0,
                        0,
                        injected));
                }
                catch
                {
                    // A failing handler must never break the input hook chain.
                }
            }
        }
        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != nint.Zero)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var window = GetForegroundWindow();
            if (window != nint.Zero && window != _ignoredWindow && TryMapToClient(window, data.Point, out var clientX, out var clientY))
            {
                var message = unchecked((int)wParam);
                var kind = message switch
                {
                    WmMouseMove => MacroEventKind.MouseMove,
                    WmLButtonDown or WmRButtonDown or WmMButtonDown => MacroEventKind.MouseButtonDown,
                    WmLButtonUp or WmRButtonUp or WmMButtonUp => MacroEventKind.MouseButtonUp,
                    WmMouseWheel => MacroEventKind.MouseWheel,
                    _ => (MacroEventKind?)null
                };
                if (kind is not null)
                {
                    var button = message is WmLButtonDown or WmLButtonUp ? 1 : message is WmRButtonDown or WmRButtonUp ? 2 : message is WmMButtonDown or WmMButtonUp ? 3 : 0;
                    var wheel = message == WmMouseWheel ? unchecked((short)(data.MouseData >> 16)) : 0;
                    var isMove = kind == MacroEventKind.MouseMove;
                    if (!isMove || !_hasLastMousePoint || data.Point.X != _lastMousePoint.X || data.Point.Y != _lastMousePoint.Y)
                    {
                        _lastMousePoint = data.Point;
                        _hasLastMousePoint = true;
                        try
                        {
                            _callback?.Invoke(new CapturedInput(new MacroEvent { Kind = kind.Value, Button = button, WheelDelta = wheel }, window, clientX, clientY, (data.Flags & LlMhfInjected) != 0, data.Point.X, data.Point.Y));
                        }
                        catch
                        {
                            // A failing handler must never break the input hook chain.
                        }
                    }
                }
            }
        }
        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private static bool TryMapToClient(nint window, POINT point, out int clientX, out int clientY)
    {
        var clientPoint = point;
        if (!ScreenToClient(window, ref clientPoint))
        {
            clientX = clientY = 0;
            return false;
        }
        clientX = clientPoint.X;
        clientY = clientPoint.Y;
        return true;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint VirtualKeyCode; public uint ScanCode; public uint Flags; public uint Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT Point; public uint MouseData; public uint Flags; public uint Time; public nint ExtraInfo; }
    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);
    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetKeyboardHook(int idHook, LowLevelKeyboardProc callback, nint moduleHandle, uint threadId);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetMouseHook(int idHook, LowLevelMouseProc callback, nint moduleHandle, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref POINT point);
}
