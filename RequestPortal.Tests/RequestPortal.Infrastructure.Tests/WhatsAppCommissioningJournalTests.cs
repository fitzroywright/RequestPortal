using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Web.Services.WhatsAppCommissioning;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class WhatsAppCommissioningJournalTests
{
    [Fact]
    public async Task AtomicClaimAndDeliveryCorrelationUseExistingProcessedJournalWithoutQueueDispatch()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(ct);
        var journal=new CommissioningJournal(database.Db);
        var actor=Guid.NewGuid(); var now=DateTimeOffset.UtcNow;
        var test=new CommissioningTest(Guid.NewGuid(),actor,"protected-fixture","v23.0",now,now.AddMinutes(10));
        await journal.CreateAsync(test,ct);
        var row=await database.Db.OutboxMessages.AsNoTracking().SingleAsync(x=>x.Id==test.Id,ct);
        Assert.NotNull(row.ProcessedUtc);Assert.True(row.IsTerminal);Assert.Equal(0,row.AttemptCount);
        await using var sibling=database.CreateSiblingContext();
        var other=new CommissioningJournal(sibling);
        var claims=await Task.WhenAll(journal.ClaimAsync(test.Id,actor,now,ct),other.ClaimAsync(test.Id,actor,now,ct));
        Assert.Single(claims,x=>x is not null);
        var claimed=claims.Single(x=>x is not null)!;
        await journal.CompleteAsync(claimed with { State="Accepted",Result=new("Accepted",200,MessageId:"wamid.fixture"),CompletedUtc=now },ct);
        Assert.Null(await other.ClaimAsync(test.Id,actor,now,ct));
        Assert.Null(await journal.ReadAsync(test.Id,Guid.NewGuid(),ct));
        Assert.Equal(3,await database.Db.AuditEntries.CountAsync(x=>x.EntityId==test.Id,ct));
        var router=MessageNavigationPageTests.Proxy.For<global::Common.Diagnostics.IDiagnosticOperationsRouter>((_,_)=>Task.CompletedTask);
        var webhook=new WhatsAppWebhookStore(database.Db,router);
        var delivery=new WhatsAppWebhookEvent(CommissioningGateway.Account,CommissioningGateway.Phone,"wamid.fixture","status","delivered","123",[]);
        await webhook.AcceptAsync([delivery,delivery],ct);
        await webhook.AcceptAsync([delivery with { MessageId="wamid.other" },delivery with { AccountId="other" }],ct);
        var matches=await journal.DeliveryAsync("wamid.fixture",ct);
        Assert.Equal("delivered",Assert.Single(matches).State);
        Assert.Empty(Assert.Single(matches).ErrorCodes);
    }
    [Fact]
    public async Task ExpiredAndOtherActorCannotClaim()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(ct);
        var journal=new CommissioningJournal(database.Db);var now=DateTimeOffset.UtcNow;var actor=Guid.NewGuid();
        var expired=new CommissioningTest(Guid.NewGuid(),actor,"fixture","v23.0",now.AddMinutes(-20),now.AddMinutes(-10));
        await journal.CreateAsync(expired,ct);
        Assert.Null(await journal.ClaimAsync(expired.Id,actor,now,ct));
        var valid=expired with {Id=Guid.NewGuid(),ExpiresUtc=now.AddMinutes(10)};
        await journal.CreateAsync(valid,ct);
        Assert.Null(await journal.ClaimAsync(valid.Id,Guid.NewGuid(),now,ct));
    }
}
