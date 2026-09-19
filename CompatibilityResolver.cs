namespace PxGCorpseReader;

internal static class CompatibilityResolver
{
    internal const string KnownExeSha256 =
        "9518E6BCD67074FEF4FE812F9BC0BA4A6365B6B8C6F6F40271EBDF62135F8236";

    // Signatures intentionally wildcard RIP-relative displacements/calls.
    // They are not used to bypass protections; they only recover addresses
    // that moved after a normal client rebuild.
    private const string LuaPCallPattern =
        "41 56 41 55 41 54 55 57 56 53 48 83 EC 20 45 31 F6 " +
        "4C 8B 69 10 48 8B 71 28 41 0F B6 AD 91 00 00";

    private const string LuaLoadBufferXPattern =
        "48 83 EC 48 48 8B 44 24 70 48 89 44 24 20 " +
        "48 89 54 24 30 48 8D 15 ?? ?? ?? ?? " +
        "4C 89 44 24 38 4C 8D 44 24 30 " +
        "E8 ?? ?? ?? ?? 48 83 C4 48 C3";

    private const string LuaInterfacePattern =
        "41 56 41 55 41 54 55 57 56 53 48 83 C4 80 " +
        "4C 8B 25 ?? ?? ?? ?? " +
        "48 B8 67 5F 63 68 61 72 74 62 4C 89 E1";

    private const string MapPattern =
        "83 FB 09 75 06 49 83 FC 01 75 22 " +
        "48 8B 05 ?? ?? ?? ?? 8B 80 14 0B 00 00 " +
        "49 39 C4 0F 82 ?? ?? ?? ?? 83 FB 2A";

    // This initialization shape appears for a few globals. We deliberately
    // keep all candidates and let runtime validation identify the Game object.
    private const string GameCandidatePattern =
        "48 8D 0D ?? ?? ?? ?? 48 8D 5C 24 20 " +
        "E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? " +
        "E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? " +
        "48 8B 0D ?? ?? ?? ?? 48 89 DA " +
        "48 C7 44 24 20 00 00 00 00 66 48 0F 6E C0";

    internal static CompatibilityProfile Resolve(
        ProcessMemoryReader reader,
        Action<string>? log = null)
    {
        if (!reader.IsAttached)
            throw new InvalidOperationException("Reader não está conectado.");

        log ??= _ => { };

        string sha = reader.ExeSha256;

        log($"COMPAT_BEGIN sha256={sha}");

        // Current published build: no scanning required, but still validate
        // the live structures before enabling the bridge.
        if (string.Equals(
            sha,
            KnownExeSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            var known = CompatibilityProfile.KnownCurrent(sha);

            if (ValidateProfile(reader, known, out var reason))
            {
                log(
                    $"COMPAT_OK source=known-profile; " +
                    $"game=0x{known.GameRva:X}; map=0x{known.MapPointerRva:X}; " +
                    $"lua_slot=0x{known.LuaInterfaceSlotRva:X}; " +
                    $"pcall=0x{known.LuaPCallRva:X}; " +
                    $"loadbuffer=0x{known.LuaLoadBufferXRva:X}");

                return known;
            }

            log($"COMPAT_KNOWN_PROFILE_RUNTIME_FAIL reason={reason}");
        }

        // A previously auto-resolved build is revalidated every launch. A
        // cached address is never trusted solely because the SHA matches.
        var cached = CompatibilityProfileStore.TryLoad(sha);

        if (cached is not null && cached.Validated)
        {
            if (ValidateProfile(reader, cached, out var reason))
            {
                var accepted = cached.Clone("cache-revalidated");

                log(
                    $"COMPAT_OK source=cache-revalidated; " +
                    $"game=0x{accepted.GameRva:X}; map=0x{accepted.MapPointerRva:X}; " +
                    $"lua_slot=0x{accepted.LuaInterfaceSlotRva:X}; " +
                    $"pcall=0x{accepted.LuaPCallRva:X}; " +
                    $"loadbuffer=0x{accepted.LuaLoadBufferXRva:X}");

                return accepted;
            }

            log($"COMPAT_CACHE_REJECTED reason={reason}");
        }

        var pe = new PeImage(reader.ExePath);

        ulong pcallRva = FindExactlyOne(
            pe,
            LuaPCallPattern,
            "lua_pcall",
            log);

        ulong loadBufferRva = FindExactlyOne(
            pe,
            LuaLoadBufferXPattern,
            "luaL_loadbufferx",
            log);

        ulong luaPatternRva = FindExactlyOne(
            pe,
            LuaInterfacePattern,
            "LuaInterface xref",
            log);

        // Pattern start +14 is: 4C 8B 25 disp32 (7 bytes)
        ulong luaSlotRva = pe.DecodeRipRelativeTargetRva(
            luaPatternRva + 14,
            displacementOffset: 3,
            instructionLength: 7);

        log($"COMPAT_RESOLVE lua_slot=0x{luaSlotRva:X}");

        // In both client generations studied so far, Map* immediately follows
        // LuaInterface by 0x10. Prefer that relationship, but verify it before
        // accepting. If it ever stops being true, fall back to the Map AOB.
        ulong mapRva = luaSlotRva + 0x10;

        if (!ValidateMap(reader, mapRva, out var mapReason))
        {
            log(
                $"COMPAT_MAP_ADJACENT_REJECTED rva=0x{mapRva:X}; " +
                $"reason={mapReason}");

            ulong mapPatternRva = FindExactlyOne(
                pe,
                MapPattern,
                "Map xref",
                log);

            // Pattern start +11 is: 48 8B 05 disp32 (7 bytes)
            mapRva = pe.DecodeRipRelativeTargetRva(
                mapPatternRva + 11,
                displacementOffset: 3,
                instructionLength: 7);
        }

        log($"COMPAT_RESOLVE map=0x{mapRva:X}");

        var gameCandidateHits = pe.FindAllInExecutableSections(
            GameCandidatePattern);

        var gameCandidateRvas = new List<ulong>();

        foreach (var hit in gameCandidateHits)
        {
            try
            {
                // Pattern starts directly on LEA RCX,[RIP+disp32].
                ulong candidate = pe.DecodeRipRelativeTargetRva(
                    hit,
                    displacementOffset: 3,
                    instructionLength: 7);

                if (!gameCandidateRvas.Contains(candidate))
                    gameCandidateRvas.Add(candidate);
            }
            catch
            {
            }
        }

        log(
            $"COMPAT_SCAN Game candidates={gameCandidateRvas.Count}; " +
            $"rvas={string.Join(",", gameCandidateRvas.Select(x => $"0x{x:X}"))}");

        var acceptedGames = new List<ulong>();

        foreach (var candidate in gameCandidateRvas)
        {
            if (ValidateGame(reader, candidate, out var gameReason))
            {
                acceptedGames.Add(candidate);
                log($"COMPAT_GAME_CANDIDATE PASS rva=0x{candidate:X}");
            }
            else
            {
                log(
                    $"COMPAT_GAME_CANDIDATE reject rva=0x{candidate:X}; " +
                    $"reason={gameReason}");
            }
        }

        if (acceptedGames.Count != 1)
        {
            if (acceptedGames.Count == 0)
            {
                throw new InvalidOperationException(
                    "Compatibility Resolver não conseguiu identificar o objeto Game. " +
                    "Entre com o personagem no jogo e tente novamente. Se o cliente " +
                    "mudou a estrutura do Game, a automação continuará bloqueada.");
            }

            throw new InvalidOperationException(
                "Compatibility Resolver encontrou mais de um candidato válido para Game " +
                $"({acceptedGames.Count}). A automação foi bloqueada para evitar usar " +
                "um endereço ambíguo.");
        }

        ulong gameRva = acceptedGames[0];

        var profile = new CompatibilityProfile
        {
            PxgSha256 = sha,
            Source = "signature-scan",
            GameRva = gameRva,
            MapPointerRva = mapRva,
            LuaInterfaceSlotRva = luaSlotRva,
            LuaPCallRva = pcallRva,
            LuaLoadBufferXRva = loadBufferRva,
            Validated = true,
            ValidatedAtUtc = DateTime.UtcNow
        };

        if (!pe.IsExecutableRva(profile.LuaPCallRva) ||
            !pe.IsExecutableRva(profile.LuaLoadBufferXRva) ||
            !pe.IsImageRva(profile.LuaInterfaceSlotRva) ||
            !pe.IsImageRva(profile.GameRva) ||
            !pe.IsImageRva(profile.MapPointerRva))
        {
            throw new InvalidOperationException(
                "Compatibility Resolver obteve RVAs fora das regiões esperadas do PE.");
        }

        if (!ValidateProfile(reader, profile, out var profileReason))
        {
            throw new InvalidOperationException(
                "Compatibility Resolver encontrou endereços, mas a validação runtime " +
                $"falhou: {profileReason}");
        }

        CompatibilityProfileStore.Save(profile);

        log(
            $"COMPAT_AUTO_PROFILE_SAVED sha={sha}; " +
            $"game=0x{profile.GameRva:X}; map=0x{profile.MapPointerRva:X}; " +
            $"lua_slot=0x{profile.LuaInterfaceSlotRva:X}; " +
            $"pcall=0x{profile.LuaPCallRva:X}; " +
            $"loadbuffer=0x{profile.LuaLoadBufferXRva:X}; " +
            $"path={CompatibilityProfileStore.FilePath}");

        return profile;
    }

    private static ulong FindExactlyOne(
        PeImage pe,
        string pattern,
        string label,
        Action<string> log)
    {
        var hits = pe.FindAllInExecutableSections(pattern);

        log(
            $"COMPAT_SCAN {label} hits={hits.Count}; " +
            $"rvas={string.Join(",", hits.Take(12).Select(x => $"0x{x:X}"))}");

        if (hits.Count != 1)
        {
            throw new InvalidOperationException(
                $"Compatibility Resolver esperava 1 assinatura para {label}, " +
                $"mas encontrou {hits.Count}.");
        }

        return hits[0];
    }

    internal static bool ValidateProfile(
        ProcessMemoryReader reader,
        CompatibilityProfile profile,
        out string reason)
    {
        if (!ValidateGame(reader, profile.GameRva, out reason))
            return false;

        if (!ValidateMap(reader, profile.MapPointerRva, out reason))
            return false;

        if (!ValidateLua(reader, profile.LuaInterfaceSlotRva, out reason))
            return false;

        if (profile.LuaPCallRva == 0 ||
            profile.LuaLoadBufferXRva == 0)
        {
            reason = "Lua function RVA zero";
            return false;
        }

        reason = "OK";
        return true;
    }

    private static bool ValidateGame(
        ProcessMemoryReader reader,
        ulong gameRva,
        out string reason)
    {
        try
        {
            ulong game = reader.ModuleBase + gameRva;
            ulong localPlayer = reader.ReadPointer(game);

            if (!ProcessMemoryReader.LooksLikePointer(localPlayer))
            {
                reason = "LocalPlayer inválido/ausente";
                return false;
            }

            if (!reader.TryReadCreature(localPlayer, out var local))
            {
                reason = "LocalPlayer não passou no layout Creature";
                return false;
            }

            if (string.IsNullOrWhiteSpace(local.Name))
            {
                reason = "LocalPlayer sem nome legível";
                return false;
            }

            if (!local.Position.IsValid)
            {
                reason = $"LocalPlayer posição inválida {local.Position}";
                return false;
            }

            uint attacking = reader.ReadUInt32(
                game + ProcessMemoryReader.AttackingCreatureIdOffset);
            uint following = reader.ReadUInt32(game + 0x0C);
            uint controlling = reader.ReadUInt32(game + 0x10);

            if (attacking == uint.MaxValue ||
                following == uint.MaxValue ||
                controlling == uint.MaxValue)
            {
                reason = "Game creature IDs inválidos";
                return false;
            }

            reason = $"player={local.Name}; hp={local.HpPercent}; pos={local.Position}";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool ValidateMap(
        ProcessMemoryReader reader,
        ulong mapPointerRva,
        out string reason)
    {
        try
        {
            ulong map = reader.ReadPointer(
                reader.ModuleBase + mapPointerRva);

            if (!ProcessMemoryReader.LooksLikePointer(map))
            {
                reason = "Map* inválido";
                return false;
            }

            ulong bucketCount = reader.ReadUInt64(
                map + ProcessMemoryReader.MapBucketCountOffset);
            ulong table = reader.ReadPointer(
                map + ProcessMemoryReader.MapBucketsOffset);
            ulong alt = reader.ReadPointer(
                map + ProcessMemoryReader.MapAltListOffset);

            bool bucketView =
                bucketCount > 0 &&
                bucketCount < 1_000_000 &&
                ProcessMemoryReader.LooksLikePointer(table);

            bool listView =
                ProcessMemoryReader.LooksLikePointer(alt);

            if (!bucketView && !listView)
            {
                reason =
                    $"Map collection inválida buckets={bucketCount}; " +
                    $"table=0x{table:X}; alt=0x{alt:X}";
                return false;
            }

            reason = $"map=0x{map:X}; buckets={bucketCount}";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool ValidateLua(
        ProcessMemoryReader reader,
        ulong luaSlotRva,
        out string reason)
    {
        try
        {
            ulong luaInterface = reader.ReadPointer(
                reader.ModuleBase + luaSlotRva);

            if (!ProcessMemoryReader.LooksLikePointer(luaInterface))
            {
                reason = "LuaInterface* inválido";
                return false;
            }

            ulong L = reader.ReadPointer(luaInterface + 0x08);

            if (!ProcessMemoryReader.LooksLikePointer(L))
            {
                reason = "lua_State* inválido";
                return false;
            }

            ulong stackBase = reader.ReadPointer(L + 0x20);
            ulong stackTop = reader.ReadPointer(L + 0x28);
            ulong stackMax = reader.ReadPointer(L + 0x30);

            if (!ProcessMemoryReader.LooksLikePointer(stackBase) ||
                !ProcessMemoryReader.LooksLikePointer(stackTop) ||
                !ProcessMemoryReader.LooksLikePointer(stackMax) ||
                !(stackBase <= stackTop && stackTop <= stackMax) ||
                stackMax - stackBase > 0x01000000UL)
            {
                reason = "lua_State stack inválida";
                return false;
            }

            reason = $"LuaInterface=0x{luaInterface:X}; L=0x{L:X}";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
