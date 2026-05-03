// The model of the Player Setup screen (FearGui::SkinView draw 0049af20, armor choice 0049a9bc, FGSkin::set 0049a798): the player armor
// of the gender in the chosen skin, looping its "run" sequence, drawn into the control's rectangle with the camera of 004998e0 and
// 0049a9bc. The stock fits the camera from the bounds of the shape; the lighting is the client's default look (no exe light data).
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
    public static Camera3D CameraFor(double cx, double cy, double cz, bool male, double yaw, int w, int h)
    {
        double tanH = Math.Tan(HalfAngle), tanV = tanH * h / Math.Max(w, 1);
        double dist = Fit * Math.Max((male ? 0.85 : 0.75) / tanH, (male ? 1.325 : 1.25) / tanV);
        double zOff = (male ? -0.3 : -0.1) + ZShift;
        Xf rot = Xf.Euler(-0.25, 0, -yaw);
        var (px, py, pz) = rot.Apply(cx, cy - dist, cz + zOff);
        var (fx, fy, fz) = rot.Rotate(0, 1, 0);
        double camYaw = Math.Atan2(-fx, fy) * 180 / Math.PI, camPitch = Math.Asin(Math.Clamp(fz, -1, 1)) * 180 / Math.PI;
        return new Camera3D(px, py, pz, camYaw, camPitch, 2 * HalfAngle * 180 / Math.PI, w, h, 0.1, 100);
    }

    // A look, fitted to the stock preview: soft ambient and a sun above and in front of the model (the exe's TS lights are not read).
    private static LightSet Light() => new() { AmbR = 0.75f, AmbG = 0.75f, AmbB = 0.75f, SunX = 0, SunY = 0.5, SunZ = 0.87, SunR = 0.55f, SunG = 0.55f, SunB = 0.55f };

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
        }
        if (mInst == null || mShape == null || mRenderer == null)
            return;
        long now = mApp.Host.TickMs;
        if (mThread != null && mLastMs >= 0)
            mInst.Advance(mThread, Math.Min(now - mLastMs, 200) / 1000.0);
        mLastMs = now;
        Camera3D cam = CameraFor(mShape.Cx, mShape.Cy, mShape.Cz, view.Male, view.Yaw, w, h);
        var ctx = new FrameContext { Camera = cam, Assets = mAssets!, Renderer = mRenderer, Light = Light() };
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
