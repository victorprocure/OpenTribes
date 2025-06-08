// The render context lists (docs/shapes-render.md 2.7): category 3 first list sorted ascending, category 0 opaque in query
// order, category 1 translucent sorted ascending by key (far to near), category 2 foreground after the scene.
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

public sealed class DrawBatch
{
    public Vertex3D[] Vertices = Array.Empty<Vertex3D>();
    public DrawState3D State;
}

public sealed class RenderImage
{
    public int Category;           // 0 opaque, 1 translucent, 2 foreground, 3 first
    public double SortKey;
    public readonly List<DrawBatch> Batches = new();
    public string Label = "";
}

public sealed class RenderLists
{
    public readonly List<RenderImage> First = new(), Opaque = new(), Translucent = new(), Foreground = new();
    public int ImageCount => First.Count + Opaque.Count + Translucent.Count + Foreground.Count;

    // 2.2 step 4: key = n / depth when depth > n, else 0.99, n the near distance; larger keys are nearer.
    public static double SortKey(double depth, double near) => depth > near ? near / depth : 0.99;

    public void Add(RenderImage img)
    {
        if (img.Batches.Count == 0)
            return;
        switch (img.Category)
        {
            case 3: First.Add(img); break;
            case 1: Translucent.Add(img); break;
            case 2: Foreground.Add(img); break;
            default: Opaque.Add(img); break;
        }
    }

    // The images in drawing order: first (ascending), opaque (query order), translucent (ascending key), foreground.
    public IEnumerable<RenderImage> Ordered()
    {
        foreach (RenderImage i in First.OrderBy(x => x.SortKey))
            yield return i;
        foreach (RenderImage i in Opaque)
            yield return i;
        foreach (RenderImage i in Translucent.OrderBy(x => x.SortKey))   // stable
            yield return i;
        foreach (RenderImage i in Foreground)
            yield return i;
    }

    public int Draw(IGfxDevice dev)
    {
        int n = 0;
        foreach (RenderImage img in Ordered())
            foreach (DrawBatch b in img.Batches)
            {
                dev.Draw3D(b.Vertices, b.State);
                ++n;
            }
        return n;
    }

    public void Clear()
    {
        First.Clear(); Opaque.Clear(); Translucent.Clear(); Foreground.Clear();
    }
}
