using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using UdtLib.Framing;
using UdtLib.Reliability;
using Xunit;

namespace UdtLib.Tests.Reliability;

public class ReliableChannelTests
{
    private static readonly IPEndPoint TestRemote = new(IPAddress.Loopback, 9000);

    /// <summary>Creates a properly-owned inbound frame to hand to ProcessInboundAsync.</summary>
    private static UdtFrame MakeInbound(UdtFrameType type, uint sequence, byte[] payload)
    {
        using var tmp = PacketFramer.Create(type, sequence, payload);
        var owner = MemoryPool<byte>.Shared.Rent(tmp.WireMemory.Length);
        tmp.WireMemory.CopyTo(owner.Memory);
        PacketFramer.TryParse(owner.Memory, tmp.WireMemory.Length, owner, out var frame);
        return frame!;
    }

    // ------------------------------------------------------------ ReadAllAsync

    [Fact]
    public async Task ReadAllAsync_CompletesAfterDispose_ReturnsBufferedMessages()
    {
        var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.ProcessInboundAsync(MakeInbound(UdtFrameType.Data, 1u, "msg"u8.ToArray()), TestContext.Current.CancellationToken);

        await channel.DisposeAsync();

        var collected = new List<byte[]>();
        await foreach (var msg in channel.ReadAllAsync(TestContext.Current.CancellationToken))
            collected.Add(msg.ToArray());

        Assert.Single(collected);
        Assert.Equal("msg"u8.ToArray(), collected[0]);

        static ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
            => ValueTask.FromResult(buf.Length);
    }

    // -------------------------------------------------------------- SendAsync

    [Fact]
    public async Task SendAsync_InvokesSenderWithDataFrame()
    {
        var sent = new List<byte[]>();
        await using var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.SendAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Single(sent);
        Assert.Equal((byte)UdtFrameType.Data, sent[0][0]);

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            sent.Add(buf.ToArray());
            return ValueTask.FromResult(buf.Length);
        }
    }

    [Fact]
    public async Task SendAsync_SendsToCorrectRemoteEndPoint()
    {
        EndPoint? captured = null;

        await using var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.SendAsync("test"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(TestRemote, captured);

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            captured = ep;
            return ValueTask.FromResult(buf.Length);
        }
    }

    [Fact]
    public async Task SendAsync_SequenceNumbersStrictlyIncrement()
    {
        var sequences = new List<uint>();
        await using var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.SendAsync("a"u8.ToArray(), TestContext.Current.CancellationToken);
        await channel.SendAsync("b"u8.ToArray(), TestContext.Current.CancellationToken);
        await channel.SendAsync("c"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(3, sequences.Count);
        Assert.Equal(sequences[0] + 1, sequences[1]);
        Assert.Equal(sequences[1] + 1, sequences[2]);

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            sequences.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf.Span.Slice(1, 4)));
            return ValueTask.FromResult(buf.Length);
        }
    }

    // ---------------------------------------------------- ProcessInbound(Data)

    [Fact]
    public async Task ProcessInbound_DataFrame_SendsAckWithMatchingSequence()
    {
        var sent = new List<byte[]>();

        await using var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.ProcessInboundAsync(MakeInbound(UdtFrameType.Data, 1u, "payload"u8.ToArray()), TestContext.Current.CancellationToken);

        Assert.Single(sent);
        Assert.Equal((byte)UdtFrameType.Ack, sent[0][0]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(sent[0].AsSpan().Slice(1, 4)));

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            sent.Add(buf.ToArray());
            return ValueTask.FromResult(buf.Length);
        }
    }

    [Fact]
    public async Task ProcessInbound_DataFrame_EnqueuesPayloadForReading()
    {
        await using var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        var expected = "hello world"u8.ToArray();
        await channel.ProcessInboundAsync(MakeInbound(UdtFrameType.Data, 1u, expected), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var msg in channel.ReadAllAsync(cts.Token))
        {
            Assert.Equal(expected, msg.ToArray());
            break;
        }

        static ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
            => ValueTask.FromResult(buf.Length);
    }

    [Fact]
    public async Task ProcessInbound_MultipleDataFrames_AllEnqueuedInOrder()
    {
        await using var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        var messages = new[] { "first"u8.ToArray(), "second"u8.ToArray(), "third"u8.ToArray() };
        for (uint i = 0; i < messages.Length; i++)
            await channel.ProcessInboundAsync(MakeInbound(UdtFrameType.Data, i + 1, messages[i]), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<byte[]>();
        await foreach (var msg in channel.ReadAllAsync(cts.Token))
        {
            received.Add(msg.ToArray());
            if (received.Count == messages.Length) break;
        }

        Assert.Equal(messages.Length, received.Count);
        for (int i = 0; i < messages.Length; i++)
            Assert.Equal((IEnumerable<byte>)messages[i], received[i]);

        static ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
            => ValueTask.FromResult(buf.Length);
    }

    // ----------------------------------------------------- ProcessInbound(Ack)

    [Fact]
    public async Task ProcessInbound_AckFrame_PreventsRetransmit()
    {
        var sendCount = 0;

        await using var channel = new ReliableChannel(
            TestRemote, Sender, TimeProvider.System,
            retransmitAfter: TimeSpan.FromMilliseconds(50));

        await channel.SendAsync("ping"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(1, sendCount);

        // ACK the sequence number that was just sent (Interlocked.Increment starts at 0 → first seq is 1)
        await channel.ProcessInboundAsync(MakeInbound(UdtFrameType.Ack, 1u, []), TestContext.Current.CancellationToken);
        // Wait well past the retransmit window — no retransmit should occur
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(1, sendCount);

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            Interlocked.Increment(ref sendCount);
            return ValueTask.FromResult(buf.Length);
        }
    }

    // -------------------------------------------------------- Retransmit loop

    [Fact]
    public async Task RetransmitLoop_ResendsUnackedFrameAfterDelay()
    {
        var sendCount = 0;
        await using var channel = new ReliableChannel(
            TestRemote, Sender, TimeProvider.System,
            retransmitAfter: TimeSpan.FromMilliseconds(50));

        await channel.SendAsync("data"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(1, sendCount);

        // Wait long enough for at least one retransmit cycle
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(sendCount >= 2, $"Expected at least 2 sends (initial + retransmit), got {sendCount}");

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            Interlocked.Increment(ref sendCount);
            return ValueTask.FromResult(buf.Length);
        }
    }

    [Fact]
    public async Task RetransmitLoop_UsesTimeProvider_FrozenTimePreventsRetransmit()
    {
        var sendCount = 0;
        var fakeTime = new FakeTimeProvider();
        await using var channel = new ReliableChannel(
            TestRemote, Sender, fakeTime,
            retransmitAfter: TimeSpan.FromSeconds(1));

        await channel.SendAsync("data"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(1, sendCount);

        // With time frozen, no retransmit should fire
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, sendCount);

        // Advance fake time past the retransmit threshold; retransmit loop should now trigger
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.True(sendCount >= 2, $"Expected retransmit after time advancement, got {sendCount}");

        ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
        {
            Interlocked.Increment(ref sendCount);
            return ValueTask.FromResult(buf.Length);
        }
    }

    // ------------------------------------------------------------ ReadAllAsync

    [Fact]
    public async Task ReadAllAsync_CompletesWhenChannelIsDisposed()
    {
        var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.ProcessInboundAsync(MakeInbound(UdtFrameType.Data, 1u, "msg"u8.ToArray()), CancellationToken.None);

        await channel.DisposeAsync();

        var collected = new List<byte[]>();
        await foreach (var msg in channel.ReadAllAsync(TestContext.Current.CancellationToken))
            collected.Add(msg.ToArray());

        Assert.Single(collected);
        Assert.Equal("msg"u8.ToArray(), collected[0]);

        static ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
            => ValueTask.FromResult(buf.Length);
    }

    // -------------------------------------------------------------- Dispose

    [Fact]
    public async Task DisposeAsync_CompletesWithoutHanging()
    {
        var channel = new ReliableChannel(TestRemote, Sender, TimeProvider.System);
        await channel.SendAsync("pending"u8.ToArray(), TestContext.Current.CancellationToken);

        await channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        static ValueTask<int> Sender(ReadOnlyMemory<byte> buf, EndPoint ep, CancellationToken ct)
            => ValueTask.FromResult(buf.Length);
    }
}
