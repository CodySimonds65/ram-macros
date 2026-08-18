namespace RamMacros;

public sealed record RecorderWindow(string Role, nint WindowHandle, int ClientWidth, int ClientHeight);

public sealed class MacroRecorder
{
    private readonly object _gate = new();
    private readonly Func<nint> _foregroundWindow;
    private readonly Func<nint, nint, bool> _windowMatches;
    private readonly Func<nint, bool, (int X, int Y, int Width, int Height)> _clientMetrics;
    private readonly Stopwatch _clock = new();
    private readonly List<MacroEvent> _events = [];
    private IReadOnlyDictionary<nint, RecorderWindow> _windows = new Dictionary<nint, RecorderWindow>();

    public MacroRecorder(Func<nint>? foregroundWindow = null,
        Func<nint, bool, (int X, int Y, int Width, int Height)>? clientMetrics = null,
        Func<nint, nint, bool>? windowMatches = null)
    {
        _foregroundWindow = foregroundWindow ?? (() => nint.Zero);
        _clientMetrics = clientMetrics ?? ((_, _) => default);
        _windowMatches = windowMatches ?? ((foreground, target) => foreground == target);
    }

    public void Start(IEnumerable<RecorderWindow> windows)
    {
        lock (_gate)
        {
            _events.Clear();
            _windows = windows.ToDictionary(window => window.WindowHandle);
            _clock.Restart();
        }
    }

    public IReadOnlyList<MacroEvent> Snapshot()
    {
        lock (_gate) return _events.OrderBy(item => item.OffsetMicroseconds).ToArray();
    }

    public bool TryRecord(RecorderWindow window, MacroEvent input, int clientX, int clientY, bool injected, bool multiWindow)
    {
        if (injected) return false;
        lock (_gate)
        {
            if (!_windows.ContainsKey(window.WindowHandle)) return false;
            if (!multiWindow && !_windowMatches(_foregroundWindow(), window.WindowHandle)) return false;
            var metrics = _clientMetrics(window.WindowHandle, false);
            var width = metrics.Width > 0 ? metrics.Width : window.ClientWidth;
            var height = metrics.Height > 0 ? metrics.Height : window.ClientHeight;
            if (clientX < 0 || clientY < 0 || clientX >= width || clientY >= height) return false;
            _events.Add(input with
            {
                OffsetMicroseconds = Math.Max(0, _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency),
                NormalizedX = MacroCoordinateMapper.Normalize(clientX, width),
                NormalizedY = MacroCoordinateMapper.Normalize(clientY, height),
                WindowRole = multiWindow ? window.Role : null
            });
            return true;
        }
    }

    public IReadOnlyList<MacroEvent> Stop() { lock (_gate) { _clock.Stop(); return _events.OrderBy(item => item.OffsetMicroseconds).ToArray(); } }
}
