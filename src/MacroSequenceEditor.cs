namespace RamMacros;

public static class MacroSequenceEditor
{
    public static IReadOnlyList<MacroEvent> SetDelay(IReadOnlyList<MacroEvent> events, int index, int delayMilliseconds)
    {
        if (index < 0 || index >= events.Count) return events;
        var deltas = ToDeltas(events);
        deltas[index] = Math.Max(0, delayMilliseconds) * 1000L;
        return Rebuild(events, deltas);
    }

    public static IReadOnlyList<MacroEvent> Move(IReadOnlyList<MacroEvent> events, int from, int to)
    {
        if (from < 0 || from >= events.Count || to < 0 || to >= events.Count || from == to) return events;
        var deltas = ToDeltas(events);
        var delta = deltas[from];
        deltas.RemoveAt(from);
        deltas.Insert(Math.Min(to, deltas.Count), delta);
        var items = new List<MacroEvent>(events);
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(Math.Min(to, items.Count), item);
        return Rebuild(items, deltas);
    }

    public static IReadOnlyList<MacroEvent> RemoveAt(IReadOnlyList<MacroEvent> events, int index)
    {
        if (index < 0 || index >= events.Count) return events;
        var deltas = ToDeltas(events);
        deltas.RemoveAt(index);
        var items = new List<MacroEvent>(events);
        items.RemoveAt(index);
        return Rebuild(items, deltas);
    }

    public static IReadOnlyList<MacroEvent> Insert(IReadOnlyList<MacroEvent> events, int index, MacroEvent item)
    {
        if (index < 0 || index > events.Count) return events;
        var deltas = ToDeltas(events);
        deltas.Insert(Math.Min(index, deltas.Count), 0);
        var items = events.ToArray();
        Array.Resize(ref items, items.Length + 1);
        Array.Copy(items, index, items, index + 1, items.Length - index - 1);
        items[index] = item;
        return Rebuild(items, deltas);
    }

    public static IReadOnlyList<MacroEvent> UpdateEvent(IReadOnlyList<MacroEvent> events, int index, MacroEvent item)
    {
        if (index < 0 || index >= events.Count) return events;
        var deltas = ToDeltas(events);
        var items = events.ToArray();
        items[index] = item;
        return Rebuild(items, deltas);
    }

    public static IReadOnlyList<MacroEvent> InsertDelay(IReadOnlyList<MacroEvent> events, int afterIndex, int defaultMilliseconds = 500)
    {
        if (events.Count == 0) return Insert(events, 0, new MacroEvent { Kind = MacroEventKind.Delay });
        if (afterIndex < 0 || afterIndex >= events.Count) return events;
        var deltas = ToDeltas(events);
        var items = new List<MacroEvent>(events);
        long gap;
        if (afterIndex + 1 < deltas.Count)
        {
            gap = deltas[afterIndex + 1];
            deltas[afterIndex + 1] = 0;
        }
        else
        {
            gap = Math.Max(0, defaultMilliseconds) * 1000L;
        }
        deltas.Insert(afterIndex + 1, gap);
        items.Insert(afterIndex + 1, new MacroEvent { Kind = MacroEventKind.Delay });
        return Rebuild(items, deltas);
    }

    public static long TotalDurationMicroseconds(IReadOnlyList<MacroEvent> events) =>
        events.Count == 0 ? 0 : events[^1].OffsetMicroseconds;

    private static List<long> ToDeltas(IReadOnlyList<MacroEvent> events)
    {
        var deltas = new List<long>(events.Count);
        long previous = 0;
        foreach (var item in events)
        {
            deltas.Add(Math.Max(0, item.OffsetMicroseconds - previous));
            previous = item.OffsetMicroseconds;
        }
        return deltas;
    }

    private static IReadOnlyList<MacroEvent> Rebuild(IReadOnlyList<MacroEvent> events, List<long> deltas)
    {
        var result = new MacroEvent[events.Count];
        long offset = 0;
        for (var i = 0; i < events.Count; i++)
        {
            offset += deltas[i];
            result[i] = events[i] with { OffsetMicroseconds = offset };
        }
        return result;
    }
}
