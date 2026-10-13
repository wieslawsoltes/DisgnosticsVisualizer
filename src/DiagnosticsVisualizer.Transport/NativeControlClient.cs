using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Transport;

public enum NativeControlOperation { Status, Pause, Resume, Stop, StopAndRevert }
public sealed record NativeControlReply(NativeControlOperation Operation, int HResult, int State, int Methods, int FailedMethods, long Timestamp);

/// <summary>A caller-supplied capability, never inferred from trace contents. Does not attach processes.</summary>
public interface INativeTargetController
{
    ValueTask<string> ExecuteAsync(Guid session, TraceCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Loopback IPv4-only native control, authenticated by a separate 256-bit token and bound to the
/// capture session. An authorized TCP/TLS sidecar may bridge this endpoint. Credentials never enter
/// traces, connection URLs, command arguments, record-generated ToString methods, or exceptions.
/// </summary>
public sealed class NativeControlClient : INativeTargetController, IDisposable
{
    private readonly int _port;
    private readonly byte[] _token;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private bool _disposed;
    public NativeControlClient(int port, string token, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        if (_timeout < TimeSpan.FromMilliseconds(100) || _timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Expected a 256-bit hexadecimal native control token.", nameof(token));
        _token = Convert.FromHexString(token);
        if (_token.All(b => b == 0)) throw new ArgumentException("An empty native capability is forbidden.", nameof(token));
        _port = port;
    }
    public async Task<NativeControlReply> SendAsync(Guid session, NativeControlOperation operation, CancellationToken cancellationToken = default)
    {
        if (session == Guid.Empty || !Enum.IsDefined(operation)) throw new ArgumentException("Invalid native control session or operation.");
        byte[] request = new byte[72];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            "DVNCMD01"u8.CopyTo(request); session.TryWriteBytes(request.AsSpan(8, 16)); _token.CopyTo(request, 24);
        }
        RandomNumberGenerator.Fill(request.AsSpan(56, 8)); BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(64), (int)operation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            await socket.ConnectAsync(IPAddress.Loopback, _port, deadline.Token).ConfigureAwait(false);
            using var stream = socket.GetStream();
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var reply = new byte[64]; await stream.ReadExactlyAsync(reply, deadline.Token).ConfigureAwait(false);
            if (!reply.AsSpan(0, 8).SequenceEqual("DVNACK01"u8) || new Guid(reply.AsSpan(8, 16)) != session ||
                !reply.AsSpan(24, 8).SequenceEqual(request.AsSpan(56, 8)) || BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(32)) != (int)operation ||
                BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(52)) != 0)
                throw new InvalidDataException("Native control response identity mismatch.");
            int Read(int offset) => BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(offset));
            var result = new NativeControlReply(operation, Read(36), Read(40), Read(44), Read(48), BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(56)));
            if (result.State is < 0 or > 4 || result.Methods is < 0 or > 4096 || result.FailedMethods < 0 || result.FailedMethods > result.Methods || result.Timestamp < 0)
                throw new InvalidDataException("Invalid native control response fields.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Native control did not acknowledge within the configured deadline; its outcome is unknown."); }
        finally { CryptographicOperations.ZeroMemory(request); }
    }
    public async ValueTask<string> ExecuteAsync(Guid session, TraceCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind is not (CommandKind.Pause or CommandKind.Resume or CommandKind.Capabilities)) return "UNSUPPORTED_NATIVE_CONTROL";
        try
        {
            var reply = await SendAsync(session, command.Kind == CommandKind.Capabilities ? NativeControlOperation.Status : command.Kind == CommandKind.Pause ? NativeControlOperation.Pause : NativeControlOperation.Resume, cancellationToken).ConfigureAwait(false);
            if (command.Kind == CommandKind.Capabilities) return reply.HResult == 0 && reply.State is 0 or 1 ? "NATIVE_PAUSE_RESUME" : "NATIVE_READ_ONLY";
            return reply.HResult == 0 && reply.State == (command.Kind == CommandKind.Pause ? 1 : 0) ? "OK" : "NATIVE_CONTROL_REJECTED";
        }
        catch (Exception e) when (e is IOException or SocketException or TimeoutException)
        { return "NATIVE_CONTROL_UNACKNOWLEDGED"; } // Never claim rollback or success after a lost acknowledgement.
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_token); } }
}
