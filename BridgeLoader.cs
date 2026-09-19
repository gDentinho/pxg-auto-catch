using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PxGCorpseReader;

internal sealed class BridgeLoader
{
    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_READ = 0x0010;

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    internal async Task<string> EnsureLoadedAsync(
        ProcessMemoryReader reader,
        BridgeClient client,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        if (!reader.IsAttached)
            throw new InvalidOperationException("Reader não está conectado.");

        var profile = reader.CompatibilityProfile;

        if (profile is null || !profile.Validated)
        {
            throw new InvalidOperationException(
                "Bridge bloqueada: Compatibility Resolver não validou este pxgme.exe.");
        }

        if (profile.LuaInterfaceSlotRva > int.MaxValue ||
            profile.LuaPCallRva > int.MaxValue ||
            profile.LuaLoadBufferXRva > int.MaxValue)
        {
            throw new InvalidOperationException(
                "Bridge bloqueada: RVA de compatibilidade excede o protocolo v6.");
        }

        string dllPath = Path.Combine(
            AppContext.BaseDirectory,
            "PxGCorpseBridge_v6.dll");

        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                "PxGCorpseBridge_v6.dll não está ao lado do executável.",
                dllPath);
        }

        bool alreadyLoaded = IsModuleLoaded(
            reader.Process,
            "PxGCorpseBridge_v6.dll");

        if (alreadyLoaded)
        {
            log("BRIDGE_LOAD already_loaded=1 version=6");
        }
        else
        {
            InjectLoadLibrary(reader.Process, dllPath);
            log($"BRIDGE_LOAD injected=1 version=6 dll={dllPath}");
        }

        Exception? last = null;

        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var ping = await client.SendAsync(
                    reader.Process.Id,
                    BridgeAction.Ping,
                    timeoutMs: 500,
                    cancellationToken: cancellationToken);

                if (ping.Status != BridgeStatus.Executed)
                {
                    last = new InvalidOperationException(
                        $"PING retornou {ping.Status}, detail={ping.Detail0}/{ping.Detail1}");
                }
                else
                {
                    var configure = await client.SendAsync(
                        reader.Process.Id,
                        BridgeAction.ConfigureCompatibility,
                        x: checked((int)profile.LuaInterfaceSlotRva),
                        y: checked((int)profile.LuaPCallRva),
                        z: checked((int)profile.LuaLoadBufferXRva),
                        argument0: profile.Schema,
                        timeoutMs: 2500,
                        cancellationToken: cancellationToken);

                    if (configure.Status != BridgeStatus.Executed ||
                        configure.Detail0 != 6000)
                    {
                        throw new InvalidOperationException(
                            "Bridge rejeitou o perfil de compatibilidade. " +
                            $"status={configure.Status}; " +
                            $"detail={configure.Detail0}/{configure.Detail1}; " +
                            $"{BridgeProbeCatalog.ExplainDetail(configure.Detail0)}; " +
                            $"lua={BridgeProbeCatalog.ExplainDetail(configure.Detail1)}");
                    }

                    log(
                        $"BRIDGE_COMPAT_APPLIED source={profile.Source}; " +
                        $"lua_slot=0x{profile.LuaInterfaceSlotRva:X}; " +
                        $"pcall=0x{profile.LuaPCallRva:X}; " +
                        $"loadbuffer=0x{profile.LuaLoadBufferXRva:X}; " +
                        $"detail={configure.Detail0}");

                    return
                        $"Ready v6 / compat={profile.Source} / PING={ping.Detail0}";
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(100, cancellationToken);
        }

        if (alreadyLoaded)
        {
            throw new InvalidOperationException(
                "PxGCorpseBridge_v6.dll já está carregada, mas não respondeu " +
                $"corretamente. Último erro: {last?.Message}");
        }

        throw new InvalidOperationException(
            $"Bridge v6 carregada, mas configuração/PING falhou. Último erro: {last?.Message}");
    }

    private static bool IsModuleLoaded(Process process, string moduleName)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(
                    module.ModuleName,
                    moduleName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static void InjectLoadLibrary(Process process, string dllPath)
    {
        byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");

        nint processHandle = OpenProcess(
            PROCESS_CREATE_THREAD |
            PROCESS_QUERY_INFORMATION |
            PROCESS_VM_OPERATION |
            PROCESS_VM_WRITE |
            PROCESS_VM_READ,
            false,
            process.Id);

        if (processHandle == 0)
            ThrowWin32("OpenProcess");

        nint remoteBuffer = 0;
        nint thread = 0;

        try
        {
            remoteBuffer = VirtualAllocEx(
                processHandle,
                0,
                (nuint)pathBytes.Length,
                MEM_COMMIT | MEM_RESERVE,
                PAGE_READWRITE);

            if (remoteBuffer == 0)
                ThrowWin32("VirtualAllocEx");

            if (!WriteProcessMemory(
                processHandle,
                remoteBuffer,
                pathBytes,
                (nuint)pathBytes.Length,
                out var written) ||
                written != (nuint)pathBytes.Length)
            {
                ThrowWin32("WriteProcessMemory");
            }

            nint localKernel = GetModuleHandleW("kernel32.dll");

            if (localKernel == 0)
                ThrowWin32("GetModuleHandleW(kernel32)");

            nint localLoadLibrary = GetProcAddress(localKernel, "LoadLibraryW");

            if (localLoadLibrary == 0)
                ThrowWin32("GetProcAddress(LoadLibraryW)");

            var remoteKernel = process.Modules
                .Cast<ProcessModule>()
                .FirstOrDefault(m =>
                    string.Equals(
                        m.ModuleName,
                        "kernel32.dll",
                        StringComparison.OrdinalIgnoreCase));

            if (remoteKernel is null)
                throw new InvalidOperationException("kernel32.dll remoto não encontrado.");

            long functionOffset =
                localLoadLibrary.ToInt64() - localKernel.ToInt64();

            nint remoteLoadLibrary =
                (nint)(remoteKernel.BaseAddress.ToInt64() + functionOffset);

            thread = CreateRemoteThread(
                processHandle,
                0,
                0,
                remoteLoadLibrary,
                remoteBuffer,
                0,
                out _);

            if (thread == 0)
                ThrowWin32("CreateRemoteThread");

            uint wait = WaitForSingleObject(thread, 5000);

            if (wait != WAIT_OBJECT_0)
            {
                throw new TimeoutException(
                    $"LoadLibrary remoto não terminou. wait=0x{wait:X8}");
            }

            if (!GetExitCodeThread(thread, out uint exitCode))
                ThrowWin32("GetExitCodeThread");

            if (exitCode == 0)
                throw new InvalidOperationException("LoadLibraryW remoto retornou NULL.");
        }
        finally
        {
            if (thread != 0)
                CloseHandle(thread);

            if (remoteBuffer != 0)
                VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);

            CloseHandle(processHandle);
        }
    }

    private static void ThrowWin32(string operation)
        => throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"{operation} falhou.");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(
        nint process,
        nint address,
        nuint size,
        uint allocationType,
        uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(
        nint process,
        nint address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        nint process,
        nint address,
        byte[] buffer,
        nuint size,
        out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(
        nint process,
        nint threadAttributes,
        nuint stackSize,
        nint startAddress,
        nint parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        nint handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(
        nint thread,
        out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string procName);
}
