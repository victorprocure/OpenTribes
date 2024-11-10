// UdpSocket against the C++ semantics of src/net/udp.cpp: a datagram larger than the caller's buffer is cut to
// the buffer (recvfrom), not dropped; an empty poll returns 0.
using OpenTribes.Net;

namespace OpenTribes.Tests;

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
        for (int i = 0; i < big.Length; ++i)
            big[i] = (byte)(i * 7);
        Assert.True(a.Send(to, big));
        var buf = new byte[0x5f4];
        int n = 0;
        for (int tries = 0; tries < 200 && n == 0; ++tries)
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
        for (int tries = 0; tries < 200 && n == 0; ++tries)
        {
            n = b.Receive(out Address from, buf);
            if (n == 0)
                Thread.Sleep(5);
            else
                Assert.Equal(to.Ip, from.Ip);
        }
        Assert.Equal(3, n);
        Assert.True(buf.AsSpan(0, 3).SequenceEqual(small));
    }
}
