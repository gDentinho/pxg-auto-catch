using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PxGCorpseReader;

internal sealed class ProcessMemoryReader : IDisposable
{
    // Runtime addresses are now supplied by CompatibilityProfile.
    // Structural offsets below remain version-sensitive and are validated
    // before an auto-resolved profile is accepted.

    public const ulong LocalPlayerPointerOffset = 0x00;
    public const ulong AttackingCreatureIdOffset = 0x08;

    public const ulong CreaturePosXOffset = 0x1C;
    public const ulong CreaturePosYOffset = 0x20;
    public const ulong CreaturePosZOffset = 0x24;

    // Creature::name layout previously validated by PxG State Reader:
    // +0x58 = std::string data pointer
    // +0x60 = std::string length
    public const ulong CreatureNameDataOffset = 0x58;
    public const ulong CreatureNameLengthOffset = 0x60;

    public const ulong CreatureHpPercentOffset = 0x3D0;

    public const ulong MapBucketsOffset = 0x380;
    public const ulong MapBucketCountOffset = 0x388;
    public const ulong MapAltListOffset = 0x390;
    public const ulong MapSizeOffset = 0x398;

    public const ulong NodeNextOffset = 0x00;
    public const ulong NodeCreatureIdOffset = 0x08;
    public const ulong NodeCreaturePtrOffset = 0x10;

    private nint _handle;
    private Process? _process;

    public Process Process =>
        _process ?? throw new InvalidOperationException("Not attached.");

    public ulong ModuleBase { get; private set; }

    public CompatibilityProfile? CompatibilityProfile { get; private set; }

    public ulong GameAddress =>
        ModuleBase +
        (CompatibilityProfile?.GameRva
            ?? throw new InvalidOperationException("Compatibilidade não resolvida."));

    public ulong MapPointerAddress =>
        ModuleBase +
        (CompatibilityProfile?.MapPointerRva
            ?? throw new InvalidOperationException("Compatibilidade não resolvida."));

    public bool IsAttached =>
        _process is not null &&
        !_process.HasExited &&
        _handle != 0;

    public string ExePath { get; private set; } = "";
    public string ExeSha256 { get; private set; } = "";
    public bool IsKnownBuild =>
        string.Equals(
            ExeSha256,
            CompatibilityResolver.KnownExeSha256,
            StringComparison.OrdinalIgnoreCase);

    public bool IsCompatibleBuild =>
        CompatibilityProfile is { Validated: true };

    public void Attach(
        string processName = "pxgme",
        Action<string>? compatibilityLog = null)
    {
        Dispose();

        var p = Process.GetProcessesByName(processName)
            .OrderByDescending(x => x.StartTime)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{processName}.exe não está aberto.");

        var main = p.MainModule
            ?? throw new InvalidOperationException("Não foi possível obter o módulo principal.");

        var h = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
            false,
            p.Id);

        if (h == 0)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"OpenProcess falhou. Win32={err}");
        }

        _process = p;
        _handle = h;
        ModuleBase = (ulong)main.BaseAddress.ToInt64();
        ExePath = main.FileName;
        ExeSha256 = ComputeSha256(ExePath);

        try
        {
            CompatibilityProfile =
                CompatibilityResolver.Resolve(
                    this,
                    compatibilityLog);
        }
        catch
        {
            CompatibilityProfile = null;
            throw;
        }
    }

    public uint ReadAttackingCreatureId()
        => ReadUInt32(GameAddress + AttackingCreatureIdOffset);

    public uint ReadFollowingCreatureId()
        => ReadUInt32(GameAddress + 0x0C);

    public uint ReadControllingCreatureId()
        => ReadUInt32(GameAddress + 0x10);

    public ulong ReadLocalPlayerPointer()
        => ReadPointer(GameAddress + LocalPlayerPointerOffset);

    public ulong ReadMapPointer()
        => ReadPointer(MapPointerAddress);

    public bool TryReadCreature(ulong creaturePtr, out TargetSample sample)
    {
        sample = new TargetSample();

        if (!LooksLikePointer(creaturePtr))
            return false;

        try
        {
            var hp = ReadByte(creaturePtr + CreatureHpPercentOffset);
            var x = ReadInt32(creaturePtr + CreaturePosXOffset);
            var y = ReadInt32(creaturePtr + CreaturePosYOffset);
            var z = (int)ReadByte(creaturePtr + CreaturePosZOffset);

            TryReadCreatureName(creaturePtr, out var name);

            sample = new TargetSample
            {
                CreaturePointer = creaturePtr,
                Name = name,
                HpPercent = hp,
                Position = new WorldPos(x, y, z),
                Resolved = true
            };

            return hp <= 100;
        }
        catch
        {
            return false;
        }
    }

    public bool TryResolveCreatureById(uint id, out TargetSample sample)
    {
        sample = new TargetSample { Id = id };

        if (id == 0)
            return false;

        var map = ReadMapPointer();
        if (!LooksLikePointer(map))
            return false;

        if (TryResolveViaAltList(map, id, out var ptr))
        {
            if (TryReadCreature(ptr, out var c))
            {
                sample = new TargetSample
                {
                    Id = id,
                    CreaturePointer = ptr,
                    Name = c.Name,
                    HpPercent = c.HpPercent,
                    Position = c.Position,
                    Resolved = true,
                    ResolverMode = "alt-list"
                };
                return true;
            }
        }

        if (TryResolveViaBuckets(map, id, out ptr))
        {
            if (TryReadCreature(ptr, out var c))
            {
                sample = new TargetSample
                {
                    Id = id,
                    CreaturePointer = ptr,
                    Name = c.Name,
                    HpPercent = c.HpPercent,
                    Position = c.Position,
                    Resolved = true,
                    ResolverMode = "buckets"
                };
                return true;
            }
        }

        return false;
    }

    private bool TryResolveViaAltList(ulong map, uint id, out ulong creaturePtr)
    {
        creaturePtr = 0;

        try
        {
            // Current client getCreatureById():
            // if [Map+0x398] == 0, start at [Map+0x390]
            // node +0x00 = next
            // node +0x08 = Creature ID
            // node +0x10 = Creature pointer/shared_ptr first pointer
            var node = ReadPointer(map + MapAltListOffset);
            var visited = new HashSet<ulong>();

            for (int guard = 0;
                 guard < 25000 && LooksLikePointer(node);
                 guard++)
            {
                if (!visited.Add(node))
                    break;

                var nodeId = ReadUInt32(node + NodeCreatureIdOffset);

                if (nodeId == id)
                {
                    var ptr = ReadPointer(node + NodeCreaturePtrOffset);

                    if (LooksLikePointer(ptr))
                    {
                        creaturePtr = ptr;
                        return true;
                    }

                    return false;
                }

                var next = ReadPointer(node + NodeNextOffset);

                if (next == node)
                    break;

                node = next;
            }
        }
        catch
        {
        }

        return false;
    }

    private bool TryResolveViaBuckets(ulong map, uint id, out ulong creaturePtr)
    {
        creaturePtr = 0;

        try
        {
            // Exact behavior reproduced from the current pxgme.exe:
            //
            // bucketCount = [Map+0x388]
            // bucketIndex = id % bucketCount
            // table       = [Map+0x380]
            // bucketHead  = [table + bucketIndex*8]
            // node        = [bucketHead]
            //
            // A chained node remains in this bucket only while
            // nextNode.Id % bucketCount == bucketIndex.

            var bucketCount = ReadUInt64(map + MapBucketCountOffset);

            if (bucketCount == 0 || bucketCount > 1_000_000)
                return false;

            var table = ReadPointer(map + MapBucketsOffset);

            if (!LooksLikePointer(table))
                return false;

            var bucketIndex = (ulong)id % bucketCount;
            var bucketHead = ReadPointer(table + bucketIndex * 8);

            if (!LooksLikePointer(bucketHead))
                return false;

            var node = ReadPointer(bucketHead);

            if (!LooksLikePointer(node))
                return false;

            var visited = new HashSet<ulong>();

            for (int guard = 0;
                 guard < 25000 && LooksLikePointer(node);
                 guard++)
            {
                if (!visited.Add(node))
                    break;

                var nodeId = ReadUInt32(node + NodeCreatureIdOffset);

                if (nodeId == id)
                {
                    var ptr = ReadPointer(node + NodeCreaturePtrOffset);

                    if (LooksLikePointer(ptr))
                    {
                        creaturePtr = ptr;
                        return true;
                    }

                    return false;
                }

                var next = ReadPointer(node + NodeNextOffset);

                if (!LooksLikePointer(next))
                    return false;

                var nextId = ReadUInt32(next + NodeCreatureIdOffset);

                if (((ulong)nextId % bucketCount) != bucketIndex)
                    return false;

                node = next;
            }
        }
        catch
        {
        }

        return false;
    }

    public IReadOnlyList<MapCreatureEntry> EnumerateMapCreatures(
        int maxCreatures = 25000)
    {
        var found = new Dictionary<uint, ulong>();

        if (maxCreatures <= 0)
            return Array.Empty<MapCreatureEntry>();

        ulong map;

        try
        {
            map = ReadMapPointer();
        }
        catch
        {
            return Array.Empty<MapCreatureEntry>();
        }

        if (!LooksLikePointer(map))
            return Array.Empty<MapCreatureEntry>();

        // The current client exposes both a linked-list style collection and
        // the bucket table used by getCreatureById. We merge both views and
        // deduplicate by Creature ID. Each traversal is guarded because the
        // game can mutate the collection while we are reading it externally.
        TryEnumerateCreatureAltList(map, found, maxCreatures);

        if (found.Count < maxCreatures)
            TryEnumerateCreatureBuckets(map, found, maxCreatures);

        return found
            .Select(x => new MapCreatureEntry(x.Key, x.Value))
            .ToArray();
    }

    private void TryEnumerateCreatureAltList(
        ulong map,
        Dictionary<uint, ulong> found,
        int maxCreatures)
    {
        try
        {
            var node = ReadPointer(map + MapAltListOffset);
            var visited = new HashSet<ulong>();

            for (int guard = 0;
                 guard < maxCreatures &&
                 found.Count < maxCreatures &&
                 LooksLikePointer(node);
                 guard++)
            {
                if (!visited.Add(node))
                    break;

                uint id = ReadUInt32(node + NodeCreatureIdOffset);
                ulong ptr = ReadPointer(node + NodeCreaturePtrOffset);

                if (id != 0 && LooksLikePointer(ptr))
                    found[id] = ptr;

                ulong next = ReadPointer(node + NodeNextOffset);

                if (next == node)
                    break;

                node = next;
            }
        }
        catch
        {
            // A concurrent map mutation can invalidate a node between reads.
            // The bucket pass below can still recover the remaining entries.
        }
    }

    private void TryEnumerateCreatureBuckets(
        ulong map,
        Dictionary<uint, ulong> found,
        int maxCreatures)
    {
        try
        {
            ulong bucketCount = ReadUInt64(map + MapBucketCountOffset);
            ulong table = ReadPointer(map + MapBucketsOffset);

            if (bucketCount == 0 ||
                bucketCount > 1_000_000 ||
                !LooksLikePointer(table))
            {
                return;
            }

            var visitedNodes = new HashSet<ulong>();

            for (ulong bucket = 0;
                 bucket < bucketCount && found.Count < maxCreatures;
                 bucket++)
            {
                ulong bucketHead;

                try
                {
                    bucketHead = ReadPointer(table + bucket * 8UL);
                }
                catch
                {
                    continue;
                }

                if (!LooksLikePointer(bucketHead))
                    continue;

                ulong node;

                try
                {
                    // Current pxgme getCreatureById() has one extra
                    // bucketHead -> node indirection.
                    node = ReadPointer(bucketHead);
                }
                catch
                {
                    continue;
                }

                for (int guard = 0;
                     guard < maxCreatures &&
                     found.Count < maxCreatures &&
                     LooksLikePointer(node);
                     guard++)
                {
                    if (!visitedNodes.Add(node))
                        break;

                    uint id;
                    ulong ptr;

                    try
                    {
                        id = ReadUInt32(node + NodeCreatureIdOffset);
                        ptr = ReadPointer(node + NodeCreaturePtrOffset);
                    }
                    catch
                    {
                        break;
                    }

                    if (id != 0 && LooksLikePointer(ptr))
                        found[id] = ptr;

                    ulong next;

                    try
                    {
                        next = ReadPointer(node + NodeNextOffset);
                    }
                    catch
                    {
                        break;
                    }

                    if (!LooksLikePointer(next) || next == node)
                        break;

                    uint nextId;

                    try
                    {
                        nextId = ReadUInt32(next + NodeCreatureIdOffset);
                    }
                    catch
                    {
                        break;
                    }

                    if (((ulong)nextId % bucketCount) != bucket)
                        break;

                    node = next;
                }
            }
        }
        catch
        {
        }
    }

    public bool TryReadCreatureName(ulong creaturePtr, out string name)
    {
        name = "";

        if (!LooksLikePointer(creaturePtr))
            return false;

        try
        {
            var length = ReadUInt64(creaturePtr + CreatureNameLengthOffset);

            // Character/Pokémon names in PxG are short. A strict upper bound
            // prevents a bad pointer/layout from turning into a large read.
            if (length == 0 || length > 96)
                return false;

            var dataPtr = ReadPointer(creaturePtr + CreatureNameDataOffset);

            byte[] bytes;

            if (LooksLikePointer(dataPtr))
            {
                bytes = ReadBytes(dataPtr, checked((int)length));
            }
            else
            {
                // Defensive SSO fallback. In the layout used by this client,
                // the inline string storage follows pointer+length.
                if (length > 15)
                    return false;

                bytes = ReadBytes(
                    creaturePtr + CreatureNameDataOffset + 0x10,
                    checked((int)length));
            }

            var decoded = Encoding.UTF8
                .GetString(bytes)
                .TrimEnd('\0')
                .Trim();

            if (decoded.Length == 0 || decoded.Length > 96)
                return false;

            if (decoded.Any(char.IsControl))
                return false;

            name = decoded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public byte ReadByte(ulong address)
    {
        var b = ReadBytes(address, 1);
        return b[0];
    }

    public int ReadInt32(ulong address)
        => BitConverter.ToInt32(ReadBytes(address, 4), 0);

    public uint ReadUInt32(ulong address)
        => BitConverter.ToUInt32(ReadBytes(address, 4), 0);

    public ulong ReadUInt64(ulong address)
        => BitConverter.ToUInt64(ReadBytes(address, 8), 0);

    public ulong ReadPointer(ulong address)
        => ReadUInt64(address);

    public byte[] ReadBytes(ulong address, int count)
    {
        if (!IsAttached)
            throw new InvalidOperationException("Reader não está conectado.");

        var buffer = new byte[count];

        if (!NativeMethods.ReadProcessMemory(
            _handle,
            (nint)address,
            buffer,
            (nuint)count,
            out var read) || read != (nuint)count)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"ReadProcessMemory 0x{address:X} len={count} falhou. Win32={err}");
        }

        return buffer;
    }

    public static bool LooksLikePointer(ulong p)
        => p >= 0x10000 && p < 0x0000800000000000UL;

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = 0;
        }

        _process?.Dispose();
        _process = null;

        ModuleBase = 0;
        ExePath = "";
        ExeSha256 = "";
        CompatibilityProfile = null;
    }
}
