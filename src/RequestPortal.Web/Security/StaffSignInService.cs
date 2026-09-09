using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Security.Abstractions;
using Common.Security.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed record StaffSignInResult(bool Succeeded, string? FailureCode = null);

public sealed class StaffSignInService
{
    private readonly IAuthenticator authenticator;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly RoleManager<IdentityRole<Guid>> roleManager;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly ActiveDirectoryOptions directoryOptions;
    private readonly IStarfleetDiagnostics diagnostics;

    public StaffSignInService(
        IAuthenticator authenticator,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        SignInManager<ApplicationUser> signInManager,
        IOptions<ActiveDirectoryOptions> directoryOptions,
        IStarfleetDiagnostics diagnostics)
    {
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
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
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedUserName = NormalizeUserName(userName);
        AuthenticationResult authentication = authenticator.Authenticate(normalizedUserName, password);

        if (!authentication.IsAuthenticated)
        {
            await diagnostics.ReportAsync(
                "Security.ActiveDirectory",
                "SF-AUTH-AD-FAIL",
                StarfleetSeverity.Warning,
                "FFP staff directory authentication failed.",
                "INVALID_CREDENTIALS_OR_DIRECTORY_UNAVAILABLE",
                cancellationToken: cancellationToken);

            return new StaffSignInResult(false, "INVALID_CREDENTIALS_OR_DIRECTORY_UNAVAILABLE");
        }

        string displayName = authentication.UserInfo?.DisplayName?.Trim() ?? normalizedUserName;
        string emailAddress = authentication.UserInfo?.Mail?.Trim()
            ?? (string.IsNullOrWhiteSpace(directoryOptions.Domain)
                ? normalizedUserName
                : $"{normalizedUserName}@{directoryOptions.Domain}");

        bool isAdministrator = MatchesAnyGroup(authentication.GroupNames, directoryOptions.AdministratorGroups);
        bool isReviewer = isAdministrator || MatchesAnyGroup(authentication.GroupNames, directoryOptions.ReviewerGroups);

        if (!isReviewer)
        {
            await diagnostics.ReportAsync(
                "Security.Authorization",
                "SF-AUTH-STAFF-DENIED",
                StarfleetSeverity.Warning,
                "Authenticated FFP staff member is not assigned to an authorized Request Portal AD group.",
                normalizedUserName,
                cancellationToken: cancellationToken);

            return new StaffSignInResult(false, "NOT_AUTHORIZED_FOR_REQUEST_PORTAL");
        }

        ApplicationUser? user = await userManager.FindByEmailAsync(emailAddress);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = emailAddress,
                Email = emailAddress,
                EmailConfirmed = true,
                DisplayName = displayName,
                IsInternalStaff = true,
                DirectoryUserName = normalizedUserName,
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

        user.DisplayName = displayName;
        user.DirectoryUserName = normalizedUserName;
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
            "FFP staff member authenticated through Common.Security.",
            normalizedUserName,
            cancellationToken: cancellationToken);

        return new StaffSignInResult(true);
    }

    private static bool MatchesAnyGroup(
        IReadOnlyCollection<string> actualGroups,
        string[] requiredGroups)
        => requiredGroups.Any(requiredGroup =>
            actualGroups.Contains(requiredGroup, StringComparer.OrdinalIgnoreCase));

    private static string NormalizeUserName(string userName)
    {
        string trimmed = userName.Trim();
        int slashIndex = trimmed.LastIndexOf('\\');
        if (slashIndex >= 0 && slashIndex < trimmed.Length - 1)
        {
            trimmed = trimmed[(slashIndex + 1)..];
        }

        int atIndex = trimmed.IndexOf('@');
        if (atIndex > 0)
        {
            trimmed = trimmed[..atIndex];
        }

        return trimmed;
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
