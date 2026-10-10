using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

/// <summary>Exactly one reader; concurrent sends are serialized. Cancellation terminates blocked I/O.</summary>
public interface IFrameConnection : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);
}

public static class ConnectionAuthentication
{
    public static async Task AuthenticateClientAsync(IFrameConnection connection, string token,
        bool producer, CancellationToken cancellationToken = default)
    {
        NetworkSecurity.ValidateToken(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await connection.SendAsync(CommandCodec.Encode(new(0, CommandKind.Authenticate, producer ? 1 : 0, token)), timeout.Token).ConfigureAwait(false);
        var response = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("Collector disconnected during authentication.");
        var command = CommandCodec.Decode(response);
        if (command.Kind != CommandKind.Response || command.Argument != "AUTHENTICATED")
            throw new UnauthorizedAccessException("Collector authentication was not accepted.");
    }
    public static async Task<bool> AuthenticateServerAsync(IFrameConnection connection, string expectedToken,
        bool? requireProducer = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var frame = await connection.ReceiveAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("Peer disconnected before authentication.");
        var command = CommandCodec.Decode(frame);
        var producer = command.ObjectId == 1;
        if (command.Kind != CommandKind.Authenticate || command.ObjectId is < 0 or > 1 ||
            !NetworkSecurity.TokenEquals(command.Argument, expectedToken) || (requireProducer is not null && producer != requireProducer))
            throw new UnauthorizedAccessException("Invalid capture credentials or role.");
        await connection.SendAsync(CommandCodec.Encode(new(0, CommandKind.Response, 0, "AUTHENTICATED")), timeout.Token).ConfigureAwait(false);
        return producer;
    }
}
