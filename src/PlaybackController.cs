namespace RamMacros;

public enum PlaybackMode { Once, Repeat, Continuous, WhileHeld }

public enum PlaybackIntent
{
    BackgroundMessage,
    BackgroundMessageProbe
}

public sealed record PlaybackRunReport(int RunNumber, IReadOnlyList<MacroDispatchResult> Results)
{
    public bool Accepted => Results.Count > 0 && Results.All(result => result.Accepted);
}

public sealed class PlaybackRunCompletedEventArgs(PlaybackRunReport report) : EventArgs
{
    public PlaybackRunReport Report { get; } = report ?? throw new ArgumentNullException(nameof(report));
}

public sealed record PlaybackSummary(bool Started, string Code, string Message, int RunCount)
{
    /// <summary>The intent used for this playback request.</summary>
    public PlaybackIntent Intent { get; init; } = PlaybackIntent.BackgroundMessage;

    /// <summary>
    /// Ordered per-account results from the most recently completed run. A
    /// result's AccountId is populated by SequenceRunner.
    /// </summary>
    public IReadOnlyList<MacroDispatchResult> Results { get; init; } = [];
}

public sealed class PlaybackController
{
    private readonly SequenceRunner _runner;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private int _playing;

    public PlaybackController(IBackgroundMacroTarget target)
    {
        _runner = new SequenceRunner(target);
    }

    public bool IsPlaying => Volatile.Read(ref _playing) != 0;

    public event EventHandler? StateChanged;
    public event EventHandler<PlaybackRunCompletedEventArgs>? RunCompleted;

    public async Task<PlaybackSummary> PlayAsync(
        MacroDefinition macro,
        IReadOnlyList<string> accountIds,
        PlaybackMode mode,
        int repeatCount,
        CancellationToken cancellationToken)
        => await PlayCoreAsync(macro, accountIds, mode, repeatCount, PlaybackIntent.BackgroundMessage, cancellationToken);

    /// <summary>
    /// Replays exactly once with an explicit background-message probe intent.
    /// The result reports posting/acceptance only, not client consumption.
    /// </summary>
    public async Task<PlaybackSummary> PlayProbeAsync(
        MacroDefinition macro,
        IReadOnlyList<string> accountIds,
        CancellationToken cancellationToken = default)
        => await PlayCoreAsync(macro, accountIds, PlaybackMode.Once, 1, PlaybackIntent.BackgroundMessageProbe, cancellationToken);

    private async Task<PlaybackSummary> PlayCoreAsync(
        MacroDefinition macro,
        IReadOnlyList<string> accountIds,
        PlaybackMode mode,
        int repeatCount,
        PlaybackIntent intent,
        CancellationToken cancellationToken)
    {
        if (macro is null || macro.Events.Count == 0)
            return new PlaybackSummary(false, "empty-macro", "The macro has no events.", 0) { Intent = intent };
        if (accountIds is null || accountIds.Count == 0)
            return new PlaybackSummary(false, "no-targets", "Select at least one account to play to.", 0) { Intent = intent };

        // Snapshot targets so account refreshes cannot mutate an active run.
        var targets = accountIds.ToArray();
        var duplicate = targets
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            return new PlaybackSummary(false, "duplicate-targets", $"The account '{duplicate}' is selected more than once.", 0) { Intent = intent };
        if (targets.Any(string.IsNullOrWhiteSpace))
            return new PlaybackSummary(false, "invalid-targets", "Every playback target must have an account ID.", 0) { Intent = intent };
        if (!_runGate.Wait(0))
            return new PlaybackSummary(false, "busy", "A macro is already playing.", 0) { Intent = intent };
        try
        {
            Volatile.Write(ref _playing, 1);
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate) _loopCts = loopCts;
            var runCount = 0;
            var code = "ok";
            var message = "Playback finished.";
            IReadOnlyList<MacroDispatchResult> lastResults = [];
            try
            {
                RaiseStateChanged();
                var runs = mode switch
                {
                    PlaybackMode.Once => 1,
                    PlaybackMode.Repeat => Math.Max(1, repeatCount),
                    _ => 0
                };
                while (true)
                {
                    if (loopCts.IsCancellationRequested)
                    {
                        code = "stopped";
                        message = "Playback stopped.";
                        break;
                    }
                    var results = await _runner.RunConcurrentAsync(macro, targets, loopCts.Token, intent);
                    lastResults = results;
                    runCount++;
                    RaiseRunCompleted(new PlaybackRunReport(runCount, results));
                    var failed = results.FirstOrDefault(result => !result.Accepted);
                    if (failed is not null)
                    {
                        code = failed.Code;
                        message = failed.Message;
                        break;
                    }
                    if (runs > 0 && runCount >= runs) break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                code = "cancelled";
                message = "Playback cancelled.";
            }
            catch (OperationCanceledException)
            {
                code = "stopped";
                message = "Playback stopped.";
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loopCts, loopCts)) _loopCts = null;
                }
                Volatile.Write(ref _playing, 0);
                RaiseStateChanged();
            }
            return new PlaybackSummary(true, code, message, runCount)
            {
                Intent = intent,
                Results = lastResults
            };
        }
        finally
        {
            _runGate.Release();
        }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* observer failures must not alter playback */ }
    }

    private void RaiseRunCompleted(PlaybackRunReport report)
    {
        try { RunCompleted?.Invoke(this, new PlaybackRunCompletedEventArgs(report)); }
        catch { /* observer failures must not alter playback */ }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _loopCts;
        cts?.Cancel();
    }
}
