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
        ["CommonSecrets:Providers:OpenBao:Type"] = "OpenBao",
        ["CommonSecrets:Providers:OpenBao:Settings:Enabled"] = "true",
        ["CommonSecrets:Providers:OpenBao:Settings:RequireHttps"] = "true",
        ["CommonSecrets:Providers:OpenBao:Settings:Address"] = "https://bao.example.test",
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

    [Theory]
    [InlineData("OpenBao")]
    [InlineData("PrimarySecrets")]
    [InlineData(" primarysecrets ")]
    public async Task NamedOpenBaoInstanceUsesEffectiveOrderAndBootstrap(string instance)
    {
        var values = ValidSettings();
        var overrides = new Dictionary<string, string?> {
            ["CommonSecrets:ProviderOrder:0"] = instance,
            ["CommonSecrets:Providers:PrimarySecrets:Type"] = "OpenBao",
            ["CommonSecrets:Providers:PrimarySecrets:Settings:Enabled"] = "true",
            ["CommonSecrets:Providers:PrimarySecrets:Settings:RequireHttps"] = "true",
            ["CommonSecrets:Providers:PrimarySecrets:Settings:Address"] = "https://primary.example.test"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).AddInMemoryCollection(overrides).Build();
        Probe.ValidateConfiguration(config);
        var definition = Probe.ResolveOpenBaoProvider(config);
        Assert.Equal(instance.Trim(), definition.Name, ignoreCase: true);
        await using var runtime = Common.Secrets.CommonSecretsBootstrapRuntime.Create(config);
        var health = Assert.Single(runtime.ProviderHealth);
        Assert.Equal(definition.Name, health.ProviderName, ignoreCase: true);
        Assert.True(health.IsEnabled);
    }

    [Theory]
    [InlineData("CommonSecrets:Providers:OpenBao:Type", null)]
    [InlineData("CommonSecrets:Providers:OpenBao:Type", "Environment")]
    [InlineData("CommonSecrets:Providers:OpenBao:Type", "Configuration")]
    [InlineData("CommonSecrets:ProviderOrder:0", "Missing")]
    [InlineData("CommonSecrets:ProviderOrder:0", " ")]
    [InlineData("CommonSecrets:ProviderOrder:1", "OpenBao")]
    [InlineData("CommonSecrets:ProviderOrder:1", "Fallback")]
    [InlineData("CommonSecrets:ProviderOrder:1", "SecondBao")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:Enabled", "false")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:Enabled", "fixture-private")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:RequireHttps", "false")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:Address", "http://bao.example.test")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:Address", "fixture-private")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:Address", null)]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:ClearSecretIdEnvironmentVariableAfterRead", "false")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:RenewTokens", "false")]
    [InlineData("CommonSecrets:Providers:OpenBao:Settings:RevokeTokenOnDispose", "false")]
    public void InvalidOrInsecureProviderFailsClosed(string key, string? value)
    {
        var values = ValidSettings();
        values[key] = value;
        values["CommonSecrets:Providers:Fallback:Type"] = "Environment";
        values["CommonSecrets:Providers:SecondBao:Type"] = "OpenBao";
        values["CommonSecrets:Providers:SecondBao:Settings:Enabled"] = "true";
        values["CommonSecrets:Providers:SecondBao:Settings:Address"] = "https://second.example.test";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = Assert.Throws<Probe.ConfigurationFailure>(() => Probe.ValidateConfiguration(config));
        Assert.Equal(Probe.ConfigurationRule.OpenBaoProviderOrder, error.Rule);
        Assert.DoesNotContain("fixture-private", Probe.FailureJson(error, "configuration"));
    }

    [Fact]
    public void MissingProviderDefinitionsFailClosed()
    {
        var values = ValidSettings().Where(x => !x.Key.StartsWith("CommonSecrets:Providers:"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = Assert.Throws<Probe.ConfigurationFailure>(() => Probe.ValidateConfiguration(config));
        Assert.Equal(Probe.ConfigurationRule.OpenBaoProviderOrder, error.Rule);
    }

    [Fact]
    public void EnvironmentSelectsNamedInstanceAndPreservesItsSecretIdFile()
    {
        var directory = Directory.CreateTempSubdirectory("rp-provider-order-");
        var env = new Dictionary<string, string?> {
            ["CommonSecrets__ProviderOrder__0"] = "PrimarySecrets",
            ["CommonSecrets__Providers__PrimarySecrets__Type"] = "OpenBao",
            ["CommonSecrets__Providers__PrimarySecrets__Settings__Enabled"] = "true",
            ["CommonSecrets__Providers__PrimarySecrets__Settings__RequireHttps"] = "true",
            ["CommonSecrets__Providers__PrimarySecrets__Settings__Address"] = "https://primary.example.test",
            ["CommonSecrets__Providers__PrimarySecrets__Settings__DeleteSecretIdFileAfterRead"] = "true"
        };
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.Production.json"),
                """{"CommonSecrets":{"Mode":"Production","ProviderOrder":["OpenBao"],"Providers":{"OpenBao":{"Type":"OpenBao"}}}}""");
            var original = env.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var item in env) Environment.SetEnvironmentVariable(item.Key, item.Value);
                var loaded = Probe.LoadConfiguration(directory.FullName);
                Assert.Equal("PrimarySecrets", Probe.ResolveOpenBaoProvider(loaded).Name);
                Assert.False(loaded.GetValue<bool>("CommonSecrets:Providers:PrimarySecrets:Settings:DeleteSecretIdFileAfterRead"));
            }
            finally { foreach (var item in original) Environment.SetEnvironmentVariable(item.Key, item.Value); }
        }
        finally
        {
            directory.Delete(true);
        }
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
