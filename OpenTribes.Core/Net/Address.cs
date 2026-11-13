namespace OpenTribes.Net;

/// <summary>An IPv4 endpoint in host byte order.</summary>
/// <remarks>Port of src/net/address.hpp.</remarks>
public struct Address : IEquatable<Address>, IComparable<Address>
{
    /// <summary>The IPv4 address in host byte order.</summary>
    public uint Ip;
    /// <summary>The UDP port.</summary>
    public ushort Port;

    /// <summary>Creates an endpoint.</summary>
    /// <param name="ip">The IPv4 address in host byte order.</param>
    /// <param name="port">The UDP port.</param>
    public Address(uint ip, ushort port)
    {
        Ip = ip;
        Port = port;
    }

    /// <summary>Compares address and port.</summary>
    /// <param name="o">The other endpoint.</param>
    /// <returns>True when both are equal.</returns>
    public readonly bool Equals(Address o) => Ip == o.Ip && Port == o.Port;
    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Address o && Equals(o);
    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(Ip, Port);
    /// <summary>Tests two endpoints for equality.</summary>
    /// <param name="a">The first endpoint.</param>
    /// <param name="b">The second endpoint.</param>
    /// <returns>True when equal.</returns>
    public static bool operator ==(Address a, Address b) => a.Equals(b);
    /// <summary>Tests two endpoints for inequality.</summary>
    /// <param name="a">The first endpoint.</param>
    /// <param name="b">The second endpoint.</param>
    /// <returns>True when different.</returns>
    public static bool operator !=(Address a, Address b) => !a.Equals(b);

    /// <summary>Orders by address, then port.</summary>
    /// <param name="o">The other endpoint.</param>
    /// <returns>A negative, zero or positive value.</returns>
    public readonly int CompareTo(Address o) => Ip != o.Ip ? Ip.CompareTo(o.Ip) : Port.CompareTo(o.Port);
    /// <summary>Tests whether the first endpoint orders before the second.</summary>
    /// <param name="a">The first endpoint.</param>
    /// <param name="b">The second endpoint.</param>
    /// <returns>True when <paramref name="a"/> is less.</returns>
    public static bool operator <(Address a, Address b) => a.CompareTo(b) < 0;
    /// <summary>Tests whether the first endpoint orders after the second.</summary>
    /// <param name="a">The first endpoint.</param>
    /// <param name="b">The second endpoint.</param>
    /// <returns>True when <paramref name="a"/> is greater.</returns>
    public static bool operator >(Address a, Address b) => a.CompareTo(b) > 0;

    /// <summary>Formats the endpoint in the original format <c>IP:%d.%d.%d.%d:%d</c>.</summary>
    /// <returns>The text.</returns>
    public override readonly string ToString() =>
        "IP:" + (Ip >> 24) + "." + ((Ip >> 16) & 0xff) + "." + ((Ip >> 8) & 0xff) + "." + (Ip & 0xff) + ":" + Port;
}
