namespace UdtLib;

public interface IUdtSender
{
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
