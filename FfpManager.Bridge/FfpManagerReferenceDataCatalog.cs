using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace FfpManager.Bridge;

public sealed record FfpManagerReferenceItem(long Id, string? Code, string Name);

public sealed record FfpManagerReferenceDataSnapshot(
    DateTimeOffset RefreshedUtc,
    IReadOnlyList<FfpManagerReferenceItem> ApplicationStatuses,
    IReadOnlyList<FfpManagerReferenceItem> DocumentTypes,
    IReadOnlyList<FfpManagerReferenceItem> UnitsOfMeasure);

public interface IFfpManagerReferenceDataCatalog
{
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<FfpManagerReferenceDataSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<long> ResolveApplicationStatusIdAsync(string name, CancellationToken cancellationToken = default);
    Task<long> ResolveDocumentTypeIdAsync(string name, CancellationToken cancellationToken = default);
    Task<long> ResolveUnitOfMeasureIdAsync(string name, CancellationToken cancellationToken = default);
}

public sealed class FfpManagerReferenceDataCatalog : IFfpManagerReferenceDataCatalog
{
    private readonly BridgeOptions options;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private volatile FfpManagerReferenceDataSnapshot? snapshot;

    public FfpManagerReferenceDataCatalog(IOptions<BridgeOptions> options)
    {
        this.options = options.Value;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
            IReadOnlyList<FfpManagerReferenceItem> statuses = await LoadAsync(connection, "ApplicationStatusTypes", cancellationToken);
            IReadOnlyList<FfpManagerReferenceItem> documentTypes = await LoadAsync(connection, "DocumentTypes", cancellationToken);
            IReadOnlyList<FfpManagerReferenceItem> units = await LoadAsync(connection, "UnitsOfMeasure", cancellationToken);
            snapshot = new FfpManagerReferenceDataSnapshot(DateTimeOffset.UtcNow, statuses, documentTypes, units);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task<FfpManagerReferenceDataSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        FfpManagerReferenceDataSnapshot? current = snapshot;
        TimeSpan maxAge = TimeSpan.FromSeconds(Math.Clamp(options.ReferenceDataRefreshSeconds, 30, 86400));
        if (current is null || DateTimeOffset.UtcNow - current.RefreshedUtc > maxAge)
        {
            await RefreshAsync(cancellationToken);
            current = snapshot;
        }

        return current ?? throw new InvalidOperationException("FFP Manager reference data could not be loaded.");
    }

    public async Task<long> ResolveApplicationStatusIdAsync(string name, CancellationToken cancellationToken = default) =>
        Resolve(await GetSnapshotAsync(cancellationToken), name, static item => item.ApplicationStatuses, "ApplicationStatusTypes");

    public async Task<long> ResolveDocumentTypeIdAsync(string name, CancellationToken cancellationToken = default) =>
        Resolve(await GetSnapshotAsync(cancellationToken), name, static item => item.DocumentTypes, "DocumentTypes");

    public async Task<long> ResolveUnitOfMeasureIdAsync(string name, CancellationToken cancellationToken = default) =>
        Resolve(await GetSnapshotAsync(cancellationToken), name, static item => item.UnitsOfMeasure, "UnitsOfMeasure");

    private static long Resolve(
        FfpManagerReferenceDataSnapshot snapshot,
        string value,
        Func<FfpManagerReferenceDataSnapshot, IReadOnlyList<FfpManagerReferenceItem>> selector,
        string table)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Reference-data name is required.", nameof(value));
        }

        IReadOnlyList<FfpManagerReferenceItem> items = selector(snapshot);
        FfpManagerReferenceItem? byName = items.FirstOrDefault(item =>
            string.Equals(item.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName.Id;
        }

        // Compatibility only. FFP Manager business logic is name-driven, but older portal data may still carry a code.
        FfpManagerReferenceItem? byCode = items.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.Code) &&
            string.Equals(item.Code, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byCode is not null)
        {
            return byCode.Id;
        }

        throw new InvalidOperationException($"FFP Manager lookup dbo.{table} contains no Name matching '{value}'.");
    }

    private static async Task<IReadOnlyList<FfpManagerReferenceItem>> LoadAsync(
        SqlConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        if (table is not ("ApplicationStatusTypes" or "DocumentTypes" or "UnitsOfMeasure"))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        string sql = $"SELECT ID, Code, Name FROM dbo.[{table}] ORDER BY ID;";
        await using SqlCommand command = new(sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<FfpManagerReferenceItem> items = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            long id = reader.GetInt64(0);
            string? code = reader.IsDBNull(1) ? null : reader.GetString(1);
            string name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            items.Add(new FfpManagerReferenceItem(id, code, name));
        }

        return items;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString))
        {
            throw new InvalidOperationException("Bridge:SqlConnectionString is required when Bridge:Gateway is Sql.");
        }

        return await FfpSqlConnection.OpenAsync(options.SqlConnectionString, cancellationToken);
    }
}
