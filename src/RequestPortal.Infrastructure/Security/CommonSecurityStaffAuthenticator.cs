using Common.Security.Abstractions;
using Common.Security.Models;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;

namespace RequestPortal.Infrastructure.Security;

public sealed class CommonSecurityStaffAuthenticator : IStaffDirectoryAuthenticator
{
    private readonly IAuthenticator authenticator;
    private readonly ActiveDirectoryOptions options;

    public CommonSecurityStaffAuthenticator(
        IAuthenticator authenticator,
        IOptions<ActiveDirectoryOptions> options)
    {
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<StaffAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Authenticate(userName, password), cancellationToken);
    }

    private StaffAuthenticationResult Authenticate(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return new StaffAuthenticationResult(false, null, "INVALID_CREDENTIALS");
        }

        string normalizedUserName = NormalizeUserName(userName);
        AuthenticationResult result = authenticator.Authenticate(normalizedUserName, password);
        if (!result.IsAuthenticated)
        {
            return new StaffAuthenticationResult(false, null, "INVALID_CREDENTIALS_OR_DIRECTORY_UNAVAILABLE");
        }

        string displayName = result.UserInfo?.DisplayName?.Trim() ?? normalizedUserName;
        string emailAddress = result.UserInfo?.Mail?.Trim()
            ?? (string.IsNullOrWhiteSpace(options.Domain)
                ? normalizedUserName
                : $"{normalizedUserName}@{options.Domain}");

        StaffDirectoryIdentity identity = new(
            normalizedUserName,
            displayName,
            emailAddress,
            result.GroupNames);

        return new StaffAuthenticationResult(true, identity);
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
