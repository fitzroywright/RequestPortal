using System.Security.Claims;
using Common.Diagnostics;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed record StaffOidcPrincipalResult(bool Allowed, ClaimsPrincipal? Principal, string? FailureCode = null);

public sealed class StaffOidcPrincipalService
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPermissionAuthorizer permissionAuthorizer;
    private readonly StaffOidcOptions oidcOptions;
    private readonly ActiveDirectoryOptions directoryOptions;
    private readonly IDiagnosticOperationsRouter diagnostics;

    public StaffOidcPrincipalService(
        UserManager<ApplicationUser> userManager,
        IPermissionAuthorizer permissionAuthorizer,
        IOptions<StaffOidcOptions> oidcOptions,
        IOptions<ActiveDirectoryOptions> directoryOptions,
        IDiagnosticOperationsRouter diagnostics)
    {
        this.userManager = userManager;
        this.permissionAuthorizer = permissionAuthorizer;
        this.oidcOptions = oidcOptions.Value;
        this.directoryOptions = directoryOptions.Value;
        this.diagnostics = diagnostics;
    }

    public async Task<StaffOidcPrincipalResult> BuildPrincipalAsync(
        ClaimsPrincipal providerPrincipal,
        CancellationToken cancellationToken = default)
    {
        string? rawUserName = FirstValue(providerPrincipal, oidcOptions.UserNameClaim)
            ?? FirstValue(providerPrincipal, ClaimTypes.Name)
            ?? FirstValue(providerPrincipal, "sub");
        if (string.IsNullOrWhiteSpace(rawUserName))
        {
            return new(false, null, "OIDC_USERNAME_MISSING");
        }

        string userName = NormalizeUserName(rawUserName);
        string displayName = FirstValue(providerPrincipal, oidcOptions.DisplayNameClaim)
            ?? FirstValue(providerPrincipal, ClaimTypes.Name)
            ?? userName;
        string email = FirstValue(providerPrincipal, oidcOptions.EmailClaim)
            ?? FirstValue(providerPrincipal, ClaimTypes.Email)
            ?? BuildDirectoryEmail(userName);
        string[] groups = oidcOptions.GroupClaimTypes
            .SelectMany(type => providerPrincipal.FindAll(type).Select(claim => claim.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(SplitGroupClaim)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AuthorizationSubject subject = AuthorizationSubject.Create(email, groups);
        AuthorizationDecision access = await permissionAuthorizer.AuthorizeAsync(
            subject,
            RequestPortalPermissions.Access,
            cancellationToken);
        if (!access.Allowed)
        {
            await ReportAsync("RP-AUTH-OIDC-DENIED", DiagnosticSeverity.Warning,
                "OIDC-authenticated staff member does not have Request Portal access.", access.Reason, cancellationToken);
            return new(false, null, "NOT_AUTHORIZED_FOR_REQUEST_PORTAL");
        }

        ApplicationUser user = await FindOrCreateShadowUserAsync(userName, displayName, email, cancellationToken);

        List<Claim> claims = providerPrincipal.Claims
            .Where(claim => claim.Type != ClaimTypes.NameIdentifier && claim.Type != ClaimTypes.Role)
            .ToList();
        claims.Add(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        claims.Add(new Claim(ClaimTypes.Name, displayName));
        claims.Add(new Claim(ClaimTypes.Email, email));
        claims.Add(new Claim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue));
        claims.AddRange(access.EffectiveRoles.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(role => new Claim(ClaimTypes.Role, role)));

        foreach (string permission in KnownStaffPermissions())
        {
            AuthorizationDecision decision = await permissionAuthorizer.AuthorizeAsync(subject, permission, cancellationToken);
            if (decision.Allowed)
            {
                claims.Add(new Claim(StaffOidcOptions.PermissionClaim, permission));
            }
        }

        ClaimsIdentity identity = new(
            claims,
            StaffOidcOptions.StaffCookieScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        await ReportAsync("RP-AUTH-OIDC-OK", DiagnosticSeverity.Information,
            "FFP staff identity authenticated by OIDC and authorized through Common.Security.",
            string.Join(',', access.EffectiveRoles), cancellationToken);
        return new(true, new ClaimsPrincipal(identity));
    }

    private async Task<ApplicationUser> FindOrCreateShadowUserAsync(
        string userName,
        string displayName,
        string email,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                IsInternalStaff = true,
                DirectoryUserName = userName,
                CreatedUtc = DateTimeOffset.UtcNow
            };
            IdentityResult created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "Unable to create the local staff shadow identity: " +
                    string.Join("; ", created.Errors.Select(error => error.Description)));
            }
        }

        user.DisplayName = displayName;
        user.DirectoryUserName = userName;
        user.IsInternalStaff = true;
        user.LastSignInUtc = DateTimeOffset.UtcNow;
        IdentityResult updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                "Unable to update the local staff shadow identity: " +
                string.Join("; ", updated.Errors.Select(error => error.Description)));
        }
        return user;
    }

    private IEnumerable<string> KnownStaffPermissions()
    {
        yield return RequestPortalPermissions.Access;
        yield return RequestPortalPermissions.ReviewRequests;
        yield return RequestPortalPermissions.ViewManagementDashboard;
        yield return RequestPortalPermissions.ManageSecurity;
    }

    private string BuildDirectoryEmail(string userName) =>
        string.IsNullOrWhiteSpace(directoryOptions.Domain)
            ? userName
            : $"{userName}@{directoryOptions.Domain}";

    private static string? FirstValue(ClaimsPrincipal principal, string claimType) =>
        principal.FindFirst(claimType)?.Value?.Trim();

    private static IEnumerable<string> SplitGroupClaim(string value) =>
        value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeUserName(string value)
    {
        string normalized = value.Trim();
        int slash = normalized.LastIndexOf('\\');
        if (slash >= 0 && slash < normalized.Length - 1) normalized = normalized[(slash + 1)..];
        int at = normalized.IndexOf('@');
        if (at > 0) normalized = normalized[..at];
        return normalized;
    }

    private Task ReportAsync(
        string code,
        DiagnosticSeverity severity,
        string message,
        string? detail,
        CancellationToken cancellationToken)
    {
        DiagnosticTelemetryEvent telemetry = new(
            DateTimeOffset.UtcNow,
            "Security.OIDC",
            code,
            severity,
            message,
            Properties: string.IsNullOrWhiteSpace(detail)
                ? null
                : new Dictionary<string, string> { ["Detail"] = detail });
        return diagnostics.RouteAsync(telemetry, cancellationToken);
    }
}
