// UdpSocket against the C++ semantics of src/net/udp.cpp: a datagram larger than the caller's buffer is cut to
// the buffer (recvfrom), not dropped; an empty poll returns 0.
namespace OpenTribes.Tests;

using OpenTribes.Net;

public class UdpTests
{
    [Fact]
    public void TestReceiveCutsOversizedDatagram()
    {
        using var a = new UdpSocket();
        using var b = new UdpSocket();
        Assert.True(a.Open(0) && b.Open(0));
        var to = new Address(0x7f000001u, b.Port());
        var big = new byte[3000];
        for (var i = 0; i < big.Length; ++i)
            big[i] = (byte)(i * 7);
        Assert.True(a.Send(to, big));
        var buf = new byte[0x5f4];
        var n = 0;
        for (var tries = 0; tries < 200 && n == 0; ++tries)
        {
            n = b.Receive(out _, buf);
            if (n == 0)
                Thread.Sleep(5);
        }
        Assert.Equal(buf.Length, n);
        Assert.True(buf.AsSpan().SequenceEqual(big.AsSpan(0, buf.Length)));
        Assert.Equal(0, b.Receive(out _, buf));  // the rest of the datagram is gone, nothing pending
        var small = new byte[] { 1, 2, 3 };
        Assert.True(a.Send(to, small));
        n = 0;
        for (var tries = 0; tries < 200 && n == 0; ++tries)
        {
            n = b.Receive(out var from, buf);
            if (n == 0)
                Thread.Sleep(5);
            else
                Assert.Equal(to.Ip, from.Ip);
        }
        Assert.Equal(3, n);
        Assert.True(buf.AsSpan(0, 3).SequenceEqual(small));
    }

    [Fact]
    public void MemorySocketsPassPacketsInProcessWithoutAnOsSocket() // LOOPTransport 0058d744, connect 0058d7d4
    {
        using var server = new UdpSocket();
        using var client = new UdpSocket();
        Assert.True(server.OpenMemory(0));
        Assert.NotEqual(0, server.Port());
        var to = new Address(0x7f000001u, server.Port());
        Assert.True(client.OpenToward(to));
        Assert.True(client.Send(to, [1, 2, 3]));
        var buf = new byte[16];
        Assert.Equal(3, server.Receive(out var from, buf));
        Assert.Equal(client.Port(), from.Port);
        Assert.Equal(0u + 0x7f000001u, from.Ip);
        Assert.Equal(0, server.Receive(out _, buf));
        // The answer goes back by the sender's address.
        Assert.True(server.Send(from, [9]));
        Assert.Equal(1, client.Receive(out _, buf));
        Assert.Equal(9, buf[0]);
    }

    [Fact]
    public void AMemorySocketIsNotReachableFromARealSocket()
    {
        using var server = new UdpSocket();
        using var real = new UdpSocket();
        Assert.True(server.OpenMemory(0) && real.Open(0));
        Assert.True(real.Send(new Address(0x7f000001u, server.Port()), [1]));
        Assert.False(server.WaitRead(30));
        // Nor does it send out: a target that is no memory socket is refused.
        Assert.False(server.Send(new Address(0x7f000001u, real.Port()), [1]));
    }

    [Fact]
    public void ClosingAMemorySocketFreesItsPort()
    {
        var s = new UdpSocket();
        Assert.True(s.OpenMemory(0));
        var port = s.Port();
        using var other = new UdpSocket();
        Assert.False(other.OpenMemory(port));
        s.Close();
        Assert.True(other.OpenMemory(port));
        using var ordinary = new UdpSocket(); // a target that is no memory socket gets an ordinary socket
        Assert.True(ordinary.OpenToward(new Address(0x7f000001u, 1)));
    }
}
