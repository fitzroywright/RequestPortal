using System;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Auditing;
using RequestPortal.Domain.Beneficiaries;
using RequestPortal.Domain.Countries;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Messages;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.ReferenceData;
using RequestPortal.Domain.Reviewers;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestFulfilment = RequestPortal.Domain.Fulfilment.Fulfilment;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class RequestPortalDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public RequestPortalDbContext(DbContextOptions<RequestPortalDbContext> options) : base(options) { }
    public DbSet<RequestPortal.Infrastructure.Management.ManagementDashboardAggregate> ManagementDashboardAggregates => Set<RequestPortal.Infrastructure.Management.ManagementDashboardAggregate>();
    public DbSet<RequestPortal.Infrastructure.Management.PersistedRequestLocation> RequestLocations => Set<RequestPortal.Infrastructure.Management.PersistedRequestLocation>();
    public DbSet<RequestPortal.Infrastructure.Management.RequestLocationImportState> RequestLocationImportStates => Set<RequestPortal.Infrastructure.Management.RequestLocationImportState>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();
    public DbSet<AssistanceRequest> AssistanceRequests => Set<AssistanceRequest>();
    public DbSet<QuickIntakeRecord> QuickIntakes => Set<QuickIntakeRecord>();
    public DbSet<AssistanceRequestItem> AssistanceRequestItems => Set<AssistanceRequestItem>();
    public DbSet<RequestStatusHistory> RequestStatusHistory => Set<RequestStatusHistory>();
    public DbSet<ProjectProposal> ProjectProposals => Set<ProjectProposal>();
    public DbSet<ProjectBudgetItem> ProjectBudgetItems => Set<ProjectBudgetItem>();
    public DbSet<ProjectMilestone> ProjectMilestones => Set<ProjectMilestone>();
    public DbSet<RequestCategory> RequestCategories => Set<RequestCategory>();
    public DbSet<ProgramClassification> ProgramClassifications => Set<ProgramClassification>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<CountryConfiguration> CountryConfigurations => Set<CountryConfiguration>();
    public DbSet<StoredDocument> Documents => Set<StoredDocument>();
    public DbSet<DeliveryRecord> Deliveries => Set<DeliveryRecord>();
    public DbSet<DeliveryReturnRequest> DeliveryReturns => Set<DeliveryReturnRequest>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<RequestFulfilment> Fulfilments => Set<RequestFulfilment>();
    public DbSet<IntegrationLink> IntegrationLinks => Set<IntegrationLink>();
    public DbSet<IntegrationAttempt> IntegrationAttempts => Set<IntegrationAttempt>();
    public DbSet<MessageThread> MessageThreads => Set<MessageThread>();
    public DbSet<PortalMessage> PortalMessages => Set<PortalMessage>();
    public DbSet<MessageRecipient> MessageRecipients => Set<MessageRecipient>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationHistory> NotificationHistory => Set<NotificationHistory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<ExternalReviewerInvitation> ExternalReviewerInvitations => Set<ExternalReviewerInvitation>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasSequence<long>("request_code_sequence").StartsAt(10000);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RequestPortalDbContext).Assembly);
    }
}
