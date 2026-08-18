using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamMacros;

public sealed class PluginClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _pluginId;
    private readonly string _token;
    private readonly string _manifestPath;
    private readonly string[] _capabilities;
    public PluginClient(string pipeName, string token, string pluginId, string manifestPath)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _token = token; _pluginId = pluginId; _manifestPath = manifestPath;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        _capabilities = doc.RootElement.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    public static PluginClient? FromArgs(string[] args)
    {
        var values = ParseArgs(args);
        if (!values.TryGetValue("pipe", out var pipe) || !values.TryGetValue("plugin-id", out var id)) return null;
        if (!values.TryGetValue("token", out var token) && values.TryGetValue("token-file", out var tokenFile)) { token = File.ReadAllText(tokenFile).Trim(); try { File.Delete(tokenFile); } catch { } }
        if (string.IsNullOrWhiteSpace(token)) return null;
        var manifest = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        if (!File.Exists(manifest)) manifest = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        return File.Exists(manifest) ? new PluginClient(pipe, token, id, manifest) : null;
    }
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _pipe.ConnectAsync(5000, cancellationToken);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(_manifestPath, cancellationToken))).ToLowerInvariant();
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        await SendAsync("plugin.hello", new { pluginId = _pluginId, token = _token, protocolMajor = 1, protocolMinor = 0, manifestSha256 = hash, declaredCapabilities = _capabilities, processId = Environment.ProcessId, processStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks }, cancellationToken: cancellationToken);
        var response = await ReadAsync(cancellationToken) ?? throw new InvalidDataException("Plugin host closed the handshake.");
        if (!string.Equals(response.Type, "host.accept", StringComparison.Ordinal)) throw new InvalidDataException("Plugin host rejected the handshake.");
    }
    public async Task SendAsync(string type, object payload, string requestId = "", CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(type, requestId, JsonSerializer.SerializeToElement(payload, Json.Options)), Json.Options);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Plugin message too large.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await _writeGate.WaitAsync(cancellationToken);
        try { await _pipe.WriteAsync(header, cancellationToken); await _pipe.WriteAsync(bytes, cancellationToken); await _pipe.FlushAsync(cancellationToken); }
        finally { _writeGate.Release(); }
    }
    public Task<Envelope?> ReceiveAsync(CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);
    public static T? Deserialize<T>(JsonElement payload) => payload.Deserialize<T>(Json.Options);
    public async ValueTask DisposeAsync() { _writeGate.Dispose(); await _pipe.DisposeAsync(); }
    private async Task<Envelope?> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4]; if (!await ReadExactlyAsync(header, cancellationToken)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header); if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Plugin message too large.");
        var bytes = new byte[length]; if (!await ReadExactlyAsync(bytes, cancellationToken)) return null;
        return JsonSerializer.Deserialize<Envelope>(bytes, Json.Options);
    }
    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    { var offset = 0; while (offset < buffer.Length) { var read = await _pipe.ReadAsync(buffer.AsMemory(offset), cancellationToken); if (read == 0) return false; offset += read; } return true; }
    private static Dictionary<string, string> ParseArgs(string[] args)
    { var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); for (var i = 0; i + 1 < args.Length; i++) if (args[i].StartsWith("--", StringComparison.Ordinal)) result[args[i][2..]] = args[++i]; return result; }
    public sealed record Envelope(string Type, string RequestId, JsonElement Payload, int ProtocolMajor = 1, int ProtocolMinor = 0);
    private static class Json { public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }; }
}
