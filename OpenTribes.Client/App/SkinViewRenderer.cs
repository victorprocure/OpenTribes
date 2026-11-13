namespace OpenTribes.Client.App;

using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

/// <summary>The model of the Player Setup screen: the player armor of the gender in the chosen skin, looping its "run" sequence, drawn into the rectangle of the control.</summary>
/// <remarks>Follows FearGui::SkinView draw (0049af20), the armor choice (0049a9bc) and FGSkin::set (0049a798), with the camera of 004998e0 and 0049a9bc. The stock fits the camera from the bounds of the shape and lights it with an ambient and a headlight.</remarks>
public sealed class SkinViewRenderer : IModelViewRenderer, IDisposable
{
    private static readonly string[] sMale = ["larmor.dts", "marmor.dts", "harmor.dts"];
    private static readonly string[] sFemale = ["lfemale.dts", "mfemale.dts", "harmor.dts"];

    // 004998e0: the horizontal half angle is 0.3 radians; the fit is 0.85 (male, 0.75 female) wide and 1.325 (1.25) high around the
    // centre of the bounds, 0.3 below it for the male armor.
    private const double HalfAngle = 0.3;
    // Not exe-derived (item 9 of the exact2 pass): the exe formulas of 004998e0 and 0049b0c7 to 0049b121 above (distance, centre, 0.3 or 0.1
    // below it, tilt 0.25, half angle 0.3, near 1.0, viewport = the control rectangle) contain no further factor, and with them alone the figure
    // is 247 pixels tall against 231 in a stock frame of the screen at 640 x 480 (7 percent), 4 pixels lower on the screen. The two constants
    // below reproduce the stock height (229 pixels) and are kept until the source of that scale is found; routines read without finding it:
    // 004998e0, 0049a9bc, 0049af20, the camera set up 005a94a4 and the radius projection near 005aa5c0.
    private const double Fit = 1.06, ZShift = -0.07;

    private readonly ClientApp mApp;
    private ShapeAssets? mAssets;
    private ShapeRenderer? mRenderer;
    private RenderInstance? mInst;
    private RenderThread? mThread;
    private RenderShape? mShape;
    private string mKey = "";
    private long mLastMs = -1;

    /// <summary>Creates the renderer.</summary>
    /// <param name="app">The client that owns the device and resources.</param>
    public SkinViewRenderer(ClientApp app) => mApp = app;

    /// <summary>Releases the shape assets.</summary>
    public void Dispose() => mAssets?.Dispose();

    private bool Ensure()
    {
        if (mAssets != null)
            return true;
        if (mApp.Dev == null)
            return false;
        var bank = mApp.CurrentPalette ?? GfxAssets.LoadPalette(mApp.Res, "lush.day.ppl") ?? PaletteTables.Grayscale();
        mAssets = new ShapeAssets(mApp.Dev, new WorldAssets(mApp.Res, bank));
        mRenderer = new ShapeRenderer(mAssets);
        return true;
    }

    /// <summary>Computes the distance of the camera from the model so the armor fits the view.</summary>
    /// <param name="male">True for a male armor, false for a female armor.</param>
    /// <param name="w">The view width in pixels.</param>
    /// <param name="h">The view height in pixels.</param>
    /// <returns>The distance.</returns>
    /// <remarks>Follows 004998e0 and 0049af20: the stock puts the camera behind the model, looks down by 0.25 radians and turns it around the model with the view angle.</remarks>
    public static double FitDistance(bool male, int w, int h)
    {
        double tanH = Math.Tan(HalfAngle), tanV = tanH * h / Math.Max(w, 1);
        return Fit * Math.Max((male ? 0.85 : 0.75) / tanH, (male ? 1.325 : 1.25) / tanV);
    }

    /// <summary>Computes the largest camera distance the view allows.</summary>
    /// <param name="w">The view width in pixels.</param>
    /// <param name="h">The view height in pixels.</param>
    /// <returns>The distance.</returns>
    /// <remarks>Follows +0x1f4 of 004998e0 (camera vtable +0xc = 005aa65c): the camera call (10.0, smaller side in pixels) gives 10 * near * (half the width in pixels) / (tan(0.3) * smaller side). The near of the camera of the view is 1.0 (0049a511).</remarks>
    public static double MaxDistance(int w, int h) => 10.0 * 1.0 * (w * 0.5) / (Math.Tan(HalfAngle) * Math.Min(w, h));

    /// <summary>Builds the camera that looks at the model.</summary>
    /// <param name="cx">The model centre x.</param>
    /// <param name="cy">The model centre y.</param>
    /// <param name="cz">The model centre z.</param>
    /// <param name="male">True for a male armor.</param>
    /// <param name="yaw">The view angle around the model.</param>
    /// <param name="w">The view width in pixels.</param>
    /// <param name="h">The view height in pixels.</param>
    /// <param name="distance">The camera distance, or null to fit the model.</param>
    /// <returns>The camera.</returns>
    public static Camera3D CameraFor(double cx, double cy, double cz, bool male, double yaw, int w, int h, double? distance = null)
    {
        var dist = distance ?? FitDistance(male, w, h);
        var zOff = (male ? -0.3 : -0.1) + ZShift;
        Xf rot = Xf.Euler(-0.25, 0, -yaw);
        var (px, py, pz) = rot.Apply(cx, cy - dist, cz + zOff);
        var (fx, fy, fz) = rot.Rotate(0, 1, 0);
        double camYaw = Math.Atan2(-fx, fy) * 180 / Math.PI, camPitch = Math.Asin(Math.Clamp(fz, -1, 1)) * 180 / Math.PI;
        return new Camera3D(px, py, pz, camYaw, camPitch, 2 * HalfAngle * 180 / Math.PI, w, h, 0.1, 100);
    }

    // The view's light set [code 0049a5d3 build, 0049af20 draw]: ambient 0.7 (0x3f333333) and one directional light (type 1) of colour 1.0
    // whose direction is set every draw to the unit vector from the camera to the shape centre, so the lit side faces the camera.
    private const float Ambient = 0.7f;
    private static LightSet Light(in Camera3D cam, double cx, double cy, double cz)
    {
        double dx = cam.X - cx, dy = cam.Y - cy, dz = cam.Z - cz, len = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (len < 1e-9)
            len = 1;
        return new() { AmbR = Ambient, AmbG = Ambient, AmbB = Ambient, SunX = dx / len, SunY = dy / len, SunZ = dz / len, SunR = 1f, SunG = 1f, SunB = 1f };
    }

    /// <summary>Draws the model of a skin view control.</summary>
    /// <param name="view">The control.</param>
    /// <param name="p">The paint target.</param>
    /// <param name="x">The left edge of the control.</param>
    /// <param name="y">The top edge of the control.</param>
    /// <param name="w">The control width.</param>
    /// <param name="h">The control height.</param>
    public void Draw(GuiShapeView view, GuiPaint p, int x, int y, int w, int h)
    {
        if (view.ClassName != "FearGui::SkinView" || w <= 0 || h <= 0 || !Ensure() || view.SkinBase.Length == 0)
            return;
        IGfxDevice dev = mApp.Dev!;
        var name = (view.Male ? sMale : sFemale)[view.Armor];
        var key = name + "|" + view.SkinBase;
        if (key != mKey || mInst == null)
        {
            mKey = key;
            mShape = mAssets!.Shape(name);
            mInst = mShape == null ? null : new RenderInstance(mShape);
            mThread = mInst?.AddThread("run", 0, 0);
            mLastMs = -1;
            view.Zoom = double.NaN;
        }
        if (mInst == null || mShape == null || mRenderer == null)
            return;
        var now = mApp.Host.TickMs;
        if (mThread != null && mLastMs >= 0)
            mInst.Advance(mThread, Math.Min(now - mLastMs, 200) / 1000.0);
        mLastMs = now;
        // +0x1ec, +0x1e8: the fitted distance is the lowest and the first one; the right button moves it.
        var fit = FitDistance(view.Male, w, h);
        if (double.IsNaN(view.Zoom) || Math.Abs(view.ZoomMin - fit) > 1e-9)
            view.Zoom = fit;
        view.ZoomMin = fit;
        view.ZoomMax = MaxDistance(w, h);
        var cam = CameraFor(mShape.Cx, mShape.Cy, mShape.Cz, view.Male, view.Yaw, w, h, view.Zoom);
        var ctx = new FrameContext { Camera = cam, Assets = mAssets!, Renderer = mRenderer, Light = Light(cam, mShape.Cx, mShape.Cy, mShape.Cz) };
        var d = ctx.Draw(Xf.Identity, name);
        d.Skin = view.SkinBase;
        d.ForceDetail = 0;
        var img = mRenderer.Build(mInst, cam, d);
        if (img == null)
            return;
        ctx.Lists.Add(img);
        dev.SetViewport3D(new GfxRect(x, y, x + w - 1, y + h - 1));
        dev.BeginScene3D(cam, 0.7f, 0.7f, 0.7f);
        ctx.Lists.Draw(dev);
        dev.EndScene3D();
        dev.SetViewport3D(null);
    }
}
