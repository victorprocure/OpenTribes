// Port of src/net/address.hpp: IPv4 endpoint in host byte order.
namespace OpenTribes.Net;

public struct Address : IEquatable<Address>, IComparable<Address>
{
    public uint Ip;
    public ushort Port;

    public Address(uint ip, ushort port)
    {
        Ip = ip;
        Port = port;
    }

    // src/net/address.hpp operator==.
    public readonly bool Equals(Address o) => Ip == o.Ip && Port == o.Port;
    public override readonly bool Equals(object? obj) => obj is Address o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(Ip, Port);
    public static bool operator ==(Address a, Address b) => a.Equals(b);
    public static bool operator !=(Address a, Address b) => !a.Equals(b);

    // src/net/address.hpp operator<.
    public readonly int CompareTo(Address o) => Ip != o.Ip ? Ip.CompareTo(o.Ip) : Port.CompareTo(o.Port);
    public static bool operator <(Address a, Address b) => a.CompareTo(b) < 0;
    public static bool operator >(Address a, Address b) => a.CompareTo(b) > 0;

    // src/net/address.hpp toString: the original format "IP:%d.%d.%d.%d:%d".
    public override readonly string ToString() =>
        "IP:" + (Ip >> 24) + "." + ((Ip >> 16) & 0xff) + "." + ((Ip >> 8) & 0xff) + "." + (Ip & 0xff) + ":" + Port;
}
