namespace RamMacros;

public sealed record DiagnosticEntry(DateTime Utc, string Level, string Message)
{
    public override string ToString() => $"{Utc.ToLocalTime():HH:mm:ss}  [{Level}]  {Message}";
}

public sealed class DiagnosticsLog
{
    private readonly object _gate = new();
    private readonly Queue<DiagnosticEntry> _entries = new();

    public event EventHandler<DiagnosticEntry>? Added;

    public IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void Trace(string message) => Add("trace", message);
    public void Info(string message) => Add("info", message);
    public void Warning(string message) => Add("warning", message);
    public void Error(string message) => Add("error", message);

    private void Add(string level, string message)
    {
        var normalized = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length == 0) return;
        if (normalized.Length > 2_000) normalized = normalized[..2_000];
        var entry = new DiagnosticEntry(DateTime.UtcNow, level, normalized);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > 100) _entries.Dequeue();
        }
        var handlers = Added?.GetInvocationList();
        if (handlers is null) return;
        foreach (EventHandler<DiagnosticEntry> handler in handlers)
        {
            try { handler(this, entry); }
            catch { /* Logging must not fail recording or shutdown. */ }
        }
    }
}
