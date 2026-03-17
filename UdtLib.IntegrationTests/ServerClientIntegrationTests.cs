using System.Net;
using Xunit;

namespace UdtLib.IntegrationTests;

/// <summary>
/// End-to-end tests that exercise real UDP sockets.
/// Each test binds the server to port 0 (OS assigns a free port) and retrieves
/// it via <see cref="IUdtServer.LocalEndPoint"/>.
/// </summary>
public class ServerClientIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    // -------------------------------------------------------- single message

    [Fact]
    public async Task Client_SendsMessage_Server_Receives()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var server = IUdtServer.Create(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)server.LocalEndPoint;

        var serverTask = Task.Run(async () =>
        {
            await foreach (var session in server.AcceptAllAsync(cts.Token))
            {
                await foreach (var message in session.ReadAllAsync(cts.Token))
                    return message.ToArray();
            }
            return [];
        }, cts.Token);

        await using var client = UdtClient.Connect(serverEndPoint);
        var payload = "hello UDT"u8.ToArray();
        await client.SendAsync(payload, cts.Token);

        var received = await serverTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(payload, received);
    }

    // -------------------------------------------------------- echo round-trip

    [Fact]
    public async Task Client_SendsMessage_Server_Echoes_Client_Receives()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var server = IUdtServer.Create(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)server.LocalEndPoint;

        // Accept sessions and echo each message back once
        _ = Task.Run(async () =>
        {
            await foreach (var session in server.AcceptAllAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    await foreach (var message in session.ReadAllAsync(cts.Token))
                    {
                        await session.SendAsync(message, cts.Token);
                        break;
                    }
                }, cts.Token);
            }
        }, cts.Token);

        await using var client = UdtClient.Connect(serverEndPoint);
        var payload = "echo me"u8.ToArray();
        await client.SendAsync(payload, cts.Token);

        using var readCts = new CancellationTokenSource(TestTimeout);
        await foreach (var reply in client.ReadAllAsync(readCts.Token))
        {
            Assert.Equal(payload, reply.ToArray());
            break;
        }
    }

    // --------------------------------------------------- multiple messages

    [Fact]
    public async Task Client_SendsMultipleMessages_Server_ReceivesAll_InOrder()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var server = IUdtServer.Create(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)server.LocalEndPoint;

        const int messageCount = 5;
        var receivedMessages = new List<byte[]>();
        var allReceived = new TaskCompletionSource<bool>();

        _ = Task.Run(async () =>
        {
            await foreach (var session in server.AcceptAllAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    await foreach (var message in session.ReadAllAsync(cts.Token))
                    {
                        receivedMessages.Add(message.ToArray());
                        if (receivedMessages.Count == messageCount)
                        {
                            allReceived.TrySetResult(true);
                            break;
                        }
                    }
                }, cts.Token);
                break; // one client session
            }
        }, cts.Token);

        await using var client = UdtClient.Connect(serverEndPoint);
        var payloads = Enumerable.Range(1, messageCount)
            .Select(i => System.Text.Encoding.UTF8.GetBytes($"message-{i}"))
            .ToArray();

        foreach (var p in payloads)
            await client.SendAsync(p, cts.Token);

        await allReceived.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(messageCount, receivedMessages.Count);
        for (int i = 0; i < messageCount; i++)
            Assert.Equal(payloads[i], receivedMessages[i]);
    }

    // ------------------------------------------- multiple independent clients

    [Fact]
    public async Task Server_AcceptsMultipleIndependentSessions()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var server = IUdtServer.Create(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)server.LocalEndPoint;

        const int clientCount = 3;
        var serverReceived = new System.Collections.Concurrent.ConcurrentBag<string>();
        var allDone = new TaskCompletionSource<bool>();

        _ = Task.Run(async () =>
        {
            await foreach (var session in server.AcceptAllAsync(cts.Token))
            {
                _ = Task.Run(async () =>
                {
                    await foreach (var message in session.ReadAllAsync(cts.Token))
                    {
                        serverReceived.Add(System.Text.Encoding.UTF8.GetString(message.Span));
                        if (serverReceived.Count >= clientCount)
                            allDone.TrySetResult(true);
                        break;
                    }
                }, cts.Token);
            }
        }, cts.Token);

        // Three clients each send a unique message
        var clients = Enumerable.Range(1, clientCount)
            .Select(_ => UdtClient.Connect(serverEndPoint))
            .ToArray();

        for (int i = 0; i < clientCount; i++)
            await clients[i].SendAsync(System.Text.Encoding.UTF8.GetBytes($"client-{i + 1}"), cts.Token);

        await allDone.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        await Task.WhenAll(clients.Select(c => c.DisposeAsync().AsTask()));

        Assert.Equal(clientCount, serverReceived.Count);
        for (int i = 1; i <= clientCount; i++)
            Assert.Contains($"client-{i}", serverReceived);
    }

    // -------------------------------------------- large payload (near 64 KB)

    [Fact]
    public async Task Client_SendsLargePayload_Server_ReceivesIntact()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var server = IUdtServer.Create(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndPoint = (IPEndPoint)server.LocalEndPoint;

        var payload = new byte[60_000];
        new Random(42).NextBytes(payload);

        var serverTask = Task.Run(async () =>
        {
            await foreach (var session in server.AcceptAllAsync(cts.Token))
            {
                await foreach (var message in session.ReadAllAsync(cts.Token))
                    return message.ToArray();
            }
            return [];
        }, cts.Token);

        await using var client = UdtClient.Connect(serverEndPoint);
        await client.SendAsync(payload, cts.Token);

        var received = await serverTask.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(payload, received);
    }

    // --------------------------------------------------------- graceful dispose

    [Fact]
    public async Task Server_Dispose_CompletesWithoutHanging()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var server = IUdtServer.Create(new IPEndPoint(IPAddress.Loopback, 0));
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
