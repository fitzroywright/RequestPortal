namespace RequestPortal.Infrastructure.Security;

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "Authentication:ActiveDirectory";

    // RequestPortal does not connect to AD. Domain is used only to normalize a
    // provider identity when the configured OIDC provider does not emit email.
    public string Domain { get; set; } = string.Empty;

    // These are authorization mappings for AD group claims emitted by the OIDC provider.
    public string[] AdministratorGroups { get; set; } = [];

    public string[] ReviewerGroups { get; set; } = [];
}
