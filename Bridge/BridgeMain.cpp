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
    constexpr std::uint16_t kVersion = 9;
    constexpr UINT kBridgeMessage = WM_APP + 0x4B1;
    constexpr std::uintptr_t kLuaInterfaceSlotRva = 0x01104730;

    // Current pxgme.exe SHA-256:
    // 9518E6BCD67074FEF4FE812F9BC0BA4A6365B6B8C6F6F40271EBDF62135F8236
    //
    // These RVAs were recovered directly from the on-disk PE image.
    // They are used only as a fallback because another loaded component may
    // have patched the in-memory prologue, making an AOB scan miss it.
    constexpr std::uintptr_t kLuaPCallRva = 0x00A2B380;
    constexpr std::uintptr_t kLuaLoadBufferXRva = 0x00A2CA10;

    enum class Action : std::uint16_t
    {
        Ping = 0,
        ProbeBallApi = 1,
        UseBallOnCorpse = 2
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

    // The bridge never subclasses the PxG window in v9. MacroHelpers also
    // lives inside the same process, so chaining multiple third-party WndProc
    // hooks is an unnecessary shared-state risk. A window timer is used only
    // to marshal the request onto the PxG UI thread.
    std::atomic<UINT_PTR> g_dispatchTimerId{ 0 };

    std::mutex g_actionMutex;
    std::mutex g_luaMutex;
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
    int g_luaResolutionMode = 0; // 4=validated fixed RVA pair for current build

    bool IsCanonical(std::uintptr_t p)
    {
        return p >= 0x10000ULL && p < 0x0000800000000000ULL;
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

        // Current validated pxgme.exe:
        // SHA-256
        // 9518E6BCD67074FEF4FE812F9BC0BA4A6365B6B8C6F6F40271EBDF62135F8236
        //
        // Important:
        // 0x00A2C230 is lua_loadx, NOT luaL_loadbufferx.
        // lua_loadx expects a lua_Reader callback as its second parameter.
        // Passing a source buffer there causes an access violation.
        //
        // Correct pair for this build:
        // lua_pcall        = moduleBase + 0x00A2B380
        // luaL_loadbufferx = moduleBase + 0x00A2CA10
        //
        // The external Reader refuses to load this bridge unless the pxgme SHA
        // is the validated build, so these RVAs are not used on unknown clients.
        auto base = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

        if (!base)
            return false;

        auto* fixedPCall =
            reinterpret_cast<void*>(base + kLuaPCallRva);

        auto* fixedLoadBufferX =
            reinterpret_cast<void*>(base + kLuaLoadBufferXRva);

        if (!IsCanonical(reinterpret_cast<std::uintptr_t>(fixedPCall)) ||
            !IsCanonical(reinterpret_cast<std::uintptr_t>(fixedLoadBufferX)))
        {
            return false;
        }

        g_pcall = reinterpret_cast<LuaPCall>(fixedPCall);
        g_loadBuffer = reinterpret_cast<LuaLoadBufferX>(fixedLoadBufferX);
        g_luaResolutionMode = 4;
        return true;
    }

    void* ResolveLuaState()
    {
        auto base = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

        if (!base)
            return nullptr;

        auto luaInterface =
            *reinterpret_cast<std::uintptr_t*>(base + kLuaInterfaceSlotRva);

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

    struct LuaSnapshot
    {
        std::uintptr_t L;
        std::uintptr_t stackBase;
        std::uintptr_t stackTop;
        std::uintptr_t stackMax;
    };

    bool TryReadLuaSnapshot(LuaSnapshot& snapshot)
    {
        snapshot = {};

        __try
        {
            auto base = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));

            if (!base)
                return false;

            auto luaInterface =
                *reinterpret_cast<std::uintptr_t*>(base + kLuaInterfaceSlotRva);

            if (!IsCanonical(luaInterface))
                return false;

            auto L = *reinterpret_cast<std::uintptr_t*>(luaInterface + 0x08);

            if (!IsCanonical(L))
                return false;

            auto stackBase = *reinterpret_cast<std::uintptr_t*>(L + 0x20);
            auto stackTop = *reinterpret_cast<std::uintptr_t*>(L + 0x28);
            auto stackMax = *reinterpret_cast<std::uintptr_t*>(L + 0x30);

            if (!IsCanonical(stackBase) ||
                !IsCanonical(stackTop) ||
                !IsCanonical(stackMax) ||
                !(stackBase <= stackTop && stackTop <= stackMax) ||
                stackMax - stackBase > 0x01000000ULL)
            {
                return false;
            }

            snapshot = { L, stackBase, stackTop, stackMax };
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    bool IsLuaStateStable(LuaSnapshot& stable)
    {
        LuaSnapshot first{};
        LuaSnapshot second{};

        if (!TryReadLuaSnapshot(first))
            return false;

        // Give another in-process component a scheduling opportunity before
        // committing to the shared Lua state. This is not a cross-tool lock;
        // it is a conservative collision detector.
        SwitchToThread();

        if (!TryReadLuaSnapshot(second))
            return false;

        if (first.L != second.L ||
            first.stackBase != second.stackBase ||
            first.stackTop != second.stackTop ||
            first.stackMax != second.stackMax)
        {
            return false;
        }

        stable = second;
        return true;
    }

    bool TryRestoreLuaTop(
        const LuaSnapshot& snapshot)
    {
        __try
        {
            auto currentBase =
                *reinterpret_cast<std::uintptr_t*>(snapshot.L + 0x20);
            auto currentTop =
                *reinterpret_cast<std::uintptr_t*>(snapshot.L + 0x28);
            auto currentMax =
                *reinterpret_cast<std::uintptr_t*>(snapshot.L + 0x30);

            // Never overwrite the stack pointer if another component appears
            // to have changed the Lua stack meanwhile.
            if (currentBase != snapshot.stackBase ||
                currentMax != snapshot.stackMax ||
                !IsCanonical(currentTop) ||
                currentTop < currentBase ||
                currentTop > currentMax)
            {
                return false;
            }

            *reinterpret_cast<std::uintptr_t*>(snapshot.L + 0x28) =
                snapshot.stackTop;

            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    struct ChunkResult
    {
        bool ok;
        int detail;
    };

    // Keep SEH in a helper that owns no C++ objects requiring unwinding.
    // MSVC rejects __try in a function that also contains std::lock_guard
    // (C2712), so the mutex lifetime lives in the wrapper below.
    ChunkResult RunChunkDetailedSeh(
        const char* chunkData,
        std::size_t chunkSize)
    {
        if (!ResolveLuaFunctions())
            return { false, 2102 };

        LuaSnapshot snapshot{};

        if (!IsLuaStateStable(snapshot))
            return { false, 2110 };

        void* L = reinterpret_cast<void*>(snapshot.L);

        __try
        {
            int loadStatus = g_loadBuffer(
                L,
                chunkData,
                chunkSize,
                "@PxGCorpseBridge/v0.10.3",
                "t");

            if (loadStatus != 0)
            {
                if (!TryRestoreLuaTop(snapshot))
                    return { false, 2111 };

                return { false, 2103 };
            }

            int callStatus = g_pcall(L, 0, 0, 0);

            if (callStatus != 0)
            {
                if (!TryRestoreLuaTop(snapshot))
                    return { false, 2111 };

                return { false, 2104 };
            }

            auto topAfter =
                *reinterpret_cast<std::uintptr_t*>(snapshot.L + 0x28);

            if (topAfter != snapshot.stackTop)
            {
                if (!TryRestoreLuaTop(snapshot))
                    return { false, 2111 };

                return { false, 2105 };
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // Do not blindly write into lua_State from an SEH handler.
            return { false, 2199 };
        }

        return { true, 0 };
    }

    ChunkResult RunChunkDetailed(const std::string& chunk)
    {
        // Serialize only our own Bridge calls. The actual SEH execution is in
        // RunChunkDetailedSeh(), which has no object requiring destruction.
        std::lock_guard<std::mutex> luaLock(g_luaMutex);

        return RunChunkDetailedSeh(
            chunk.c_str(),
            chunk.size());
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
                return response;

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
                    response.detail1 = 2401;
                else
                    response.detail1 = result.detail;

                return response;
            }

            case Action::UseBallOnCorpse:
            {
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

    bool FindGameWindow()
    {
        HWND hwnd = nullptr;
        EnumWindows(FindWindowForPid, reinterpret_cast<LPARAM>(&hwnd));

        if (!hwnd)
            return false;

        g_hwnd = hwnd;
        return true;
    }

    VOID CALLBACK BridgeTimerProc(
        HWND hwnd,
        UINT,
        UINT_PTR timerId,
        DWORD)
    {
        KillTimer(hwnd, timerId);
        g_dispatchTimerId.store(0);

        Request request{};

        {
            std::lock_guard<std::mutex> lock(g_actionMutex);

            if (!g_actionPending || g_actionDone)
                return;

            request = g_pendingRequest;
        }

        Response response = Execute(request);

        {
            std::lock_guard<std::mutex> lock(g_actionMutex);

            // A request may have timed out while Execute() was running.
            // Never let a stale completion mark a newer request as finished.
            if (!g_actionPending ||
                g_actionDone ||
                g_pendingRequest.commandId != request.commandId)
            {
                return;
            }

            g_pendingResponse = response;
            g_actionDone = true;
        }

        g_actionCv.notify_all();
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

        // Marshal onto the PxG window thread without replacing/subclassing its
        // WndProc. DispatchMessage invokes TIMERPROC callbacks on that thread.
        UINT_PTR timerId = SetTimer(
            g_hwnd,
            0,
            USER_TIMER_MINIMUM,
            BridgeTimerProc);

        if (timerId == 0)
        {
            std::lock_guard<std::mutex> lock(g_actionMutex);
            g_actionPending = false;
            g_actionDone = false;

            busy.status = static_cast<std::uint16_t>(Status::Failed);
            busy.detail0 = 4002;
            return busy;
        }

        g_dispatchTimerId.store(timerId);

        std::unique_lock<std::mutex> lock(g_actionMutex);

        bool completed = g_actionCv.wait_for(
            lock,
            std::chrono::seconds(2),
            [commandId = request.commandId]
            {
                return g_actionDone &&
                    g_pendingRequest.commandId == commandId;
            });

        if (!completed)
        {
            UINT_PTR activeTimer = g_dispatchTimerId.exchange(0);

            if (activeTimer != 0)
                KillTimer(g_hwnd, activeTimer);

            // Invalidate this command. BridgeTimerProc checks commandId before
            // publishing a result, preventing stale-completion races.
            if (g_pendingRequest.commandId == request.commandId)
            {
                g_actionPending = false;
                g_actionDone = false;
            }

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
            L"\\\\.\\pipe\\PxGCorpseBridge.%lu.v9",
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
        for (int i = 0; i < 50 && !FindGameWindow(); ++i)
            Sleep(100);

        if (!g_hwnd)
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
