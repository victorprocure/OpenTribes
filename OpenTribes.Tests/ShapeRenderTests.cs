// Unit tests of the shape and effect render (ROADMAP M9 C5). Each test names the section of docs/shapes-render.md (SR) it
// follows (R9). Synthetic files only (R5): SyntheticShape builds the `.dts`.
using OpenTribes.Client.Effects;
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using Xunit.Abstractions;

namespace OpenTribes.Tests;

public class ShapeRenderTests
{
    private readonly ITestOutputHelper mOut;

    public ShapeRenderTests(ITestOutputHelper output) => mOut = output;

    private static RenderShape Shape(uint materialFlags = 0x402)
    {
        RenderShape? s = RenderShape.Parse(SyntheticShape.Build(materialFlags), "synthetic");
        Assert.NotNull(s);
        return s!;
    }

    // ---- SR 1.1, 1.2: what the render reader keeps ----

    // M4: ShapeBase_explode leaves a destroyed shape drawn when it has a debris0 detail (generator, solar panel, stations), so it can be repaired.
    [Fact]
    public void DestroyedShapeStaysDrawnOnlyWithDebrisDetail()
    {
        var wreck = new RenderShape
        {
            Names = new[] { "root", "debris0" },
            Nodes = new[] { new RsNode { Name = 0, Parent = -1 }, new RsNode { Name = 1, Parent = -1 } },
            Details = new[] { new RsDetail { RootNode = 0, Size = 25 }, new RsDetail { RootNode = 1, Size = 0 } },
        };
        Assert.True(ShapeVisual.HasDebrisDetail(wreck));
        var plain = new RenderShape
        {
            Names = new[] { "root", "debris0" },
            Nodes = new[] { new RsNode { Name = 0, Parent = -1 } },
            Details = new[] { new RsDetail { RootNode = 0, Size = 25 } },
        };
        Assert.False(ShapeVisual.HasDebrisDetail(plain));
    }

    [Fact]
    public void ReaderKeepsTextureVerticesTriggersImageListsAndMaterials()
    {
        RenderShape s = Shape();
        Assert.Equal(3, s.Nodes.Length);
        Assert.Equal(2, s.Sequences.Length);
        Assert.Single(s.Triggers);
        Assert.Equal(1, s.Sequences[1].NumIfl);                 // 1.2 sequence tail
        Assert.Equal(1, s.Sequences[0].NumTriggers);
        RsMesh m = s.Meshes[0];
        Assert.Equal(8, m.TexVerts.Length);                     // 1.2 texture vertices
        Assert.Equal(2, m.Faces[0].T1);
        Assert.Equal(4, m.TexVertsPerFrame);
        Assert.Single(s.Materials);                             // 1.3 material list
        Assert.Equal(2, s.Materials[0].Type);
        Assert.Equal(200, s.Materials[0].R);
        Assert.Equal(2, s.RemapSize);                           // 1.2 header int after the arrays
        Assert.Equal(3, s.FindName("spin"));
        Assert.Equal(1, s.FindSequence("frames"));
    }

    [Fact]
    public void MaterialFlagsSplitIntoTypeShadingAndKeyedAndTranslucentBits() // SR 1.3.1
    {
        var m = new RsMaterial { Flags = 0x2403 };
        Assert.Equal(3, m.Type);
        Assert.Equal(4, m.Shading);
        Assert.True(m.Translucent);
        Assert.False(m.Keyed);
        Assert.True(new RsMaterial { Flags = 0x1103 }.Keyed);
        Assert.Equal(1, new RsMaterial { Flags = 0x103 }.Shading);
    }

    // ---- SR 2.3 detail level choice ----

    [Fact]
    public void DetailChoiceTakesTheFirstDetailAtMostTheSize() // SR 2.3 steps 2 and 3
    {
        float[] armor = { 36, 10, 2 };
        Assert.Equal(0, RenderShape.ChooseDetail(armor, 40));
        Assert.Equal(0, RenderShape.ChooseDetail(armor, 36));
        Assert.Equal(1, RenderShape.ChooseDetail(armor, 20));
        Assert.Equal(1, RenderShape.ChooseDetail(armor, 10));
        Assert.Equal(2, RenderShape.ChooseDetail(armor, 5));
        Assert.Equal(2, RenderShape.ChooseDetail(armor, 2));
        Assert.Equal(-1, RenderShape.ChooseDetail(armor, 1));   // no detail matches: not drawn
        Assert.Equal(1, RenderShape.ChooseDetail(armor, -20));  // |size|
    }

    [Fact]
    public void ADetailOfSizeZeroAndASingleDetailFollowTheSpec() // SR 2.3 steps 2 and 3, the stock tail details of size 0
    {
        float[] flyer = { 128, 32, 25, 5, 2, 0 };
        Assert.Equal(-1, RenderShape.ChooseDetail(flyer, 0.5));  // the stored size 0 matches: -1, the shape vanishes
        Assert.Equal(4, RenderShape.ChooseDetail(flyer, 3));
        Assert.Equal(0, RenderShape.ChooseDetail(new float[] { 0 }, 0.1));   // fewer than 2 details: 0
        Assert.Equal(1, RenderShape.ChooseDetail(flyer, 100, minDetail: -1, maxDetail: 1));   // clamped to [min, max]
    }

    [Fact]
    public void DetailFollowsTheProjectedRadiusAndThePreferenceScale() // SR 2.3 step 1
    {
        RenderShape s = Shape();                                 // details 20 and 5, radius 2
        var r = new ShapeRenderer(new ShapeAssets(null, new WorldAssets(new ResourceManager(Path.GetTempPath()), PaletteTables.Grayscale())));
        var cam = new Camera3D(0, 0, 0, 0, 0, 90, 640, 480, 0.5, 1000);
        Xf at(double d) => Xf.Translate(0, d, 0);
        Assert.Equal(0, r.DetailFor(s, cam, at(20), 1));         // 320 * 2 / 20 = 32 pixels
        Assert.Equal(1, r.DetailFor(s, cam, at(100), 1));        // 6.4 pixels
        Assert.Equal(-1, r.DetailFor(s, cam, at(1000), 1));      // 0.64 pixels
        Assert.Equal(1, r.DetailFor(s, cam, at(20), 0.2));       // the shape detail preference: coarser sooner
    }

    [Fact]
    public void RootTransformIsTheNodeZeroDefaultAndStaysOutOfTheNodeTable() // SR 3.6 image root transform
    {
        RenderShape sh = Shape();
        int di = sh.Nodes[0].DefaultTransform;
        sh.Transforms[di] = new RsTransform { Qw = 1, Tx = -0.002, Ty = 0.184, Tz = -0.164 };  // the disc launcher's node 0
        Xf r = sh.RootTransform();
        Assert.Equal((-0.002, 0.184, -0.164), r.Apply(0, 0, 0));
        var inst = new RenderInstance(sh);
        inst.Animate(0);
        Assert.Equal((0.0, 0.0, 0.0), inst.NodeWorld[0].Apply(0, 0, 0));
    }

    // ---- SR 3.1 threads, keys ----

    [Fact]
    public void LowerbackNodeHangsOnTheUprightFrameInAViewMode() // SR 3.4, 5.2
    {
        RenderShape sh = Shape();
        sh.Names[sh.Nodes[1].Name] = "lowerback36";
        var inst = new RenderInstance(sh);
        RenderThread spin = inst.AddThread("spin")!;
        spin.Position = 0.3;
        inst.Animate(0);
        Xf plain = inst.NodeWorld[1];
        inst.ViewMode = 1;
        inst.Animate(0);
        Xf viewed = inst.NodeWorld[1];
        Xf expect = plain.Then(Xf.Euler(0, 0, Math.PI / 2));
        Assert.Equal(expect.M0, viewed.M0, 9);
        Assert.Equal(expect.M1, viewed.M1, 9);
        Assert.Equal(expect.M3, viewed.M3, 9);
        Assert.Equal(expect.M4, viewed.M4, 9);
        inst.ViewMode = 2;                                       // no `dummyalways root` node: the normal parent
        inst.Animate(0);
        Assert.Equal(plain.M0, inst.NodeWorld[1].M0, 9);
    }

    [Fact]
    public void CyclicThreadsWrapAndOneShotThreadsClamp() // SR 3.1
    {
        var inst = new RenderInstance(Shape());
        RenderThread spin = inst.AddThread("spin")!;
        inst.Advance(spin, 1.5);                                 // 1.5 s of a 2 s cycle
        Assert.Equal(0.75, spin.Position, 9);
        inst.Advance(spin, 1.0);
        Assert.Equal(0.25, spin.Position, 9);                    // wrapped
        RenderThread one = inst.AddThread("frames")!;
        inst.Advance(one, 5);
        Assert.Equal(1.0, one.Position, 9);                      // clamped at the end
        Assert.True(one.AtEnd(inst.Shape));
        one.TimeScale = -1;
        inst.Advance(one, 5);
        Assert.Equal(0.0, one.Position, 9);                      // backward clamps at 0
    }

    [Fact]
    public void NodeTranslationInterpolatesBetweenKeys() // SR 3.1: node transforms interpolate (collision.md T2.5a)
    {
        var inst = new RenderInstance(Shape());
        RenderThread spin = inst.AddThread("spin")!;
        spin.TimeScale = 0;
        spin.Position = 0.25;                                    // half way between the keys at 0 and 0.5
        inst.Animate(0);
        Assert.Equal(1.0, inst.NodeWorld[1].Px, 6);
        spin.Position = 0.5;
        inst.Animate(0);
        Assert.Equal(2.0, inst.NodeWorld[1].Px, 6);
        spin.Position = 0.75;                                    // cyclic: back towards the first key
        inst.Animate(0);
        Assert.Equal(1.0, inst.NodeWorld[1].Px, 6);
    }

    [Fact]
    public void FrameTriggersFireWhenTheirPositionIsPassed() // SR 3.7
    {
        var inst = new RenderInstance(Shape());
        RenderThread spin = inst.AddThread("spin")!;
        var got = new List<uint>();
        inst.Advance(spin, 0.9, got);                            // 0 to 0.45
        Assert.Empty(got);
        inst.Advance(spin, 0.2, got);                            // 0.45 to 0.55 crosses 0.5
        Assert.Equal(new uint[] { 1 }, got);
        got.Clear();
        inst.Advance(spin, 1.0, got);                            // 0.55 to 1.05: wraps, does not cross 0.5 again before 1.05... crosses at 1.5? no
        Assert.Empty(got);
        inst.Advance(spin, 1.0, got);                            // 0.05 to 0.55 crosses 0.5 after the wrap
        Assert.Equal(new uint[] { 1 }, got);
    }

    // ---- SR 1.5 image lists ----

    [Fact]
    public void ImageListKeysWriteTheMaterialRemap() // SR 1.5
    {
        var inst = new RenderInstance(Shape());
        Assert.Equal(new[] { 0, 1 }, inst.Remap);                // identity before any thread
        RenderThread t = inst.AddThread("frames")!;
        t.TimeScale = 0;
        t.Position = 0.25;
        inst.Animate(0);
        Assert.Equal(0, inst.Remap[0]);                          // key at 0: remap[0] = 0
        t.Position = 0.6;
        inst.Animate(0);
        Assert.Equal(1, inst.Remap[0]);                          // key at 0.5: remap[0] = 1
        Assert.Equal(1, inst.Remap[1]);                          // other slots stay identity
    }

    // ---- SR 3.3 transitions ----

    [Fact]
    public void ATransitionBlendsFromThePoseAtTheSwitch() // SR 3.3
    {
        var inst = new RenderInstance(Shape());
        RenderThread t = inst.AddThread("spin")!;
        t.TimeScale = 0;
        t.Position = 0.5;                                        // node 1 at x = 2
        inst.Animate(0);
        Assert.Equal(2.0, inst.NodeWorld[1].Px, 6);
        inst.StartTransition(t, inst.Shape.FindSequence("spin"), 0, 0.12);   // new pose x = 0
        Assert.True(inst.Blending);
        inst.Animate(0);
        Assert.Equal(2.0, inst.NodeWorld[1].Px, 6);              // at the start of the blend: the old pose
        inst.Advance(0.06);
        inst.Animate(0);
        Assert.InRange(inst.NodeWorld[1].Px, 0.9, 1.1);          // half way
        inst.Advance(0.1);
        inst.Animate(0);
        Assert.Equal(0.0, inst.NodeWorld[1].Px, 6);              // done
        Assert.False(inst.Blending);
    }

    // ---- SR 2.7 sort keys and lists ----

    [Fact]
    public void SortKeyIsNearOverDepthOrPointNineNine() // SR 2.2 step 4
    {
        Assert.Equal(0.5 / 10, RenderLists.SortKey(10, 0.5), 12);
        Assert.Equal(0.99, RenderLists.SortKey(0.4, 0.5), 12);   // depth not above near
        Assert.Equal(0.99, RenderLists.SortKey(0.5, 0.5), 12);
        Assert.True(RenderLists.SortKey(5, 0.5) > RenderLists.SortKey(50, 0.5));   // larger is nearer
    }

    [Fact]
    public void ListsDrawFirstThenOpaqueThenTranslucentFarToNear() // SR 2.7
    {
        var l = new RenderLists();
        RenderImage Img(string label, int cat, double key)
        {
            var i = new RenderImage { Label = label, Category = cat, SortKey = key };
            i.Batches.Add(new DrawBatch());
            return i;
        }
        l.Add(Img("opaque1", 0, 0));
        l.Add(Img("near", 1, 0.5));
        l.Add(Img("fg", 2, 0));
        l.Add(Img("far", 1, 0.01));
        l.Add(Img("first", 3, 0));
        l.Add(Img("opaque2", 0, 0));
        l.Add(Img("empty", 1, 0.2).Also(i => i.Batches.Clear()));
        Assert.Equal(new[] { "first", "opaque1", "opaque2", "far", "near", "fg" }, l.Ordered().Select(i => i.Label).ToArray());
    }

    // ---- SR 2.9 skins ----

    [Fact]
    public void SkinNamesReplaceTheBasePrefixOrPrefixCustomSkins() // SR 2.9 algorithm (names)
    {
        Assert.Equal("beagle.larmor.bmp", Skins.Candidate("base.larmor.bmp", "beagle"));
        Assert.Equal("beagle.helmet.bmp", Skins.Candidate("helmet.bmp", "beagle"));          // custom skin path
        Assert.Equal("helmet.bmp", Skins.Candidate("helmet.bmp", "beagle", custom: false));   // $pref::useCustomSkins off
        Assert.Equal("x.bmp", Skins.Candidate("x.bmp", ""));
        var have = new HashSet<string> { "beagle.larmor.bmp" };
        Assert.Equal("beagle.larmor.bmp", Skins.Resolve("base.larmor.bmp", "beagle", have.Contains));
        Assert.Equal("base.larmor.bmp", Skins.Resolve("base.larmor.bmp", "dsword", have.Contains));   // missing: keep the original
    }

    [Fact]
    public void MountedImagesTakeOnlyTheSkinOfTheirMountTeam() // SR 2.9, Player_setImage
    {
        Assert.Equal("", Skins.ImageSkin(-1, "beagle"));      // mountItem without a team: the shape's own materials
        Assert.Equal("", Skins.ImageSkin(0, "base"));
        Assert.Equal("beagle", Skins.ImageSkin(0, "beagle"));
    }

    [Fact]
    public void DamageSkinLevelsFollowThePreference() // SR 2.9 inputs (b)
    {
        Assert.Equal(new[] { 0, 0, 1, 2, 3 }, new[] { 0.1, 0.25, 0.3, 0.6, 0.8 }.Select(d => Skins.DamageLevel(d, 2)).ToArray());
        Assert.Equal(2, Skins.DamageLevel(0.5, 1));
        Assert.Equal(0, Skins.DamageLevel(0.4, 1));
        Assert.Equal(0, Skins.DamageLevel(0.99, 0));
        Assert.Equal(new[] { 0, 1, 2 }, new[] { 0.3, 0.31, 0.71 }.Select(Skins.DamageLevelShort).ToArray());
    }

    [Fact]
    public void DamageDecalPositionsAreTheDocumentedProducts() // SR 2.9 damage decals
    {
        var (x1, y1) = Skins.DecalPosition(1, 128, 32, 128, 16);
        Assert.Equal((int)(0x01cc0a03u % 96u), x1);
        Assert.Equal((int)(unchecked((uint)(-0x34328f9b)) % 112u), y1);
        var (x2, _) = Skins.DecalPosition(2, 128, 32, 128, 16);
        Assert.Equal((int)(unchecked(0x01cc0a03u * 2) % 96u), x2);
        Assert.Equal((0, 0), Skins.DecalPosition(1, 32, 32, 16, 16));      // no room: origin
    }

    [Fact]
    public void DamageDecalsCoverTheRequestedAreaOnACopy() // SR 2.9 damage decals
    {
        var skin = new PbmpBitmap { Width = 64, Height = 64, Pixels = new byte[64 * 64], Attributes = BitmapAttributes.OwnsMemory };
        for (int i = 0; i < skin.Pixels.Length; ++i)
            skin.Pixels[i] = 5;
        var decal = new PbmpBitmap { Width = 16, Height = 16, Pixels = new byte[256], Attributes = BitmapAttributes.OwnsMemory };
        for (int i = 0; i < 256; ++i)
            decal.Pixels[i] = (byte)(i % 3 == 0 ? 0 : 9);        // index 0 stays clear
        PbmpBitmap same = Skins.ApplyDamage(skin, 0, new[] { decal });
        Assert.Same(skin, same);                                 // level 0: nothing
        PbmpBitmap worn = Skins.ApplyDamage(skin, 2, new[] { decal });
        Assert.NotSame(skin, worn);
        Assert.All(skin.Pixels, p => Assert.Equal(5, p));         // the original is untouched
        int marked = worn.Pixels.Count(p => p == 9);
        Assert.InRange(marked, 1, 64 * 64);
        PbmpBitmap worse = Skins.ApplyDamage(skin, 3, new[] { decal });
        Assert.True(worse.Pixels.Count(p => p == 9) >= marked);
        PbmpBitmap again = Skins.ApplyDamage(skin, 2, new[] { decal });
        Assert.Equal(worn.Pixels, again.Pixels);                  // deterministic
    }

    // ---- SR 4.3 explosion ----

    private static LightCurve Curve() => new(1.5, 0.4, 0.25, 0.85,
        new[] { new[] { 0.4f, 0.4f, 1f }, new[] { 1f, 1f, 1f }, new[] { 1f, 0.95f, 1f } }, new[] { 0.5, 1.0, 1.0 }, 8);

    [Fact]
    public void ExplosionLifeAndLightPhases() // SR 4.3 Life and Light curve
    {
        LightCurve c = Curve();
        Assert.Equal(600, c.LifeMs);                              // trunc(1.5 * 0.4 * 1000)
        Assert.Equal(150, c.TA);
        Assert.Equal(510, c.TB);
        var start = c.At(0)!.Value;                               // colors[0], radius factor rad[0]
        Assert.Equal(0.4f, start.r, 5);
        Assert.Equal(0.5, start.radius, 9);
        var atA = c.At(150)!.Value;                               // colors[1] at tA
        Assert.Equal(1f, atA.r, 5);
        Assert.Equal(1.0, atA.radius, 9);
        var late = c.At(555)!.Value;                              // phase 3: colors[2] * h
        Assert.Equal(0.5, late.r, 5);                             // (600 - 555) / (600 - 510) = 0.5
        Assert.Null(c.At(600));                                   // gone after the life
        Assert.Null(c.At(-1));
    }

    [Fact]
    public void AnExplosionLivesForItsLifeAndGivesALightWhileItDoes() // SR 4.3, 4.9
    {
        RenderShape s = Shape();
        var db = new ClientDataBlock { Group = 20, Class = "ExplosionData" };
        void F(string n, object v) => db.Fields.Add(new DecodedField(n, v));
        F("timeScale", 1.0); F("faceCamera", 1L); F("randomSpin", 0L); F("hasLight", 1L); F("lightRange", 4.0); F("timeZero", 0.0);
        F("timeOne", 0.5);
        for (int i = 0; i < 3; ++i)
        {
            F($"colors[{i}].r", 1.0); F($"colors[{i}].g", 0.5); F($"colors[{i}].b", 0.25);
            F($"radFactors[{i}]", 1.0);
        }
        // the first sequence of the synthetic shape lasts 2 s
        var e = new ExplosionEffect(s, db, 1, 2, 3, 0, 0, 1, new Random(1));
        Assert.Equal(2000, e.LifeMs);
        Assert.True(e.Alive);
        PointLight? l = e.Light;
        Assert.NotNull(l);
        Assert.Equal(3, l!.Value.Z);
        Assert.Equal(4.0, l.Value.Range, 9);
        for (int i = 0; i < 62; ++i)
            e.Tick(0.032);                                        // 1984 ms
        Assert.True(e.Alive);
        e.Tick(0.032);
        Assert.False(e.Alive);
        Assert.Null(e.Light);
    }

    // ---- SR 4.4: debris pieces explode (Debris_tick 004721f8, Debris_explode 00472954) ----

    [Fact]
    public void DebrisPiecesLandOnTheFloorAndEachEndsInTheSpawnedExplosion()
    {
        RenderShape s = Shape();
        var explosions = new List<(int index, double z)>();
        var piece = new DebrisParams(MinTimeout: 3, MaxTimeout: 6, ExplodeOnBounce: 0.3, ExplodeOnRest: true, Explosion: 7);
        // an interior floor at z 10 (no terrain): the pieces start above it and must stay on it
        var d = new DebrisEffect(new RenderInstance(s), Xf.Translate(0, 0, 12), 0, 0, 0, null, new Random(3), piece,
            FloorAtTen, (e, x, y, z) => explosions.Add((e, z)));
        Assert.True(d.PieceCount > 0);
        Assert.True(d.Alive);
        double minZ = double.MaxValue;
        for (int i = 0; i < 400 && d.Alive; ++i)
        {
            d.Tick(0.032);
            foreach ((int _, double z) in explosions)
                minZ = Math.Min(minZ, z);
        }
        Assert.False(d.Alive);                                   // every piece exploded: lifetime 3 to 6 s at most (the run is 12.8 s)
        Assert.Equal(d.PieceCount, explosions.Count);            // one explosion per piece
        Assert.All(explosions, e => Assert.Equal(7, e.index));
        Assert.True(minZ >= 10.0, "no piece went through the floor");
    }

    // The static world of the debris tests: a floor at z 10 (the ray returns the first hit of rays that go down).
    private static (bool, double, double, double, double) FloorAtTen(double sx, double sy, double sz, double dx, double dy, double dz, double len)
    {
        if (dz >= 0)
            return (false, 0, 0, 0, 0);
        double t = (10 - sz) / dz;
        return t >= 0 && t <= len ? (true, t, 0, 0, 1) : (false, 0, 0, 0, 0);
    }

    [Fact]
    public void DebrisBounceKeepsElasticityTimesTheNormalSpeedAndLosesTheSidewaysSpeedToFriction() // Debris_bounce 00473500
    {
        var p = DebrisParams.Default;                           // elasticity 0.25, friction 0.5
        var b = new DebrisBody { X = 0, Y = 0, Z = 10.3, Vx = 4, Vz = -10, Radius = 0.25, Life = 100 };
        b.Integrate(p, null, FloorAtTen, new Random(1), 0, 0, 0);
        // gravity first: vz = -10 - 20 / 30 = -10.667; friction removes 0.5 * 10.667 = 5.33 of the sideways 4; the normal speed returns times 0.25
        Assert.True(b.Collided);
        Assert.Equal(0, b.Vx, 6);
        Assert.Equal(0.25 * (10 + 20 * DebrisBody.TimeSlice), b.Vz, 6);
        Assert.Equal(10.3 + (4 + 0) * 0 + -(10 + 20 * DebrisBody.TimeSlice / 2) * DebrisBody.TimeSlice, b.Z, 9);   // the integrated position, not the contact point
    }

    [Fact]
    public void DebrisSelfDamageIsTakenOnlyOnTheDetailedCollisionPathAbovePositiveThreshold() // Debris_collide 00472ad0 at 004731cb, WantsExplosion 004721f8
    {
        var p = DebrisParams.Default with { CollisionDetail = 1, DamageThreshold = 1 };
        // the rule subtracts (velocity . normal - damageThreshold) from the damage when that is positive: a bounce is a velocity against the normal, so only a
        // piece whose contact test sees it leaving fast takes damage
        var b = new DebrisBody { X = 0, Y = 0, Z = 10.3, Vz = 9, Radius = 0.25, Life = 100, Damage = 5 };
        b.Integrate(p, null, FloorAtTen, new Random(1), 0, 0, 0);
        Assert.False(b.Collided);
        // a ray that reports a hit with a normal along the motion: velocity . normal = 8.33 > 1
        b.Integrate(p, null, (double sx, double sy, double sz, double dx, double dy, double dz, double len) => (true, 0.1, 0, 0, 1), new Random(1), 0, 0, 0);
        Assert.InRange(b.Damage, -10, 5 - 6);
        Assert.True(b.WantsExplosion(p, new Random(1)));
        // the stock DebrisData have collisionDetail 0: no damage
        var c = new DebrisBody { Z = 10.3, Vz = 9, Radius = 0.25, Life = 100, Damage = 5 };
        c.Integrate(p with { CollisionDetail = 0 }, null, (double sx, double sy, double sz, double dx, double dy, double dz, double len) => (true, 0.1, 0, 0, 1), new Random(1), 0, 0, 0);
        Assert.Equal(5, c.Damage);
    }

    [Fact]
    public void DebrisBounceMovingAwayFromTheSurfaceIsNoContact() // Debris_bounce 00473500: only a velocity against the normal bounces
    {
        var b = new DebrisBody { X = 0, Y = 0, Z = 10.3, Vx = 4, Vz = 12, Radius = 0.25, Life = 100 };
        b.Integrate(DebrisParams.Default, null, FloorAtTen, new Random(1), 0, 0, 0);
        Assert.False(b.Collided);
        Assert.Equal(4, b.Vx, 9);
    }

    [Fact]
    public void DebrisExplodesOnAFreshBounceByChanceOnTwoContactsInARowAndAtRest() // Debris_tick 004721f8
    {
        var never = DebrisParams.Default with { ExplodeOnBounce = 0, ExplodeOnRest = false };
        var always = never with { ExplodeOnBounce = 1 };
        var rng = new Random(5);
        Assert.False(new DebrisBody { Collided = true, Vx = 1 }.WantsExplosion(never, rng));           // a bounce, chance 0
        Assert.True(new DebrisBody { Collided = true, Vx = 1 }.WantsExplosion(always, rng));           // a bounce, chance 1
        Assert.True(new DebrisBody { Collided = true, PrevCollided = true, Vx = 1 }.WantsExplosion(never, rng));   // contact in two ticks
        Assert.False(new DebrisBody { Vx = 1 }.WantsExplosion(always, rng));                                        // flying
        Assert.False(new DebrisBody().WantsExplosion(never, rng));                                                  // still, explodeOnRest off
        Assert.True(new DebrisBody().WantsExplosion(never with { ExplodeOnRest = true }, rng));                     // still, explodeOnRest on
        var b = new DebrisBody { Collided = true };
        b.WantsExplosion(never, rng);
        Assert.True(b.PrevCollided);
        Assert.False(b.Collided);                                                                                   // the flags move one tick on
    }

    [Fact]
    public void DebrisFreezesAfterASecondInContactWhenSlow() // Debris_tick 004721f8, flag +0x164 & 0x80
    {
        var b = new DebrisBody { X = 0, Y = 0, Z = 10.25, Radius = 0.25, Life = 100, Age = 2 };
        var rng = new Random(2);
        for (int i = 0; i < 400 && !b.Frozen; ++i)
            b.Integrate(DebrisParams.Default, null, FloorAtTen, rng, 0, 0, 0);
        Assert.True(b.Frozen);
        Assert.Equal(0, b.Vz);
        double z = b.Z;
        b.Integrate(DebrisParams.Default, null, FloorAtTen, rng, 0, 0, 0);
        Assert.Equal(z, b.Z);
        Assert.True(b.Collided);
    }

    [Fact]
    public void PartDebrisCloudThrowsEveryPieceOutFromTheShapeCentreAtBurstStrengthOverMass() // 00476858, ShapeBase_explode 0044cdec
    {
        RenderShape s = Shape();
        var d = new DebrisEffect(new RenderInstance(s), Xf.Translate(0, 0, 50), 3, -2, 1, null, new Random(9));
        Assert.True(d.PieceCount > 0);
        foreach (DebrisBody b in d.Bodies)
        {
            double dv = Math.Sqrt((b.Vx - 3) * (b.Vx - 3) + (b.Vy + 2) * (b.Vy + 2) + (b.Vz - 1) * (b.Vz - 1));
            Assert.True(Math.Abs(dv - 8) < 1e-9 || dv < 1e-9, $"burst of 2000 / 250 or none (a piece at the shape centre), got {dv}");
            Assert.InRange(b.Wx, -2.5, 2.5);
            Assert.InRange(b.Wy, -2.5, 2.5);
            Assert.InRange(b.Wz, -2.5, 2.5);
            Assert.Equal(250, b.Mass);
        }
    }

    [Fact]
    public void DebrisCloudRowsAreTheDataWhoseTypeSharesABitWithTheMaskUpToTheFirstRowOverTheBudget() // 00475484
    {
        var rows = new[]
        {
            new DebrisRow(0, 1, 10, 2, "a.dts", DebrisParams.Default), new DebrisRow(1, 2, 10, 1, "b.dts", DebrisParams.Default),
            new DebrisRow(2, 3, 20, 4, "c.dts", DebrisParams.Default), new DebrisRow(3, 1, 500, 1, "d.dts", DebrisParams.Default),
            new DebrisRow(4, 1, 5, 1, "e.dts", DebrisParams.Default),
        };
        var (found, total) = DebrisCloud.Find(rows, 1, 100);
        Assert.Equal(new[] { 0, 2 }, found.Select(r => r.Index).ToArray());     // row 3 is over the budget and ends the walk; row 4 is never reached
        Assert.Equal(6, total, 9);
        Assert.Empty(DebrisCloud.Find(rows, 4, 100).Rows);                      // no row has bit 4
        // the stock DebrisData have type 0 and mask 1: no row, no piece
        Assert.Empty(DebrisCloud.Find(new[] { new DebrisRow(0, 0, 100, 1, "", DebrisParams.Default) }, 1, 90).Rows);
    }

    [Fact]
    public void DebrisCloudSpendsItsBudgetRowByRowAndDropsRowsThatNoLongerFit() // DebrisCloud_spawn 00474478
    {
        var heavy = new DebrisRow(0, 1, 30, 1, "", DebrisParams.Default);
        var light = new DebrisRow(1, 1, 10, 1, "", DebrisParams.Default);
        Assert.Equal(2, DebrisCloud.Pick(new List<DebrisRow> { heavy }, 1, 70, new Random(4)).Count);   // 70 -> 40 -> 10 (< 30)
        // with both rows the light one is dropped as soon as the budget is under 10, the heavy one under 30: at most 4 + 1 pieces for 45
        Assert.InRange(DebrisCloud.Pick(new List<DebrisRow> { heavy, light }, 2, 45, new Random(4)).Count, 2, 5);
        Assert.Equal(3, DebrisCloud.Pick(new List<DebrisRow> { light }, 1, 35, new Random(4)).Count);   // 35 -> 25 -> 15 -> 5 (< 10)
        Assert.Empty(DebrisCloud.Pick(new List<DebrisRow>(), 0, 100, new Random(4)));
    }

    [Fact]
    public void DebrisCloudLaunchThrowsAlongTheConeAxisByStrengthOverMass() // 00473e58 and the basis 00473c20
    {
        var basis = DebrisCloud.Basis(0, 0, 2, 0.5);                // a direction of any length, half angle 0.5
        Assert.Equal(0.5, basis.Angle);
        Assert.Equal(1, basis.Z[2], 9);
        var b = new DebrisBody();
        DebrisCloud.Launch(b, 1, 2, 3, 0, 0, -1, 0, 0, 0, 0, basis, 90, 0.2, 100, new Random(7));   // a cone of angle 0: straight along z
        Assert.Equal(1, b.X, 9); Assert.Equal(2, b.Y, 9); Assert.Equal(3.2, b.Z, 9);
        Assert.Equal(-1 + 0.9, b.Vz, 9);
        Assert.InRange(b.Wx, -0.675 * 2, 0.675 * 2);
        var none = DebrisCloud.Basis(0, 0, 0, 0.1);
        Assert.Equal(Math.PI, none.Angle);                          // no direction: every direction
    }

    [Fact]
    public void ADebrisEndHandsTheSecondaryCloudTheMassTimesPointNineAsBudget() // Debris_explode 00472954
    {
        var p = DebrisParams.Default with { SpawnMask = 3, SpawnStrength = 90, SpawnRadius = 0.4 };
        var b = new DebrisBody { X = 1, Y = 2, Z = 3, Vx = 4, Mass = 100 };
        DebrisCloudSpec spec = b.SecondaryCloud(p, 9990)!.Value;
        Assert.Equal((3, 1.0, 2.0, 3.0, 4.0, 90.0, 90.0, 0.4, 9990.0), (spec.Mask, spec.X, spec.Y, spec.Z, spec.Vx, spec.Budget, spec.Strength, spec.Radius, spec.Remaining));
        Assert.Null(b.SecondaryCloud(p with { SpawnMask = 0 }, 9990));
        Assert.Null(b.SecondaryCloud(p, 0));
    }

    [Fact]
    public void DebrisDataFieldsFeedTheDebrisParameters() // SR 4.4
    {
        Assert.Equal(DebrisParams.Default, DebrisParams.From(null));
        Assert.Equal(-1, DebrisParams.Default.Explosion);
        Assert.Equal(0.3, DebrisParams.Default.ExplodeOnBounce);
    }

    // ---- SR 4.5 smoke pool, 4.6 decals ----

    [Fact]
    public void SmokePoolIsARingAndPuffsDieWhenTheirSequenceEnds() // SR 4.5
    {
        RenderShape s = Shape();                                  // no Sequence01: the first sequence plays (2 s, cyclic)
        var pool = new SmokePool(3);
        for (int i = 0; i < 5; ++i)
            pool.Spawn(s, i, 0, 0);
        Assert.Equal(3, pool.Live);                               // the two oldest were evicted
        Assert.Equal(3, pool.RemoveInside(3, 0, 0, 1.5));         // the puffs left are at x = 2, 3 and 4: all inside
    }

    [Fact]
    public void SmokePuffsDriftUpAndExpireAfterScaleSeconds() // SR 4.5 FUN_0045b4a4 / FUN_0045b070
    {
        RenderShape s = Shape();                                  // sequence 2 s, cyclic
        var pool = new SmokePool(4);
        pool.Spawn(s, 0, 0, 0, 1, 0, 0, 1);
        pool.Tick(0.5);
        Assert.Equal(1, pool.Live);
        pool.Tick(0.6);                                           // 1.1 s: past the 1000 ms deadline
        Assert.Equal(0, pool.Live);
    }

    [Theory]
    [InlineData(0.05, 0.0, 50)]
    [InlineData(0.05, 0.5, 100)]
    [InlineData(0.01, 1.0, 30)]
    public void GrenadePuffIntervalFollowsProjSpecialTimeAndSpeed(double special, double r, int ms) // SR 4.5 FUN_0045ffd0
        => Assert.Equal(ms, OpenTribes.Client.Scene.ProjectileVisual.SmokeInterval(special, r));

    [Fact]
    public void SmokePuffsAreRemovedByRadius() // SR 4.5 FUN_0045b6d4
    {
        RenderShape s = Shape();
        var pool = new SmokePool(10);
        pool.Spawn(s, 0, 0, 0);
        pool.Spawn(s, 10, 0, 0);
        Assert.Equal(1, pool.RemoveInside(0, 0, 0, 2));
        Assert.Equal(1, pool.Live);
    }

    [Fact]
    public void DecalRingOverwritesTheOldestAndHasTheDocumentedBitmaps() // SR 4.6
    {
        var ring = new DecalRing(3);
        for (int i = 0; i < 5; ++i)
            ring.Add(DecalRing.Quad(0, i, 0, 0, 0, 0, 1, 0, 1, 0));
        Assert.Equal(3, ring.Count);
        Assert.Equal(3, ring.Capacity);
        Assert.Equal(0, new DecalRing(0).Capacity);               // capacity 0 disables decals
        Assert.Equal(200, new DecalRing(500).Capacity);           // clamped to 200
        Assert.Equal("bullethole.bmp", DecalRing.Table[6].File);
        Assert.Equal(0.32, DecalRing.Table[4].Half);
        Assert.Equal("heavyfootl.bmp", DecalRing.Table[5].File);  // index 5 names the left bitmap again
    }

    [Fact]
    public void DecalQuadIsLiftedAlongTheNormalAndSizedByTheTable() // SR 4.6 Footprint
    {
        DecalRing.Entry e = DecalRing.Quad(2, 10, 20, 30, 0, 0, 1, 0, 1, 0);
        Assert.Equal(30.05, e.Cz, 9);                             // lifted by 0.05 along the normal
        double w = Math.Abs(e.Corners[3] - e.Corners[0]);         // right extent: 2 * half = 0.4
        Assert.Equal(0.4, w, 9);
        Assert.Equal(0.4, Math.Abs(e.Corners[1] - e.Corners[7]), 9);
    }

    // ---- SR 4.7 beams ----

    [Fact]
    public void BoltsHaveTwoToTheNPlusOnePointsBetweenTheEndPoints() // SR 4.7 Lightning
    {
        double[][] p = Beams.BoltPoints(new[] { 0.0, 0, 0 }, new[] { 0.0, 40, 0 }, 4, 0.15, new Random(3));
        Assert.Equal(17, p.Length);
        Assert.Equal(new[] { 0.0, 0, 0 }, p[0]);
        Assert.Equal(new[] { 0.0, 40, 0 }, p[16]);
        Assert.Contains(p, q => Math.Abs(q[0]) > 1e-6 || Math.Abs(q[2]) > 1e-6);   // jittered sideways
        Assert.Equal(9, Beams.BoltPoints(new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }, 3, 0, new Random(1)).Length);
        double[][] mid = Beams.Lerp(p, Beams.BoltPoints(new[] { 0.0, 0, 0 }, new[] { 0.0, 40, 0 }, 4, 0.15, new Random(4)), 0.5);
        Assert.Equal(17, mid.Length);
    }

    // ---- SR 4.8 weather ----

    [Fact]
    public void ParticleCountScalesWithIntensity() // SR 4.8, 004419b8: 2048 rain streaks and 1024 snow flakes at intensity 1
    {
        Assert.Equal(0, WeatherEffect.Count(0, true));
        Assert.Equal(2048, WeatherEffect.Count(1, true));
        Assert.Equal(1024, WeatherEffect.Count(0.5, true));
        Assert.Equal(1024, WeatherEffect.Count(1, false));
        Assert.Equal(2048, WeatherEffect.Count(7, true));
    }

    [Fact]
    public void RainStreakIsAKiteOfStockSize() // 00442338: 0.15 across at 2 units, end at 2.25 units along the fall direction
    {
        var q = WeatherEffect.Streak((10, 20, 30), 0, 0, (0, 1, 0));
        Assert.Equal((10, 20, 30), q.tip);
        Assert.Equal((10 + 0.075, 20, 28), q.left);   // forward x down is -x
        Assert.Equal((10 - 0.075, 20, 28), q.right);
        Assert.Equal((10, 20, 30 - 2.25), q.end);
        var w = WeatherEffect.Streak((0, 0, 0), 3, 4, (0, 1, 0));
        double len = Math.Sqrt(9 + 16 + 1);
        Assert.Equal(2.25 * 3 / len, w.end.x, 9);
        Assert.Equal(-2.25 / len, w.end.z, 9);
    }

    [Fact]
    public void WeatherDrawsOnlyWhilePrecipitationIsOnAndNotSuspended() // shapes-render.md 4.8, 004420a4 and 00441c50
    {
        Assert.True(WeatherEffect.Draws(true, false, 1));
        Assert.False(WeatherEffect.Draws(false, false, 1));
        Assert.False(WeatherEffect.Draws(true, true, 1));
        Assert.False(WeatherEffect.Draws(true, false, 0));
    }

    // ---- SR 3.2 and client-net 3.2 ghost fields ----

    [Fact]
    public void ThreadSlotsAndImagesParseFromTheGhostText() // client-net.md 3.2, 3.4
    {
        List<ThreadSlot> t = ThreadSlot.ParseAll(new List<object?> { "0:seq=3 state=0 fwd=1 end=1", "2:seq=-1 state=2 fwd=0 end=0" });
        Assert.Equal(new ThreadSlot(0, 3, 0, true, true), t[0]);
        Assert.Equal(new ThreadSlot(2, -1, 2, false, false), t[1]);
        List<ImageSlot> im = ImageSlot.ParseAll(new List<object?> { "0:img=21 state=-1 01 0 f=0", "1:img=29 state=2 11 1", "2:img=-1 state=-1 00 0 f=0" });
        Assert.Equal(new ImageSlot(0, 21, -1, 0, false, true, false), im[0]);
        Assert.Equal(new ImageSlot(1, 29, 2, 1, true, true, false), im[1]);
        Assert.Equal(-1, im[2].Image);
    }

    [Fact]
    public void ABasisPlacesLocalYAlongTheForwardVector() // SR 4.3 faceCamera: the image faces the camera
    {
        Xf b = Mathx.Basis(1, 2, 3, 0, 1, 0);
        var (x, y, z) = b.Apply(0, 5, 0);
        Assert.Equal((1.0, 7.0, 3.0), (x, y, z));
        var (ux, uy, uz) = b.Rotate(0, 0, 1);                     // local up stays world up
        Assert.Equal(0, ux, 9);
        Assert.Equal(0, uy, 9);
        Assert.Equal(1, uz, 9);
    }

    // ---- client-net 5 .rec playback ----

    [Fact]
    public void PlaybackAdvancesInThirtyTwoMillisecondFrames() // client-net.md 5, 2.1
    {
        var rec = new List<byte>();
        rec.AddRange(BitConverter.GetBytes(2052u));
        rec.AddRange(BitConverter.GetBytes(2048u));
        rec.AddRange(BitConverter.GetBytes(0xffffffffu));
        var p = new DemoPlayback(rec.ToArray());
        int frames = 0;
        p.OnFrame = _ => ++frames;
        p.StepTo(1000);
        Assert.Equal(32, frames);                                 // 32 * 32 ms = 1024 ms >= 1000
        Assert.Equal(1024, p.ClockMs);
        Assert.True(p.Finished);
        Assert.Equal(2052, p.World.PlayerId);
    }

    [Fact]
    public void ObserverOrbitMovesBackAlongTheViewDirection() // game-stream.md 6.6 mode 3
    {
        DemoRender.CameraPose c = DemoRender.Orbit(10, 20, 30, 0, 0, 5);          // yaw 0 looks along +Y
        Assert.Equal(10, c.X, 9);
        Assert.Equal(15, c.Y, 9);
        Assert.Equal(30, c.Z, 9);
        DemoRender.CameraPose up = DemoRender.Orbit(0, 0, 0, 0, Math.PI / 6, 10);  // looking up 30 degrees from below
        Assert.Equal(-10 * Math.Cos(Math.PI / 6), up.Y, 9);
        Assert.Equal(-10 * Math.Sin(Math.PI / 6), up.Z, 9);
    }

    // ---- SR 1.3.2, 2.4 headless render of a synthetic shape ----

    [Fact]
    public void ASyntheticShapeRendersInAnOffscreenContext() // SR 1.3.2 (flat colour material), 2.4 (cull, frame), 2.5 (light)
    {
        GlWindow? win = null;
        var failures = new List<string>();
        foreach (string? driver in new string?[] { "offscreen", null })
        {
            win = GlWindow.TryCreate("test", 64, 48, hidden: true, driver, out string err);
            if (win != null)
                break;
            failures.Add($"{driver ?? "default"}: {err}");
        }
        if (win == null)
        {
            mOut.WriteLine("SKIPPED: no OpenGL 3.3 context could be made (" + string.Join("; ", failures) + ")");
            return;
        }
        mOut.WriteLine($"RAN: video driver {win.VideoDriver}, OpenGL {win.GlVersion}");
        using (win)
        using (var dev = new GlGfxDevice(win))
        {
            PaletteBank bank = PaletteTables.Grayscale();
            dev.SetPalette(bank);
            using var assets = new ShapeAssets(dev, new WorldAssets(new ResourceManager(Path.GetTempPath()), bank));
            RenderShape shape = Shape();
            var inst = new RenderInstance(shape);
            var renderer = new ShapeRenderer(assets);
            var cam = new Camera3D(0, -5, 0, 0, 0, 60, dev.Width, dev.Height, 0.5, 100);
            var ctx = new FrameContext { Camera = cam, Assets = assets, Renderer = renderer, Light = new LightSet { AmbR = 1, AmbG = 1, AmbB = 1, SunR = 0, SunG = 0, SunB = 0 } };
            // viewed from the front the quad (facing -Y) is drawn; from behind it is culled (SR 2.4 step 6)
            ShapeDraw d = ctx.Draw(Xf.Identity, "synthetic");
            d.ForceDetail = 0;
            RenderImage? img = renderer.Build(inst, cam, d);
            Assert.NotNull(img);
            Assert.Equal(0, renderer.LastCulled);
            Assert.Single(img!.Batches);
            Assert.Equal(6, img.Batches[0].Vertices.Length);
            ctx.Lists.Add(img);
            dev.BeginFrame();
            dev.ClearRgb(0, 0, 0);
            dev.BeginScene3D(cam, 0, 0, 0);
            Assert.Equal(1, ctx.Lists.Draw(dev));
            dev.EndScene3D();
            dev.EndFrame();
            byte[] px = dev.ReadPixels();
            int o = (24 * 64 + 32) * 4;
            mOut.WriteLine($"centre pixel {px[o]},{px[o + 1]},{px[o + 2]}");
            Assert.InRange((int)px[o], 197, 203);                          // the material colour (200, 50, 50) fully lit
            Assert.InRange((int)px[o + 1], 47, 53);
            Assert.InRange((int)px[o + 2], 47, 53);
            Assert.Equal(0, px[0]);                               // a corner stays clear
            // the same quad seen from behind: every face is culled
            var back = new Camera3D(0, 5, 0, 180, 0, 60, dev.Width, dev.Height, 0.5, 100);
            Assert.Null(renderer.Build(inst, back, ctx.Draw(Xf.Identity, "back").With(x => x.ForceDetail = 0)));
            Assert.Equal(2, renderer.LastCulled);
        }
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T t, Action<T> a)
    {
        a(t);
        return t;
    }

    public static T With<T>(this T t, Action<T> a) => t.Also(a);
}
