using System.IO.Pipes;

namespace PxGCorpseReader;

internal sealed class BridgeClient
{
    private long _nextCommandId;

    internal async Task<BridgeResponse> SendAsync(
        int pid,
        BridgeAction action,
        int x = 0,
        int y = 0,
        int z = 0,
        int argument0 = 0,
        int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        ulong commandId = unchecked((ulong)Interlocked.Increment(ref _nextCommandId));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        using var pipe = new NamedPipeClientStream(
            ".",
            BridgeProtocol.PipeName(pid),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeoutCts.Token);

        var request = BridgeProtocol.SerializeRequest(
            action,
            commandId,
            x,
            y,
            z,
            argument0);

        await pipe.WriteAsync(request, timeoutCts.Token);
        await pipe.FlushAsync(timeoutCts.Token);

        var responseBytes = new byte[BridgeProtocol.ResponseSize];
        int read = 0;

        while (read < responseBytes.Length)
        {
            int n = await pipe.ReadAsync(
                responseBytes.AsMemory(read, responseBytes.Length - read),
                timeoutCts.Token);

            if (n <= 0)
                throw new EndOfStreamException("Bridge fechou o pipe antes da resposta completa.");

            read += n;
        }

        var response = BridgeProtocol.DeserializeResponse(responseBytes);

        if (response.CommandId != commandId)
            throw new InvalidDataException(
                $"CommandId divergente. esperado={commandId}; recebido={response.CommandId}");

        return response;
    }
}
