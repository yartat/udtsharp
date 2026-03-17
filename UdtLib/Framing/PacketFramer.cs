using System.Buffers;
using System.Buffers.Binary;

namespace UdtLib.Framing;

internal enum UdtFrameType : byte
{
    Undefined = 0,
    Data = 1,
    Ack = 2,
    Handshake = 3,
}

internal sealed class UdtFrame : IDisposable
{
    private bool _disposed;

    public const int HeaderSize = 7;

    public required UdtFrameType Type { get; init; }

    public required uint Sequence { get; init; }

    public required int PayloadLength { get; init; }

    public required IMemoryOwner<byte> Owner { get; init; }

    public Memory<byte> WireMemory => Owner.Memory[..(HeaderSize + PayloadLength)];

    public ReadOnlyMemory<byte> Payload => Owner.Memory.Slice(HeaderSize, PayloadLength);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Owner.Dispose();
    }
}

internal static class PacketFramer
{
    public static UdtFrame Create(UdtFrameType type, uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"Payload size must be {ushort.MaxValue} bytes or less.");
        }

        var owner = MemoryPool<byte>.Shared.Rent(UdtFrame.HeaderSize + payload.Length);
        var span = owner.Memory.Span;

        span[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(5, 2), (ushort)payload.Length);
        payload.CopyTo(span.Slice(UdtFrame.HeaderSize, payload.Length));

        return new UdtFrame
        {
            Type = type,
            Sequence = sequence,
            PayloadLength = payload.Length,
            Owner = owner
        };
    }

    public static bool TryParse(Memory<byte> source, int receivedBytes, IMemoryOwner<byte> owner, out UdtFrame? frame)
    {
        frame = null;
        if (receivedBytes < UdtFrame.HeaderSize)
        {
            return false;
        }

        var span = source.Span[..receivedBytes];
        var type = (UdtFrameType)span[0];
        if (!Enum.IsDefined(type))
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(1, 4));
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(5, 2));

        if (UdtFrame.HeaderSize + payloadLength > receivedBytes)
        {
            return false;
        }

        frame = new UdtFrame
        {
            Type = type,
            Sequence = sequence,
            PayloadLength = payloadLength,
            Owner = owner
        };

        return true;
    }
}
