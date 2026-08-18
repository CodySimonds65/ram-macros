using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RamMacros;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MacroDefinition> _macros = [];
    private readonly MacroRecorder _recorder;
    private readonly GlobalInputCapture _inputCapture = new();
    private readonly ManagedAccountRegistry _managedAccounts;
    private MacroDefinition? _selected;
    private bool _recording;
    private nint _recordingWindow;
    private nint _windowHandle;
    private int _eventRefreshPending;

    public MainWindow(ManagedAccountRegistry? managedAccounts = null)
    {
        InitializeComponent();
        _managedAccounts = managedAccounts ?? new ManagedAccountRegistry();
        MacroList.ItemsSource = _macros;
        WindowAppearance.Apply(this);
        _recorder = new MacroRecorder(
            NativeWindowMetrics.GetForegroundWindow,
            (window, _) =>
            {
                var metrics = NativeWindowMetrics.GetClientMetrics(window);
                return (0, 0, metrics.Width, metrics.Height);
            },
            NativeWindowMetrics.IsSameWindowTree);
        SourceInitialized += (_, _) => _windowHandle = new WindowInteropHelper(this).Handle;
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        var macro = new MacroDefinition { Name = $"Macro {_macros.Count + 1}" };
        _macros.Add(macro); MacroList.SelectedItem = macro;
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "RAM macro bundle (*.ramacro)|*.ramacro" };
        if (dialog.ShowDialog(this) != true) return;
        try { var bundle = MacroStore.ImportAsync(dialog.FileName).GetAwaiter().GetResult(); foreach (var macro in bundle.Macros) _macros.Add(macro); StatusText.Text = $"Imported {bundle.Macros.Count} macro(s)."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => MacroList.ItemsSource = _macros.Where(m => string.IsNullOrWhiteSpace(SearchBox.Text) || m.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    private void MacroList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = MacroList.SelectedItem as MacroDefinition;
        RefreshEventList();
    }
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            StatusText.Text = "Create or select a macro first.";
            return;
        }
        if (_recording)
        {
            StopRecording();
            return;
        }

        var foreground = NativeWindowMetrics.GetForegroundWindow();
        if (_managedAccounts.Snapshot().Count == 0 && foreground != _windowHandle)
        {
            StatusText.Text = "No running managed Roblox windows are available. Start an account and try again.";
            return;
        }

        _recording = true;
        _recordingWindow = nint.Zero;
        _recorder.Start([]);
        try
        {
            _inputCapture.Start(HandleCapturedInput, _windowHandle);
        }
        catch (Exception ex)
        {
            _recorder.Stop();
            _recording = false;
            _recordingWindow = nint.Zero;
            StatusText.Text = $"Could not start recording.\n{ex.Message}";
            return;
        }
        RecordButton.Content = "■  Stop recording";
        FooterText.Text = "Recording background input. The panel stays visible; its own input is ignored.";
        StatusText.Text = foreground == _windowHandle
            ? "Recording armed. Activate a managed Roblox window; events will appear here."
            : "Recording managed window input...\nTarget will bind to the active Roblox client.";
    }

    private void HandleCapturedInput(CapturedInput captured)
    {
        if (!_recording || captured.WindowHandle == _windowHandle) return;
        // Filter before binding a target. Injected input must never be able to
        // select a window or enter the recorded sequence.
        if (captured.Injected) return;
        if (!_managedAccounts.TryResolve(captured.WindowHandle, out var account)) return;
        var targetWindow = account.WindowHandle;
        var clientX = captured.ClientX;
        var clientY = captured.ClientY;
        if (captured.Event.Kind is MacroEventKind.MouseMove or MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp or MacroEventKind.MouseWheel)
        {
            if (!NativeWindowMetrics.TryScreenToClient(targetWindow, captured.ScreenX, captured.ScreenY, out clientX, out clientY)) return;
        }
        if (_recordingWindow == nint.Zero)
        {
            _recordingWindow = targetWindow;
            var metrics = NativeWindowMetrics.GetClientMetrics(_recordingWindow);
            _recorder.Start([new RecorderWindow("default", _recordingWindow, metrics.Width, metrics.Height)]);
            QueueUi(() => StatusText.Text = $"Recording {account.Label} input...\nTarget bound to the managed window.");
        }

        var metricsNow = NativeWindowMetrics.GetClientMetrics(targetWindow);
        var recorderWindow = new RecorderWindow("default", targetWindow, metricsNow.Width, metricsNow.Height);
        if (_recorder.TryRecord(recorderWindow, captured.Event, clientX, clientY, captured.Injected, multiWindow: false))
        {
            QueueEventListRefresh();
        }
    }

    private void QueueEventListRefresh()
    {
        if (Interlocked.Exchange(ref _eventRefreshPending, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _eventRefreshPending, 0);
            RefreshEventList();
        }));
    }

    private void QueueUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void StopRecording()
    {
        _inputCapture.Stop();
        var events = _recorder.Stop();
        _recording = false;
        RecordButton.Content = "●  Record";
        FooterText.Text = "Background-safe mode: no focus APIs are used.";
        if (_selected is not null)
        {
            var updated = _selected with
            {
                Events = events,
                RecordedClientWidth = NativeWindowMetrics.GetClientMetrics(_recordingWindow).Width,
                RecordedClientHeight = NativeWindowMetrics.GetClientMetrics(_recordingWindow).Height
            };
            var index = _macros.IndexOf(_selected);
            if (index >= 0) _macros[index] = updated;
            _selected = updated;
            MacroList.SelectedItem = updated;
        }
        RefreshEventList();
        StatusText.Text = $"Recording stopped.\n{events.Count} event(s) captured.";
    }

    private void RefreshEventList()
    {
        var events = _recording ? _recorder.Snapshot() : _selected?.Events ?? [];
        EventList.ItemsSource = events.Select(item => $"{item.OffsetMicroseconds / 1000.0:0.0} ms  {item.Kind}").ToArray();
        StatusText.Text = _selected is null ? "Select a macro to begin." : $"{_selected.Name}\n{events.Count} event(s)\nPortable normalized coordinates.";
    }
    private void Play_Click(object sender, RoutedEventArgs e) => StatusText.Text = _selected is null ? "Select a macro first." : "Playback requires managed account targets from the host.";
    private void Stack_Click(object sender, RoutedEventArgs e) => StatusText.Text = "STACK requested with SWP_NOACTIVATE.";
    private void Grid_Click(object sender, RoutedEventArgs e) => StatusText.Text = "GRID requested with SWP_NOACTIVATE.";
    private void Reset_Click(object sender, RoutedEventArgs e) => StatusText.Text = "RESET requested with SWP_NOACTIVATE.";
    protected override void OnClosed(EventArgs e)
    {
        _inputCapture.Dispose();
        base.OnClosed(e);
    }
}

internal static class NativeWindowMetrics
{
    public static nint GetForegroundWindow() => GetForegroundWindowNative();

    public static (int Width, int Height) GetClientMetrics(nint window)
    {
        if (window == nint.Zero || !GetClientRect(window, out var rect)) return (0, 0);
        return (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    public static bool IsSameWindowTree(nint left, nint right)
    {
        if (left == nint.Zero || right == nint.Zero) return false;
        if (left == right) return true;
        var leftRoot = GetAncestor(left, GaRoot);
        var rightRoot = GetAncestor(right, GaRoot);
        return leftRoot != nint.Zero && leftRoot == rightRoot;
    }

    public static bool TryScreenToClient(nint window, int screenX, int screenY, out int clientX, out int clientY)
    {
        var point = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(window, ref point))
        {
            clientX = clientY = 0;
            return false;
        }
        clientX = point.X;
        clientY = point.Y;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint GetForegroundWindowNative();
    private const uint GaRoot = 2;
}

internal static class WindowAppearance
{
    public static void Apply(Window window) { window.SourceInitialized += (_, _) => { }; }
}
