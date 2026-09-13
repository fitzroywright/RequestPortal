namespace RequestPortal.Application.Requests;

public sealed record ProjectProposalDetail(
    Guid RequestId,
    string PublicReference,
    string Title,
    string RichContentHtml,
    int Version,
    DateTimeOffset UpdatedUtc,
    bool CanEdit);

public sealed record ProjectProposalRevisionDetail(
    Guid Id,
    int Version,
    string Title,
    DateTimeOffset SavedUtc,
    bool Automatic,
    Guid EditedByUserId);

public sealed record LongFormTemplate(
    string Key,
    string Name,
    string RequestType,
    string Title,
    string RichContentHtml);

public sealed record ProjectProposalExport(
    string FileName,
    string ContentType,
    byte[] Content);

public interface IProjectProposalService
{
    Task InitializeProjectAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default);

    Task<ProjectProposalDetail?> GetAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default);

    Task SaveAsync(
        Guid userId,
        Guid requestId,
        string title,
        string richContentHtml,
        CancellationToken cancellationToken = default);

    Task SaveAutosaveAsync(
        Guid userId,
        Guid requestId,
        string title,
        string richContentHtml,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectProposalRevisionDetail>> GetRevisionsAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task RestoreRevisionAsync(
        Guid userId,
        Guid requestId,
        Guid revisionId,
        CancellationToken cancellationToken = default);

    IReadOnlyList<LongFormTemplate> GetTemplates(string requestType = "Project");

    Task ApplyTemplateAsync(
        Guid userId,
        Guid requestId,
        string templateKey,
        CancellationToken cancellationToken = default);

    Task<ProjectProposalExport> ExportDocxAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task<ProjectProposalExport> ExportPdfAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default);
}
