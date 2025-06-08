using System.Buffers.Binary;
using OpenTribes.Core;

namespace OpenTribes.Client.World;

// world-render.md 3.6 and 3.7: the `.dil` lighting file (ITRLighting, ITRMissionLighting), its light maps and the light
// state selection.
public sealed class InteriorLighting
{
    public struct State
    {
        public ushort R, G, B, Mono;
        public float EndTime;
        public short DataCount, DataIndex;
    }

    public struct StateData
    {
        public short Surface, Slot;
        public int MapIndex;
    }

    public struct Light
    {
        public int Id, NameOffset, StateCount, FirstState;
        public float Duration;
        public uint Flags;
        public string Name;
        public bool AutoStart => (Flags & 1) != 0;
        public bool Loop => (Flags & 2) != 0;
        public bool Random => (Flags & 4) != 0;
    }

    public struct Surface
    {
        public uint Word;      // bit 31 constant colour, bit 30 compressed, else raw offset
        public short LightCount, FirstSlot;
        public byte SizeX, SizeY, OffsetX, OffsetY;
    }

    public bool IsMission;
    public uint BuildId, LightScaleShift, MapListSize;
    public State[] States = Array.Empty<State>();
    public StateData[] StateDatas = Array.Empty<StateData>();
    public Light[] Lights = Array.Empty<Light>();
    public Surface[] Surfaces = Array.Empty<Surface>();
    public byte[] MapData = Array.Empty<byte>();
    // Huffman table: nodes (child for bit 1, child for bit 0), leaves low 16 bits.
    public int[,] Nodes = new int[0, 2];
    public uint[] Leaves = Array.Empty<uint>();
    public bool HasHuffman;
    public int TailOffset, FileLength;
    // ITRMissionLighting index: sorted source map indices and their destination offsets in MapData.
    public uint[] MissionSource = Array.Empty<uint>(), MissionDest = Array.Empty<uint>();

    public static InteriorLighting? Parse(byte[] d)
    {
        try
        {
            if (d.Length < 16 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
                return null;
            var lt = new InteriorLighting();
            long pos = 8;
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2));
            string cls = System.Text.Encoding.Latin1.GetString(d, (int)pos + 2, nameLen);
            lt.IsMission = cls.Equals("ITRMissionLighting", StringComparison.OrdinalIgnoreCase);
            pos += 2 + ((nameLen + 1) & ~1);
            pos += 4; // version
            uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            short I16() { short v = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan((int)pos, 2)); pos += 2; return v; }
            ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2)); pos += 2; return v; }
            float F32() { float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            lt.BuildId = U32();
            lt.LightScaleShift = U32();
            lt.MapListSize = U32();
            uint nStates = U32(), nData = U32(), nLights = U32(), nSurfaces = U32(), nMap = U32();
            if (nStates > 1 << 20 || nData > 1 << 22 || nLights > 1 << 20 || nSurfaces > 1 << 22 || nMap > d.Length)
                return null;
            lt.States = new State[nStates];
            for (int i = 0; i < nStates; ++i)
            {
                lt.States[i] = new State { R = U16(), G = U16(), B = U16(), Mono = U16(), EndTime = F32(), DataCount = I16(), DataIndex = I16() };
            }
            lt.StateDatas = new StateData[nData];
            for (int i = 0; i < nData; ++i)
                lt.StateDatas[i] = new StateData { Surface = I16(), Slot = I16(), MapIndex = I32() };
            lt.Lights = new Light[nLights];
            for (int i = 0; i < nLights; ++i)
                lt.Lights[i] = new Light { Id = I32(), NameOffset = I32(), StateCount = I32(), FirstState = I32(), Duration = F32(), Flags = U32(), Name = "" };
            lt.Surfaces = new Surface[nSurfaces];
            for (int i = 0; i < nSurfaces; ++i)
            {
                lt.Surfaces[i] = new Surface { Word = U32(), LightCount = I16(), FirstSlot = I16() };
                lt.Surfaces[i].SizeX = d[pos++];
                lt.Surfaces[i].SizeY = d[pos++];
                lt.Surfaces[i].OffsetX = d[pos++];
                lt.Surfaces[i].OffsetY = d[pos++];
            }
            lt.MapData = d.AsSpan((int)pos, (int)nMap).ToArray();
            pos += nMap;
            uint nameSize = U32();
            long names = pos;
            pos += nameSize;
            for (int i = 0; i < lt.Lights.Length; ++i)
                if (lt.Lights[i].NameOffset >= 0 && lt.Lights[i].NameOffset < nameSize)
                {
                    int e = (int)names + lt.Lights[i].NameOffset, n = 0;
                    while (e + n < names + nameSize && d[e + n] != 0)
                        ++n;
                    lt.Lights[i].Name = System.Text.Encoding.Latin1.GetString(d, e, n);
                }
            byte flag = d[pos++];
            if (flag == 1)
            {
                uint nodeCount = U32(), leafCount = U32();
                lt.Nodes = new int[nodeCount, 2];
                for (int i = 0; i < nodeCount; ++i)
                {
                    lt.Nodes[i, 0] = I32(); // child for bit 1
                    lt.Nodes[i, 1] = I32(); // child for bit 0
                }
                lt.Leaves = new uint[leafCount];
                for (int i = 0; i < leafCount; ++i)
                    lt.Leaves[i] = U32();
                lt.HasHuffman = true;
            }
            lt.TailOffset = (int)pos;
            lt.FileLength = d.Length;
            if (lt.IsMission && pos + 8 <= d.Length)
            {
                uint n = U32();
                if (n > 0 && n < 1 << 22)
                {
                    var o = new byte[n * 8];
                    Lzh.LzhDecodeCounted(d.AsSpan((int)pos), o);
                    lt.MissionSource = new uint[n];
                    lt.MissionDest = new uint[n];
                    for (int i = 0; i < n; ++i)
                    {
                        lt.MissionSource[i] = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(i * 8, 4));
                        lt.MissionDest[i] = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(i * 8 + 4, 4));
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

    // 3.6 decode of a compressed map: one bit at a time (LSB first in each byte), from node 0 taking the child for bit 1
    // or bit 0 until a negative child (-(k+1) is leaf k), the leaf's low 16 bits is the pixel.
    public ushort[] DecodeHuffman(int offset, int pixels)
    {
        var r = new ushort[pixels];
        int bit = 0;
        for (int p = 0; p < pixels; ++p)
        {
            int node = 0;
            while (node >= 0)
            {
                int bi = offset + (bit >> 3);
                if (bi >= MapData.Length || node >= Nodes.GetLength(0))
                    return r;
                int b = (MapData[bi] >> (bit & 7)) & 1;
                ++bit;
                node = Nodes[node, b == 1 ? 0 : 1];
            }
            int leaf = -(node + 1);
            r[p] = leaf < Leaves.Length ? (ushort)(Leaves[leaf] & 0xffff) : (ushort)0;
        }
        return r;
    }

    // The pixels (u16 nibbles M R G B) of the base light map of a surface, sizeX * sizeY, or null when the surface has
    // no size. `mission` supplies replacement maps (3.7).
    public ushort[]? SurfaceMap(int surface, InteriorLighting? mission)
    {
        Surface s = Surfaces[surface];
        int px = (s.SizeX) * (s.SizeY);
        if (px <= 0)
            return null;
        if ((s.Word & 0x80000000u) != 0)
        {
            var r = new ushort[px];
            Array.Fill(r, (ushort)(s.Word & 0xffff));
            return r;
        }
        uint index = s.Word & 0xbfffffff;
        bool compressed = (s.Word & 0x40000000u) != 0;
        InteriorLighting src = this;
        long offset = index;
        if (mission != null && mission.TryMissionOffset(index, out uint dest))
        {
            src = mission;
            offset = dest;
        }
        if (compressed)
            return src.DecodeHuffman((int)offset, px);
        var raw = new ushort[px];
        for (int i = 0; i < px; ++i)
        {
            long o = offset + i * 2;
            if (o + 2 > src.MapData.Length)
                break;
            raw[i] = BinaryPrimitives.ReadUInt16LittleEndian(src.MapData.AsSpan((int)o, 2));
        }
        return raw;
    }

    public bool TryMissionOffset(uint sourceIndex, out uint dest)
    {
        int i = Array.BinarySearch(MissionSource, sourceIndex);
        dest = i >= 0 ? MissionDest[i] : 0;
        return i >= 0;
    }

    // 3.7 Timing: the state of a light at `time`: the number of state records after the first whose end time is below the
    // time, limited to stateCount - 1. Random lights (flag 4) are handled by the caller (they pick states privately).
    public static int SelectState(ReadOnlySpan<State> stateRecords, int stateCount, float time)
    {
        // interiors.md 5.4: the state is the count of following records (after the first) whose end time is below the
        // time, at most stateCount - 1.
        int state = 0;
        for (int j = 1; j < stateCount && j < stateRecords.Length; ++j)
            if (stateRecords[j].EndTime < time)
                ++state;
        return Math.Min(state, Math.Max(stateCount - 1, 0));
    }

    // 3.7 Building a surface light map: the colour packed from a state ((word + 0x80) >> 8 per channel, mono red green blue).
    public static (int mono, int r, int g, int b) PackedColor(State s) =>
        ((s.Mono + 0x80) >> 8, (s.R + 0x80) >> 8, (s.G + 0x80) >> 8, (s.B + 0x80) >> 8);

    // 3.7 step 3: for every pixel with intensity i >= 16 each of the four channels gets (colourByte * i) >> 12 added to its
    // nibble, each sum clamped to 15.
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
