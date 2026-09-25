using Common.Diagnostics;
using Common.Secrets;
using RequestPortal.Web.Diagnostics;

namespace RequestPortal.Web.Startup;

internal static class DiagnosticsServiceRegistration
{
    public static void Add(
        WebApplicationBuilder builder
        , string instanceId)
    {
        builder.Services.AddCommonDiagnostics();

        builder.Services.AddSingleton(new DiagnosticLevelExecutionOptions
        {
            Application = "RequestPortal"
            , Component = "RequestPortal.Web"
            , InstanceId = instanceId
            , CatalogVersion = "1"
        });

        builder.Services.AddSingleton(new DiagnosticLevelStoreOptions
        {
            Path = builder.Configuration["Aegis:Diagnostics:StorePath"]?.Trim()
                ?? Path.Combine(
                    builder.Environment.ContentRootPath
                    , "data"
                    , "diagnostic-level"
                    , "requestportal.db")
        });

        builder.Services.AddSingleton<
            IDiagnosticLevelRequestCredentialProvider
            , CommonSecretsDiagnosticLevelRequestCredentialProvider>();

        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , BasicRuntimeIdentityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , BasicControlPlaneConfigurationDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , PostgreSqlConnectivityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , MalwareScannerConnectivityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , ExternalIdentityProvidersDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , OrganizationAdministratorIntegrityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , MembershipStateIntegrityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , RequestRelationshipIntegrityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , DocumentStorageReferenceDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , ExpiredOrganizationRegistrationDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , FulfilmentIntegrityDiagnosticLevelTest>();
        builder.Services.AddSingleton<
            IDiagnosticLevelLocalTest
            , FfpManagerBridgeReadinessDiagnosticLevelTest>();
    }
}
