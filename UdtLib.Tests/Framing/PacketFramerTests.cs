using System.Buffers;
using System.Buffers.Binary;
using UdtLib.Framing;
using Xunit;

namespace UdtLib.Tests.Framing;

public class PacketFramerTests
{
    // ------------------------------------------------------------------ Create

    [Fact]
    public void Create_DataFrame_SetsFieldsCorrectly()
    {
        var payload = "Hello"u8.ToArray();

        using var frame = PacketFramer.Create(UdtFrameType.Data, 42u, payload);

        Assert.Equal(UdtFrameType.Data, frame.Type);
        Assert.Equal(42u, frame.Sequence);
        Assert.Equal(payload.Length, frame.PayloadLength);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public void Create_AckFrame_HasEmptyPayload()
    {
        using var frame = PacketFramer.Create(UdtFrameType.Ack, 99u, []);

        Assert.Equal(UdtFrameType.Ack, frame.Type);
        Assert.Equal(99u, frame.Sequence);
        Assert.Equal(0, frame.PayloadLength);
        Assert.Equal(UdtFrame.HeaderSize, frame.WireMemory.Length);
    }

    [Fact]
    public void Create_WireMemory_ContainsCorrectHeader()
    {
        var payload = new byte[] { 1, 2, 3 };

        using var frame = PacketFramer.Create(UdtFrameType.Data, 5u, payload);
        var wire = frame.WireMemory.Span;

        Assert.Equal((byte)UdtFrameType.Data, wire[0]);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(wire.Slice(1, 4)));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(wire.Slice(5, 2)));
        Assert.Equal(payload, wire[UdtFrame.HeaderSize..].ToArray());
    }

    [Fact]
    public void Create_MaxPayloadSize_Succeeds()
    {
        using var frame = PacketFramer.Create(UdtFrameType.Data, 1u, new byte[ushort.MaxValue]);

        Assert.Equal(ushort.MaxValue, frame.PayloadLength);
    }

    [Fact]
    public void Create_PayloadTooLarge_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PacketFramer.Create(UdtFrameType.Data, 1u, new byte[ushort.MaxValue + 1]));
    }

    [Fact]
    public void Create_SequenceZero_IsValid()
    {
        using var frame = PacketFramer.Create(UdtFrameType.Data, 0u, "x"u8.ToArray());

        Assert.Equal(0u, frame.Sequence);
    }

    [Fact]
    public void Create_SequenceMaxValue_IsValid()
    {
        using var frame = PacketFramer.Create(UdtFrameType.Ack, uint.MaxValue, []);

        Assert.Equal(uint.MaxValue, frame.Sequence);
    }

    // ---------------------------------------------------------------- TryParse

    [Fact]
    public void TryParse_ValidDataFrame_ReturnsTrueAndPopulatesFields()
    {
        var payload = "world"u8.ToArray();
        using var original = PacketFramer.Create(UdtFrameType.Data, 7u, payload);
        var owner = MemoryPool<byte>.Shared.Rent(original.WireMemory.Length);
        original.WireMemory.CopyTo(owner.Memory);

        var result = PacketFramer.TryParse(owner.Memory, original.WireMemory.Length, owner, out var parsed);

        Assert.True(result);
        using (parsed)
        {
            Assert.NotNull(parsed);
            Assert.Equal(UdtFrameType.Data, parsed!.Type);
            Assert.Equal(7u, parsed.Sequence);
            Assert.Equal(payload.Length, parsed.PayloadLength);
            Assert.Equal(payload, parsed.Payload.ToArray());
        }
    }

    [Fact]
    public void TryParse_ValidAckFrame_ReturnsTrueWithZeroPayload()
    {
        using var original = PacketFramer.Create(UdtFrameType.Ack, 3u, []);
        var owner = MemoryPool<byte>.Shared.Rent(original.WireMemory.Length);
        original.WireMemory.CopyTo(owner.Memory);

        var result = PacketFramer.TryParse(owner.Memory, original.WireMemory.Length, owner, out var parsed);

        Assert.True(result);
        using (parsed)
        {
            Assert.NotNull(parsed);
            Assert.Equal(UdtFrameType.Ack, parsed!.Type);
            Assert.Equal(3u, parsed.Sequence);
            Assert.Equal(0, parsed.PayloadLength);
        }
    }

    [Fact]
    public void TryParse_ZeroReceivedBytes_ReturnsFalse()
    {
        using var owner = MemoryPool<byte>.Shared.Rent(UdtFrame.HeaderSize);

        var result = PacketFramer.TryParse(owner.Memory, 0, owner, out var frame);

        Assert.False(result);
        Assert.Null(frame);
    }

    [Fact]
    public void TryParse_FewerBytesThanHeader_ReturnsFalse()
    {
        using var owner = MemoryPool<byte>.Shared.Rent(UdtFrame.HeaderSize - 1);

        var result = PacketFramer.TryParse(owner.Memory, UdtFrame.HeaderSize - 1, owner, out var frame);

        Assert.False(result);
        Assert.Null(frame);
    }

    [Fact]
    public void TryParse_UnknownTypeByte_ReturnsFalse()
    {
        using var owner = MemoryPool<byte>.Shared.Rent(UdtFrame.HeaderSize);
        owner.Memory.Span[0] = 0xFF;

        var result = PacketFramer.TryParse(owner.Memory, UdtFrame.HeaderSize, owner, out var frame);

        Assert.False(result);
        Assert.Null(frame);
    }

    [Fact]
    public void TryParse_DeclaredPayloadExceedsReceivedBytes_ReturnsFalse()
    {
        using var owner = MemoryPool<byte>.Shared.Rent(UdtFrame.HeaderSize + 5);
        var span = owner.Memory.Span;
        span[0] = (byte)UdtFrameType.Data;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), 1u);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(5, 2), 100); // claims 100-byte payload

        var result = PacketFramer.TryParse(owner.Memory, UdtFrame.HeaderSize + 5, owner, out var frame);

        Assert.False(result);
        Assert.Null(frame);
    }

    [Fact]
    public void TryParse_RoundTrip_PreservesSequenceAndPayload()
    {
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var original = PacketFramer.Create(UdtFrameType.Data, uint.MaxValue - 1, payload);
        var owner = MemoryPool<byte>.Shared.Rent(original.WireMemory.Length);
        original.WireMemory.CopyTo(owner.Memory);

        PacketFramer.TryParse(owner.Memory, original.WireMemory.Length, owner, out var parsed);

        using (parsed)
        {
            Assert.Equal(uint.MaxValue - 1, parsed!.Sequence);
            Assert.Equal(payload, parsed.Payload.ToArray());
        }
    }
}
