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
        AddCore(
            builder
            , instanceId);
        AddLevel5(builder);
        AddLevel4(builder);
        AddLevel3(builder);
        AddLevel2(builder);
    }

    private static void AddCore(
        WebApplicationBuilder builder
        , string instanceId)
    {
        builder.Services.AddCommonDiagnostics();

        builder.Services.AddSingleton(
            new DiagnosticLevelExecutionOptions
            {
                Application = "RequestPortal"
                , Component = "RequestPortal.Web"
                , InstanceId = instanceId
                , CatalogVersion = "1"
            });

        builder.Services.AddSingleton(
            new DiagnosticLevelStoreOptions
            {
                Path =
                    builder.Configuration[
                        "Aegis:Diagnostics:StorePath"]?.Trim()
                    ?? Path.Combine(
                        builder.Environment.ContentRootPath
                        , "data"
                        , "diagnostic-level"
                        , "requestportal.db")
            });

        builder.Services.AddSingleton<
            IDiagnosticLevelRequestCredentialProvider
            , CommonSecretsDiagnosticLevelRequestCredentialProvider>();
    }

    private static void AddLevel5(
        WebApplicationBuilder builder)
    {
        builder.Services.AddDiagnosticLevelTest<BasicRuntimeIdentityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<BasicControlPlaneConfigurationDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<PostgreSqlConnectivityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<MalwareScannerConnectivityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<ExternalIdentityProvidersDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<ExternalIdentityProviderSecretsDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<RequiredSecretsDiagnosticLevelTest>();
    }

    private static void AddLevel4(
        WebApplicationBuilder builder)
    {
        builder.Services.AddDiagnosticLevelTest<OrganizationAdministratorIntegrityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<MembershipStateIntegrityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<RequestRelationshipIntegrityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<DocumentStorageReferenceDiagnosticLevelTest>();
    }

    private static void AddLevel3(
        WebApplicationBuilder builder)
    {
        builder.Services.AddDiagnosticLevelTest<ExpiredOrganizationRegistrationDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<FulfilmentIntegrityDiagnosticLevelTest>();

        builder.Services.AddDiagnosticLevelTest<MicrosoftGraphMessagingDiagnosticLevelTest>();
    }

    private static void AddLevel2(
        WebApplicationBuilder builder)
    {
        builder.Services.AddDiagnosticLevelTest<FfpManagerBridgeReadinessDiagnosticLevelTest>();
    }
}
