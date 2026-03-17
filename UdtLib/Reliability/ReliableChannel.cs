using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UdtLib.Framing;

namespace UdtLib.Reliability;

internal sealed class ReliableChannel : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRetransmitDelay = TimeSpan.FromMilliseconds(40);
    private readonly EndPoint _remote;
    private readonly Func<ReadOnlyMemory<byte>, EndPoint, CancellationToken, ValueTask<int>> _sender;
    private readonly ConcurrentDictionary<uint, PendingFrame> _pending = new();
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TimeSpan _retransmitAfter;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _retransmitTask;
    private readonly TimeProvider _timeProvider;
    private uint _nextSequence;

    private sealed record PendingFrame(UdtFrame Frame, DateTimeOffset LastSent)
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Frame.Dispose();
            _disposed = true;
        }
    }

    public ReliableChannel(
        EndPoint remote,
        Func<ReadOnlyMemory<byte>, EndPoint, CancellationToken, ValueTask<int>> sender,
        TimeProvider timeProvider,
        TimeSpan? retransmitAfter = null)
    {
        _remote = remote;
        _sender = sender;
        _timeProvider = timeProvider;
        _retransmitAfter = retransmitAfter ?? TimeSpan.FromMilliseconds(200);
        _retransmitTask = Task.Run(RetransmitLoopAsync);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _nextSequence);
        var frame = PacketFramer.Create(UdtFrameType.Data, sequence, payload.Span);
        _pending[sequence] = new PendingFrame(frame, _timeProvider.GetUtcNow());

        await _sender(frame.WireMemory, _remote, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ProcessInboundAsync(UdtFrame frame, CancellationToken cancellationToken)
    {
        using (frame)
        {
            if (frame.Type == UdtFrameType.Ack)
            {
                if (_pending.TryRemove(frame.Sequence, out var pending))
                {
                    pending.Dispose();
                }
                return;
            }

            if (frame.Type == UdtFrameType.Data)
            {
                using var ack = PacketFramer.Create(UdtFrameType.Ack, frame.Sequence, ReadOnlySpan<byte>.Empty);
                await _sender(ack.WireMemory, _remote, cancellationToken).ConfigureAwait(false);

                var payloadCopy = new byte[frame.PayloadLength];
                frame.Payload.Span.CopyTo(payloadCopy);
                await _inbound.Writer.WriteAsync(payloadCopy, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_inbound.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    private async Task RetransmitLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var processingPendingTasks = _pending.ToArray().Select(x => InternalRetransmitAsync(x, _cts.Token)); // ToArray to avoid concurrent modification issues
                if (processingPendingTasks.Any())
                {
                    await Task.WhenAll(processingPendingTasks).ConfigureAwait(false);
                }
                else
                {
                    // If there are no pending frames, wait for a short period before checking again to avoid busy-waiting.
                    await Task.Delay(DefaultRetransmitDelay, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        async Task InternalRetransmitAsync(KeyValuePair<uint, PendingFrame> item, CancellationToken token)
        {
            var now = _timeProvider.GetUtcNow();
            var seq = item.Key;
            var pending = item.Value;
            if (token.IsCancellationRequested || now - pending.LastSent < _retransmitAfter)
            {
                return;
            }

            await _sender(pending.Frame.WireMemory, _remote, token).ConfigureAwait(false);
            _pending[seq] = pending with { LastSent = now };
            await Task.Delay(DefaultRetransmitDelay, _cts.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _cts.Cancel();
        try { await _retransmitTask.ConfigureAwait(false); } catch { }

        foreach (var item in _pending.Values)
        {
            item.Dispose();
        }
        
        _pending.Clear();
        _cts.Dispose();
    }
}
