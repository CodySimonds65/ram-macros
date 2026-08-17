using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RamMacros;

/// <summary>
/// The only windows that recording may accept. The host is authoritative; a
/// foreground window is never treated as a Roblox target merely because it is
/// active.
/// </summary>
public sealed class ManagedAccountRegistry
{
    private readonly object _gate = new();
    private IReadOnlyList<ManagedAccountSnapshot> _accounts = [];

    public void Replace(IReadOnlyList<ManagedAccountSnapshot> accounts)
    {
        lock (_gate) _accounts = accounts.Where(IsUsable).ToArray();
    }

    public IReadOnlyList<ManagedAccountSnapshot> Snapshot()
    {
        lock (_gate) return _accounts.ToArray();
    }

    public bool TryResolve(nint foregroundWindow, out ManagedAccountSnapshot account)
    {
        lock (_gate)
        {
            account = _accounts.FirstOrDefault(candidate =>
                IsUsable(candidate) && IsCurrentIdentity(candidate) && SameWindowTree(foregroundWindow, candidate.WindowHandle))!;
            return account is not null;
        }
    }

    private static bool IsUsable(ManagedAccountSnapshot account) =>
        account.IsRunning && account.WindowHandle != nint.Zero && account.ProcessId > 0 &&
        account.ProcessStartTimeUtcTicks > 0 && account.ClientWidth > 0 && account.ClientHeight > 0;

    private static bool SameWindowTree(nint left, nint right)
    {
        if (left == nint.Zero || right == nint.Zero) return false;
        if (left == right) return true;
        var leftRoot = GetAncestor(left, GaRoot);
        var rightRoot = GetAncestor(right, GaRoot);
        return leftRoot != nint.Zero && leftRoot == rightRoot;
    }

    private static bool IsCurrentIdentity(ManagedAccountSnapshot account)
    {
        if (!GetWindowThreadProcessId(account.WindowHandle, out var processId) || processId != account.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == account.ProcessStartTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private const uint GaRoot = 2;
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowThreadProcessId(nint hwnd, out int processId);
}

public sealed record ManagedAccountSnapshot(
    string AccountId,
    string Label,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    nint WindowHandle,
    int ClientX,
    int ClientY,
    int ClientWidth,
    int ClientHeight,
    uint Dpi,
    bool IsMinimized,
    DateTime LastActivityUtc,
    bool IsRunning);
