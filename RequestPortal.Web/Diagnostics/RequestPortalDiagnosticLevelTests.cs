using Common.Diagnostics;

namespace RequestPortal.Web.Diagnostics;

public sealed class BasicRuntimeIdentityDiagnosticLevelTest(
    IConfiguration configuration,
    IHostEnvironment environment) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L5.RUNTIME.IDENTITY";
    public string Name => "Runtime identity";
    public string Owner => "RequestPortal.Web";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string instanceId =
            configuration["Service:Identity"]?.Trim()
            ?? configuration["Client:InstallationId"]?.Trim()
            ?? Environment.MachineName;

        return Task.FromResult(string.IsNullOrWhiteSpace(instanceId)
            ? EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "No stable runtime identity could be resolved.")
            : EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Runtime identity is available.",
                $"Instance={instanceId}; Environment={environment.EnvironmentName}; Machine={Environment.MachineName}"));
    }
}

public sealed class BasicControlPlaneConfigurationDiagnosticLevelTest(
    IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L5.CONTROLPLANE.CONFIG";
    public string Name => "Control Plane configuration";
    public string Owner => "RequestPortal.Web";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] required =
        [
            "Aegis:Configuration:Url",
            "Aegis:Diagnostics:Url",
            "Aegis:Operations:Url",
            "Aegis:Diagnostics:RequestCredentialSecretName"
        ];

        string[] missing = required
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();

        return Task.FromResult(missing.Length == 0
            ? EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Required Control Plane settings are configured.")
            : EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "One or more Control Plane settings are missing.",
                $"Missing={string.Join(",", missing)}"));
    }
}
