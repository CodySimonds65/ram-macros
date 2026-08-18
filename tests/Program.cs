using RamMacros;
using System.Text.Json;
using System.Reflection;

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
var pluginIdField = typeof(PluginClient).GetField("_pluginId", BindingFlags.Instance | BindingFlags.NonPublic);
var tokenField = typeof(PluginClient).GetField("_token", BindingFlags.Instance | BindingFlags.NonPublic);
Require((string?)pluginIdField?.GetValue(launchClient) == "io.github.codysimonds65.ram.macros", "Plugin launch arguments did not preserve the plugin ID.");
Require((string?)tokenField?.GetValue(launchClient) == "test-token", "Plugin launch arguments did not preserve the token.");
var parseMethod = typeof(PluginClient).GetMethod("TryParseArgs", BindingFlags.Static | BindingFlags.NonPublic);
var parseArguments = new object?[] { new[] { "--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "io.github.codysimonds65.ram.macros", "--data", "test-data" }, null };
Require((bool?)parseMethod?.Invoke(null, parseArguments) == true, "Valid launch arguments were not parsed.");
var parsedArguments = parseArguments[1] as IReadOnlyDictionary<string, string>;
Require(parsedArguments is not null && parsedArguments["pipe"] == "test-pipe" && parsedArguments["data"] == "test-data", "Parsed pipe or data values were not preserved.");
await launchClient!.DisposeAsync();
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe"]) is null, "A missing pipe value was accepted.");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token", "test-token"]) is null, "An empty pipe value was accepted.");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id"]) is null, "A missing plugin ID value was accepted.");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "", "--token", "test-token"]) is null, "An empty plugin ID was accepted.");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token", ""]) is null, "An empty inline token was accepted.");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token-file"]) is null, "A missing token-file value was accepted.");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token-file", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing")]) is null, "A missing token file was not rejected safely.");
var emptyTokenPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".empty-token");
await File.WriteAllTextAsync(emptyTokenPath, "");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token-file", emptyTokenPath]) is null, "An empty token file was accepted.");
var conflictTokenPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".conflict-token");
await File.WriteAllTextAsync(conflictTokenPath, "file-token");
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "test-pipe", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token", "inline-token", "--token-file", conflictTokenPath]) is null, "Conflicting credential sources were accepted.");
if (File.Exists(conflictTokenPath)) File.Delete(conflictTokenPath);
Require(PluginClient.FromArgs(["--ram-plugin", "--pipe", "one", "--pipe", "two", "--plugin-id", "io.github.codysimonds65.ram.macros", "--token", "test-token"]) is null, "Duplicate launch options were accepted.");
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ramacro");
try
{
    await MacroStore.ExportAsync(path, new MacroBundle { Macros = [new MacroDefinition { Name = "test", Events = [input] }] });
    var loaded = await MacroStore.ImportAsync(path);
    Require(loaded.Macros.Count == 1 && loaded.Macros[0].Events.Count == 1, "Macro bundle round-trip failed.");
}
finally { if (File.Exists(path)) File.Delete(path); }
var libraryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".library.json");
try
{
    MacroStore.SaveLibrary(libraryPath, new MacroBundle { Macros = [new MacroDefinition { Name = "Saved", Events = [input] }] });
    var reloadedLibrary = MacroStore.LoadLibrary(libraryPath);
    Require(reloadedLibrary is not null && reloadedLibrary.Macros.Count == 1 && reloadedLibrary.Macros[0].Name == "Saved" && reloadedLibrary.Macros[0].Events.Count == 1, "Macro library save/load round-trip failed.");
    Require(MacroStore.LoadLibrary(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing.json")) is null, "A missing library file did not load as empty.");
}
finally { if (File.Exists(libraryPath)) File.Delete(libraryPath); }
Console.WriteLine("RAM Macros tests passed.");
static MacroEvent E(long offsetUs, MacroEventKind kind = MacroEventKind.KeyDown) => new MacroEvent { OffsetMicroseconds = offsetUs, Kind = kind, VirtualKey = 65 };
static void RequireMonotonic(IReadOnlyList<MacroEvent> items)
{
    long previous = 0;
    foreach (var item in items)
    {
        Require(item.OffsetMicroseconds >= previous, "Macro sequence offsets are not monotonic.");
        previous = item.OffsetMicroseconds;
    }
}
var editorEvents = new MacroEvent[] { E(0), E(250_000) with { VirtualKey = 66 }, E(1_000_000) with { VirtualKey = 67 } };
var delayed = MacroSequenceEditor.SetDelay(editorEvents, 1, 500);
Require(delayed[0].OffsetMicroseconds == 0 && delayed[1].OffsetMicroseconds == 500_000 && delayed[2].OffsetMicroseconds == 1_250_000, "SetDelay did not replace the delta at the requested index and shift later offsets.");
RequireMonotonic(delayed);
var firstDelay = MacroSequenceEditor.SetDelay(editorEvents, 0, 100);
Require(firstDelay[0].OffsetMicroseconds == 100_000 && firstDelay[1].OffsetMicroseconds == 350_000 && firstDelay[2].OffsetMicroseconds == 1_100_000, "SetDelay did not apply the delay to the first delta.");
RequireMonotonic(firstDelay);
var clampedDelay = MacroSequenceEditor.SetDelay(editorEvents, 1, -500);
Require(clampedDelay[0].OffsetMicroseconds == 0 && clampedDelay[1].OffsetMicroseconds == 0 && clampedDelay[2].OffsetMicroseconds == 750_000, "Negative delays were not clamped to zero.");
RequireMonotonic(clampedDelay);
Require(ReferenceEquals(MacroSequenceEditor.SetDelay(editorEvents, 3, 100), editorEvents), "An out-of-range SetDelay index did not return the input unchanged.");
var movedForward = MacroSequenceEditor.Move(editorEvents, 0, 2);
Require(movedForward[0].VirtualKey == 66 && movedForward[1].VirtualKey == 67 && movedForward[2].VirtualKey == 65, "Move did not relocate the event to the requested index.");
Require(movedForward[0].OffsetMicroseconds == 250_000 && movedForward[1].OffsetMicroseconds == 1_000_000 && movedForward[2].OffsetMicroseconds == 1_000_000, "Move did not relocate the moved delta to the end of the sequence.");
RequireMonotonic(movedForward);
var movedBack = MacroSequenceEditor.Move(editorEvents, 2, 0);
Require(movedBack[0].VirtualKey == 67 && movedBack[1].VirtualKey == 65 && movedBack[2].VirtualKey == 66, "Move did not relocate the event to the front.");
Require(movedBack[0].OffsetMicroseconds == 750_000 && movedBack[1].OffsetMicroseconds == 750_000 && movedBack[2].OffsetMicroseconds == 1_000_000, "Move did not relocate the moved delta to the front of the sequence.");
RequireMonotonic(movedBack);
Require(ReferenceEquals(MacroSequenceEditor.Move(editorEvents, 1, 1), editorEvents), "A no-op Move did not return the input unchanged.");
var removed = MacroSequenceEditor.RemoveAt(editorEvents, 1);
Require(removed.Count == 2 && removed[0].VirtualKey == 65 && removed[1].VirtualKey == 67, "RemoveAt did not drop the requested event.");
Require(removed[0].OffsetMicroseconds == 0 && removed[1].OffsetMicroseconds == 750_000, "RemoveAt did not keep the following event's delta.");
RequireMonotonic(removed);
Require(ReferenceEquals(MacroSequenceEditor.RemoveAt(editorEvents, 5), editorEvents), "An out-of-range RemoveAt index did not return the input unchanged.");
var inserted = MacroSequenceEditor.Insert(editorEvents, 1, E(0) with { VirtualKey = 88 });
Require(inserted[0].VirtualKey == 65 && inserted[1].VirtualKey == 88 && inserted[2].VirtualKey == 66 && inserted[3].VirtualKey == 67, "Insert did not place the new event at the requested index.");
Require(inserted[0].OffsetMicroseconds == 0 && inserted[1].OffsetMicroseconds == 0 && inserted[2].OffsetMicroseconds == 250_000 && inserted[3].OffsetMicroseconds == 1_000_000, "Insert did not give the new event a zero delta while preserving later deltas.");
RequireMonotonic(inserted);
var appended = MacroSequenceEditor.Insert(editorEvents, 3, E(0));
Require(appended[2].OffsetMicroseconds == 1_000_000 && appended[3].OffsetMicroseconds == 1_000_000, "Insert at the end did not append with a zero delta.");
RequireMonotonic(appended);
Require(ReferenceEquals(MacroSequenceEditor.Insert(editorEvents, 9, E(0)), editorEvents), "An out-of-range Insert index did not return the input unchanged.");
var updated = MacroSequenceEditor.UpdateEvent(editorEvents, 1, E(999, MacroEventKind.MouseWheel) with { WheelDelta = 120 });
Require(updated[1].Kind == MacroEventKind.MouseWheel && updated[1].WheelDelta == 120, "UpdateEvent did not replace the event payload.");
Require(updated[0].OffsetMicroseconds == 0 && updated[1].OffsetMicroseconds == 250_000 && updated[2].OffsetMicroseconds == 1_000_000, "UpdateEvent did not preserve the existing offsets.");
RequireMonotonic(updated);
Require(MacroSequenceEditor.TotalDurationMicroseconds([]) == 0, "Total duration of an empty sequence was not zero.");
Require(MacroSequenceEditor.TotalDurationMicroseconds(editorEvents) == 1_000_000, "Total duration did not equal the last offset.");
var withDelay = MacroSequenceEditor.InsertDelay(editorEvents, 0);
Require(withDelay.Count == 4 && withDelay[1].Kind == MacroEventKind.Delay, "InsertDelay did not place a delay row after the requested event.");
Require(withDelay[0].OffsetMicroseconds == 0 && withDelay[1].OffsetMicroseconds == 250_000 && withDelay[2].OffsetMicroseconds == 250_000 && withDelay[3].OffsetMicroseconds == 1_000_000, "InsertDelay did not absorb the existing gap and shift the following event onto the delay end.");
RequireMonotonic(withDelay);
var appendedDelay = MacroSequenceEditor.InsertDelay(editorEvents, 2);
Require(appendedDelay.Count == 4 && appendedDelay[3].Kind == MacroEventKind.Delay && appendedDelay[3].OffsetMicroseconds == 1_500_000, "InsertDelay at the end did not append a default 500 ms pause.");
RequireMonotonic(appendedDelay);
var emptyDelay = MacroSequenceEditor.InsertDelay([], 0);
Require(emptyDelay.Count == 1 && emptyDelay[0].Kind == MacroEventKind.Delay, "InsertDelay on an empty sequence did not create a delay row.");
Require(ReferenceEquals(MacroSequenceEditor.InsertDelay(editorEvents, 9), editorEvents), "An out-of-range InsertDelay index did not return the input unchanged.");
var resizedDelay = MacroSequenceEditor.SetDelay(appendedDelay, 3, 1200);
Require(resizedDelay[3].OffsetMicroseconds == 2_200_000 && resizedDelay[3].Kind == MacroEventKind.Delay, "SetDelay did not resize the delay row's pause.");
nint recorderForeground = (nint)0x1234;
var boundsRecorder = new MacroRecorder(foregroundWindow: () => recorderForeground, clientMetrics: (_, _) => (0, 0, 10, 10), windowMatches: (fg, target) => fg == target);
var recorderWindow = new RecorderWindow("default", (nint)0x1234, 10, 10);
boundsRecorder.Start([recorderWindow]);
Require(boundsRecorder.TryRecordDetailed(recorderWindow, E(0), 999, 999, injected: false, multiWindow: false, out var bypassReason) && bypassReason is null, "A keyboard event far outside the client bounds was not recorded.");
Require(!boundsRecorder.TryRecordDetailed(recorderWindow, E(0, MacroEventKind.MouseButtonDown), 999, 999, injected: false, multiWindow: false, out var boundsReason) && boundsReason is not null && boundsReason.Contains("bounds"), "A pointer event outside the client bounds was not rejected.");
Require(!boundsRecorder.TryRecordDetailed(recorderWindow, E(0), 0, 0, injected: true, multiWindow: false, out var injectedReason) && injectedReason is not null && injectedReason.Contains("injected"), "An injected key event was not rejected.");
Require(!boundsRecorder.TryRecordDetailed(new RecorderWindow("default", (nint)0x9999, 10, 10), E(0), 0, 0, injected: false, multiWindow: false, out var unknownReason) && unknownReason is not null && unknownReason.Contains("not in target set"), "An unknown target window was not rejected.");
recorderForeground = (nint)0x5555;
Require(!boundsRecorder.TryRecordDetailed(recorderWindow, E(0), 0, 0, injected: false, multiWindow: false, out var mismatchReason) && mismatchReason is not null && mismatchReason.Contains("foreground mismatch"), "A foreground mismatch was not rejected.");
Require(boundsRecorder.TryRecordDetailed(recorderWindow, E(0), 0, 0, injected: false, multiWindow: true, out _), "A foreground mismatch was not tolerated in multi-window mode.");
recorderForeground = (nint)0x1234;
var zeroMetricsRecorder = new MacroRecorder(foregroundWindow: () => (nint)0x1234, clientMetrics: (_, _) => (0, 0, 0, 0), windowMatches: (fg, target) => fg == target);
var zeroSizeWindow = new RecorderWindow("default", (nint)0x1234, 0, 0);
zeroMetricsRecorder.Start([zeroSizeWindow]);
Require(zeroMetricsRecorder.TryRecord(zeroSizeWindow, E(0), 0, 0, injected: false, multiWindow: false), "A keyboard event was not recorded for a zero-size window.");
Require(new DiagnosticEntry(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "info", "hello").ToString().EndsWith("[info]  hello"), "Diagnostic entry formatting did not end with the level and message.");
Require(InputPostSender.ToWire(E(0, MacroEventKind.Delay)) is null, "Delay events must not reach the wire.");
Require(InputPostSender.ToWire(new MacroEvent { Kind = MacroEventKind.MouseButtonDown, Button = 1 }) is { Button: 0 }, "Button 1 (left) must map to host button 0.");
Require(InputPostSender.ToWire(new MacroEvent { Kind = MacroEventKind.MouseButtonDown, Button = 3 }) is { Button: 2 }, "Button 3 (middle) must map to host button 2.");
Require(InputPostSender.ToWire(new MacroEvent { Kind = MacroEventKind.MouseButtonDown, Button = 0 }) is null, "An invalid mouse button must be dropped.");
Require(InputPostSender.ToWire(new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 300, ScanCode = 400, OffsetMicroseconds = 123 }) is { VirtualKey: 255, ScanCode: 255, OffsetMicroseconds: 123 }, "Key fields must be clamped and offsets preserved.");
var chunkSource = new List<MacroEvent>();
for (var i = 0; i < 4500; i++) chunkSource.Add(E(i * 1000L));
var wireChunks = InputPostSender.ChunkForWire(chunkSource, 2000);
Require(wireChunks.Count == 3 && wireChunks[0].Length == 2000 && wireChunks[1].Length == 2000 && wireChunks[2].Length == 500, "Chunking did not split into the expected sizes.");
Require(wireChunks[0][0].OffsetMicroseconds == 0 && wireChunks[1][0].OffsetMicroseconds == 2_000_000, "Chunk offsets were not preserved as absolute before normalization.");
var expandedGaps = MacroSequenceEditor.ExpandGapsToDelays(editorEvents);
Require(expandedGaps.Count == 5 && expandedGaps[1].Kind == MacroEventKind.Delay && expandedGaps[3].Kind == MacroEventKind.Delay, "Recorded gaps were not expanded into delay rows.");
Require(expandedGaps[0].OffsetMicroseconds == 0 && expandedGaps[1].OffsetMicroseconds == 250_000 && expandedGaps[2].OffsetMicroseconds == 250_000 && expandedGaps[3].OffsetMicroseconds == 1_000_000 && expandedGaps[4].OffsetMicroseconds == 1_000_000, "Expanded delay rows did not preserve the timeline.");
Require(MacroSequenceEditor.TotalDurationMicroseconds(expandedGaps) == MacroSequenceEditor.TotalDurationMicroseconds(editorEvents), "Gap expansion changed the total duration.");
Require(MacroSequenceEditor.ExpandGapsToDelays([E(0), E(0)]).Count == 2, "Zero-length gaps must not produce delay rows.");
Require(MacroSequenceEditor.ExpandGapsToDelays([E(0)]).Count == 1, "A single event must not expand.");
var fakeDispatchCount = 0;
var fakeTarget = new FakeMacroTarget(() => Interlocked.Increment(ref fakeDispatchCount));
var playback = new PlaybackController(fakeTarget);
var playbackMacro = new MacroDefinition { Events = [E(0)] };
var playedOnce = await playback.PlayAsync(playbackMacro, ["account-a"], PlaybackMode.Once, 1, CancellationToken.None);
Require(playedOnce.Started && playedOnce.RunCount == 1 && fakeDispatchCount == 1, "Play once did not run exactly once.");
var playedThrice = await playback.PlayAsync(playbackMacro, ["account-a"], PlaybackMode.Repeat, 3, CancellationToken.None);
Require(playedThrice.Started && playedThrice.RunCount == 3 && fakeDispatchCount == 4, "Play x times did not run the requested count.");
var continuousPlay = playback.PlayAsync(playbackMacro, ["account-a"], PlaybackMode.Continuous, 1, CancellationToken.None);
await Task.Delay(120);
playback.Stop();
var playedContinuous = await continuousPlay;
Require(playedContinuous.Started && playedContinuous.Code == "stopped" && playedContinuous.RunCount >= 1, "Continuous playback did not stop cleanly.");
var noTargets = await playback.PlayAsync(playbackMacro, [], PlaybackMode.Once, 1, CancellationToken.None);
Require(!noTargets.Started && noTargets.Code == "no-targets", "Empty targets were not rejected.");
var continuousGuard = playback.PlayAsync(playbackMacro, ["account-a"], PlaybackMode.WhileHeld, 1, CancellationToken.None);
var busyResult = await playback.PlayAsync(playbackMacro, ["account-a"], PlaybackMode.Once, 1, CancellationToken.None);
Require(!busyResult.Started && busyResult.Code == "busy", "The busy guard did not reject concurrent playback.");
playback.Stop();
await continuousGuard;
Console.WriteLine("Macro playback smoke tests passed.");

file sealed class FakeMacroTarget(Action onDispatch) : IBackgroundMacroTarget
{
    public async Task<MacroDispatchResult> DispatchAsync(string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken)
    {
        onDispatch();
        await Task.Yield();
        return new MacroDispatchResult(true, "ok", "ok", events.Count);
    }
}
