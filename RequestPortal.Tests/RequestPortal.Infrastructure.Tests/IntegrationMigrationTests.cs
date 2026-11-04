using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Common.Messaging;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class IntegrationMigrationTests
{
    [Fact]
    public async Task UpgradePreservesExistingPayloadOutcomeAndRetrySchedule_AndSupportsLegacyWriters()
    {
        var token=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(token,"20261104070000_ApplicantIdentityRecovery");
        Guid id=Guid.NewGuid(),request=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var retry=now.AddMinutes(10);
        string payload=JsonSerializer.Serialize(new FfpManagerSubmissionRequestedMessage(id,now,request));
        string type=typeof(FfpManagerSubmissionRequestedMessage).FullName!;
        string error=$"RETRY_AT={retry:O}|Synthetic transient failure";
        await database.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id","MessageType","Payload","OccurredUtc","AttemptCount","LastError")
            VALUES ({id},{type},{payload}::jsonb,{now},{3},{error})
            """,token);
        await database.Db.GetService<IMigrator>().MigrateAsync(cancellationToken:token);
        var migrated=await database.Db.OutboxMessages.SingleAsync(item=>item.Id==id,token);
        Assert.Equal(request,migrated.RequestId);Assert.Equal(3,migrated.AttemptCount);Assert.Equal(error,migrated.LastError);
        Assert.Null(migrated.ProcessedUtc);Assert.False(migrated.IsTerminal);
        Assert.InRange(migrated.NextAttemptUtc!.Value,retry.AddMilliseconds(-1),retry.AddMilliseconds(1));
        Assert.Equal(request,JsonSerializer.Deserialize<FfpManagerSubmissionRequestedMessage>(migrated.Payload)!.RequestId);
        Guid legacyId=Guid.NewGuid();
        await database.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id","MessageType","Payload","OccurredUtc","AttemptCount")
            VALUES ({legacyId},{type},{payload}::jsonb,{now},{0})
            """,token);
        Assert.False((await database.Db.OutboxMessages.SingleAsync(item=>item.Id==legacyId,token)).IsTerminal);
    }
}
