using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UdtLib;
using UdtLib.Framing;

namespace UdtLib.Session;

internal sealed class UdtServer : IUdtServer
{
    private const int MaxPacketSize = 64 * 1024;
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<string, Lazy<Task<UdtSession>>> _sessions = new();
    private readonly Channel<UdtSession> _accepted = Channel.CreateUnbounded<UdtSession>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveTask;

    public UdtServer(IPEndPoint endpoint)
    {
        _socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(endpoint);
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public EndPoint LocalEndPoint => _socket.LocalEndPoint!;

    public async IAsyncEnumerable<IUdtSession> AcceptAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _accepted.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_accepted.Reader.TryRead(out var session))
            {
                yield return session;
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var owner = MemoryPool<byte>.Shared.Rent(MaxPacketSize);
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                var result = await _socket.ReceiveFromAsync(owner.Memory, SocketFlags.None, remote, _cts.Token).ConfigureAwait(false);
                if (result.ReceivedBytes <= 0)
                {
                    owner.Dispose();
                    continue;
                }

                if (!PacketFramer.TryParse(owner.Memory, result.ReceivedBytes, owner, out var frame) || frame is null)
                {
                    owner.Dispose();
                    continue;
                }

                var key = result.RemoteEndPoint.ToString()!;
                var session = await _sessions.GetOrAdd(
                    key, 
                    _ => new Lazy<Task<UdtSession>>(() => CreateSessionAsync(result.RemoteEndPoint), LazyThreadSafetyMode.ExecutionAndPublication))
                    .Value;
                await session.ProcessInboundAsync(frame, _cts.Token).ConfigureAwait(false);
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

    internal void RemoveSession(string key)
    {
        _sessions.TryRemove(key, out _);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _accepted.Writer.TryComplete();
        try { await _receiveTask.ConfigureAwait(false); } catch { }

        var disposeTaskList = _sessions.Values
            .Select(x => x.IsValueCreated && x.Value.IsCompletedSuccessfully ? x.Value.Result : null)
            .Where(x => x is not null)
            .Select(x => x!.DisposeAsync().AsTask());
        await Task.WhenAll(disposeTaskList).ConfigureAwait(false);

        _socket.Dispose();
        _cts.Dispose();
    }

    private async Task<UdtSession> CreateSessionAsync(EndPoint endPoint)
    {
        var sessionResult = UdtSession.CreateForServer(this, endPoint, _socket);
        await _accepted.Writer.WriteAsync(sessionResult, _cts.Token).ConfigureAwait(false);
        return sessionResult;
    }
}
