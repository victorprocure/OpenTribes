namespace OpenTribes.Client.World;

using System.Buffers.Binary;
using OpenTribes.Core;

/// <summary>The <c>.dil</c> lighting file (ITRLighting, ITRMissionLighting), its light maps and the light state selection (world-render.md 3.6 and 3.7).</summary>
public sealed class InteriorLighting
{
    /// <summary>The state of a light: a colour and the time it ends.</summary>
    public struct State
    {
        /// <summary>The red word.</summary>
        public ushort R { get; set; }
        /// <summary>The green word.</summary>
        public ushort G { get; set; }
        /// <summary>The blue word.</summary>
        public ushort B { get; set; }
        /// <summary>The mono word.</summary>
        public ushort Mono { get; set; }
        /// <summary>The time the state ends.</summary>
        public float EndTime { get; set; }
        /// <summary>The number of state data records.</summary>
        public short DataCount { get; set; }
        /// <summary>The index of the first state data record.</summary>
        public short DataIndex { get; set; }
    }

    /// <summary>A surface and light map slot a state touches.</summary>
    public struct StateData
    {
        /// <summary>The surface index.</summary>
        public short Surface { get; set; }
        /// <summary>The light slot of the surface.</summary>
        public short Slot { get; set; }
        /// <summary>The source map index.</summary>
        public int MapIndex { get; set; }
    }

    /// <summary>A light.</summary>
    public struct Light
    {
        /// <summary>The light id.</summary>
        public int Id { get; set; }
        /// <summary>The offset of the name.</summary>
        public int NameOffset { get; set; }
        /// <summary>The number of states.</summary>
        public int StateCount { get; set; }
        /// <summary>The index of the first state.</summary>
        public int FirstState { get; set; }
        /// <summary>The duration of one cycle.</summary>
        public float Duration { get; set; }
        /// <summary>The flags: 1 auto start, 2 loop, 4 random.</summary>
        public uint Flags { get; set; }
        /// <summary>The light name.</summary>
        public string Name { get; set; }
        /// <summary>True when the light starts by itself.</summary>
        public readonly bool AutoStart => (Flags & 1) != 0;
        /// <summary>True when the light loops.</summary>
        public readonly bool Loop => (Flags & 2) != 0;
        /// <summary>True for a random light, which picks its states privately.</summary>
        public readonly bool Random => (Flags & 4) != 0;
    }

    /// <summary>A surface light map.</summary>
    public struct Surface
    {
        /// <summary>The map word: bit 31 constant colour, bit 30 compressed, else a raw offset.</summary>
        public uint Word { get; set; }
        /// <summary>The number of lights that touch the surface.</summary>
        public short LightCount { get; set; }
        /// <summary>The index of the first slot.</summary>
        public short FirstSlot { get; set; }
        /// <summary>The map width.</summary>
        public byte SizeX { get; set; }
        /// <summary>The map height.</summary>
        public byte SizeY { get; set; }
        /// <summary>The map offset x.</summary>
        public byte OffsetX { get; set; }
        /// <summary>The map offset y.</summary>
        public byte OffsetY { get; set; }
    }

    /// <summary>True for a mission lighting file.</summary>
    public bool IsMission { get; set; }
    /// <summary>The build id of the file.</summary>
    public uint BuildId { get; set; }
    /// <summary>The light scale shift.</summary>
    public uint LightScaleShift { get; set; }
    /// <summary>The size of the map list.</summary>
    public uint MapListSize { get; set; }
    /// <summary>The states.</summary>
    public State[] States { get; set; } = [];
    /// <summary>The state data records.</summary>
    public StateData[] StateDatas { get; set; } = [];
    /// <summary>The lights.</summary>
    public Light[] Lights { get; set; } = [];
    /// <summary>The surfaces.</summary>
    public Surface[] Surfaces { get; set; } = [];
    /// <summary>The packed map data.</summary>
    public byte[] MapData { get; set; } = [];
    /// <summary>The Huffman table nodes: the child for bit 1 and the child for bit 0.</summary>
    public int[,] Nodes { get; set; } = new int[0, 2];
    /// <summary>The Huffman table leaves, the low 16 bits.</summary>
    public uint[] Leaves { get; set; } = [];
    /// <summary>True when the file has a Huffman table.</summary>
    public bool HasHuffman { get; set; }
    /// <summary>The offset of the tail of the file.</summary>
    public int TailOffset { get; set; }
    /// <summary>The length of the file.</summary>
    public int FileLength { get; set; }
    /// <summary>The ITRMissionLighting index: the sorted source map indices.</summary>
    public uint[] MissionSource { get; set; } = [];
    /// <summary>The destination offsets in <see cref="MapData"/> of the source maps.</summary>
    public uint[] MissionDest { get; set; } = [];

    /// <summary>Parses a <c>.dil</c> file.</summary>
    /// <param name="d">The file bytes.</param>
    /// <returns>The lighting, or null when the file is not valid.</returns>
    public static InteriorLighting? Parse(byte[] d)
    {
        try
        {
            if (d.Length < 16 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
                return null;
            var lt = new InteriorLighting();
            long pos = 8;
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2));
            var cls = System.Text.Encoding.Latin1.GetString(d, (int)pos + 2, nameLen);
            lt.IsMission = cls.Equals("ITRMissionLighting", StringComparison.OrdinalIgnoreCase);
            pos += 2 + ((nameLen + 1) & ~1);
            pos += 4; // version
            uint U32()
            { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            int I32()
            { var v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            short I16()
            { var v = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan((int)pos, 2)); pos += 2; return v; }
            ushort U16()
            { var v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2)); pos += 2; return v; }
            float F32()
            { var v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            lt.BuildId = U32();
            lt.LightScaleShift = U32();
            lt.MapListSize = U32();
            uint nStates = U32(), nData = U32(), nLights = U32(), nSurfaces = U32(), nMap = U32();
            if (nStates > 1 << 20 || nData > 1 << 22 || nLights > 1 << 20 || nSurfaces > 1 << 22 || nMap > d.Length)
                return null;
            lt.States = new State[nStates];
            for (var i = 0; i < nStates; ++i)
            {
                lt.States[i] = new State { R = U16(), G = U16(), B = U16(), Mono = U16(), EndTime = F32(), DataCount = I16(), DataIndex = I16() };
            }
            lt.StateDatas = new StateData[nData];
            for (var i = 0; i < nData; ++i)
                lt.StateDatas[i] = new StateData { Surface = I16(), Slot = I16(), MapIndex = I32() };
            lt.Lights = new Light[nLights];
            for (var i = 0; i < nLights; ++i)
                lt.Lights[i] = new Light { Id = I32(), NameOffset = I32(), StateCount = I32(), FirstState = I32(), Duration = F32(), Flags = U32(), Name = "" };
            lt.Surfaces = new Surface[nSurfaces];
            for (var i = 0; i < nSurfaces; ++i)
            {
                lt.Surfaces[i] = new Surface
                {
                    Word = U32(),
                    LightCount = I16(),
                    FirstSlot = I16(),
                    SizeX = d[pos++],
                    SizeY = d[pos++],
                    OffsetX = d[pos++],
                    OffsetY = d[pos++]
                };
            }
            lt.MapData = d.AsSpan((int)pos, (int)nMap).ToArray();
            pos += nMap;
            var nameSize = U32();
            var names = pos;
            pos += nameSize;
            for (var i = 0; i < lt.Lights.Length; ++i)
                if (lt.Lights[i].NameOffset >= 0 && lt.Lights[i].NameOffset < nameSize)
                {
                    int e = (int)names + lt.Lights[i].NameOffset, n = 0;
                    while (e + n < names + nameSize && d[e + n] != 0)
                        ++n;
                    lt.Lights[i].Name = System.Text.Encoding.Latin1.GetString(d, e, n);
                }
            var flag = d[pos++];
            if (flag == 1)
            {
                uint nodeCount = U32(), leafCount = U32();
                lt.Nodes = new int[nodeCount, 2];
                for (var i = 0; i < nodeCount; ++i)
                {
                    lt.Nodes[i, 0] = I32(); // child for bit 1
                    lt.Nodes[i, 1] = I32(); // child for bit 0
                }
                lt.Leaves = new uint[leafCount];
                for (var i = 0; i < leafCount; ++i)
                    lt.Leaves[i] = U32();
                lt.HasHuffman = true;
            }
            lt.TailOffset = (int)pos;
            lt.FileLength = d.Length;
            if (lt.IsMission && pos + 8 <= d.Length)
            {
                var n = U32();
                if (n is > 0 and < (1 << 22))
                {
                    var o = new byte[n * 8];
                    Lzh.LzhDecodeCounted(d.AsSpan((int)pos), o);
                    lt.MissionSource = new uint[n];
                    lt.MissionDest = new uint[n];
                    for (var i = 0; i < n; ++i)
                    {
                        lt.MissionSource[i] = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(i * 8, 4));
                        lt.MissionDest[i] = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan((i * 8) + 4, 4));
                    }
                }
            }
            return lt;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Decodes a compressed map (3.6): one bit at a time (LSB first in each byte), from node 0 taking the child for bit 1 or bit 0 until a negative child (-(k+1) is leaf k). The low 16 bits of the leaf is the pixel.</summary>
    /// <param name="offset">The bit offset of the map.</param>
    /// <param name="pixels">The number of pixels.</param>
    /// <returns>The pixels.</returns>
    public ushort[] DecodeHuffman(int offset, int pixels)
    {
        var r = new ushort[pixels];
        var bit = 0;
        for (var p = 0; p < pixels; ++p)
        {
            var node = 0;
            while (node >= 0)
            {
                var bi = offset + (bit >> 3);
                if (bi >= MapData.Length || node >= Nodes.GetLength(0))
                    return r;
                var b = (MapData[bi] >> (bit & 7)) & 1;
                ++bit;
                node = Nodes[node, b == 1 ? 0 : 1];
            }
            var leaf = -(node + 1);
            r[p] = leaf < Leaves.Length ? (ushort)(Leaves[leaf] & 0xffff) : (ushort)0;
        }
        return r;
    }

    /// <summary>Reads the pixels (u16 nibbles M R G B) of the base light map of a surface, sizeX * sizeY.</summary>
    /// <param name="surface">The surface index.</param>
    /// <param name="mission">The mission lighting that supplies replacement maps (3.7), or null.</param>
    /// <returns>The pixels, or null when the surface has no size.</returns>
    public ushort[]? SurfaceMap(int surface, InteriorLighting? mission)
    {
        var s = Surfaces[surface];
        var px = s.SizeX * s.SizeY;
        if (px <= 0)
            return null;
        if ((s.Word & 0x80000000u) != 0)
        {
            var r = new ushort[px];
            Array.Fill(r, (ushort)(s.Word & 0xffff));
            return r;
        }
        var index = s.Word & 0xbfffffff;
        var compressed = (s.Word & 0x40000000u) != 0;
        var src = this;
        long offset = index;
        if (mission != null && mission.TryMissionOffset(index, out var dest))
        {
            src = mission;
            offset = dest;
        }
        if (compressed)
            return src.DecodeHuffman((int)offset, px);
        var raw = new ushort[px];
        for (var i = 0; i < px; ++i)
        {
            var o = offset + (i * 2);
            if (o + 2 > src.MapData.Length)
                break;
            raw[i] = BinaryPrimitives.ReadUInt16LittleEndian(src.MapData.AsSpan((int)o, 2));
        }
        return raw;
    }

    /// <summary>Looks up the destination of a source map in the mission lighting.</summary>
    /// <param name="sourceIndex">The source map index.</param>
    /// <param name="dest">The destination offset, or 0 when not found.</param>
    /// <returns>True when the map has a replacement.</returns>
    public bool TryMissionOffset(uint sourceIndex, out uint dest)
    {
        var i = Array.BinarySearch(MissionSource, sourceIndex);
        dest = i >= 0 ? MissionDest[i] : 0;
        return i >= 0;
    }

    /// <summary>Selects the state of a light at a time (3.7 Timing): the number of state records after the first whose end time is below the time, limited to stateCount - 1.</summary>
    /// <param name="stateRecords">The state records of the light.</param>
    /// <param name="stateCount">The number of states.</param>
    /// <param name="time">The time in the cycle.</param>
    /// <returns>The state index. Random lights (flag 4) are handled by the caller, they pick states privately.</returns>
    public static int SelectState(ReadOnlySpan<State> stateRecords, int stateCount, float time)
    {
        // interiors.md 5.4: the state is the count of following records (after the first) whose end time is below the
        // time, at most stateCount - 1.
        var state = 0;
        for (var j = 1; j < stateCount && j < stateRecords.Length; ++j)
            if (stateRecords[j].EndTime < time)
                ++state;
        return Math.Min(state, Math.Max(stateCount - 1, 0));
    }

    /// <summary>Computes the colour of a light at a time in a state (3.7 Timing, ITRInstance_animateLight 00556b28): the four words of the state interpolated towards the next record (the first record again after the last state, at the light duration) by the time fraction, each channel then (word / 256 + 0.5) cut to a byte.</summary>
    /// <param name="records">The state records of the light.</param>
    /// <param name="duration">The duration of the cycle.</param>
    /// <param name="state">The state index.</param>
    /// <param name="time">The time in the cycle.</param>
    /// <returns>The mono, red, green and blue bytes.</returns>
    public static (int mono, int r, int g, int b) ColorAt(ReadOnlySpan<State> records, float duration, int state, float time)
    {
        var a = records[state];
        float ta = a.EndTime, tb;
        State n;
        if (state != records.Length - 1)
        {
            n = records[state + 1];
            tb = n.EndTime;
        }
        else
        {
            n = records[0];
            tb = duration;
        }
        var f = tb - ta != 0f ? (time - ta) / (tb - ta) : 1f;
        static int Ch(float from, float to, float f) => (int)(((from + ((to - from) * f)) * 0.00390625f) + 0.5f) & 0xff;
        return (Ch(a.Mono, n.Mono, f), Ch(a.R, n.R, f), Ch(a.G, n.G, f), Ch(a.B, n.B, f));
    }

    /// <summary>Packs the colour of a state (3.7 Building a surface light map): (word + 0x80) >> 8 per channel.</summary>
    /// <param name="s">The state.</param>
    /// <returns>The mono, red, green and blue bytes.</returns>
    public static (int mono, int r, int g, int b) PackedColor(State s) =>
        ((s.Mono + 0x80) >> 8, (s.R + 0x80) >> 8, (s.G + 0x80) >> 8, (s.B + 0x80) >> 8);

    /// <summary>Adds the light of a colour to a light map pixel (3.7 step 3): for every pixel with intensity i >= 16 each of the four channels gets (colourByte * i) >> 12 added to its nibble, each sum clamped to 15.</summary>
    /// <param name="pixel">The pixel, nibbles M R G B.</param>
    /// <param name="c">The colour bytes.</param>
    /// <param name="intensity">The intensity of the light at the pixel.</param>
    /// <returns>The new pixel.</returns>
    public static ushort AddLight(ushort pixel, (int mono, int r, int g, int b) c, int intensity)
    {
        if (intensity < 16)
            return pixel;
        int m = (pixel >> 12) & 0xf, r = (pixel >> 8) & 0xf, g = (pixel >> 4) & 0xf, b = pixel & 0xf;
        m = Math.Min(15, m + ((c.mono * intensity) >> 12));
        r = Math.Min(15, r + ((c.r * intensity) >> 12));
        g = Math.Min(15, g + ((c.g * intensity) >> 12));
        b = Math.Min(15, b + ((c.b * intensity) >> 12));
        return (ushort)((m << 12) | (r << 8) | (g << 4) | b);
    }
}
