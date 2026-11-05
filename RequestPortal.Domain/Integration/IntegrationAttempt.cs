namespace RequestPortal.Domain.Integration;

/// <summary>Append-only outcome history. Incomplete attempts survive process termination.</summary>
public sealed class IntegrationAttempt
{
    private IntegrationAttempt() { }
    public IntegrationAttempt(Guid linkId, int number, DateTimeOffset now, string operation = "Submission")
    {
        Id = Guid.NewGuid(); LinkId = linkId; Number = number; StartedUtc = now; Operation = operation;
    }
    public Guid Id { get; private set; }
    public Guid LinkId { get; private set; }
    public int Number { get; private set; }
    public string Operation { get; private set; } = "Submission";
    public DateTimeOffset StartedUtc { get; private set; }
    public DateTimeOffset? CompletedUtc { get; private set; }
    public bool Succeeded { get; private set; }
    public string Stage { get; private set; } = "Preparation";
    public string? Classification { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? Explanation { get; private set; }
    // Restricted operator diagnostics; never included in ordinary integration search results.
    public string? ExceptionDetails { get; private set; }
    public void SetExceptionDetails(string? details) => ExceptionDetails = details;
    public void SetStage(string stage) => Stage = stage;
    public void Complete(DateTimeOffset now) { CompletedUtc = now; Succeeded = true; }
    public void Fail(DateTimeOffset now, string stage, string classification, string code, string explanation)
    { CompletedUtc = now; Stage = stage; Classification = classification; ErrorCode = code; Explanation = explanation; }
}
