using System.Buffers.Binary;
using System.Text;

namespace PxGCorpseReader;

internal sealed class PeImage
{
    internal readonly record struct Section(
        string Name,
        uint VirtualAddress,
        uint VirtualSize,
        uint RawOffset,
        uint RawSize,
        uint Characteristics);

    private readonly byte[] _data;
    private readonly List<Section> _sections = new();

    internal ulong ImageBase { get; }
    internal uint SizeOfImage { get; }

    internal PeImage(string path)
    {
        _data = File.ReadAllBytes(path);

        if (_data.Length < 0x100)
            throw new InvalidDataException("PE muito pequeno.");

        int pe = ReadInt32(0x3C);

        if (pe < 0 || pe + 0x100 > _data.Length)
            throw new InvalidDataException("PE header inválido.");

        if (_data[pe] != (byte)'P' ||
            _data[pe + 1] != (byte)'E' ||
            _data[pe + 2] != 0 ||
            _data[pe + 3] != 0)
        {
            throw new InvalidDataException("Assinatura PE inválida.");
        }

        ushort sectionCount = ReadUInt16(pe + 6);
        ushort optionalSize = ReadUInt16(pe + 20);
        int optional = pe + 24;

        ushort magic = ReadUInt16(optional);

        if (magic != 0x20B)
            throw new InvalidDataException("Somente PE32+ x64 é suportado.");

        ImageBase = ReadUInt64(optional + 24);
        SizeOfImage = ReadUInt32(optional + 56);

        int sectionTable = optional + optionalSize;

        for (int i = 0; i < sectionCount; i++)
        {
            int off = sectionTable + i * 40;

            if (off + 40 > _data.Length)
                break;

            string name = Encoding.ASCII
                .GetString(_data, off, 8)
                .TrimEnd('\0');

            _sections.Add(new Section(
                name,
                ReadUInt32(off + 12),
                ReadUInt32(off + 8),
                ReadUInt32(off + 20),
                ReadUInt32(off + 16),
                ReadUInt32(off + 36)));
        }
    }

    internal IReadOnlyList<ulong> FindAllInExecutableSections(string patternText)
    {
        var pattern = ParsePattern(patternText);
        var results = new List<ulong>();

        int firstConcrete = Array.FindIndex(pattern, x => x >= 0);
        byte firstByte = firstConcrete >= 0
            ? (byte)pattern[firstConcrete]
            : (byte)0;

        foreach (var section in _sections)
        {
            const uint IMAGE_SCN_MEM_EXECUTE = 0x20000000;

            if ((section.Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0)
                continue;

            int raw = checked((int)section.RawOffset);
            int rawSize = checked((int)section.RawSize);

            if (raw < 0 || raw >= _data.Length)
                continue;

            int end = Math.Min(_data.Length, raw + rawSize);

            for (int off = raw; off + pattern.Length <= end; off++)
            {
                if (firstConcrete >= 0 &&
                    _data[off + firstConcrete] != firstByte)
                {
                    continue;
                }

                bool match = true;

                for (int i = 0; i < pattern.Length; i++)
                {
                    int expected = pattern[i];

                    if (expected >= 0 && _data[off + i] != (byte)expected)
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                    continue;

                ulong rva = section.VirtualAddress +
                    (ulong)(off - raw);

                results.Add(rva);
            }
        }

        return results;
    }

    internal bool IsExecutableRva(ulong rva)
    {
        foreach (var section in _sections)
        {
            ulong start = section.VirtualAddress;
            ulong end = start + Math.Max(section.VirtualSize, section.RawSize);

            if (rva < start || rva >= end)
                continue;

            return (section.Characteristics & 0x20000000u) != 0;
        }

        return false;
    }

    internal bool IsImageRva(ulong rva)
        => rva > 0 && rva < SizeOfImage;

    internal ulong DecodeRipRelativeTargetRva(
        ulong instructionRva,
        int displacementOffset,
        int instructionLength)
    {
        int fileOffset = RvaToFileOffset(instructionRva);
        int dispAt = checked(fileOffset + displacementOffset);

        if (dispAt < 0 || dispAt + 4 > _data.Length)
            throw new InvalidDataException("RIP displacement fora do PE.");

        int displacement = BinaryPrimitives.ReadInt32LittleEndian(
            _data.AsSpan(dispAt, 4));

        long target = checked(
            (long)instructionRva +
            instructionLength +
            displacement);

        if (target <= 0)
            throw new InvalidDataException("RIP target inválido.");

        return (ulong)target;
    }

    internal byte[] ReadBytesAtRva(ulong rva, int count)
    {
        int off = RvaToFileOffset(rva);

        if (off < 0 || off + count > _data.Length)
            throw new InvalidDataException("Leitura RVA fora do PE.");

        return _data.AsSpan(off, count).ToArray();
    }

    private int RvaToFileOffset(ulong rva)
    {
        foreach (var section in _sections)
        {
            ulong start = section.VirtualAddress;
            ulong end = start + Math.Max(section.VirtualSize, section.RawSize);

            if (rva < start || rva >= end)
                continue;

            ulong delta = rva - start;

            if (delta >= section.RawSize)
                throw new InvalidDataException("RVA sem bytes raw correspondentes.");

            return checked((int)(section.RawOffset + delta));
        }

        if (rva < 0x1000)
            return checked((int)rva);

        throw new InvalidDataException($"RVA 0x{rva:X} não pertence a uma seção.");
    }

    private static int[] ParsePattern(string text)
    {
        var tokens = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
            throw new ArgumentException("Pattern vazio.");

        var result = new int[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (token is "?" or "??")
            {
                result[i] = -1;
                continue;
            }

            result[i] = Convert.ToInt32(token, 16);
        }

        return result;
    }

    private ushort ReadUInt16(int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, 2));

    private uint ReadUInt32(int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, 4));

    private int ReadInt32(int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4));

    private ulong ReadUInt64(int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset, 8));
}
