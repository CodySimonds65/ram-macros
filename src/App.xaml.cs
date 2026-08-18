using System.Windows;

namespace RamMacros;

public partial class App : Application
{
    private PluginClient? _client;
    private InputPostSender? _sender;
    public ManagedAccountRegistry ManagedAccounts { get; } = new();
    public DiagnosticsLog Diagnostics { get; } = new();
    public PlaybackController Playback { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDirectory = PluginClient.DataDirectoryFromArgs(e.Args);
        _client = PluginClient.FromArgs(e.Args);
        _sender = new InputPostSender(_client);
        Playback = new PlaybackController(_sender);
        MainWindow = new MainWindow(ManagedAccounts, Diagnostics, dataDirectory); MainWindow.Show();
        if (_client is not null) _ = ConnectHostAsync(_client, Diagnostics, _sender, Playback);
        else Diagnostics.Info("Running without a launcher host pipe; standalone Roblox recording is available, while managed playback requires the launcher.");
    }
    private static async Task ConnectHostAsync(PluginClient client, DiagnosticsLog diagnostics, InputPostSender sender, PlaybackController playback)
    {
        using var shutdown = new CancellationTokenSource();
        var heartbeat = Task.CompletedTask;
        var accountRefresh = Task.CompletedTask;
        EventHandler<DiagnosticEntry>? forwardDiagnostic = null;
        try
        {
            diagnostics.Info("Connecting to the launcher plugin host...");
            await client.ConnectAsync();
            diagnostics.Info("Launcher plugin host accepted the connection.");
            forwardDiagnostic = (_, entry) => _ = SendDiagnosticAsync(client, entry, shutdown.Token);
            diagnostics.Added += forwardDiagnostic;
            await client.SendAsync("action.register", new { actionId = "io.github.codysimonds65.ram.macros.run", displayName = "Run RAM macro", description = "Run a named macro on selected managed accounts without focus changes.", argumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"macroId\":{\"type\":\"string\"}}}", requiredCapabilities = new[] { "host.input.background" } });
            diagnostics.Info("Registered RAM Macros action bridge.");
            heartbeat = SendHeartbeatsAsync(client, shutdown.Token);
            accountRefresh = RefreshAccountsAsync(client, shutdown.Token);
            while (true)
            {
                var envelope = await client.ReceiveAsync(shutdown.Token); if (envelope is null) break;
                if (envelope.Type == "input.result")
                {
                    sender.HandleResult(envelope.RequestId, envelope.Payload);
                    continue;
                }
                if (envelope.Type == "host.reject")
                {
                    var reason = envelope.Payload.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                    var messageType = envelope.Payload.TryGetProperty("messageType", out var messageTypeElement) ? messageTypeElement.GetString() : null;
                    if (messageType is "input.post" or null)
                    {
                        sender.HandleRejected(reason ?? "the host rejected the request");
                        diagnostics.Error($"Host rejected an input request: {reason ?? "no reason supplied"}");
                    }
                    continue;
                }
                if (envelope.Type == "action.invoke")
                    await client.SendAsync("action.result", new { accepted = true, code = "queued", message = "Macro invocation accepted by RAM Macros." }, envelope.RequestId, shutdown.Token);
                else if (envelope.Type == "accounts.result")
                {
                    var accounts = PluginClient.Deserialize<List<ManagedAccountSnapshot>>(envelope.Payload.GetProperty("accounts")) ?? [];
                    ((App)Current).ManagedAccounts.Replace(accounts);
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Error($"Host connection stopped: {ex.Message}");
        }
        finally
        {
            shutdown.Cancel();
            if (forwardDiagnostic is not null) diagnostics.Added -= forwardDiagnostic;
            sender.ConnectionClosed();
            playback.Stop();
            try { await Task.WhenAll(heartbeat, accountRefresh); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            await client.DisposeAsync();
            diagnostics.Info("Launcher plugin host connection closed.");
        }
    }

    private static async Task SendDiagnosticAsync(PluginClient client, DiagnosticEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await client.SendAsync("diagnostic.log", new { level = entry.Level, message = entry.Message, utc = entry.Utc }, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    private static async Task RefreshAccountsAsync(PluginClient client, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                await client.SendAsync("accounts.list", new { }, Guid.NewGuid().ToString("N"), cancellationToken: cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private static async Task SendHeartbeatsAsync(PluginClient client, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) await client.SendAsync("plugin.heartbeat", new { utc = DateTime.UtcNow }, cancellationToken: cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    protected override void OnExit(ExitEventArgs e) { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); base.OnExit(e); }
}
