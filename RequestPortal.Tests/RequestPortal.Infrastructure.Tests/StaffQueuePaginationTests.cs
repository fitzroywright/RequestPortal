using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Administration;
using RequestPortal.Infrastructure.Identity;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffQueuePaginationTests
{
    [Fact]
    public async Task ApplicantFilterMatchesNamesOrLiteralRequestNumbersBeforePaginationWithOtherFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Alicia", LastName = "Campbell", DisplayName = "Ali", CreatedUtc = now };
        db.Users.Add(user);
        var org = new Organization(Guid.NewGuid(), "Search fixture organization", "JM", user.Id, now);
        var otherOrg = new Organization(Guid.NewGuid(), "Other organization", "JM", user.Id, now);
        db.Organizations.AddRange(org, otherOrg);
        AssistanceRequest Add(string reference, Organization organization, bool submitted = true)
        {
            var request = new AssistanceRequest(Guid.NewGuid(), reference, organization.Id, user.Id, now);
            request.SetKind(RequestKind.Project);
            if (submitted) request.Submit(user.Id, now);
            db.AssistanceRequests.Add(request);
            return request;
        }
        for (int i = 0; i < 30; i++) Add($"REQ-2026-{i:D4}", org);
        Add("REQ-2026-OTHER", otherOrg);
        Add("REQ-2026-DRAFT", org, false);
        Add("REQ-LITERAL%_", org);
        await db.SaveChangesAsync(ct);
        var service = new StaffRequestSearchService(db);

        foreach (string term in new[] { "alicia campbell", " CAMPBELL ", "Ali", "req-2026-", "2026" })
        {
            var first = await service.GetFilteredQueueAsync(term, "search fixture", RequestStatus.Submitted, 1, 25, ct);
            var second = await service.GetFilteredQueueAsync(term, "search fixture", RequestStatus.Submitted, 2, 25, ct);
            Assert.Equal(term.Contains("2026") ? 30 : 31, first.TotalCount);
            Assert.Equal(25, first.Items.Count);
            Assert.Equal(first.TotalCount - 25, second.Items.Count);
            Assert.Empty(first.Items.Select(r => r.RequestId).Intersect(second.Items.Select(r => r.RequestId)));
            Assert.All(first.Items.Concat(second.Items), r => {
                Assert.Equal(org.Name, r.OrganizationName);
                Assert.Equal(RequestStatus.Submitted, r.Status);
            });
        }
        foreach (string term in new[] { "REQ-2026-0029", "req-2026-0029", "0029" })
        {
            var result = await service.GetFilteredQueueAsync(term, null, null, 1, 25, ct);
            Assert.Equal("REQ-2026-0029", Assert.Single(result.Items).PublicReference);
            Assert.Equal(1, result.TotalCount);
        }
        Assert.Single((await service.GetFilteredQueueAsync("%_", null, null, cancellationToken: ct)).Items);
        Assert.Empty((await service.GetFilteredQueueAsync("missing", null, null, cancellationToken: ct)).Items);
        Assert.Empty((await service.GetFilteredQueueAsync("0029", "other", null, cancellationToken: ct)).Items);
        Assert.Empty((await service.GetFilteredQueueAsync("0029", null, RequestStatus.Draft, cancellationToken: ct)).Items);
        Assert.Single((await service.GetFilteredQueueAsync("2026-DRAFT", null, RequestStatus.Draft, cancellationToken: ct)).Items);
        Assert.Equal(31, (await service.GetFilteredQueueAsync("2026", null, null, cancellationToken: ct)).TotalCount);
    }

    [Theory]
    [InlineData(46,25)][InlineData(76,25)][InlineData(76,50)][InlineData(126,100)]
    public async Task EverySubmittedRequestIsReachableExactlyOnceInReceivedOrder(int count,int size)
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);var db=fixture.Db;var now=DateTimeOffset.UtcNow;
        var user=new ApplicationUser{Id=Guid.NewGuid(),FirstName="Queue",LastName="Applicant",DisplayName="Queue Applicant",CreatedUtc=now};db.Users.Add(user);
        var org=new Organization(Guid.NewGuid(),"Queue fixture organization","JM",user.Id,now);db.Organizations.Add(org);
        var expected=new List<AssistanceRequest>();
        for(int i=0;i<count;i++)
        {
            var request=new AssistanceRequest(Guid.NewGuid(),"QUEUE-"+i.ToString("D4"),org.Id,user.Id,now.AddDays(-100+i));
            request.SetKind(RequestKind.Project);request.SetSummary("Retained queue summary "+i);
            // Deliberately differ from creation order; ties must stay deterministic.
            request.Submit(user.Id,now.AddDays(-(i%4)));db.AssistanceRequests.Add(request);expected.Add(request);
        }
        var draft=new AssistanceRequest(Guid.NewGuid(),"QUEUE-DRAFT",org.Id,user.Id,now);db.AssistanceRequests.Add(draft);
        await db.SaveChangesAsync(ct);
        var service=new StaffRequestSearchService(db);var ids=new List<Guid>();int pages=(int)Math.Ceiling(count/(double)size);
        for(int page=1;page<=pages;page++)
        {
            var result=await service.GetSubmittedQueueAsync(page,size,ct);
            Assert.Equal(count,result.TotalCount);Assert.Equal(pages,result.TotalPages);Assert.Equal(page,result.PageNumber);Assert.Equal(size,result.PageSize);
            Assert.Equal((page-1)*size+1,result.FirstRecord);Assert.Equal(Math.Min(page*size,count),result.LastRecord);
            Assert.Equal(Math.Min(size,count-(page-1)*size),result.Items.Count);
            Assert.All(result.Items,row=>{Assert.Equal(RequestStatus.Submitted,row.Status);Assert.Equal("Queue Applicant",row.ApplicantName);Assert.StartsWith("Retained queue summary",row.Summary);});
            ids.AddRange(result.Items.Select(r=>r.RequestId));
            Assert.Equal(result.Items.Select(r=>r.RequestId),(await service.GetSubmittedQueueAsync(page,size,ct)).Items.Select(r=>r.RequestId));
        }
        Assert.Equal(count,ids.Distinct().Count());
        Assert.Equal(expected.OrderByDescending(r=>r.SubmittedUtc).ThenBy(r=>r.PublicReference).ThenBy(r=>r.Id).Select(r=>r.Id),ids);
        Assert.DoesNotContain(draft.Id,ids);
        Assert.Equal(pages,(await service.GetSubmittedQueueAsync(int.MaxValue,size,ct)).PageNumber);
        Assert.Equal(1,(await service.GetSubmittedQueueAsync(-2,size,ct)).PageNumber);
        Assert.Equal(25,(await service.GetSubmittedQueueAsync(1,int.MaxValue,ct)).PageSize);
        // Search remains global, including drafts, and paginates after name filtering.
        var searched=new List<Guid>();
        for(int page=1;page<=Math.Ceiling((count+1)/(double)size);page++)
        {
            var result=await service.SearchAsync("queue applicant",page,size,ct);Assert.Equal(count+1,result.TotalCount);searched.AddRange(result.Items.Select(r=>r.RequestId));
        }
        Assert.Equal(count+1,searched.Distinct().Count());Assert.Contains(draft.Id,searched);
        Assert.Equal(expected.Last().Id,(await service.SearchAsync(expected.Last().PublicReference.ToLowerInvariant(),1,size,ct)).ExactReferenceId);
    }

    [Fact]
    public async Task EmptyQueueHasZeroRangeAndOnePage()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var result=await new StaffRequestSearchService(fixture.Db).GetSubmittedQueueAsync(99,50,ct);
        Assert.Empty(result.Items);Assert.Equal(0,result.TotalCount);Assert.Equal(0,result.FirstRecord);Assert.Equal(0,result.LastRecord);Assert.Equal(1,result.PageNumber);Assert.Equal(1,result.TotalPages);
    }
}
