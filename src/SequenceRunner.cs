namespace RamMacros;

public interface IBackgroundMacroTarget
{
    Task<MacroDispatchResult> DispatchAsync(string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken);
}

/// <summary>
/// Optional extension implemented by targets that can preserve the caller's
/// delivery intent on the wire. The base target interface remains unchanged so
/// existing macro targets continue to work.
/// </summary>
public interface IBackgroundMacroIntentTarget
{
    Task<MacroDispatchResult> DispatchAsync(
        string accountId,
        IReadOnlyList<MacroEvent> events,
        PlaybackIntent intent,
        CancellationToken cancellationToken);
}

public sealed record ForegroundSessionResult(bool Accepted, string Code, string Message, string? SessionId = null);

public interface IForegroundMacroTarget
{
    Task<ForegroundSessionResult> OpenForegroundSessionAsync(IReadOnlyList<string> accountIds, CancellationToken cancellationToken);
    Task<ForegroundSessionResult> ActivateForegroundAccountAsync(string sessionId, string accountId, CancellationToken cancellationToken);
    Task<MacroDispatchResult> DispatchForegroundAsync(string sessionId, string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken);
    Task<ForegroundSessionResult> CloseForegroundSessionAsync(string sessionId, bool restoreForeground, CancellationToken cancellationToken);
}

public sealed record MacroDispatchResult(bool Accepted, string Code, string Message, int PostedCount)
{
    /// <summary>
    /// The account that produced this result. This is populated by
    /// <see cref="SequenceRunner"/> and is optional for compatibility with
    /// callers that construct dispatch results themselves.
    /// </summary>
    public string? AccountId { get; init; }

    public string? DeliveryMode { get; init; }
    public string? Verification { get; init; }
    public string? TraceId { get; init; }
    public nint DestinationWindow { get; init; }
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    public bool? SelectedVisible { get; init; }
}

public sealed class SequenceRunner(IBackgroundMacroTarget target)
{
    private readonly IBackgroundMacroTarget _target = target ?? throw new ArgumentNullException(nameof(target));

    public async Task<IReadOnlyList<MacroDispatchResult>> RunSequentialAsync(MacroDefinition macro, IReadOnlyList<string> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(accountIds);
        ValidateAccountIds(accountIds);
        var results = new List<MacroDispatchResult>();
        var events = OrderedEvents(macro);
        foreach (var accountId in accountIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await DispatchOneAsync(accountId, events, PlaybackIntent.BackgroundMessage, cancellationToken));
        }
        return results;
    }

    public async Task<IReadOnlyList<MacroDispatchResult>> RunForegroundAsync(
        MacroDefinition macro,
        IReadOnlyList<string> accountIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(accountIds);
        ValidateAccountIds(accountIds);
        if (_target is not IForegroundMacroTarget foreground)
            return await RunSequentialAsync(macro, accountIds, cancellationToken).ConfigureAwait(false);

        var results = new List<MacroDispatchResult>(accountIds.Count);
        var opened = await foreground.OpenForegroundSessionAsync(accountIds, cancellationToken).ConfigureAwait(false);
        if (!opened.Accepted || string.IsNullOrWhiteSpace(opened.SessionId))
        {
            return accountIds.Select(accountId => new MacroDispatchResult(false, opened.Code, opened.Message, 0) { AccountId = accountId }).ToArray();
        }

        try
        {
            var events = OrderedEvents(macro);
            foreach (var accountId in accountIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activated = await foreground.ActivateForegroundAccountAsync(opened.SessionId, accountId, cancellationToken).ConfigureAwait(false);
                if (!activated.Accepted)
                {
                    results.Add(new MacroDispatchResult(false, activated.Code, activated.Message, 0) { AccountId = accountId });
                    if (activated.Code is "user-takeover" or "focus-denied" or "cancelled") break;
                    continue;
                }
                var dispatched = await DispatchOneForegroundAsync(foreground, opened.SessionId, accountId, events, cancellationToken).ConfigureAwait(false);
                results.Add(dispatched);
                if (!dispatched.Accepted && dispatched.Code is ("user-takeover" or "focus-lost" or "focus-denied" or "cancelled")) break;
            }
            return results;
        }
        finally
        {
            await foreground.CloseForegroundSessionAsync(opened.SessionId, restoreForeground: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches one independent request per account at the same time. The
    /// returned list is always in the caller-provided account order, even when
    /// the target replies out of order.
    /// </summary>
    public Task<IReadOnlyList<MacroDispatchResult>> RunConcurrentAsync(
        MacroDefinition macro,
        IReadOnlyList<string> accountIds,
        CancellationToken cancellationToken,
        PlaybackIntent intent = PlaybackIntent.BackgroundMessage)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(accountIds);
        ValidateAccountIds(accountIds);
        if (accountIds.Count == 0) return Task.FromResult<IReadOnlyList<MacroDispatchResult>>([]);

        var events = OrderedEvents(macro);
        var dispatches = new Task<MacroDispatchResult>[accountIds.Count];
        for (var index = 0; index < accountIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accountId = accountIds[index];
            dispatches[index] = DispatchOneAsync(accountId, events, intent, cancellationToken);
        }
        return AwaitInCallerOrderAsync(dispatches);
    }

    // A descriptive alias for integrations that call this operation a
    // parallel run. Both names deliberately share the same ordering contract.
    public Task<IReadOnlyList<MacroDispatchResult>> RunParallelAsync(
        MacroDefinition macro,
        IReadOnlyList<string> accountIds,
        CancellationToken cancellationToken,
        PlaybackIntent intent = PlaybackIntent.BackgroundMessage) =>
        RunConcurrentAsync(macro, accountIds, cancellationToken, intent);

    public async Task<IReadOnlyList<MacroDispatchResult>> RunRoundRobinAsync(
        MacroDefinition macro, IReadOnlyList<string> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(accountIds);
        ValidateAccountIds(accountIds);
        if (accountIds.Count == 0) return [];
        var results = new List<MacroDispatchResult>();
        var minimumOffset = macro.Events.Count == 0 ? 0 : macro.Events.Min(eventItem => eventItem.OffsetMicroseconds);
        foreach (var (accountId, index) in accountIds.Select((id, index) => (id, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = index == 0 ? 0 : minimumOffset;
            var events = macro.Events.Select(eventItem => eventItem with { OffsetMicroseconds = Math.Max(0, eventItem.OffsetMicroseconds - offset) }).ToArray();
            results.Add(await DispatchOneAsync(accountId, events, PlaybackIntent.BackgroundMessage, cancellationToken));
        }
        return results;
    }

    public async Task<IReadOnlyList<MacroDispatchResult>> RunMultiWindowAsync(
        MacroDefinition macro, IReadOnlyDictionary<string, string> roleToAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(roleToAccount);
        if (!macro.MultiWindow) throw new InvalidOperationException("The macro is not a multi-window recording.");
        if (macro.WindowRoles.Any(role => !roleToAccount.ContainsKey(role)))
            throw new InvalidOperationException("Every recorded window role must be mapped to an account.");
        var grouped = macro.Events.GroupBy(eventItem => eventItem.WindowRole ?? string.Empty)
            .Where(group => group.Key.Length > 0).ToArray();
        var targetIds = grouped.Select(group => roleToAccount[group.Key]).ToArray();
        ValidateAccountIds(targetIds);
        var dispatches = grouped.Select((group, index) => DispatchOneAsync(
            targetIds[index], group.OrderBy(item => item.OffsetMicroseconds).ToArray(),
            PlaybackIntent.BackgroundMessage, cancellationToken)).ToArray();
        return await AwaitInCallerOrderAsync(dispatches);
    }

    private async Task<MacroDispatchResult> DispatchOneAsync(
        string accountId,
        IReadOnlyList<MacroEvent> events,
        PlaybackIntent intent,
        CancellationToken cancellationToken)
    {
        MacroDispatchResult result;
        try
        {
            result = _target is IBackgroundMacroIntentTarget intentTarget
                ? await intentTarget.DispatchAsync(accountId, events, intent, cancellationToken)
                : await _target.DispatchAsync(accountId, events, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a run-level outcome. Do not turn it into a
            // per-account failure while sibling dispatches are being stopped.
            throw;
        }
        catch (Exception ex)
        {
            // Keep the other accounts observable when one dispatch fails. The
            // controller can aggregate this deterministic failure with the
            // successful siblings without losing the exception's message.
            result = new MacroDispatchResult(false, "dispatch-error", ex.Message, 0);
        }
        return result with { AccountId = accountId };
    }

    private static async Task<MacroDispatchResult> DispatchOneForegroundAsync(
        IForegroundMacroTarget target,
        string sessionId,
        string accountId,
        IReadOnlyList<MacroEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await target.DispatchForegroundAsync(sessionId, accountId, events, cancellationToken).ConfigureAwait(false)) with { AccountId = accountId };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new MacroDispatchResult(false, "dispatch-error", ex.Message, 0) { AccountId = accountId }; }
    }

    private static async Task<IReadOnlyList<MacroDispatchResult>> AwaitInCallerOrderAsync(
        IReadOnlyList<Task<MacroDispatchResult>> dispatches)
    {
        // Task.WhenAll retains the order of its input tasks; this is important
        // because a target is intentionally free to complete out of order.
        return await Task.WhenAll(dispatches);
    }

    private static MacroEvent[] OrderedEvents(MacroDefinition macro) =>
        macro.Events.OrderBy(item => item.OffsetMicroseconds).ToArray();

    internal static void ValidateAccountIds(IReadOnlyList<string> accountIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < accountIds.Count; index++)
        {
            var accountId = accountIds[index];
            if (string.IsNullOrWhiteSpace(accountId))
                throw new ArgumentException("Playback targets must have a non-empty account ID.", nameof(accountIds));
            if (!seen.Add(accountId))
                throw new ArgumentException($"The playback target '{accountId}' was selected more than once.", nameof(accountIds));
        }
    }
}
