using System.IO.Compression;
using System.Text.Json;

namespace RamMacros;

public static class MacroStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task ExportAsync(string path, MacroBundle bundle, CancellationToken cancellationToken = default)
    {
        if (!path.EndsWith(".ramacro", StringComparison.OrdinalIgnoreCase)) path += ".ramacro";
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("bundle.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, bundle, Json, cancellationToken);
    }

    public static async Task<MacroBundle> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 128) throw new InvalidDataException("The macro bundle contains too many files.");
        foreach (var entry in archive.Entries)
        {
            var entryPath = entry.FullName.Replace('\\', '/');
            if (entryPath.Split('/').Any(part => part is "" or "." or "..") || entryPath.Contains(':') ||
                entryPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Macro bundles may contain metadata and preview assets only.");
            if (entry.Length > 10 * 1024 * 1024) throw new InvalidDataException("A macro bundle entry is too large.");
        }
        var bundleEntry = archive.GetEntry("bundle.json") ?? throw new InvalidDataException("The macro bundle has no bundle.json.");
        if (bundleEntry.Length > 10 * 1024 * 1024) throw new InvalidDataException("The macro bundle metadata is too large.");
        await using var entryStream = bundleEntry.Open();
        var bundle = await JsonSerializer.DeserializeAsync<MacroBundle>(entryStream, Json, cancellationToken)
                     ?? throw new InvalidDataException("The macro bundle is invalid.");
        return Migrate(bundle);
    }

    public static MacroBundle Migrate(MacroBundle bundle)
    {
        if (bundle.FormatMajor != 1) throw new InvalidDataException($"Unsupported macro format {bundle.FormatMajor}.");
        return bundle with { FormatMinor = Math.Max(bundle.FormatMinor, 1) };
    }
}
