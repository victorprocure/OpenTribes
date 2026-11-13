namespace OpenTribes.Client.Shapes;

/// <summary>A playing sequence of a shape instance.</summary>
public sealed class RenderThread
{
    /// <summary>The sequence index, -1 for none.</summary>
    public int Sequence = -1;
    /// <summary>The position in the sequence, 0 to 1.</summary>
    public double Position;
    /// <summary>The playback speed factor. A negative value plays backwards.</summary>
    public double TimeScale = 1;
    /// <summary>The priority of the thread.</summary>
    public int Priority;
    /// <summary>The position before the last advance.</summary>
    public double LastPosition;

    /// <summary>Tells whether a one shot sequence reached its end.</summary>
    /// <param name="s">The shape that owns the sequence.</param>
    /// <returns>True when the sequence is not cyclic and the position is at the end in the direction of play.</returns>
    public bool AtEnd(RenderShape s) => Sequence >= 0 && Sequence < s.Sequences.Length && !s.Sequences[Sequence].Cyclic &&
        (TimeScale >= 0 ? Position >= 1.0 : Position <= 0.0);
}

/// <summary>The state of one object of the shape.</summary>
public struct RsObjectState
{
    /// <summary>The frame index.</summary>
    public int Frame;
    /// <summary>The texture frame index.</summary>
    public int TextureFrame;
    /// <summary>True when the object draws.</summary>
    public bool Visible;
}

/// <summary>The render side instance of a shape: threads, sequence stepping, node transforms by interpolated keys, object frames, visibility and texture frames, the image list remap (1.5), frame triggers (3.7) and run time transitions (3.3).</summary>
/// <remarks>docs/shapes-render.md 3. It computes in double. The <c>ShapeInstance</c> of the server (Fpu truncating math) is not used.</remarks>
public sealed class RenderInstance
{
    /// <summary>The shape.</summary>
    public readonly RenderShape Shape;
    /// <summary>The playing threads.</summary>
    public readonly List<RenderThread> Threads = [];
    /// <summary>The image list remap of the materials (1.5).</summary>
    public readonly int[] Remap;
    /// <summary>The transform of each node to shape space.</summary>
    public readonly Xf[] NodeWorld;
    /// <summary>The state of each object.</summary>
    public readonly RsObjectState[] Objects;
    /// <summary>The local rotation of each node as x, y, z, w, as animated.</summary>
    public readonly double[][] LocalQ;
    /// <summary>The local translation of each node, as animated.</summary>
    public readonly double[][] LocalT;
    /// <summary>True for each node that is visible.</summary>
    public bool[] NodeVisible;
    /// <summary>The detail the nodes were last animated for, -2 for none.</summary>
    public int DetailAnimated = -2;

    /// <summary>The player view mode (player-sim.md 5.2, byte +0x9e of the instance): 0 none, 1 chase or third person, 2 first person.</summary>
    /// <remarks>With a mode set, a <c>lowerback</c> node that a thread animates does not hang on its parent. Its parent frame is the fixed upright frame (yaw pi / 2) at the position of the parent (mode 1) or at (0, 0, height of <c>dummyalways root</c>) (mode 2), so the whole upper body, the eye and the hand follow the looks thread alone and never the leg cycle.</remarks>
    public int ViewMode;
    private readonly bool[] mLowerBack;
    private readonly int mRootNode;

    // Transition (3.3): blend from the pose at the switch to the animated pose.
    private double[][]? mFromQ, mFromT;
    private double mBlendLeft, mBlendDuration;

    /// <summary>Creates an instance of a shape with no threads.</summary>
    /// <param name="shape">The shape.</param>
    public RenderInstance(RenderShape shape)
    {
        Shape = shape;
        var rm = Math.Max(shape.RemapSize, shape.MaterialCount);
        Remap = new int[Math.Max(rm, 1)];
        for (var i = 0; i < Remap.Length; ++i)
            Remap[i] = i;
        NodeWorld = new Xf[shape.Nodes.Length];
        for (var i = 0; i < NodeWorld.Length; ++i)
            NodeWorld[i] = Xf.Identity;
        Objects = new RsObjectState[shape.Objects.Length];
        LocalQ = new double[shape.Nodes.Length][];
        LocalT = new double[shape.Nodes.Length][];
        NodeVisible = new bool[shape.Nodes.Length];
        for (var i = 0; i < shape.Nodes.Length; ++i)
        {
            LocalQ[i] = new double[4];
            LocalT[i] = new double[3];
        }
        mLowerBack = new bool[shape.Nodes.Length];
        mRootNode = -1;
        for (var i = 0; i < shape.Nodes.Length; ++i)
        {
            var name = shape.Names[shape.Nodes[i].Name];
            mLowerBack[i] = name.StartsWith("lowerback", StringComparison.Ordinal);
            if (mRootNode < 0 && name == "dummyalways root")
                mRootNode = i;
        }
        Animate(0);
    }

    /// <summary>Adds a thread.</summary>
    /// <param name="sequence">The sequence index.</param>
    /// <param name="position">The start position, 0 to 1.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>The thread.</returns>
    public RenderThread AddThread(int sequence, double position = 0, int priority = 0)
    {
        var t = new RenderThread { Sequence = sequence, Position = position, Priority = priority, LastPosition = position };
        Threads.Add(t);
        return t;
    }

    /// <summary>Adds a thread by sequence name.</summary>
    /// <param name="sequence">The sequence name.</param>
    /// <param name="position">The start position, 0 to 1.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>The thread, or null when the shape has no such sequence.</returns>
    public RenderThread? AddThread(string sequence, double position = 0, int priority = 0)
    {
        var s = Shape.FindSequence(sequence);
        return s < 0 ? null : AddThread(s, position, priority);
    }

    /// <summary>Advances one thread: position += dt * timeScale / duration (shapes-render.md 3.1). A cyclic sequence wraps and a one shot clamps to 0..1.</summary>
    /// <param name="t">The thread.</param>
    /// <param name="dt">The time step in seconds.</param>
    /// <param name="triggers">Receives the triggers the thread passed, or null.</param>
    public void Advance(RenderThread t, double dt, List<uint>? triggers = null)
    {
        if (t.Sequence < 0 || t.Sequence >= Shape.Sequences.Length)
            return;
        RsSequence q = Shape.Sequences[t.Sequence];
        var dur = q.Duration > 1e-6 ? q.Duration : 1e-6;
        var old = t.Position;
        var p = old + (dt * t.TimeScale / dur);
        var wrapped = false;
        if (q.Cyclic)
        {
            var f = p - Math.Floor(p);
            wrapped = Math.Floor(p) != Math.Floor(old);
            p = f;
        }
        else
            p = Math.Clamp(p, 0, 1);
        t.LastPosition = old;
        t.Position = p;
        if (triggers != null)
            CollectTriggers(t, q, old, p, wrapped, triggers);
    }

    // shapes-render.md 3.7: a record {position, value} fires when the position is passed forward (position > 0) or
    // backward (position < 0); a cyclic wrap counts.
    private void CollectTriggers(RenderThread t, in RsSequence q, double from, double to, bool wrapped, List<uint> output)
    {
        for (var i = 0; i < q.NumTriggers && q.FirstTrigger + i < Shape.Triggers.Length; ++i)
        {
            RsTrigger tr = Shape.Triggers[q.FirstTrigger + i];
            double pos = Math.Abs(tr.Position);
            var forward = t.TimeScale >= 0;
            if (tr.Position >= 0 != forward && tr.Position != 0)
                continue;
            bool hit;
            if (wrapped)
                hit = forward ? (pos > from || pos <= to) : (pos < from || pos >= to);
            else
                hit = forward ? (pos > from && pos <= to) : (pos < from && pos >= to);
            if (hit)
                output.Add(tr.Value);
        }
    }

    /// <summary>Advances every thread and the transition blend.</summary>
    /// <param name="dt">The time step in seconds.</param>
    /// <param name="triggers">Receives the triggers the threads passed, or null.</param>
    public void Advance(double dt, List<uint>? triggers = null)
    {
        foreach (RenderThread t in Threads)
            Advance(t, dt, triggers);
        if (mBlendLeft > 0)
        {
            mBlendLeft -= dt;
            if (mBlendLeft <= 0)
                mFromQ = mFromT = null;
        }
    }

    /// <summary>Switches a thread to another sequence and blends the node poses over a duration (3.3).</summary>
    /// <param name="t">The thread.</param>
    /// <param name="sequence">The new sequence index.</param>
    /// <param name="position">The start position.</param>
    /// <param name="duration">The blend time in seconds.</param>
    public void StartTransition(RenderThread t, int sequence, double position, double duration)
    {
        Animate(Math.Max(DetailAnimated, 0));
        mFromQ = [.. LocalQ.Select(a => (double[])a.Clone())];
        mFromT = [.. LocalT.Select(a => (double[])a.Clone())];
        mBlendLeft = mBlendDuration = duration;
        t.Sequence = sequence;
        t.Position = position;
        t.LastPosition = position;
    }

    /// <summary>True while a transition blend runs.</summary>
    public bool Blending => mBlendLeft > 0;

    private int SubFor(int first, int count, int sequence)
    {
        if (count <= 0 || first < 0)
            return -1;
        for (var i = first; i < first + count && i < Shape.SubSequences.Length; ++i)
            if (Shape.SubSequences[i].Sequence == sequence)
                return i;
        return -1;
    }

    /// <summary>Finds the last key whose position is at most p. Before the first key a cyclic sequence takes the last key and a one shot the first.</summary>
    /// <param name="sub">The sub sequence.</param>
    /// <param name="p">The position.</param>
    /// <returns>The key index.</returns>
    public int CurrentKey(in RsSubSequence sub, double p)
    {
        if (sub.NumKeys <= 0 || sub.FirstKey < 0 || sub.FirstKey + sub.NumKeys > Shape.Keys.Length)
            return -1;
        var last = sub.FirstKey - 1;
        for (var i = sub.FirstKey; i < sub.FirstKey + sub.NumKeys; ++i)
        {
            if (Shape.Keys[i].Position > p)
                break;
            last = i;
        }
        if (last < sub.FirstKey)
        {
            var cyclic = sub.Sequence >= 0 && sub.Sequence < Shape.Sequences.Length && Shape.Sequences[sub.Sequence].Cyclic;
            last = cyclic ? sub.FirstKey + sub.NumKeys - 1 : sub.FirstKey;
        }
        return last;
    }

    /// <summary>Finds the pair of keys around p and the blend fraction. Node transforms interpolate (3.1).</summary>
    /// <param name="sub">The sub sequence.</param>
    /// <param name="p">The position.</param>
    /// <param name="lo">The key before p.</param>
    /// <param name="hi">The key after p.</param>
    /// <param name="frac">The blend fraction from <paramref name="lo"/> to <paramref name="hi"/>.</param>
    /// <returns>True when keys were found.</returns>
    public bool FindKeys(in RsSubSequence sub, double p, out int lo, out int hi, out double frac)
    {
        lo = hi = 0;
        frac = 0;
        int n = sub.NumKeys, f = sub.FirstKey;
        if (n <= 0 || f < 0 || f + n > Shape.Keys.Length)
            return false;
        var cyclic = sub.Sequence >= 0 && sub.Sequence < Shape.Sequences.Length && Shape.Sequences[sub.Sequence].Cyclic;
        var idx = -1;
        for (var i = f; i < f + n; ++i)
        {
            if (Shape.Keys[i].Position > p)
                break;
            idx = i;
        }
        int first = f, last = f + n - 1;
        if (n == 1)
        {
            lo = hi = f;
            return true;
        }
        if (idx < 0)
        {
            if (!cyclic)
            {
                lo = hi = first;
                return true;
            }
            lo = last;
            hi = first;
            double span = Shape.Keys[first].Position + 1 - Shape.Keys[last].Position;
            frac = span > 1e-9 ? (p + 1 - Shape.Keys[last].Position) / span : 0;
            return true;
        }
        if (idx == last)
        {
            if (!cyclic)
            {
                lo = hi = last;
                return true;
            }
            lo = last;
            hi = first;
            double span = Shape.Keys[first].Position + 1 - Shape.Keys[last].Position;
            frac = span > 1e-9 ? (p - Shape.Keys[last].Position) / span : 0;
            return true;
        }
        lo = idx;
        hi = idx + 1;
        double d = Shape.Keys[hi].Position - Shape.Keys[lo].Position;
        frac = d > 1e-9 ? (p - Shape.Keys[lo].Position) / d : 0;
        return true;
    }

    private static void Slerp(double[] a, RsTransform tb, double t, double[] q)
    {
        double bx = tb.Qx, by = tb.Qy, bz = tb.Qz, bw = tb.Qw;
        var c = (a[0] * bx) + (a[1] * by) + (a[2] * bz) + (a[3] * bw);
        double sign = 1;
        if (c < 0)
        { c = -c; sign = -1; }
        double s0, s1;
        if (1 - c > 1e-5)
        {
            double om = Math.Acos(Math.Min(c, 1)), so = Math.Sin(om);
            s0 = Math.Sin((1 - t) * om) / so;
            s1 = Math.Sin(t * om) / so * sign;
        }
        else
        {
            s0 = 1 - t;
            s1 = sign * t;
        }
        q[0] = (s0 * a[0]) + (s1 * bx);
        q[1] = (s0 * a[1]) + (s1 * by);
        q[2] = (s0 * a[2]) + (s1 * bz);
        q[3] = (s0 * a[3]) + (s1 * bw);
    }

    private readonly List<RenderThread> mOrdered = [];

    private List<RenderThread> Ordered()
    {
        // Ascending priority: the first thread whose sequence drives a node wins (a lower number is a higher priority, the
        // looks thread uses -1). Stable insertion sort into a list kept between calls (no allocation per frame).
        List<RenderThread> l = mOrdered;
        l.Clear();
        foreach (RenderThread t in Threads)
        {
            var i = l.Count;
            while (i > 0 && l[i - 1].Priority > t.Priority)
                --i;
            l.Insert(i, t);
        }
        return l;
    }

    /// <summary>Recomputes node transforms, object states and the image list remap for the subtree of a detail and the always node.</summary>
    /// <param name="detail">The detail index.</param>
    public void Animate(int detail)
    {
        DetailAnimated = detail;
        RenderShape sh = Shape;
        List<RenderThread> order = Ordered();
        for (var i = 0; i < Remap.Length; ++i)
            Remap[i] = i;
        foreach (RenderThread t in order)
            StepIfl(t);
        var nodes = sh.NodesOfDetail(detail).Sorted;
        var blend = mBlendLeft > 0 && mFromQ != null ? Math.Clamp(1 - (mBlendLeft / mBlendDuration), 0, 1) : 1;
        var q = new double[4];
        foreach (var node in nodes)
        {
            ref RsNode nd = ref sh.Nodes[node];
            Xf entry = Xf.Identity;
            if (node >= 1 && nd.Parent >= 0 && nd.Parent < node)
            {
                var done = false;
                NodeVisible[node] = true;
                foreach (RenderThread t in order)
                {
                    var sub = SubFor(nd.FirstSub, nd.NumSub, t.Sequence);
                    if (sub < 0 || !FindKeys(sh.SubSequences[sub], t.Position, out var lo, out var hi, out var frac))
                        continue;
                    RsKey ka = sh.Keys[lo];
                    if (ka.Index >= sh.Transforms.Length || sh.Keys[hi].Index >= sh.Transforms.Length)
                        continue;
                    RsTransform ta = sh.Transforms[ka.Index], tb = sh.Transforms[sh.Keys[hi].Index];
                    LocalQ[node][0] = ta.Qx;
                    LocalQ[node][1] = ta.Qy;
                    LocalQ[node][2] = ta.Qz;
                    LocalQ[node][3] = ta.Qw;
                    Slerp(LocalQ[node], tb, frac, q);
                    Array.Copy(q, LocalQ[node], 4);
                    LocalT[node][0] = ta.Tx + ((tb.Tx - ta.Tx) * frac);
                    LocalT[node][1] = ta.Ty + ((tb.Ty - ta.Ty) * frac);
                    LocalT[node][2] = ta.Tz + ((tb.Tz - ta.Tz) * frac);

                    done = true;
                    break;
                }
                if (!done)
                {
                    var di = nd.DefaultTransform;
                    if (di < 0 || di >= sh.Transforms.Length)
                        continue;
                    RsTransform d = sh.Transforms[di];
                    LocalQ[node][0] = d.Qx;
                    LocalQ[node][1] = d.Qy;
                    LocalQ[node][2] = d.Qz;
                    LocalQ[node][3] = d.Qw;
                    LocalT[node][0] = d.Tx;
                    LocalT[node][1] = d.Ty;
                    LocalT[node][2] = d.Tz;
                }
                if (blend < 1 && mFromQ != null && mFromT != null)
                {
                    double[] fq = mFromQ[node], cq = LocalQ[node];
                    var dot = (fq[0] * cq[0]) + (fq[1] * cq[1]) + (fq[2] * cq[2]) + (fq[3] * cq[3]);
                    double sg = dot < 0 ? -1 : 1, len = 0;
                    for (var k = 0; k < 4; ++k)
                    {
                        q[k] = (fq[k] * sg * (1 - blend)) + (cq[k] * blend);
                        len += q[k] * q[k];
                    }
                    len = Math.Sqrt(len);
                    if (len > 1e-9)
                        for (var k = 0; k < 4; ++k)
                            cq[k] = q[k] / len;
                    for (var k = 0; k < 3; ++k)
                        LocalT[node][k] = (mFromT[node][k] * (1 - blend)) + (LocalT[node][k] * blend);
                }
                Xf local = Xf.FromQuat(LocalQ[node][0], LocalQ[node][1], LocalQ[node][2], LocalQ[node][3]);
                local.Px = LocalT[node][0];
                local.Py = LocalT[node][1];
                local.Pz = LocalT[node][2];
                if (ViewMode != 0 && mLowerBack[node] && done && (ViewMode == 1 || mRootNode >= 0))
                {
                    Xf frame = Xf.Euler(0, 0, Math.PI / 2);
                    if (ViewMode == 1)
                        (frame.Px, frame.Py, frame.Pz) = (NodeWorld[nd.Parent].Px, NodeWorld[nd.Parent].Py, NodeWorld[nd.Parent].Pz);
                    else
                        frame.Pz = NodeWorld[mRootNode].Pz;
                    entry = local.Then(frame);
                }
                else
                    entry = local.Then(NodeWorld[nd.Parent]);
            }
            NodeWorld[node] = entry;
        }
        // Objects (3.1): defaults, then the threads from the last to the first of the order, each key overriding the
        // fields its flags mark as valid.
        for (var oi = 0; oi < sh.Objects.Length; ++oi)
        {
            ref RsObject ob = ref sh.Objects[oi];
            ref RsObjectState st = ref Objects[oi];
            st.Frame = 0;
            st.TextureFrame = 0;
            st.Visible = (ob.Flags & 1) == 0;
            for (var i = order.Count; i-- > 0;)
            {
                RenderThread t = order[i];
                var sub = SubFor(ob.FirstSub, ob.NumSub, t.Sequence);
                var key = sub >= 0 ? CurrentKey(sh.SubSequences[sub], t.Position) : -1;
                if (key < 0)
                    continue;
                RsKey k = sh.Keys[key];
                if ((k.Flags & 0x1000) != 0)
                    st.Frame = k.Index;
                if ((k.Flags & 0x4000) != 0)
                    st.Visible = (k.Flags & 0x8000) != 0;
                if ((k.Flags & 0x2000) != 0)
                    st.TextureFrame = k.Flags & 0xfff;
            }
        }
    }

    // 1.5: the numIfl sub-sequences from firstIfl step to the last key at most the thread position (no interpolation) and
    // write remap[key.flags & 0xfff] = key.index.
    private void StepIfl(RenderThread t)
    {
        if (t.Sequence < 0 || t.Sequence >= Shape.Sequences.Length)
            return;
        RsSequence q = Shape.Sequences[t.Sequence];
        for (var i = 0; i < q.NumIfl; ++i)
        {
            var si = q.FirstIfl + i;
            if (si < 0 || si >= Shape.SubSequences.Length)
                break;
            var k = CurrentKey(Shape.SubSequences[si], t.Position);
            if (k < 0)
                continue;
            var slot = Shape.Keys[k].Flags & 0xfff;
            if (slot < Remap.Length)
                Remap[slot] = Shape.Keys[k].Index;
        }
    }

    /// <summary>Gets the transform of an object to shape space: its own offset, then its node.</summary>
    /// <param name="objectIndex">The object index.</param>
    /// <returns>The transform.</returns>
    public Xf ObjectTransform(int objectIndex)
    {
        RsObject ob = Shape.Objects[objectIndex];
        Xf node = ob.Node >= 0 && ob.Node < NodeWorld.Length ? NodeWorld[ob.Node] : Xf.Identity;
        return Xf.Translate(ob.Ox, ob.Oy, ob.Oz).Then(node);
    }
}
