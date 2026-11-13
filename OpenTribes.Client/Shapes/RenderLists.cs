namespace OpenTribes.Client.Shapes;

using OpenTribes.Platform.Gfx;

/// <summary>One batch of triangles that draws with one state.</summary>
public sealed class DrawBatch
{
    /// <summary>The vertices.</summary>
    public Vertex3D[] Vertices = [];
    /// <summary>The draw state.</summary>
    public DrawState3D State;
}

/// <summary>The drawing of one object: batches in a category with a sort key.</summary>
public sealed class RenderImage
{
    /// <summary>The category: 0 opaque, 1 translucent, 2 foreground, 3 first.</summary>
    public int Category;
    /// <summary>The sort key inside the category.</summary>
    public double SortKey;
    /// <summary>The batches to draw.</summary>
    public readonly List<DrawBatch> Batches = [];
    /// <summary>The label, for traces.</summary>
    public string Label = "";
    /// <summary>The interior (instance index) whose database holds the object, -1 for none.</summary>
    public int Container = -1;
    internal bool Drawn;
}

/// <summary>The render context lists: category 3 first list sorted ascending, category 0 opaque in query order, category 1 translucent sorted ascending by key (far to near), category 2 foreground after the scene.</summary>
/// <remarks>docs/shapes-render.md 2.7.</remarks>
public sealed class RenderLists
{
    /// <summary>The first list (category 3).</summary>
    public readonly List<RenderImage> First = [];
    /// <summary>The opaque list (category 0).</summary>
    public readonly List<RenderImage> Opaque = [];
    /// <summary>The translucent list (category 1).</summary>
    public readonly List<RenderImage> Translucent = [];
    /// <summary>The foreground list (category 2).</summary>
    public readonly List<RenderImage> Foreground = [];
    /// <summary>The interior container of the images added now. The builder sets it while it adds the images of one object (<c>SceneModel.Build</c>).</summary>
    public int CurrentContainer = -1;
    /// <summary>The number of images in all lists.</summary>
    public int ImageCount => First.Count + Opaque.Count + Translucent.Count + Foreground.Count;

    /// <summary>Computes the sort key of a translucent image (2.2 step 4): n / depth when depth is greater than n, else 0.99, where n is the near distance. Larger keys are nearer.</summary>
    /// <param name="depth">The depth of the image.</param>
    /// <param name="near">The near distance.</param>
    /// <returns>The key.</returns>
    public static double SortKey(double depth, double near) => depth > near ? near / depth : 0.99;

    /// <summary>Adds an image to the list of its category. Images without batches are dropped.</summary>
    /// <param name="img">The image.</param>
    public void Add(RenderImage img)
    {
        if (img.Batches.Count == 0)
            return;
        img.Container = CurrentContainer;
        switch (img.Category)
        {
            case 3:
                First.Add(img);
                break;
            case 1:
                Translucent.Add(img);
                break;
            case 2:
                Foreground.Add(img);
                break;
            default:
                Opaque.Add(img);
                break;
        }
    }

    /// <summary>Lists the images in drawing order: first (ascending), opaque (query order), translucent (ascending key), foreground.</summary>
    /// <returns>The images.</returns>
    public IEnumerable<RenderImage> Ordered()
    {
        foreach (var i in First.OrderBy(x => x.SortKey))
            yield return i;
        foreach (var i in Opaque)
            yield return i;
        foreach (var i in Translucent.OrderBy(x => x.SortKey))   // stable
            yield return i;
        foreach (var i in Foreground)
            yield return i;
    }

    /// <summary>Draws the opaque images of the objects in the database of an interior.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="container">The interior instance index.</param>
    /// <returns>The number of batches drawn.</returns>
    /// <remarks>The stock scope draws the contents of a container interior before the interior itself (InteriorShape_findObjects 00480648, vtable +0x98: database, then own image).</remarks>
    public int DrawContents(IGfxDevice dev, int container)
    {
        var n = 0;
        foreach (var img in Opaque)
        {
            if (img.Container != container || img.Drawn)
                continue;
            img.Drawn = true;
            foreach (var b in img.Batches)
            {
                dev.Draw3D(b.Vertices, b.State);
                ++n;
            }
        }
        return n;
    }

    /// <summary>Draws every image not yet drawn, in drawing order.</summary>
    /// <param name="dev">The graphics device.</param>
    /// <returns>The number of batches drawn.</returns>
    public int Draw(IGfxDevice dev)
    {
        var n = 0;
        foreach (var img in Ordered())
        {
            if (img.Drawn)
                continue;
            foreach (var b in img.Batches)
            {
                dev.Draw3D(b.Vertices, b.State);
                ++n;
            }
        }
        return n;
    }

    /// <summary>Empties all lists and resets the container.</summary>
    public void Clear()
    {
        First.Clear();
        Opaque.Clear();
        Translucent.Clear();
        Foreground.Clear();
        CurrentContainer = -1;
    }
}
