using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Demo;

internal static class DemoManagementDataSeeder
{
    private static readonly Guid EachUnitId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed record Scenario(
        string Key,
        int AgeDays,
        string Community,
        string Parish,
        string Category,
        string Program,
        RequestPriority Priority,
        DemoState State,
        string Item);

    private enum DemoState
    {
        Submitted,
        MoreInformationRequired,
        Approved,
        PendingFulfilment,
        Rejected,
        Fulfilled,
        Closed
    }

    private static readonly Scenario[] Scenarios =
    [
        new("MG01", 0,  "Downtown",       "Kingston",       "Emergency Relief",   "Disaster Response",     RequestPriority.High,   DemoState.Submitted,               "Emergency family food packages"),
        new("MG02", 2,  "Half Way Tree",  "St. Andrew",     "Health",             "Health Support",        RequestPriority.Normal, DemoState.Submitted,               "Mobility aids"),
        new("MG03", 8,  "Morant Bay",     "St. Thomas",     "Housing",            "Housing Support",       RequestPriority.High,   DemoState.MoreInformationRequired, "Roofing sheets"),
        new("MG04", 4,  "Port Antonio",   "Portland",       "Water & Sanitation", "Community Development", RequestPriority.High,   DemoState.Approved,                "Water storage tanks"),
        new("MG05", 15, "Highgate",       "St. Mary",       "Education",          "Education Support",     RequestPriority.Normal, DemoState.Rejected,                "School furniture"),
        new("MG06", 20, "Ocho Rios",      "St. Ann",        "Health",             "Health Support",        RequestPriority.Normal, DemoState.Closed,                  "Hygiene kits"),
        new("MG07", 10, "Falmouth",       "Trelawny",       "Emergency Relief",   "Disaster Response",     RequestPriority.High,   DemoState.PendingFulfilment,       "Tarpaulins"),
        new("MG08", 30, "Montego Bay",    "St. James",      "Housing",            "Housing Support",       RequestPriority.High,   DemoState.Submitted,               "Lumber and roofing materials"),
        new("MG09", 24, "Lucea",          "Hanover",        "Education",          "Education Support",     RequestPriority.Normal, DemoState.Fulfilled,               "Student backpacks"),
        new("MG10", 6,  "Savanna-la-Mar", "Westmoreland",  "Health",             "Health Support",        RequestPriority.Normal, DemoState.Submitted,               "Adult care supplies"),
        new("MG11", 12, "Black River",    "St. Elizabeth",  "Livelihoods",        "Community Development", RequestPriority.Normal, DemoState.Approved,                "Farm starter kits"),
        new("MG12", 18, "Mandeville",     "Manchester",     "Education",          "Education Support",     RequestPriority.Normal, DemoState.MoreInformationRequired, "Learning materials"),
        new("MG13", 28, "May Pen",        "Clarendon",      "Housing",            "Housing Support",       RequestPriority.High,   DemoState.Rejected,                "Home repair materials"),
        new("MG14", 35, "Spanish Town",   "St. Catherine",  "Water & Sanitation", "Community Development", RequestPriority.Normal, DemoState.Closed,                  "Community water filters"),
        new("MG15", 1,  "Papine",         "St. Andrew",     "Education",          "Education Support",     RequestPriority.High,   DemoState.Submitted,               "Special-needs learning aids"),
        new("MG16", 5,  "Old Harbour",    "St. Catherine",  "Emergency Relief",   "Disaster Response",     RequestPriority.High,   DemoState.Approved,                "Emergency bedding"),
        new("MG17", 42, "Negril",         "Westmoreland",   "Livelihoods",        "Community Development", RequestPriority.Normal, DemoState.Closed,                  "Small business recovery kits"),
        new("MG18", 16, "Annotto Bay",    "St. Mary",       "Health",             "Health Support",        RequestPriority.Normal, DemoState.PendingFulfilment,       "Clinic consumables")
    ];

    public static async Task EnsureAsync(
        RequestPortalDbContext dbContext,
        Organization organization,
        ApplicationUser representative,
        ApplicationUser staff,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (Scenario scenario in Scenarios)
        {
            string summary = $"Management demo {scenario.Key}: {scenario.Item} for {scenario.Community}.";
            bool exists = await dbContext.AssistanceRequests
                .AsNoTracking()
                .AnyAsync(item => item.Summary == summary, cancellationToken);
            if (exists) continue;

            DateTimeOffset created = now.AddDays(-scenario.AgeDays).AddHours(-2);
            DateTimeOffset submitted = created.AddHours(1);
            AssistanceRequest request = new(
                Guid.NewGuid(),
                $"RP-DEMO-{scenario.Key}",
                organization.Id,
                representative.Id,
                created);
            request.SetSummary(summary);
            request.SetDetails(
                null,
                scenario.Category,
                scenario.Item,
                scenario.Program,
                scenario.Priority,
                $"{scenario.Community}, {scenario.Parish}, Jamaica",
                $"Synthetic management demonstration request for {scenario.Community}, {scenario.Parish}.");
            request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, scenario.Item, 10, EachUnitId));
            request.Submit(representative.Id, submitted);

            DateTimeOffset action = submitted.AddHours(Math.Max(2, Math.Min(48, scenario.AgeDays * 2)));
            switch (scenario.State)
            {
                case DemoState.MoreInformationRequired:
                    request.RequestMoreInformation(staff.Id, action, "Please provide the outstanding supporting information for this demonstration request.");
                    break;
                case DemoState.Approved:
                    request.Approve(staff.Id, action);
                    break;
                case DemoState.PendingFulfilment:
                    request.Approve(staff.Id, action);
                    request.MarkPendingFulfilment(staff.Id, action.AddHours(4));
                    break;
                case DemoState.Rejected:
                    request.Reject(staff.Id, action, "Request did not meet the programme criteria in this demonstration scenario.");
                    break;
                case DemoState.Fulfilled:
                    request.Approve(staff.Id, action);
                    request.MarkPendingFulfilment(staff.Id, action.AddHours(4));
                    request.MarkFulfilled(staff.Id, action.AddDays(2));
                    break;
                case DemoState.Closed:
                    request.Approve(staff.Id, action);
                    request.MarkPendingFulfilment(staff.Id, action.AddHours(4));
                    request.MarkFulfilled(staff.Id, action.AddDays(2));
                    request.Close(staff.Id, action.AddDays(3));
                    break;
            }

            dbContext.AssistanceRequests.Add(request);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
