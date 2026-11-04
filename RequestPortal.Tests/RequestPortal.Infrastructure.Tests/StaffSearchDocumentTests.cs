using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Documents;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Administration;
using RequestPortal.Infrastructure.Identity;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffSearchDocumentTests
{
    private sealed record Clock(DateTimeOffset UtcNow) : IClock;
    public class Audit : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Task.CompletedTask;
    }

    private sealed class CapturingAudit : IBusinessAuditWriter
    {
        public List<string> Actions { get; } = [];
        public Task WriteAsync(Guid actorUserId, string action, string entityType, Guid entityId, Guid? organizationId, string? detail = null, CancellationToken cancellationToken = default)
        { Actions.Add(action); return Task.CompletedTask; }
    }

    [Fact]
    public async Task SearchUsesPersistedApplicantIdentitiesAcrossStatusesAndPaginatesWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var db = fixture.Db;
        var now = DateTimeOffset.UtcNow;
        ApplicationUser User(string first, string last, string display)
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = first, LastName = last, DisplayName = display, UserName = Guid.NewGuid().ToString(), CreatedUtc = now };
            db.Users.Add(user); return user;
        }
        var john = User("John", "Brown", "Johnny");
        var duplicate = User("John", "Brown", "Other Brown");
        var creator = User("Record", "Creator", "Creator");
        var representative = User("Marcia", "Williams", "Representative display");
        var anonymous = User("", "", "Anonymous technical account");
        var converted = User("Jane", "Married", "Jane registered");
        var org = new Organization(Guid.NewGuid(), "Organization Brown must not match", "JM", creator.Id, now);
        db.Organizations.Add(org);
        AssistanceRequest Request(string reference, Guid owner, Guid? submitter = null)
        {
            var request = new AssistanceRequest(Guid.NewGuid(), reference, org.Id, owner, now);
            request.SetKind(RequestKind.Project); request.SetSummary("Fixture");
            if (submitter.HasValue) request.Submit(submitter.Value, now);
            db.AssistanceRequests.Add(request); return request;
        }
        var exact = Request("REQ-10452", john.Id, john.Id);
        exact.Reject(creator.Id, now, "Fixture decision");
        for (int i = 0; i < 28; i++) Request("REQ-PAGE-" + i.ToString("D2"), i % 2 == 0 ? john.Id : duplicate.Id);
        var representativeRequest = Request("REQ-REP", creator.Id, representative.Id);
        var intakeRequest = Request("REQ-INTAKE", anonymous.Id);
        var linkedRequest = Request("REQ-LINKED", anonymous.Id);
        QuickIntakeRecord Intake(AssistanceRequest request, string first, string last) => new(Guid.NewGuid(), request.Id, anonymous.Id, org.Id, first, last, new(1980, 1, 1), "Female", "Single", "+18765550101", null, "Fixture street", "Fixture district", "Kingston", "Unemployed", 0, false, false, "None", "Food", now);
        var intake = Intake(intakeRequest, "Alicia", "Campbell");
        var linked = Intake(linkedRequest, "Jane", "Campbell");
        linked.MarkConverted(converted.Id, creator.Id, now); linkedRequest.ConvertApplicant(converted.Id);
        db.QuickIntakes.AddRange(intake, linked);
        var literal = User("Literal%_", "Person", "Literal%_ Person"); Request("REQ-LITERAL", literal.Id);
        await db.SaveChangesAsync(ct);
        var service = new StaffRequestSearchService(db);
        foreach (string value in new[] { "REQ-10452", "req-10452", "  req-10452  " })
            Assert.Equal(exact.Id, (await service.SearchAsync(value, cancellationToken: ct)).ExactReferenceId);
        foreach (string value in new[] { "John", "brown", " JOHN   BROWN ", "ohn bro" })
        {
            var result = await service.SearchAsync(value, cancellationToken: ct);
            Assert.Equal(29, result.TotalCount); Assert.Equal(25, result.Items.Count);
            var second = await service.SearchAsync(value, 2, ct);
            Assert.Equal(4, second.Items.Count); Assert.Empty(result.Items.Select(x => x.RequestId).Intersect(second.Items.Select(x => x.RequestId)));
            Assert.Equal(result.Items.Select(x => x.RequestId), (await service.SearchAsync(value, 1, ct)).Items.Select(x => x.RequestId));
            Assert.Contains(exact.Id, result.Items.Concat(second.Items).Select(x => x.RequestId)); // Declined is searchable too.
        }
        Assert.Equal(representativeRequest.Id, Assert.Single((await service.SearchAsync("Marcia Williams", cancellationToken: ct)).Items).RequestId);
        Assert.DoesNotContain(representativeRequest.Id, (await service.SearchAsync("Record Creator", cancellationToken: ct)).Items.Select(x => x.RequestId));
        var campbells = await service.SearchAsync("Campbell", cancellationToken: ct);
        Assert.Equal(2, campbells.TotalCount);
        Assert.True(Assert.Single(campbells.Items, x => x.RequestId == intakeRequest.Id).IsUnverifiedQuickIntake);
        Assert.False(Assert.Single(campbells.Items, x => x.RequestId == linkedRequest.Id).IsUnverifiedQuickIntake);
        Assert.Equal(linkedRequest.Id, Assert.Single((await service.SearchAsync("Jane Married", cancellationToken: ct)).Items).RequestId);
        Assert.Single((await service.SearchAsync("Literal%_", cancellationToken: ct)).Items);
        Assert.Equal(0, (await service.SearchAsync("does-not-exist", cancellationToken: ct)).TotalCount);
        Assert.Equal(0, (await service.SearchAsync("Organization Brown", cancellationToken: ct)).TotalCount);
        Assert.Equal(2, (await service.SearchAsync("Brown", int.MaxValue, ct)).PageNumber);
        var applicants = await service.GetApplicantsAsync([representativeRequest.Id, intakeRequest.Id, linkedRequest.Id], ct);
        Assert.Equal("Marcia Williams", applicants[representativeRequest.Id].ApplicantName);
        Assert.Equal("Jane Campbell", applicants[linkedRequest.Id].ApplicantName);
    }

    [Fact]
    public async Task DocumentQueueUsesPolymorphicOwnerAndGuardsMissingArchivedOrInconsistentContexts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var db = fixture.Db; var now = DateTimeOffset.UtcNow; var actor = Guid.NewGuid();
        var org = new Organization(Guid.NewGuid(), "Actual organization", "JM", actor, now);
        var context = new Organization(Guid.NewGuid(), "Internal individual context", "JM", actor, now);
        db.Organizations.AddRange(org, context);
        var request = new AssistanceRequest(Guid.NewGuid(), "REQ-DOC", context.Id, actor, now);
        db.AssistanceRequests.Add(request);
        db.QuickIntakes.Add(new(Guid.NewGuid(), request.Id, actor, context.Id, "Intake", "Applicant", new(1980, 1, 1), "Female", "Single", "+18765550101", null, "Fixture street", "Fixture district", "Kingston", "Unemployed", 0, false, false, "None", "Food", now));
        StoredDocument Doc(DocumentOwnerType type, Guid owner, Guid organization) => new(Guid.NewGuid(), type, owner, organization, DocumentType.RequestSupport, "evidence.pdf", "application/pdf", 12, Guid.NewGuid().ToString(), actor, now);
        var organizationDoc = Doc(DocumentOwnerType.Organization, org.Id, org.Id);
        var requestDoc = Doc(DocumentOwnerType.AssistanceRequest, request.Id, context.Id);
        var inconsistent = Doc(DocumentOwnerType.AssistanceRequest, request.Id, org.Id);
        var missing = Doc(DocumentOwnerType.AssistanceRequest, Guid.NewGuid(), org.Id);
        var archived = Doc(DocumentOwnerType.AssistanceRequest, request.Id, context.Id); archived.MarkArchived(now);
        var wrongOrganization = Doc(DocumentOwnerType.Organization, context.Id, org.Id);
        db.Documents.AddRange(organizationDoc, requestDoc, inconsistent, missing, archived, wrongOrganization);
        await db.SaveChangesAsync(ct);
        var operations = new StaffOperationsService(db, new Clock(now), DispatchProxy.Create<IBusinessAuditWriter, Audit>(), new ConfigurationBuilder().Build());
        var queue = (await operations.GetSnapshotAsync(ct)).DocumentsAwaitingVerification;
        Assert.Equal(5, queue.Count);
        var orgItem = Assert.Single(queue, x => x.DocumentId == organizationDoc.Id);
        Assert.Null(orgItem.RequestId); Assert.True(orgItem.HasValidContext);
        var requestItem = Assert.Single(queue, x => x.DocumentId == requestDoc.Id);
        Assert.Equal(request.Id, requestItem.RequestId); Assert.Equal("Intake Applicant", requestItem.ApplicantName); Assert.True(requestItem.HasValidContext);
        foreach (var doc in new[] { inconsistent, missing, wrongOrganization }) Assert.False(Assert.Single(queue, x => x.DocumentId == doc.Id).HasValidContext);
        var guard = new StaffDocumentContextService(db);
        Assert.Equal(StaffDocumentContextStatus.Available, await guard.ValidateAsync(DocumentOwnerType.Organization, org.Id, organizationDoc.Id, ct));
        Assert.Equal(StaffDocumentContextStatus.Available, await guard.ValidateAsync(DocumentOwnerType.AssistanceRequest, request.Id, requestDoc.Id, ct));
        Assert.Equal(StaffDocumentContextStatus.Archived, await guard.ValidateAsync(DocumentOwnerType.AssistanceRequest, request.Id, archived.Id, ct));
        Assert.Equal(StaffDocumentContextStatus.NotFound, await guard.ValidateAsync(DocumentOwnerType.Organization, context.Id, requestDoc.Id, ct));
        foreach (var doc in new[] { inconsistent, missing, wrongOrganization }) Assert.Equal(StaffDocumentContextStatus.NotFound, await guard.ValidateAsync(doc.OwnerType, doc.OwnerId, doc.Id, ct));
        Assert.Equal(StaffDocumentContextStatus.NotFound, await guard.ValidateAsync(DocumentOwnerType.Organization, org.Id, Guid.NewGuid(), ct));
        var audit = new CapturingAudit();
        var documents = new DocumentService(db, null!, null!, new Clock(now), null!, null!, audit, null!);
        await documents.VerifyAsync(actor, requestDoc.Id, "Fixture verification", ct);
        Assert.Equal(DocumentVerificationStatus.Verified, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == requestDoc.Id, ct)).VerificationStatus);
        await documents.RejectVerificationAsync(actor, requestDoc.Id, "Fixture rejection", ct);
        Assert.Equal(DocumentVerificationStatus.Rejected, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == requestDoc.Id, ct)).VerificationStatus);
        await documents.ArchiveAsync(actor, requestDoc.Id, ct);
        Assert.True((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == requestDoc.Id, ct)).IsArchived);
        Assert.Equal(new[] { "DocumentVerified", "DocumentVerificationRejected", "DocumentArchived" }, audit.Actions);

    }
}
