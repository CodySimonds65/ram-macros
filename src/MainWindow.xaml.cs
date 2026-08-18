using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private readonly DiagnosticsLog _diagnostics;
    private MacroDefinition? _selected;
    private bool _recording;
    private nint _recordingWindow;
    private nint _windowHandle;
    private int _eventRefreshPending;
    private int _capturedInputCount;
    private int _ignoredInjectedCount;
    private int _rejectedEventCount;
    private DateTime _lastUnmanagedDiagnosticUtc;
    private DateTime _lastRejectedDiagnosticUtc;
    private bool _standaloneRecording;

    public MainWindow(ManagedAccountRegistry? managedAccounts = null, DiagnosticsLog? diagnostics = null)
    {
        InitializeComponent();
        _managedAccounts = managedAccounts ?? new ManagedAccountRegistry();
        _diagnostics = diagnostics ?? new DiagnosticsLog();
        _diagnostics.Added += Diagnostics_Added;
        _managedAccounts.Changed += ManagedAccounts_Changed;
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
        _macros.Add(macro);
        RefreshMacroList(macro);
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "RAM macro bundle (*.ramacro)|*.ramacro" };
        if (dialog.ShowDialog(this) != true) return;
        try { var bundle = MacroStore.ImportAsync(dialog.FileName).GetAwaiter().GetResult(); foreach (var macro in bundle.Macros) _macros.Add(macro); StatusText.Text = $"Imported {bundle.Macros.Count} macro(s)."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshMacroList(_selected);
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "RAM macro bundle (*.ramacro)|*.ramacro", AddExtension = true, DefaultExt = ".ramacro", FileName = "ram-macros.ramacro" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            MacroStore.ExportAsync(dialog.FileName, new MacroBundle { Macros = _macros.ToArray() }).GetAwaiter().GetResult();
            StatusText.Text = $"Saved {_macros.Count} macro(s).";
            Diagnostic($"Exported {_macros.Count} macro(s) to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshMacroList(_selected);

    private void RefreshMacroList(MacroDefinition? select = null)
    {
        var filtered = _macros.Where(m => string.IsNullOrWhiteSpace(SearchBox.Text) || m.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        MacroList.ItemsSource = filtered;
        if (select is not null)
            MacroList.SelectedItem = filtered.FirstOrDefault(m => m.Id == select.Id);
    }

    private void RenameMacro_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is MacroDefinition macro) RenameMacro(macro);
    }

    private void RenameContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMacro(sender) is { } macro) RenameMacro(macro);
    }

    private void DuplicateContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMacro(sender) is not { } macro) return;
        var duplicate = macro with { Id = Guid.NewGuid().ToString("N"), Name = $"{macro.Name} copy" };
        _macros.Add(duplicate);
        RefreshMacroList(duplicate);
        Diagnostic($"Duplicated macro '{macro.Name}'.");
    }

    private void RemoveContext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextMacro(sender) is { } macro) RemoveMacro(macro);
    }

    private static MacroDefinition? GetContextMacro(object sender)
    {
        if (sender is not MenuItem item) return null;
        var menu = item.Parent as ContextMenu ?? ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu;
        return (menu?.PlacementTarget as FrameworkElement)?.DataContext as MacroDefinition;
    }

    private void RenameMacro(MacroDefinition macro)
    {
        var input = new TextBox { Text = macro.Name, Margin = new Thickness(0, 0, 0, 12), MinWidth = 280, Padding = new Thickness(8, 5, 8, 5) };
        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Background = System.Windows.Media.Brushes.MediumPurple, Foreground = System.Windows.Media.Brushes.White };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 34, 48)), Foreground = System.Windows.Media.Brushes.White };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Macro name", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(input); panel.Children.Add(buttons);
        var dialog = new Window { Title = "Rename macro", Content = panel, Width = 360, Height = 160, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 20, 27)), Foreground = System.Windows.Media.Brushes.White };
        WindowAppearance.Apply(dialog);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; }
            dialog.DialogResult = true;
        };
        input.SelectAll();
        if (dialog.ShowDialog() != true) return;
        var renamed = macro with { Name = input.Text.Trim() };
        var index = _macros.IndexOf(macro);
        if (index < 0) return;
        _macros[index] = renamed;
        if (_selected?.Id == macro.Id) _selected = renamed;
        RefreshMacroList(renamed);
        RefreshEventList();
        Diagnostic($"Renamed macro to '{renamed.Name}'.");
    }

    private void RemoveMacro(MacroDefinition macro)
    {
        if (MessageBox.Show(this, $"Remove '{macro.Name}'?", "Remove macro", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_recording && _selected?.Id == macro.Id) StopRecording();
        _macros.Remove(macro);
        if (_selected?.Id == macro.Id) _selected = _macros.FirstOrDefault();
        RefreshMacroList(_selected);
        RefreshEventList();
        Diagnostic($"Removed macro '{macro.Name}'.");
    }
    private void MacroList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selected = MacroList.SelectedItem as MacroDefinition;
        RefreshEventList();
    }

    private void Diagnostics_Added(object? sender, DiagnosticEntry entry)
    {
        if (Dispatcher.CheckAccess()) AddDiagnosticToList(entry);
        else
        {
            try { Dispatcher.BeginInvoke(new Action(() => AddDiagnosticToList(entry))); }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { }
            catch (TaskCanceledException) { }
        }
    }

    private void AddDiagnosticToList(DiagnosticEntry entry)
    {
        DiagnosticsList.Items.Add(entry.ToString());
        while (DiagnosticsList.Items.Count > 80) DiagnosticsList.Items.RemoveAt(0);
        DiagnosticsList.ScrollIntoView(entry.ToString());
    }

    private void ManagedAccounts_Changed(object? sender, int count) =>
        _diagnostics.Info($"Managed-account registry: {count} usable Roblox window(s).");

    private void Diagnostic(string message) => _diagnostics.Info(message);
    private void DiagnosticWarning(string message) => _diagnostics.Warning(message);
    private void DiagnosticError(string message) => _diagnostics.Error(message);
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            StatusText.Text = "Create or select a macro first.";
            DiagnosticWarning("Record requested without a selected macro.");
            return;
        }
        if (_recording)
        {
            StopRecording();
            return;
        }

        var foreground = NativeWindowMetrics.GetForegroundWindow();
        var managedAvailable = _managedAccounts.Snapshot().Count > 0;
        _standaloneRecording = !managedAvailable;
        if (!managedAvailable && foreground != _windowHandle && !NativeWindowMetrics.TryGetStandaloneRobloxSnapshot(foreground, out _))
        {
            StatusText.Text = "No running managed Roblox windows are available. Start an account and try again.";
            DiagnosticWarning("Record blocked: no usable managed Roblox windows are available.");
            return;
        }

        _recording = true;
        _recordingWindow = nint.Zero;
        _capturedInputCount = 0;
        _ignoredInjectedCount = 0;
        _rejectedEventCount = 0;
        _lastRejectedDiagnosticUtc = DateTime.MinValue;
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
            DiagnosticError($"Global input hooks failed to start: {ex.Message}");
            return;
        }
        RecordButton.Content = "■  Stop recording";
        FooterText.Text = "Recording background input. The panel stays visible; its own input is ignored.";
        StatusText.Text = foreground == _windowHandle
            ? (_standaloneRecording ? "Recording armed. Activate Roblox; events will appear here." : "Recording armed. Activate a managed Roblox window; events will appear here.")
            : (_standaloneRecording ? "Recording Roblox input in standalone mode..." : "Recording managed window input...\nTarget will bind to the active Roblox client.");
        Diagnostic(foreground == _windowHandle
            ? (_standaloneRecording ? "Recording armed without a launcher host; panel input will be ignored and the next Roblox foreground will bind." : "Recording armed while the RAM Macros panel is foreground; panel input will be ignored.")
            : (_standaloneRecording ? "Standalone recording started with a Roblox client foreground." : "Recording started with a Roblox client foreground."));
    }

    private void HandleCapturedInput(CapturedInput captured)
    {
        if (!_recording || captured.WindowHandle == _windowHandle) return;
        var capturedCount = Interlocked.Increment(ref _capturedInputCount);
        if (capturedCount == 1)
            Diagnostic($"Input hook observed {captured.Event.Kind} on foreground HWND 0x{captured.WindowHandle.ToInt64():X}.");
        // Filter before binding a target. Injected input must never be able to
        // select a window or enter the recorded sequence.
        if (captured.Injected)
        {
            var ignored = Interlocked.Increment(ref _ignoredInjectedCount);
            if (ignored == 1) Diagnostic("Ignored injected input from the recording hook.");
            return;
        }
        if (!_managedAccounts.TryResolve(captured.WindowHandle, out var account) && (!_standaloneRecording || !NativeWindowMetrics.TryGetStandaloneRobloxSnapshot(captured.WindowHandle, out account)))
        {
            if (DateTime.UtcNow - _lastUnmanagedDiagnosticUtc > TimeSpan.FromSeconds(2))
            {
                _lastUnmanagedDiagnosticUtc = DateTime.UtcNow;
                DiagnosticWarning(_standaloneRecording ? "Ignored input from a non-Roblox foreground window." : "Ignored input from an unmanaged foreground window.");
            }
            return;
        }
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
            Diagnostic($"Bound recording target to {account.Label} (HWND 0x{account.WindowHandle.ToInt64():X}).");
        }

        var metricsNow = NativeWindowMetrics.GetClientMetrics(targetWindow);
        var recorderWindow = new RecorderWindow("default", targetWindow, metricsNow.Width, metricsNow.Height);
        if (_recorder.TryRecord(recorderWindow, captured.Event, clientX, clientY, captured.Injected, multiWindow: false))
        {
            QueueEventListRefresh();
            var count = _recorder.Snapshot().Count;
            if (count == 1 || count % 25 == 0) Diagnostic($"Captured {count} event(s) for {account.Label}.");
        }
        else
        {
            var rejected = Interlocked.Increment(ref _rejectedEventCount);
            if (rejected == 1 || DateTime.UtcNow - _lastRejectedDiagnosticUtc >= TimeSpan.FromSeconds(2))
            {
                _lastRejectedDiagnosticUtc = DateTime.UtcNow;
                DiagnosticWarning($"Input hook event was rejected for {account.Label}; check foreground/window bounds.");
            }
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
        _standaloneRecording = false;
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
        Diagnostic($"Recording stopped with {events.Count} event(s); hook observed {_capturedInputCount}, ignored {_ignoredInjectedCount} injected, rejected {_rejectedEventCount}.");
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
        _diagnostics.Added -= Diagnostics_Added;
        _managedAccounts.Changed -= ManagedAccounts_Changed;
        _inputCapture.Dispose();
        base.OnClosed(e);
    }
}

internal static class NativeWindowMetrics
{
    public static nint GetForegroundWindow() => GetForegroundWindowNative();

    public static bool TryGetStandaloneRobloxSnapshot(nint window, out ManagedAccountSnapshot snapshot)
    {
        snapshot = null!;
        if (window == nint.Zero) return false;
        var root = GetAncestor(window, GaRoot);
        if (root == nint.Zero) root = window;
        if (!GetWindowThreadProcessId(root, out var processId) || processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            if (!processName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(processName, "Roblox", StringComparison.OrdinalIgnoreCase)) return false;
            var metrics = GetClientMetrics(root);
            if (metrics.Width <= 0 || metrics.Height <= 0) return false;
            snapshot = new ManagedAccountSnapshot(
                $"standalone-{processId}",
                string.IsNullOrWhiteSpace(process.MainWindowTitle) ? "Roblox (standalone)" : process.MainWindowTitle,
                processId,
                process.StartTime.ToUniversalTime().Ticks,
                root,
                0,
                0,
                metrics.Width,
                metrics.Height,
                96,
                false,
                DateTime.UtcNow,
                true,
                root);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

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
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint GetForegroundWindowNative();
    private const uint GaRoot = 2;
}

internal static class WindowAppearance
{
    public static void Apply(Window window) { window.SourceInitialized += (_, _) => { }; }
}
