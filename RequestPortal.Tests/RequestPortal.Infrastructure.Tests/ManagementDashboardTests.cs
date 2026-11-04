using System.Reflection;
using System.Text.Json;
using Common.Storage;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Management;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ManagementDashboardTests
{
    public class Storage : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "GetMetadataAsync"
            ? Task.FromResult<StoredFile?>(null) : throw new InvalidOperationException("Unexpected storage operation: " + method.Name);
    }
    [Theory]
    [InlineData("Saint Catherine", "St. Catherine")]
    [InlineData("ST.  CATHERINE Parish", "St. Catherine")]
    [InlineData("St.Catherine", "St. Catherine")]
    [InlineData(" saint mary ", "St. Mary")]
    [InlineData("Kingston", "Kingston")]
    [InlineData(null, JamaicaParishes.Unknown)]
    [InlineData("", JamaicaParishes.Unknown)]
    [InlineData("An unrecognized address", JamaicaParishes.Unknown)]
    public void ParishIdentityNormalizesAliasesAndKeepsUnknownExplicit(string? input, string expected) => Assert.Equal(expected, JamaicaParishes.Normalize(input));

    [Fact]
    public void PinnedGeographyContainsFourteenRealWgs84BoundariesAndNoRequestData()
    {
        string root = Path.GetFullPath("../../../../../RequestPortal.Web/wwwroot/data", AppContext.BaseDirectory);
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, "jamaica-parishes.528b7394d3b3.geojson"));
        Assert.StartsWith("528b7394d3b3", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        using var json = JsonDocument.Parse(bytes);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(14, features.Length);
        Assert.Equal(JamaicaParishes.Names.Order(), features.Select(feature => feature.GetProperty("properties").GetProperty("name").GetString()!).Order());
        int vertices = 0;
        foreach (var feature in features)
        {
            Assert.Single(feature.GetProperty("properties").EnumerateObject());
            var geometry = feature.GetProperty("geometry");
            Assert.Contains(geometry.GetProperty("type").GetString(), new[] { "Polygon", "MultiPolygon" });
            void Check(JsonElement value)
            {
                if (value[0].ValueKind == JsonValueKind.Number)
                {
                    Assert.InRange(value[0].GetDouble(), -78.5, -76.0);
                    Assert.InRange(value[1].GetDouble(), 17.5, 18.6); vertices++;
                }
                else foreach (var item in value.EnumerateArray()) Check(item);
            }
            Check(geometry.GetProperty("coordinates"));
        }
        Assert.True(vertices > 10000, "Real coastline and parish geometry must not become a schematic outline.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedScopeReconcilesStatusesGeographyDrilldownsAndDelivery(bool deliveries)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await PostgreSqlTestDatabase.CreateAsync(token);
        var now = DateTimeOffset.UtcNow;
        var actor = Guid.NewGuid();
        var organization = new Organization(Guid.NewGuid(), "Aggregate fixture organization", "JM", actor, now.AddDays(-40));
        fixture.Db.Organizations.Add(organization);
        AssistanceRequest Request(string suffix, string? location, int age = 5, bool submit = true)
        {
            var request = new AssistanceRequest(Guid.NewGuid(), "RP-MAP-" + suffix, organization.Id, actor, now.AddDays(-40));
            request.SetKind(RequestKind.Project); request.SetSummary("Aggregate fixture");
            request.SetDetails(null, "Recorded category", null, "Recorded program", RequestPriority.Normal, location, null);
            if (submit) request.Submit(actor, now.AddDays(-age));
            fixture.Db.AssistanceRequests.Add(request); return request;
        }
        var first = Request("ALIAS", "Saint Catherine");
        var second = Request("CANONICAL", "St. Catherine");
        second.Approve(actor, now.AddDays(-4)); second.MarkPendingFulfilment(actor, now.AddDays(-3)); second.MarkFulfilled(actor, now.AddDays(-2));
        Request("MISSING", null);
        Request("UNKNOWN", "Unrecognized place");
        Request("KINGSTON", "Kingston", age: 0);
        var rejected = Request("REJECTED", "Saint Mary"); rejected.Reject(actor, now.AddDays(-2), "Fixture decision");
        Request("DRAFT", "Hanover", submit: false);
        var cancelled = Request("CANCELLED", "Hanover"); cancelled.Cancel(actor, now.AddDays(-1), "Fixture cancellation");
        if (deliveries)
        {
            foreach (var status in Enum.GetValues<DeliveryStatus>())
            {
                var delivery = new DeliveryRecord(Guid.NewGuid(), first.Id, "DeliveryOrder", "DEL-" + status, "{}", "fixture-hash", "Fixture recipient", null, now.AddDays(-2));
                if (status != DeliveryStatus.Ready) delivery.Acknowledge(status, "Fixture recipient", now.AddDays(-1), null, null, null);
                fixture.Db.Deliveries.Add(delivery);
            }
        }
        await fixture.Db.SaveChangesAsync(token);
        var service = new ManagementDashboardService(fixture.Db, DispatchProxy.Create<IFileStorage, Storage>(), Options.Create(new ManagementDashboardOptions()));
        var dashboard = await service.GetDashboardAsync(token);
        Assert.Equal(6, dashboard.Statuses.Sum(item => item.Count));
        Assert.Equal(15, dashboard.Parishes.Count);
        Assert.Equal(6, dashboard.Parishes.Sum(item => item.Requests));
        Assert.Equal(dashboard.Executive.Outstanding, dashboard.Parishes.Sum(item => item.Outstanding));
        Assert.Equal(dashboard.Executive.Completed, dashboard.Parishes.Sum(item => item.Completed));
        Assert.Equal(2, dashboard.Parishes.Single(item => item.Parish == "St. Catherine").Requests);
        Assert.Equal(1, dashboard.Parishes.Single(item => item.Parish == "St. Catherine").Completed);
        Assert.Equal(2, dashboard.Parishes.Single(item => item.Parish == JamaicaParishes.Unknown).Requests);
        Assert.Equal(0, dashboard.Parishes.Single(item => item.Parish == "Hanover").Requests);
        foreach (var parish in dashboard.Parishes)
        {
            var rows = await service.GetRequestsAsync(new(Parish: parish.Parish), token);
            Assert.Equal(parish.Requests, rows.Count);
        }
        Assert.Equal(2, (await service.GetRequestsAsync(new(Parish: "saint catherine"), token)).Count);
        Assert.Equal(2, (await service.GetRequestsAsync(new(Parish: "unknown"), token)).Count);
        foreach (var status in dashboard.Statuses)
            Assert.Equal(status.Count, (await service.GetRequestsAsync(new(Status: status.Key), token)).Count);
        Assert.Equal(4, dashboard.Executive.Outstanding); Assert.Equal(1, dashboard.Executive.Completed);
        Assert.Equal(deliveries ? 1 : 0, dashboard.Delivery.AwaitingAcknowledgement);
        Assert.Equal(deliveries ? 1 : 0, dashboard.Delivery.Delivered);
        Assert.Equal(deliveries ? 1 : 0, dashboard.Delivery.PartiallyDelivered);
        Assert.Equal(deliveries ? 1 : 0, dashboard.Delivery.Failed);
        Assert.Equal(deliveries ? 3 : 0, dashboard.Delivery.PendingFfpManagerSync);
        Assert.Equal(deliveries ? 1 : 0, dashboard.DeliveryAnalytics.Trend.Sum(item => item.Delivered));
        Assert.Equal(deliveries ? 1 : 0, dashboard.DeliveryAnalytics.Trend.Sum(item => item.PartiallyDelivered));
        Assert.Equal(deliveries ? 1 : 0, dashboard.DeliveryAnalytics.Trend.Sum(item => item.Failed));
        Assert.Equal(deliveries ? 4 : 0, (await service.GetDeliveriesAsync(new(Parish: "Saint Catherine"), token)).Count);
        Assert.Empty(await service.GetDeliveriesAsync(new(Parish: "unknown"), token));
        Assert.Equal(deliveries ? 1 : 0, dashboard.DeliveryAnalytics.Organizations.Count);
        Assert.Equal(1, dashboard.Trend.Sum(item => item.Completed));
        Assert.Equal(6, dashboard.Categories.Sum(item => item.Count));
        Assert.Equal(6, dashboard.Programs.Sum(item => item.Count));
    }
}
