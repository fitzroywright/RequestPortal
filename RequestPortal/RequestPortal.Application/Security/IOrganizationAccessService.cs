using System;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Security;

public interface IOrganizationAccessService
{
    Task<bool> IsApprovedMemberAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);

    Task<bool> IsAdministratorAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default);
}
