using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class DeviceCapabilityCatalogTests
{
    [Fact]
    public void Installed_descriptors_are_versioned_validated_and_read_only()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        var descriptor = catalog.Get("homeassistant.turn", "1.0.0");
        Assert.Equal(DeviceCapabilityRisk.Reversible, descriptor.Risk);
        Assert.Equal(DeviceConfirmationPolicy.Owner, descriptor.ConfirmationPolicy);
        Assert.Contains("additionalProperties", descriptor.ArgumentSchemaJson);
    }

    [Fact]
    public void Sensitive_actions_cannot_be_installed_without_owner_confirmation()
    {
        var descriptor = new DeviceCapabilityDescriptor("test.sensitive", "1.0.0", "Sensitive", "Fixture",
            DeviceCapabilityRisk.Sensitive, DeviceConfirmationPolicy.None, DeviceIdempotencyRule.CallerKey,
            "{\"type\":\"object\"}", "{\"type\":\"object\"}");
        Assert.Throws<DomainException>(() => new DeviceCapabilityCatalog([descriptor]));
    }

    [Fact]
    public void Unknown_versions_and_invalid_schemas_are_refused()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        Assert.Throws<DomainException>(() => catalog.Get("homeassistant.turn", "2.0.0"));
        var invalid = new DeviceCapabilityDescriptor("test.action", "1.0.0", "Test", "Fixture",
            DeviceCapabilityRisk.ReadOnly, DeviceConfirmationPolicy.None, DeviceIdempotencyRule.SafeReplay,
            "[]", "{\"type\":\"object\"}");
        Assert.Throws<DomainException>(() => new DeviceCapabilityCatalog([invalid]));
    }
}
