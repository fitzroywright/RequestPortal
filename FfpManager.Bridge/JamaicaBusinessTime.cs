namespace FfpManager.Bridge;

/// <summary>
/// FFP Manager's legacy datetime columns store Jamaica wall time without an offset.
/// Portal contracts and the integration outbox continue to carry UTC instants.
/// </summary>
public static class JamaicaBusinessTime
{
    private static readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "SA Pacific Standard Time" : "America/Jamaica");

    public static DateTime ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, zone).DateTime;

    public static DateTimeOffset ToUtc(DateTime local)
    {
        if (local.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("A legacy database datetime must have unspecified kind.", nameof(local));

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
