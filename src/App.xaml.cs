using System.Windows;

namespace RamMacros;

public partial class App : Application
{
    private PluginClient? _client;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow(); MainWindow.Show();
        _client = PluginClient.FromArgs(e.Args);
        if (_client is not null) _ = ConnectHostAsync(_client);
    }
    private static async Task ConnectHostAsync(PluginClient client)
    {
        try
        {
            await client.ConnectAsync();
            await client.SendAsync("action.register", new { actionId = "io.github.codysimonds65.ram.macros.run", displayName = "Run RAM macro", description = "Run a named macro on selected managed accounts without focus changes.", argumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"macroId\":{\"type\":\"string\"}}}", requiredCapabilities = new[] { "host.input.background" } });
            using var shutdown = new CancellationTokenSource();
            while (true)
            {
                var envelope = await client.ReceiveAsync(shutdown.Token); if (envelope is null) break;
                if (envelope.Type == "action.invoke")
                    await client.SendAsync("action.result", new { accepted = true, code = "queued", message = "Macro invocation accepted by RAM Macros." }, envelope.RequestId, shutdown.Token);
            }
        }
        catch { await client.DisposeAsync(); }
    }
    protected override void OnExit(ExitEventArgs e) { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); base.OnExit(e); }
}
