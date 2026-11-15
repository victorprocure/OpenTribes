// DemoFile.Parse on recordings cut off inside a record: the whole records before the cut come back with Complete false.
namespace OpenTribes.Tests;

using OpenTribes.Game;

public class DemoTruncationTests
{
    private static List<byte> Header()
    {
        var f = new List<byte>();
        f.AddRange(BitConverter.GetBytes(2052u));
        f.AddRange(BitConverter.GetBytes(2048u));
        return f;
    }

    private static void Packet(List<byte> f, uint time, byte[] body)
    {
        f.AddRange(BitConverter.GetBytes(time));
        f.Add(0xff);
        f.AddRange(BitConverter.GetBytes((ushort)body.Length));
        f.AddRange(body);
    }

    private static void Move(List<byte> f, uint time)
    {
        f.AddRange(BitConverter.GetBytes(time));
        f.Add(1);
        f.Add(1);
        f.AddRange(BitConverter.GetBytes(1.0f));
        f.AddRange(new byte[28]);
    }

    private static void Action(List<byte> f, uint time)
    {
        f.AddRange(BitConverter.GetBytes(time));
        f.Add(1);
        f.Add(0);
        f.AddRange(BitConverter.GetBytes(1.0f));
        f.AddRange(BitConverter.GetBytes(7u));
    }

    // One of each record, then the end record.
    private static byte[] Whole()
    {
        var f = Header();
        Packet(f, 10, [1, 2, 3, 4, 5]);
        Move(f, 20);
        Action(f, 30);
        f.AddRange(BitConverter.GetBytes(0xffffffffu));
        return [.. f];
    }

    [Fact]
    public void WholeFileStillParsesComplete()
    {
        var d = DemoFile.Parse(Whole());
        Assert.True(d.Complete);
        Assert.Equal((1, 1, 1), (d.Packets, d.Moves, d.Actions));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, d.Records[0].Packet);
        Assert.Equal(1.0f, d.Records[1].Move.Forward);
        Assert.Equal(7u, d.Records[2].ActionId);
    }

    [Fact]
    public void EveryCutInsideARecordGivesTheRecordsBeforeIt()
    {
        var whole = Whole();
        // The records end at these offsets: header 8, packet 8 + 4 + 1 + 2 + 5 = 20, move 20 + 4 + 2 + 32 = 58, action 58 + 4 + 2 + 8 = 72.
        int[] ends = [8, 20, 58, 72];
        for (var cut = 8; cut < whole.Length; ++cut)
        {
            var d = DemoFile.Parse(whole.AsSpan(0, cut).ToArray());
            Assert.False(d.Complete);
            var expected = ends.Count(e => e > 8 && e <= cut);
            Assert.Equal(expected, d.Records.Count);
            Assert.Equal(expected >= 1 ? 1 : 0, d.Packets);
            Assert.Equal(expected >= 2 ? 1 : 0, d.Moves);
            Assert.Equal(expected >= 3 ? 1 : 0, d.Actions);
        }
    }

    [Fact]
    public void TheEndRecordMarksTheFileComplete()
    {
        var whole = Whole();
        Assert.True(DemoFile.Parse(whole).Complete);
        Assert.False(DemoFile.Parse(whole.AsSpan(0, whole.Length - 1).ToArray()).Complete);
    }

    [Fact]
    public void BadKindsAndAShortHeaderStillThrow()
    {
        Assert.Throws<InvalidDataException>(() => DemoFile.Parse([1, 2, 3]));
        var f = Header();
        f.AddRange(BitConverter.GetBytes(5u));
        f.Add(0x7f);
        Assert.Throws<InvalidDataException>(() => DemoFile.Parse([.. f]));
        f = Header();
        f.AddRange(BitConverter.GetBytes(5u));
        f.AddRange([1, 9]);
        Assert.Throws<InvalidDataException>(() => DemoFile.Parse([.. f]));
    }
}
