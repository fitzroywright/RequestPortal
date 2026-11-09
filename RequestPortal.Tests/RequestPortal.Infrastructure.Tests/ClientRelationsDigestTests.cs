using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Messaging;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Persistence;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ClientRelationsDigestTests
{
    [Theory]
    [InlineData("2026-11-09T12:59:59Z", false)]
    [InlineData("2026-11-09T13:00:00Z", true)]
    [InlineData("2026-11-09T21:00:00Z", true)]
    [InlineData("2026-11-09T22:00:00Z", false)]
    [InlineData("2026-11-14T13:00:00Z", false)]
    [InlineData("2026-11-15T13:00:00Z", false)]
    public void ScheduleUsesJamaicaWeekdays(string utc, bool expected) =>
        Assert.Equal(expected, ClientRelationsDigest.SlotFor(DateTimeOffset.Parse(utc)).HasValue);

    [Fact]
    public async Task WeekendBacklogIsBatchedOnceAndNewItemsWaitForNextHour()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        var db = database.Db;
        var monday = DateTimeOffset.Parse("2026-11-09T13:00:00Z");
        Add(monday.AddDays(-2));
        Add(monday.AddDays(-1));
        await db.SaveChangesAsync(token);
        await ClientRelationsDigest.QueueAsync(db, monday.AddDays(-1), token);
        Assert.Equal(2, await db.OutboxMessages.CountAsync(token));
        await ClientRelationsDigest.QueueAsync(db, monday, token);
        var first = await db.OutboxMessages.SingleAsync(m => m.MessageType == typeof(ClientRelationsDigest).FullName, token);
        Assert.Equal(2, JsonSerializer.Deserialize<ClientRelationsDigest>(first.Payload)!.Items.Length);
        Add(monday.AddMinutes(1));
        await db.SaveChangesAsync(token);
        await ClientRelationsDigest.QueueAsync(db, monday.AddMinutes(30), token);
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.MessageType == typeof(ClientRelationsDigest).FullName, token));
        await ClientRelationsDigest.QueueAsync(db, monday.AddHours(1), token);
        Assert.Equal(2, await db.OutboxMessages.CountAsync(m => m.MessageType == typeof(ClientRelationsDigest).FullName, token));
        await ClientRelationsDigest.QueueAsync(db, monday.AddHours(2), token);
        Assert.Equal(2, await db.OutboxMessages.CountAsync(m => m.MessageType == typeof(ClientRelationsDigest).FullName, token));

        void Add(DateTimeOffset occurred)
        {
            var id = Guid.NewGuid();
            var item = new QuickIntakeClientRelationsEmailRequestedMessage(id, occurred, Guid.NewGuid(), "RP-TEST", "FFP-TEST");
            db.OutboxMessages.Add(new OutboxMessage { Id = id, OccurredUtc = occurred,
                MessageType = typeof(QuickIntakeClientRelationsEmailRequestedMessage).FullName!, Payload = JsonSerializer.Serialize(item) });
        }
    }
}
