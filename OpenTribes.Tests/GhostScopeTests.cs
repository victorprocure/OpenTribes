// Port of tests/test_ghost_scope.cpp: net flags, camera record, the GameBase priority (004488e8) and the scope query
// (docs/game-stream-ghosts.md 6.1-6.3). 31 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;

public class GhostScopeTests
{
    private sealed class Named : SimObject
    {
        private readonly string _class;
        public string Label { get; }
        public Named(string cls, string l)
        {
            _class = cls;
            Label = l;
        }
        public override string ClassName() => _class;
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
        Assert.Equal(0x180u, GhostScope.NetFlags(marker));
        Assert.Equal(0u, GhostScope.NetFlags(cam));
        Assert.Equal(0x100u, GhostScope.NetFlags(shape));
        Assert.Equal(0x140u, GhostScope.NetFlags(itr));
        Assert.Equal(0u, GhostScope.NetFlags(other));
        Assert.Equal(0u, GhostScope.NetFlags(trigger));
        var vol = new ArgsVolume();
        Assert.Equal(0u, GhostScope.NetFlags(vol)); // a script SimVolume without the true argument
    }

    private static CameraRecord Observer()
    {
        var rec = new CameraRecord();
        float[] pos = [0, 0, 0], rot = [0, 0, 0];
        GhostScope.ObserverCameraRecord(pos, rot, rec);
        rec.VisibleDistance = 1500;
        return rec;
    }

    private static float Prio(PriorityInputs input, CameraRecord rec, uint skip = 0, int recId = 100, uint mask = 0xffffffffu) =>
        GhostScope.GameBasePriority(input, rec, recId, false, mask, skip, new PriorityWeights());

    private static PriorityInputs At(float x, float y, float z, uint type = ObjectType.StaticType)
    {
        var input = new PriorityInputs();
        float[] c = [x, y, z];
        for (var i = 0; i < 3; ++i)
        {
            input.Box[i] = c[i] - 1;
            input.Box[i + 3] = c[i] + 1;
        }
        input.Type = type;
        return input;
    }

    private static void CameraRecordTest()
    {
        var rec = Observer();
        Assert.True(rec.Dir[0] == 0 && rec.Dir[1] == 1 && rec.Dir[2] == 0);
        Assert.Equal(45.0f, rec.Fov);
        Assert.True(Near(rec.SinHalfFov, -0.487174, 1e-5) && Near(rec.CosHalfFov, -0.873305, 1e-5));
    }

    private static void Priority()
    {
        var rec = Observer();
        // In front: fov term + distance term.
        Assert.True(Near(Prio(At(0, 100, 0), rec), 1.0 + (0.4 * (1 - (100.0 / 1500)))));
        // Straight behind (outside cos 22.5 rad): distance term only.
        Assert.True(Near(Prio(At(0, -100, 0), rec), 0.4 * (1 - (100.0 / 1500))));
        // Beyond the visible distance only the fov term remains.
        Assert.True(Near(Prio(At(0, 2000, 0), rec), 1.0));
        // Item interest, skips, ownership, Player interest.
        Assert.True(Near(Prio(At(0, 100, 0, ObjectType.ItemType), rec), 1.0 + (0.4 * (1 - (100.0 / 1500))) + 0.05));
        Assert.True(Near(Prio(At(0, 100, 0), rec, 3), 1.0 + (0.4 * (1 - (100.0 / 1500))) + 0.3));
        var owned = At(0, 100, 0);
        owned.OwnerId = 100;
        Assert.True(Near(Prio(owned, rec), 1.0 + (0.4 * (1 - (100.0 / 1500))) + 0.2));
        Assert.True(Near(Prio(At(0, 100, 0, ObjectType.PlayerType), rec), 1.0 + (0.4 * (1 - (100.0 / 1500))) + 0.15));
        // Velocity term: |dir x v| through the bit-level square root, (0,0,-10) -> 10.25.
        var moving = At(0, 100, 0);
        moving.Velocity[0] = 10;
        Assert.True(Near(Prio(moving, rec), 1.0 + (0.4 * (1 - (100.0 / 1500))) + (0.4 * (10.25 / 1500))));
        // Projectile coming at the camera: 0.3 + 0.2 * speed toward it.
        var shot = At(0, 100, 0, 0x100);
        shot.Velocity[1] = -10;
        Assert.True(Near(Prio(shot, rec), 1.0 + (0.4 * (1 - (100.0 / 1500))) + (0.2 * 2.3), 1e-5));
        shot.Velocity[1] = 10;
        Assert.True(Near(Prio(shot, rec), 1.0 + (0.4 * (1 - (100.0 / 1500))) + (0.2 * 0.3)));
        // The record object itself; a laser with mask bit 27; a centre on the camera.
        Assert.Equal(10.0f, GhostScope.GameBasePriority(At(0, 100, 0), rec, 100, true, 0xffffffffu, 0, new PriorityWeights()));
        var laser = At(0, 100, 0);
        laser.Laser = true;
        Assert.Equal(9.89999962f, Prio(laser, rec, 0, 100, 0x08000000u));
        Assert.NotEqual(9.89999962f, Prio(laser, rec, 0, 100, 0x1u));
        Assert.True(Near(Prio(At(0, 0, 0), rec), 1.0 + (0.4 * (1 - (0.001 / 1500)))));
        // Weights come from the variables.
        var w = new PriorityWeights { Fov = 2 };
        Assert.True(Near(GhostScope.GameBasePriority(At(0, 100, 0), rec, 100, false, 0xffffffffu, 0, w), 2.0 + (0.4 * (1 - (100.0 / 1500)))));
    }

    private static Container.Entry E(object obj, uint type, float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(obj, type, Float6.Of(x0, y0, z0, x1, y1, z1), false, null);

    private static void ScopeQuery()
    {
        var c = new Container();
        Named t = new("SimTerrain", "T"), a = new("StaticShape", "a"), m = new("Marker", "m"), b = new("Sensor", "b"),
              itr = new("InteriorShape", "I");
        var te = E(t, ObjectType.TerrainType, -500, -500, 0, 500, 500, 300);
        te.DatabaseKind = Container.Database.Terrain;
        te.ContainerMask = 0xffffffffu;
        te.VisibleDistance = 600;
        c.Add(te);
        c.Add(E(a, ObjectType.StaticType, 10, 10, 10, 12, 12, 12));
        c.Add(E(m, ObjectType.MarkerType, 20, 10, 10, 22, 12, 12));
        c.Add(E(b, ObjectType.StaticType, 30, 10, 10, 32, 12, 12));
        c.Add(E(itr, ObjectType.InteriorType, 40, 10, 10, 42, 12, 12));
        foreach (var client in new[] { 2050, 2049 })
        {
            var gm = new GhostManager(client);
            var order = "";
            gm.OnNewGhost = (index, obj) =>
            {
                Assert.True(index == order.Length);
                var camera = obj.ClassName() == "ObserverCamera";
                order += camera ? "C" : ((Named)obj).Label;
            };
            var cam = new ObserverCamera();
            cam.Position[2] = 50;
            gm.SetScopeObject(cam);
            GhostScope.ScopeGhosts(gm, c);
            // Camera first, then the terrain database; the 0x80 Marker only for client 2049; scope-always objects
            // (terrain, interior) are not marked by the traversal.
            Assert.True(order == (client == 2049 ? "Camb" : "Cab"));
            Assert.True(gm.Camera.CameraObject == cam && gm.Camera.VisibleDistance == 1500);
        }
    }

    // A control object whose eye transform (vtable +0x110) lies above its object transform, like a Player standing on a
    // floor.
    private sealed class EyeBase : GameBase
    {
        public float EyeHeight { get; set; }
        public EyeBase(ClassContext ctx) : base("Player", Kind.ShapeBase, -1, [], ctx) { }
        public override Transform EyeTransform()
        {
            var t = Transform;
            t.P[2] = Core.Fpu.AddF(t.P[2], EyeHeight);
            return t;
        }
    }

    // docs/game-stream-ghosts.md 6.1: a GameBase scope object queries from the translation and row 1 of its eye
    // transform.
    private static void EyeScope()
    {
        var c = new Container();
        Named t = new("SimTerrain", "T"), far = new("StaticShape", "f");
        var te = E(t, ObjectType.TerrainType, -5000, -5000, 0, 5000, 5000, 5000);
        te.DatabaseKind = Container.Database.Terrain;
        te.ContainerMask = 0xffffffffu;
        te.VisibleDistance = 600;
        c.Add(te);
        c.Add(E(far, ObjectType.StaticType, 0, 0, 1699, 2, 2, 1701));
        var ctx = new ClassContext();
        var body = new EyeBase(ctx);
        body.Transform.P[0] = 1;
        body.Transform.P[1] = 1;
        body.Transform.P[2] = 100;
        body.Fov = 1.5707964f;
        foreach (var eye in new[] { 0.0f, 1.8f, 900.0f })
        {
            body.EyeHeight = eye;
            var gm = new GhostManager(2049);
            var marked = 0;
            gm.OnNewGhost = (_, obj) => marked += obj == far ? 1 : 0;
            gm.SetScopeObject(body);
            GhostScope.ScopeGhosts(gm, c);
            // The camera record holds the eye translation and row 1 of the eye transform.
            Assert.True(gm.Camera.CameraObject == body && Near(gm.Camera.Pos[0], 1) && Near(gm.Camera.Pos[2], 100.0 + eye, 1e-3));
            Assert.Equal(1.0f, gm.Camera.Dir[1]);
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
