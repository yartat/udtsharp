using System.Net;

namespace UdtLib;

public interface IUdtServer : IAsyncDisposable
{
    EndPoint LocalEndPoint { get; }

    IAsyncEnumerable<IUdtSession> AcceptAllAsync(CancellationToken cancellationToken = default);

    static IUdtServer Create(IPEndPoint endpoint) => new Session.UdtServer(endpoint);
}
