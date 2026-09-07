using System;
using System.Threading;
using System.Threading.Tasks;
using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Requests;

public sealed class ProjectProposalService : IProjectProposalService
{
    private static readonly HtmlSanitizer sanitizer = CreateSanitizer();
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;
    private readonly IClock clock;

    public ProjectProposalService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService,
        IClock clock)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
        this.clock = clock;
    }

    public async Task InitializeProjectAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);
        request.SetKind(RequestKind.Project);

        bool exists = await dbContext.ProjectProposals
            .AnyAsync(item => item.RequestId == requestId, cancellationToken);

        if (!exists)
        {
            dbContext.ProjectProposals.Add(new ProjectProposal(Guid.NewGuid(), requestId, userId, clock.UtcNow));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProjectProposalDetail?> GetAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest? request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);

        if (request is null || request.Kind != RequestKind.Project)
        {
            return null;
        }

        if (!isStaff)
        {
            await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);
        }

        ProjectProposal? proposal = await dbContext.ProjectProposals
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);

        if (proposal is null)
        {
            return null;
        }

        bool canEdit = !isStaff && (request.Status == RequestStatus.Draft || request.Status == RequestStatus.MoreInformationRequired);
        return new ProjectProposalDetail(
            request.Id,
            request.PublicReference,
            proposal.Title,
            proposal.RichContentHtml,
            proposal.Version,
            proposal.UpdatedUtc,
            canEdit);
    }

    public async Task SaveAsync(
        Guid userId,
        Guid requestId,
        string title,
        string richContentHtml,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);

        if (request.Kind != RequestKind.Project)
        {
            throw new InvalidOperationException("The request is not a project request.");
        }

        if (request.Status != RequestStatus.Draft && request.Status != RequestStatus.MoreInformationRequired)
        {
            throw new InvalidOperationException("The proposal cannot be edited in the current request status.");
        }

        ProjectProposal proposal = await dbContext.ProjectProposals
            .SingleAsync(item => item.RequestId == requestId, cancellationToken);

        string sanitizedHtml = sanitizer.Sanitize(richContentHtml ?? string.Empty).Trim();
        if (sanitizedHtml.Length > 500_000)
        {
            throw new InvalidOperationException("The project proposal is too large. Reduce embedded image sizes or attach large files separately.");
        }

        proposal.Update(title, sanitizedHtml, userId, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAccessAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot access this project proposal.");
        }
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        HtmlSanitizer value = new();
        value.AllowedTags.Clear();
        foreach (string tag in new[]
        {
            "p", "br", "strong", "b", "em", "i", "u", "s", "h1", "h2", "h3", "h4",
            "ul", "ol", "li", "blockquote", "table", "thead", "tbody", "tr", "th", "td",
            "a", "img", "figure", "figcaption", "hr", "span", "div"
        })
        {
            value.AllowedTags.Add(tag);
        }

        value.AllowedAttributes.Clear();
        foreach (string attribute in new[]
        {
            "href", "src", "alt", "title", "colspan", "rowspan", "width", "height", "class"
        })
        {
            value.AllowedAttributes.Add(attribute);
        }

        value.AllowedSchemes.Clear();
        value.AllowedSchemes.Add("https");
        value.AllowedSchemes.Add("http");
        value.AllowedSchemes.Add("data");
        value.AllowDataAttributes = false;
        value.KeepChildNodes = true;
        return value;
    }
}
