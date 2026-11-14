// A generated `.dts` (TS::Shape v8) for the render tests: R5 allows only synthetic fixtures. Layout: docs/interiors.md 8.2
// and 8.3, material list docs/collision.md 4.2.
namespace OpenTribes.Tests;

using System.Text;

public static class SyntheticShape
{
    private static void Pers(MemoryStream o, string name, uint version, byte[] body)
    {
        var tmp = new MemoryStream();
        var w = new BinaryWriter(tmp);
        var nm = Encoding.ASCII.GetBytes(name);
        w.Write((ushort)nm.Length);
        w.Write(nm);
        if ((nm.Length & 1) != 0)
            w.Write((byte)0);
        w.Write(version);
        w.Write(body);
        var ow = new BinaryWriter(o);
        ow.Write(Encoding.ASCII.GetBytes("PERS"));
        ow.Write((uint)tmp.Length);
        ow.Write(tmp.ToArray());
    }

    private static byte[] Fixed(string s, int n)
    {
        var b = new byte[n];
        Encoding.ASCII.GetBytes(s, 0, Math.Min(s.Length, n - 1), b, 0);
        return b;
    }

    // Shape: nodes bounds(0), detail0(1), detail1(2) under node 0; two objects with one quad mesh; sequences `spin` (cyclic, 2 s,
    // node 1 moves by +2 in x over half the sequence, one trigger at 0.5 with value 1) and `frames` (one-shot, 1 s, an image list
    // that sets remap[0] to 0 then 1 at position 0.5). Details 20 and 5. One material: flags 0x402, RGB (200, 50, 50).
    public static byte[] Build(uint materialFlags = 0x402)
    {
        string[] names = ["bounds", "detail0", "detail1", "spin", "frames", "obj0", "obj1"];
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        int[] counts = [3, 2, 2, 4, 2, names.Length, 2, 2, 1];
        foreach (var c in counts)
            w.Write(c);
        w.Write(0);                 // transitions
        w.Write(1);                 // triggers
        w.Write(2.0f);              // radius
        w.Write(0f);
        w.Write(0f);
        w.Write(0f);
        for (var i = 0; i < 6; ++i)
            w.Write(i < 3 ? -1f : 1f);
        // nodes: name, parent, numSub, firstSub, defaultTransform
        void Node(short name, short parent, short numSub, short firstSub, short dt)
        {
            w.Write(name);
            w.Write(parent);
            w.Write(numSub);
            w.Write(firstSub);
            w.Write(dt);
        }
        Node(0, -1, 0, 0, 0);
        Node(1, 0, 1, 0, 0);
        Node(2, 0, 0, 0, 0);
        // sequences: name, cyclic, duration, priority, firstTrigger, numTriggers, numIfl, firstIfl
        void Seq(int name, int cyclic, float dur, int prio, int ft, int nt, int ni, int fi)
        {
            w.Write(name);
            w.Write(cyclic);
            w.Write(dur);
            w.Write(prio);
            w.Write(ft);
            w.Write(nt);
            w.Write(ni);
            w.Write(fi);
        }
        Seq(3, 1, 2.0f, 0, 0, 1, 0, 0);
        Seq(4, 0, 1.0f, 0, 0, 0, 1, 1);
        // sub sequences: sequence, numKeys, firstKey
        w.Write((short)0);
        w.Write((short)2);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)2);
        w.Write((short)2);
        // keys: position, index | flags << 16
        void Key(float p, int index, int flags)
        {
            w.Write(p);
            w.Write((uint)(index | (flags << 16)));
        }
        Key(0f, 0, 0x8000);
        Key(0.5f, 1, 0x8000);
        Key(0f, 0, 0);        // image list: slot 0 -> image 0
        Key(0.5f, 1, 0);      // slot 0 -> image 1
        // transforms: 4 x i16 quaternion, translation
        void Tr(float x)
        {
            w.Write((short)0);
            w.Write((short)0);
            w.Write((short)0);
            w.Write((short)32767);
            w.Write(x);
            w.Write(0f);
            w.Write(0f);
        }
        Tr(0f);
        Tr(2f);
        foreach (var n in names)
            w.Write(Fixed(n, 24));
        // objects: name, flags, mesh, node, pad, offset, numSub, firstSub, padding to 28 bytes
        void Obj(short name, int mesh, short node)
        {
            w.Write(name);
            w.Write((ushort)0);
            w.Write(mesh);
            w.Write(node);
            w.Write((short)0);
            w.Write(0f);
            w.Write(0f);
            w.Write(0f);
            w.Write((short)0);
            w.Write((short)0);
        }
        Obj(5, 0, 1);
        Obj(6, 0, 2);
        // details: root node, size
        w.Write(1);
        w.Write(20f);
        w.Write(2);
        w.Write(5f);
        // triggers
        w.Write(0.5f);
        w.Write(1u);
        w.Write(2);                 // remap size
        w.Write(-1);                // always node
        // mesh: a quad in the XZ plane facing -Y (clockwise seen from outside, as the stock faces)
        var mesh = new MemoryStream();
        var mw = new BinaryWriter(mesh);
        mw.Write(4);
        mw.Write(4);
        mw.Write(4);
        mw.Write(2);
        mw.Write(1);
        mw.Write(4);
        mw.Write(1.0f);
        foreach (var v in new[] { new byte[] { 0, 0, 0, 0 }, [1, 0, 0, 0], [1, 0, 1, 0], [0, 0, 1, 0] })
            mw.Write(v);
        foreach (var f in new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f })
            mw.Write(f);
        void Face(int a, int ta, int b, int tb, int c, int tc)
        {
            mw.Write(a);
            mw.Write(ta);
            mw.Write(b);
            mw.Write(tb);
            mw.Write(c);
            mw.Write(tc);
            mw.Write(0);
        }
        Face(0, 0, 2, 2, 1, 1);
        Face(0, 0, 3, 3, 2, 2);
        mw.Write(0);
        mw.Write(1f);
        mw.Write(1f);
        mw.Write(1f);
        mw.Write(-0.5f);
        mw.Write(0f);
        mw.Write(-0.5f);
        var shape = new MemoryStream();
        w.Flush();
        Pers(body, "TS::CelAnimMesh", 3, mesh.ToArray());
        w.Write(1);                 // hasMaterials
        var mat = new MemoryStream();
        var xw = new BinaryWriter(mat);
        xw.Write(1u);
        xw.Write(1u);
        var entry = new byte[0x40];
        BitConverter.GetBytes(materialFlags).CopyTo(entry, 0);
        entry[0xc] = 200;
        entry[0xd] = 50;
        entry[0xe] = 50;
        Encoding.ASCII.GetBytes("none.bmp").CopyTo(entry, 0x10);
        xw.Write(entry);
        Pers(body, "TS::MaterialList", 4, mat.ToArray());
        Pers(shape, "TS::Shape", 8, body.ToArray());
        return shape.ToArray();
    }
}
