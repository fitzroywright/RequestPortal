using System;
using Microsoft.AspNetCore.Identity;

namespace RequestPortal.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsInternalStaff { get; set; }
    public string? DirectoryUserName { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastSignInUtc { get; set; }

    // A representative account returns to individual mode when its final approved
    // organization membership ends. The profile must then be completed before
    // personal requests can be submitted.
    public bool IndividualProfileUpdateRequired { get; set; }
    public DateTimeOffset? IndividualProfileUpdateRequiredUtc { get; set; }
}
