# UdtLib

`UdtLib` is a managed `.NET 8+` implementation of the UDT (UDP-based Data Transfer) protocol.

It provides reliable, ordered, message-oriented transport over UDP through a small async API. The package is intended for applications that want UDP as the underlying transport but still need delivery guarantees and predictable message ordering.

## Why `UdtLib`

- Reliable delivery over UDP with retransmission on missing acknowledgements
- Ordered message processing per session
- Async-first API based on `Task` and `IAsyncEnumerable<T>`
- Server and client entry points with a minimal public surface
- Managed implementation built on `System.Net.Sockets`, `System.Threading.Channels`, and `System.Buffers`
- Works with both IPv4 and IPv6 through `IPEndPoint`

## Installation

```bash
dotnet add package UdtLib
```

## Package contents

The package exposes four main entry points:

- `IUdtServer` for accepting remote peers over UDP
- `IUdtSession` for bidirectional message exchange
- `IUdtSender` when only sending is needed
- `IUdtReceiver` when only receiving is needed

Use `IUdtServer.Create(...)` to bind a server socket and `UdtClient.Connect(...)` to create an outbound session.

## Quick start

### Server

```csharp
using System.Net;
using UdtLib;

var endpoint = new IPEndPoint(IPAddress.Any, 9000);

await using IUdtServer server = IUdtServer.Create(endpoint);

await foreach (IUdtSession session in server.AcceptAllAsync(cancellationToken))
{
    _ = Task.Run(() => HandleSessionAsync(session, cancellationToken));
}

static async Task HandleSessionAsync(IUdtSession session, CancellationToken cancellationToken)
{
    await using (session)
    {
        await foreach (ReadOnlyMemory<byte> message in session.ReadAllAsync(cancellationToken))
        {
            await session.SendAsync(message, cancellationToken);
        }
    }
}
```

### Client

```csharp
using System.Net;
using UdtLib;

await using IUdtSession session =
    UdtClient.Connect(new IPEndPoint(IPAddress.Loopback, 9000));

byte[] payload = "Hello, UDT!"u8.ToArray();

await session.SendAsync(payload, cancellationToken);

await foreach (ReadOnlyMemory<byte> reply in session.ReadAllAsync(cancellationToken))
{
    Console.WriteLine($"Received {reply.Length} bytes");
    break;
}
```

## API summary

### `IUdtServer`

| Member | Description |
|--------|-------------|
| `EndPoint LocalEndPoint` | Gets the bound local endpoint |
| `IAsyncEnumerable<IUdtSession> AcceptAllAsync(CancellationToken)` | Produces a session for each newly observed remote endpoint |
| `static IUdtServer Create(IPEndPoint endpoint)` | Creates and binds a server |

### `UdtClient`

| Member | Description |
|--------|-------------|
| `static IUdtSession Connect(IPEndPoint remote)` | Creates a client session connected to a remote endpoint |

### `IUdtSession`

`IUdtSession` combines `IUdtSender`, `IUdtReceiver`, and `IAsyncDisposable`.

| Member | Description |
|--------|-------------|
| `Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken)` | Sends a message reliably |
| `IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken)` | Reads inbound messages in order |
| `ValueTask DisposeAsync()` | Stops and disposes the session |

## Transport characteristics

- Message-oriented: each send represents one payload
- Reliable: sent frames are retransmitted until acknowledged
- Ordered: inbound messages are exposed in sequence order
- Maximum payload per frame: `65,535` bytes

`UdtLib` is a good fit when you want a lightweight managed UDP transport with reliability semantics, especially for message-based protocols, service-to-service communication, and controlled networking environments.

## Requirements

- `.NET 8`
- `.NET 10`

## License

BSD 3-Clause.

Source repository: <https://github.com/yartat/udtsharp>
