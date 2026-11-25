using System.Net;
using System.Net.Sockets;
using Sensor.Communication.Transport;

namespace Sensor.Tests.Communication;

/// <summary>Integration tests over real loopback sockets (port 0 = OS-assigned free port).</summary>
public sealed class UdpDatagramTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static UdpDatagramTransport Bind() => new(new IPEndPoint(IPAddress.Loopback, 0));

    [Fact]
    public async Task SendAsync_ThenReceiveAsync_DeliversOneWholeDatagramWithSender()
    {
        using var receiver = Bind();
        using var sender = Bind();
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[2048];

        await sender.SendAsync(ProtocolExamples.CameraSetupHeartbeat, receiver.LocalEndPoint, cts.Token);
        var result = await receiver.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(ProtocolExamples.CameraSetupHeartbeat, buffer[..result.BytesReceived]);
        Assert.Equal(sender.LocalEndPoint, result.RemoteEndPoint);
    }

    [Fact]
    public async Task ReceiveAsync_KeepsDatagramBoundaries()
    {
        using var receiver = Bind();
        using var sender = Bind();
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[2048];

        await sender.SendAsync(new byte[35], receiver.LocalEndPoint, cts.Token);
        await sender.SendAsync(new byte[426], receiver.LocalEndPoint, cts.Token);

        Assert.Equal(35, (await receiver.ReceiveAsync(buffer, cts.Token)).BytesReceived);
        Assert.Equal(426, (await receiver.ReceiveAsync(buffer, cts.Token)).BytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_AfterSendingToClosedPort_StillReceives()
    {
        // Regression for the Windows ICMP "port unreachable" -> ConnectionReset behavior.
        using var receiver = Bind();
        using var peer = Bind();
        using var cts = new CancellationTokenSource(Timeout);
        var closedPort = ReserveThenReleasePort();

        await receiver.SendAsync(new byte[27], new IPEndPoint(IPAddress.Loopback, closedPort), cts.Token);
        await Task.Delay(100, cts.Token); // Give the ICMP reply time to arrive before receiving.
        await peer.SendAsync(new byte[28], receiver.LocalEndPoint, cts.Token);

        var result = await receiver.ReceiveAsync(new byte[2048], cts.Token);

        Assert.Equal(28, result.BytesReceived);
    }

    [Fact]
    public async Task ReceiveAsync_Cancelled_Throws()
    {
        using var receiver = Bind();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await receiver.ReceiveAsync(new byte[2048], cts.Token));
    }

    [Fact]
    public void Constructor_PortInUse_Throws()
    {
        using var first = Bind();

        Assert.Throws<SocketException>(() => new UdpDatagramTransport(first.LocalEndPoint));
    }

    private static int ReserveThenReleasePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
