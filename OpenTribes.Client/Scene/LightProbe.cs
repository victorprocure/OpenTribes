namespace OpenTribes.Client.Scene;

/// <summary>The shape light probe: a ray from the top of the box of an object straight down to the nearest terrain or interior surface.</summary>
/// <remarks>Follows ShapeBase_queryLight (0044ebc8), ShapeBase_lightProbe (0044e6f4) and the smoothing (0044e5f4, 0044e50c). The ray is h + 20 long (h the half height of the box along the ray). The light map value at the hit and the strength (length - t) / length become the ambient and sun adjust of the object (<c>LightSet.WithProbe</c>).</remarks>
public sealed class LightProbe
{
    /// <summary>Casts the ray into the interiors. Takes the ray start and direction and returns the hit, its distance and the light there.</summary>
    public Func<double, double, double, double, double, double, double, (bool hit, double t, (float r, float g, float b) light)>? Interior { get; set; }
    /// <summary>Reads the terrain light map value at an x and y position.</summary>
    public Func<double, double, (float r, float g, float b)>? TerrainLight { get; set; }
    /// <summary>The terrain the ray tests.</summary>
    public OpenTribes.World.Terrain? Ground { get; set; }

    /// <summary>Probes the light below an object.</summary>
    /// <param name="box">The box of the object as minimum x, y, z then maximum x, y, z.</param>
    /// <returns>Whether a surface was hit, the light colour there and the strength.</returns>
    public (bool ok, (float r, float g, float b) rgb, float strength) Query(double[] box)
    {
        double cx = (box[0] + box[3]) * 0.5, cy = (box[1] + box[4]) * 0.5, cz = (box[2] + box[5]) * 0.5;
        var h = (box[5] - box[2]) * 0.5; // the ray is straight down, so the half height is the largest projected half extent
        double sz = cz + h, length = h + 20;
        var best = double.MaxValue;
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

/// <summary>The per object state of the probe: the target and the smoothed colour and strength, halved toward the target at each update (0044e5f4).</summary>
public sealed class ProbeState
{
    private bool _valid;
    private int _countdown;
    private (float r, float g, float b) _cur, _target;
    private float _targetStrength;
    /// <summary>The time of the last update, or -1 before the first.</summary>
    public double LastUpdate { get; set; } = -1;

    /// <summary>Feeds the result of one probe and moves the smoothed values.</summary>
    /// <param name="found">True when the ray hit a surface.</param>
    /// <param name="rgb">The light colour at the hit.</param>
    /// <param name="strength">The strength of the hit.</param>
    public void Update(bool found, (float r, float g, float b) rgb, float strength)
    {
        if (found)
        {
            if (!_valid && _countdown == 0)
                _cur = rgb;
            _countdown = 0;
            _targetStrength = strength;
            _target = rgb;
        }
        else if (_valid)
        {
            _countdown = 9;
            _targetStrength = 0;
        }
        else if (_countdown != 0)
            --_countdown;
        _valid = found;
        if (_valid || _countdown != 0)
        {
            _cur = ((_cur.r + _target.r) * 0.5f, (_cur.g + _target.g) * 0.5f, (_cur.b + _target.b) * 0.5f);
            Strength = (Strength + _targetStrength) * 0.5f;
            if (_countdown != 0)
                --_countdown;
        }
    }

    /// <summary>True while the smoothed light applies.</summary>
    public bool Active => _valid || _countdown != 0;
    /// <summary>The smoothed light colour.</summary>
    public (float r, float g, float b) Colour => _cur;
    /// <summary>The smoothed strength.</summary>
    public float Strength { get; private set; }
}
