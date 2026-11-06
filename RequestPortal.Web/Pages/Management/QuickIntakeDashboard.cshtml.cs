using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;

namespace RequestPortal.Web.Pages.Management;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class QuickIntakeDashboardModel(IQuickIntakeDashboardService service, TimeProvider time) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Through { get; set; }
    public QuickIntakeDashboardSnapshot Dashboard { get; private set; } = null!;
    public QuickIntakeRange Range { get; private set; } = null!;
    private QuickIntakeRange Resolve()
    {
        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var range = (Period ?? "30") switch {
            "7" => new QuickIntakeRange(today.AddDays(-6), today),
            "30" => new QuickIntakeRange(today.AddDays(-29), today),
            "90" => new QuickIntakeRange(today.AddDays(-89), today),
            "custom" when From.HasValue && Through.HasValue => new QuickIntakeRange(From.Value, Through.Value),
            _ => throw new ArgumentException("Choose a valid date range.") };
        range.Validate();
        if (range.Through > today) throw new ArgumentException("The end date cannot be in the future.");
        return range;
    }
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("Invalid date range.");
        Period ??= "30";
        try { Range = Resolve(); Dashboard = await service.GetAsync(Range, ct); return Page(); }
        catch (ArgumentException error) { return BadRequest(error.Message); }
    }
    public async Task<IActionResult> OnGetDataAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest("Invalid date range.");
        try { return new JsonResult(await service.GetAsync(Resolve(), ct)); }
        catch (ArgumentException error) { return BadRequest(error.Message); }
    }
    public static string Icon(string name) => "<svg viewBox=\"0 0 32 32\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" + (name switch {
        "bulb" => "<path d=\"M11 24h10m-10 4h10M11 20c0-4-5-5-5-11a10 10 0 0120 0c0 6-5 7-5 11z\"/>",
        "inbox" => "<path d=\"M4 18l4-13h16l4 13v9H4zM4 18h7l2 5h6l2-5h7\"/>",
        "clock" => "<circle cx=\"16\" cy=\"16\" r=\"12\"/><path d=\"M16 7v10l7 4\"/>",
        "people" => "<circle cx=\"12\" cy=\"9\" r=\"5\"/><path d=\"M2 28v-3a10 10 0 0120 0v3zM23 4a5 5 0 010 10m3 5a8 8 0 014 7\"/>",
        "check" => "<circle cx=\"16\" cy=\"16\" r=\"12\"/><path d=\"M8 16l6 6L28 7\"/>",
        "person" => "<circle cx=\"12\" cy=\"9\" r=\"5\"/><path d=\"M2 28v-3a10 10 0 0120 0v3zM26 8v10m-5-5h10\"/>",
        "sync" => "<path d=\"M27 12A12 12 0 006 6l-3 5m0-8v8h8M5 20a12 12 0 0021 6l3-5m0 8v-8h-8\"/>",
        _ => "<path d=\"M10 6H5v24h22V6h-5M11 3h10v7H11zM10 16h12M10 22h9\"/>" }) + "</svg>";
}
