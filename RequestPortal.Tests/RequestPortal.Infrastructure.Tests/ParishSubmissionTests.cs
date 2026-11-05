using System.Text.Json;
using FfpManager.Bridge;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using RequestPortal.Application.Integration;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ParishSubmissionTests
{
    [Theory]
    [InlineData("IndividualContext")]
    [InlineData("Charity")]
    public void QuickIntake_UsesItsExplicitAnswerDespiteDifferentProfileParishes(string organizationType)
    {
        QuickIntakeRecord intake = CreateIntake();
        Assert.Equal("Kingston", FfpManagerSubmissionParish.Resolve(
            "RP-TEST", intake, organizationType, "Clarendon", "St. Andrew"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IncompleteQuickIntake_DoesNotFallBackToAnotherParish(string? parish)
    {
        QuickIntakeRecord intake = CreateIntake();
        // Simulate legacy/incomplete persisted data, which bypasses the intake constructor.
        typeof(QuickIntakeRecord).GetProperty(nameof(QuickIntakeRecord.Parish))!.SetValue(intake, parish);
        ExternalApplicationValidationException error = Assert.Throws<ExternalApplicationValidationException>(() =>
            FfpManagerSubmissionParish.Resolve("RP-TEST", intake, "IndividualContext", "Clarendon", "St. Andrew"));
        Assert.Equal("Parish", error.Field);
        Assert.Contains("Quick Intake record", error.Message);
        Assert.Contains("RP-TEST", error.Message);
    }

    [Theory]
    [InlineData("IndividualContext", "Clarendon", " St. Andrew ", "St. Andrew")]
    [InlineData("Charity", " Clarendon ", "St. Andrew", "Clarendon")]
    public void NonIntake_UsesTheRelevantExplicitProfileField(
        string organizationType, string organizationParish, string applicantParish, string expected) =>
        Assert.Equal(expected, FfpManagerSubmissionParish.Resolve(
            "RP-TEST", null, organizationType, organizationParish, applicantParish));

    [Theory]
    [InlineData("IndividualContext", "Clarendon", null)]
    [InlineData("Charity", null, "St. Andrew")]
    public void MissingProfileParish_DoesNotBorrowAnotherProfilesAnswer(
        string organizationType, string? organizationParish, string? applicantParish) =>
        Assert.Throws<ExternalApplicationValidationException>(() => FfpManagerSubmissionParish.Resolve(
            "RP-TEST", null, organizationType, organizationParish, applicantParish));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingParish_ReturnsFieldValidation400_BeforeReferenceAllocation(string? parish)
    {
        ExternalApplicationSubmission submission = Submission() with { Parish = parish };
        var result = await ApplicationSubmissionEndpoint.HandleAsync(submission, new MissingParishGateway(), NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
        var response = Assert.IsType<JsonHttpResult<TransferFailure>>(result);
        Assert.Equal(422, response.StatusCode);
        Assert.Equal("VALIDATION_PARISH", response.Value!.Code);
        Assert.Contains("Parish", response.Value.Explanation);
        Assert.Contains("ExternalApplicationValidationException", response.Value.TechnicalDetails);
    }

    [Fact]
    public async Task ExistingFfpReference_IsReturnedWithoutReallocation_EvenForLegacyPayloadWithoutParish()
    {
        // This is the actual shared generator's idempotent path; it must not require SQL or a parish.
        string reference = await FfpManagerReferenceNumberGenerator.EnsureExistingAsync(
            null!, null!, 123, "KIN/26/GR/0042", DateTimeOffset.UtcNow,
            Submission() with { Parish = null }, TestContext.Current.CancellationToken);
        Assert.Equal("KIN/26/GR/0042", reference);
    }

    [Fact]
    public async Task ValidSubmission_RoundTripsParishAndReturnsSameApplicationOnRepeat()
    {
        ExternalApplicationSubmission submission = Submission();
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        string body = JsonSerializer.Serialize(submission, json);
        Assert.Equal("Kingston", JsonDocument.Parse(body).RootElement.GetProperty("parish").GetString());
        submission = JsonSerializer.Deserialize<ExternalApplicationSubmission>(body, json)!;
        FakeFfpManagerGateway gateway = new();
        var first = Assert.IsType<Ok<ExternalApplicationReference>>(
            (await ApplicationSubmissionEndpoint.HandleAsync(submission, gateway, NullLoggerFactory.Instance, TestContext.Current.CancellationToken))).Value!;
        var second = Assert.IsType<Ok<ExternalApplicationReference>>(
            (await ApplicationSubmissionEndpoint.HandleAsync(submission, gateway, NullLoggerFactory.Instance, TestContext.Current.CancellationToken))).Value!;
        Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.Equal(first.ApplicationNumber, second.ApplicationNumber);
        Assert.True(second.AlreadyExisted);
        Assert.Equal("Kingston", gateway.GetSnapshot(first.ApplicationId).Submission.Parish);
    }

    [Fact]
    public async Task InfrastructureErrors_AreNotMisreportedAsApplicantValidation()
    {
        // A valid parish with no SQL connection configured is a deployment error, not a 400.
        var options = Microsoft.Extensions.Options.Options.Create(new BridgeOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationSubmissionEndpoint.HandleAsync(
            Submission(), new SyncedSqlFfpManagerGateway(options, new FfpManagerReferenceDataCatalog(options)),
            NullLoggerFactory.Instance, TestContext.Current.CancellationToken));
    }

    private static QuickIntakeRecord CreateIntake() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test", "Applicant",
        new DateOnly(1980, 1, 1), "Male", "Single", "+18765550123", null,
        "Clarendon Road", "St. Andrew District", "Kingston", "Unemployed", 0, false, false,
        "None", "Food", DateTimeOffset.UtcNow);

    private static ExternalApplicationSubmission Submission() => new(
        Guid.NewGuid(), Guid.NewGuid(), "RP-TEST", Guid.NewGuid(), "Test applicant", null,
        "An address mentioning Clarendon", null, null, Guid.NewGuid(), DateTimeOffset.UtcNow,
        "Standard", "Normal", "Test request", "Food", null, null, "Free-text St. Andrew", null,
        [new(Guid.NewGuid(), "Food", 1, "EA")], [], "Kingston", "Request Portal - Quick Intake");

    private sealed class MissingParishGateway : IFfpManagerGateway
    {
        public Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<ExternalApplicationReference> CreateApplicationAsync(ExternalApplicationSubmission submission, CancellationToken cancellationToken = default)
        {
            // Exercise the production generator's guard with no connection or transaction available.
            await FfpManagerReferenceNumberGenerator.GenerateAsync(null!, null!, submission.Parish, submission.SubmittedUtc, cancellationToken);
            throw new InvalidOperationException("A missing parish must never reach SQL allocation.");
        }

        public Task SaveArtifactAsync(string applicationId, ExternalArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalApplicationStatus?> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveInformationResponseAsync(ExternalInformationResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalApplicationSubmission?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalApplicationStatus> SetStatusAsync(string applicationId, string statusCode, string statusText, string? detail, bool terminal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
