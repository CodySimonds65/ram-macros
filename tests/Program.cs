using RamMacros;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
var input = new MacroEvent { NormalizedX = 0.5, NormalizedY = 0.25 };
Require(MacroCoordinateMapper.ToClient(input, 100, 200) == (50, 50), "Normalized coordinates were not mapped correctly.");
Require(MacroCoordinateMapper.Normalize(50, 100) > 0.49 && MacroCoordinateMapper.Normalize(50, 100) < 0.51, "Coordinate normalization failed.");
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ramacro");
try
{
    await MacroStore.ExportAsync(path, new MacroBundle { Macros = [new MacroDefinition { Name = "test", Events = [input] }] });
    var loaded = await MacroStore.ImportAsync(path);
    Require(loaded.Macros.Count == 1 && loaded.Macros[0].Events.Count == 1, "Macro bundle round-trip failed.");
}
finally { if (File.Exists(path)) File.Delete(path); }
Console.WriteLine("RAM Macros tests passed.");
