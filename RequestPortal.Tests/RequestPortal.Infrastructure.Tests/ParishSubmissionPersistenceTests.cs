using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Time;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.ReferenceData;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ParishSubmissionPersistenceTests
{
    [Fact]
    public async Task QuickIntakeReceiptTimestampSurvivesDelayedBridgeSubmissionAndRetries()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        DateTimeOffset beforeReceipt = DateTimeOffset.UtcNow;
        Guid requestId = await CreateIntakeAsync(database, token);
        DateTimeOffset afterReceipt = DateTimeOffset.UtcNow;
        // Compare persisted PostgreSQL microsecond precision, not the tracked
        // .NET value that can retain finer ticks on Linux.
        database.Db.ChangeTracker.Clear();
        var request = await database.Db.AssistanceRequests.SingleAsync(x => x.Id == requestId, token);
        DateTimeOffset received = request.SubmittedUtc!.Value;
        Assert.InRange(received, beforeReceipt, afterReceipt);
        Assert.Equal(received, (await database.Db.QuickIntakes.SingleAsync(token)).CreatedUtc);
        var bridge = new CapturingBridge();
        var service = Service(database, bridge, new DelayedClock(received.AddDays(7)));
        await service.SubmitRequestAsync(requestId, token);
        await service.SubmitRequestAsync(requestId, token);
        Assert.Equal(2, bridge.Submissions.Count);
        Assert.All(bridge.Submissions, submission => Assert.Equal(received, submission.SubmittedUtc));
        Assert.Equal(received, (await database.Db.AssistanceRequests.AsNoTracking().SingleAsync(x => x.Id == requestId, token)).SubmittedUtc);
    }

    [Fact]
    public async Task PersistedQuickIntake_MapsParishItemsAndDocuments_AndRetryReusesIdentity()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid requestId = await CreateIntakeAsync(database, token);
        var request = await database.Db.AssistanceRequests.SingleAsync(item => item.Id == requestId, token);
        Guid documentId = Guid.NewGuid();
        database.Db.Documents.Add(new StoredDocument(documentId, DocumentOwnerType.AssistanceRequest,
            requestId, request.OrganizationId, DocumentType.RequestSupport, "support.txt", "text/plain", 7,
            "test/support.txt", request.CreatedByUserId, DateTimeOffset.UtcNow));
        await database.Db.SaveChangesAsync(token);
        database.Db.ChangeTracker.Clear();

        CapturingBridge bridge = new();
        FfpManagerIntegrationService service = Service(database, bridge);
        ExternalApplicationReference first = await service.SubmitRequestAsync(requestId, token);
        ExternalApplicationReference second = await service.SubmitRequestAsync(requestId, token);

        Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.Equal(first.ApplicationNumber, second.ApplicationNumber);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(2, bridge.Submissions.Count);
        Assert.Equal(bridge.Submissions[0].CorrelationId, bridge.Submissions[1].CorrelationId);
        foreach (ExternalApplicationSubmission submission in bridge.Submissions)
        {
            Assert.Equal(requestId, submission.PortalRequestId);
            Assert.Equal("Test Applicant", submission.OrganizationName);
            Assert.Equal("Test", submission.FirstName);
            Assert.Equal("Applicant", submission.LastName);
            Assert.Equal("Clarendon Road", submission.ApplicantAddress);
            Assert.Equal("Clarendon Road", submission.StreetAddress);
            Assert.Equal("St. Andrew District", submission.CommunityDistrict);
            Assert.Null(submission.ApplicantEmail);
            Assert.Equal("Male", submission.ApplicantGender);
            Assert.Equal("Unemployed", submission.ApplicantEmploymentStatus);
            Assert.Equal("Kingston", submission.Parish);
            Assert.Equal("+18765550123",submission.PrimaryTelephone);
            Assert.Null(submission.SecondaryTelephone);
            Assert.Equal(new DateOnly(1980,1,1),submission.DateOfBirth);
            Assert.Equal("Request Portal - Quick Intake", submission.DocumentSource);
            ExternalApplicationItem item = Assert.Single(submission.Items);
            Assert.Equal(1, item.Quantity);
            Assert.Equal("Each", item.UnitOfMeasure);
            Assert.Equal(documentId, Assert.Single(submission.Artifacts).PortalDocumentId);
        }
        Assert.Equal("support", Encoding.UTF8.GetString(bridge.Artifacts[documentId]));
        Assert.Single(bridge.Artifacts);
        var link = await database.Db.IntegrationLinks.SingleAsync(token);
        Assert.Equal(SynchronizationStatus.Synchronized, link.Status);
        Assert.Equal(first.ApplicationId, link.ExternalReference);
        Assert.Equal(first.ApplicationNumber, link.ApplicationNumber);
        // Submission, Client Relations email and applicant confirmation are each queued once.
        Assert.Equal(3, await database.Db.OutboxMessages.CountAsync(token));
        Assert.Equal("Kingston", (await database.Db.QuickIntakes.AsNoTracking().SingleAsync(token)).Parish);
    }

    [Theory]
    [InlineData("Charity", "Clarendon")]
    [InlineData("IndividualContext", "St. Andrew")]
    public async Task RegularRequest_PreservesOrganizationNameAndExplicitParish(string organizationType, string expectedParish)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(token);
        await CreateIntakeAsync(database, token);
        var organization = await database.Db.Organizations.SingleAsync(token);
        organization.Rename("Community Support Foundation");
        database.Db.Entry(organization).Property(item => item.OrganizationType).CurrentValue = organizationType;
        var original = await database.Db.AssistanceRequests.SingleAsync(token);
        var request = new RequestPortal.Domain.Requests.AssistanceRequest(
            Guid.NewGuid(), "RP-ORG-TEST", organization.Id, original.CreatedByUserId, DateTimeOffset.UtcNow);
        request.SetSummary("Organization request");
        request.AddItem(new RequestPortal.Domain.Requests.AssistanceRequestItem(
            Guid.NewGuid(), request.Id, "Food parcels", 2, original.Items.Single().UnitOfMeasureId));
        request.Submit(original.CreatedByUserId, DateTimeOffset.UtcNow);
        database.Db.AssistanceRequests.Add(request);
        await database.Db.SaveChangesAsync(token);
        database.Db.ChangeTracker.Clear();
        CapturingBridge bridge = new();

        await Service(database, bridge).SubmitRequestAsync(request.Id, token);

        ExternalApplicationSubmission submission = Assert.Single(bridge.Submissions);
        Assert.Equal("Community Support Foundation", submission.OrganizationName);
        Assert.Null(submission.FirstName);
        Assert.Null(submission.LastName);
        Assert.Null(submission.ApplicantAddress);
        Assert.Null(submission.StreetAddress);
        Assert.Null(submission.CommunityDistrict);
        Assert.Null(submission.PrimaryTelephone);
        Assert.Null(submission.SecondaryTelephone);
        Assert.Null(submission.DateOfBirth);
        Assert.Null(submission.ApplicantEmail);
        Assert.Null(submission.ApplicantGender);
        Assert.Null(submission.ApplicantEmploymentStatus);
        Assert.Equal(expectedParish, submission.Parish);
        Assert.NotEqual("Request Portal - Quick Intake", submission.DocumentSource);
        Assert.Equal("Test", (await database.Db.QuickIntakes.AsNoTracking().SingleAsync(token)).FirstName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingPersistedIntakeParish_RecordsValidation_WithoutSendingOrChangingApplicantData(string parish)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid requestId = await CreateIntakeAsync(database, token);
        await database.Db.QuickIntakes.Where(item => item.RequestId == requestId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Parish, parish), token);
        database.Db.ChangeTracker.Clear();
        string originalPayload = (await database.Db.OutboxMessages.AsNoTracking().SingleAsync(item => item.MessageType == typeof(RequestSubmittedMessage).FullName, token)).Payload;
        CapturingBridge bridge = new();

        ExternalApplicationValidationException error = await Assert.ThrowsAsync<ExternalApplicationValidationException>(() =>
            Service(database, bridge).SubmitRequestAsync(requestId, token));

        Assert.Equal("Parish", error.Field);
        Assert.Contains("Quick Intake record", error.Message);
        Assert.Empty(bridge.Submissions);
        var link = await database.Db.IntegrationLinks.AsNoTracking().SingleAsync(token);
        Assert.Equal(SynchronizationStatus.Failed, link.Status);
        Assert.Equal(error.Message, link.LastError);
        var queued = await database.Db.OutboxMessages.AsNoTracking().SingleAsync(item => item.MessageType == typeof(RequestSubmittedMessage).FullName, token);
        Assert.Equal(originalPayload, queued.Payload);
        Assert.Equal(0, queued.AttemptCount);
        Assert.Equal(parish, (await database.Db.QuickIntakes.AsNoTracking().SingleAsync(token)).Parish);
        Assert.Equal("St. Andrew", (await database.Db.Users.AsNoTracking().SingleAsync(token)).Parish);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("portal-code")]
    public async Task InvalidFfpReferenceKeepsNotificationsPending_AndRetryPersistsGenuineNumber(string? invalid)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid requestId = await CreateIntakeAsync(database, token);
        var request = await database.Db.AssistanceRequests.SingleAsync(token);
        var bridge = new CapturingBridge { ApplicationNumber = invalid == "portal-code" ? request.PublicReference : invalid };
        var service = Service(database, bridge);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitRequestAsync(requestId, token));
        var link = await database.Db.IntegrationLinks.SingleAsync(token);
        Assert.Equal(SynchronizationStatus.Failed, link.Status);
        Assert.Null(link.ApplicationNumber);
        Assert.Null(link.ExternalReference);
        Assert.False(await database.Db.OutboxMessages.AnyAsync(
            item => item.MessageType == typeof(FfpManagerClientConfirmationRequestedMessage).FullName, token));
        var staff = await database.Db.OutboxMessages.SingleAsync(
            item => item.Id == QuickIntakeClientRelationsEmailRequestedMessage.IdForRequest(requestId), token);
        Assert.Equal(QuickIntakeClientRelationsEmailRequestedMessage.AwaitingReferenceMessageType, staff.MessageType);
        Assert.Equal(0, staff.AttemptCount);
        bridge.ApplicationNumber = "KGN/26/GR/0042";
        await service.SubmitRequestAsync(requestId, token);
        await service.SubmitRequestAsync(requestId, token);
        Assert.Equal("KGN/26/GR/0042", link.ApplicationNumber);
        Assert.Equal("123", link.ExternalReference);
        Assert.Single(await database.Db.OutboxMessages.Where(
            item => item.MessageType == typeof(FfpManagerClientConfirmationRequestedMessage).FullName).ToListAsync(token));
        Assert.Equal(typeof(QuickIntakeClientRelationsEmailRequestedMessage).FullName, staff.MessageType);
    }

    [Fact]
    public async Task ArtifactFailureDoesNotMarkSynchronizedOrReleaseNotifications()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid requestId = await CreateIntakeAsync(database, token);
        var request = await database.Db.AssistanceRequests.SingleAsync(token);
        database.Db.Documents.Add(new StoredDocument(Guid.NewGuid(), DocumentOwnerType.AssistanceRequest,
            requestId, request.OrganizationId, DocumentType.RequestSupport, "support.txt", "text/plain", 7,
            "test/support.txt", request.CreatedByUserId, DateTimeOffset.UtcNow));
        await database.Db.SaveChangesAsync(token);
        var bridge = new CapturingBridge { FailArtifactTransfer = true };
        var service = Service(database, bridge);
        await Assert.ThrowsAsync<IOException>(() => service.SubmitRequestAsync(requestId, token));
        var link = await database.Db.IntegrationLinks.SingleAsync(token);
        Assert.Equal(SynchronizationStatus.Failed, link.Status);
        Assert.Null(link.ApplicationNumber);
        Assert.False(await database.Db.OutboxMessages.AnyAsync(
            item => item.MessageType == typeof(FfpManagerClientConfirmationRequestedMessage).FullName, token));
        bridge.FailArtifactTransfer = false;
        await service.SubmitRequestAsync(requestId, token);
        Assert.Equal(SynchronizationStatus.Synchronized, link.Status);
        Assert.Equal("KIN/26/GR/0042", link.ApplicationNumber);
    }

    [Fact]
    public async Task SeparateAddressMappingPreservesOtherCapturedApplicantValues()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid requestId = await CreateIntakeAsync(database, token, "applicant@example.invalid", "+18765550124");
        var captured = await database.Db.QuickIntakes.AsNoTracking().SingleAsync(token);
        var organization = await database.Db.Organizations.AsNoTracking().SingleAsync(token);
        var bridge = new CapturingBridge();
        await Service(database,bridge).SubmitRequestAsync(requestId,token);
        var submission = Assert.Single(bridge.Submissions);
        Assert.Equal(captured.StreetAddress,submission.StreetAddress);
        Assert.Equal(captured.CommunityDistrict,submission.CommunityDistrict);
        Assert.Equal(captured.Parish,submission.Parish);
        Assert.Equal(captured.Gender,submission.ApplicantGender);
        Assert.Equal(captured.EmploymentStatus,submission.ApplicantEmploymentStatus);
        Assert.Equal(captured.Email,submission.ApplicantEmail);
        Assert.Equal(organization.Phone,submission.OrganizationPhone);
        Assert.Equal(captured.MobileNumber,submission.PrimaryTelephone);
        Assert.Equal(captured.SecondaryTelephone,submission.SecondaryTelephone);
        Assert.Equal(captured.DateOfBirth,submission.DateOfBirth);
        var unchanged = await database.Db.QuickIntakes.AsNoTracking().SingleAsync(token);
        Assert.Equal(captured.DateOfBirth,unchanged.DateOfBirth);
        Assert.Equal(captured.MobileNumber,unchanged.MobileNumber);
        Assert.Equal(captured.SecondaryTelephone,unchanged.SecondaryTelephone);
    }

    private static async Task<Guid> CreateIntakeAsync(PostgreSqlTestDatabase database, CancellationToken token, string? email = null, string? secondary = null)
    {
        database.Db.UnitsOfMeasure.Add(new UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
        await database.Db.SaveChangesAsync(token);
        QuickIntakeResult result = await new QuickIntakeService(database.Db).CreateAsync(new QuickIntakeSubmission(
            "Test", "Applicant", new DateOnly(1980, 1, 1), "Male", "Single", "8765550123", email,
            "Clarendon Road", "St. Andrew District", "Kingston", "Unemployed", 0, false, false,
            ["None"], "Food", SecondaryTelephone: secondary), token);
        // Conflicting explicit profiles must not replace this intake's answer.
        (await database.Db.Users.SingleAsync(token)).Parish = "St. Andrew";
        var organization = await database.Db.Organizations.SingleAsync(token);
        database.Db.Entry(organization).Property(item => item.Parish).CurrentValue = "Clarendon";
        await database.Db.SaveChangesAsync(token);
        return result.RequestId;
    }

    private static FfpManagerIntegrationService Service(PostgreSqlTestDatabase database, CapturingBridge bridge, IClock? clock = null) => new(
        database.Db, new DocumentStub(), bridge, Options.Create(new FfpManagerBridgeOptions { Enabled = true }),
        clock ?? new SystemClock(), null!); // Submission only queues notifications; it does not send Portal messages here.

    private sealed record DelayedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class CapturingBridge : IExternalApplicationBridgeClient
    {
        public string? ApplicationNumber { get; set; } = "KIN/26/GR/0042";
        public bool FailArtifactTransfer { get; set; }
        public List<ExternalApplicationSubmission> Submissions { get; } = [];
        public Dictionary<Guid, byte[]> Artifacts { get; } = [];

        public Task<ExternalApplicationReference> SubmitApplicationAsync(ExternalApplicationSubmission submission, CancellationToken cancellationToken = default)
        {
            // Exercise the same camel-case JSON contract as the HTTP bridge client.
            JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
            submission = JsonSerializer.Deserialize<ExternalApplicationSubmission>(JsonSerializer.Serialize(submission, json), json)!;
            Submissions.Add(submission);
            return Task.FromResult(new ExternalApplicationReference("123", ApplicationNumber, submission.SubmittedUtc, Submissions.Count > 1));
        }

        public async Task UploadArtifactAsync(string applicationId, ExternalArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default)
        {
            if (FailArtifactTransfer) throw new IOException("Synthetic artifact outage");
            Assert.Equal("123", applicationId);
            using MemoryStream bytes = new();
            await content.CopyToAsync(bytes, cancellationToken);
            Artifacts[descriptor.PortalDocumentId] = bytes.ToArray();
        }

        public Task<ExternalApplicationStatus> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendInformationResponseAsync(ExternalInformationResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DocumentStub : IDocumentService
    {
        public Task<DocumentDownload?> DownloadAsync(Guid userId, Guid documentId, bool isStaff, CancellationToken cancellationToken = default) =>
            Task.FromResult<DocumentDownload?>(new(new MemoryStream(Encoding.UTF8.GetBytes("support")), "text/plain", "support.txt"));
        public Task<Guid> UploadDeliveryEvidenceAsync(Guid requestId, Guid deliveryId, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> UploadForRequestAsync(Guid userId, Guid requestId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> UploadForOrganizationAsync(Guid userId, Guid organizationId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSummary>> GetRequestDocumentsAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSummary>> GetOrganizationDocumentsAsync(Guid userId, Guid organizationId, bool isStaff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task VerifyAsync(Guid staffUserId, Guid documentId, string? notes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RejectVerificationAsync(Guid staffUserId, Guid documentId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetRetentionAsync(Guid staffUserId, Guid documentId, DocumentRetentionClass retentionClass, DateTimeOffset? retainUntilUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ArchiveAsync(Guid staffUserId, Guid documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveRequestDocumentAsync(Guid userId, Guid requestId, Guid documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
