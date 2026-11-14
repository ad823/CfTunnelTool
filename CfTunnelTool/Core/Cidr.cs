using System.Net;
using System.Net.Sockets;

namespace CfTunnelTool.Core;

/// <summary>IPv4 CIDR 網段（例如 192.168.0.0/16）。</summary>
readonly record struct Cidr(uint Network, int Prefix)
{
    static uint Mask(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    public static bool TryParse(string text, out Cidr cidr)
    {
        cidr = default;
        var parts = text.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)) return false;
        if (parts.Length > 2) return false;
        cidr = new Cidr(ToUInt(ip) & Mask(prefix), prefix);
        return true;
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static string ToIp(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    public bool Contains(uint ip) => (ip & Mask(Prefix)) == Network;

    public bool Contains(Cidr other) => other.Prefix >= Prefix && Contains(other.Network);

    public override string ToString() => $"{ToIp(Network)}/{Prefix}";

    /// <summary>
    /// 從這個網段扣掉 <paramref name="hole"/>（必須被包含在內），回傳剩下的網段。
    /// 例如 192.168.0.0/16 扣掉 192.168.5.200/32，會得到 16 個網段。
    /// </summary>
    public List<Cidr> Subtract(Cidr hole)
    {
        if (!Contains(hole)) return [this];
        var result = new List<Cidr>();
        for (var len = Prefix + 1; len <= hole.Prefix; len++)
        {
            // hole 在這一層的「兄弟」網段：把第 len 位元反轉
            var bit = 1u << (32 - len);
            var sibling = (hole.Network ^ bit) & Mask(len);
            result.Add(new Cidr(sibling, len));
        }
        return result;
    }
}
