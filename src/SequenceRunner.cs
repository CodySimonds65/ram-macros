namespace RamMacros;

public interface IBackgroundMacroTarget
{
    Task<MacroDispatchResult> DispatchAsync(string accountId, IReadOnlyList<MacroEvent> events, CancellationToken cancellationToken);
}

public sealed record MacroDispatchResult(bool Accepted, string Code, string Message, int PostedCount);

public sealed class SequenceRunner(IBackgroundMacroTarget target)
{
    public async Task<IReadOnlyList<MacroDispatchResult>> RunSequentialAsync(MacroDefinition macro, IReadOnlyList<string> accountIds, CancellationToken cancellationToken)
    {
        var results = new List<MacroDispatchResult>();
        foreach (var accountId in accountIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = 0L;
            var batch = new List<MacroEvent>();
            foreach (var input in macro.Events.OrderBy(item => item.OffsetMicroseconds))
            {
                var delay = input.OffsetMicroseconds - previous;
                if (delay > 0) await Task.Delay(TimeSpan.FromTicks(delay * 10), cancellationToken);
                batch.Add(input);
                previous = input.OffsetMicroseconds;
            }
            results.Add(await target.DispatchAsync(accountId, batch, cancellationToken));
        }
        return results;
    }

    public async Task<IReadOnlyList<MacroDispatchResult>> RunRoundRobinAsync(
        MacroDefinition macro, IReadOnlyList<string> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0) return [];
        var results = new List<MacroDispatchResult>();
        foreach (var (accountId, index) in accountIds.Select((id, index) => (id, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = index == 0 ? 0 : macro.Events.Min(eventItem => eventItem.OffsetMicroseconds);
            var events = macro.Events.Select(eventItem => eventItem with { OffsetMicroseconds = Math.Max(0, eventItem.OffsetMicroseconds - offset) }).ToArray();
            results.Add(await target.DispatchAsync(accountId, events, cancellationToken));
        }
        return results;
    }

    public async Task<IReadOnlyList<MacroDispatchResult>> RunMultiWindowAsync(
        MacroDefinition macro, IReadOnlyDictionary<string, string> roleToAccount, CancellationToken cancellationToken)
    {
        if (!macro.MultiWindow) throw new InvalidOperationException("The macro is not a multi-window recording.");
        if (macro.WindowRoles.Any(role => !roleToAccount.ContainsKey(role)))
            throw new InvalidOperationException("Every recorded window role must be mapped to an account.");
        var grouped = macro.Events.GroupBy(eventItem => eventItem.WindowRole ?? string.Empty)
            .Where(group => group.Key.Length > 0).ToArray();
        var results = new List<MacroDispatchResult>();
        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await target.DispatchAsync(roleToAccount[group.Key], group.OrderBy(item => item.OffsetMicroseconds).ToArray(), cancellationToken));
        }
        return results;
    }
}
