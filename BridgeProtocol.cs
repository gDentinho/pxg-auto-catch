using System.Buffers.Binary;

namespace PxGCorpseReader;

internal enum BridgeAction : ushort
{
    Ping = 0,
    ProbeBallApi = 1,
    UseBallOnCorpse = 2
}

internal enum BridgeStatus : ushort
{
    Executed = 0,
    RejectedUnsupported = 1,
    Failed = 2,
    IncompatibleClient = 3,
    Busy = 4
}

internal readonly record struct BridgeResponse(
    ulong CommandId,
    BridgeStatus Status,
    int Detail0,
    int Detail1);

internal static class BridgeProtocol
{
    internal const uint Magic = 0x31424350; // "PCB1"
    internal const ushort Version = 9;
    internal const int RequestSize = 32;
    internal const int ResponseSize = 32;

    internal static string PipeName(int pid)
        => $"PxGCorpseBridge.{pid}.v9";

    internal static byte[] SerializeRequest(
        BridgeAction action,
        ulong commandId,
        int x,
        int y,
        int z,
        int argument0)
    {
        var data = new byte[RequestSize];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), (ushort)action);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8, 8), commandId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), x);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20, 4), y);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), z);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), argument0);

        return data;
    }

    internal static BridgeResponse DeserializeResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length != ResponseSize)
            throw new InvalidDataException($"Resposta da bridge com {data.Length} bytes.");

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));

        if (magic != Magic)
            throw new InvalidDataException($"Magic inválido: 0x{magic:X8}");

        if (version != Version)
            throw new InvalidDataException($"Versão da bridge inválida: {version}");

        var status = (BridgeStatus)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        var commandId = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8, 8));
        var detail0 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16, 4));
        var detail1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(20, 4));

        return new BridgeResponse(commandId, status, detail0, detail1);
    }
}

internal readonly record struct BridgeProbeDefinition(
    int Code,
    string Label,
    bool RequiresTile = false);

internal static class BridgeProbeCatalog
{
    internal static readonly BridgeProbeDefinition[] Definitions =
    {
        new(0,  "Lua executor: chunk trivial"),
        new(1,  "_G.g_game"),
        new(2,  "_G.g_map"),
        new(3,  "_G.modules"),
        new(4,  "_G.modules.game_catch"),
        new(5,  "_G.g_gameActions"),
        new(6,  "_G.modules.game_containers"),

        new(10, "g_game.useInventoryItemWith"),
        new(11, "g_game.useWith"),
        new(12, "g_game.use"),
        new(13, "g_game.useInventoryItem"),
        new(14, "g_game.useItemWith"),
        new(15, "g_game.useThing"),
        new(16, "g_game.useItem"),
        new(17, "g_map.getTile"),

        new(20, "g_map.getTile(position) dot-call", true),
        new(21, "g_map:getTile(position) colon-call", true),
        new(22, "tile.getTopUseThing via dot getTile", true),
        new(23, "tile.getTopUseThing via colon getTile", true),
        new(24, "tile.getTopThing via dot getTile", true),
        new(25, "tile.getThings via dot getTile", true),

        new(30, "modules.game_catch.useBall"),
        new(31, "modules.game_catch.throwBall"),
        new(32, "modules.game_catch.catch"),
        new(33, "modules.game_catch.catchPokemon"),
        new(34, "modules.game_catch.startCatch"),
        new(35, "modules.game_catch.doCatch"),
        new(36, "modules.game_catch.sendCatch"),
        new(37, "modules.game_catch.throwPokeball"),
        new(38, "modules.game_catch.usePokeball"),
        new(39, "modules.game_catch.catchCorpse"),
        new(40, "modules.game_catch.onCatch"),
        new(41, "modules.game_catch.onCatchPokemon"),
        new(42, "modules.game_catch.sendBall"),
    };

    internal static string ExplainDetail(int detail)
        => detail switch
        {
            0 => "OK",
            2101 => "LuaInterface/lua_State inválido",
            2102 => "AOB luaL_loadbufferx/lua_pcall não resolveu de forma única",
            2103 => "luaL_loadbufferx falhou",
            2104 => "lua_pcall falhou (predicate ausente/falso ou erro Lua)",
            2105 => "stack Lua ficou desbalanceado",
            2110 => "lua_State mudou durante a janela de estabilidade; ação abortada",
            2111 => "stack Lua mudou durante tentativa de restauração; ação abortada",
            2199 => "exceção nativa durante execução Lua",
            2200 => "probe code desconhecido",
            2401 => "OK; funções Lua resolvidas pelos RVAs fixos validados do build atual",
            5000 => "Ball interna executada",
            5101 => "parâmetros inválidos para UseBallOnCorpse",
            5102 => "executor Lua falhou em UseBallOnCorpse",
            _ => $"detail={detail}"
        };
}
