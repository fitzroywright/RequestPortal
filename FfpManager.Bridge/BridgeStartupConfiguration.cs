using Common.Secrets;

namespace FfpManager.Bridge;

public static class BridgeStartupConfiguration
{
    public static async Task ResolveAndValidateAsync(
        ConfigurationManager configuration,
        ISecretProvider provider,
        bool isProduction,
        CancellationToken cancellationToken = default)
    {
        string sharedSecretName = SecretName(configuration, "Bridge:SharedSecretSecretName", "FfpManagerIntegration:SharedSecret");
        string sqlSecretName = SecretName(configuration, "Bridge:SqlConnectionStringSecretName", "FfpManagerBridge:SqlConnectionString");

        string? sharedSecret = await provider.GetAsync(sharedSecretName, cancellationToken);
        string? sqlConnection = await provider.GetAsync(sqlSecretName, cancellationToken);

        if (!isProduction)
        {
            sharedSecret ??= configuration["Bridge:SharedSecret"];
            sqlConnection ??= configuration["Bridge:SqlConnectionString"];
        }

        // Overlay only in memory, after every external configuration source. The
        // secret-name setting is a lookup key, not a configuration substitution.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bridge:SharedSecret"] = string.IsNullOrWhiteSpace(sharedSecret) ? string.Empty : sharedSecret,
            ["Bridge:SqlConnectionString"] = string.IsNullOrWhiteSpace(sqlConnection) ? string.Empty : sqlConnection
        });

        string gateway = (configuration["Bridge:Gateway"] ?? "Fake").Trim();
        if (!gateway.Equals("Fake", StringComparison.OrdinalIgnoreCase) &&
            !gateway.Equals("Sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bridge:Gateway must be either 'Fake' or 'Sql'.");

        if (!gateway.Equals("Sql", StringComparison.OrdinalIgnoreCase)) return;

        if (string.IsNullOrWhiteSpace(sqlConnection))
            throw new InvalidOperationException(
                "Common.Secrets did not resolve Bridge:SqlConnectionStringSecretName into Bridge:SqlConnectionString. " +
                "Verify the configured OpenBao provider, MountPath, BasePath and KV v2 data.data.value. SQL gateway startup is blocked.");

        FfpSqlConnection.ValidateTarget(sqlConnection, configuration["Bridge:SqlTarget"] ?? string.Empty);

        if (string.IsNullOrWhiteSpace(configuration["Bridge:SupportingDocumentRoot"]))
            throw new InvalidOperationException("Bridge:SupportingDocumentRoot is required when Bridge:Gateway is Sql so RequestPortal artifacts can be stored for FFP Manager.");

        if (string.IsNullOrWhiteSpace(configuration["Bridge:InitialApplicationStatus"]))
            throw new InvalidOperationException("Bridge:InitialApplicationStatus is required when Bridge:Gateway is Sql.");
    }

    private static string SecretName(IConfiguration configuration, string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!.Trim();
}
