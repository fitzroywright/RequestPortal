using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

/// <summary>A session lock spans independent durable checkpoints and the HTTP exchange.</summary>
internal sealed class IntegrationRequestLock(RequestPortalDbContext db, long key, bool opened) : IAsyncDisposable
{
    public static async Task<IntegrationRequestLock> AcquireAsync(RequestPortalDbContext db, Guid requestId, CancellationToken token)
    {
        bool opened = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(token);
        long key = BitConverter.ToInt64(requestId.ToByteArray(), 0);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            command.Parameters.Add(new NpgsqlParameter("key", key));
            if (!Equals(await command.ExecuteScalarAsync(token), true))
                throw new IntegrationTransferException(TransferFailures.For("Concurrency", "CONNECTION"));
            return new(db, key, opened);
        }
        catch { if (opened) await db.Database.CloseConnectionAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (db.Database.GetDbConnection().State == ConnectionState.Open)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                command.Parameters.Add(new NpgsqlParameter("key", key));
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
