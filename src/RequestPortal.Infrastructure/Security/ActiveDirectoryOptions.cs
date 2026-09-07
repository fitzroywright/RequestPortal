namespace RequestPortal.Infrastructure.Security;

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "Authentication:ActiveDirectory";

    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 636;

    public string Domain { get; set; } = string.Empty;

    public string SearchBase { get; set; } = string.Empty;

    public bool RequireLdaps { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 10;
}
