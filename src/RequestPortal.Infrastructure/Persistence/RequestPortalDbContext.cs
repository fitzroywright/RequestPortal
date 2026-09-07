using System;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class RequestPortalDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
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

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RequestPortalDbContext).Assembly);
    }
}
