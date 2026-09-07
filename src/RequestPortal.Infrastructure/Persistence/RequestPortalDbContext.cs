using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class RequestPortalDbContext : DbContext
{
    public RequestPortalDbContext(DbContextOptions<RequestPortalDbContext> options)
        : base(options)
    {
    }

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();

    public DbSet<AssistanceRequest> AssistanceRequests => Set<AssistanceRequest>();

    public DbSet<AssistanceRequestItem> AssistanceRequestItems => Set<AssistanceRequestItem>();

    public DbSet<RequestStatusHistory> RequestStatusHistory => Set<RequestStatusHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RequestPortalDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
