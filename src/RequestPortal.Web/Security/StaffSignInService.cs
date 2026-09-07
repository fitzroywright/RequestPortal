using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed record StaffSignInResult(bool Succeeded, string? FailureCode = null);

public sealed class StaffSignInService
{
    private readonly IStaffDirectoryAuthenticator directoryAuthenticator;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly RoleManager<IdentityRole<Guid>> roleManager;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly ActiveDirectoryOptions directoryOptions;
    private readonly IStarfleetDiagnostics diagnostics;

    public StaffSignInService(
        IStaffDirectoryAuthenticator directoryAuthenticator,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        SignInManager<ApplicationUser> signInManager,
        IOptions<ActiveDirectoryOptions> directoryOptions,
        IStarfleetDiagnostics diagnostics)
    {
        this.directoryAuthenticator = directoryAuthenticator;
        this.userManager = userManager;
        this.roleManager = roleManager;
        this.signInManager = signInManager;
        this.directoryOptions = directoryOptions.Value;
        this.diagnostics = diagnostics;
    }

    public async Task<StaffSignInResult> SignInAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        StaffAuthenticationResult authentication = await directoryAuthenticator.AuthenticateAsync(
            userName,
            password,
            cancellationToken);

        if (!authentication.Succeeded || authentication.Identity is null)
        {
            await diagnostics.ReportAsync(
                "Security.ActiveDirectory",
                "SF-AUTH-AD-FAIL",
                StarfleetSeverity.Warning,
                "FFP staff directory authentication failed.",
                authentication.FailureCode,
                cancellationToken: cancellationToken);

            return new StaffSignInResult(false, authentication.FailureCode ?? "AUTHENTICATION_FAILED");
        }

        StaffDirectoryIdentity identity = authentication.Identity;
        bool isAdministrator = MatchesAnyGroup(identity, directoryOptions.AdministratorGroups);
        bool isReviewer = isAdministrator || MatchesAnyGroup(identity, directoryOptions.ReviewerGroups);

        if (!isReviewer)
        {
            await diagnostics.ReportAsync(
                "Security.Authorization",
                "SF-AUTH-STAFF-DENIED",
                StarfleetSeverity.Warning,
                "Authenticated FFP staff member is not assigned to an authorized Request Portal AD group.",
                identity.UserName,
                cancellationToken: cancellationToken);

            return new StaffSignInResult(false, "NOT_AUTHORIZED_FOR_REQUEST_PORTAL");
        }

        ApplicationUser? user = await userManager.FindByEmailAsync(identity.EmailAddress);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = identity.EmailAddress,
                Email = identity.EmailAddress,
                EmailConfirmed = true,
                DisplayName = identity.DisplayName,
                IsInternalStaff = true,
                DirectoryUserName = identity.UserName,
                CreatedUtc = DateTimeOffset.UtcNow
            };

            IdentityResult createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                string detail = string.Join("; ", createResult.Errors.Select(error => error.Description));
                await diagnostics.ReportAsync(
                    "Security.Identity",
                    "SF-AUTH-LOCALUSER-FAIL",
                    StarfleetSeverity.Error,
                    "Failed to create local staff identity after successful AD authentication.",
                    detail,
                    cancellationToken: cancellationToken);
                return new StaffSignInResult(false, "LOCAL_IDENTITY_CREATION_FAILED");
            }
        }

        user.DisplayName = identity.DisplayName;
        user.DirectoryUserName = identity.UserName;
        user.IsInternalStaff = true;
        user.LastSignInUtc = DateTimeOffset.UtcNow;

        IdentityResult updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return new StaffSignInResult(false, "LOCAL_IDENTITY_UPDATE_FAILED");
        }

        await EnsureRoleExistsAsync(PortalRoles.FfpReviewer);
        await EnsureRoleExistsAsync(PortalRoles.FfpAdministrator);

        await SynchronizeRoleAsync(user, PortalRoles.FfpReviewer, isReviewer);
        await SynchronizeRoleAsync(user, PortalRoles.FfpAdministrator, isAdministrator);

        await signInManager.SignInAsync(user, isPersistent: false);

        await diagnostics.ReportAsync(
            "Security.ActiveDirectory",
            "SF-AUTH-AD-OK",
            StarfleetSeverity.Information,
            "FFP staff member authenticated through Active Directory.",
            identity.UserName,
            cancellationToken: cancellationToken);

        return new StaffSignInResult(true);
    }

    private static bool MatchesAnyGroup(StaffDirectoryIdentity identity, string[] requiredGroups)
    {
        return requiredGroups.Any(requiredGroup =>
            identity.GroupNames.Contains(requiredGroup, StringComparer.OrdinalIgnoreCase));
    }

    private async Task EnsureRoleExistsAsync(string roleName)
    {
        if (await roleManager.RoleExistsAsync(roleName))
        {
            return;
        }

        IdentityResult result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to create portal role '{roleName}'.");
        }
    }

    private async Task SynchronizeRoleAsync(ApplicationUser user, string roleName, bool shouldHaveRole)
    {
        bool currentlyInRole = await userManager.IsInRoleAsync(user, roleName);

        if (shouldHaveRole && !currentlyInRole)
        {
            await userManager.AddToRoleAsync(user, roleName);
        }
        else if (!shouldHaveRole && currentlyInRole)
        {
            await userManager.RemoveFromRoleAsync(user, roleName);
        }
    }
}
