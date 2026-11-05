using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using Probe = RequestPortal.Tools.WhatsAppCommissioning.Program;

namespace WhatsAppCommissioningProbe.Tests;

public sealed class ConfigurationTests
{
    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Notifications:WhatsAppEnabled"] = "false",
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["Notifications:WhatsAppWebhookEnabled"] = "true",
        ["Notifications:WhatsAppPhoneNumberId"] = "1427917097069650",
        ["Notifications:WhatsAppBusinessAccountId"] = "1823610105620506",
        ["Notifications:WhatsAppAccessTokenSecretName"] = "messaging/whatsapp/api-token",
        ["Notifications:WhatsAppVerificationTokenSecretName"] = "messaging/whatsapp/verification-token",
        ["Notifications:WhatsAppAppSecretName"] = "messaging/whatsapp/app-secret",
        ["CommonSecrets:ProviderOrder:0"] = "OpenBao",
        ["CommonSecrets:Mode"] = "Production",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning"
    };

    [Fact]
    public void ApprovedConfigurationStillPasses() =>
        Probe.ValidateConfiguration(new ConfigurationBuilder().AddInMemoryCollection(ValidSettings()).Build());

    [Theory]
    [InlineData("ASPNETCORE_ENVIRONMENT", null, "AspNetCoreEnvironmentProduction")]
    [InlineData("ASPNETCORE_ENVIRONMENT", "fixture-private", "AspNetCoreEnvironmentProduction")]
    [InlineData("Notifications:WhatsAppEnabled", "true", "WhatsAppOutboundDisabled")]
    [InlineData("Notifications:WhatsAppEnabled", "fixture-private", "WhatsAppOutboundDisabled")]
    [InlineData("Notifications:WhatsAppWebhookEnabled", "false", "WhatsAppWebhookEnabled")]
    [InlineData("Notifications:WhatsAppWebhookEnabled", "fixture-private", "WhatsAppWebhookEnabled")]
    [InlineData("Notifications:WhatsAppPhoneNumberId", "fixture-private", "WhatsAppPhoneNumberId")]
    [InlineData("Notifications:WhatsAppBusinessAccountId", "fixture-private", "WhatsAppBusinessAccountId")]
    [InlineData("Notifications:WhatsAppAccessTokenSecretName", "fixture-private", "WhatsAppAccessTokenReference")]
    [InlineData("Notifications:WhatsAppVerificationTokenSecretName", "fixture-private", "WhatsAppVerificationTokenReference")]
    [InlineData("Notifications:WhatsAppAppSecretName", "fixture-private", "WhatsAppAppSecretReference")]
    [InlineData("CommonSecrets:ProviderOrder:0", null, "OpenBaoProviderOrder")]
    [InlineData("CommonSecrets:ProviderOrder:0", "fixture-private", "OpenBaoProviderOrder")]
    [InlineData("CommonSecrets:Mode", "fixture-private", "CommonSecretsProductionMode")]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "Information", "RequestLoggingSafe")]
    public void FailuresIdentifyOnlyTheExistingRule(string key, string? value, string rule)
    {
        var values = ValidSettings();
        values[key] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = Assert.Throws<Probe.ConfigurationFailure>(() => Probe.ValidateConfiguration(config));
        Assert.Equal(rule, error.Rule.ToString());
        string output = Probe.FailureJson(error, "configuration");
        Assert.DoesNotContain("fixture-private", output);
        Assert.DoesNotContain(key, output);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(rule, document.RootElement.GetProperty("rule").GetString());
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void MalformedConfigurationAndUnexpectedExceptionsNeverExposeRawMessages()
    {
        var directory = Directory.CreateTempSubdirectory("rp-probe-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), "{fixture-private");
            var error = Assert.Throws<Probe.ConfigurationFailure>(() => Probe.LoadConfiguration(directory.FullName));
            Assert.Equal(Probe.ConfigurationRule.ConfigurationLoad, error.Rule);
            Assert.DoesNotContain("fixture-private", Probe.FailureJson(error, "configuration"));
            string output = Probe.FailureJson(new InvalidOperationException("fixture-private"), "configuration");
            Assert.DoesNotContain("fixture-private", output);
            using var document = JsonDocument.Parse(output);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("rule").ValueKind);
        }
        finally { directory.Delete(true); }
    }
}
