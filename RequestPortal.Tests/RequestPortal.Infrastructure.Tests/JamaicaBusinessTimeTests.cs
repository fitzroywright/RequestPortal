using System.Globalization;
using FfpManager.Bridge;
using RequestPortal.Application.Integration;
using RequestPortal.Domain.Requests;
using System.Text.Json;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class JamaicaBusinessTimeTests
{
    [Theory]
    [InlineData("2026-07-01T12:30:00Z", "2026-07-01T07:30:00")]
    [InlineData("2026-11-02T04:59:59Z", "2026-11-01T23:59:59")]
    [InlineData("2026-11-02T05:00:00Z", "2026-11-02T00:00:00")]
    [InlineData("2027-01-01T04:59:59Z", "2026-12-31T23:59:59")]
    [InlineData("2027-01-01T05:00:00Z", "2027-01-01T00:00:00")]
    [InlineData("2026-07-01T14:30:00+02:00", "2026-07-01T07:30:00")]
    public void BusinessDatetimeRoundTripsWithoutUsingHostTimezone(string instant, string wallTime)
    {
        DateTimeOffset utc = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture).ToUniversalTime();
        DateTime local = JamaicaBusinessTime.ToLocal(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(wallTime, CultureInfo.InvariantCulture), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        Assert.Equal(utc, JamaicaBusinessTime.ToUtc(local));
        Assert.Equal(TimeSpan.Zero, JamaicaBusinessTime.ToUtc(local).Offset);
    }

    [Theory]
    [InlineData("2027-01-01T04:59:59Z", "KGN/26/GR/0042")]
    [InlineData("2027-01-01T05:00:00Z", "KGN/27/GR/0042")]
    [InlineData("2027-01-01T06:00:00+02:00", "KGN/26/GR/0042")]
    public void ReferenceYearUsesJamaicaDateCreated(string instant, string expected) =>
        Assert.Equal(expected, FfpManagerReferenceNumberGenerator.Format(
            "KGN", DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), 42));

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void DatabaseReadRejectsDatetimeWithAnAlreadyAssignedTimezone(DateTimeKind kind) =>
        Assert.Throws<ArgumentException>(() => JamaicaBusinessTime.ToUtc(
            DateTime.SpecifyKind(new DateTime(2027, 1, 1), kind)));

    [Fact]
    public void StatusSynchronizationContractRetainsUtcAcrossJamaicaMidnight()
    {
        var status = new ExternalApplicationStatus("123", "APPROVED", "Approved",
            JamaicaBusinessTime.ToUtc(new DateTime(2027, 1, 1, 0, 15, 0)));
        var callback = new ExternalStatusCallback(Guid.NewGuid(), Guid.NewGuid(), status);
        var received = JsonSerializer.Deserialize<ExternalStatusCallback>(JsonSerializer.Serialize(callback))!;
        DateTimeOffset submitted = new(2026, 12, 31, 22, 0, 0, TimeSpan.Zero);
        Guid userId = Guid.NewGuid();
        var request = new AssistanceRequest(callback.PortalRequestId, "RP-TIME", Guid.NewGuid(), userId, submitted);
        request.SetSummary("Timezone regression");
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Food", 1, Guid.NewGuid()));
        request.Submit(userId, submitted);
        request.Approve(userId, received.Status.ChangedUtc);

        var history = Assert.Single(request.StatusHistory, item => item.Status == RequestStatus.Approved);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 5, 15, 0, TimeSpan.Zero), history.ChangedUtc);
        Assert.Equal(TimeSpan.Zero, history.ChangedUtc.Offset);
        Assert.Equal(submitted, request.SubmittedUtc);
        Assert.Equal(submitted, request.CreatedUtc);
    }
}
