using System.Text;
using FfpManager.Bridge;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerBridgeTests
{
    [Fact]
    public async Task CreateApplicationAsync_IsIdempotentByCorrelationId()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        ExternalApplicationSubmission submission = CreateSubmission(Guid.NewGuid());

        ExternalApplicationReference first = await gateway.CreateApplicationAsync(submission, cancellationToken);
        ExternalApplicationReference second = await gateway.CreateApplicationAsync(submission, cancellationToken);

        Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
    }

    [Fact]
    public async Task SetStatusAsync_ChangesStatusWithoutChangingApplicationIdentity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        ExternalApplicationReference reference = await gateway.CreateApplicationAsync(
            CreateSubmission(Guid.NewGuid()),
            cancellationToken);

        ExternalApplicationStatus changed = await gateway.SetStatusAsync(
            reference.ApplicationId,
            "APPROVED",
            "Approved",
            "Approved in FFP Manager test workflow.",
            terminal: false,
            cancellationToken: cancellationToken);
        ExternalApplicationStatus? current = await gateway.GetStatusAsync(reference.ApplicationId, cancellationToken);

        Assert.NotNull(current);
        Assert.Equal(reference.ApplicationId, changed.ApplicationId);
        Assert.Equal("APPROVED", current.StatusCode);
        Assert.Equal(changed, current);
    }

    [Fact]
    public async Task SaveArtifactAsync_TracksDocumentAgainstApplication()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        ExternalApplicationReference reference = await gateway.CreateApplicationAsync(
            CreateSubmission(Guid.NewGuid()),
            cancellationToken);
        Guid documentId = Guid.NewGuid();
        byte[] bytes = Encoding.UTF8.GetBytes("sample supporting document");
        ExternalArtifactDescriptor descriptor = new(
            documentId,
            "RequestSupport",
            "quotation.txt",
            "text/plain",
            bytes.Length,
            DateTimeOffset.UtcNow);

        await using MemoryStream content = new(bytes);
        await gateway.SaveArtifactAsync(reference.ApplicationId, descriptor, content, cancellationToken);

        FakeFfpManagerApplicationSnapshot snapshot = gateway.GetSnapshot(reference.ApplicationId);
        ExternalArtifactDescriptor artifact = Assert.Single(snapshot.Artifacts);
        Assert.Equal(documentId, artifact.PortalDocumentId);
        Assert.Equal("quotation.txt", artifact.FileName);
    }

    [Fact]
    public async Task SaveArtifactAsync_IsIdempotentByPortalDocumentId()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        ExternalApplicationReference reference = await gateway.CreateApplicationAsync(
            CreateSubmission(Guid.NewGuid()),
            cancellationToken);
        Guid documentId = Guid.NewGuid();
        ExternalArtifactDescriptor first = new(documentId, "RequestSupport", "first.txt", "text/plain", 3, DateTimeOffset.UtcNow);
        ExternalArtifactDescriptor replacement = first with { FileName = "replacement.txt" };

        await using (MemoryStream content = new(Encoding.UTF8.GetBytes("one")))
        {
            await gateway.SaveArtifactAsync(reference.ApplicationId, first, content, cancellationToken);
        }
        await using (MemoryStream content = new(Encoding.UTF8.GetBytes("two")))
        {
            await gateway.SaveArtifactAsync(reference.ApplicationId, replacement, content, cancellationToken);
        }

        FakeFfpManagerApplicationSnapshot snapshot = gateway.GetSnapshot(reference.ApplicationId);
        ExternalArtifactDescriptor artifact = Assert.Single(snapshot.Artifacts);
        Assert.Equal("replacement.txt", artifact.FileName);
    }

    [Fact]
    public async Task SaveInformationResponseAsync_PreservesResponseAndArtifacts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        ExternalApplicationSubmission submission = CreateSubmission(Guid.NewGuid());
        ExternalApplicationReference reference = await gateway.CreateApplicationAsync(submission, cancellationToken);
        ExternalArtifactDescriptor artifact = new(
            Guid.NewGuid(),
            "RequestSupport",
            "beneficiary-list.pdf",
            "application/pdf",
            100,
            DateTimeOffset.UtcNow);
        ExternalInformationResponse response = new(
            Guid.NewGuid(),
            submission.PortalRequestId,
            reference.ApplicationId,
            Guid.NewGuid(),
            "The requested information is attached.",
            DateTimeOffset.UtcNow,
            [artifact]);

        await gateway.SaveInformationResponseAsync(response, cancellationToken);

        FakeFfpManagerApplicationSnapshot snapshot = gateway.GetSnapshot(reference.ApplicationId);
        ExternalInformationResponse captured = Assert.Single(snapshot.Responses);
        Assert.Equal(response.MessageId, captured.MessageId);
        Assert.Equal(submission.PortalRequestId, captured.PortalRequestId);
        Assert.Single(captured.Artifacts);
    }

    [Fact]
    public async Task SyncedSqlGateway_RejectsFractionalQuantityBeforeOpeningSql()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IOptions<BridgeOptions> options = Options.Create(new BridgeOptions());
        SyncedSqlFfpManagerGateway gateway = new(
            options,
            new FfpManagerReferenceDataCatalog(options));

        ExternalApplicationSubmission submission = CreateSubmission(Guid.NewGuid()) with
        {
            Items =
            [
                new ExternalApplicationItem(
                    Guid.NewGuid(),
                    "Fractional test item",
                    1.5m,
                    "EA")
            ]
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.CreateApplicationAsync(submission, cancellationToken));

        Assert.Contains("Quantity is an integer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlGateway_RequiresConnectionString()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SqlFfpManagerGateway gateway = new(Options.Create(new BridgeOptions()));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.CreateApplicationAsync(CreateSubmission(Guid.NewGuid()), cancellationToken));

        Assert.Contains("SqlConnectionString", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqlGatewaysRejectOversizedApplicantNamesBeforeConnecting(bool synced)
    {
        IFfpManagerGateway gateway = synced
            ? new SyncedSqlFfpManagerGateway(Options.Create(new BridgeOptions()), null!)
            : new SqlFfpManagerGateway(Options.Create(new BridgeOptions()));
        foreach (var submission in new[] {
            CreateSubmission(Guid.NewGuid()) with { FirstName = new string('x',201) },
            CreateSubmission(Guid.NewGuid()) with { LastName = new string('y',101) } })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.CreateApplicationAsync(submission, TestContext.Current.CancellationToken));
            Assert.Contains("200 characters", error.Message);
            Assert.DoesNotContain("SqlConnectionString", error.Message);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqlGatewaysRejectOversizedStreetOrTownBeforeConnecting(bool synced)
    {
        IFfpManagerGateway gateway = synced
            ? new SyncedSqlFfpManagerGateway(Options.Create(new BridgeOptions()), null!)
            : new SqlFfpManagerGateway(Options.Create(new BridgeOptions()));
        foreach (var submission in new[] {
            CreateSubmission(Guid.NewGuid()) with { StreetAddress = new string('x',501) },
            CreateSubmission(Guid.NewGuid()) with { CommunityDistrict = new string('y',501) } })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gateway.CreateApplicationAsync(submission, TestContext.Current.CancellationToken));
            Assert.Contains("Street and Town", error.Message);
        }
    }

    [Fact]
    public void AddressContractKeepsStreetTownAndParishIndependent_AndAcceptsLegacyPayloads()
    {
        var value = CreateSubmission(Guid.NewGuid()) with {
            StreetAddress = new string('x',500), CommunityDistrict = "Separate district", Parish = "Kingston",
            ApplicantAddress = "Legacy address remains independent", ApplicantGender = "Female",
            ApplicantEmploymentStatus = "Employed", ApplicantEmail = "test@example.invalid" };
        BridgeApplicantAddress.Validate(value);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var payload = System.Text.Json.JsonSerializer.Serialize(value,options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ExternalApplicationSubmission>(payload,options)!;
        Assert.Equal(value,restored with { Items=value.Items, Artifacts=value.Artifacts });
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(payload)!;
        legacyJson.AsObject().Remove("streetAddress");
        legacyJson.AsObject().Remove("communityDistrict");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ExternalApplicationSubmission>(legacyJson.ToJsonString(),options)!;
        Assert.Null(legacy.StreetAddress);
        Assert.Null(legacy.CommunityDistrict);
        Assert.Equal(value.OrganizationName,legacy.OrganizationName);
    }

    private static ExternalApplicationSubmission CreateSubmission(Guid correlationId) => new(
        correlationId,
        Guid.NewGuid(),
        "RP-TEST-001",
        Guid.NewGuid(),
        "Test Organization",
        "REG-001",
        "Test address",
        "876-555-0100",
        "test@example.invalid",
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        "Standard",
        "Normal",
        "Test request",
        "General",
        null,
        null,
        "Jamaica",
        "Test circumstances",
        [new ExternalApplicationItem(Guid.NewGuid(), "Roofing material", 10, "EA")],
        []);
}
