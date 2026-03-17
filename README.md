# UdtLib

[![License](https://img.shields.io/badge/License-BSD_3--Clause-blue.svg)](https://opensource.org/licenses/BSD-3-Clause)
![Build](https://github.com/yartat/udtsharp/actions/workflows/ci.yml/badge.svg)
[![NuGet Badge](https://img.shields.io/nuget/v/UdtLib.svg)](https://www.nuget.org/packages/UdtLib)

A managed **.NET 8+** implementation of the **UDT (UDP-based Data Transfer)** protocol.  
UdtLib provides **reliable, ordered, message-level** data transport over UDP with a fully async API built on `System.Net.Sockets`, `System.Threading.Channels`, and `System.Buffers`.

---

## Features

- **Reliable delivery** — automatic retransmission with configurable timeout (default 200 ms)
- **Ordered delivery** — frames are acknowledged per-sequence; the sender withholds retransmits until ACK is received
- **Async-first API** — `IAsyncEnumerable<ReadOnlyMemory<byte>>` for reading, `Task`-based sending
- **Memory-efficient** — receive and send buffers are rented from `MemoryPool<byte>.Shared`; zero extra allocations on the ACK path
- **Testable timing** — retransmit scheduling built on `TimeProvider`, injectable in tests
- **Server and client modes** — single entry-point per role via static factory methods
- **IPv4 and IPv6** — socket address family follows the provided `IPEndPoint`

---

## Requirements

| Component | Version |
|-----------|---------|
| .NET      | 8.0+    |

---

## Installation

```
dotnet add package UdtLib
```

---

## Quick Start

### Server

```csharp
using System.Net;
using UdtLib;

// Create and start a server
await using IUdtServer server = IUdtServer.Create(new IPEndPoint(IPAddress.Any, 9000));

await foreach (IUdtSession session in server.AcceptAllAsync(cancellationToken))
{
    _ = Task.Run(() => HandleSessionAsync(session, cancellationToken));
}

static async Task HandleSessionAsync(IUdtSession session, CancellationToken ct)
{
    await using (session)
    {
        await foreach (ReadOnlyMemory<byte> message in session.ReadAllAsync(ct))
        {
            Console.WriteLine($"Received {message.Length} bytes");

            // Echo back
            await session.SendAsync(message, ct);
        }
    }
}
```

### Client

```csharp
using System.Net;
using UdtLib;

await using IUdtSession session = UdtClient.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9000));

byte[] payload = "Hello, UDT!"u8.ToArray();
await session.SendAsync(payload, cancellationToken);

await foreach (ReadOnlyMemory<byte> reply in session.ReadAllAsync(cancellationToken))
{
    Console.WriteLine($"Reply: {reply.Length} bytes");
    break;
}
```

---

## API Reference

### `IUdtServer`

The server listens on a bound UDP socket and demultiplexes incoming packets by remote endpoint.

| Member | Description |
|--------|-------------|
| `static IUdtServer Create(IPEndPoint endpoint)` | Creates and binds a server on the given endpoint |
| `IAsyncEnumerable<IUdtSession> AcceptAllAsync(CancellationToken)` | Yields a new `IUdtSession` for each first-seen remote endpoint |
| `ValueTask DisposeAsync()` | Cancels the receive loop and disposes all active sessions |

### `UdtClient`

Static factory for outbound connections.

| Member | Description |
|--------|-------------|
| `static IUdtSession Connect(IPEndPoint remote)` | Creates a client session bound to an ephemeral local port |

### `IUdtSession`

Represents a bidirectional message channel between two endpoints.  
Extends `IUdtSender`, `IUdtReceiver`, and `IAsyncDisposable`.

| Member | Description |
|--------|-------------|
| `Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken)` | Sends a message; retransmits until ACK is received |
| `IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken)` | Streams inbound messages in order |
| `ValueTask DisposeAsync()` | Cancels and tears down the session |

### `IUdtSender` / `IUdtReceiver`

Composable role interfaces — use these as dependencies when only one direction is needed.

---

## Architecture


```
+-----------------------------------------------------+
|  Public API (UdtLib namespace)                      |
|  IUdtServer  IUdtSession  IUdtSender  IUdtReceiver  |
+---------------------+-------------------------------+
                      |
+---------------------v-------------------------------+
|  Session layer  (Session namespace)                 |
|  UdtServer  -- receive loop, session registry       |
|  UdtSession -- per-peer state, owned socket(client) |
+---------------------+-------------------------------+
                      |
+---------------------v-------------------------------+
|  Reliability layer  (Reliability namespace)         |
|  ReliableChannel                                    |
|  . _pending  ConcurrentDictionary<uint,PendingFrame>|
|  . _inbound  Channel<ReadOnlyMemory<byte>>          |
|  . RetransmitLoopAsync                              |
+---------------------+-------------------------------+
                      |
+---------------------v-------------------------------+
|  Framing layer  (Framing namespace)                 |
|  PacketFramer.Create / TryParse                     |
|  UdtFrame -- pooled wire buffer wrapper             |
+-----------------------------------------------------+
```

### Wire Protocol

Each UDP datagram carries exactly one UDT frame.

```
 0       1       2       3       4       5       6       7+ bytes
+-------+-----------------------+---------------+------------------+
| Type  |    Sequence (uint32)  | Length(uint16)|    Payload ...   |
| 1 byte|     4 bytes LE        |   2 bytes LE  |  0-65535 bytes   |
+-------+-----------------------+---------------+------------------+
```

| Type byte | Meaning |
|-----------|---------|
| `0x01`    | Data frame — carries a payload |
| `0x02`    | ACK frame — acknowledges a sequence number; no payload |
| `0x03`    | Handshake (reserved, not yet implemented) |

**Sequence numbers** are `uint32`, wrapping naturally through `uint.MaxValue -> 0`.  
**Maximum payload** per frame is **65 535 bytes** (enforced at creation time).

### Reliability Model

1. Sender assigns a monotonically increasing `uint` sequence number to each `Data` frame.
2. The frame is stored in `_pending` and transmitted immediately.
3. The retransmit loop runs per-pending-frame; if `now - LastSent >= retransmitAfter` the frame is re-sent and `LastSent` is updated.
4. On receiving an `Ack` frame the corresponding entry is removed from `_pending` and its buffer is returned to the pool.
5. Receivers always emit an `Ack` for every valid `Data` frame received.

### Memory Model

- Receive buffers are rented from `MemoryPool<byte>.Shared` at the start of every loop iteration.
- Ownership transfers to `UdtFrame` on successful parse; `UdtFrame.Dispose()` returns the buffer.
- Inbound payloads are **copied** before the frame is disposed so that channel readers receive stable memory.
- Send frames share the same pooled buffer for both immediate send and retransmit storage; the buffer is returned when the ACK removes the frame from `_pending`.

---

## Configuration

`ReliableChannel` (used internally by `UdtSession`) accepts optional parameters:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `retransmitAfter` | `200 ms` | Time after which an unacknowledged frame is retransmitted |
| `timeProvider` | `TimeProvider.System` | Time source used for retransmit scheduling; replace with `FakeTimeProvider` in tests |

---

## License

BSD 3-Clause — see [LICENSE](https://raw.githubusercontent.com/yartat/udtsharp/refs/heads/main/LICENSE) for details.  
Based on the original [UDT C++ library](https://github.com/zerovm/udt).
