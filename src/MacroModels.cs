using System.Text.Json;

namespace RamMacros;

public enum MacroEventKind { KeyDown, KeyUp, MouseMove, MouseButtonDown, MouseButtonUp, MouseWheel }

public sealed record MacroEvent
{
    public MacroEventKind Kind { get; init; }
    public long OffsetMicroseconds { get; init; }
    public int VirtualKey { get; init; }
    public int ScanCode { get; init; }
    public bool Extended { get; init; }
    public int Button { get; init; }
    public int WheelDelta { get; init; }
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public string? WindowRole { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Tags { get; init; }
}

public sealed record MacroDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "New macro";
    public string? Description { get; init; }
    public bool MultiWindow { get; init; }
    public int RecordedClientWidth { get; init; }
    public int RecordedClientHeight { get; init; }
    public IReadOnlyList<string> WindowRoles { get; init; } = [];
    public IReadOnlyList<MacroEvent> Events { get; init; } = [];
}

public sealed record MacroBundle
{
    public int FormatMajor { get; init; } = 1;
    public int FormatMinor { get; init; }
    public string ExportedBy { get; init; } = "RAM Macros";
    public DateTime ExportedUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<MacroDefinition> Macros { get; init; } = [];
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}
