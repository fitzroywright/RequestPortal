using System;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Auditing;
using RequestPortal.Domain.Beneficiaries;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestFulfilment = RequestPortal.Domain.Fulfilment.Fulfilment;

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

    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();

    public DbSet<StoredDocument> Documents => Set<StoredDocument>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<RequestFulfilment> Fulfilments => Set<RequestFulfilment>();

    public DbSet<IntegrationLink> IntegrationLinks => Set<IntegrationLink>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<NotificationHistory> NotificationHistory => Set<NotificationHistory>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RequestPortalDbContext).Assembly);
    }
}
