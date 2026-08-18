using RamMacros;
using System.Text.Json;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
var input = new MacroEvent { NormalizedX = 0.5, NormalizedY = 0.25 };
Require(MacroCoordinateMapper.ToClient(input, 100, 200) == (50, 50), "Normalized coordinates were not mapped correctly.");
Require(MacroCoordinateMapper.Normalize(50, 100) > 0.49 && MacroCoordinateMapper.Normalize(50, 100) < 0.51, "Coordinate normalization failed.");
var recorder = new MacroRecorder(() => (nint)42, (_, _) => (0, 0, 100, 100));
recorder.Start([new RecorderWindow("default", (nint)42, 100, 100)]);
Require(recorder.TryRecord(new RecorderWindow("default", (nint)42, 100, 100), new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 65 }, 0, 0, injected: false, multiWindow: false), "A foreground key event was not recorded.");
Require(recorder.Snapshot().Count == 1, "The recorder snapshot did not expose the captured event.");
Require(!recorder.TryRecord(new RecorderWindow("default", (nint)42, 100, 100), new MacroEvent { Kind = MacroEventKind.KeyUp, VirtualKey = 65 }, 0, 0, injected: true, multiWindow: false), "An injected event was recorded.");
Require(recorder.Stop().Count == 1, "The recorder lost the captured event when stopping.");
var diagnostics = new DiagnosticsLog();
var diagnosticCount = 0;
diagnostics.Added += (_, _) => diagnosticCount++;
diagnostics.Added += (_, _) => throw new InvalidOperationException("subscriber failure");
diagnostics.Info("hook started");
diagnostics.Warning(new string('x', 2_100));
Require(diagnosticCount == 2 && diagnostics.Snapshot().Count == 2, "Diagnostics entries were not retained and raised.");
Require(diagnostics.Snapshot()[1].Message.Length == 2_000, "Diagnostic messages were not bounded.");
using var snapshotDocument = JsonDocument.Parse("{\"accountId\":\"a\",\"label\":\"A\",\"processId\":1,\"processStartTimeUtcTicks\":1,\"windowHandle\":42,\"clientX\":0,\"clientY\":0,\"clientWidth\":100,\"clientHeight\":100,\"dpi\":96,\"isMinimized\":false,\"lastActivityUtc\":\"2026-01-01T00:00:00Z\",\"isRunning\":true,\"rootWindowHandle\":41}");
var decodedSnapshot = PluginClient.Deserialize<ManagedAccountSnapshot>(snapshotDocument.RootElement);
Require(decodedSnapshot?.WindowHandle == (nint)42 && decodedSnapshot.RootWindowHandle == (nint)41, "Managed-account HWND wire deserialization failed.");
var tokenPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".token");
await File.WriteAllTextAsync(tokenPath, "test-token");
var launchClient = PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--token-file", tokenPath, "--plugin-id", "io.github.codysimonds65.ram.macros", "--data", "test-data"]);
Require(launchClient is not null && !File.Exists(tokenPath), "Plugin launch arguments did not preserve the host pipe and token-file values.");
await launchClient!.DisposeAsync();
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ramacro");
try
{
    await MacroStore.ExportAsync(path, new MacroBundle { Macros = [new MacroDefinition { Name = "test", Events = [input] }] });
    var loaded = await MacroStore.ImportAsync(path);
    Require(loaded.Macros.Count == 1 && loaded.Macros[0].Events.Count == 1, "Macro bundle round-trip failed.");
}
finally { if (File.Exists(path)) File.Delete(path); }
Console.WriteLine("RAM Macros tests passed.");
