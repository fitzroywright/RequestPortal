using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed record StaffSignInResult(bool Succeeded, string? FailureCode = null);

public sealed class StaffSignInService
{
    private readonly IAuthenticator authenticator;
    private readonly IPermissionAuthorizer permissionAuthorizer;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly ActiveDirectoryOptions directoryOptions;
    private readonly IStarfleetDiagnostics diagnostics;

    public StaffSignInService(
        IAuthenticator authenticator,
        RequestPortalDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IOptions<ActiveDirectoryOptions> directoryOptions,
        IStarfleetDiagnostics diagnostics)
    {
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        this.directoryOptions = directoryOptions?.Value ?? throw new ArgumentNullException(nameof(directoryOptions));
        this.permissionAuthorizer = new PermissionAuthorizer(
            new RequestPortalAuthorizationStore(dbContext, directoryOptions));
        this.userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        this.signInManager = signInManager ?? throw new ArgumentNullException(nameof(signInManager));
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
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

        AuthorizationSubject subject = AuthorizationSubject.Create(emailAddress, authentication.GroupNames);
        AuthorizationDecision accessDecision = await permissionAuthorizer.AuthorizeAsync(
            subject,
            RequestPortalPermissions.Access,
            cancellationToken);

        if (!accessDecision.Allowed)
        {
            await diagnostics.ReportAsync(
                "Security.Authorization",
                "SF-AUTH-STAFF-DENIED",
                StarfleetSeverity.Warning,
                "Authenticated FFP staff member has no Request Portal access permission.",
                accessDecision.Reason,
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

        Claim[] roleClaims = accessDecision.EffectiveRoles
            .Select(role => new Claim(ClaimTypes.Role, role))
            .ToArray();
        await signInManager.SignInWithClaimsAsync(user, isPersistent: false, roleClaims);

        await diagnostics.ReportAsync(
            "Security.Authorization",
            "SF-AUTH-PERMISSION-OK",
            StarfleetSeverity.Information,
            "FFP staff member authenticated and authorized through Common.Security.",
            string.Join(",", accessDecision.EffectiveRoles),
            cancellationToken: cancellationToken);

        return new StaffSignInResult(true);
    }

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
}
