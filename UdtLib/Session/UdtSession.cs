using System.Buffers;
using System.Net;
using System.Net.Sockets;
using UdtLib;
using UdtLib.Framing;
using UdtLib.Reliability;

namespace UdtLib.Session;

internal sealed class UdtSession : IUdtSession
{
    private const int MaxFrameSize = 64 * 1024; // 64 KB
    private readonly Socket? _ownedSocket;
    private readonly ReliableChannel _channel;
    private readonly UdtServer? _server;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoopTask;

    public EndPoint RemoteEndPoint { get; }

    private UdtSession(
        UdtServer? server,
        EndPoint remoteEndPoint,
        ReliableChannel channel,
        Socket? ownedSocket,
        Task? receiveLoopTask)
    {
        RemoteEndPoint = remoteEndPoint;
        _server = server;
        _channel = channel;
        _ownedSocket = ownedSocket;
        _receiveLoopTask = receiveLoopTask;
    }

    internal static UdtSession CreateForServer(UdtServer server, EndPoint remote, Socket sharedSocket)
    {
        var channel = new ReliableChannel(
            remote,
            (buffer, target, ct) => sharedSocket.SendToAsync(buffer, SocketFlags.None, target, ct),
            TimeProvider.System);

        return new UdtSession(server, remote, channel, null, null);
    }

    public static IUdtSession CreateClient(IPEndPoint remote)
    {
        var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var channel = new ReliableChannel(
            remote,
            (buffer, target, ct) => socket.SendToAsync(buffer, SocketFlags.None, target, ct),
            TimeProvider.System);

        var session = new UdtSession(null, remote, channel, socket, null);
        session._receiveLoopTask = Task.Run(session.ReceiveLoopAsync);
        return session;
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _channel.SendAsync(payload, cancellationToken);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.ReadAllAsync(cancellationToken);

    internal ValueTask ProcessInboundAsync(UdtFrame frame, CancellationToken cancellationToken)
        => _channel.ProcessInboundAsync(frame, cancellationToken);

    private async Task ReceiveLoopAsync()
    {
        if (_ownedSocket is null)
        {
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            var owner = MemoryPool<byte>.Shared.Rent(MaxFrameSize);
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                var result = await _ownedSocket.ReceiveFromAsync(owner.Memory, SocketFlags.None, remote, _cts.Token).ConfigureAwait(false);
                if (result.ReceivedBytes <= 0)
                {
                    owner.Dispose();
                    continue;
                }

                if (!result.RemoteEndPoint.Equals(RemoteEndPoint))
                {
                    owner.Dispose();
                    continue;
                }

                if (!PacketFramer.TryParse(owner.Memory, result.ReceivedBytes, owner, out var frame) || frame is null)
                {
                    owner.Dispose();
                    continue;
                }

                await _channel.ProcessInboundAsync(frame, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                owner.Dispose();
                break;
            }
            catch
            {
                owner.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask.ConfigureAwait(false); } catch { }
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _ownedSocket?.Dispose();
        _server?.RemoveSession(RemoteEndPoint.ToString()!);
        _cts.Dispose();
    }
}
