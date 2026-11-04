using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Administration;
using RequestPortal.Infrastructure.Identity;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffQueuePaginationTests
{
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
