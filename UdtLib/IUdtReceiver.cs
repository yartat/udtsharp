namespace UdtLib;

public interface IUdtReceiver
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken = default);
}
