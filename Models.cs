namespace PxGCorpseReader;

internal readonly record struct WorldPos(int X, int Y, int Z)
{
    public bool IsValid =>
        X > 0 && X < 65535 &&
        Y > 0 && Y < 65535 &&
        Z >= 0 && Z <= 15;

    public override string ToString() => $"{X},{Y},{Z}";
}

internal readonly record struct MapCreatureEntry(uint Id, ulong Pointer);

internal sealed class TargetSample
{
    public uint Id { get; init; }
    public ulong CreaturePointer { get; init; }
    public string Name { get; init; } = "";
    public byte HpPercent { get; init; }
    public WorldPos Position { get; init; }
    public bool Resolved { get; init; }
    public string ResolverMode { get; init; } = "";
}

internal sealed class DeathEvent
{
    public DateTime Time { get; init; }
    public uint TargetId { get; init; }
    public ulong TargetPointer { get; init; }
    public string TargetName { get; init; } = "";
    public WorldPos CorpsePosition { get; init; }
    public string Reason { get; init; } = "";
    public byte LastHp { get; init; }
}

internal sealed class CorpseEvent
{
    public DateTime Time { get; init; }
    public uint TargetId { get; init; }
    public ulong TargetPointer { get; init; }
    public string TargetName { get; init; } = "";
    public WorldPos Position { get; init; }
    public ulong ThingPointer { get; init; }
    public ulong ThingVTable { get; init; }
    public int ThingIndex { get; init; } = -1;
    public int ConfirmedAfterMs { get; init; }
    public string Reason { get; init; } = "";
}
