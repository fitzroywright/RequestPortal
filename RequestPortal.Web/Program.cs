using Microsoft.EntityFrameworkCore;
using Common.Diagnostics;
using Common.Secrets;
using RequestPortal.Common.Configuration;
using RequestPortal.Web.Configuration;
using RequestPortal.Web.Startup;

WebApplicationBuilder builder =
    WebApplication.CreateBuilder(args.FirstOrDefault() == "provision-staff" ? [] : args);

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

if (args.Length > 0 && args[0] == "provision-staff")
{
    if (args.Length != 7) throw new ArgumentException("provision-staff ACCOUNT_ID PROVIDER SCOPE SUBJECT AUTHORITY REASON requires six explicit values.");
    string protectedConnection = runtimeSecrets.ConnectionString ?? throw new InvalidOperationException("Protected PostgreSQL configuration is required.");
    using var db = new RequestPortal.Infrastructure.Persistence.RequestPortalDbContext(
        new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>().UseNpgsql(protectedConnection).Options);
    var accounts = new RequestPortal.Web.Security.RequestPortalAccounts(protectedConnection); accounts.EnsureSchema();
    var roleStore = new RequestPortal.Infrastructure.Security.RequestPortalAuthorizationStore(db, Microsoft.Extensions.Options.Options.Create(new RequestPortal.Infrastructure.Security.ActiveDirectoryOptions()));
    var service = new RequestPortal.Web.Security.StaffAdministrationService(accounts,db,roleStore,new global::Common.Security.Authorization.PermissionAuthorizer(roleStore));
    await service.BootstrapAsync(args[1],args[2],args[3],args[4],args[5],args[6]);
    Console.WriteLine("Administrator provisioning persistence and security.manage permission verified.");
    return;
}

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
app.Services.GetRequiredService<RequestPortal.Web.Security.RequestPortalAccounts>().EnsureSchema();

await using (var administrationScope = app.Services.CreateAsyncScope())
{
    var administration = administrationScope.ServiceProvider.GetRequiredService<RequestPortal.Web.Security.StaffAdministrationService>();
    if (!await administration.HasAdministratorAsync())
        app.Logger.LogWarning("RP-STAFF-BOOTSTRAP-001: No active staff Administrator exists. Use the protected offline provision-staff command with an explicitly verified immutable staff identity. See docs/STAFF-ADMINISTRATION.md.");
}

ApplicationPipeline.Configure(app);
EndpointRegistration.Map(app);

await StartupTasks.CompleteAsync(app);
await app.RunAsync();

public partial class Program
{
}
