using System.Text.Json;

namespace RamMacros;

internal sealed record WireInput(
    string Kind,
    int VirtualKey,
    int ScanCode,
    bool Extended,
    int Button,
    int WheelDelta,
    double NormalizedX,
    double NormalizedY,
    long OffsetMicroseconds);

public sealed class InputPostSender : IBackgroundMacroTarget
{
    private readonly PluginClient? _client;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<MacroDispatchResult>> _pending = new(StringComparer.Ordinal);
    private const int ChunkSize = 2000;

    public InputPostSender(PluginClient? client) => _client = client;

    public async Task<MacroDispatchResult> DispatchAsync(string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken)
    {
        if (_client is null) return new MacroDispatchResult(false, "no-host", "The launcher plugin host is not connected.", 0);
        var chunks = ChunkForWire(events, ChunkSize);
        if (chunks.Count == 0) return new MacroDispatchResult(false, "invalid-request", "The macro has no playable events.", 0);
        var totalPosted = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[index];
            if (index > 0)
            {
                var firstOffset = chunk[0].OffsetMicroseconds;
                if (firstOffset > 0)
                    chunk = chunk.Select(item => item with { OffsetMicroseconds = Math.Max(0, item.OffsetMicroseconds - firstOffset) }).ToArray();
            }
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<MacroDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending[requestId] = completion;
            try
            {
                await _client.SendAsync("input.post", new { accountId, events = chunk }, requestId, cancellationToken);
                var result = await completion.Task.WaitAsync(cancellationToken);
                totalPosted += result.PostedCount;
                if (!result.Accepted) return result with { PostedCount = totalPosted };
            }
            finally
            {
                lock (_gate) _pending.Remove(requestId);
            }
        }
        return new MacroDispatchResult(true, "ok", "All input was posted.", totalPosted);
    }

    public void HandleResult(string requestId, JsonElement payload)
    {
        TaskCompletionSource<MacroDispatchResult>? completion;
        lock (_gate) _pending.TryGetValue(requestId, out completion);
        if (completion is null) return;
        var accepted = payload.TryGetProperty("accepted", out var acceptedElement) && acceptedElement.ValueKind == JsonValueKind.True;
        var code = payload.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? string.Empty : string.Empty;
        var message = payload.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
        var postedCount = payload.TryGetProperty("postedCount", out var countElement) && countElement.TryGetInt32(out var count) ? count : 0;
        completion.TrySetResult(new MacroDispatchResult(accepted, code, message, postedCount));
    }

    public void ConnectionClosed()
    {
        List<TaskCompletionSource<MacroDispatchResult>> completions;
        lock (_gate)
        {
            completions = _pending.Values.ToList();
            _pending.Clear();
        }
        var failure = new MacroDispatchResult(false, "disconnected", "The launcher plugin host connection was lost.", 0);
        foreach (var completion in completions) completion.TrySetResult(failure);
    }

    internal static WireInput? ToWire(MacroEvent item)
    {
        var kind = item.Kind switch
        {
            MacroEventKind.KeyDown => "KeyDown",
            MacroEventKind.KeyUp => "KeyUp",
            MacroEventKind.MouseMove => "MouseMove",
            MacroEventKind.MouseButtonDown => "MouseButtonDown",
            MacroEventKind.MouseButtonUp => "MouseButtonUp",
            MacroEventKind.MouseWheel => "MouseWheel",
            _ => null
        };
        if (kind is null) return null;
        var button = 0;
        if (item.Kind is MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp)
        {
            if (item.Button is not (1 or 2 or 3)) return null;
            button = item.Button - 1;
        }
        var virtualKey = item.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? Math.Clamp(item.VirtualKey, 1, 255) : 0;
        var scanCode = item.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp ? Math.Clamp(item.ScanCode, 0, 255) : 0;
        var wheelDelta = item.Kind == MacroEventKind.MouseWheel ? Math.Clamp(item.WheelDelta, -120000, 120000) : 0;
        return new WireInput(kind, virtualKey, scanCode, item.Extended, button, wheelDelta, item.NormalizedX, item.NormalizedY, item.OffsetMicroseconds);
    }

    internal static List<WireInput[]> ChunkForWire(IReadOnlyList<MacroEvent> events, int chunkSize)
    {
        var wireEvents = new List<WireInput>(events.Count);
        foreach (var item in events)
        {
            var wire = ToWire(item);
            if (wire is not null) wireEvents.Add(wire);
        }
        var chunks = new List<WireInput[]>();
        for (var i = 0; i < wireEvents.Count; i += chunkSize)
        {
            var chunk = wireEvents.Skip(i).Take(chunkSize).ToArray();
            if (chunk.Length > 0) chunks.Add(chunk);
        }
        return chunks;
    }
}
