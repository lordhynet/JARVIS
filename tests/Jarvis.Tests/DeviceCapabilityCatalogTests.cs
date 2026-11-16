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
        Assert.Contains("allowedEntityIds", descriptor.ConstraintSchemaJson);
    }

    [Fact]
    public void Sensitive_actions_cannot_be_installed_without_owner_confirmation()
    {
        var descriptor = BuiltInDeviceCapabilities.All.Single() with
        {
            Risk = DeviceCapabilityRisk.Sensitive,
            ConfirmationPolicy = DeviceConfirmationPolicy.None
        };
        Assert.Throws<DomainException>(() => new DeviceCapabilityCatalog([descriptor]));
    }

    [Fact]
    public void Unknown_versions_and_invalid_schemas_are_refused()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        Assert.Throws<DomainException>(() => catalog.Get("homeassistant.turn", "2.0.0"));
        var invalid = catalog.Installed.Single() with { ArgumentSchemaJson = "[]" };
        Assert.Throws<DomainException>(() => new DeviceCapabilityCatalog([invalid]));
    }

    [Fact]
    public void Home_assistant_permission_checks_the_exact_entity_and_a_supported_action()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        const string allowed = """{"allowedEntityIds":["light.kitchen","switch.porch"]}""";
        Assert.True(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"light.kitchen","action":"turn_on"}"""));
        Assert.True(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"switch.porch","action":"turn_off"}"""));
        Assert.False(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"light.bedroom","action":"turn_on"}"""));
        Assert.False(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"light.kitchen","action":"toggle"}"""));
        Assert.False(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"light.kitchen","action":"turn_on","other":true}"""));
        Assert.False(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"light.kitchen","entityId":"light.bedroom","action":"turn_on"}"""));
        Assert.False(catalog.Permits("homeassistant.turn", "1.0.0", allowed, """{"entityId":"light.kitchen"}"""));
        Assert.False(catalog.Permits("homeassistant.turn", "2.0.0", allowed, """{"entityId":"light.kitchen","action":"turn_on"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"allowedEntityIds":[]}""")]
    [InlineData("""{"allowedEntityIds":"light.kitchen"}""")]
    [InlineData("""{"allowedEntityIds":[false]}""")]
    [InlineData("""{"allowedEntityIds":["light.kitchen","light.kitchen"]}""")]
    [InlineData("""{"allowedEntityIds":["light.kitchen"],"allowedEntityIds":["light.kitchen"]}""")]
    [InlineData("""{"allowedEntityIds":["light.kitchen"],"unknown":true}""")]
    [InlineData("""{"allowedEntityIds":["cover.garage"]}""")]
    public void Missing_or_invalid_constraints_deny_actions(string? constraints)
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        Assert.False(catalog.HasValidConstraints("homeassistant.turn", "1.0.0", constraints));
        Assert.False(catalog.Permits("homeassistant.turn", "1.0.0", constraints, """{"entityId":"light.kitchen","action":"turn_on"}"""));
    }

    [Fact]
    public void Constraints_limit_entity_count_and_reject_descriptors_with_unsupported_schema_keywords()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        var ids = Enumerable.Range(0, 51).Select(index => "light.fixture_" + index).ToArray();
        Assert.True(catalog.HasValidConstraints("homeassistant.turn", "1.0.0",
            System.Text.Json.JsonSerializer.Serialize(new { allowedEntityIds = ids[..50] })));
        Assert.False(catalog.HasValidConstraints("homeassistant.turn", "1.0.0",
            System.Text.Json.JsonSerializer.Serialize(new { allowedEntityIds = ids })));

        var invalid = catalog.Installed.Single() with
        {
            ConstraintSchemaJson = """{"type":"object","properties":{"allow":{"type":"string","pattern":"anything"}},"additionalProperties":false}"""
        };
        Assert.Throws<DomainException>(() => new DeviceCapabilityCatalog([invalid]));
    }
}
