using RequestPortal.Domain.Documents;
using System.Net;
using Common.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.ReferenceData;
using RequestPortal.Infrastructure.Auditing;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Persistence;
using Xunit;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using RequestPortal.Common.Messaging;
using Proxy = RequestPortal.Infrastructure.Tests.MessageNavigationPageTests.Proxy;

namespace RequestPortal.Infrastructure.Tests;

public sealed class IntegrationMonitoringTests
{
    [Fact]
    public async Task BridgeBadRequestRetainsRejectedValidationFieldInsteadOfHttpWrapper()
    {
        var method = typeof(FfpManagerBridgeClient).GetMethod(
            "EnsureSuccessAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","errors":{"PrimaryTelephone":["The applicant telephone requires correction before transfer."]}}""",
                System.Text.Encoding.UTF8, "application/problem+json")
        };
        async Task Call() => await (Task)method.Invoke(null, [response, TestContext.Current.CancellationToken])!;
        var error = await Assert.ThrowsAsync<IntegrationTransferException>(Call);
        Assert.Equal("VALIDATION_PRIMARY_TELEPHONE", error.Failure.Code);
        Assert.Contains("Primary telephone: requires correction", error.Failure.Explanation);
        Assert.DoesNotContain("HTTP 400", error.Failure.Explanation);
    }

    [Fact]
    public async Task BridgeBadRequestDoesNotDisplayUntrustedValidationValues()
    {
        var method = typeof(FfpManagerBridgeClient).GetMethod(
            "EnsureSuccessAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"errors":{"password=TOP-SECRET":["Applicant Private Person, token TOP-SECRET"]}}""",
                System.Text.Encoding.UTF8, "application/problem+json")
        };
        async Task Call() => await (Task)method.Invoke(null, [response, TestContext.Current.CancellationToken])!;
        var error = await Assert.ThrowsAsync<IntegrationTransferException>(Call);
        Assert.DoesNotContain("TOP-SECRET", error.Failure.Explanation);
        Assert.DoesNotContain("Private Person", error.Failure.Explanation);
    }

    [Fact]
    public void CapturedIntegrationExceptionsRetainSafeTypeStageAndDiagnostic()
    {
        var classifier = typeof(FfpManagerIntegrationService).Assembly.GetType(
            "RequestPortal.Infrastructure.Integration.IntegrationFailureClassifier")!;
        var classify = classifier.GetMethod("Classify", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        TransferFailure Classified(Exception error, string stage)
            => Assert.IsType<TransferFailure>(classify.Invoke(null, [error, stage]));
        var timeout = Classified(new TimeoutException("secret=TOP-SECRET; applicant=Private"), "BridgeSubmission");
        Assert.Contains("TimeoutException during BridgeSubmission", timeout.Explanation);
        Assert.DoesNotContain("TOP-SECRET", timeout.Explanation);
        Assert.DoesNotContain("Private", timeout.Explanation);
        var source = Classified(new InvalidOperationException("Draft requests cannot be sent to FFP Manager."), "Preparation");
        Assert.Contains("Draft requests cannot be sent to FFP Manager.", source.Explanation);
        var structured = TransferFailures.For("SqlSubmission", "SQL_CONSTRAINT");
        var bridge = Classified(new IntegrationTransferException(structured,
            new Exception("password=TOP-SECRET")), "SqlSubmission");
        Assert.Equal(structured.Explanation, bridge.Explanation);
        Assert.DoesNotContain("TOP-SECRET", bridge.Explanation);
    }

    [Fact]
    public async Task AuthenticatedBridgeStructuredFailureRetainsRootEvidence()
    {
        var method = typeof(FfpManagerBridgeClient).GetMethod(
            "EnsureSuccessAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var failure = TransferFailures.For("Validation", "VALIDATION_PRIMARY_TELEPHONE") with
        {
            TechnicalDetails = "Exception: ExternalApplicationValidationException\nStage: Validation\nRejected field: PrimaryTelephone"
        };
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.UnprocessableEntity)
        {
            Content = System.Net.Http.Json.JsonContent.Create(failure)
        };
        async Task Call() => await (Task)method.Invoke(null, [response, TestContext.Current.CancellationToken])!;
        var error = await Assert.ThrowsAsync<IntegrationTransferException>(Call);
        Assert.Equal("VALIDATION_PRIMARY_TELEPHONE", error.Failure.Code);
        Assert.Contains("Primary telephone", error.Failure.Explanation);
        Assert.Contains("ExternalApplicationValidationException", error.Failure.TechnicalDetails);
        Assert.DoesNotContain("HTTP 422", error.Failure.Explanation);
    }

    [Fact]
    public void RestrictedExceptionCaptureOmitsUntrustedMessagesAndSourcePaths()
    {
        var unsafeException = new InvalidOperationException(
            "password=TOP-SECRET; applicant=Private Person; server=hidden.internal");
        string detail = IntegrationExceptionDiagnostics.Capture(unsafeException, "SqlSubmission", "UNCLASSIFIED");
        Assert.Contains("InvalidOperationException", detail);
        Assert.Contains("SqlSubmission", detail);
        Assert.DoesNotContain("TOP-SECRET", detail);
        Assert.DoesNotContain("Private Person", detail);
        Assert.DoesNotContain("hidden.internal", detail);
        Assert.True(detail.Length <= 4000);
        Assert.True(IntegrationExceptionDiagnostics.IsSafeTransportDetail(detail));
    }

    [Fact]
    public void RestrictedExceptionCaptureKeepsApprovedValidationAndInnerExceptionTypes()
    {
        var error = new IntegrationTransferException(
            TransferFailures.For("SqlServer", "SQL_CONSTRAINT"),
            new TimeoutException("SECRET=TOP-SECRET"));
        string detail = IntegrationExceptionDiagnostics.Capture(error, "SqlServer", "SQL_CONSTRAINT");
        Assert.Contains("IntegrationTransferException", detail);
        Assert.Contains("TimeoutException", detail);
        Assert.DoesNotContain("TOP-SECRET", detail);
    }

    [Fact]
    public async Task RestrictedExceptionHistoryIsPersistedWithoutLeakingIntoTransferSummary()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid requestId = await Intake(database, "Diagnostics");
        var link = await database.Db.IntegrationLinks.SingleAsync(l => l.RequestId == requestId, token);
        link.BeginAttempt(DateTimeOffset.UtcNow);
        var attempt = new IntegrationAttempt(link.Id, link.AttemptCount, DateTimeOffset.UtcNow);
        attempt.Fail(DateTimeOffset.UtcNow, "SqlServer", "PermanentSql", "SQL_2627",
            "The destination rejected a SQL constraint.");
        attempt.SetExceptionDetails("Exception: SqlException\nStage: SqlServer\nSQL procedure: InsertApplication\nSQL line: 42");
        database.Db.IntegrationAttempts.Add(attempt);
        link.RecordFailure(DateTimeOffset.UtcNow, "SqlServer", "PermanentSql", "SQL_2627",
            "The destination rejected a SQL constraint.", false);
        await database.Db.SaveChangesAsync(token);
        var monitor = Monitor(database, new Bridge());
        string? restricted = await monitor.GetExceptionDetailsAsync(requestId, token);
        Assert.Contains("SQL line: 42", restricted);
        var row = Assert.Single((await monitor.SearchAsync(new(Search: "Diagnostics"), token)).Items);
        Assert.DoesNotContain("SQL line: 42", row.Explanation ?? "");
        Assert.DoesNotContain("SQL procedure:", row.RecordedError ?? "");
    }

    private static readonly FfpManagerBridgeOptions OptionsValue = new() { Enabled = true };
    private static readonly IClock Clock = new SystemClock();

    private static async Task<Guid> Intake(PostgreSqlTestDatabase database, string first = "Recovery")
    {
        if (!await database.Db.UnitsOfMeasure.AnyAsync(cancellationToken: TestContext.Current.CancellationToken))
        { database.Db.UnitsOfMeasure.Add(new(Guid.NewGuid(), "EA", "Each")); await database.Db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken); }
        return (await new QuickIntakeService(database.Db).CreateAsync(new QuickIntakeSubmission(
            first, "Applicant", new(1980,1,1), "Male", "Single", "8765550199", null, "Test street", "Test community",
            "Kingston", "Unemployed", 0, false, false, ["None"], "Food"), TestContext.Current.CancellationToken)).RequestId;
    }

    private static FfpManagerIntegrationService Submit(PostgreSqlTestDatabase database, Bridge bridge)
        => new(database.Db, new Documents(), bridge, Options.Create(OptionsValue), Clock, null!);
    private static IntegrationStatusService Monitor(PostgreSqlTestDatabase database, Bridge bridge, bool authorized = true)
        => new(database.Db, Clock, Submit(database, bridge), Options.Create(OptionsValue), new BusinessAuditWriter(database.Db, Clock), bridge, new Authorization(authorized));

    [Fact]
    public async Task MonitorShowsStoredClassifiedErrorAndSearchesTrnAndDestinationIdWithActivitySorting()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid first = await Intake(database, "First");
        Guid second = await Intake(database, "Second");
        var intake = await database.Db.QuickIntakes.SingleAsync(item => item.RequestId == first, token);
        var applicant = await database.Db.Users.SingleAsync(item => item.Id == intake.AnonymousUserId, token);
        applicant.NationalIdTrn = "TEST-TRN-123456789";
        var firstLink = await database.Db.IntegrationLinks.SingleAsync(item => item.RequestId == first, token);
        var secondLink = await database.Db.IntegrationLinks.SingleAsync(item => item.RequestId == second, token);
        firstLink.RecordVerifiedDestination("DESTINATION-901", "TEST/26/901", "JMDB01", "FFPJM", DateTimeOffset.UtcNow.AddMinutes(2));
        firstLink.RecordFailure(DateTimeOffset.UtcNow, "SqlSubmission", "PermanentSql", "SQL_CONSTRAINT", "Recorded constraint diagnostic: field length exceeded.", false);
        secondLink.RecordFailure(DateTimeOffset.UtcNow, "SqlSubmission", "PermanentSql", "SQL_CONSTRAINT", "Second diagnostic.", false);
        await database.Db.SaveChangesAsync(token);
        var monitor = Monitor(database, new Bridge());
        var trn = Assert.Single((await monitor.SearchAsync(new(Search: "TEST-TRN-123456789"), token)).Items);
        Assert.Equal(first, trn.RequestId);
        Assert.Equal("Recorded constraint diagnostic: field length exceeded.", trn.RecordedError);
        Assert.Equal(trn.RecordedError, trn.Explanation);
        Assert.Equal("Review the mapping before retrying.", trn.RecoveryAction);
        Assert.Equal(first, Assert.Single((await monitor.SearchAsync(new(Search: "DESTINATION-901"), token)).Items).RequestId);
        Assert.Equal(first, (await monitor.SearchAsync(new(Sort: "activity"), token)).Items[0].RequestId);
        Assert.Equal(trn.RecordedError, (await monitor.GetRequestAsync(first, token))!.Transfer.RecordedError);
        Assert.Empty((await monitor.SearchAsync(new(Search: "not-a-real-reference"), token)).Items);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(12, 12)]
    public async Task ExistingFailuresShowRecordedAttemptsWithoutResubmitting(int linkAttempts, int expected)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid request = await Intake(database);
        var link = await database.Db.IntegrationLinks.SingleAsync(token);
        for (int i = 0; i < linkAttempts; i++) link.BeginAttempt(DateTimeOffset.UtcNow);
        link.MarkFailed(DateTimeOffset.UtcNow, "PROTECTED historical provider payload");
        var submission = new OutboxMessage {
            Id = Guid.NewGuid(), RequestId = request,
            MessageType = typeof(FfpManagerSubmissionRequestedMessage).FullName!,
            Payload = "{}", OccurredUtc = DateTimeOffset.UtcNow, AttemptCount = 10 };
        database.Db.OutboxMessages.Add(submission);
        var notification = await database.Db.OutboxMessages.SingleAsync(item =>
            item.RequestId == request && item.MessageType.EndsWith("RequestSubmittedMessage"), token);
        notification.AttemptCount = 99;
        await database.Db.SaveChangesAsync(token);
        database.Db.ChangeTracker.Clear();
        var bridge = new Bridge();
        var monitor = Monitor(database, bridge);
        var page = await monitor.SearchAsync(new(Status: "Failed"), cancellationToken: token);
        var displayed = Assert.Single(page.Items);
        Assert.Equal(expected, displayed.AttemptCount);
        Assert.Equal(expected, (await monitor.GetRequestAsync(request, token))!.Transfer.AttemptCount);
        Assert.Equal("LEGACY_FAILURE", displayed.ErrorCode);
        Assert.DoesNotContain("PROTECTED", displayed.Explanation!);
        Assert.NotNull(displayed.LastAttemptUtc);
        Assert.Equal(0, bridge.Inserts);
        Assert.Empty(await database.Db.IntegrationAttempts.ToListAsync(token));
        Assert.Equal(10, (await database.Db.OutboxMessages.SingleAsync(item => item.Id == submission.Id, token)).AttemptCount);
    }

    [Fact]
    public async Task SubmissionAndTrackingAreAtomic_AndSuccessfulTransferIsVerifiedAndIdempotent()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Guid request = await Intake(database);
        var initial = await database.Db.IntegrationLinks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SynchronizationStatus.Pending, initial.Status);
        Assert.True(await database.Db.OutboxMessages.AnyAsync(item => item.RequestId == request && item.MessageType.EndsWith("RequestSubmittedMessage"), cancellationToken: TestContext.Current.CancellationToken));
        var bridge = new Bridge(); var submit = Submit(database, bridge);
        await submit.SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken); await submit.SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        database.Db.ChangeTracker.Clear();
        var detail = await Monitor(database, bridge).GetRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SynchronizationStatus.Synchronized, detail!.Transfer.Status);
        Assert.Equal("JMDB01 / FFPJM", detail.Transfer.Target);
        Assert.NotNull(detail.Transfer.VerifiedUtc);
        Assert.Equal("KGN/26/GR/0100", detail.Transfer.ApplicationNumber);
        Assert.Equal(1, bridge.Inserts);
        Assert.Equal(2, detail.Transfer.AttemptCount);
        Assert.Equal(2, detail.Attempts.Count);
        Assert.All(detail.Attempts, item => Assert.True(item.Succeeded));
        Assert.Equal(1, (await Monitor(database, bridge).SearchAsync(new(), cancellationToken: TestContext.Current.CancellationToken)).Counts.Synchronized);
    }

    [Theory]
    [InlineData("VALIDATION", "PermanentValidation", false)]
    [InlineData("VALIDATION_PRIMARY_TELEPHONE", "PermanentValidation", false)]
    [InlineData("VALIDATION_SECONDARY_TELEPHONE", "PermanentValidation", false)]
    [InlineData("SQL_CONSTRAINT", "PermanentSql", false)]
    [InlineData("SQL_547", "PermanentSql", false)]
    [InlineData("SQL_18456", "Authentication", false)]
    [InlineData("SQL_1205", "TransientConnection", true)]
    [InlineData("SQL_AUTH", "Authentication", false)]
    [InlineData("BRIDGE_AUTH", "Authentication", false)]
    [InlineData("CONNECTION", "TransientConnection", true)]
    public async Task FailuresHaveDurableSanitizedClassificationAndBoundedRetry(string code, string classification, bool retry)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Guid request = await Intake(database);
        var bridge = new Bridge { FailureCode = code };
        await Assert.ThrowsAsync<IntegrationTransferException>(() => Submit(database, bridge).SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken));
        database.Db.ChangeTracker.Clear();
        var detail = (await Monitor(database, bridge).GetRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(code, detail.Transfer.ErrorCode);
        Assert.Equal(classification, detail.Transfer.FailureClassification);
        Assert.Equal(retry ? SynchronizationStatus.Retrying : SynchronizationStatus.Failed, detail.Transfer.Status);
        Assert.Equal(retry, detail.Transfer.NextRetryUtc.HasValue);
        var attempt = Assert.Single(detail.Attempts);
        Assert.False(attempt.Succeeded); Assert.NotNull(attempt.CompletedUtc);
        Assert.DoesNotContain("TOP-SECRET", attempt.Explanation ?? "");
        if(retry)
        {
            for(int i=1;i<10;i++) await Assert.ThrowsAsync<IntegrationTransferException>(() => Submit(database, bridge).SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken));
            var link = await database.Db.IntegrationLinks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken); Assert.Null(link.NextRetryUtc); Assert.Equal(SynchronizationStatus.Failed,link.Status);
        }
    }

    [Theory]
    [InlineData(null)] [InlineData("Not a parish")]
    public async Task InvalidSourceMappingFailsBeforeSqlButAfterDurableAttempt(string? parish)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Guid request = await Intake(database);
        var intake = await database.Db.QuickIntakes.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        database.Db.Entry(intake).Property(item => item.Parish).CurrentValue = parish ?? "";
        await database.Db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var bridge = new Bridge { FailureCode = parish is null ? null : "VALIDATION" };
        var failure = await Assert.ThrowsAsync<IntegrationTransferException>(() => Submit(database, bridge).SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(parish is null ? "VALIDATION_PARISH" : "VALIDATION", failure.Failure.Code); Assert.Equal(parish is null ? "Preparation" : "SqlSubmission", failure.Failure.Stage);
        Assert.Equal(0, bridge.Inserts); Assert.Single(await database.Db.IntegrationAttempts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CommitThenLostAcknowledgementReconcilesAndRetriesWithoutDuplicateOrLostHistory()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Guid request = await Intake(database);
        var bridge = new Bridge { LoseAcknowledgement = true };
        await Assert.ThrowsAsync<IntegrationTransferException>(() => Submit(database, bridge).SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, bridge.Inserts);
        var monitor = Monitor(database,bridge);
        await monitor.ReconcileRequestAsync(Guid.NewGuid(),request, cancellationToken: TestContext.Current.CancellationToken);
        var detail = (await monitor.GetRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal("123",detail.Transfer.ApplicationId);
        Assert.Equal(SynchronizationStatus.ReconciliationRequired,detail.Transfer.Status);
        await monitor.RetryAsync(Guid.NewGuid(),detail.Transfer.LinkId!.Value, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1,bridge.Inserts);
        detail=(await monitor.GetRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(SynchronizationStatus.Synchronized,detail.Transfer.Status);
        Assert.Contains(detail.Attempts,item=>!item.Succeeded);
        Assert.Contains(detail.OperatorEvents,item=>item.Action=="IntegrationRetryRequested");
        Assert.Contains(detail.OperatorEvents,item=>item.Action=="IntegrationReconciled");
        Assert.Single(await database.Db.AssistanceRequests.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HttpSuccessWithoutVerifiableDestinationCannotSynchronizeOrReleaseConfirmations()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Guid request = await Intake(database);
        var bridge = new Bridge { HideDestination = true };
        await Assert.ThrowsAsync<IntegrationTransferException>(()=>Submit(database,bridge).SubmitRequestAsync(request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(SynchronizationStatus.ReconciliationRequired,(await Monitor(database,bridge).GetTransferAsync(request,TestContext.Current.CancellationToken))!.Status);
        Assert.False(await database.Db.OutboxMessages.AnyAsync(item=>item.MessageType.EndsWith("FfpManagerClientConfirmationRequestedMessage"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingLinksQueuesAndStalePendingAreVisibleInAccurateFilteredPaginatedTotals()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        for(int i=0;i<27;i++) await Intake(database,i==26?"FindMe":"Common");
        var lost=await database.Db.IntegrationLinks.FirstAsync(cancellationToken: TestContext.Current.CancellationToken); database.Db.IntegrationLinks.Remove(lost);
        var stale=await database.Db.IntegrationLinks.FirstAsync(item=>item.Id!=lost.Id, cancellationToken: TestContext.Current.CancellationToken);
        database.Db.Entry(stale).Property(item=>item.CreatedUtc).CurrentValue=DateTimeOffset.UtcNow.AddHours(-1);
        database.Db.Entry(stale).Property(item=>item.LastAttemptUtc).CurrentValue=DateTimeOffset.UtcNow.AddHours(-1);
        var requests=await database.Db.AssistanceRequests.Where(item=>item.Id==lost.RequestId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        database.Db.Entry(requests[0]).Property(item=>item.SubmittedUtc).CurrentValue=DateTimeOffset.UtcNow.AddHours(-1);
        database.Db.OutboxMessages.RemoveRange(await database.Db.OutboxMessages.Where(item=>item.RequestId==lost.RequestId).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        await database.Db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var monitor=Monitor(database,new Bridge());
        var first=await monitor.SearchAsync(new(), cancellationToken: TestContext.Current.CancellationToken);var second=await monitor.SearchAsync(new(Page:2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(27,first.TotalCount);Assert.Equal(25,first.Items.Count);Assert.Equal(2,second.Items.Count);
        Assert.Equal(27,first.Items.Concat(second.Items).Select(item=>item.RequestId).Distinct().Count());
        Assert.Equal(2,first.Counts.ReconciliationRequired);Assert.Equal(2,await monitor.CountAttentionAsync(cancellationToken: TestContext.Current.CancellationToken));
        var filtered=await monitor.SearchAsync(new(Status:"ReconciliationRequired"), cancellationToken: TestContext.Current.CancellationToken);Assert.Equal(2,filtered.TotalCount);
        Assert.Equal("MISSING_LINK",(await monitor.GetTransferAsync(lost.RequestId, cancellationToken: TestContext.Current.CancellationToken))!.ErrorCode);
        Assert.Single((await monitor.SearchAsync(new(Search:"findme applicant"), cancellationToken: TestContext.Current.CancellationToken)).Items);
        Assert.Empty((await monitor.SearchAsync(new(Search:"never matches"), cancellationToken: TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    public async Task UnauthorizedOperatorsCannotReconcileRetryOrCreateLinks()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Guid request=await Intake(database);var bridge=new Bridge();var monitor=Monitor(database,bridge,false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>monitor.ReconcileRequestAsync(Guid.NewGuid(),request, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>monitor.RetryAsync(Guid.NewGuid(),database.Db.IntegrationLinks.Single().Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0,bridge.Inserts);Assert.Empty(await database.Db.IntegrationAttempts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentTransferIsLockedAcrossDurableCheckpoints()
    {
        var token=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(token);
        Guid request=await Intake(database);var bridge=new Bridge { Release=new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var first=Submit(database,bridge).SubmitRequestAsync(request,token);
        try
        {
            await bridge.Entered.Task.WaitAsync(token);
            await using var other=database.CreateSiblingContext();
            var concurrent=new FfpManagerIntegrationService(other,new Documents(),bridge,Options.Create(OptionsValue),Clock,null!);
            var error=await Assert.ThrowsAsync<IntegrationTransferException>(()=>concurrent.SubmitRequestAsync(request,token));
            Assert.Equal("Concurrency",error.Failure.Stage);
        }
        finally { bridge.Release.TrySetResult(); }
        await first;Assert.Equal(1,bridge.Inserts);
        Assert.Single(await database.Db.IntegrationAttempts.ToListAsync(token));
    }

    [Theory]
    [InlineData("JMDB01","FFP")][InlineData("OTHER","FFPJM")]
    public void DestinationGuardRejectsRehearsalAndOtherServers(string server,string database)
    {
        var id=Guid.NewGuid();
        var error=Assert.Throws<IntegrationTransferException>(()=>FfpManagerIntegrationService.ValidateReceipt(id,
            new(id,"123","KGN/26/GR/0100",DateTimeOffset.UtcNow,server,database)));
        Assert.Equal("DESTINATION_CONFLICT",error.Failure.Code);
    }

    [Fact]
    public async Task ReturnOutboxFailuresAndRecoveryRetainSeparateDurableHistory()
    {
        var token=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(token);
        var bridge=new Bridge();var monitor=Monitor(database,bridge);
        await monitor.ObserveReturnOutboxAsync(token);await monitor.ObserveReturnOutboxAsync(token);
        Assert.Single(await monitor.GetReturnHistoryAsync(token));Assert.Equal(1,await monitor.CountAttentionAsync(token));
        bridge.ReturnStatus=new(0,0,0,null);await monitor.ObserveReturnOutboxAsync(token);
        var history=await monitor.GetReturnHistoryAsync(token);Assert.Equal(2,history.Count);
        Assert.Contains(history,item=>item.Snapshot?.Failed==1);Assert.Equal(0,await monitor.CountAttentionAsync(token));
        bridge.ReturnUnavailable=true;await monitor.ObserveReturnOutboxAsync(token);
        Assert.Equal("RETURN_OUTBOX_UNAVAILABLE",(await monitor.GetReturnHistoryAsync(token))[0].ErrorCode);
        Assert.Equal(1,await monitor.CountAttentionAsync(token));
    }

    [Theory][InlineData("SQL_CONSTRAINT",true)][InlineData("CONNECTION",false)]
    public async Task SubmissionWorkerStopsPermanentFailuresAndSchedulesTransientRetriesWithoutStarvingReadyWork(string code,bool terminal)
    {
        var token=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(token);
        Guid request=await Intake(database);
        foreach(var message in await database.Db.OutboxMessages.ToListAsync(token))message.ProcessedUtc=DateTimeOffset.UtcNow;
        for(int i=0;i<100;i++)database.Db.OutboxMessages.Add(new OutboxMessage { Id=Guid.NewGuid(),MessageType="Future work",Payload="{}",
            OccurredUtc=DateTimeOffset.UtcNow.AddDays(-2),NextAttemptUtc=DateTimeOffset.UtcNow.AddHours(1),AttemptCount=1 });
        var messageId=FfpManagerSubmissionRequestedMessage.IdForRequest(request);
        var submission=new FfpManagerSubmissionRequestedMessage(messageId,DateTimeOffset.UtcNow,request);
        var queued=new OutboxMessage { Id=messageId,RequestId=request,MessageType=typeof(FfpManagerSubmissionRequestedMessage).FullName!,
            Payload=System.Text.Json.JsonSerializer.Serialize(submission),OccurredUtc=submission.OccurredUtc };
        database.Db.OutboxMessages.Add(queued);await database.Db.SaveChangesAsync(token);
        var bridge=new Bridge { FailureCode=code };
        var dispatcher=new OutboxDispatcher(database.Db,
            Proxy.For<INotificationSender>((method,_)=>throw new InvalidOperationException(method)),
            Submit(database,bridge),Options.Create(OptionsValue),Options.Create(new NotificationOptions()),NullLogger<OutboxDispatcher>.Instance,
            new Flow(),Proxy.For<IPortalMessageService>((method,_)=>throw new InvalidOperationException(method)));
        Assert.Equal(0,await dispatcher.DispatchBatchAsync(cancellationToken:token));
        Assert.Equal(terminal,queued.IsTerminal);Assert.Equal(1,queued.AttemptCount);
        if(terminal) Assert.False((bool)typeof(OutboxDispatcher).GetMethod("IsDue",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,[queued,DateTimeOffset.UtcNow])!);

        Assert.Equal(!terminal,queued.NextAttemptUtc.HasValue);
        Assert.Equal(1,(await database.Db.IntegrationLinks.SingleAsync(token)).AttemptCount);
        Assert.Equal(0,await dispatcher.DispatchBatchAsync(cancellationToken:token));Assert.Equal(1,queued.AttemptCount);
        if(!terminal)
        {
            queued.NextAttemptUtc=DateTimeOffset.UtcNow.AddMinutes(-1);queued.LastError=$"RETRY_AT={DateTimeOffset.UtcNow.AddMinutes(-1):O}|Synthetic failure";
            await database.Db.SaveChangesAsync(token);bridge.FailureCode=null;
            Assert.Equal(1,await dispatcher.DispatchBatchAsync(cancellationToken:token));
            Assert.NotNull(queued.ProcessedUtc);Assert.Equal(2,await database.Db.IntegrationAttempts.CountAsync(token));
        }
    }
    private sealed class Flow : IRequestPortalOperationsFlowPublisher
    { public bool TryEnqueue(Guid requestId,string stage,DateTimeOffset observedAtUtc,string? detail=null,bool failed=false)=>true; }

    [Fact]
    public async Task LegacyFailureEvidenceSurvivesSuccessfulRetryWithoutExposingOriginalPayload()
    {
        var token=TestContext.Current.CancellationToken;
        await using var database=await PostgreSqlTestDatabase.CreateAsync(token);
        Guid request=await Intake(database);var link=await database.Db.IntegrationLinks.SingleAsync(token);
        link.MarkFailed(DateTimeOffset.UtcNow.AddHours(-1),"Historical TOP-SECRET provider payload");await database.Db.SaveChangesAsync(token);
        var bridge=new Bridge();await Submit(database,bridge).SubmitRequestAsync(request,token);
        var detail=(await Monitor(database,bridge).GetRequestAsync(request,token))!;
        var historical=Assert.Single(detail.Attempts,item=>item.Operation=="HistoricalFailure");
        Assert.StartsWith("HISTORICAL_",historical.ErrorCode);Assert.DoesNotContain("TOP-SECRET",historical.Explanation);
        Assert.Equal(SynchronizationStatus.Synchronized,detail.Transfer.Status);
    }

    private sealed class Authorization(bool allowed) : IIntegrationOperatorAuthorization
    { public Task DemandAsync(Guid actorId,CancellationToken cancellationToken=default) => allowed?Task.CompletedTask:throw new UnauthorizedAccessException(); }
    private sealed class Documents : IDocumentService
    {
        public Task<DocumentDownload?> DownloadAsync(Guid userId, Guid documentId, bool isStaff, CancellationToken cancellationToken = default) =>
            Task.FromResult<DocumentDownload?>(new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("support")), "text/plain", "support.txt"));
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
    private sealed class Bridge : IExternalApplicationBridgeClient
    {
        public Dictionary<Guid,ExternalApplicationReceipt> Records {get;}=[];
        public string? FailureCode {get;set;}
        public bool LoseAcknowledgement {get;set;}
        public bool HideDestination {get;set;}
        public int Inserts {get;private set;}
        public TaskCompletionSource Entered {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release {get;set;}
        public ExternalReturnOutboxStatus ReturnStatus {get;set;} = new(2,1,0,DateTimeOffset.UtcNow);
        public bool ReturnUnavailable {get;set;}
        public Task<ExternalReturnOutboxStatus> GetReturnOutboxAsync(CancellationToken cancellationToken=default)
            => ReturnUnavailable ? throw new HttpRequestException("TOP-SECRET") : Task.FromResult(ReturnStatus);
        public Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid id,CancellationToken cancellationToken=default)
            => Task.FromResult(HideDestination?null:Records.GetValueOrDefault(id));
        public async Task<ExternalApplicationReference> SubmitApplicationAsync(ExternalApplicationSubmission submission,CancellationToken cancellationToken=default)
        {
            if(FailureCode is {} code) throw new IntegrationTransferException(TransferFailures.For("SqlServer",code),new Exception("TOP-SECRET"));
            Inserts++;
            var receipt=new ExternalApplicationReceipt(submission.PortalRequestId,"123","KGN/26/GR/0100",submission.SubmittedUtc,"JMDB01","FFPJM");
            Records.Add(submission.PortalRequestId,receipt);
            Entered.TrySetResult();
            if(Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if(LoseAcknowledgement){LoseAcknowledgement=false;throw new HttpRequestException("Connection lost after SQL commit; TOP-SECRET");}
            return new ExternalApplicationReference(receipt.ApplicationId,receipt.ApplicationNumber,receipt.AcceptedUtc);
        }
        public Task UploadArtifactAsync(string applicationId,ExternalArtifactDescriptor descriptor,Stream content,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<ExternalApplicationStatus> GetStatusAsync(string applicationId,CancellationToken cancellationToken=default)=>Task.FromResult(new ExternalApplicationStatus(applicationId,"RECEIVED","Received",DateTimeOffset.UtcNow));
        public Task SendInformationResponseAsync(ExternalInformationResponse response,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
}
