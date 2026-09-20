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

        if (!reader.IsKnownBuild)
            throw new InvalidOperationException(
                "Bridge bloqueada: o SHA do pxgme.exe não é o build validado.");

        string dllPath = Path.Combine(
            AppContext.BaseDirectory,
            "PxGCorpseBridge_v9.dll");

        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                "PxGCorpseBridge.dll não está ao lado do executável.",
                dllPath);

        bool alreadyLoaded = IsModuleLoaded(reader.Process, "PxGCorpseBridge_v9.dll");

        if (alreadyLoaded)
        {
            log("BRIDGE_LOAD already_loaded=1");
        }
        else
        {
            InjectLoadLibrary(reader.Process, dllPath);
            log($"BRIDGE_LOAD injected=1 dll={dllPath}");
        }

        Exception? last = null;

        for (int i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await client.SendAsync(
                    reader.Process.Id,
                    BridgeAction.Ping,
                    timeoutMs: 500,
                    cancellationToken: cancellationToken);

                if (response.Status == BridgeStatus.Executed)
                {
                    return $"Ready v9 race-guard / PING detail={response.Detail0}";
                }

                last = new InvalidOperationException(
                    $"PING retornou {response.Status}, detail={response.Detail0}/{response.Detail1}");
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
                "Já existe uma PxGCorpseBridge.dll carregada neste processo do PxG, " +
                "mas ela não respondeu ao protocolo v5. Feche completamente o PxG, " +
                "abra novamente e então carregue a bridge v0.5.2. " +
                $"Último erro: {last?.Message}");
        }

        throw new InvalidOperationException(
            $"Bridge carregada, mas o PING v5 não respondeu. Último erro: {last?.Message}");
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
                throw new TimeoutException($"LoadLibrary remoto não terminou. wait=0x{wait:X8}");

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
