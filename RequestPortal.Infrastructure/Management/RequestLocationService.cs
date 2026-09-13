using System.Text.Json;
using Common.Storage;
using RequestPortal.Application.Management;

namespace RequestPortal.Infrastructure.Management;

internal sealed record StoredRequestLocationEnvelope(
    Guid RequestId,
    string? SourceText,
    string Parish,
    string? Community,
    decimal? Latitude,
    decimal? Longitude,
    LocationPrecision Precision,
    DateTimeOffset ResolvedUtc);

internal static class RequestLocationStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Key(Guid requestId) => $"requestportal/locations/{requestId:N}/resolved-location.json";

    public static byte[] Serialize(RequestLocationSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(
        new StoredRequestLocationEnvelope(
            snapshot.RequestId,
            snapshot.SourceText,
            snapshot.Parish,
            snapshot.Community,
            snapshot.Latitude,
            snapshot.Longitude,
            snapshot.Precision,
            snapshot.ResolvedUtc),
        JsonOptions);

    public static async Task<RequestLocationSnapshot?> ReadAsync(
        IFileStorage storage,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        string key = Key(requestId);
        StoredFile? metadata = await storage.GetMetadataAsync(key, cancellationToken);
        if (metadata is null || metadata.Status != StorageStatus.Stored) return null;

        await using Stream stream = await storage.OpenReadAsync(key, cancellationToken);
        StoredRequestLocationEnvelope? envelope = await JsonSerializer.DeserializeAsync<StoredRequestLocationEnvelope>(
            stream,
            JsonOptions,
            cancellationToken);
        if (envelope is null || envelope.RequestId != requestId || string.IsNullOrWhiteSpace(envelope.Parish)) return null;

        return new RequestLocationSnapshot(
            envelope.RequestId,
            envelope.SourceText,
            envelope.Parish,
            envelope.Community,
            envelope.Latitude,
            envelope.Longitude,
            envelope.Precision,
            envelope.ResolvedUtc);
    }
}

public sealed class RequestLocationService : IRequestLocationService
{
    private sealed record ParishPoint(string Name, decimal Latitude, decimal Longitude, string[] Aliases);

    private static readonly ParishPoint[] Parishes =
    [
        new("Kingston", 17.9712m, -76.7936m, ["kingston"]),
        new("St. Andrew", 18.0179m, -76.8099m, ["st andrew", "st. andrew", "saint andrew"]),
        new("St. Thomas", 17.9700m, -76.4330m, ["st thomas", "st. thomas", "saint thomas"]),
        new("Portland", 18.1500m, -76.4500m, ["portland"]),
        new("St. Mary", 18.3167m, -76.9000m, ["st mary", "st. mary", "saint mary"]),
        new("St. Ann", 18.3500m, -77.2000m, ["st ann", "st. ann", "saint ann"]),
        new("Trelawny", 18.3520m, -77.6070m, ["trelawny"]),
        new("St. James", 18.4762m, -77.8939m, ["st james", "st. james", "saint james"]),
        new("Hanover", 18.4098m, -78.1336m, ["hanover"]),
        new("Westmoreland", 18.2350m, -78.1336m, ["westmoreland"]),
        new("St. Elizabeth", 18.0500m, -77.7833m, ["st elizabeth", "st. elizabeth", "saint elizabeth"]),
        new("Manchester", 18.0500m, -77.5333m, ["manchester"]),
        new("Clarendon", 17.9667m, -77.2333m, ["clarendon"]),
        new("St. Catherine", 18.0000m, -77.0000m, ["st catherine", "st. catherine", "saint catherine"])
    ];

    private readonly IFileStorage storage;

    public RequestLocationService(IFileStorage storage)
    {
        this.storage = storage;
    }

    internal static RequestLocationSnapshot? ResolveExisting(Guid requestId, string? locationText)
    {
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(locationText)) return null;
        ResolvedLocation? resolved = Resolve(locationText, null, null);
        return resolved is null
            ? null
            : new RequestLocationSnapshot(
                requestId,
                NormalizeOptional(locationText),
                resolved.Parish.Name,
                NormalizeOptional(resolved.Community),
                resolved.Latitude,
                resolved.Longitude,
                resolved.Precision,
                DateTimeOffset.MinValue);
    }

    public async Task<RequestLocationSnapshot?> ResolveAndSaveAsync(
        Guid requestId,
        string? locationText,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));

        ResolvedLocation? resolved = Resolve(locationText, latitude, longitude);
        if (resolved is null) return null;

        RequestLocationSnapshot snapshot = new(
            requestId,
            NormalizeOptional(locationText),
            resolved.Parish.Name,
            NormalizeOptional(resolved.Community),
            resolved.Latitude,
            resolved.Longitude,
            resolved.Precision,
            DateTimeOffset.UtcNow);

        byte[] payload = RequestLocationStorage.Serialize(snapshot);
        await using MemoryStream stream = new(payload, writable: false);
        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RequestId"] = requestId.ToString("D"),
            ["Parish"] = snapshot.Parish,
            ["Precision"] = snapshot.Precision.ToString()
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Community)) metadata["Community"] = snapshot.Community;

        await storage.StoreAsync(
            new StorageWriteRequest(
                RequestLocationStorage.Key(requestId),
                stream,
                "application/json",
                "resolved-location.json",
                "RequestPortal.LocationResolver",
                metadata,
                MaximumBytes: 32 * 1024),
            cancellationToken);

        return snapshot;
    }

    private static ResolvedLocation? Resolve(string? locationText, double? latitude, double? longitude)
    {
        if (latitude.HasValue && longitude.HasValue && IsJamaica(latitude.Value, longitude.Value))
        {
            ParishPoint nearest = Parishes
                .OrderBy(parish => DistanceSquared((decimal)latitude.Value, (decimal)longitude.Value, parish.Latitude, parish.Longitude))
                .First();
            string? community = ParseTextLocation(locationText, nearest).Community;
            return new ResolvedLocation(
                nearest,
                community,
                decimal.Round((decimal)latitude.Value, 6),
                decimal.Round((decimal)longitude.Value, 6),
                LocationPrecision.Exact);
        }

        if (string.IsNullOrWhiteSpace(locationText)) return null;

        string normalized = Normalize(locationText);
        ParishPoint? matched = Parishes.FirstOrDefault(
            parish => parish.Aliases.Any(alias => normalized.Contains(alias, StringComparison.OrdinalIgnoreCase)));
        if (matched is null) return null;

        ParsedTextLocation parsed = ParseTextLocation(locationText, matched);
        return new ResolvedLocation(matched, parsed.Community, null, null, parsed.Precision);
    }

    private static ParsedTextLocation ParseTextLocation(string? source, ParishPoint parish)
    {
        if (string.IsNullOrWhiteSpace(source)) return new ParsedTextLocation(null, LocationPrecision.Parish);

        string[] geographicParts = source
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part =>
            {
                string normalized = Normalize(part);
                return !parish.Aliases.Any(alias => normalized.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    && !normalized.Equals("jamaica", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        if (geographicParts.Length == 0) return new ParsedTextLocation(null, LocationPrecision.Parish);
        if (geographicParts.Length == 1) return new ParsedTextLocation(geographicParts[0], LocationPrecision.Community);
        return new ParsedTextLocation(geographicParts[^1], LocationPrecision.Address);
    }

    private static bool IsJamaica(double latitude, double longitude) =>
        latitude is >= 17.60 and <= 18.65 && longitude is >= -78.55 and <= -75.80;

    private static decimal DistanceSquared(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        decimal dLat = lat1 - lat2;
        decimal dLon = lon1 - lon2;
        return (dLat * dLat) + (dLon * dLon);
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace("saint ", "st ").Replace("st. ", "st ");

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ParsedTextLocation(string? Community, LocationPrecision Precision);

    private sealed record ResolvedLocation(
        ParishPoint Parish,
        string? Community,
        decimal? Latitude,
        decimal? Longitude,
        LocationPrecision Precision);
}
