namespace RequestPortal.Application.Management;

public sealed record QuickIntakeRange(DateOnly From, DateOnly Through)
{
    public DateTimeOffset StartUtc => new(From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public DateTimeOffset EndUtc => new(Through.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    public void Validate()
    {
        if (Through < From || Through.DayNumber - From.DayNumber > 365)
            throw new ArgumentException("Choose a date range of up to 366 days, with the start before the end.");
    }
}
public sealed record QuickIntakeCount(string Name, int? Count, decimal? Percent);
public sealed record QuickIntakeDistribution(IReadOnlyList<QuickIntakeCount> Items, bool Suppressed, string? Unavailable = null);
public sealed record QuickIntakeDashboardSnapshot(QuickIntakeRange Range, int Total, int SentToFfpManager,
    IReadOnlyList<QuickIntakeCount> Parishes, IReadOnlyList<QuickIntakeCount> RequestTypes,
    QuickIntakeDistribution ChildrenUnder18, QuickIntakeDistribution Employment, QuickIntakeDistribution Income,
    QuickIntakeDistribution Gender, QuickIntakeDistribution Ages, IReadOnlyList<string> Insights)
{
    public int NotVerifiedInFfpManager => Total - SentToFfpManager;
    public QuickIntakeDistribution WeeklyIncome { get; init; } = new([], false, "Weekly income unavailable.");
    public int? AwaitingScreening => null;
    public int? FollowUpRequired => null;
    public int? ScreeningComplete => null;
    public int? NewApplicants => null;
}
public sealed record QuickIntakeRecordLink(Guid RequestId, string Reference, string Parish, DateTimeOffset ReceivedUtc);
public interface IQuickIntakeDashboardService
{
    Task<QuickIntakeDashboardSnapshot> GetAsync(QuickIntakeRange range, CancellationToken ct = default, string? parish = null);
    Task<IReadOnlyList<QuickIntakeRecordLink>> GetRecordsAsync(QuickIntakeRange range, string? parish, int page, CancellationToken ct = default);
}
