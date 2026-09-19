using System.Diagnostics;

namespace PxGCorpseReader;

internal sealed class CatchMonitor
{
    private sealed class AmbientCreatureState
    {
        public uint Id { get; init; }
        public ulong Pointer { get; set; }
        public string Name { get; set; } = "";
        public byte LastHp { get; set; } = 100;
        public WorldPos LastValidPos { get; set; }
        public DateTime LastSeenAt { get; set; }
        public DateTime MissingSince { get; set; }
        public TileSnapshot? Baseline { get; set; }
        public DateTime BaselineAt { get; set; }
    }

    private readonly ProcessMemoryReader _reader;
    private readonly TileInspector _tileInspector;

    // Existing explicit-target tracker.
    private uint _trackedId;
    private ulong _trackedPtr;
    private WorldPos _lastValidPos;
    private byte _lastHp = 100;
    private string _trackedName = "";
    private DateTime _lastResolvedAt;
    private DateTime _zeroAttackSince;
    private string _lastResolverMode = "";
    private TileSnapshot? _lastTargetTileSnapshot;
    private DateTime _lastTileSnapshotAt;

    // New area/AoE tracker.
    private readonly object _watchLock = new();
    private HashSet<string> _watchedNames =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, AmbientCreatureState> _ambient = new();
    private DateTime _lastAmbientPollAt;

    // Shared dedupe between explicit target deaths and AoE deaths.
    private readonly Dictionary<uint, DateTime> _recentProbeIds = new();

    public event Action<string>? Log;
    public event Action<TargetSample?>? TargetUpdated;
    public event Action<WorldPos?>? PlayerPositionUpdated;
    public event Action<DeathEvent>? DeathDetected;
    public event Action<CorpseEvent>? CorpseConfirmed;

    public int PollMs { get; set; } = 60;
    public int LostTargetConfirmMs { get; set; } = 900;

    // Scanning the complete Creature map every 60 ms is unnecessary.
    // 120 ms is still fast enough to have a pre-death Tile baseline for AoE.
    public int AmbientPollMs { get; set; } = 120;
    public int AmbientMissingConfirmMs { get; set; } = 180;

    public CatchMonitor(ProcessMemoryReader reader)
    {
        _reader = reader;
        _tileInspector = new TileInspector(reader);
    }

    public void SetWatchedPokemonNames(IEnumerable<string> names)
    {
        var next = names
            .Select(NormalizeName)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_watchLock)
        {
            _watchedNames = next;
        }

        Log?.Invoke(
            $"AREA_WATCH_RULES count={next.Count}; " +
            $"names={(next.Count == 0 ? "none" : string.Join("|", next.OrderBy(x => x)))}");
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"MONITOR ERRO: {ex.Message}");
            }

            try
            {
                await Task.Delay(PollMs, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void Poll()
    {
        var localPtr = _reader.ReadLocalPlayerPointer();

        if (_reader.TryReadCreature(localPtr, out var lp))
            PlayerPositionUpdated?.Invoke(lp.Position);
        else
            PlayerPositionUpdated?.Invoke(null);

        var attackingId = _reader.ReadAttackingCreatureId();

        // Critical change in v0.8:
        // the area tracker runs even while another creature is the explicit
        // attacking target. This is what allows AoE kills around that target
        // to be observed independently.
        PollAmbientCreatures(localPtr, attackingId);

        PollExplicitTarget(attackingId);
    }

    private void PollExplicitTarget(uint attackingId)
    {
        if (attackingId != 0)
        {
            _zeroAttackSince = default;

            if (attackingId != _trackedId)
            {
                _trackedId = attackingId;
                _trackedPtr = 0;
                _lastHp = 100;
                _trackedName = "";
                _lastValidPos = default;
                _lastResolverMode = "";
                _lastTargetTileSnapshot = null;
                _lastTileSnapshotAt = default;

                // If this creature was already being watched as an ambient
                // candidate, explicit target tracking takes ownership.
                _ambient.Remove(attackingId);

                Log?.Invoke(
                    $"TARGET NOVO id={attackingId} hex=0x{attackingId:X8}");
            }

            if (_reader.TryResolveCreatureById(attackingId, out var target))
            {
                _trackedPtr = target.CreaturePointer;
                _lastHp = target.HpPercent;

                if (!string.IsNullOrWhiteSpace(target.Name) &&
                    !string.Equals(
                        _trackedName,
                        target.Name,
                        StringComparison.Ordinal))
                {
                    _trackedName = target.Name;

                    Log?.Invoke(
                        $"TARGET IDENTIFICADO id={attackingId}; " +
                        $"name={_trackedName}; ptr=0x{target.CreaturePointer:X}");
                }

                _lastResolvedAt = DateTime.Now;
                _lastResolverMode = target.ResolverMode;

                if (target.Position.IsValid)
                {
                    _lastValidPos = target.Position;

                    if ((DateTime.Now - _lastTileSnapshotAt)
                        .TotalMilliseconds >= 120)
                    {
                        if (_tileInspector.TryCapture(
                            target.Position,
                            out var tileSnap,
                            out _))
                        {
                            if (tileSnap.Things.Any(
                                x => x.Pointer == target.CreaturePointer))
                            {
                                _lastTargetTileSnapshot = tileSnap;
                                _lastTileSnapshotAt = DateTime.Now;
                            }
                        }
                    }
                }

                TargetUpdated?.Invoke(target);

                if (target.HpPercent == 0)
                {
                    EmitTrackedDeath(
                        "HP_ZERO",
                        target.Position.IsValid
                            ? target.Position
                            : _lastValidPos);
                }
            }
            else
            {
                TargetUpdated?.Invoke(new TargetSample
                {
                    Id = attackingId,
                    Resolved = false
                });
            }

            return;
        }

        TargetUpdated?.Invoke(null);

        if (_trackedId == 0 || _trackedPtr == 0)
            return;

        if (_zeroAttackSince == default)
            _zeroAttackSince = DateTime.Now;

        bool readOk = _reader.TryReadCreature(
            _trackedPtr,
            out var oldTarget);

        if (readOk)
        {
            if (oldTarget.Position.IsValid)
                _lastValidPos = oldTarget.Position;

            _lastHp = oldTarget.HpPercent;

            if (!string.IsNullOrWhiteSpace(oldTarget.Name))
                _trackedName = oldTarget.Name;

            bool invalidPos =
                oldTarget.Position.X == 65535 ||
                oldTarget.Position.Y == 65535 ||
                oldTarget.Position.Z == 255 ||
                !oldTarget.Position.IsValid;

            if (oldTarget.HpPercent == 0 &&
                _lastValidPos.IsValid)
            {
                EmitTrackedDeath(
                    invalidPos
                        ? "HP_ZERO_AND_VISIBILITY_INVALID"
                        : "HP_ZERO_AFTER_TARGET_CLEAR",
                    _lastValidPos);

                return;
            }
        }

        var elapsed =
            (DateTime.Now - _zeroAttackSince).TotalMilliseconds;

        if (elapsed >= LostTargetConfirmMs)
        {
            if (!readOk &&
                _lastHp <= 15 &&
                _lastValidPos.IsValid)
            {
                EmitTrackedDeath(
                    "TARGET_DISAPPEARED_LOW_HP",
                    _lastValidPos);
                return;
            }

            Log?.Invoke(
                $"TARGET LIMPO sem morte confirmada id={_trackedId} " +
                $"lastHp={_lastHp}% resolver={_lastResolverMode}");

            ClearTracked();
        }
    }

    private void PollAmbientCreatures(
        ulong localPlayerPtr,
        uint attackingId)
    {
        var now = DateTime.Now;

        if ((now - _lastAmbientPollAt).TotalMilliseconds < AmbientPollMs)
            return;

        _lastAmbientPollAt = now;

        HashSet<string> watched;

        lock (_watchLock)
        {
            watched = new HashSet<string>(
                _watchedNames,
                StringComparer.OrdinalIgnoreCase);
        }

        if (watched.Count == 0)
        {
            _ambient.Clear();
            return;
        }

        uint controllingId = 0;

        try
        {
            controllingId = _reader.ReadControllingCreatureId();
        }
        catch
        {
        }

        IReadOnlyList<MapCreatureEntry> entries =
            _reader.EnumerateMapCreatures();

        var seen = new HashSet<uint>();

        foreach (var entry in entries)
        {
            if (entry.Id == 0 ||
                !ProcessMemoryReader.LooksLikePointer(entry.Pointer))
            {
                continue;
            }

            // Never treat our character or our summoned Pokémon as a wild
            // catch candidate.
            if (entry.Pointer == localPlayerPtr ||
                entry.Id == controllingId)
            {
                _ambient.Remove(entry.Id);
                continue;
            }

            // Explicit target tracker owns this creature.
            if (entry.Id == attackingId ||
                entry.Id == _trackedId)
            {
                _ambient.Remove(entry.Id);
                continue;
            }

            if (WasRecentlyProbed(entry.Id, now))
            {
                _ambient.Remove(entry.Id);
                continue;
            }

            if (!_reader.TryReadCreature(
                entry.Pointer,
                out var sample))
            {
                continue;
            }

            string name = NormalizeName(sample.Name);

            if (name.Length == 0 ||
                !watched.Contains(name))
            {
                _ambient.Remove(entry.Id);
                continue;
            }

            seen.Add(entry.Id);

            bool isNewAmbient =
                !_ambient.TryGetValue(
                    entry.Id,
                    out var state) ||
                state.Pointer != entry.Pointer;

            if (isNewAmbient)
            {
                // The Creature collection can retain old objects after death.
                // They typically appear as HP=0 at the invalid sentinel
                // 65535,65535,255. Do not create an AoE tracker from those
                // stale first sightings.
                //
                // Important: this filter applies ONLY to the first sighting.
                // A creature that was already tracked while alive may still
                // legitimately transition to HP=0 / invalid position and that
                // transition remains death evidence.
                if (sample.HpPercent == 0 ||
                    !sample.Position.IsValid)
                {
                    _ambient.Remove(entry.Id);
                    continue;
                }

                state = new AmbientCreatureState
                {
                    Id = entry.Id,
                    Pointer = entry.Pointer,
                    Name = name,
                    LastHp = sample.HpPercent,
                    LastValidPos = sample.Position,
                    LastSeenAt = now
                };

                _ambient[entry.Id] = state;

                Log?.Invoke(
                    $"AOE_TRACK_START id={entry.Id}; name={name}; " +
                    $"ptr=0x{entry.Pointer:X}; hp={sample.HpPercent}; " +
                    $"pos={sample.Position}");
            }

            state.Pointer = entry.Pointer;
            state.Name = name;
            state.LastHp = sample.HpPercent;
            state.LastSeenAt = now;
            state.MissingSince = default;

            if (sample.Position.IsValid)
            {
                state.LastValidPos = sample.Position;

                if (state.Baseline is null ||
                    (now - state.BaselineAt).TotalMilliseconds >= 120)
                {
                    if (_tileInspector.TryCapture(
                        sample.Position,
                        out var tileSnap,
                        out _) &&
                        tileSnap.Things.Any(
                            x => x.Pointer == entry.Pointer))
                    {
                        state.Baseline = tileSnap;
                        state.BaselineAt = now;
                    }
                }
            }

            bool invalidPos =
                sample.Position.X == 65535 ||
                sample.Position.Y == 65535 ||
                sample.Position.Z == 255 ||
                !sample.Position.IsValid;

            if (sample.HpPercent == 0 &&
                state.LastValidPos.IsValid)
            {
                Log?.Invoke(
                    $"AOE_DEATH_DETECTED id={state.Id}; " +
                    $"name={state.Name}; hp=0; pos={state.LastValidPos}");

                StartCorpseProbe(
                    state.Id,
                    state.Pointer,
                    state.Name,
                    state.LastHp,
                    state.LastValidPos,
                    state.Baseline,
                    invalidPos
                        ? "AOE_HP_ZERO_AND_VISIBILITY_INVALID"
                        : "AOE_HP_ZERO",
                    "area");

                _ambient.Remove(state.Id);
            }
        }

        // A creature killed by an AoE can disappear from the Creature map
        // between two samples. We do not assume that disappearance == death.
        // We only start a Tile corpse probe after it stays absent briefly;
        // the actual catch still requires the corpse replacement to be
        // confirmed on the same Tile.
        foreach (var pair in _ambient.ToArray())
        {
            var state = pair.Value;

            if (seen.Contains(state.Id))
                continue;

            if (state.Id == attackingId ||
                state.Id == _trackedId)
            {
                _ambient.Remove(state.Id);
                continue;
            }

            if (state.MissingSince == default)
            {
                state.MissingSince = now;
                continue;
            }

            if ((now - state.MissingSince).TotalMilliseconds <
                AmbientMissingConfirmMs)
            {
                continue;
            }

            if (state.LastValidPos.IsValid)
            {
                Log?.Invoke(
                    $"AOE_MAP_DISAPPEARED id={state.Id}; " +
                    $"name={state.Name}; lastHp={state.LastHp}; " +
                    $"pos={state.LastValidPos}");

                StartCorpseProbe(
                    state.Id,
                    state.Pointer,
                    state.Name,
                    state.LastHp,
                    state.LastValidPos,
                    state.Baseline,
                    "AOE_MAP_DISAPPEARED",
                    "area");
            }

            _ambient.Remove(state.Id);
        }
    }

    private void EmitTrackedDeath(
        string reason,
        WorldPos pos)
    {
        var targetId = _trackedId;
        var targetPtr = _trackedPtr;
        var hp = _lastHp;
        var targetName = _trackedName;
        var baseline = _lastTargetTileSnapshot;

        StartCorpseProbe(
            targetId,
            targetPtr,
            targetName,
            hp,
            pos,
            baseline,
            reason,
            "target");

        ClearTracked();
    }

    private void StartCorpseProbe(
        uint targetId,
        ulong targetPtr,
        string targetName,
        byte hp,
        WorldPos pos,
        TileSnapshot? baseline,
        string reason,
        string source)
    {
        if (targetId == 0 ||
            !ProcessMemoryReader.LooksLikePointer(targetPtr) ||
            !pos.IsValid)
        {
            Log?.Invoke(
                $"MORTE CANDIDATA SEM POSICAO VALIDA id={targetId}; " +
                $"name={targetName}; hp={hp}%; pos={pos}; " +
                $"reason={reason}; source={source}");
            return;
        }

        if (!TryMarkProbe(targetId))
        {
            Log?.Invoke(
                $"CORPSE_PROBE_DUPLICATE id={targetId}; " +
                $"name={targetName}; source={source}");
            return;
        }

        DeathDetected?.Invoke(new DeathEvent
        {
            Time = DateTime.Now,
            TargetId = targetId,
            TargetPointer = targetPtr,
            TargetName = targetName,
            CorpsePosition = pos,
            Reason = reason,
            LastHp = hp
        });

        _ = Task.Run(async () =>
        {
            try
            {
                if (baseline is null ||
                    baseline.Position != pos ||
                    !baseline.Things.Any(
                        x => x.Pointer == targetPtr))
                {
                    Log?.Invoke(
                        $"CORPSE PROBE sem baseline confiável id={targetId}; " +
                        $"name={targetName}; pos={pos}; " +
                        $"target_ptr=0x{targetPtr:X}; source={source}");
                    return;
                }

                Log?.Invoke(
                    $"TILE PRE-DEATH source={source}; " +
                    _tileInspector.Describe(baseline));

                var baselinePtrs = baseline.Things
                    .Select(x => x.Pointer)
                    .ToHashSet();

                int targetIndex = baseline.Things.FindIndex(
                    x => x.Pointer == targetPtr);

                ulong targetVTable =
                    targetIndex >= 0
                        ? baseline.Things[targetIndex].VTable
                        : 0;

                var candidateSeen =
                    new Dictionary<ulong, int>();

                var candidateMeta =
                    new Dictionary<ulong, ThingEntry>();

                var candidateIndex =
                    new Dictionary<ulong, int>();

                var sw = Stopwatch.StartNew();
                bool emitted = false;

                foreach (var targetDelay in
                    new[] { 80, 250, 650, 950, 1250 })
                {
                    var remaining =
                        targetDelay - (int)sw.ElapsedMilliseconds;

                    if (remaining > 0)
                        await Task.Delay(remaining);

                    if (!_tileInspector.TryCapture(
                        pos,
                        out var after,
                        out var detail))
                    {
                        Log?.Invoke(
                            $"TILE POST-DEATH +{targetDelay}ms " +
                            $"falhou pos={pos}; {detail}");
                        continue;
                    }

                    Log?.Invoke(
                        $"TILE POST-DEATH +{targetDelay}ms {detail}; " +
                        _tileInspector.Describe(after));

                    foreach (var line in
                        _tileInspector.Diff(baseline, after))
                    {
                        Log?.Invoke(
                            $"+{targetDelay}ms {line}");
                    }

                    bool targetWasPresent =
                        baseline.Things.Any(
                            x => x.Pointer == targetPtr);

                    bool targetStillPresent =
                        after.Things.Any(
                            x => x.Pointer == targetPtr);

                    var added = after.Things
                        .Select((thing, index) => new
                        {
                            Thing = thing,
                            Index = index
                        })
                        .Where(x =>
                            !baselinePtrs.Contains(
                                x.Thing.Pointer))
                        .ToArray();

                    foreach (var item in added)
                    {
                        candidateSeen.TryGetValue(
                            item.Thing.Pointer,
                            out int n);

                        candidateSeen[item.Thing.Pointer] =
                            n + 1;

                        candidateMeta[item.Thing.Pointer] =
                            item.Thing;

                        candidateIndex[item.Thing.Pointer] =
                            item.Index;
                    }

                    var stable = candidateSeen
                        .Where(kv => kv.Value >= 2)
                        .Select(kv =>
                        {
                            var thing = candidateMeta[kv.Key];
                            int index = candidateIndex.TryGetValue(
                                kv.Key,
                                out var idx)
                                    ? idx
                                    : -1;

                            return new
                            {
                                Thing = thing,
                                Index = index,
                                Seen = kv.Value
                            };
                        })
                        .Where(x =>
                            x.Thing.Pointer != targetPtr &&
                            (targetVTable == 0 ||
                             x.Thing.VTable != targetVTable))
                        .ToArray();

                    // When multiple Pokémon die on the same Tile, more than one
                    // new corpse Thing can be stable. Prefer the Thing replacing
                    // the dead Creature at the same stack index. Fall back to
                    // the old "exactly one stable candidate" rule.
                    var preferred = stable
                        .Where(x =>
                            targetIndex >= 0 &&
                            x.Index == targetIndex)
                        .ToArray();

                    var chosen =
                        preferred.Length == 1
                            ? preferred[0]
                            : stable.Length == 1
                                ? stable[0]
                                : null;

                    Log?.Invoke(
                        $"CORPSE_CHECK +{targetDelay}ms " +
                        $"source={source}; " +
                        $"target_before={(targetWasPresent ? 1 : 0)}; " +
                        $"target_after={(targetStillPresent ? 1 : 0)}; " +
                        $"target_index={targetIndex}; " +
                        $"added_now={FormatThings(added.Select(x => x.Thing))}; " +
                        $"stable={FormatThings(stable.Select(x => x.Thing))}; " +
                        $"preferred={(chosen is null ? "none" : $"0x{chosen.Thing.Pointer:X}@{chosen.Index}")}");

                    if (!emitted &&
                        targetWasPresent &&
                        !targetStillPresent &&
                        chosen is not null)
                    {
                        var corpse = chosen.Thing;

                        Log?.Invoke(
                            $"CORPSE CONFIRMADO id={targetId}; " +
                            $"name={targetName}; pos={pos}; " +
                            $"thing=0x{corpse.Pointer:X}; " +
                            $"vtable=0x{corpse.VTable:X}; " +
                            $"thing_index={chosen.Index}; " +
                            $"confirmed_after={targetDelay}ms; " +
                            $"source={source}");

                        CorpseConfirmed?.Invoke(
                            new CorpseEvent
                            {
                                Time = DateTime.Now,
                                TargetId = targetId,
                                TargetPointer = targetPtr,
                                TargetName = targetName,
                                Position = pos,
                                ThingPointer = corpse.Pointer,
                                ThingVTable = corpse.VTable,
                                ThingIndex = chosen.Index,
                                ConfirmedAfterMs = targetDelay,
                                Reason =
                                    "TARGET_REMOVED_AND_NEW_THING_STABLE"
                            });

                        emitted = true;
                    }
                }

                if (!emitted)
                {
                    Log?.Invoke(
                        $"CORPSE NAO CONFIRMADO id={targetId}; " +
                        $"name={targetName}; pos={pos}; source={source}; " +
                        $"candidatos_estaveis=" +
                        string.Join(
                            "|",
                            candidateSeen
                                .Where(x => x.Value >= 2)
                                .Select(
                                    x => $"0x{x.Key:X}:{x.Value}")));
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke(
                    $"CORPSE PROBE ERRO id={targetId}; " +
                    $"name={targetName}; source={source}; " +
                    $"error={ex.Message}");
            }
        });
    }

    private bool TryMarkProbe(uint id)
    {
        var now = DateTime.Now;

        foreach (var old in _recentProbeIds
            .Where(x =>
                (now - x.Value).TotalSeconds > 15)
            .Select(x => x.Key)
            .ToArray())
        {
            _recentProbeIds.Remove(old);
        }

        if (_recentProbeIds.ContainsKey(id))
            return false;

        _recentProbeIds[id] = now;
        return true;
    }

    private bool WasRecentlyProbed(
        uint id,
        DateTime now)
    {
        if (!_recentProbeIds.TryGetValue(
            id,
            out var time))
        {
            return false;
        }

        if ((now - time).TotalSeconds <= 15)
            return true;

        _recentProbeIds.Remove(id);
        return false;
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }

    private static string FormatThings(
        IEnumerable<ThingEntry> things)
    {
        var arr = things
            .Take(16)
            .Select(
                x =>
                    $"0x{x.Pointer:X}" +
                    $"(vt=0x{x.VTable:X})")
            .ToArray();

        return arr.Length == 0
            ? "none"
            : string.Join("|", arr);
    }

    private void ClearTracked()
    {
        _trackedId = 0;
        _trackedPtr = 0;
        _lastValidPos = default;
        _lastHp = 100;
        _trackedName = "";
        _lastResolvedAt = default;
        _zeroAttackSince = default;
        _lastResolverMode = "";
        _lastTargetTileSnapshot = null;
        _lastTileSnapshotAt = default;
    }
}
