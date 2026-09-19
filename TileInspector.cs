namespace PxGCorpseReader;

internal sealed class ThingEntry
{
    public ulong Pointer { get; init; }
    public ulong VTable { get; init; }

    public override string ToString()
        => $"0x{Pointer:X}(vt=0x{VTable:X})";
}

internal sealed class TileSnapshot
{
    public DateTime Time { get; init; }
    public WorldPos Position { get; init; }
    public ulong TilePointer { get; init; }
    public string LookupMode { get; init; } = "";
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    // Layout confirmado estaticamente no build atual:
    // Tile + 0x1C = quantidade de Things (uint8)
    // Tile + 0x1D = ponteiro para array de Thing* (8 bytes por entrada)
    public byte ThingCount { get; init; }
    public ulong ThingArrayPointer { get; init; }
    public List<ThingEntry> Things { get; init; } = new();
}

internal sealed class TileInspector
{
    private readonly ProcessMemoryReader _reader;

    public int RawSnapshotSize { get; set; } = 0x100;

    public TileInspector(ProcessMemoryReader reader)
    {
        _reader = reader;
    }

    public bool TryGetTilePointer(
        WorldPos pos,
        out ulong tilePtr,
        out string mode,
        out string detail)
    {
        tilePtr = 0;
        mode = "";
        detail = "";

        if (!pos.IsValid)
        {
            detail = "posição inválida";
            return false;
        }

        try
        {
            var map = _reader.ReadMapPointer();

            if (!ProcessMemoryReader.LooksLikePointer(map))
            {
                detail = "Map* inválido";
                return false;
            }

            var floor = map + (ulong)pos.Z * 0x38UL;

            int blockX = pos.X >> 5;
            int blockY = pos.Y >> 5;
            int key = blockY * 2047 + blockX;

            int localX = pos.X & 31;
            int localY = pos.Y & 31;
            int tileIndex = (localY << 5) + localX;

            ulong state = _reader.ReadUInt64(floor + 0x18);
            ulong node;

            if (state == 0)
            {
                mode = "list";
                node = _reader.ReadPointer(floor + 0x10);

                var visited = new HashSet<ulong>();

                for (int guard = 0;
                     guard < 25000 && ProcessMemoryReader.LooksLikePointer(node);
                     guard++)
                {
                    if (!visited.Add(node))
                        break;

                    int nodeKey = _reader.ReadInt32(node + 0x08);

                    if (nodeKey == key)
                    {
                        var slot = node + 0x10UL + (ulong)tileIndex * 8UL;
                        tilePtr = _reader.ReadPointer(slot);

                        if (ProcessMemoryReader.LooksLikePointer(tilePtr))
                        {
                            detail =
                                $"floor=0x{floor:X}; key={key}; tileIndex={tileIndex}; slot=0x{slot:X}";
                            return true;
                        }

                        detail = "slot encontrado, Tile* nulo";
                        return false;
                    }

                    var next = _reader.ReadPointer(node);

                    if (next == node)
                        break;

                    node = next;
                }

                detail = $"bloco não encontrado; key={key}";
                return false;
            }

            mode = "hash";

            var bucketCount = _reader.ReadUInt64(floor + 0x08);
            var table = _reader.ReadPointer(floor + 0x00);

            if (bucketCount == 0 || bucketCount > 1_000_000)
            {
                detail = $"bucketCount inválido={bucketCount}";
                return false;
            }

            if (!ProcessMemoryReader.LooksLikePointer(table))
            {
                detail = "bucket table inválida";
                return false;
            }

            var bucketIndex = (ulong)(uint)key % bucketCount;
            var bucketHead = _reader.ReadPointer(table + bucketIndex * 8UL);

            if (!ProcessMemoryReader.LooksLikePointer(bucketHead))
            {
                detail = $"bucketHead inválido idx={bucketIndex}";
                return false;
            }

            node = _reader.ReadPointer(bucketHead);

            if (!ProcessMemoryReader.LooksLikePointer(node))
            {
                detail = $"bucket vazio idx={bucketIndex}";
                return false;
            }

            var seen = new HashSet<ulong>();

            for (int guard = 0;
                 guard < 25000 && ProcessMemoryReader.LooksLikePointer(node);
                 guard++)
            {
                if (!seen.Add(node))
                    break;

                int nodeKey = _reader.ReadInt32(node + 0x08);

                if (nodeKey == key)
                {
                    var slot = node + 0x10UL + (ulong)tileIndex * 8UL;
                    tilePtr = _reader.ReadPointer(slot);

                    if (ProcessMemoryReader.LooksLikePointer(tilePtr))
                    {
                        detail =
                            $"floor=0x{floor:X}; key={key}; bucket={bucketIndex}/{bucketCount}; " +
                            $"tileIndex={tileIndex}; slot=0x{slot:X}";
                        return true;
                    }

                    detail = "slot encontrado, Tile* nulo";
                    return false;
                }

                var next = _reader.ReadPointer(node);

                if (!ProcessMemoryReader.LooksLikePointer(next))
                    break;

                int nextKey = _reader.ReadInt32(next + 0x08);

                if (((ulong)(uint)nextKey % bucketCount) != bucketIndex)
                    break;

                node = next;
            }

            detail = $"bloco não encontrado; key={key}; bucket={bucketIndex}";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public bool TryCapture(
        WorldPos pos,
        out TileSnapshot snapshot,
        out string detail)
    {
        snapshot = new TileSnapshot();
        detail = "";

        if (!TryGetTilePointer(pos, out var tile, out var mode, out var lookupDetail))
        {
            detail = $"getTile falhou: {lookupDetail}";
            return false;
        }

        try
        {
            var raw = _reader.ReadBytes(tile, RawSnapshotSize);

            byte count = _reader.ReadByte(tile + 0x1C);
            ulong thingsPtr = _reader.ReadPointer(tile + 0x1D);

            var things = new List<ThingEntry>();

            if (count > 0)
            {
                if (count > 64)
                {
                    detail = $"ThingCount suspeito={count}; {lookupDetail}";
                    return false;
                }

                if (!ProcessMemoryReader.LooksLikePointer(thingsPtr))
                {
                    detail = $"ThingArray* inválido=0x{thingsPtr:X}; count={count}; {lookupDetail}";
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    ulong ptr = _reader.ReadPointer(thingsPtr + (ulong)i * 8UL);

                    if (!ProcessMemoryReader.LooksLikePointer(ptr))
                        continue;

                    ulong vt = 0;

                    try
                    {
                        vt = _reader.ReadPointer(ptr);
                    }
                    catch
                    {
                    }

                    things.Add(new ThingEntry
                    {
                        Pointer = ptr,
                        VTable = vt
                    });
                }
            }

            snapshot = new TileSnapshot
            {
                Time = DateTime.Now,
                Position = pos,
                TilePointer = tile,
                LookupMode = mode,
                Raw = raw,
                ThingCount = count,
                ThingArrayPointer = thingsPtr,
                Things = things
            };

            detail = lookupDetail;
            return true;
        }
        catch (Exception ex)
        {
            detail = $"snapshot Tile 0x{tile:X} falhou: {ex.Message}";
            return false;
        }
    }

    public IEnumerable<string> Diff(TileSnapshot before, TileSnapshot after)
    {
        yield return
            $"TILE_DIFF pos={before.Position}; before_tile=0x{before.TilePointer:X}; " +
            $"after_tile=0x{after.TilePointer:X}; before_mode={before.LookupMode}; after_mode={after.LookupMode}";

        int len = Math.Min(before.Raw.Length, after.Raw.Length);
        var changed = new List<string>();

        for (int off = 0; off + 8 <= len; off += 8)
        {
            ulong a = BitConverter.ToUInt64(before.Raw, off);
            ulong b = BitConverter.ToUInt64(after.Raw, off);

            if (a != b)
                changed.Add($"0x{off:X}:0x{a:X}->0x{b:X}");
        }

        yield return changed.Count == 0
            ? "TILE_RAW_DIFF none"
            : $"TILE_RAW_DIFF qwords={changed.Count}; {string.Join(",", changed.Take(32))}";

        var beforePtrs = before.Things.Select(x => x.Pointer).ToHashSet();
        var afterPtrs = after.Things.Select(x => x.Pointer).ToHashSet();

        var added = after.Things
            .Where(x => !beforePtrs.Contains(x.Pointer))
            .ToArray();

        var removed = before.Things
            .Where(x => !afterPtrs.Contains(x.Pointer))
            .ToArray();

        yield return
            $"THING_LIST before_count={before.ThingCount}; after_count={after.ThingCount}; " +
            $"before_array=0x{before.ThingArrayPointer:X}; after_array=0x{after.ThingArrayPointer:X}; " +
            $"added={FormatThings(added)}; removed={FormatThings(removed)}";
    }

    public string Describe(TileSnapshot snap)
    {
        return
            $"TILE_SNAPSHOT pos={snap.Position}; tile=0x{snap.TilePointer:X}; " +
            $"mode={snap.LookupMode}; thing_count={snap.ThingCount}; " +
            $"things_ptr=0x{snap.ThingArrayPointer:X}; things=[{FormatThings(snap.Things)}]";
    }

    public ThingEntry[] AddedThings(TileSnapshot before, TileSnapshot after)
    {
        var beforePtrs = before.Things.Select(x => x.Pointer).ToHashSet();

        return after.Things
            .Where(x => !beforePtrs.Contains(x.Pointer))
            .ToArray();
    }

    public ThingEntry[] RemovedThings(TileSnapshot before, TileSnapshot after)
    {
        var afterPtrs = after.Things.Select(x => x.Pointer).ToHashSet();

        return before.Things
            .Where(x => !afterPtrs.Contains(x.Pointer))
            .ToArray();
    }

    private static string FormatThings(IEnumerable<ThingEntry> things)
    {
        var arr = things.Take(32).Select(x => x.ToString()).ToArray();
        return arr.Length == 0 ? "none" : string.Join("|", arr);
    }
}
