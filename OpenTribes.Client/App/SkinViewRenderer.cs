// The model of the Player Setup screen (FearGui::SkinView draw 0049af20, armor choice 0049a9bc, FGSkin::set 0049a798): the player armor
// of the gender in the chosen skin, looping its "run" sequence, drawn into the control's rectangle with the camera of 004998e0 and
// 0049a9bc. The stock fits the camera from the bounds of the shape and lights it with an ambient and a headlight.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.App;

public sealed class SkinViewRenderer : IModelViewRenderer, IDisposable
{
    private static readonly string[] sMale = { "larmor.dts", "marmor.dts", "harmor.dts" };
    private static readonly string[] sFemale = { "lfemale.dts", "mfemale.dts", "harmor.dts" };

    // 004998e0: the horizontal half angle is 0.3 radians; the fit is 0.85 (male, 0.75 female) wide and 1.325 (1.25) high around the
    // centre of the bounds, 0.3 below it for the male armor.
    private const double HalfAngle = 0.3;
    // Measured against stock frames of the screen at 640 x 480: the model stands 6 percent smaller and 0.07 higher than the plain fit.
    private const double Fit = 1.06, ZShift = -0.07;

    private readonly ClientApp mApp;
    private ShapeAssets? mAssets;
    private ShapeRenderer? mRenderer;
    private RenderInstance? mInst;
    private RenderThread? mThread;
    private RenderShape? mShape;
    private string mKey = "";
    private long mLastMs = -1;

    public SkinViewRenderer(ClientApp app) => mApp = app;

    public void Dispose() => mAssets?.Dispose();

    private bool Ensure()
    {
        if (mAssets != null)
            return true;
        if (mApp.Dev == null)
            return false;
        PaletteBank bank = mApp.CurrentPalette ?? GfxAssets.LoadPalette(mApp.Res, "lush.day.ppl") ?? PaletteTables.Grayscale();
        mAssets = new ShapeAssets(mApp.Dev, new WorldAssets(mApp.Res, bank));
        mRenderer = new ShapeRenderer(mAssets);
        return true;
    }

    // The camera of the view: the stock puts it behind the model, looks down by 0.25 radians and turns it around the model
    // with the view angle (004998e0, 0049af20).
    public static double FitDistance(bool male, int w, int h)
    {
        double tanH = Math.Tan(HalfAngle), tanV = tanH * h / Math.Max(w, 1);
        return Fit * Math.Max((male ? 0.85 : 0.75) / tanH, (male ? 1.325 : 1.25) / tanV);
    }

    // +0x1f4 [code 004998e0, camera vtable +0xc = 005aa65c]: the camera call (10.0, smaller side in pixels) gives
    // 10 * near * (half the width in pixels) / (tan(0.3) * smaller side); the near of the view's camera is 1.0 (0049a511).
    public static double MaxDistance(int w, int h) => 10.0 * 1.0 * (w * 0.5) / (Math.Tan(HalfAngle) * Math.Min(w, h));

    public static Camera3D CameraFor(double cx, double cy, double cz, bool male, double yaw, int w, int h, double? distance = null)
    {
        double dist = distance ?? FitDistance(male, w, h);
        double zOff = (male ? -0.3 : -0.1) + ZShift;
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
        double dx = cam.X - cx, dy = cam.Y - cy, dz = cam.Z - cz, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-9)
            len = 1;
        return new() { AmbR = Ambient, AmbG = Ambient, AmbB = Ambient, SunX = dx / len, SunY = dy / len, SunZ = dz / len, SunR = 1f, SunG = 1f, SunB = 1f };
    }

    public void Draw(GuiShapeView view, GuiPaint p, int x, int y, int w, int h)
    {
        if (view.ClassName != "FearGui::SkinView" || w <= 0 || h <= 0 || !Ensure() || view.SkinBase.Length == 0)
            return;
        IGfxDevice dev = mApp.Dev!;
        string name = (view.Male ? sMale : sFemale)[view.Armor];
        string key = name + "|" + view.SkinBase;
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
        long now = mApp.Host.TickMs;
        if (mThread != null && mLastMs >= 0)
            mInst.Advance(mThread, Math.Min(now - mLastMs, 200) / 1000.0);
        mLastMs = now;
        // +0x1ec, +0x1e8: the fitted distance is the lowest and the first one; the right button moves it.
        double fit = FitDistance(view.Male, w, h);
        if (double.IsNaN(view.Zoom) || Math.Abs(view.ZoomMin - fit) > 1e-9)
            view.Zoom = fit;
        view.ZoomMin = fit;
        view.ZoomMax = MaxDistance(w, h);
        Camera3D cam = CameraFor(mShape.Cx, mShape.Cy, mShape.Cz, view.Male, view.Yaw, w, h, view.Zoom);
        var ctx = new FrameContext { Camera = cam, Assets = mAssets!, Renderer = mRenderer, Light = Light(cam, mShape.Cx, mShape.Cy, mShape.Cz) };
        ShapeDraw d = ctx.Draw(Xf.Identity, name);
        d.Skin = view.SkinBase;
        d.ForceDetail = 0;
        RenderImage? img = mRenderer.Build(mInst, cam, d);
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
