using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerApplicationNumberMigrationTests
{
    [Fact]
    public async Task ApprovedMigrationAddsOnlyNullableApplicationNumberAndHistoryEntry()
    {
        var token=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(token,"20261014170000_AddIntakeApplicantProfileFields");
        var migrator=database.Db.GetService<IMigrator>();
        Assert.Contains("20261014190000_AddFfpManagerApplicationNumber",await database.Db.Database.GetPendingMigrationsAsync(token));
        await database.Db.Database.OpenConnectionAsync(token);
        async Task<List<string>> Snapshot()
        {
            await using var command=database.Db.Database.GetDbConnection().CreateCommand();
            command.CommandText="""
                SELECT 'COLUMN:'||table_name||':'||column_name||':'||data_type||':'||is_nullable
                FROM information_schema.columns WHERE table_schema=current_schema()
                UNION ALL
                SELECT 'INDEX:'||tablename||':'||indexname||':'||indexdef FROM pg_indexes WHERE schemaname=current_schema()
                UNION ALL
                SELECT 'CONSTRAINT:'||conrelid::regclass::text||':'||conname||':'||pg_get_constraintdef(oid)
                FROM pg_constraint WHERE connamespace=current_schema()::regnamespace
                ORDER BY 1
                """;
            await using var reader=await command.ExecuteReaderAsync(token);
            var rows=new List<string>();while(await reader.ReadAsync(token))rows.Add(reader.GetString(0));return rows;
        }
        var before=await Snapshot();
        await migrator.MigrateAsync("20261014190000_AddFfpManagerApplicationNumber",token);
        var after=await Snapshot();
        Assert.Empty(before.Except(after));
        Assert.Equal(["COLUMN:integration_links:ApplicationNumber:text:YES"],after.Except(before));
        Assert.DoesNotContain("20261014190000_AddFfpManagerApplicationNumber",await database.Db.Database.GetPendingMigrationsAsync(token));
    }
}
