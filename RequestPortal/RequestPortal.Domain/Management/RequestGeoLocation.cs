namespace RequestPortal.Domain.Management;

public enum GeoPrecision
{
    Parish = 0,
    Community = 1,
    Address = 2,
    Exact = 3
}

public sealed class RequestGeoLocation
{
    private RequestGeoLocation() { }

    public RequestGeoLocation(
        Guid requestId,
        string? sourceText,
        string parish,
        string? community,
        decimal? latitude,
        decimal? longitude,
        GeoPrecision precision,
        DateTimeOffset resolvedUtc)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(parish)) throw new ArgumentException("Parish is required.", nameof(parish));

        RequestId = requestId;
        Update(sourceText, parish, community, latitude, longitude, precision, resolvedUtc);
    }

    public Guid RequestId { get; private set; }
    public string? SourceText { get; private set; }
    public string Parish { get; private set; } = string.Empty;
    public string? Community { get; private set; }
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public GeoPrecision Precision { get; private set; }
    public DateTimeOffset ResolvedUtc { get; private set; }

    public void Update(
        string? sourceText,
        string parish,
        string? community,
        decimal? latitude,
        decimal? longitude,
        GeoPrecision precision,
        DateTimeOffset resolvedUtc)
    {
        if (string.IsNullOrWhiteSpace(parish)) throw new ArgumentException("Parish is required.", nameof(parish));
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));

        SourceText = Normalize(sourceText);
        Parish = parish.Trim();
        Community = Normalize(community);
        Latitude = latitude;
        Longitude = longitude;
        Precision = precision;
        ResolvedUtc = resolvedUtc;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
