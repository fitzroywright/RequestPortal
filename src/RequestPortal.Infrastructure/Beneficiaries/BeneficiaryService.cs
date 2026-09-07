using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Beneficiaries;
using RequestPortal.Application.Security;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Beneficiaries;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Beneficiaries;

public sealed class BeneficiaryService : IBeneficiaryService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;
    private readonly IClock clock;

    public BeneficiaryService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService,
        IClock clock)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
        this.clock = clock;
    }

    public async Task<Guid> CreateAsync(
        Guid userId,
        Guid organizationId,
        string firstName,
        string lastName,
        DateOnly? dateOfBirth,
        string? parentOrGuardian,
        string? location,
        CancellationToken cancellationToken = default)
    {
        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot create beneficiaries for this organization.");
        }

        Beneficiary beneficiary = new(
            Guid.NewGuid(),
            organizationId,
            firstName,
            lastName,
            dateOfBirth,
            parentOrGuardian,
            location,
            userId,
            clock.UtcNow);

        dbContext.Beneficiaries.Add(beneficiary);
        await dbContext.SaveChangesAsync(cancellationToken);
        return beneficiary.Id;
    }

    public async Task<IReadOnlyList<BeneficiarySummary>> GetForOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot access beneficiaries for this organization.");
        }

        List<BeneficiarySummary> beneficiaries = await dbContext.Beneficiaries
            .AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.LastName)
            .ThenBy(item => item.FirstName)
            .Select(item => new BeneficiarySummary(
                item.Id,
                item.OrganizationId,
                item.FirstName,
                item.LastName,
                item.DateOfBirth,
                item.ParentOrGuardian,
                item.Location))
            .ToListAsync(cancellationToken);

        return beneficiaries;
    }
}
