namespace RamMacros;

public enum PlaybackMode { Once, Repeat, Continuous, WhileHeld }

public sealed record PlaybackSummary(bool Started, string Code, string Message, int RunCount);

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

    public async Task<PlaybackSummary> PlayAsync(MacroDefinition macro, IReadOnlyList<string> accountIds, PlaybackMode mode, int repeatCount, CancellationToken cancellationToken)
    {
        if (macro is null || macro.Events.Count == 0)
            return new PlaybackSummary(false, "empty-macro", "The macro has no events.", 0);
        if (accountIds is null || accountIds.Count == 0)
            return new PlaybackSummary(false, "no-targets", "Select at least one account to play to.", 0);
        if (!_runGate.Wait(0))
            return new PlaybackSummary(false, "busy", "A macro is already playing.", 0);
        try
        {
            Volatile.Write(ref _playing, 1);
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate) _loopCts = loopCts;
            var runCount = 0;
            var code = "ok";
            var message = "Playback finished.";
            try
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
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
                    var results = await _runner.RunSequentialAsync(macro, accountIds, loopCts.Token);
                    runCount++;
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
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            return new PlaybackSummary(true, code, message, runCount);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _loopCts;
        cts?.Cancel();
    }
}
