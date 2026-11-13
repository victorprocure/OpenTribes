namespace OpenTribes.Net;

/// <summary>The Huffman code of one symbol.</summary>
public readonly struct HuffmanCode
{
    /// <summary>The code bits, LSB first. Bit i is the i-th bit written.</summary>
    public readonly ulong Bits;   // LSB first, bit i is the i-th bit written
    /// <summary>The code length in bits.</summary>
    public readonly byte Length;

    /// <summary>Creates a code.</summary>
    /// <param name="bits">The code bits, LSB first.</param>
    /// <param name="length">The code length in bits.</param>
    public HuffmanCode(ulong bits, byte length)
    {
        Bits = bits;
        Length = length;
    }
}

/// <summary>The string Huffman coder used by <see cref="BitStream"/> strings.</summary>
/// <remarks>Port of src/net/huffman.cpp. The tree is built from the fixed character frequencies once at start-up.</remarks>
public static class Huffman
{
    // src/net/huffman.hpp detail::HuffNode.
    private struct HuffNode
    {
        public short Child0; // < 0 encodes leaf -(index + 1)
        public short Child1;
    }

    // src/net/huffman.cpp kCharFreqs: character frequencies. Identical to csm_charFreqs in Torque3D
    // (Engine/source/core/stream/bitStream.cpp), Copyright (c) 2012 GarageGames, LLC, MIT license.
    private static readonly uint[] kCharFreqs =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 329, 21, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2809, 68, 0, 27, 0, 58, 3, 62, 4, 7, 0, 0, 15, 65, 554, 3,
        394, 404, 189, 117, 30, 51, 27, 15, 34, 32, 80, 1, 142, 3, 142, 39,
        0, 144, 125, 44, 122, 275, 70, 135, 61, 127, 8, 12, 113, 246, 122, 36,
        185, 1, 149, 309, 335, 12, 11, 14, 54, 151, 0, 0, 2, 0, 0, 211,
        0, 2090, 344, 736, 993, 2872, 701, 605, 646, 1552, 328, 305, 1240, 735, 1533, 1713,
        562, 3, 1775, 1149, 1469, 979, 407, 553, 59, 279, 31, 0, 0, 0, 68, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    // src/net/huffman.cpp isAsciiAlnum.
    private static bool IsAsciiAlnum(int c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    // src/net/huffman.cpp struct Tables.
    private sealed class Tables
    {
        public readonly HuffNode[] Nodes = new HuffNode[256];
        public readonly global::OpenTribes.Net.HuffmanCode[] Codes = new global::OpenTribes.Net.HuffmanCode[256];

        // A wrap is either a leaf (< 0, encoded like a child index) or a node index.
        private struct Wrap
        {
            public short Index;
            public uint Weight;
        }

        // src/net/huffman.cpp Tables::Tables.
        public Tables()
        {
            var wraps = new Wrap[256];
            for (int i = 0; i < 256; ++i)
                // Tribes adds 1 for alphanumerics on top of Torque's +1.
                wraps[i] = new Wrap { Index = (short)-(i + 1), Weight = kCharFreqs[i] + 1 + (IsAsciiAlnum(i) ? 1u : 0u) };

            int nodeCount = 1; // slot 0 is reserved for the root
            int count = 256;
            while (count != 1)
            {
                uint min1 = 0xfffffffe, min2 = 0xffffffff;
                int index1 = -1, index2 = -1;
                for (int i = 0; i < count; ++i)
                {
                    if (wraps[i].Weight < min1)
                    {
                        min2 = min1;
                        index2 = index1;
                        min1 = wraps[i].Weight;
                        index1 = i;
                    }
                    else if (wraps[i].Weight < min2)
                    {
                        min2 = wraps[i].Weight;
                        index2 = i;
                    }
                }
                int node = nodeCount++;
                Nodes[node] = new HuffNode { Child0 = wraps[index1].Index, Child1 = wraps[index2].Index };
                uint weight = wraps[index1].Weight + wraps[index2].Weight;

                int merge = index1 < index2 ? index1 : index2;
                int nuke = index1 < index2 ? index2 : index1;
                wraps[merge] = new Wrap { Index = (short)node, Weight = weight };
                if (index2 != count - 1)
                    wraps[nuke] = wraps[count - 1];
                --count;
            }
            Nodes[0] = Nodes[wraps[0].Index];
            Assign(0, 0, 0);
        }

        // src/net/huffman.cpp Tables::assign.
        private void Assign(int index, ulong bits, int depth)
        {
            if (index < 0)
            {
                Codes[-(index + 1)] = new global::OpenTribes.Net.HuffmanCode(bits, (byte)depth);
                return;
            }
            Assign(Nodes[index].Child0, bits, depth + 1);
            Assign(Nodes[index].Child1, bits | (1UL << depth), depth + 1);
        }
    }

    private static readonly Tables sTables = new();

    /// <summary>Gets the code of a symbol.</summary>
    /// <param name="symbol">The byte value.</param>
    /// <returns>The code.</returns>
    public static global::OpenTribes.Net.HuffmanCode HuffmanCode(byte symbol) => sTables.Codes[symbol];

    /// <summary>Decodes one symbol by walking the tree with bits from <paramref name="readBit"/> until a leaf.</summary>
    /// <param name="readBit">Supplies the next bit.</param>
    /// <returns>The symbol.</returns>
    public static byte HuffmanDecode(Func<bool> readBit)
    {
        HuffNode[] nodes = sTables.Nodes;
        int index = 0;
        while (index >= 0)
            index = readBit() ? nodes[index].Child1 : nodes[index].Child0;
        return (byte)-(index + 1);
    }
}
