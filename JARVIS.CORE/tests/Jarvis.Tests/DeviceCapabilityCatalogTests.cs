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
        var descriptor = BuiltInDeviceCapabilities.All.Single(x => x.Id == BuiltInDeviceCapabilities.HomeAssistantTurn) with
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
        var invalid = catalog.Get(BuiltInDeviceCapabilities.HomeAssistantTurn, "1.0.0") with { ArgumentSchemaJson = "[]" };
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

        var invalid = catalog.Get(BuiltInDeviceCapabilities.HomeAssistantTurn, "1.0.0") with
        {
            ConstraintSchemaJson = """{"type":"object","properties":{"allow":{"type":"string","pattern":"anything"}},"additionalProperties":false}"""
        };
        Assert.Throws<DomainException>(() => new DeviceCapabilityCatalog([invalid]));
    }

    [Fact]
    public void Home_light_settings_require_valid_bounds_and_one_colour_mode()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        const string id = BuiltInDeviceCapabilities.HomeAssistantLightSet;
        const string allow = """{"allowedEntityIds":["light.kitchen"]}""";
        Assert.False(catalog.HasValidConstraints(id, "1.0.0", """{"allowedEntityIds":["switch.porch"]}"""));
        Assert.True(catalog.Permits(id, "1.0.0", allow,
            """{"entityId":"light.kitchen","brightnessPct":25,"rgbColor":[125,0,255]}"""));
        Assert.False(catalog.Permits(id, "1.0.0", allow, """{"entityId":"light.kitchen"}"""));
        Assert.False(catalog.Permits(id, "1.0.0", allow, """{"entityId":"light.kitchen","brightnessPct":101}"""));
        Assert.False(catalog.Permits(id, "1.0.0", allow, """{"entityId":"light.kitchen","rgbColor":[256,1,2]}"""));
        Assert.False(catalog.Permits(id, "1.0.0", allow, """{"entityId":"light.kitchen","rgbColor":[4,1,2],"colorTempKelvin":2700}"""));
    }

    [Fact]
    public void Home_temperature_constraints_require_ordered_numeric_bounds_and_limit_the_exact_setpoint()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        const string id = BuiltInDeviceCapabilities.HomeAssistantClimateSet;
        const string allow = """{"allowedEntityIds":["climate.hall"],"minC":18,"maxC":22}""";
        Assert.False(catalog.HasValidConstraints(id, "1.0.0", """{"allowedEntityIds":["climate.hall"],"minC":18}"""));
        Assert.False(catalog.HasValidConstraints(id, "1.0.0", """{"allowedEntityIds":["climate.hall"],"minC":22,"maxC":18}"""));
        Assert.True(catalog.Permits(id, "1.0.0", allow, """{"entityId":"climate.hall","temperatureC":21.5}"""));
        Assert.False(catalog.Permits(id, "1.0.0", allow, """{"entityId":"climate.hall","temperatureC":22.01}"""));
        Assert.False(catalog.Permits(id, "1.0.0", allow, """{"entityId":"climate.hall","temperatureC":"21.5"}"""));
    }

    [Fact]
    public void Sensitive_capabilities_require_owner_confirmation_and_explicit_actions()
    {
        var catalog = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        Assert.All(catalog.Installed.Where(x => BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(x.Id)), descriptor =>
        {
            Assert.Equal(DeviceCapabilityRisk.Sensitive, descriptor.Risk);
            Assert.Equal(DeviceConfirmationPolicy.Owner, descriptor.ConfirmationPolicy);
            Assert.Equal(DeviceIdempotencyRule.NeverReplay, descriptor.IdempotencyRule);
        });
        const string lockId = BuiltInDeviceCapabilities.HomeAssistantLockSet;
        const string allow = """{"allowedEntityIds":["lock.front"],"allowedActions":["lock"]}""";
        Assert.True(catalog.Permits(lockId, "1.0.0", allow, """{"entityId":"lock.front","action":"lock"}"""));
        Assert.False(catalog.Permits(lockId, "1.0.0", allow, """{"entityId":"lock.front","action":"unlock"}"""));
        Assert.False(catalog.Permits(lockId, "1.0.0", allow, """{"entityId":"lock.front","action":"lock","code":"secret"}"""));
        Assert.False(catalog.HasValidConstraints(lockId, "1.0.0", """{"allowedEntityIds":["lock.front"],"allowedActions":[]}"""));
        Assert.False(catalog.HasValidConstraints(lockId, "1.0.0", """{"allowedEntityIds":["lock.front"],"allowedActions":["lock","lock"]}"""));
        const string coverId = BuiltInDeviceCapabilities.HomeAssistantCoverSet;
        const string cover = """{"allowedEntityIds":["cover.garage"],"allowedActions":["close_cover","set_cover_position"]}""";
        Assert.True(catalog.Permits(coverId, "1.0.0", cover, """{"entityId":"cover.garage","action":"set_cover_position","position":41}"""));
        Assert.False(catalog.Permits(coverId, "1.0.0", cover, """{"entityId":"cover.garage","action":"set_cover_position"}"""));
        Assert.False(catalog.Permits(coverId, "1.0.0", cover, """{"entityId":"cover.garage","action":"close_cover","position":41}"""));
    }
}
