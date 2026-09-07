using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Common.Security;

public sealed record StaffDirectoryIdentity(
    string UserName,
    string DisplayName,
    string EmailAddress,
    IReadOnlyCollection<string> GroupNames);

public sealed record StaffAuthenticationResult(
    bool Succeeded,
    StaffDirectoryIdentity? Identity,
    string? FailureCode = null);

public interface IStaffDirectoryAuthenticator
{
    Task<StaffAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default);
}
