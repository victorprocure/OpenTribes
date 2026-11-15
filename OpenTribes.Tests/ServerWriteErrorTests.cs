// A failed console or log write loses the text and the server keeps running: the stream write of the original (00585948)
// records the WriteFile error and returns, and the C++ server ignores the printf result.
namespace OpenTribes.Tests;

using OpenTribes.Server;

public sealed class ServerWriteErrorTests
{
    // A stream whose writes fail like a closed pipe or a full disk.
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() => throw new IOException("Broken pipe");
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("No space left on device");
    }

    [Fact]
    public void BrokenStdioStreamDropsTheText()
    {
        using var broken = new BrokenStream();
        Assert.False(StdIo.TryWrite(broken, "line\n"u8, true));
        Assert.False(StdIo.TryWrite(broken, [], true));
    }

    [Fact]
    public void WorkingStdioStreamGetsTheSameBytes()
    {
        using var ms = new MemoryStream();
        Assert.True(StdIo.TryWrite(ms, "a\n"u8, true));
        Assert.True(StdIo.TryWrite(ms, "b"u8, false));
        Assert.Equal("a\nb"u8.ToArray(), ms.ToArray());
    }

    [Fact]
    public void FullDiskLogLosesTheLines()
    {
        if (!File.Exists("/dev/full"))
            return;
        static DateTime When() => new(2026, 10, 9, 7, 5, 3);
        using var log = new ConsoleLog("/dev/full", When);
        log.SetMode(1);
        log.Write("one");
        log.Write("two");
        log.SetMode(2);
        log.Write("three");
        log.Write("four");
        log.SetMode(0);
        Assert.Equal(0, log.Mode());
    }
}
