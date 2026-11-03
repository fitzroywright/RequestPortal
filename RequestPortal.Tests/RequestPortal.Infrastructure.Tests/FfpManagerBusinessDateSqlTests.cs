using System.Data;
using System.Text;
using FfpManager.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

[Trait("Category", "LiveFfpSql")]
public sealed class FfpManagerBusinessDateSqlTests
{
    // Opt-in rehearsal only. Each case creates its own marked application and document;
    // existing applications, schema and reference data are never changed.
    [Theory]
    [InlineData(false, "Employed")]
    [InlineData(true, "Self Employed")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task SubmissionUploadAndStatusDatesRoundTrip(bool synced, string? employmentStatus)
    {
        string? sql = Environment.GetEnvironmentVariable("FFPMANAGER_TEST");
        if (string.IsNullOrWhiteSpace(sql)) Assert.Skip("FFPMANAGER_TEST is unavailable; SQL business-date regression requires the rehearsal database.");
        FfpSqlConnection.ValidateTarget(sql!, "Rehearsal");
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "ffp-business-date-" + Guid.NewGuid().ToString("N"));
        BridgeOptions settings = new()
        {
            SqlConnectionString = sql!, SqlTarget = "Rehearsal",
            SupportingDocumentRoot = root, SupportingDocumentManagerRoot = @"\\test\documents"
        };
        var options = Options.Create(settings);
        var catalog = new FfpManagerReferenceDataCatalog(options);
        var snapshot = await catalog.GetSnapshotAsync(token);
        settings.InitialApplicationStatus = snapshot.ApplicationStatuses.First().Name;
        settings.DefaultDocumentType = snapshot.DocumentTypes.First().Name;
        IFfpManagerGateway gateway = synced ? new SyncedSqlFfpManagerGateway(options, catalog) : new SqlFfpManagerGateway(options);
        DateTimeOffset submitted = new(2027, 1, 1, 2, 30, 0, TimeSpan.Zero);
        Guid requestId = Guid.NewGuid();
        ExternalApplicationSubmission submission = new(requestId, requestId, "RP-TIME-" + requestId.ToString("N"),
            Guid.NewGuid(), "Timezone Regression", null, null, null, null, Guid.NewGuid(), submitted,
            "Individual", "Normal", "Synthetic timezone regression only", "Food", null, null, null, null,
            [new(Guid.NewGuid(), "Timezone verification only", 1, snapshot.UnitsOfMeasure.First().Name)], [], "Kingston",
            "Request Portal - Timezone Test", "Timezone", "Regression",
            StreetAddress: "42 Separate Street", CommunityDistrict: "Separate District", PrimaryTelephone: "8762101234", SecondaryTelephone: "8762101235",
            DateOfBirth: new DateOnly(1980,2,3), ApplicantGender: "Female", ApplicantEmail: "mapping@example.invalid", ApplicantEmploymentStatus: employmentStatus);
        try
        {
            var accepted = await gateway.CreateApplicationAsync(submission, token);
            Assert.Contains("/26/GR/", accepted.ApplicationNumber);
            Assert.Equal(submitted, accepted.AcceptedUtc);
            var repeated = await gateway.CreateApplicationAsync(submission with { SubmittedUtc = submitted.AddDays(2), ApplicantEmploymentStatus = "Student" }, token);
            Assert.True(repeated.AlreadyExisted);
            Assert.Equal(accepted.ApplicationId, repeated.ApplicationId);
            Assert.Equal(submitted, repeated.AcceptedUtc);
            var saved = (await gateway.GetApplicationAsync(accepted.ApplicationId, token))!;
            Assert.Equal(submitted, saved.SubmittedUtc);
            Assert.Equal(submission.StreetAddress,saved.StreetAddress);
            Assert.Equal(submission.CommunityDistrict,saved.CommunityDistrict);
            Assert.Equal(submission.DateOfBirth,saved.DateOfBirth);
            Assert.Equal("Female",saved.ApplicantGender);
            Assert.Equal("+18762101234",saved.PrimaryTelephone);
            Assert.Equal("+18762101235",saved.SecondaryTelephone);
            Assert.Equal(submission.ApplicantEmail,saved.ApplicantEmail);
            Assert.Equal(employmentStatus, saved.ApplicantEmploymentStatus);
            Assert.Equal(submitted, (await gateway.GetStatusAsync(accepted.ApplicationId, token))!.ChangedUtc);

            await using var connection = await FfpSqlConnection.OpenAsync(sql!, "Rehearsal", token);
            await using (var count = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.PhoneNumbers WHERE ApplicationLetter=@ID AND GCRecord IS NULL;",connection))
            {
                count.Parameters.Add("@ID",SqlDbType.BigInt).Value=long.Parse(accepted.ApplicationId);
                Assert.Equal(2L,(long)(await count.ExecuteScalarAsync(token))!);
            }
            async Task AssertDates(string query, DateTime expected)
            {
                await using var command = new SqlCommand(query, connection);
                command.Parameters.Add("@ID", SqlDbType.BigInt).Value = long.Parse(accepted.ApplicationId);
                await using var reader = await command.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                for (int i = 0; i < reader.FieldCount; i++) Assert.Equal(expected, reader.GetDateTime(i));
                Assert.False(await reader.ReadAsync(token));
            }
            await AssertDates("SELECT ApplicationDate FROM dbo.ApplicationLetters WHERE ID=@ID", new DateTime(2026, 12, 31));
            await AssertDates("SELECT DateReceived,DateCreated,DateUpdated FROM dbo.ApplicationLetters WHERE ID=@ID", new DateTime(2026, 12, 31, 21, 30, 0));
            await using (var occupation = new SqlCommand("SELECT Occupation FROM dbo.ApplicationLetters WHERE ID=@ID", connection))
            {
                occupation.Parameters.Add("@ID", SqlDbType.BigInt).Value = long.Parse(accepted.ApplicationId);
                Assert.Equal((object?)employmentStatus ?? DBNull.Value, await occupation.ExecuteScalarAsync(token));
            }
            await AssertDates("SELECT DateCreated,DateUpdated FROM dbo.ItemsRequested WHERE ApplicationLetterID=@ID", new DateTime(2026, 12, 31, 21, 30, 0));

            byte[] bytes = Encoding.UTF8.GetBytes("Timezone verification only");
            ExternalArtifactDescriptor descriptor = new(Guid.NewGuid(), settings.DefaultDocumentType,
                "timezone.txt", "text/plain", bytes.Length, new DateTimeOffset(2027, 1, 1, 4, 59, 59, TimeSpan.Zero));
            DateTimeOffset beforeUpload = DateTimeOffset.UtcNow;
            await using (var stream = new MemoryStream(bytes)) await gateway.SaveArtifactAsync(accepted.ApplicationId, descriptor, stream, token);
            await AssertDates("SELECT DateCreated,DateSubmitted FROM dbo.SupportingDocument WHERE ApplicationLetter=@ID", new DateTime(2026, 12, 31, 23, 59, 59));
            await using (var command = new SqlCommand("SELECT DateUpdated FROM dbo.SupportingDocument WHERE Oid=@Oid", connection))
            {
                command.Parameters.Add("@Oid", SqlDbType.UniqueIdentifier).Value = descriptor.PortalDocumentId;
                DateTime updated = (DateTime)(await command.ExecuteScalarAsync(token))!;
                Assert.InRange(JamaicaBusinessTime.ToUtc(updated), beforeUpload.AddMilliseconds(-4), DateTimeOffset.UtcNow.AddMilliseconds(4));
            }
            DateTimeOffset beforeStatus = DateTimeOffset.UtcNow;
            var status = await gateway.SetStatusAsync(accepted.ApplicationId, settings.InitialApplicationStatus,
                settings.InitialApplicationStatus, null, false, token);
            Assert.InRange(status.ChangedUtc, beforeStatus.AddMilliseconds(-4), DateTimeOffset.UtcNow.AddMilliseconds(4));
            Assert.Equal(TimeSpan.Zero, status.ChangedUtc.Offset);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
