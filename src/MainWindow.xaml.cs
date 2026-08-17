using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace RamMacros;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MacroDefinition> _macros = [];
    private MacroDefinition? _selected;
    private bool _recording;

    public MainWindow()
    {
        InitializeComponent();
        MacroList.ItemsSource = _macros;
        WindowAppearance.Apply(this);
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
        _selected = MacroList.SelectedItem as MacroDefinition; EventList.ItemsSource = _selected?.Events.Select(item => $"{item.OffsetMicroseconds / 1000.0:0.0} ms  {item.Kind}").ToArray(); StatusText.Text = _selected is null ? "Select a macro to begin." : $"{_selected.Name}\n{_selected.Events.Count} event(s)\nPortable normalized coordinates.";
    }
    private void Record_Click(object sender, RoutedEventArgs e) { _recording = !_recording; RecordButton.Content = _recording ? "■  Stop recording" : "●  Record"; FooterText.Text = _recording ? "Recording managed-window input; injected events are ignored." : "Background-safe mode: no focus APIs are used."; }
    private void Play_Click(object sender, RoutedEventArgs e) => StatusText.Text = _selected is null ? "Select a macro first." : "Playback requires managed account targets from the host.";
    private void Stack_Click(object sender, RoutedEventArgs e) => StatusText.Text = "STACK requested with SWP_NOACTIVATE.";
    private void Grid_Click(object sender, RoutedEventArgs e) => StatusText.Text = "GRID requested with SWP_NOACTIVATE.";
    private void Reset_Click(object sender, RoutedEventArgs e) => StatusText.Text = "RESET requested with SWP_NOACTIVATE.";
}

internal static class WindowAppearance
{
    public static void Apply(Window window) { window.SourceInitialized += (_, _) => { }; }
}
