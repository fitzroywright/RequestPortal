using Common.Security.Abstractions;
using Common.Security.Authorization;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed class StaffNotificationAccess(RequestPortalAccounts accounts, RequestPortalDbContext db,
    IPermissionAuthorizer permissions, IAuthorizationStore roles) : IStaffNotificationAccess
{
    private async Task<string[]> GroupsAsync(Guid id, CancellationToken ct) =>
        await db.Database.SqlQuery<string[]>($"SELECT groups AS \"Value\" FROM requestportal_staff_signins WHERE account_id={id.ToString()}")
            .SingleOrDefaultAsync(ct) ?? [];

    public async Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct) =>
        accounts.Staff.Find(userId.ToString()) is { Status: SecurityAccountStatus.Active, Population: AccountPopulation.Staff } &&
        (await permissions.AuthorizeAsync(AuthorizationSubject.Create(userId.ToString(), await GroupsAsync(userId, ct)),
            RequestPortalPermissions.ReviewRequests, ct)).Allowed;

    public async Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct) =>
        await IsEligibleAsync(userId, ct) &&
        (await roles.GetDirectRoleKeysAsync(userId.ToString(), ct))
            .Concat(await roles.GetRoleKeysForGroupsAsync(await GroupsAsync(userId, ct), ct))
            .Contains(PortalRoles.Administrator, StringComparer.OrdinalIgnoreCase);
}
