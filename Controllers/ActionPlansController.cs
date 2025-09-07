using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using System.Text;
using System.Net;

namespace KPIMonitor.Controllers
{
  public class ActionPlansController : Controller
  {
    private readonly AppDbContext _db;
    public ActionPlansController(AppDbContext db) { _db = db; }

    [HttpGet]
    public IActionResult Index() => View();

    // HTML board with controls (status select + move deadline)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BoardHtml([FromBody] decimal[] kpiIds)
    {
      // When opened “standalone”, if no filter passed, default to ALL red KPIs
      IEnumerable<decimal> filter = kpiIds ?? Array.Empty<decimal>();
      if (!filter.Any())
      {
        // same logic as RedBoard “red list” but only the KpiId set
        var latestIds =
            from p in _db.KpiYearPlans
            where p.IsActive == 1 && p.Period != null
            group p by p.KpiId into g
            select new { KpiId = g.Key, MaxPlanId = g.Max(x => x.KpiYearPlanId) };

        var latestPlans =
            from lid in latestIds
            join p in _db.KpiYearPlans on
                new { lid.KpiId, PlanId = lid.MaxPlanId }
                equals new { p.KpiId, PlanId = p.KpiYearPlanId }
            select new { p.KpiId, Year = p.Period!.Year };

        var redCodes = new[] { "red", "ecart" };
        var redKpiIds = await (
            from lp in latestPlans
            join f in _db.KpiFacts on lp.KpiId equals f.KpiId
            join per in _db.DimPeriods on f.PeriodId equals per.PeriodId
            where f.IsActive == 1
                  && per.Year == lp.Year
                  && f.StatusCode != null
                  && redCodes.Contains(f.StatusCode.ToLower())
            select lp.KpiId
        ).Distinct().ToListAsync();

        filter = redKpiIds;
      }

      // KPI-scoped actions for the selection
      var kpiScoped = await _db.KpiActions
          .AsNoTracking()
          .Include(a => a.Kpi)!.ThenInclude(k => k.Objective)
          .Include(a => a.Kpi)!.ThenInclude(k => k.Pillar)
          .Where(a => a.KpiId.HasValue && filter.Contains(a.KpiId.Value))
          .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
          .ToListAsync();

      // General actions (no KPI) — include on this page
      var general = await _db.KpiActions
          .AsNoTracking()
          .Where(a => a.KpiId == null)
          .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
          .ToListAsync();

      var actions = kpiScoped.Concat(general).ToList();

      static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
      static string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";

      var todo = actions.Where(a => string.Equals(a.StatusCode, "todo", StringComparison.OrdinalIgnoreCase)).ToList();
      var prog = actions.Where(a => string.Equals(a.StatusCode, "inprogress", StringComparison.OrdinalIgnoreCase)).ToList();
      var done = actions.Where(a => string.Equals(a.StatusCode, "done", StringComparison.OrdinalIgnoreCase)).ToList();

string Tile(KpiAction a, string title, string badgeClass)
{
    string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";

    string info;
    if (a.KpiId == null || a.IsGeneral)
    {
        info = "<div class='small text-muted mt-1'><span class='badge text-bg-info me-1'>General</span>General Action</div>";
    }
    else
    {
        string code = $"{H(a.Kpi?.Pillar?.PillarCode ?? "")}.{H(a.Kpi?.Objective?.ObjectiveCode ?? "")} {H(a.Kpi?.KpiCode ?? "")}";
        string name = H(a.Kpi?.KpiName ?? "-");
        string pillar = H(a.Kpi?.Pillar?.PillarName ?? "");
        string obj = H(a.Kpi?.Objective?.ObjectiveName ?? "");
        info = $@"
<div class='small text-muted mt-1'>
  KPI: <strong>{code}</strong> — {name}
  {(string.IsNullOrWhiteSpace(pillar) ? "" : $"<div>Pillar: {pillar}</div>")}
  {(string.IsNullOrWhiteSpace(obj) ? "" : $"<div>Objective: {obj}</div>")}
</div>";
    }

    return $@"
<div class='border rounded-3 p-2 bg-white'>
  <div class='d-flex justify-content-between align-items-center'>
    <strong>{H(a.Owner)}</strong>
    <span class='badge rounded-pill {badgeClass}'>{H(title)}</span>
  </div>
  {info}
  <div class='mt-1'>{H(a.Description)}</div>
  <div class='text-muted small mt-1'>Due: {F(a.DueDate)} • Ext: {a.ExtensionCount}</div>

  <!-- Buttons only (no inline status select) -->
  <div class='d-flex justify-content-end gap-2 mt-2'>
    <button type='button' class='btn btn-sm btn-outline-secondary ap-action-btn'
            data-action='edit-plan' data-id='{a.ActionId}'>Edit Plan</button>
    <button type='button' class='btn btn-sm btn-outline-secondary ap-action-btn'
            data-action='move-deadline' data-id='{a.ActionId}'>Move Deadline</button>
  </div>
</div>";
}
// REPLACE your existing Column(...) with this:
string Column(string title, string key, IEnumerable<KpiAction> items)
{
    var headerClass = key switch
    {
        "todo" => "ap-col-header ap-col-header--todo",
        "prog" => "ap-col-header ap-col-header--prog",
        _      => "ap-col-header ap-col-header--done"
    };

    var sb = new StringBuilder();
    sb.Append("<div class='col-md-4'>");
    sb.Append("<div class='ap-col'>");                                // column wrapper

    // full-width colored header bar
    sb.Append($"<div class='{headerClass}'>{WebUtility.HtmlEncode(title)}</div>");

    // tiles
    sb.Append("<div class='d-grid gap-2'>");
    if (!items.Any())
        sb.Append("<div class='text-muted small'>No items.</div>");
    else
        foreach (var a in items)
            sb.Append(Tile(a, title,
                key == "todo" ? "text-bg-secondary" :
                key == "prog" ? "text-bg-warning"  :
                                "text-bg-success"));
    sb.Append("</div>");   // tiles

    sb.Append("</div>");   // ap-col
    sb.Append("</div>");   // col
    return sb.ToString();
}
var html =
    "<div class='row g-3'>" +
    Column("To Do",       "todo", todo) +
    Column("In Progress", "prog", prog) +
    Column("Done",        "done", done) +
    "</div>";

return Content(html, "text/html");
    }
  }
}
