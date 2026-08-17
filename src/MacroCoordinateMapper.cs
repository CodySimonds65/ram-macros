namespace RamMacros;

public static class MacroCoordinateMapper
{
    public static (int X, int Y) ToClient(MacroEvent input, int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0) throw new ArgumentOutOfRangeException(nameof(clientWidth));
        return (
            Math.Clamp((int)Math.Round(input.NormalizedX * (clientWidth - 1)), 0, clientWidth - 1),
            Math.Clamp((int)Math.Round(input.NormalizedY * (clientHeight - 1)), 0, clientHeight - 1));
    }

    public static double Normalize(int value, int extent) => extent <= 1 ? 0 : Math.Clamp(value / (double)(extent - 1), 0, 1);
}
