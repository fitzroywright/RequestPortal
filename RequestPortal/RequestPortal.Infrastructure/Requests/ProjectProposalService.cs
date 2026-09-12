using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Requests;

public sealed class ProjectProposalService : IProjectProposalService
{
    private static readonly HtmlSanitizer sanitizer = CreateSanitizer();
    private static readonly Regex htmlTagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex blockEndRegex = new("</(p|div|h1|h2|h3|h4|li|tr|blockquote)>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyList<LongFormTemplate> templates =
    [
        new(
            "project-proposal",
            "Project Proposal",
            "Project",
            "Project Proposal",
            """
            <h1>Executive Summary</h1><p>Summarize the project, the need, who will benefit, and the assistance requested.</p>
            <h2>Need and Context</h2><p>Describe the problem, evidence of need, location, and current conditions.</p>
            <h2>Beneficiaries</h2><p>Describe the people or communities who will benefit and the expected reach.</p>
            <h2>Objectives</h2><ul><li>Objective 1</li><li>Objective 2</li></ul>
            <h2>Activities and Implementation</h2><p>Describe the work plan, responsibilities, partners, and delivery approach.</p>
            <h2>Sustainability and Maintenance</h2><p>Explain how the benefits will be maintained after implementation.</p>
            <h2>Risks and Mitigation</h2><p>Identify significant risks and how they will be managed.</p>
            <h2>Monitoring and Results</h2><p>Describe the outcomes, indicators, and how progress will be measured.</p>
            """),
        new(
            "narrative",
            "General Narrative",
            "Project",
            "Request Narrative",
            """
            <h1>Request Narrative</h1><h2>Background</h2><p>Provide relevant background and context.</p>
            <h2>Need</h2><p>Explain the need and why assistance is required.</p>
            <h2>Requested Support</h2><p>Describe the support being requested.</p>
            <h2>Expected Impact</h2><p>Describe the expected benefit and who will be reached.</p>
            """)
    ];

    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;
    private readonly IClock clock;
    private readonly IBusinessAuditWriter auditWriter;

    public ProjectProposalService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService,
        IClock clock,
        IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
        this.clock = clock;
        this.auditWriter = auditWriter;
    }

    public async Task InitializeProjectAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.SingleAsync(item => item.Id == requestId, cancellationToken);
        await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);
        request.SetKind(RequestKind.Project);

        bool exists = await dbContext.ProjectProposals.AnyAsync(item => item.RequestId == requestId, cancellationToken);
        if (!exists)
        {
            ProjectProposal proposal = new(Guid.NewGuid(), requestId, userId, clock.UtcNow);
            dbContext.ProjectProposals.Add(proposal);
            LongFormTemplate initial = templates[0];
            proposal.Update(initial.Title, initial.RichContentHtml, userId, clock.UtcNow);
            AddRevision(proposal, userId, automatic: false);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProjectProposalDetail?> GetAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default)
    {
        AssistanceRequest? request = await dbContext.AssistanceRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null || request.Kind != RequestKind.Project) return null;
        if (!isStaff) await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);

        ProjectProposal? proposal = await dbContext.ProjectProposals.AsNoTracking().SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (proposal is null) return null;

        bool canEdit = !isStaff && (request.Status == RequestStatus.Draft || request.Status == RequestStatus.MoreInformationRequired);
        return new ProjectProposalDetail(request.Id, request.PublicReference, proposal.Title, proposal.RichContentHtml, proposal.Version, proposal.UpdatedUtc, canEdit);
    }

    public Task SaveAsync(Guid userId, Guid requestId, string title, string richContentHtml, CancellationToken cancellationToken = default) =>
        SaveInternalAsync(userId, requestId, title, richContentHtml, automatic: false, cancellationToken);

    public Task SaveAutosaveAsync(Guid userId, Guid requestId, string title, string richContentHtml, CancellationToken cancellationToken = default) =>
        SaveInternalAsync(userId, requestId, title, richContentHtml, automatic: true, cancellationToken);

    public async Task<IReadOnlyList<ProjectProposalRevisionDetail>> GetRevisionsAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.AsNoTracking().SingleAsync(item => item.Id == requestId, cancellationToken);
        if (!isStaff) await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);

        return await dbContext.ProjectProposalRevisions.AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderByDescending(item => item.SavedUtc)
            .Take(100)
            .Select(item => new ProjectProposalRevisionDetail(item.Id, item.Version, item.Title, item.SavedUtc, item.Automatic, item.EditedByUserId))
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreRevisionAsync(Guid userId, Guid requestId, Guid revisionId, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.SingleAsync(item => item.Id == requestId, cancellationToken);
        await EnsureEditableAsync(userId, request, cancellationToken);
        ProjectProposal proposal = await dbContext.ProjectProposals.SingleAsync(item => item.RequestId == requestId, cancellationToken);
        ProjectProposalRevision revision = await dbContext.ProjectProposalRevisions.AsNoTracking()
            .SingleAsync(item => item.Id == revisionId && item.RequestId == requestId, cancellationToken);

        AddRevision(proposal, userId, automatic: false);
        proposal.Update(revision.Title, revision.RichContentHtml, userId, clock.UtcNow);
        AddRevision(proposal, userId, automatic: false);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(userId, "ProjectProposalRevisionRestored", nameof(ProjectProposal), proposal.Id, request.OrganizationId,
            $"Restored proposal revision {revision.Id} (version {revision.Version}).", cancellationToken);
    }

    public IReadOnlyList<LongFormTemplate> GetTemplates(string requestType = "Project") =>
        templates.Where(item => item.RequestType.Equals(requestType, StringComparison.OrdinalIgnoreCase)).ToArray();

    public async Task ApplyTemplateAsync(Guid userId, Guid requestId, string templateKey, CancellationToken cancellationToken = default)
    {
        LongFormTemplate template = templates.FirstOrDefault(item => item.Key.Equals(templateKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown long-form document template.");
        await SaveInternalAsync(userId, requestId, template.Title, template.RichContentHtml, automatic: false, cancellationToken);
    }

    public async Task<ProjectProposalExport> ExportDocxAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default)
    {
        ProjectProposalDetail proposal = await GetRequiredForExportAsync(userId, requestId, isStaff, cancellationToken);
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new Document(new Body());
            AlternativeFormatImportPart htmlPart = main.AddAlternativeFormatImportPart(AlternativeFormatImportPartType.Html);
            string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(proposal.Title)}</title></head><body><h1>{WebUtility.HtmlEncode(proposal.Title)}</h1>{proposal.RichContentHtml}</body></html>";
            using Stream partStream = htmlPart.GetStream(FileMode.Create, FileAccess.Write);
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            partStream.Write(htmlBytes, 0, htmlBytes.Length);
            string relationshipId = main.GetIdOfPart(htmlPart);
            main.Document.Body!.Append(new AltChunk { Id = relationshipId });
            main.Document.Save();
        }

        return new ProjectProposalExport(SafeFileName(proposal.Title) + ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", stream.ToArray());
    }

    public async Task<ProjectProposalExport> ExportPdfAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default)
    {
        ProjectProposalDetail proposal = await GetRequiredForExportAsync(userId, requestId, isStaff, cancellationToken);
        string text = HtmlToPlainText(proposal.RichContentHtml);
        byte[] bytes = SimplePdfWriter.Create(proposal.Title, text);
        return new ProjectProposalExport(SafeFileName(proposal.Title) + ".pdf", "application/pdf", bytes);
    }

    private async Task SaveInternalAsync(
        Guid userId,
        Guid requestId,
        string title,
        string richContentHtml,
        bool automatic,
        CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.SingleAsync(item => item.Id == requestId, cancellationToken);
        await EnsureEditableAsync(userId, request, cancellationToken);
        ProjectProposal proposal = await dbContext.ProjectProposals.SingleAsync(item => item.RequestId == requestId, cancellationToken);

        string sanitizedHtml = Sanitize(richContentHtml);
        string cleanTitle = string.IsNullOrWhiteSpace(title) ? "Project Proposal" : title.Trim();
        if (proposal.Title == cleanTitle && proposal.RichContentHtml == sanitizedHtml) return;

        AddRevision(proposal, userId, automatic);
        proposal.Update(cleanTitle, sanitizedHtml, userId, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            userId,
            automatic ? "ProjectProposalAutosaved" : "ProjectProposalSaved",
            nameof(ProjectProposal),
            proposal.Id,
            request.OrganizationId,
            $"Version {proposal.Version}; {(automatic ? "automatic draft save" : "explicit save")}.",
            cancellationToken);
    }

    private void AddRevision(ProjectProposal proposal, Guid editedByUserId, bool automatic)
    {
        dbContext.ProjectProposalRevisions.Add(new ProjectProposalRevision(
            Guid.NewGuid(), proposal.Id, proposal.RequestId, proposal.Version, proposal.Title,
            proposal.RichContentHtml, editedByUserId, clock.UtcNow, automatic));
    }

    private string Sanitize(string richContentHtml)
    {
        string sanitizedHtml = sanitizer.Sanitize(richContentHtml ?? string.Empty).Trim();
        if (sanitizedHtml.Length > 500_000)
            throw new InvalidOperationException("The project proposal is too large. Reduce embedded image sizes or attach large files separately.");
        return sanitizedHtml;
    }

    private async Task EnsureEditableAsync(Guid userId, AssistanceRequest request, CancellationToken cancellationToken)
    {
        await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);
        if (request.Kind != RequestKind.Project) throw new InvalidOperationException("The request is not a project request.");
        if (request.Status != RequestStatus.Draft && request.Status != RequestStatus.MoreInformationRequired)
            throw new InvalidOperationException("The proposal cannot be edited in the current request status.");
    }

    private async Task<ProjectProposalDetail> GetRequiredForExportAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken) =>
        await GetAsync(userId, requestId, isStaff, cancellationToken)
        ?? throw new InvalidOperationException("The project proposal could not be found.");

    private async Task EnsureAccessAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (!allowed) throw new UnauthorizedAccessException("The current user cannot access this project proposal.");
    }

    private static string HtmlToPlainText(string html)
    {
        string withBreaks = blockEndRegex.Replace(html ?? string.Empty, Environment.NewLine);
        withBreaks = withBreaks.Replace("<br>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", Environment.NewLine, StringComparison.OrdinalIgnoreCase);
        string text = WebUtility.HtmlDecode(htmlTagRegex.Replace(withBreaks, string.Empty));
        return Regex.Replace(text, @"[ \t]+", " ").Replace("\r\n", "\n").Trim();
    }

    private static string SafeFileName(string title)
    {
        string value = string.Concat((title ?? "Project Proposal").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))).Trim();
        return string.IsNullOrWhiteSpace(value) ? "Project Proposal" : value;
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        HtmlSanitizer value = new();
        value.AllowedTags.Clear();
        foreach (string tag in new[]
        {
            "p", "br", "strong", "b", "em", "i", "u", "s", "sub", "sup", "h1", "h2", "h3", "h4",
            "ul", "ol", "li", "blockquote", "pre", "table", "thead", "tbody", "tfoot", "tr", "th", "td",
            "a", "img", "figure", "figcaption", "hr", "span", "div"
        }) value.AllowedTags.Add(tag);

        value.AllowedAttributes.Clear();
        foreach (string attribute in new[]
        {
            "href", "src", "alt", "title", "colspan", "rowspan", "width", "height", "class"
        }) value.AllowedAttributes.Add(attribute);

        value.AllowedSchemes.Clear();
        value.AllowedSchemes.Add("https");
        value.AllowedSchemes.Add("http");
        value.AllowedSchemes.Add("data");
        value.AllowDataAttributes = false;
        value.KeepChildNodes = true;
        return value;
    }

    private static class SimplePdfWriter
    {
        public static byte[] Create(string title, string content)
        {
            List<string> lines = Wrap(title, 72).Concat([""]).Concat(Wrap(content, 88)).ToList();
            const int linesPerPage = 48;
            List<List<string>> pages = lines.Chunk(linesPerPage).Select(chunk => chunk.ToList()).ToList();
            if (pages.Count == 0) pages.Add([title]);

            using MemoryStream output = new();
            using StreamWriter writer = new(output, new UTF8Encoding(false), 1024, true) { NewLine = "\n" };
            writer.WriteLine("%PDF-1.4");
            writer.Flush();

            List<long> offsets = [0];
            int pageCount = pages.Count;
            int fontObject = 3 + pageCount * 2;
            int objectCount = fontObject;

            WriteObject(writer, output, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
            string kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 2} 0 R"));
            WriteObject(writer, output, offsets, 2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

            for (int i = 0; i < pageCount; i++)
            {
                int pageObject = 3 + i * 2;
                int contentObject = pageObject + 1;
                WriteObject(writer, output, offsets, pageObject,
                    $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>");

                string stream = BuildPageStream(pages[i]);
                byte[] streamBytes = Encoding.ASCII.GetBytes(stream);
                writer.Flush();
                offsets.Add(output.Position);
                writer.WriteLine($"{contentObject} 0 obj");
                writer.WriteLine($"<< /Length {streamBytes.Length} >>");
                writer.WriteLine("stream");
                writer.Flush();
                output.Write(streamBytes, 0, streamBytes.Length);
                writer.WriteLine();
                writer.WriteLine("endstream");
                writer.WriteLine("endobj");
                writer.Flush();
            }

            WriteObject(writer, output, offsets, fontObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            long xref = output.Position;
            writer.WriteLine("xref");
            writer.WriteLine($"0 {objectCount + 1}");
            writer.WriteLine("0000000000 65535 f ");
            for (int i = 1; i <= objectCount; i++) writer.WriteLine($"{offsets[i]:D10} 00000 n ");
            writer.WriteLine("trailer");
            writer.WriteLine($"<< /Size {objectCount + 1} /Root 1 0 R >>");
            writer.WriteLine("startxref");
            writer.WriteLine(xref);
            writer.WriteLine("%%EOF");
            writer.Flush();
            return output.ToArray();
        }

        private static void WriteObject(StreamWriter writer, MemoryStream output, List<long> offsets, int id, string body)
        {
            writer.Flush();
            offsets.Add(output.Position);
            writer.WriteLine($"{id} 0 obj");
            writer.WriteLine(body);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        private static string BuildPageStream(IEnumerable<string> lines)
        {
            StringBuilder stream = new();
            stream.AppendLine("BT");
            stream.AppendLine("/F1 11 Tf");
            stream.AppendLine("50 742 Td");
            bool first = true;
            foreach (string line in lines)
            {
                if (!first) stream.AppendLine("0 -14 Td");
                stream.Append('(').Append(EscapePdf(line)).AppendLine(") Tj");
                first = false;
            }
            stream.AppendLine("ET");
            return stream.ToString();
        }

        private static IEnumerable<string> Wrap(string text, int width)
        {
            foreach (string sourceLine in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                string remaining = sourceLine.Trim();
                if (remaining.Length == 0) { yield return string.Empty; continue; }
                while (remaining.Length > width)
                {
                    int cut = remaining.LastIndexOf(' ', width);
                    if (cut <= 0) cut = width;
                    yield return remaining[..cut].Trim();
                    remaining = remaining[cut..].TrimStart();
                }
                yield return remaining;
            }
        }

        private static string EscapePdf(string value) => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Select(ch => ch <= 126 && ch >= 32 ? ch : '?')
            .Aggregate(new StringBuilder(), (builder, ch) => builder.Append(ch)).ToString();
    }
}
