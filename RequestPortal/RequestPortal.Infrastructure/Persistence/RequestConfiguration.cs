using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Persistence;

internal sealed class AssistanceRequestConfiguration : IEntityTypeConfiguration<AssistanceRequest>
{
    public void Configure(EntityTypeBuilder<AssistanceRequest> builder)
    {
        builder.ToTable("assistance_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.PublicReference).HasMaxLength(40).IsRequired();
        builder.Property(request => request.Summary).HasMaxLength(4000);
        builder.Property(request => request.Category).HasMaxLength(150);
        builder.Property(request => request.Subcategory).HasMaxLength(150);
        builder.Property(request => request.ProgramCategory).HasMaxLength(150);
        builder.Property(request => request.Location).HasMaxLength(500);
        builder.Property(request => request.Circumstances).HasMaxLength(8000);
        builder.Property(request => request.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(request => request.Priority).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(request => request.PublicReference).IsUnique();
        builder.HasIndex(request => new { request.OrganizationId, request.Status });
        builder.HasIndex(request => new { request.OrganizationId, request.Kind });
        builder.HasIndex(request => request.BeneficiaryId);
        builder.HasMany(request => request.Items).WithOne().HasForeignKey(item => item.RequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(request => request.StatusHistory).WithOne().HasForeignKey(history => history.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AssistanceRequestItemConfiguration : IEntityTypeConfiguration<AssistanceRequestItem>
{
    public void Configure(EntityTypeBuilder<AssistanceRequestItem> builder)
    {
        builder.ToTable("assistance_request_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Description).HasMaxLength(1000).IsRequired();
        builder.Property(item => item.Quantity).HasPrecision(18, 3);
    }
}

internal sealed class RequestStatusHistoryConfiguration : IEntityTypeConfiguration<RequestStatusHistory>
{
    public void Configure(EntityTypeBuilder<RequestStatusHistory> builder)
    {
        builder.ToTable("request_status_history");
        builder.HasKey(history => history.Id);
        builder.Property(history => history.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(history => history.Reason).HasMaxLength(4000);
        builder.HasIndex(history => new { history.RequestId, history.ChangedUtc });
    }
}

internal sealed class ProjectProposalConfiguration : IEntityTypeConfiguration<ProjectProposal>
{
    public void Configure(EntityTypeBuilder<ProjectProposal> builder)
    {
        builder.ToTable("project_proposals");
        builder.HasKey(proposal => proposal.Id);
        builder.Property(proposal => proposal.Title).HasMaxLength(500).IsRequired();
        builder.Property(proposal => proposal.RichContentHtml).HasColumnType("text").IsRequired();
        builder.HasIndex(proposal => proposal.RequestId).IsUnique();
    }
}

internal sealed class ProjectBudgetItemConfiguration : IEntityTypeConfiguration<ProjectBudgetItem>
{
    public void Configure(EntityTypeBuilder<ProjectBudgetItem> builder)
    {
        builder.ToTable("project_budget_items");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Description).HasMaxLength(1000).IsRequired();
        builder.Property(item => item.Quantity).HasPrecision(18, 3);
        builder.Property(item => item.UnitCost).HasPrecision(18, 2);
        builder.Property(item => item.FfpRequested).HasPrecision(18, 2);
        builder.Property(item => item.OrganizationContribution).HasPrecision(18, 2);
        builder.Property(item => item.OtherFunding).HasPrecision(18, 2);
        builder.Ignore(item => item.TotalCost);
        builder.HasIndex(item => item.RequestId);
    }
}

internal sealed class ProjectMilestoneConfiguration : IEntityTypeConfiguration<ProjectMilestone>
{
    public void Configure(EntityTypeBuilder<ProjectMilestone> builder)
    {
        builder.ToTable("project_milestones");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Title).HasMaxLength(500).IsRequired();
        builder.Property(item => item.ExpectedOutcome).HasMaxLength(4000);
        builder.HasIndex(item => new { item.RequestId, item.Sequence });
    }
}
