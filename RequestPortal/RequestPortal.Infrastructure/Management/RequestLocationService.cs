using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Management;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

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

    private readonly RequestPortalDbContext dbContext;

    public RequestLocationService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<RequestLocationSnapshot?> ResolveAndSaveAsync(
        Guid requestId,
        string? locationText,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        ResolvedLocation? resolved = Resolve(locationText, latitude, longitude);
        if (resolved is null)
        {
            return null;
        }

        RequestGeoLocation? entity = await dbContext.RequestGeoLocations
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (entity is null)
        {
            entity = new RequestGeoLocation(
                requestId,
                locationText,
                resolved.Parish.Name,
                resolved.Community,
                resolved.Latitude,
                resolved.Longitude,
                resolved.Precision,
                now);
            dbContext.RequestGeoLocations.Add(entity);
        }
        else
        {
            entity.Update(
                locationText,
                resolved.Parish.Name,
                resolved.Community,
                resolved.Latitude,
                resolved.Longitude,
                resolved.Precision,
                now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSnapshot(entity);
    }

    private static ResolvedLocation? Resolve(string? locationText, double? latitude, double? longitude)
    {
        if (latitude.HasValue && longitude.HasValue && IsJamaica(latitude.Value, longitude.Value))
        {
            ParishPoint nearest = Parishes
                .OrderBy(parish => DistanceSquared((decimal)latitude.Value, (decimal)longitude.Value, parish.Latitude, parish.Longitude))
                .First();
            string? community = ExtractCommunity(locationText, nearest);
            return new ResolvedLocation(
                nearest,
                community,
                decimal.Round((decimal)latitude.Value, 6),
                decimal.Round((decimal)longitude.Value, 6),
                GeoPrecision.Exact);
        }

        if (string.IsNullOrWhiteSpace(locationText))
        {
            return null;
        }

        string normalized = Normalize(locationText);
        ParishPoint? matched = Parishes.FirstOrDefault(
            parish => parish.Aliases.Any(alias => normalized.Contains(alias, StringComparison.OrdinalIgnoreCase)));
        if (matched is null)
        {
            return null;
        }

        string? communityName = ExtractCommunity(locationText, matched);
        return new ResolvedLocation(
            matched,
            communityName,
            matched.Latitude,
            matched.Longitude,
            GeoPrecision.Parish);
    }

    private static string? ExtractCommunity(string? source, ParishPoint parish)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        string[] parts = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            string normalized = Normalize(part);
            if (parish.Aliases.Any(alias => normalized.Equals(alias, StringComparison.OrdinalIgnoreCase))) continue;
            if (normalized.Equals("jamaica", StringComparison.OrdinalIgnoreCase)) continue;
            return part.Trim();
        }
        return null;
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

    private static RequestLocationSnapshot ToSnapshot(RequestGeoLocation entity) => new(
        entity.RequestId,
        entity.SourceText,
        entity.Parish,
        entity.Community,
        entity.Latitude,
        entity.Longitude,
        (LocationPrecision)(int)entity.Precision,
        entity.ResolvedUtc);

    private sealed record ResolvedLocation(
        ParishPoint Parish,
        string? Community,
        decimal Latitude,
        decimal Longitude,
        GeoPrecision Precision);
}
