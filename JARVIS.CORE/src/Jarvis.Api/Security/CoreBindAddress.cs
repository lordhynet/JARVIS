using System.Net;

namespace Jarvis.Api.Security;

internal static class CoreBindAddress
{
    private static readonly IPNetwork[] PrivateNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("fc00::/7")
    ];

    public static IPAddress Resolve(IConfigurationSection host, bool isLinuxContainer)
    {
        var address = IPAddress.Parse(host["BindAddress"] ?? "127.0.0.1");
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        // Compose publishes the container's IPv4 listener on host loopback.
        if (IPAddress.IsLoopback(normalized) || PrivateNetworks.Any(network => network.Contains(normalized)) ||
            isLinuxContainer && address.Equals(IPAddress.Any) || host.GetValue<bool>("AllowPublicBind"))
            return address;

        throw new InvalidOperationException(
            $"{host.Path}:BindAddress must be a loopback or private address. Set {host.Path}:AllowPublicBind to true to explicitly allow a public or wildcard bind.");
    }
}
