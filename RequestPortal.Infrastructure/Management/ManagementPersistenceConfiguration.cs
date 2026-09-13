namespace RequestPortal.Infrastructure.Management;

public sealed class ManagementDashboardOptions
{
    public const string SectionName = "ManagementDashboard";
    public int NewRequestHours { get; set; } = 48;
    public int OverdueDays { get; set; } = 14;
    public int StalledDays { get; set; } = 7;
    public int TrendWeeks { get; set; } = 12;
}
