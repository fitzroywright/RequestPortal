namespace RequestPortal.Infrastructure.Integration;

public sealed class FfpManagerBridgeOptions
{
    public const string SectionName = "FfpManagerIntegration";

    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string? SharedSecret { get; set; }

    public string SharedSecretName { get; set; } = "FfpManagerIntegration:SharedSecret";

    public int TimeoutSeconds { get; set; } = 30;

    public int PollIntervalSeconds { get; set; } = 60;

    public string SystemName { get; set; } = "FFPManager";
}
