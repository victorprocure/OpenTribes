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
    public int Container = -1;     // the interior (instance index) whose database holds the object, -1 none
    internal bool Drawn;
}

public sealed class RenderLists
{
    public readonly List<RenderImage> First = new(), Opaque = new(), Translucent = new(), Foreground = new();
    // Set by the builder while it adds the images of one object (SceneModel.Build).
    public int CurrentContainer = -1;
    public int ImageCount => First.Count + Opaque.Count + Translucent.Count + Foreground.Count;

    // 2.2 step 4: key = n / depth when depth > n, else 0.99, n the near distance; larger keys are nearer.
    public static double SortKey(double depth, double near) => depth > near ? near / depth : 0.99;

    public void Add(RenderImage img)
    {
        if (img.Batches.Count == 0)
            return;
        img.Container = CurrentContainer;
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

    // The opaque images of the objects in the database of interior `container`: the stock scope draws the contents of a
    // container interior before the interior itself (InteriorShape_findObjects 00480648 vtable +0x98: database, then own image).
    public int DrawContents(IGfxDevice dev, int container)
    {
        int n = 0;
        foreach (RenderImage img in Opaque)
        {
            if (img.Container != container || img.Drawn)
                continue;
            img.Drawn = true;
            foreach (DrawBatch b in img.Batches)
            {
                dev.Draw3D(b.Vertices, b.State);
                ++n;
            }
        }
        return n;
    }

    // Everything not yet drawn.
    public int Draw(IGfxDevice dev)
    {
        int n = 0;
        foreach (RenderImage img in Ordered())
        {
            if (img.Drawn)
                continue;
            foreach (DrawBatch b in img.Batches)
            {
                dev.Draw3D(b.Vertices, b.State);
                ++n;
            }
        }
        return n;
    }

    public void Clear()
    {
        First.Clear(); Opaque.Clear(); Translucent.Clear(); Foreground.Clear();
        CurrentContainer = -1;
    }
}
