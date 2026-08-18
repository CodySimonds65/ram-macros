using System.Windows;

namespace RamMacros;

public partial class App : Application
{
    private PluginClient? _client;
    public ManagedAccountRegistry ManagedAccounts { get; } = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow(ManagedAccounts); MainWindow.Show();
        _client = PluginClient.FromArgs(e.Args);
        if (_client is not null) _ = ConnectHostAsync(_client);
    }
    private static async Task ConnectHostAsync(PluginClient client)
    {
        using var shutdown = new CancellationTokenSource();
        var heartbeat = Task.CompletedTask;
        var accountRefresh = Task.CompletedTask;
        try
        {
            await client.ConnectAsync();
            await client.SendAsync("action.register", new { actionId = "io.github.codysimonds65.ram.macros.run", displayName = "Run RAM macro", description = "Run a named macro on selected managed accounts without focus changes.", argumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"macroId\":{\"type\":\"string\"}}}", requiredCapabilities = new[] { "host.input.background" } });
            heartbeat = SendHeartbeatsAsync(client, shutdown.Token);
            accountRefresh = RefreshAccountsAsync(client, shutdown.Token);
            while (true)
            {
                var envelope = await client.ReceiveAsync(shutdown.Token); if (envelope is null) break;
                if (envelope.Type == "action.invoke")
                    await client.SendAsync("action.result", new { accepted = true, code = "queued", message = "Macro invocation accepted by RAM Macros." }, envelope.RequestId, shutdown.Token);
                else if (envelope.Type == "accounts.result")
                {
                    var accounts = PluginClient.Deserialize<List<ManagedAccountSnapshot>>(envelope.Payload.GetProperty("accounts")) ?? [];
                    ((App)Current).ManagedAccounts.Replace(accounts);
                }
            }
        }
        catch { }
        finally
        {
            shutdown.Cancel();
            try { await Task.WhenAll(heartbeat, accountRefresh); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            await client.DisposeAsync();
        }
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
