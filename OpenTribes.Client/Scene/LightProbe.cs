using OpenTribes.Client.Shapes;

namespace OpenTribes.Client.Scene;

// The shape light probe (ShapeBase_queryLight 0044ebc8, ShapeBase_lightProbe 0044e6f4, the smoothing 0044e5f4 and 0044e50c): a ray from the top of
// the object's box straight down, h + 20 long (h the box half height along the ray), to the nearest terrain or interior surface; the light map
// value there and the strength (length - t) / length become the object's ambient and sun adjust (LightSet.WithProbe).
public sealed class LightProbe
{
    public Func<double, double, double, double, double, double, double, (bool hit, double t, (float r, float g, float b) light)>? Interior;
    public Func<double, double, (float r, float g, float b)>? TerrainLight;
    public OpenTribes.World.Terrain? Ground;

    public (bool ok, (float r, float g, float b) rgb, float strength) Query(double[] box)
    {
        double cx = (box[0] + box[3]) * 0.5, cy = (box[1] + box[4]) * 0.5, cz = (box[2] + box[5]) * 0.5;
        double h = (box[5] - box[2]) * 0.5; // the ray is straight down, so the half height is the largest projected half extent
        double sz = cz + h, length = h + 20;
        double best = double.MaxValue;
        (float r, float g, float b) col = default;
        if (Interior != null)
        {
            var (hit, t, light) = Interior(cx, cy, sz, 0, 0, -1, length);
            if (hit)
                (best, col) = (t, light);
        }
        if (Ground != null && TerrainLight != null)
            for (double t = 0; t <= length && t < best; t += 0.25)
            {
                float gh = 0;
                if (Ground.Height((float)cx, (float)cy, ref gh) && sz - t <= gh)
                {
                    best = t;
                    col = TerrainLight(cx, cy);
                    break;
                }
            }
        if (best == double.MaxValue || best >= length)
            return (false, default, 0);
        return (true, col, (float)((length - best) / length));
    }
}

// The per object state of 0044e5f4: the target and the smoothed colour and strength (halved toward the target at each update).
public sealed class ProbeState
{
    private bool mValid;
    private int mCountdown;
    private (float r, float g, float b) mCur, mTarget;
    private float mStrength, mTargetStrength;
    public double LastUpdate = -1;

    public void Update(bool found, (float r, float g, float b) rgb, float strength)
    {
        if (found)
        {
            if (!mValid && mCountdown == 0)
                mCur = rgb;
            mCountdown = 0;
            mTargetStrength = strength;
            mTarget = rgb;
        }
        else if (mValid)
        {
            mCountdown = 9;
            mTargetStrength = 0;
        }
        else if (mCountdown != 0)
            --mCountdown;
        mValid = found;
        if (mValid || mCountdown != 0)
        {
            mCur = ((mCur.r + mTarget.r) * 0.5f, (mCur.g + mTarget.g) * 0.5f, (mCur.b + mTarget.b) * 0.5f);
            mStrength = (mStrength + mTargetStrength) * 0.5f;
            if (mCountdown != 0)
                --mCountdown;
        }
    }

    public bool Active => mValid || mCountdown != 0;
    public (float r, float g, float b) Colour => mCur;
    public float Strength => mStrength;
}
