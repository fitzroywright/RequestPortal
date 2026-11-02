using Common.Secrets;
using System.Net;
using System.Net.Http.Json;
using FfpManager.Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class BridgeStartupConfigurationTests
{
    // Synthetic test data, never a production connection or credential.
    private const string TestConnection =
        "Server=JMDB01;Database=FFP;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

    [Fact]
    public async Task StartupResolvesOpenBaoKvV2PathIntoBridgeSqlConfiguration()
    {
        ConfigurationManager configuration = CreateConfiguration();
        using KvHandler handler = new();
        using HttpClient client = new(handler);
        using OpenBaoSecretProvider provider = new(new OpenBaoOptions
        {
            Enabled = true,
            Address = "https://openbao.test.invalid",
            MountPath = "secret",
            BasePath = "aegis/ffpmanager-bridge"
        }, client, new TestAuthenticator());

        await BridgeStartupConfiguration.ResolveAndValidateAsync(
            configuration, provider, isProduction: true, TestContext.Current.CancellationToken);

        Assert.Contains("/v1/secret/data/aegis/ffpmanager-bridge/FfpManagerBridge/SqlConnectionString", handler.Paths);
        Assert.Equal(TestConnection, configuration["Bridge:SqlConnectionString"]);
        FfpSqlConnection.ValidateTarget(configuration["Bridge:SqlConnectionString"]!);
    }

    [Fact]
    public async Task StartupResolvesCommonSecretsBeforeSqlValidationAndOptionsBinding()
    {
        ConfigurationManager configuration = CreateConfiguration();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CommonSecrets:Mode"] = "Test",
            ["CommonSecrets:Providers:TestSecrets:Type"] = "Configuration",
            ["FfpManagerBridge:SqlConnectionString"] = TestConnection,
            ["FfpManagerIntegration:SharedSecret"] = "synthetic-test-value"
        });
        await using CommonSecretsBootstrapRuntime runtime = CommonSecretsBootstrapRuntime.Create(configuration);
        ServiceCollection services = new();
        runtime.Register(services);

        Assert.Null(configuration["Bridge:SqlConnectionString"]);
        await BridgeStartupConfiguration.ResolveAndValidateAsync(
            configuration, runtime.Provider, isProduction: true, TestContext.Current.CancellationToken);

        services.Configure<BridgeOptions>(configuration.GetSection("Bridge"));
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        BridgeOptions options = serviceProvider.GetRequiredService<IOptions<BridgeOptions>>().Value;
        Assert.Same(runtime.Provider, serviceProvider.GetRequiredService<ISecretProvider>());
        Assert.Equal(TestConnection, options.SqlConnectionString);
        FfpSqlConnection.ValidateTarget(options.SqlConnectionString);
        Assert.Equal("Pending_Investigation", options.InitialApplicationStatus);
    }

    [Fact]
    public async Task StartupAwaitsSecretRetrievalBeforeValidatingSqlConfiguration()
    {
        ConfigurationManager configuration = CreateConfiguration();
        TaskCompletionSource<string?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DeferredProvider provider = new(pending.Task);

        Task startup = BridgeStartupConfiguration.ResolveAndValidateAsync(
            configuration, provider, isProduction: true, TestContext.Current.CancellationToken);

        Assert.False(startup.IsCompleted);
        Assert.Contains("FfpManagerBridge:SqlConnectionString", provider.Requests);
        Assert.Null(configuration["Bridge:SqlConnectionString"]);
        pending.SetResult(TestConnection);
        await startup;
        Assert.Equal(TestConnection, configuration["Bridge:SqlConnectionString"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ProductionMissingSecretRejectsPlaintextFallbackWithoutLeakingValues(string? missing)
    {
        ConfigurationManager configuration = CreateConfiguration();
        configuration["Bridge:SqlConnectionString"] = "synthetic-value-that-must-not-be-used-or-logged";
        DeferredProvider provider = new(Task.FromResult(missing));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BridgeStartupConfiguration.ResolveAndValidateAsync(
                configuration, provider, isProduction: true, TestContext.Current.CancellationToken));

        Assert.Contains("Common.Secrets did not resolve", error.Message);
        Assert.DoesNotContain("synthetic-value", error.ToString());
        Assert.Equal(string.Empty, configuration["Bridge:SqlConnectionString"]);
    }

    [Fact]
    public async Task StartupUsesConfiguredSecretName()
    {
        ConfigurationManager configuration = CreateConfiguration();
        configuration["Bridge:SqlConnectionStringSecretName"] = " CustomBridge:SqlConnectionString ";
        DeferredProvider provider = new(Task.FromResult<string?>(TestConnection));
        await BridgeStartupConfiguration.ResolveAndValidateAsync(
            configuration, provider, isProduction: true, TestContext.Current.CancellationToken);
        Assert.Contains("CustomBridge:SqlConnectionString", provider.Requests);
    }

    [Fact]
    public async Task ProductionTargetRejectsRehearsalSecretDuringStartup()
    {
        var configuration = CreateConfiguration();
        configuration["Bridge:SqlTarget"] = "Production";
        var provider = new DeferredProvider(Task.FromResult<string?>(TestConnection));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BridgeStartupConfiguration.ResolveAndValidateAsync(configuration, provider, true, TestContext.Current.CancellationToken));
        Assert.Contains("JMDB01/FFPJM", error.Message);
        Assert.DoesNotContain("Integrated Security", error.Message);
    }

    [Fact]
    public async Task ProductionTargetAcceptsOnlyProtectedProductionConnection()
    {
        var configuration = CreateConfiguration();
        configuration["Bridge:SqlTarget"] = "Production";
        var provider = new DeferredProvider(Task.FromResult<string?>(TestConnection.Replace("Database=FFP;", "Database=FFPJM;")));
        await BridgeStartupConfiguration.ResolveAndValidateAsync(configuration, provider, true, TestContext.Current.CancellationToken);
        FfpSqlConnection.ValidateTarget(configuration["Bridge:SqlConnectionString"]!, "Production");
    }

    private static ConfigurationManager CreateConfiguration()
    {
        ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Same normalized keys produced by Bridge__... environment settings.
            ["Bridge:Gateway"] = "Sql",
            ["Bridge:SqlTarget"] = "Rehearsal",
            ["Bridge:SqlConnectionStringSecretName"] = "FfpManagerBridge:SqlConnectionString",
            ["Bridge:SupportingDocumentRoot"] = "/synthetic-test-documents",
            ["Bridge:InitialApplicationStatus"] = "Pending_Investigation"
        });
        return configuration;
    }

    private sealed class DeferredProvider(Task<string?> sqlConnection) : SecretProviderBase
    {
        public List<string> Requests { get; } = [];

        public override Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            Requests.Add(name);
            return name == "FfpManagerIntegration:SharedSecret"
                ? Task.FromResult<string?>("synthetic-test-value")
                : sqlConnection.WaitAsync(cancellationToken);
        }
    }

    private sealed class TestAuthenticator : IOpenBaoAuthenticator
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-test-token");
    }

    private sealed class KvHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            string value = path.EndsWith("/FfpManagerBridge/SqlConnectionString", StringComparison.Ordinal)
                ? TestConnection : "synthetic-test-value";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new { data = new { value } } })
            });
        }
    }
}
