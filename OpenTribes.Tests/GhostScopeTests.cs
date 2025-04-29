// Port of tests/test_ghost_scope.cpp: net flags, camera record, the GameBase priority (004488e8) and the scope query
// (docs/game-stream-ghosts.md 6.1-6.3). 31 asserts.
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;

namespace OpenTribes.Tests;

public class GhostScopeTests
{
    private sealed class Named : SimObject
    {
        private readonly string mClass;
        public readonly string Label;
        public Named(string cls, string l)
        {
            mClass = cls;
            Label = l;
        }
        public override string ClassName() => mClass;
    }

    private sealed class ArgsVolume : SimObject
    {
        public override string ClassName() => "SimVolume";
        public override uint NetFlags(uint constructed) => constructed & ~0x140u;
    }

    private static bool Near(float a, double b, double eps = 1e-6) => Math.Abs(a - b) < eps;

    private static void Flags()
    {
        Named marker = new("Marker", ""), cam = new("ObserverCamera", ""), shape = new("StaticShape", ""),
              itr = new("InteriorShape", ""), other = new("SimGroup", ""), trigger = new("Trigger", "");
        Assert.True(GhostScope.NetFlags(marker) == 0x180);
        Assert.True(GhostScope.NetFlags(cam) == 0);
        Assert.True(GhostScope.NetFlags(shape) == 0x100);
        Assert.True(GhostScope.NetFlags(itr) == 0x140);
        Assert.True(GhostScope.NetFlags(other) == 0);
        Assert.True(GhostScope.NetFlags(trigger) == 0);
        var vol = new ArgsVolume();
        Assert.True(GhostScope.NetFlags(vol) == 0); // a script SimVolume without the true argument
    }

    private static CameraRecord Observer()
    {
        var rec = new CameraRecord();
        float[] pos = { 0, 0, 0 }, rot = { 0, 0, 0 };
        GhostScope.ObserverCameraRecord(pos, rot, rec);
        rec.VisibleDistance = 1500;
        return rec;
    }

    private static float Prio(PriorityInputs input, CameraRecord rec, uint skip = 0, int recId = 100, uint mask = 0xffffffffu) =>
        GhostScope.GameBasePriority(input, rec, recId, false, mask, skip, new PriorityWeights());

    private static PriorityInputs At(float x, float y, float z, uint type = ObjectType.kStaticType)
    {
        var input = new PriorityInputs();
        float[] c = { x, y, z };
        for (int i = 0; i < 3; ++i)
        {
            input.Box[i] = c[i] - 1;
            input.Box[i + 3] = c[i] + 1;
        }
        input.Type = type;
        return input;
    }

    private static void CameraRecordTest()
    {
        CameraRecord rec = Observer();
        Assert.True(rec.Dir[0] == 0 && rec.Dir[1] == 1 && rec.Dir[2] == 0);
        Assert.True(rec.Fov == 45.0f);
        Assert.True(Near(rec.SinHalfFov, -0.487174, 1e-5) && Near(rec.CosHalfFov, -0.873305, 1e-5));
    }

    private static void Priority()
    {
        CameraRecord rec = Observer();
        // In front: fov term + distance term.
        Assert.True(Near(Prio(At(0, 100, 0), rec), 1.0 + 0.4 * (1 - 100.0 / 1500)));
        // Straight behind (outside cos 22.5 rad): distance term only.
        Assert.True(Near(Prio(At(0, -100, 0), rec), 0.4 * (1 - 100.0 / 1500)));
        // Beyond the visible distance only the fov term remains.
        Assert.True(Near(Prio(At(0, 2000, 0), rec), 1.0));
        // Item interest, skips, ownership, Player interest.
        Assert.True(Near(Prio(At(0, 100, 0, ObjectType.kItemType), rec), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.05));
        Assert.True(Near(Prio(At(0, 100, 0), rec, 3), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.3));
        PriorityInputs owned = At(0, 100, 0);
        owned.OwnerId = 100;
        Assert.True(Near(Prio(owned, rec), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.2));
        Assert.True(Near(Prio(At(0, 100, 0, ObjectType.kPlayerType), rec), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.15));
        // Velocity term: |dir x v| through the bit-level square root, (0,0,-10) -> 10.25.
        PriorityInputs moving = At(0, 100, 0);
        moving.Velocity[0] = 10;
        Assert.True(Near(Prio(moving, rec), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.4 * (10.25 / 1500)));
        // Projectile coming at the camera: 0.3 + 0.2 * speed toward it.
        PriorityInputs shot = At(0, 100, 0, 0x100);
        shot.Velocity[1] = -10;
        Assert.True(Near(Prio(shot, rec), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.2 * 2.3, 1e-5));
        shot.Velocity[1] = 10;
        Assert.True(Near(Prio(shot, rec), 1.0 + 0.4 * (1 - 100.0 / 1500) + 0.2 * 0.3));
        // The record object itself; a laser with mask bit 27; a centre on the camera.
        Assert.True(GhostScope.GameBasePriority(At(0, 100, 0), rec, 100, true, 0xffffffffu, 0, new PriorityWeights()) == 10.0f);
        PriorityInputs laser = At(0, 100, 0);
        laser.Laser = true;
        Assert.True(Prio(laser, rec, 0, 100, 0x08000000u) == 9.89999962f);
        Assert.True(Prio(laser, rec, 0, 100, 0x1u) != 9.89999962f);
        Assert.True(Near(Prio(At(0, 0, 0), rec), 1.0 + 0.4 * (1 - 0.001 / 1500)));
        // Weights come from the variables.
        var w = new PriorityWeights { Fov = 2 };
        Assert.True(Near(GhostScope.GameBasePriority(At(0, 100, 0), rec, 100, false, 0xffffffffu, 0, w), 2.0 + 0.4 * (1 - 100.0 / 1500)));
    }

    private static Container.Entry E(object obj, uint type, float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(obj, type, Float6.Of(x0, y0, z0, x1, y1, z1), false, null);

    private static void ScopeQuery()
    {
        var c = new Container();
        Named t = new("SimTerrain", "T"), a = new("StaticShape", "a"), m = new("Marker", "m"), b = new("Sensor", "b"),
              itr = new("InteriorShape", "I");
        Container.Entry te = E(t, ObjectType.kTerrainType, -500, -500, 0, 500, 500, 300);
        te.DatabaseKind = Container.Database.Terrain;
        te.ContainerMask = 0xffffffffu;
        te.VisibleDistance = 600;
        c.Add(te);
        c.Add(E(a, ObjectType.kStaticType, 10, 10, 10, 12, 12, 12));
        c.Add(E(m, ObjectType.kMarkerType, 20, 10, 10, 22, 12, 12));
        c.Add(E(b, ObjectType.kStaticType, 30, 10, 10, 32, 12, 12));
        c.Add(E(itr, ObjectType.kInteriorType, 40, 10, 10, 42, 12, 12));
        foreach (int client in new[] { 2050, 2049 })
        {
            var gm = new GhostManager(client);
            string order = "";
            gm.OnNewGhost = (index, obj) =>
            {
                Assert.True(index == order.Length);
                bool camera = obj.ClassName() == "ObserverCamera";
                order += camera ? "C" : ((Named)obj).Label;
            };
            var cam = new ObserverCamera();
            cam.Position[2] = 50;
            gm.SetScopeObject(cam);
            GhostScope.ScopeGhosts(gm, c);
            // Camera first, then the terrain database; the 0x80 Marker only for client 2049; scope-always objects
            // (terrain, interior) are not marked by the traversal.
            Assert.True(order == (client == 2049 ? "Camb" : "Cab"));
            Assert.True(gm.Camera.Object == cam && gm.Camera.VisibleDistance == 1500);
        }
    }

    // A control object whose eye transform (vtable +0x110) lies above its object transform, like a Player standing on a
    // floor.
    private sealed class EyeBase : GameBase
    {
        public float EyeHeight;
        public EyeBase(ClassContext ctx) : base("Player", Kind.ShapeBase, -1, Array.Empty<PersistDef>(), ctx) { }
        public override Transform EyeTransform()
        {
            Transform t = Transform;
            t.P[2] = OpenTribes.Core.Fpu.AddF(t.P[2], EyeHeight);
            return t;
        }
    }

    // docs/game-stream-ghosts.md 6.1: a GameBase scope object queries from the translation and row 1 of its eye
    // transform.
    private static void EyeScope()
    {
        var c = new Container();
        Named t = new("SimTerrain", "T"), far = new("StaticShape", "f");
        Container.Entry te = E(t, ObjectType.kTerrainType, -5000, -5000, 0, 5000, 5000, 5000);
        te.DatabaseKind = Container.Database.Terrain;
        te.ContainerMask = 0xffffffffu;
        te.VisibleDistance = 600;
        c.Add(te);
        c.Add(E(far, ObjectType.kStaticType, 0, 0, 1699, 2, 2, 1701));
        var ctx = new ClassContext();
        var body = new EyeBase(ctx);
        body.Transform.P[0] = 1;
        body.Transform.P[1] = 1;
        body.Transform.P[2] = 100;
        body.Fov = 1.5707964f;
        foreach (float eye in new[] { 0.0f, 1.8f, 900.0f })
        {
            body.EyeHeight = eye;
            var gm = new GhostManager(2049);
            int marked = 0;
            gm.OnNewGhost = (_, obj) => marked += obj == far ? 1 : 0;
            gm.SetScopeObject(body);
            GhostScope.ScopeGhosts(gm, c);
            // The camera record holds the eye translation and row 1 of the eye transform.
            Assert.True(gm.Camera.Object == body && Near(gm.Camera.Pos[0], 1) && Near(gm.Camera.Pos[2], 100.0 + eye, 1e-3));
            Assert.True(gm.Camera.Dir[1] == 1.0f);
            // 1600 m from the object transform (out of 1500); 700 m from the eye at +900 (in).
            Assert.True(marked == (eye > 800 ? 1 : 0));
        }
    }

    [Fact]
    public void Main()
    {
        Numbers.EnterScriptFpuMode();
        Flags();
        CameraRecordTest();
        Priority();
        ScopeQuery();
        EyeScope();
    }
}
