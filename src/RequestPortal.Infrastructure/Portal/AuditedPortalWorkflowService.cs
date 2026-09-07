using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Portal;

namespace RequestPortal.Infrastructure.Portal;

public sealed class AuditedPortalWorkflowService : IPortalWorkflowService
{
    private readonly PortalWorkflowService inner;
    private readonly IBusinessAuditWriter auditWriter;

    public AuditedPortalWorkflowService(
        PortalWorkflowService inner,
        IBusinessAuditWriter auditWriter)
    {
        this.inner = inner;
        this.auditWriter = auditWriter;
    }

    public Task<IReadOnlyList<OrganizationSummary>> GetOrganizationsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return inner.GetOrganizationsForUserAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<OrganizationSummary>> GetAvailableOrganizationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return inner.GetAvailableOrganizationsAsync(userId, cancellationToken);
    }

    public async Task<Guid> RegisterOrganizationAsync(
        Guid userId,
        string name,
        string countryCode,
        CancellationToken cancellationToken = default)
    {
        Guid organizationId = await inner.RegisterOrganizationAsync(userId, name, countryCode, cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "OrganizationRegistered",
            "Organization",
            organizationId,
            organizationId,
            name,
            cancellationToken);
        return organizationId;
    }

    public async Task RequestMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await inner.RequestMembershipAsync(userId, organizationId, cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "MembershipRequested",
            "Organization",
            organizationId,
            organizationId,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<OrganizationSummary>> GetPendingOrganizationsAsync(
        CancellationToken cancellationToken = default)
    {
        return inner.GetPendingOrganizationsAsync(cancellationToken);
    }

    public async Task ApproveOrganizationAsync(
        Guid staffUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await inner.ApproveOrganizationAsync(staffUserId, organizationId, cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "OrganizationApproved",
            "Organization",
            organizationId,
            organizationId,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<PendingMembershipSummary>> GetPendingMembershipsAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return inner.GetPendingMembershipsAsync(administratorUserId, organizationId, cancellationToken);
    }

    public async Task ApproveMembershipAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        await inner.ApproveMembershipAsync(administratorUserId, organizationId, membershipId, cancellationToken);
        await auditWriter.WriteAsync(
            administratorUserId,
            "MembershipApproved",
            "OrganizationMembership",
            membershipId,
            organizationId,
            cancellationToken: cancellationToken);
    }

    public async Task<Guid> CreateRequestAsync(
        Guid userId,
        Guid organizationId,
        string summary,
        CancellationToken cancellationToken = default)
    {
        Guid requestId = await inner.CreateRequestAsync(userId, organizationId, summary, cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "RequestCreated",
            "AssistanceRequest",
            requestId,
            organizationId,
            summary,
            cancellationToken);
        return requestId;
    }

    public async Task AddRequestItemAsync(
        Guid userId,
        Guid requestId,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        CancellationToken cancellationToken = default)
    {
        await inner.AddRequestItemAsync(userId, requestId, description, quantity, unitOfMeasureId, cancellationToken);
    }

    public async Task SubmitRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        RequestDetail? request = await inner.GetRequestAsync(userId, requestId, isStaff: false, cancellationToken);
        await inner.SubmitRequestAsync(userId, requestId, cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "RequestSubmitted",
            "AssistanceRequest",
            requestId,
            request?.OrganizationId,
            cancellationToken: cancellationToken);
    }

    public async Task ResubmitRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        RequestDetail? request = await inner.GetRequestAsync(userId, requestId, isStaff: false, cancellationToken);
        await inner.ResubmitRequestAsync(userId, requestId, cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "RequestResubmitted",
            "AssistanceRequest",
            requestId,
            request?.OrganizationId,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<RequestSummary>> GetRequestsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return inner.GetRequestsForUserAsync(userId, cancellationToken);
    }

    public Task<IReadOnlyList<RequestSummary>> GetSubmittedRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        return inner.GetSubmittedRequestsAsync(cancellationToken);
    }

    public Task<RequestDetail?> GetRequestAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        return inner.GetRequestAsync(userId, requestId, isStaff, cancellationToken);
    }

    public async Task ApproveRequestAsync(
        Guid staffUserId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        RequestDetail? request = await inner.GetRequestAsync(staffUserId, requestId, isStaff: true, cancellationToken);
        await inner.ApproveRequestAsync(staffUserId, requestId, cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "RequestApproved",
            "AssistanceRequest",
            requestId,
            request?.OrganizationId,
            cancellationToken: cancellationToken);
    }

    public async Task RejectRequestAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequestDetail? request = await inner.GetRequestAsync(staffUserId, requestId, isStaff: true, cancellationToken);
        await inner.RejectRequestAsync(staffUserId, requestId, reason, cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "RequestRejected",
            "AssistanceRequest",
            requestId,
            request?.OrganizationId,
            reason,
            cancellationToken);
    }

    public async Task RequestMoreInformationAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequestDetail? request = await inner.GetRequestAsync(staffUserId, requestId, isStaff: true, cancellationToken);
        await inner.RequestMoreInformationAsync(staffUserId, requestId, reason, cancellationToken);
        await auditWriter.WriteAsync(
            staffUserId,
            "MoreInformationRequested",
            "AssistanceRequest",
            requestId,
            request?.OrganizationId,
            reason,
            cancellationToken);
    }
}
