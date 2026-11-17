using System.Net;
using Jarvis.Api.Security;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class CoreBindAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("100.88.20.10")]
    [InlineData("fd7a:115c:a1e0::10")]
    [InlineData("192.168.1.2")]
    [InlineData("::ffff:100.88.20.10")]
    public void Native_core_accepts_private_addresses(string address) =>
        Assert.Equal(IPAddress.Parse(address), CoreBindAddress.Resolve(Host(address), isLinuxContainer: false));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("100.128.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Native_core_requires_explicit_permission_for_public_and_wildcard_binds(string address)
    {
        var denied = Assert.Throws<InvalidOperationException>(() => CoreBindAddress.Resolve(Host(address), isLinuxContainer: false));
        Assert.Contains("Server:Windows:AllowPublicBind", denied.Message);
        Assert.Equal(IPAddress.Parse(address), CoreBindAddress.Resolve(Host(address, true), isLinuxContainer: false));
    }

    [Fact]
    public void Linux_container_keeps_the_compose_ipv4_listener()
    {
        Assert.Equal(IPAddress.Any, CoreBindAddress.Resolve(Host("0.0.0.0", container: true), isLinuxContainer: true));
        Assert.Throws<InvalidOperationException>(() => CoreBindAddress.Resolve(Host("8.8.8.8", container: true), isLinuxContainer: true));
        Assert.Throws<InvalidOperationException>(() => CoreBindAddress.Resolve(Host("::", container: true), isLinuxContainer: true));
    }

    private static IConfigurationSection Host(string address, bool allow = false, bool container = false)
    {
        var path = container ? "Server:Container" : "Server:Windows";
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{path}:BindAddress"] = address,
            [$"{path}:AllowPublicBind"] = allow.ToString()
        }).Build().GetSection(path);
    }
}
