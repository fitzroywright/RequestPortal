using System.Data;
using System.Globalization;
using FfpManager.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Domain.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class BridgeApplicationReceiptTests
{
    [Theory]
    [InlineData("2026-11-03T04:59:59.123Z", "2026-11-02T00:00:00", "2026-11-02T23:59:59.123")]
    [InlineData("2026-11-03T05:00:00Z", "2026-11-03T00:00:00", "2026-11-03T00:00:00")]
    [InlineData("2027-01-01T02:30:00Z", "2026-12-31T00:00:00", "2026-12-31T21:30:00")]
    [InlineData("2027-01-01T06:30:00+02:00", "2026-12-31T00:00:00", "2026-12-31T23:30:00")]
    public void ReceiptDateAndFullTimestampAreMappedSeparately(string received, string date, string timestamp)
    {
        var submission = Submission(DateTimeOffset.Parse(received, CultureInfo.InvariantCulture));
        using var command = new SqlCommand();
        BridgeApplicationReceipt.AddParameters(command, submission);

        Assert.Equal(SqlDbType.DateTime, command.Parameters["@ApplicationDate"].SqlDbType);
        Assert.Equal(SqlDbType.DateTime, command.Parameters["@DateReceived"].SqlDbType);
        Assert.Equal(DateTime.Parse(date, CultureInfo.InvariantCulture), command.Parameters["@ApplicationDate"].Value);
        Assert.Equal(DateTime.Parse(timestamp, CultureInfo.InvariantCulture), command.Parameters["@DateReceived"].Value);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)command.Parameters["@DateReceived"].Value).Kind);
        Assert.Equal(submission.SubmittedUtc, JamaicaBusinessTime.ToUtc((DateTime)command.Parameters["@DateReceived"].Value));
    }

    [Theory]
    [InlineData("Employed")]
    [InlineData("Self Employed")]
    [InlineData("Unemployed")]
    [InlineData("Student")]
    public void EmploymentStatusMapsToVerifiedOccupationColumn(string employmentStatus)
    {
        using var command = new SqlCommand();
        BridgeApplicationReceipt.AddParameters(command, Submission(DateTimeOffset.UtcNow) with { ApplicantEmploymentStatus = employmentStatus });
        Assert.Equal(SqlDbType.NVarChar, command.Parameters["@Occupation"].SqlDbType);
        Assert.Equal(255, command.Parameters["@Occupation"].Size);
        Assert.Equal(employmentStatus, command.Parameters["@Occupation"].Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEmploymentStatusIsSqlNull(string? employmentStatus)
    {
        using var command = new SqlCommand();
        BridgeApplicationReceipt.AddParameters(command, Submission(DateTimeOffset.UtcNow) with { ApplicantEmploymentStatus = employmentStatus });
        Assert.Same(DBNull.Value, command.Parameters["@Occupation"].Value);
    }

    [Fact]
    public void OccupationAtSchemaLimitIsNotTruncated()
    {
        string value = new('x', 255);
        using var command = new SqlCommand();
        BridgeApplicationReceipt.AddParameters(command, Submission(DateTimeOffset.UtcNow) with { ApplicantEmploymentStatus = value });
        Assert.Equal(value, command.Parameters["@Occupation"].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothGatewaysRejectOverlongOccupationBeforeOpeningSql(bool synced)
    {
        var options = Options.Create(new BridgeOptions());
        IFfpManagerGateway gateway = synced ? new SyncedSqlFfpManagerGateway(options, null!) : new SqlFfpManagerGateway(options);
        var error = await Assert.ThrowsAsync<ExternalApplicationValidationException>(() => gateway.CreateApplicationAsync(
            Submission(DateTimeOffset.UtcNow) with { ApplicantEmploymentStatus = new string('x', 256) }, TestContext.Current.CancellationToken));
        Assert.Equal("VALIDATION_EMPLOYMENT_STATUS", error.Code);
        Assert.Equal("ApplicantEmploymentStatus", error.Field);
    }

    [Fact]
    public void SubmissionReceiptIsDistinctFromDraftCreation()
    {
        DateTimeOffset created = new(2026, 12, 20, 18, 0, 0, TimeSpan.Zero);
        DateTimeOffset received = new(2027, 1, 1, 2, 30, 0, TimeSpan.Zero);
        Guid user = Guid.NewGuid();
        var request = new AssistanceRequest(Guid.NewGuid(), "RP-RECEIPT", Guid.NewGuid(), user, created);
        request.SetSummary("Receipt regression");
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Food", 1, Guid.NewGuid()));
        Assert.Null(request.SubmittedUtc);
        request.Submit(user, received);
        Assert.Equal(created, request.CreatedUtc);
        Assert.Equal(received, request.SubmittedUtc);
        using var command = new SqlCommand();
        BridgeApplicationReceipt.AddParameters(command, Submission(request.SubmittedUtc!.Value));
        Assert.Equal(new DateTime(2026, 12, 31), command.Parameters["@ApplicationDate"].Value);
        Assert.Equal(new DateTime(2026, 12, 31, 21, 30, 0), command.Parameters["@DateReceived"].Value);
    }

    private static ExternalApplicationSubmission Submission(DateTimeOffset received) => new(
        Guid.NewGuid(), Guid.NewGuid(), "RP-RECEIPT", Guid.NewGuid(), "Receipt Test", null, null, null, null,
        Guid.NewGuid(), received, "Individual", "Normal", null, null, null, null, null, null, [], [], "Kingston");
}
