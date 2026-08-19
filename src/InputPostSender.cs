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

public sealed class InputPostSender : IBackgroundMacroTarget, IBackgroundMacroIntentTarget, IForegroundMacroTarget
{
    private readonly PluginClient? _client;
    private readonly Func<string, object, string, CancellationToken, Task>? _sendAsync;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<MacroDispatchResult>> _pending = new(StringComparer.Ordinal);
    private const int ChunkSize = 2000;

    public InputPostSender(PluginClient? client)
    {
        _client = client;
        _sendAsync = client is null ? null : client.SendAsync;
    }

    // Test/integration seam: keeping transport injection here lets concurrent
    // request-correlation tests exercise the real pending-request lifecycle
    // without opening a named pipe.
    private InputPostSender(Func<string, object, string, CancellationToken, Task> sendAsync) => _sendAsync = sendAsync;

    internal static InputPostSender ForTesting(Func<string, object, string, CancellationToken, Task> sendAsync) =>
        new(sendAsync ?? throw new ArgumentNullException(nameof(sendAsync)));

    public async Task<MacroDispatchResult> DispatchAsync(string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken)
    {
        return await DispatchAsync(accountId, events, PlaybackIntent.BackgroundMessage, cancellationToken);
    }

    public async Task<MacroDispatchResult> DispatchAsync(
        string accountId,
        IReadOnlyList<MacroEvent> events,
        PlaybackIntent intent,
        CancellationToken cancellationToken)
    {
        return await DispatchCoreAsync(accountId, null, events, intent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ForegroundSessionResult> OpenForegroundSessionAsync(IReadOnlyList<string> accountIds, CancellationToken cancellationToken)
    {
        if (_client is null) return new(false, "no-host", "The launcher plugin host is not connected.");
        var response = await _client.RequestAsync("input.session.open", new { accountIds = accountIds.ToArray(), purpose = "macro", restoreForeground = true }, cancellationToken).ConfigureAwait(false);
        return ReadSessionResult(response);
    }

    public async Task<ForegroundSessionResult> ActivateForegroundAccountAsync(string sessionId, string accountId, CancellationToken cancellationToken)
    {
        if (_client is null) return new(false, "no-host", "The launcher plugin host is not connected.");
        var response = await _client.RequestAsync("input.session.activate", new { sessionId, accountId }, cancellationToken).ConfigureAwait(false);
        return ReadSessionResult(response);
    }

    public async Task<MacroDispatchResult> DispatchForegroundAsync(string sessionId, string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken) =>
        await DispatchCoreAsync(accountId, sessionId, events, PlaybackIntent.ForegroundReal, cancellationToken).ConfigureAwait(false);

    public async Task<ForegroundSessionResult> CloseForegroundSessionAsync(string sessionId, bool restoreForeground, CancellationToken cancellationToken)
    {
        if (_client is null) return new(false, "no-host", "The launcher plugin host is not connected.");
        var response = await _client.RequestAsync("input.session.close", new { sessionId, restoreForeground, userInitiated = false }, cancellationToken).ConfigureAwait(false);
        return ReadSessionResult(response);
    }

    private async Task<MacroDispatchResult> DispatchCoreAsync(string accountId, string? sessionId,
        IReadOnlyList<MacroEvent> events, PlaybackIntent intent, CancellationToken cancellationToken)
    {
        if (_sendAsync is null) return new MacroDispatchResult(false, "no-host", "The launcher plugin host is not connected.", 0) { AccountId = accountId };
        var chunks = ChunkForWire(events, ChunkSize);
        if (chunks.Count == 0) return new MacroDispatchResult(false, "invalid-request", "The macro has no playable events.", 0) { AccountId = accountId };
        var totalPosted = 0;
        MacroDispatchResult? lastResult = null;
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
            var completion = new TaskCompletionSource<MacroDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = RegisterPending(completion);
            try
            {
                await _sendAsync("input.post", new
                {
                    accountId,
                    events = chunk,
                    sessionId,
                    deliveryIntent = intent switch
                    {
                        PlaybackIntent.ForegroundReal => "foreground-real",
                        PlaybackIntent.BackgroundMessageProbe => "background-message-probe",
                        _ => "background-message"
                    }
                }, requestId, cancellationToken);
                var chunkDurationSeconds = chunk.Length > 0 ? chunk[^1].OffsetMicroseconds / 1_000_000.0 : 0;
                var timeout = TimeSpan.FromSeconds(Math.Max(30, chunkDurationSeconds + 10));
                MacroDispatchResult result;
                try
                {
                    result = await completion.Task.WaitAsync(timeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    return new MacroDispatchResult(false, "timeout", "The launcher host did not reply to the input in time.", totalPosted) { AccountId = accountId };
                }
                lastResult = result;
                totalPosted += result.PostedCount;
                if (!result.Accepted) return result with { AccountId = accountId, PostedCount = totalPosted };
            }
            finally
            {
                lock (_gate) _pending.Remove(requestId);
            }
        }
        return new MacroDispatchResult(true, "ok", "All input was injected through the guarded foreground session.", totalPosted)
        {
            AccountId = accountId,
            DeliveryMode = intent switch
            {
                PlaybackIntent.ForegroundReal => "send-input-session",
                PlaybackIntent.BackgroundMessageProbe => lastResult?.DeliveryMode ?? "post-message-probe",
                _ => "foreground-required"
            },
            Verification = intent == PlaybackIntent.ForegroundReal ? "guarded" : lastResult?.Verification ?? "not-delivered",
            TraceId = lastResult?.TraceId,
            DestinationWindow = lastResult?.DestinationWindow ?? nint.Zero,
            CursorX = lastResult?.CursorX ?? 0,
            CursorY = lastResult?.CursorY ?? 0,
            SelectedVisible = lastResult?.SelectedVisible
        };
    }

    private static ForegroundSessionResult ReadSessionResult(PluginClient.Envelope response)
    {
        if (response.Type != "input.session.result")
            return new(false, "rejected", "The launcher rejected the foreground session request.");
        return PluginClient.Deserialize<ForegroundSessionResult>(response.Payload)
               ?? new(false, "invalid-response", "The launcher returned an invalid foreground session response.");
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
        var deliveryMode = payload.TryGetProperty("deliveryMode", out var modeElement) ? modeElement.GetString() : null;
        var verification = payload.TryGetProperty("verification", out var verificationElement) ? verificationElement.GetString() : null;
        var traceId = payload.TryGetProperty("traceId", out var traceElement) && traceElement.ValueKind != JsonValueKind.Null ? traceElement.GetString() : null;
        var destination = payload.TryGetProperty("targetRenderWindow", out var destinationElement) && destinationElement.TryGetInt64(out var hwnd)
            ? new nint(hwnd) : nint.Zero;
        var cursorX = payload.TryGetProperty("cursorX", out var cursorXElement) && cursorXElement.TryGetInt32(out var x) ? x : 0;
        var cursorY = payload.TryGetProperty("cursorY", out var cursorYElement) && cursorYElement.TryGetInt32(out var y) ? y : 0;
        var selectedVisible = payload.TryGetProperty("selectedVisible", out var selectedVisibleElement) && selectedVisibleElement.ValueKind != JsonValueKind.Null
            ? selectedVisibleElement.GetBoolean() : (bool?)null;
        completion.TrySetResult(new MacroDispatchResult(accepted, code, message, postedCount)
        {
            DeliveryMode = deliveryMode,
            Verification = verification,
            TraceId = traceId,
            DestinationWindow = destination,
            CursorX = cursorX,
            CursorY = cursorY,
            SelectedVisible = selectedVisible
        });
    }

    public void ConnectionClosed()
    {
        FailAllPending(new MacroDispatchResult(false, "disconnected", "The launcher plugin host connection was lost.", 0));
    }

    public void HandleRejected(string detail)
    {
        FailAllPending(new MacroDispatchResult(false, "rejected", $"The launcher host rejected the input request: {detail}", 0));
    }

    private void FailAllPending(MacroDispatchResult failure)
    {
        List<TaskCompletionSource<MacroDispatchResult>> completions;
        lock (_gate)
        {
            completions = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var completion in completions) completion.TrySetResult(failure);
    }

    private string RegisterPending(TaskCompletionSource<MacroDispatchResult> completion)
    {
        lock (_gate)
        {
            string requestId;
            do requestId = Guid.NewGuid().ToString("N");
            while (_pending.ContainsKey(requestId));
            _pending.Add(requestId, completion);
            return requestId;
        }
    }

    internal int PendingRequestCount
    {
        get { lock (_gate) return _pending.Count; }
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
