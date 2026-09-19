#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cwchar>
#include <initializer_list>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace
{
    constexpr std::uint32_t kMagic = 0x31424350; // PCB1
    constexpr std::uint16_t kVersion = 7;
    constexpr UINT kBridgeMessage = WM_APP + 0x4B1;
    // v7 receives the three Lua RVAs from the external Compatibility Resolver.
    // Defaults preserve the currently validated build so diagnostics remain
    // useful before ConfigureCompatibility is sent.
    std::uintptr_t g_luaInterfaceSlotRva = 0x01104730;
    std::uintptr_t g_luaPCallRva = 0x00A2B380;
    std::uintptr_t g_luaLoadBufferXRva = 0x00A2CA10;
    bool g_compatibilityConfigured = false;

    enum class Action : std::uint16_t
    {
        Ping = 0,
        ConfigureCompatibility = 1,
        ProbeBallApi = 2,
        UseBallOnCorpse = 3
    };

    enum class Status : std::uint16_t
    {
        Executed = 0,
        RejectedUnsupported = 1,
        Failed = 2,
        IncompatibleClient = 3,
        Busy = 4
    };

#pragma pack(push, 1)
    struct Request
    {
        std::uint32_t magic;
        std::uint16_t version;
        std::uint16_t action;
        std::uint64_t commandId;
        std::int32_t x;
        std::int32_t y;
        std::int32_t z;
        std::int32_t argument0;
    };

    struct Response
    {
        std::uint32_t magic;
        std::uint16_t version;
        std::uint16_t status;
        std::uint64_t commandId;
        std::int32_t detail0;
        std::int32_t detail1;
        std::uint64_t reserved;
    };
#pragma pack(pop)

    static_assert(sizeof(Request) == 32);
    static_assert(sizeof(Response) == 32);

    HMODULE g_self = nullptr;
    HWND g_hwnd = nullptr;
    WNDPROC g_oldWndProc = nullptr;

    std::mutex g_actionMutex;
    std::condition_variable g_actionCv;
    bool g_actionPending = false;
    bool g_actionDone = false;
    Request g_pendingRequest{};
    Response g_pendingResponse{};

    using LuaLoadBufferX = int(__fastcall*)(
        void* L,
        const char* buff,
        std::size_t size,
        const char* name,
        const char* mode);

    using LuaPCall = int(__fastcall*)(
        void* L,
        int nargs,
        int nresults,
        int errfunc);

    LuaLoadBufferX g_loadBuffer = nullptr;
    LuaPCall g_pcall = nullptr;
    int g_luaResolutionMode = 0; // 7=externally resolved/validated RVA profile

    bool IsCanonical(std::uintptr_t p)
    {
        return p >= 0x10000ULL && p < 0x0000800000000000ULL;
    }

    std::size_t GetMainModuleSize()
    {
        auto* base = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));

        if (!base)
            return 0;

        auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return 0;

        auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);

        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return 0;

        return nt->OptionalHeader.SizeOfImage;
    }

    bool IsRvaInsideImage(std::uintptr_t rva)
    {
        const auto size = GetMainModuleSize();
        return rva > 0 && size > 0 && rva < size;
    }

    bool IsExecutableRva(std::uintptr_t rva)
    {
        auto* base = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));

        if (!base)
            return false;

        auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);

        auto* section = IMAGE_FIRST_SECTION(nt);

        for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i)
        {
            const auto start = static_cast<std::uintptr_t>(section[i].VirtualAddress);
            const auto span = static_cast<std::uintptr_t>(
                (section[i].Misc.VirtualSize > section[i].SizeOfRawData)
                    ? section[i].Misc.VirtualSize
                    : section[i].SizeOfRawData);
            const auto end = start + span;

            if (rva >= start && rva < end)
                return (section[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;
        }

        return false;
    }

    struct Pattern
    {
        std::vector<int> bytes;
    };

    Pattern MakePattern(std::initializer_list<int> data)
    {
        return Pattern{ std::vector<int>(data) };
    }

    void* FindUniquePattern(const Pattern& pattern)
    {
        auto* base = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));

        if (!base)
            return nullptr;

        auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return nullptr;

        auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);

        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return nullptr;

        const std::size_t size = nt->OptionalHeader.SizeOfImage;
        const std::size_t n = pattern.bytes.size();

        std::uint8_t* hit = nullptr;
        int count = 0;

        for (std::size_t i = 0; i + n <= size; ++i)
        {
            bool ok = true;

            for (std::size_t j = 0; j < n; ++j)
            {
                int expected = pattern.bytes[j];

                if (expected >= 0 && base[i + j] != static_cast<std::uint8_t>(expected))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                hit = base + i;
                ++count;

                if (count > 1)
                    return nullptr;
            }
        }

        return count == 1 ? hit : nullptr;
    }

    bool ResolveLuaFunctions()
    {
        if (g_loadBuffer && g_pcall)
            return true;

        auto base = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

        if (!base)
            return false;

        if (!IsExecutableRva(g_luaPCallRva) ||
            !IsExecutableRva(g_luaLoadBufferXRva))
        {
            return false;
        }

        auto* resolvedPCall =
            reinterpret_cast<void*>(base + g_luaPCallRva);

        auto* resolvedLoadBufferX =
            reinterpret_cast<void*>(base + g_luaLoadBufferXRva);

        if (!IsCanonical(reinterpret_cast<std::uintptr_t>(resolvedPCall)) ||
            !IsCanonical(reinterpret_cast<std::uintptr_t>(resolvedLoadBufferX)))
        {
            return false;
        }

        g_pcall = reinterpret_cast<LuaPCall>(resolvedPCall);
        g_loadBuffer = reinterpret_cast<LuaLoadBufferX>(resolvedLoadBufferX);
        g_luaResolutionMode = 7;
        return true;
    }

    void* ResolveLuaState()
    {
        auto base = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

        if (!base)
            return nullptr;

        auto luaInterface =
            *reinterpret_cast<std::uintptr_t*>(base + g_luaInterfaceSlotRva);

        if (!IsCanonical(luaInterface))
            return nullptr;

        auto L = *reinterpret_cast<std::uintptr_t*>(luaInterface + 0x08);

        if (!IsCanonical(L))
            return nullptr;

        auto stackBase = *reinterpret_cast<std::uintptr_t*>(L + 0x20);
        auto stackTop = *reinterpret_cast<std::uintptr_t*>(L + 0x28);
        auto stackMax = *reinterpret_cast<std::uintptr_t*>(L + 0x30);

        if (!IsCanonical(stackBase) ||
            !IsCanonical(stackTop) ||
            !IsCanonical(stackMax))
        {
            return nullptr;
        }

        if (!(stackBase <= stackTop && stackTop <= stackMax))
            return nullptr;

        if (stackMax - stackBase > 0x01000000ULL)
            return nullptr;

        return reinterpret_cast<void*>(L);
    }

    struct ChunkResult
    {
        bool ok;
        int detail;
    };

    ChunkResult RunChunkDetailed(const std::string& chunk)
    {
        if (!ResolveLuaFunctions())
            return { false, 2102 };

        void* L = ResolveLuaState();

        if (!L)
            return { false, 2101 };

        auto Laddr = reinterpret_cast<std::uintptr_t>(L);
        auto topBefore = *reinterpret_cast<std::uintptr_t*>(Laddr + 0x28);

        __try
        {
            int loadStatus = g_loadBuffer(
                L,
                chunk.c_str(),
                chunk.size(),
                "@PxGCorpseBridge/v0.10.1",
                "t");

            if (loadStatus != 0)
            {
                *reinterpret_cast<std::uintptr_t*>(Laddr + 0x28) = topBefore;
                return { false, 2103 };
            }

            int callStatus = g_pcall(L, 0, 0, 0);

            if (callStatus != 0)
            {
                *reinterpret_cast<std::uintptr_t*>(Laddr + 0x28) = topBefore;
                return { false, 2104 };
            }

            auto topAfter = *reinterpret_cast<std::uintptr_t*>(Laddr + 0x28);

            if (topAfter != topBefore)
            {
                *reinterpret_cast<std::uintptr_t*>(Laddr + 0x28) = topBefore;
                return { false, 2105 };
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            *reinterpret_cast<std::uintptr_t*>(Laddr + 0x28) = topBefore;
            return { false, 2199 };
        }

        return { true, 0 };
    }

    ChunkResult ProbeExpression(const char* expression)
    {
        std::string chunk =
            "if not (" + std::string(expression) +
            ") then error('pcb_probe_false') end";

        return RunChunkDetailed(chunk);
    }

    ChunkResult ProbeTileExpression(
        std::int32_t x,
        std::int32_t y,
        std::int32_t z,
        const std::string& testBody,
        bool colonMapCall)
    {
        char prefix[512]{};

        if (colonMapCall)
        {
            std::snprintf(
                prefix,
                sizeof(prefix),
                "local p={x=%d,y=%d,z=%d};"
                "local t=(g_map and g_map.getTile) and g_map:getTile(p) or nil;",
                x, y, z);
        }
        else
        {
            std::snprintf(
                prefix,
                sizeof(prefix),
                "local p={x=%d,y=%d,z=%d};"
                "local t=(g_map and g_map.getTile) and g_map.getTile(p) or nil;",
                x, y, z);
        }

        std::string chunk = prefix;
        chunk += testBody;

        return RunChunkDetailed(chunk);
    }

    ChunkResult ExecuteProbeCode(
        int code,
        std::int32_t x,
        std::int32_t y,
        std::int32_t z)
    {
        switch (code)
        {
            case 0:
                return RunChunkDetailed("local __pcb_probe_v2=1");

            case 1:
                return ProbeExpression("rawget(_G,'g_game') ~= nil");

            case 2:
                return ProbeExpression("rawget(_G,'g_map') ~= nil");

            case 3:
                return ProbeExpression("rawget(_G,'modules') ~= nil");

            case 4:
                return ProbeExpression(
                    "modules ~= nil and modules.game_catch ~= nil");

            case 5:
                return ProbeExpression("rawget(_G,'g_gameActions') ~= nil");

            case 6:
                return ProbeExpression(
                    "modules ~= nil and modules.game_containers ~= nil");

            case 10:
                return ProbeExpression(
                    "g_game ~= nil and g_game.useInventoryItemWith ~= nil");

            case 11:
                return ProbeExpression(
                    "g_game ~= nil and g_game.useWith ~= nil");

            case 12:
                return ProbeExpression(
                    "g_game ~= nil and g_game.use ~= nil");

            case 13:
                return ProbeExpression(
                    "g_game ~= nil and g_game.useInventoryItem ~= nil");

            case 14:
                return ProbeExpression(
                    "g_game ~= nil and g_game.useItemWith ~= nil");

            case 15:
                return ProbeExpression(
                    "g_game ~= nil and g_game.useThing ~= nil");

            case 16:
                return ProbeExpression(
                    "g_game ~= nil and g_game.useItem ~= nil");

            case 17:
                return ProbeExpression(
                    "g_map ~= nil and g_map.getTile ~= nil");

            case 20:
                return ProbeTileExpression(
                    x, y, z,
                    "if not t then error('tile_missing') end",
                    false);

            case 21:
                return ProbeTileExpression(
                    x, y, z,
                    "if not t then error('tile_missing') end",
                    true);

            case 22:
                return ProbeTileExpression(
                    x, y, z,
                    "if not (t and t.getTopUseThing ~= nil) then error('missing') end",
                    false);

            case 23:
                return ProbeTileExpression(
                    x, y, z,
                    "if not (t and t.getTopUseThing ~= nil) then error('missing') end",
                    true);

            case 24:
                return ProbeTileExpression(
                    x, y, z,
                    "if not (t and t.getTopThing ~= nil) then error('missing') end",
                    false);

            case 25:
                return ProbeTileExpression(
                    x, y, z,
                    "if not (t and t.getThings ~= nil) then error('missing') end",
                    false);

            case 30:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.useBall ~= nil");

            case 31:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.throwBall ~= nil");

            case 32:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.catch ~= nil");

            case 33:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.catchPokemon ~= nil");

            case 34:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.startCatch ~= nil");

            case 35:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.doCatch ~= nil");

            case 36:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.sendCatch ~= nil");

            case 37:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.throwPokeball ~= nil");

            case 38:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.usePokeball ~= nil");

            case 39:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.catchCorpse ~= nil");

            case 40:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.onCatch ~= nil");

            case 41:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.onCatchPokemon ~= nil");

            case 42:
                return ProbeExpression(
                    "modules and modules.game_catch and modules.game_catch.sendBall ~= nil");

            default:
                return { false, 2200 };
        }
    }

    Response ConfigureCompatibility(const Request& request)
    {
        Response response{};
        response.magic = kMagic;
        response.version = kVersion;
        response.commandId = request.commandId;

        const auto luaSlotRva =
            static_cast<std::uintptr_t>(static_cast<std::uint32_t>(request.x));
        const auto pcallRva =
            static_cast<std::uintptr_t>(static_cast<std::uint32_t>(request.y));
        const auto loadBufferRva =
            static_cast<std::uintptr_t>(static_cast<std::uint32_t>(request.z));

        if (!IsRvaInsideImage(luaSlotRva) ||
            !IsExecutableRva(pcallRva) ||
            !IsExecutableRva(loadBufferRva))
        {
            response.status = static_cast<std::uint16_t>(Status::IncompatibleClient);
            response.detail0 = 6101;
            return response;
        }

        // v0.10.0 executed a Lua chunk here as part of the handshake.
        // That introduced a new Lua call immediately when connecting, before
        // the user had requested any catch action. With another injected tool
        // also observing/intercepting the client's Lua path, this can create
        // an avoidable re-entrancy/timing hazard.
        //
        // v7 makes ConfigureCompatibility side-effect free: accept only RVAs
        // already validated by the external Compatibility Resolver and defer
        // the first Lua execution until an actual Probe/UseBall action.
        g_luaInterfaceSlotRva = luaSlotRva;
        g_luaPCallRva = pcallRva;
        g_luaLoadBufferXRva = loadBufferRva;
        g_loadBuffer = nullptr;
        g_pcall = nullptr;
        g_luaResolutionMode = 0;
        g_compatibilityConfigured = true;

        response.status = static_cast<std::uint16_t>(Status::Executed);
        response.detail0 = 6001; // profile applied passively; no Lua executed
        response.detail1 = 7;
        return response;
    }

    Response Execute(const Request& request)
    {
        Response response{};
        response.magic = kMagic;
        response.version = kVersion;
        response.commandId = request.commandId;

        if (request.magic != kMagic || request.version != kVersion)
        {
            response.status = static_cast<std::uint16_t>(Status::Failed);
            response.detail0 = 1001;
            return response;
        }

        switch (static_cast<Action>(request.action))
        {
            case Action::Ping:
                response.status = static_cast<std::uint16_t>(Status::Executed);
                response.detail0 = 1000;
                response.detail1 = g_compatibilityConfigured ? 1 : 0;
                return response;

            case Action::ConfigureCompatibility:
                return ConfigureCompatibility(request);

            case Action::ProbeBallApi:
            {
                const auto result = ExecuteProbeCode(
                    request.argument0,
                    request.x,
                    request.y,
                    request.z);

                response.status = static_cast<std::uint16_t>(
                    result.ok ? Status::Executed : Status::Failed);

                response.detail0 = request.argument0;

                if (result.ok && request.argument0 == 0)
                    response.detail1 = 2406;
                else
                    response.detail1 = result.detail;

                return response;
            }

            case Action::UseBallOnCorpse:
            {
                if (!g_compatibilityConfigured)
                {
                    response.status = static_cast<std::uint16_t>(Status::IncompatibleClient);
                    response.detail0 = 6103;
                    return response;
                }

                const int ballItemId = request.argument0;

                if (request.x <= 0 ||
                    request.x >= 65535 ||
                    request.y <= 0 ||
                    request.y >= 65535 ||
                    request.z < 0 ||
                    request.z > 15 ||
                    ballItemId <= 0 ||
                    ballItemId > 65535)
                {
                    response.status = static_cast<std::uint16_t>(Status::Failed);
                    response.detail0 = 5101;
                    return response;
                }

                char chunk[1536]{};

                // Semantic internal action only:
                //
                // Position -> Tile -> top use Thing -> virtual inventory item by ID.
                //
                // No mouse, keyboard, cursor, screen coordinates or foreground focus.
                std::snprintf(
                    chunk,
                    sizeof(chunk),
                    "local p={x=%d,y=%d,z=%d};"
                    "if not (g_map and g_map.getTile) then error('pcb_map_api_missing') end;"
                    "local t=g_map.getTile(p);"
                    "if not t then error('pcb_tile_missing') end;"
                    "if not t.getTopUseThing then error('pcb_top_use_api_missing') end;"
                    "local q=t:getTopUseThing();"
                    "if not q then error('pcb_top_use_thing_missing') end;"
                    "if not (g_game and g_game.useInventoryItemWith) then "
                    "error('pcb_use_inventory_with_missing') end;"
                    "g_game.useInventoryItemWith(%d,q);",
                    request.x,
                    request.y,
                    request.z,
                    ballItemId);

                const auto result = RunChunkDetailed(chunk);

                if (!result.ok)
                {
                    response.status = static_cast<std::uint16_t>(Status::Failed);
                    response.detail0 = 5102;
                    response.detail1 = result.detail;
                    return response;
                }

                response.status = static_cast<std::uint16_t>(Status::Executed);
                response.detail0 = 5000;
                response.detail1 = ballItemId;
                return response;
            }

            default:
                response.status = static_cast<std::uint16_t>(Status::RejectedUnsupported);
                response.detail0 = 3000;
                return response;
        }
    }

    LRESULT CALLBACK BridgeWndProc(
        HWND hwnd,
        UINT message,
        WPARAM wParam,
        LPARAM lParam)
    {
        if (message == kBridgeMessage)
        {
            Request request{};

            {
                std::lock_guard<std::mutex> lock(g_actionMutex);

                if (!g_actionPending || g_actionDone)
                    return 0;

                request = g_pendingRequest;
            }

            Response response = Execute(request);

            {
                std::lock_guard<std::mutex> lock(g_actionMutex);
                g_pendingResponse = response;
                g_actionDone = true;
            }

            g_actionCv.notify_all();
            return 0;
        }

        return CallWindowProcW(
            g_oldWndProc,
            hwnd,
            message,
            wParam,
            lParam);
    }

    BOOL CALLBACK FindWindowForPid(HWND hwnd, LPARAM lParam)
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);

        if (pid != GetCurrentProcessId())
            return TRUE;

        if (GetWindow(hwnd, GW_OWNER) != nullptr)
            return TRUE;

        if (!IsWindowVisible(hwnd))
            return TRUE;

        *reinterpret_cast<HWND*>(lParam) = hwnd;
        return FALSE;
    }

    bool InstallScheduler()
    {
        HWND hwnd = nullptr;
        EnumWindows(FindWindowForPid, reinterpret_cast<LPARAM>(&hwnd));

        if (!hwnd)
            return false;

        SetLastError(0);

        auto oldProc = reinterpret_cast<WNDPROC>(
            SetWindowLongPtrW(
                hwnd,
                GWLP_WNDPROC,
                reinterpret_cast<LONG_PTR>(&BridgeWndProc)));

        if (!oldProc && GetLastError() != 0)
            return false;

        g_hwnd = hwnd;
        g_oldWndProc = oldProc;
        return true;
    }

    bool ReadExact(HANDLE pipe, void* buffer, DWORD size)
    {
        auto* p = static_cast<std::uint8_t*>(buffer);
        DWORD total = 0;

        while (total < size)
        {
            DWORD got = 0;

            if (!ReadFile(pipe, p + total, size - total, &got, nullptr) || got == 0)
                return false;

            total += got;
        }

        return true;
    }

    bool WriteExact(HANDLE pipe, const void* buffer, DWORD size)
    {
        auto* p = static_cast<const std::uint8_t*>(buffer);
        DWORD total = 0;

        while (total < size)
        {
            DWORD sent = 0;

            if (!WriteFile(pipe, p + total, size - total, &sent, nullptr) || sent == 0)
                return false;

            total += sent;
        }

        return true;
    }

    Response DispatchToWindowThread(const Request& request)
    {
        Response busy{};
        busy.magic = kMagic;
        busy.version = kVersion;
        busy.commandId = request.commandId;
        busy.status = static_cast<std::uint16_t>(Status::Busy);
        busy.detail0 = 4001;

        {
            std::lock_guard<std::mutex> lock(g_actionMutex);

            if (g_actionPending && !g_actionDone)
                return busy;

            g_pendingRequest = request;
            g_pendingResponse = {};
            g_actionPending = true;
            g_actionDone = false;
        }

        if (!PostMessageW(g_hwnd, kBridgeMessage, 0, 0))
        {
            std::lock_guard<std::mutex> lock(g_actionMutex);
            g_actionPending = false;
            g_actionDone = false;

            busy.status = static_cast<std::uint16_t>(Status::Failed);
            busy.detail0 = 4002;
            return busy;
        }

        std::unique_lock<std::mutex> lock(g_actionMutex);

        bool completed = g_actionCv.wait_for(
            lock,
            std::chrono::seconds(2),
            [] { return g_actionDone; });

        if (!completed)
        {
            g_actionPending = false;
            g_actionDone = false;

            busy.status = static_cast<std::uint16_t>(Status::Failed);
            busy.detail0 = 4003;
            return busy;
        }

        Response response = g_pendingResponse;
        g_actionPending = false;
        g_actionDone = false;
        return response;
    }

    void PipeServerLoop()
    {
        wchar_t pipeName[128]{};

        std::swprintf(
            pipeName,
            sizeof(pipeName) / sizeof(pipeName[0]),
            L"\\\\.\\pipe\\PxGCorpseBridge.%lu.v7",
            static_cast<unsigned long>(GetCurrentProcessId()));

        for (;;)
        {
            HANDLE pipe = CreateNamedPipeW(
                pipeName,
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                sizeof(Response),
                sizeof(Request),
                0,
                nullptr);

            if (pipe == INVALID_HANDLE_VALUE)
                return;

            BOOL connected =
                ConnectNamedPipe(pipe, nullptr)
                ? TRUE
                : (GetLastError() == ERROR_PIPE_CONNECTED);

            if (connected)
            {
                Request request{};

                if (ReadExact(pipe, &request, sizeof(request)))
                {
                    Response response = DispatchToWindowThread(request);
                    WriteExact(pipe, &response, sizeof(response));
                    FlushFileBuffers(pipe);
                }
            }

            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
    }

    DWORD WINAPI Bootstrap(LPVOID)
    {
        // Give the game's main window a moment to settle if DLL is loaded during startup.
        for (int i = 0; i < 50 && !InstallScheduler(); ++i)
            Sleep(100);

        if (!g_hwnd || !g_oldWndProc)
            return 1;

        std::thread(PipeServerLoop).detach();
        return 0;
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = instance;
        DisableThreadLibraryCalls(instance);

        HANDLE thread = CreateThread(
            nullptr,
            0,
            Bootstrap,
            nullptr,
            0,
            nullptr);

        if (thread)
            CloseHandle(thread);
    }

    return TRUE;
}
