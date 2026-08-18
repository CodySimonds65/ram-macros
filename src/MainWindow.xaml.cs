using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private bool _panelMode;
    private bool _panelBound;
    private bool _minimizedForRecording;
    private nint _recordingWindow;
    private nint _panelTargetWindow;
    private nint _windowHandle;
    private int _eventRefreshPending;
    private int _capturedInputCount;
    private int _ignoredInjectedCount;
    private int _rejectedEventCount;
    private DateTime _lastUnmanagedDiagnosticUtc;
    private DateTime _lastRejectedDiagnosticUtc;
    private bool _standaloneRecording;
    private int _recordHotkeyVk = 0x78;

    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

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
        LoadSettings();
        HotkeyButton.Content = $"Hotkey: {KeyName(_recordHotkeyVk)}";
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.TryGetProperty("recordHotkey", out var hotkey) &&
                hotkey.TryGetInt32(out var vk) && vk is >= 1 and <= 255)
                _recordHotkeyVk = vk;
        }
        catch
        {
            // Settings are best-effort; defaults apply when unreadable.
        }
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { recordHotkey = _recordHotkeyVk }));
        }
        catch
        {
            // Settings are best-effort; the hotkey still applies for this session.
        }
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        var nextNumber = _macros.Count + 1;
        while (_macros.Any(macro => string.Equals(macro.Name, $"Macro {nextNumber}", StringComparison.OrdinalIgnoreCase))) nextNumber++;
        var macro = new MacroDefinition { Name = $"Macro {nextNumber}" };
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

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopyDiagnostics(selectedOnly: false);
    private void CopySelectedLog_Click(object sender, RoutedEventArgs e) => CopyDiagnostics(selectedOnly: true);
    private void CopyAllLog_Click(object sender, RoutedEventArgs e) => CopyDiagnostics(selectedOnly: false);

    private void CopyDiagnostics(bool selectedOnly)
    {
        var lines = selectedOnly && DiagnosticsList.SelectedItems.Count > 0
            ? DiagnosticsList.SelectedItems.Cast<string>().ToArray()
            : _diagnostics.Snapshot().Select(entry => entry.ToString()).ToArray();
        if (lines.Length == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            StatusText.Text = $"Copied {lines.Length} log line(s).";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Threading.ThreadStateException)
        {
            StatusText.Text = "The clipboard is busy; try again.";
            DiagnosticWarning("Clipboard copy failed while the clipboard was busy.");
        }
    }

    private void DiagnosticsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CopyDiagnostics(selectedOnly: false);
            e.Handled = true;
        }
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { StatusText.Text = "Stop recording before changing the hotkey."; return; }
        var capture = new TextBox { Text = KeyName(_recordHotkeyVk), MinWidth = 260, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(23, 27, 36)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(39, 45, 58)), CaretBrush = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
        var selectedVk = _recordHotkeyVk;
        capture.PreviewKeyDown += (_, keyArgs) =>
        {
            var key = keyArgs.Key == Key.System ? keyArgs.SystemKey : keyArgs.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;
            selectedVk = vk;
            capture.Text = KeyName(vk);
            keyArgs.Handled = true;
        };
        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Background = System.Windows.Media.Brushes.MediumPurple, Foreground = System.Windows.Media.Brushes.White };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(29, 34, 48)), Foreground = System.Windows.Media.Brushes.White };
        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actionButtons.Children.Add(save); actionButtons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Recording hotkey", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Press a key below, then Save. The hotkey starts and stops recording globally.", Foreground = new SolidColorBrush(Color.FromRgb(146, 154, 173)), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(capture); panel.Children.Add(actionButtons);
        var dialog = new Window { Title = "Change recording hotkey", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(17, 20, 27)), Foreground = System.Windows.Media.Brushes.White };
        WindowAppearance.Apply(dialog);
        save.Click += (_, _) => dialog.DialogResult = true;
        capture.Focus();
        if (dialog.ShowDialog() != true) return;
        _recordHotkeyVk = selectedVk;
        HotkeyButton.Content = $"Hotkey: {KeyName(_recordHotkeyVk)}";
        SaveSettings();
        Diagnostic($"Recording hotkey set to {KeyName(_recordHotkeyVk)}.");
    }

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
        StartRecording();
    }

    private void StartRecording()
    {
        if (_recording || _selected is null) return;

        _panelMode = PanelModeCheck.IsChecked == true;
        var foreground = NativeWindowMetrics.GetForegroundWindow();
        var managedAvailable = _managedAccounts.Snapshot().Count > 0;
        _standaloneRecording = !managedAvailable;
        if (!_panelMode && !managedAvailable && foreground != _windowHandle && !NativeWindowMetrics.TryGetStandaloneRobloxSnapshot(foreground, out _))
        {
            StatusText.Text = "No running managed Roblox windows are available. Start an account and try again.";
            DiagnosticWarning("Record blocked: no usable managed Roblox windows are available.");
            return;
        }

        _recording = true;
        _recordingWindow = nint.Zero;
        _panelTargetWindow = nint.Zero;
        _panelBound = false;
        _capturedInputCount = 0;
        _ignoredInjectedCount = 0;
        _rejectedEventCount = 0;
        _lastRejectedDiagnosticUtc = DateTime.MinValue;
        if (_panelMode)
        {
            _panelTargetWindow = _managedAccounts.Snapshot().FirstOrDefault()?.WindowHandle ?? nint.Zero;
            var panelMetrics = NativeWindowMetrics.GetClientMetrics(_panelTargetWindow);
            _recorder.Start([new RecorderWindow("default", _panelTargetWindow, panelMetrics.Width, panelMetrics.Height)]);
        }
        else
        {
            _recorder.Start([]);
        }
        try
        {
            _inputCapture.StopHotkey = (uint)_recordHotkeyVk;
            _inputCapture.Start(HandleCapturedInput, _windowHandle, _panelMode);
            _inputCapture.StopRequested = OnStopHotkey;
        }
        catch (Exception ex)
        {
            _recorder.Stop();
            _recording = false;
            _recordingWindow = nint.Zero;
            _panelTargetWindow = nint.Zero;
            StatusText.Text = $"Could not start recording.\n{ex.Message}";
            DiagnosticError($"Global input hooks failed to start: {ex.Message}");
            return;
        }
        RecordButton.Content = "■  Stop recording";
        PanelModeCheck.IsEnabled = false;
        HotkeyButton.IsEnabled = false;
        var hotkeyName = KeyName(_recordHotkeyVk);
        if (_panelMode)
        {
            FooterText.Text = "Panel mode: keystrokes typed with the panel focused are recorded; mouse over the panel is ignored.";
            StatusText.Text = $"Recording panel input...\nType keys with the panel focused; {hotkeyName} stops.";
            Diagnostic($"Recording panel input: keystrokes typed with the panel focused will be recorded. Mouse over the panel is ignored; {hotkeyName} stops.");
        }
        else
        {
            FooterText.Text = $"Recording background input. The panel is minimized; {hotkeyName} stops and restores.";
            StatusText.Text = foreground == _windowHandle
                ? "Recording armed. Activate a managed Roblox window; events will appear here."
                : "Recording managed window input...\nTarget will bind to the active Roblox client.";
            Diagnostic(foreground == _windowHandle
                ? "Recording armed while the RAM Macros panel is foreground; panel input will be ignored."
                : "Recording started with a Roblox client foreground.");
            _minimizedForRecording = true;
            WindowState = WindowState.Minimized;
            Diagnostic($"Recording started; the panel is minimized. Press {hotkeyName} to stop and restore.");
        }
    }

    private void HandleCapturedInput(CapturedInput captured)
    {
        if (!_recording) return;
        var isKeyboard = captured.Event.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp;
        if (captured.WindowHandle == _windowHandle && !(_panelMode && isKeyboard)) return;
        var capturedCount = Interlocked.Increment(ref _capturedInputCount);
        if (capturedCount == 1)
        {
            if (isKeyboard)
                Diagnostic($"Input hook observed {captured.Event.Kind} VK 0x{captured.Event.VirtualKey:X2} scan 0x{captured.Event.ScanCode:X2} on HWND 0x{captured.WindowHandle.ToInt64():X}.");
            else
                Diagnostic($"Input hook observed {captured.Event.Kind} on foreground HWND 0x{captured.WindowHandle.ToInt64():X}.");
        }
        if (captured.Injected)
        {
            var ignored = Interlocked.Increment(ref _ignoredInjectedCount);
            if (ignored == 1) Diagnostic("Ignored injected input from the recording hook.");
            return;
        }
        if (_panelMode)
        {
            if (!isKeyboard || captured.WindowHandle != _windowHandle) return;
            if (!_panelBound)
            {
                _panelBound = true;
                _recordingWindow = _panelTargetWindow;
                var bindMetrics = NativeWindowMetrics.GetClientMetrics(_recordingWindow);
                _recorder.Start([new RecorderWindow("default", _recordingWindow, bindMetrics.Width, bindMetrics.Height)]);
                if (_panelTargetWindow == nint.Zero)
                    Diagnostic("Panel mode: recording keystrokes without a window target.");
            }
            var panelMetrics = NativeWindowMetrics.GetClientMetrics(_recordingWindow);
            var panelWindow = new RecorderWindow("default", _recordingWindow, panelMetrics.Width, panelMetrics.Height);
            if (_recorder.TryRecordDetailed(panelWindow, captured.Event, 0, 0, captured.Injected, multiWindow: true, out var panelReason))
            {
                QueueEventListRefresh();
                var count = _recorder.Snapshot().Count;
                if (count == 1 || count % 25 == 0) Diagnostic($"Captured {count} event(s) for panel-mode keyboard recording.");
            }
            else
            {
                var rejected = Interlocked.Increment(ref _rejectedEventCount);
                if (rejected == 1 || DateTime.UtcNow - _lastRejectedDiagnosticUtc >= TimeSpan.FromSeconds(2))
                {
                    _lastRejectedDiagnosticUtc = DateTime.UtcNow;
                    DiagnosticWarning($"Input event not recorded in panel mode: {panelReason}.");
                }
            }
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
        if (_recorder.TryRecordDetailed(recorderWindow, captured.Event, clientX, clientY, captured.Injected, multiWindow: false, out var rejectReason))
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
                var foreground = NativeWindowMetrics.GetForegroundWindow();
                DiagnosticWarning(rejected == 1
                    ? $"Input event not recorded for {account.Label}: {rejectReason} (client {clientX},{clientY}, client size {metricsNow.Width}x{metricsNow.Height}, foreground 0x{foreground.ToInt64():X}, target 0x{targetWindow.ToInt64():X})."
                    : $"Input hook events are being rejected for {account.Label}: {rejectReason}.");
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

    private void OnStopHotkey()
    {
        if (_recording)
        {
            _inputCapture.StopRequested = null;
            _inputCapture.Stop();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, StopRecording);
        }
        else
        {
            StartRecording();
        }
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _inputCapture.StopRequested = null;
        _inputCapture.Stop();
        var events = _recorder.Stop();
        _recording = false;
        _standaloneRecording = false;
        if (_minimizedForRecording)
        {
            _minimizedForRecording = false;
            WindowState = WindowState.Normal;
            Activate();
        }
        RecordButton.Content = "●  Record";
        PanelModeCheck.IsEnabled = true;
        HotkeyButton.IsEnabled = true;
        FooterText.Text = "Background-safe recording: the panel never steals focus from the game.";
        if (_selected is not null)
        {
            var clientMetrics = NativeWindowMetrics.GetClientMetrics(_recordingWindow);
            var updated = _selected with
            {
                Events = events,
                RecordedClientWidth = clientMetrics.Width > 0 ? clientMetrics.Width : 1,
                RecordedClientHeight = clientMetrics.Height > 0 ? clientMetrics.Height : 1
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

    private sealed record EventRow(int Index, string IndexText, MacroEvent Event, string Badge, Brush BadgeBrush, string Detail, string? DelayText);

    private void RefreshEventList()
    {
        var events = _recording ? _recorder.Snapshot() : _selected?.Events ?? [];
        var rows = new List<EventRow>(events.Count);
        long previousOffset = 0;
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            var delayText = i + 1 < events.Count
                ? ((events[i + 1].OffsetMicroseconds - item.OffsetMicroseconds) / 1000L).ToString()
                : null;
            rows.Add(new EventRow(i, $"{i + 1}.", item, BadgeFor(item), BadgeBrushFor(item), FormatEventDetail(item, item.OffsetMicroseconds - previousOffset), delayText));
            previousOffset = item.OffsetMicroseconds;
        }
        EventList.ItemsSource = rows;
        EventSummaryText.Text = events.Count == 0
            ? "0 events"
            : $"{events.Count} event(s) · total {events[^1].OffsetMicroseconds / 1000.0:0.#} ms";
        StatusText.Text = _selected is null ? "Select a macro to begin." : $"{_selected.Name}\n{events.Count} event(s)\nPortable normalized coordinates.";
    }

    private static string BadgeFor(MacroEvent item) => item.Kind switch
    {
        MacroEventKind.KeyDown or MacroEventKind.KeyUp => "KEY",
        MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp => "BTN",
        MacroEventKind.MouseWheel => "WHEEL",
        MacroEventKind.MouseMove => "MOVE",
        MacroEventKind.Delay => "DELAY",
        _ => "?"
    };

    private static readonly Brush KeyBadgeBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFC));
    private static readonly Brush ButtonBadgeBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
    private static readonly Brush WheelBadgeBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA9, 0x4C));
    private static readonly Brush MoveBadgeBrush = new SolidColorBrush(Color.FromRgb(0x92, 0x9A, 0xAD));
    private static readonly Brush DelayBadgeBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xA6, 0xE8));

    private static Brush BadgeBrushFor(MacroEvent item) => item.Kind switch
    {
        MacroEventKind.KeyDown or MacroEventKind.KeyUp => KeyBadgeBrush,
        MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp => ButtonBadgeBrush,
        MacroEventKind.MouseWheel => WheelBadgeBrush,
        MacroEventKind.MouseMove => MoveBadgeBrush,
        MacroEventKind.Delay => DelayBadgeBrush,
        _ => MoveBadgeBrush
    };

    private static string FormatEventDetail(MacroEvent item, long delayBeforeMicroseconds) => item.Kind switch
    {
        MacroEventKind.KeyDown => $"{KeyName(item.VirtualKey)} down",
        MacroEventKind.KeyUp => $"{KeyName(item.VirtualKey)} up",
        MacroEventKind.MouseButtonDown => $"{(item.Button == 1 ? "Left" : item.Button == 2 ? "Right" : "Middle")} click down",
        MacroEventKind.MouseButtonUp => $"{(item.Button == 1 ? "Left" : item.Button == 2 ? "Right" : "Middle")} click up",
        MacroEventKind.MouseWheel => $"Wheel {(item.WheelDelta >= 0 ? "+" : "")}{item.WheelDelta}",
        MacroEventKind.MouseMove => $"Move ({item.NormalizedX:P0}, {item.NormalizedY:P0})",
        MacroEventKind.Delay => $"Delay {delayBeforeMicroseconds / 1000L} ms",
        _ => item.Kind.ToString()
    };

    private static string KeyName(int vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Numpad{vk - 0x60}",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x2D => "Insert",
        0x2E => "Delete",
        0x14 => "CapsLock",
        0x5B or 0x5C => "Win",
        0x6A => "*",
        0x6B => "+",
        0x6D => "-",
        0x6E => ".",
        0x6F => "/",
        _ => $"VK 0x{vk:X2}"
    };

    private void ApplyEditedEvents(IReadOnlyList<MacroEvent> edited)
    {
        if (_selected is null) return;
        var updated = _selected with { Events = edited };
        var index = _macros.IndexOf(_selected);
        if (index >= 0) _macros[index] = updated;
        _selected = updated;
        MacroList.SelectedItem = updated;
        RefreshEventList();
    }

    private void InsertEvent_Click(object sender, RoutedEventArgs e)
    {
        if (_recording || _selected is null) { if (!_recording) return; StatusText.Text = "Stop recording before editing the sequence."; return; }
        var item = new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0x41, ScanCode = 0x1E };
        ApplyEditedEvents(MacroSequenceEditor.Insert(_selected.Events, _selected.Events.Count, item));
        Diagnostic("Inserted a key-down event.");
    }

    private void InsertDelay_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { StatusText.Text = "Stop recording before editing the sequence."; return; }
        if (_selected is null) return;
        var afterIndex = EventList.SelectedItem is EventRow row ? row.Index : _selected.Events.Count - 1;
        if (afterIndex < 0) afterIndex = 0;
        var edited = MacroSequenceEditor.InsertDelay(_selected.Events, afterIndex);
        ApplyEditedEvents(edited);
        Diagnostic("Inserted a delay between events; double-click it to set the pause length.");
    }

    private void ClearEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { StatusText.Text = "Stop recording before editing the sequence."; return; }
        if (_selected is null) return;
        if (MessageBox.Show(this, "Remove all events from this macro?", "Clear sequence", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ApplyEditedEvents([]);
        Diagnostic("Cleared the event sequence.");
    }

    private void DeleteEvent_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { StatusText.Text = "Stop recording before editing the sequence."; return; }
        if (_selected is null || (sender as FrameworkElement)?.Tag is not int index) return;
        ApplyEditedEvents(MacroSequenceEditor.RemoveAt(_selected.Events, index));
    }

    private void DelayBox_LostFocus(object sender, RoutedEventArgs e) => CommitDelay(sender as TextBox);

    private void DelayBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitDelay(sender as TextBox);
    }

    private void CommitDelay(TextBox? box)
    {
        if (_recording || _selected is null || box?.Tag is not int index) return;
        var events = _selected.Events;
        if (!int.TryParse(box.Text, out var delayMs) || delayMs < 0)
        {
            box.Text = index + 1 < events.Count ? ((events[index + 1].OffsetMicroseconds - events[index].OffsetMicroseconds) / 1000L).ToString() : string.Empty;
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_recording || _selected is null) return;
            var current = _selected.Events;
            if (index + 1 >= current.Count) return;
            if ((current[index + 1].OffsetMicroseconds - current[index].OffsetMicroseconds) / 1000L == delayMs) return;
            ApplyEditedEvents(MacroSequenceEditor.SetDelay(current, index + 1, delayMs));
        }));
    }

    private void EventList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_recording) return;
        if (FindAncestor<TextBlock>(e.OriginalSource as DependencyObject) is not { Tag: "dragHandle" } handle ||
            handle.DataContext is not EventRow row) return;
        EventList.SelectedIndex = row.Index;
        DragDrop.DoDragDrop(EventList, new DataObject("macro-event-row", row.Index), DragDropEffects.Move);
    }

    private void EventList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("macro-event-row") || _recording)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = RowIndexAt(e.GetPosition(EventList)) >= 0 ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void EventList_Drop(object sender, DragEventArgs e)
    {
        if (_recording || _selected is null) return;
        if (e.Data.GetData("macro-event-row") is not int from) return;
        var to = RowIndexAt(e.GetPosition(EventList));
        if (to < 0) return;
        var edited = MacroSequenceEditor.Move(_selected.Events, from, to);
        if (ReferenceEquals(edited, _selected.Events)) return;
        ApplyEditedEvents(edited);
        StatusText.Text = "Reordered event sequence.";
        Diagnostic("Reordered macro event sequence.");
    }

    private int RowIndexAt(Point position)
    {
        for (var i = 0; i < EventList.Items.Count; i++)
        {
            if (EventList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container && container.IsMouseOver)
                return i;
        }
        var hit = VisualTreeHelper.HitTest(EventList, position);
        var item = FindAncestor<ListBoxItem>(hit?.VisualHit as DependencyObject);
        return item is null ? -1 : EventList.ItemContainerGenerator.IndexFromContainer(item);
    }

    private void EventList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_recording) { StatusText.Text = "Stop recording before editing the sequence."; return; }
        if (EventList.SelectedItem is not EventRow row) return;
        EditEventAt(row.Index);
    }

    private void EditEventAt(int index)
    {
        var events = _selected?.Events ?? [];
        if (index < 0 || index >= events.Count) return;
        var item = events[index];
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"Edit {item.Kind}", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
        var edited = item;
        switch (item.Kind)
        {
            case MacroEventKind.KeyDown or MacroEventKind.KeyUp:
                var capture = new TextBox { Text = KeyName(item.VirtualKey), MinWidth = 260, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(23, 27, 36)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(39, 45, 58)), CaretBrush = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
                capture.PreviewKeyDown += (_, keyArgs) =>
                {
                    var key = keyArgs.Key == Key.System ? keyArgs.SystemKey : keyArgs.Key;
                    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
                    var vk = KeyInterop.VirtualKeyFromKey(key);
                    if (vk == 0) return;
                    edited = item with { VirtualKey = vk, ScanCode = 0 };
                    capture.Text = KeyName(vk);
                    keyArgs.Handled = true;
                };
                panel.Children.Add(new TextBlock { Text = "Press a key in the box below, or type its name.", Foreground = new SolidColorBrush(Color.FromRgb(146, 154, 173)), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
                panel.Children.Add(capture);
                break;
            case MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp:
                var buttons = new ComboBox { ItemsSource = new[] { "Left (1)", "Middle (2)", "Right (3)" }, SelectedIndex = item.Button is 1 or 2 or 3 ? item.Button - 1 : 0, MinWidth = 200, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(23, 27, 36)), Foreground = System.Windows.Media.Brushes.White };
                panel.Children.Add(buttons);
                edited = item with { Button = buttons.SelectedIndex + 1 };
                buttons.SelectionChanged += (_, _) => edited = item with { Button = buttons.SelectedIndex + 1 };
                break;
            case MacroEventKind.MouseWheel:
                var wheel = new TextBox { Text = item.WheelDelta.ToString(), MinWidth = 120, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(23, 27, 36)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(39, 45, 58)), CaretBrush = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(new TextBlock { Text = "Wheel delta (e.g. 120 or -120)", Foreground = new SolidColorBrush(Color.FromRgb(146, 154, 173)), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
                panel.Children.Add(wheel);
                break;
            case MacroEventKind.Delay:
                var delay = new TextBox { Text = DelayValueFor(events, index).ToString(), MinWidth = 120, Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(23, 27, 36)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(39, 45, 58)), CaretBrush = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(new TextBlock { Text = "Pause length in milliseconds", Foreground = new SolidColorBrush(Color.FromRgb(146, 154, 173)), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) });
                panel.Children.Add(delay);
                break;
            default:
                return;
        }
        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Background = System.Windows.Media.Brushes.MediumPurple, Foreground = System.Windows.Media.Brushes.White };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(29, 34, 48)), Foreground = System.Windows.Media.Brushes.White };
        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        actionButtons.Children.Add(save); actionButtons.Children.Add(cancel);
        panel.Children.Add(actionButtons);
        var dialog = new Window { Title = $"Edit event {index + 1}", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(17, 20, 27)), Foreground = System.Windows.Media.Brushes.White };
        WindowAppearance.Apply(dialog);
        save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        if (item.Kind == MacroEventKind.MouseWheel && panel.Children.OfType<TextBox>().FirstOrDefault() is { } wheelBox && int.TryParse(wheelBox.Text, out var wheelDelta))
            edited = item with { WheelDelta = wheelDelta };
        if (item.Kind == MacroEventKind.Delay)
        {
            if (panel.Children.OfType<TextBox>().FirstOrDefault() is { } delayBox && long.TryParse(delayBox.Text, out var delayMs) && delayMs >= 0)
                ApplyEditedEvents(MacroSequenceEditor.SetDelay(events, index, (int)Math.Min(delayMs, int.MaxValue)));
            return;
        }
        ApplyEditedEvents(MacroSequenceEditor.UpdateEvent(events, index, edited));
        Diagnostic($"Edited event {index + 1} ({edited.Kind}).");
    }

    private static long DelayValueFor(IReadOnlyList<MacroEvent> events, int index)
    {
        if (index < 0 || index >= events.Count) return 0;
        var previous = index > 0 ? events[index - 1].OffsetMicroseconds : 0;
        return Math.Max(0, (events[index].OffsetMicroseconds - previous) / 1000L);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => StatusText.Text = _selected is null ? "Select a macro first." : "Playback requires managed account targets from the host.";
    private void Stack_Click(object sender, RoutedEventArgs e) => StatusText.Text = "STACK requested with SWP_NOACTIVATE.";
    private void Grid_Click(object sender, RoutedEventArgs e) => StatusText.Text = "GRID requested with SWP_NOACTIVATE.";
    private void Reset_Click(object sender, RoutedEventArgs e) => StatusText.Text = "RESET requested with SWP_NOACTIVATE.";
    protected override void OnClosed(EventArgs e)
    {
        _diagnostics.Added -= Diagnostics_Added;
        _managedAccounts.Changed -= ManagedAccounts_Changed;
        _inputCapture.StopRequested = null;
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
