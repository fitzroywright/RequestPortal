using Common.Diagnostics;
using Common.Secrets;
using Common.Storage;
using RequestPortal.Common.Configuration;
using RequestPortal.Web.Configuration;
using RequestPortal.Web.Startup;

WebApplicationBuilder builder =
    WebApplication.CreateBuilder(args);

string operationsLogInstanceId =
    OperationsServiceRegistration.Add(builder);

await using CommonSecretsBootstrapRuntime secretsRuntime =
    CommonSecretsBootstrapRuntime.Create(
        builder.Configuration);

secretsRuntime.Register(builder.Services);

RuntimeSecrets runtimeSecrets =
    await ProductionCommissioning.LoadRuntimeSecretsAsync(
        secretsRuntime.Provider
        , builder.Configuration);

DiagnosticsServiceRegistration.Add(
    builder
    , operationsLogInstanceId);

bool registrationSucceeded =
    await ConfigurationContractPublisher.PublishBootstrapAsync(
        builder.Configuration
        , builder.Environment
        , secretsRuntime.Provider
        , CancellationToken.None);

if (!registrationSucceeded)
{
    Console.Error.WriteLine(
        "RP-CONFIG-REG-001: RequestPortal could not publish its commissioning state to Aegis.Configuration before startup validation completed.");
}

if (builder.Environment.IsProduction())
{
    IReadOnlyList<ProductionConfigurationFailure> failures =
        ProductionCommissioning.Validate(
            builder.Configuration
            , runtimeSecrets);

    if (failures.Count > 0)
    {
        throw new InvalidOperationException(
            ProductionCommissioning.BuildFailureMessage(
                failures));
    }
}

string connectionString =
    runtimeSecrets.ConnectionString
    ?? throw new InvalidOperationException(
        "ConnectionStrings:RequestPortal is required through Common.Secrets.");

string storageProvider = builder.Configuration["Storage:Provider"]?.Trim() ?? "LocalFolder";
if (string.Equals(storageProvider, "SharePoint", StringComparison.OrdinalIgnoreCase))
{
    StorageTargetOptions storageTarget = StorageTargetOptions.FromConfiguration(
        builder.Configuration,
        runtimeSecrets.StorageSharePointClientSecret);
    builder.Services.AddCommonStorage(storageTarget);
}
else
{
    StorageTargetOptions storageTarget = StorageTargetOptions.FromConfiguration(builder.Configuration);
    if (storageTarget.Kind is StorageTargetKind.LocalFolder or StorageTargetKind.NetworkFolder &&
        !Path.IsPathRooted(storageTarget.RootPath))
    {
        storageTarget = storageTarget with
        {
            RootPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, storageTarget.RootPath!))
        };
    }
    builder.Services.AddCommonStorage(storageTarget);
}

PlatformServiceRegistration.AddPersistence(
    builder
    , connectionString);

AuthenticationServiceRegistration.Add(
    builder
    , runtimeSecrets);

PlatformServiceRegistration.AddPlatform(
    builder
    , secretsRuntime
    , runtimeSecrets);

MessagingServiceRegistration.Add(builder);
ApplicationServiceRegistration.Add(builder);
AuthorizationServiceRegistration.Add(builder);

WebApplication app = builder.Build();

app.MapDiagnosticLevelRuntime();

await StartupTasks.ApplyDatabaseMigrationsAsync(app);

ApplicationPipeline.Configure(app);
EndpointRegistration.Map(app);

await StartupTasks.CompleteAsync(app);
await app.RunAsync();

public partial class Program
{
}
