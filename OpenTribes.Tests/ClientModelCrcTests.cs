// Client side model check of the datablock preload (docs/datablocks.md 7.1): the CRC the server sent with a shape name must
// equal the CRC of the local model, else the preload fails with the "Client side model" text.
using OpenTribes.Core;
using OpenTribes.Game;
using static OpenTribes.Tests.Fixtures;

namespace OpenTribes.Tests;

public class ClientModelCrcTests
{
    private static List<byte> ShapeWithTexture(string texture)
    {
        var list = new List<byte>();
        PutU32(list, 1); // details
        PutU32(list, 1); // count
        var rec = new List<byte>(new byte[64]);
        SetU32(rec, 0, 3); // flags & 0xf == 3: a texture
        byte[] nb = ByteString.ToBytes(texture);
        for (int i = 0; i < nb.Length; ++i)
            rec[16 + i] = nb[i];
        list.AddRange(rec);
        var body = new List<byte>();
        PutStr(body, "meshdata");
        body.AddRange(Pers("TS::MaterialList", 3, list));
        return Pers("TS::Shape", 8, body);
    }

    private static ClientDataBlock Block(string field, string name, long crc, bool materials)
    {
        var b = new ClientDataBlock { Class = "Test" };
        b.Fields.Add(new DecodedField(field, name));
        b.Fields.Add(new DecodedField(field + ".crc", crc));
        b.Fields.Add(new DecodedField(field + ".materials", materials ? 1L : 0L));
        return b;
    }

    private static ClientDataBlocks Client(ResourceManager res) => new() { Resources = res };

    private static string Root(string name)
    {
        string root = Path.Combine(TestPaths.TmpDir(""), name);
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        PutFile(Path.Combine(root, "base/larmor.dts"), "abc");
        PutFile(Path.Combine(root, "base/mat.dts"), ShapeWithTexture("t1.bmp"));
        PutFile(Path.Combine(root, "base/t1.bmp"), "texture one");
        PutFile(Path.Combine(root, "base/b.DTS"), "bullet");
        PutFile(Path.Combine(root, "base/e.dts"), "explosion");
        return root;
    }

    private static ResourceManager Mount(string root)
    {
        var res = new ResourceManager(root);
        res.SetSearchPath("base");
        return res;
    }

    [Fact]
    public void CrcInput()
    {
        ResourceManager res = Mount(Root("crc_input"));
        // Shape bytes alone: CRC-32 of "abc", seed 0xffffffff, no final inversion (0xcadbbe3d, DataBlocksTests).
        Assert.Equal(0xcadbbe3du, DataBlockManager.ValidationCrc(res, "larmor.dts", false));
        // The materials flag adds the texture of the material list on top of the shape.
        uint shapeOnly = DataBlockManager.ValidationCrc(res, "mat.dts", false);
        uint withTexture = DataBlockManager.ValidationCrc(res, "mat.dts", true);
        Assert.NotEqual(shapeOnly, withTexture);
        // A shape without textures is the same with and without the flag.
        Assert.Equal(0xcadbbe3du, DataBlockManager.ValidationCrc(res, "larmor.dts", true));
    }

    [Fact]
    public void Comparison()
    {
        ResourceManager res = Mount(Root("crc_compare"));
        ClientDataBlocks c = Client(res);
        uint larmor = DataBlockManager.ValidationCrc(res, "larmor.dts", false);
        // ShapeBase: the name on the wire has no extension, the model is <name>.dts.
        Assert.Null(c.CheckModel(Block("shapeFile", "larmor", larmor, false)));
        Assert.Equal("Client side model larmor.dts or its textures do not match the server's and the server requires validation of this model or textures.",
                     c.CheckModel(Block("shapeFile", "larmor", larmor ^ 1, false)));
        // Projectile and explosion names are used as written.
        uint bullet = DataBlockManager.ValidationCrc(res, "b.DTS", false);
        Assert.Null(c.CheckModel(Block("bulletShapeName", "b.DTS", bullet, false)));
        Assert.Contains("model b.DTS ", c.CheckModel(Block("bulletShapeName", "b.DTS", bullet + 1, false)));
        uint explosion = DataBlockManager.ValidationCrc(res, "e.dts", false);
        Assert.Null(c.CheckModel(Block("shapeName", "e.dts", explosion, false)));
        Assert.Contains("model e.dts ", c.CheckModel(Block("shapeName", "e.dts", explosion + 1, false)));
        // An empty name is not loaded and not checked; a block without CRC fields is not checked.
        Assert.Null(c.CheckModel(Block("shapeFile", "", 5, false)));
        Assert.Null(c.CheckModel(new ClientDataBlock { Fields = { new DecodedField("shapeFile", "larmor") } }));
        // Without game files there is no check.
        Assert.Null(new ClientDataBlocks().CheckModel(Block("shapeFile", "larmor", 5, false)));
    }

    [Fact]
    public void Textures()
    {
        string root = Root("crc_tex");
        ResourceManager res = Mount(root);
        ClientDataBlocks c = Client(res);
        uint wire = DataBlockManager.ValidationCrc(res, "mat.dts", true);
        Assert.Null(c.CheckModel(Block("shapeFile", "mat", wire, true)));
        // The texture differs locally: with the materials flag the check fails, without it the shape alone still matches.
        PutFile(Path.Combine(root, "base/t1.bmp"), "texture two");
        ResourceManager changed = Mount(root);
        Assert.NotNull(Client(changed).CheckModel(Block("shapeFile", "mat", wire, true)));
        Assert.Null(Client(changed).CheckModel(Block("shapeFile", "mat", DataBlockManager.ValidationCrc(res, "mat.dts", false), false)));
    }
}
