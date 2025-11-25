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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BoardHtml([FromBody] decimal[] kpiIds)
    {
      // Optional filter: if kpiIds are provided, we filter; otherwise we show ALL KPI-linked actions.
      IEnumerable<decimal> filter = kpiIds ?? Array.Empty<decimal>();

      // KPI-scoped actions
      var kpiQuery = _db.KpiActions
          .AsNoTracking()
          .Include(a => a.Kpi)!.ThenInclude(k => k.Objective)
          .Include(a => a.Kpi)!.ThenInclude(k => k.Pillar)
          .Where(a => a.KpiId.HasValue);

      if (filter.Any())
      {
        kpiQuery = kpiQuery.Where(a => a.KpiId.HasValue && filter.Contains(a.KpiId.Value));
      }

      var kpiScoped = await kpiQuery
          .OrderBy(a => a.StatusCode)
          .ThenBy(a => a.DueDate)
          .ToListAsync();

      // General actions (no KPI) — include on this page
      var general = await _db.KpiActions
          .AsNoTracking()
          .Where(a => a.KpiId == null || a.IsGeneral)
          .OrderBy(a => a.StatusCode)
          .ThenBy(a => a.DueDate)
          .ToListAsync();

      // Combine and drop archived
      var actions = kpiScoped
          .Concat(general)
          .Where(a => !string.Equals(a.StatusCode, "archived", StringComparison.OrdinalIgnoreCase))
          .ToList();

      var todo = actions
          .Where(a => string.Equals(a.StatusCode, "todo", StringComparison.OrdinalIgnoreCase))
          .ToList();
      var prog = actions
          .Where(a => string.Equals(a.StatusCode, "inprogress", StringComparison.OrdinalIgnoreCase))
          .ToList();
      var done = actions
          .Where(a => string.Equals(a.StatusCode, "done", StringComparison.OrdinalIgnoreCase))
          .ToList();

      // ----- CARD RENDERING (back to original layout, but with archive + cleaned owner) -----
      string Tile(KpiAction a, string title, string badgeClass)
      {
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";

        // Clean owner: strip "(12345)" if present
        string CleanOwner(string? raw)
        {
          var s = raw ?? "";
          var idx = s.IndexOf('(');
          if (idx > 0) s = s[..idx];
          return s.Trim();
        }

        var ownerDisplay = CleanOwner(a.Owner);

        // KPI vs General info block
        string info;
        if (a.KpiId == null || a.IsGeneral)
        {
          info = "<div class='small text-muted mt-1'><span class='badge text-bg-info me-1'>General</span>General Action</div>";
        }
        else
        {
          string code   = $"{H(a.Kpi?.Pillar?.PillarCode ?? "")} {H(a.Kpi?.Objective?.ObjectiveCode ?? "")} {H(a.Kpi?.KpiCode ?? "")}";
          string name   = H(a.Kpi?.KpiName ?? "-");
          string pillar = H(a.Kpi?.Pillar?.PillarName ?? "");
          string obj    = H(a.Kpi?.Objective?.ObjectiveName ?? "");

          info = $@"
<div class='small text-muted mt-1'>
  KPI: <strong>{code}</strong> — {name}
  {(string.IsNullOrWhiteSpace(pillar) ? "" : $"<div>Pillar: {pillar}</div>")}
  {(string.IsNullOrWhiteSpace(obj) ? "" : $"<div>Objective: {obj}</div>")}
</div>";
        }

        // Extension label only if > 0
        var extPart = a.ExtensionCount > 0
          ? $" • Ext: {a.ExtensionCount}"
          : "";

        return $@"
<div class='border rounded-3 p-2 bg-white'>
  <div class='d-flex justify-content-between align-items-center'>
    <strong>{H(ownerDisplay)}</strong>
    <span class='badge rounded-pill {badgeClass}'>{H(title)}</span>
  </div>

  {info}

  <div class='mt-1'>{H(a.Description)}</div>

  <div class='text-muted small mt-1'>
    Due: {F(a.DueDate)}{extPart}
  </div>

  <!-- buttons row at the bottom -->
  <div class='d-flex justify-content-end gap-2 mt-2'>
    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn'
            data-action='edit-plan'
            data-id='{a.ActionId}'
            asp-admin-only>
      Edit
    </button>
    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn'
            data-action='move-deadline'
            data-id='{a.ActionId}'
            asp-admin-only>
      Move deadline
    </button>
    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn'
            data-action='archive-action'
            data-id='{a.ActionId}'
            asp-admin-only>
      Archive
    </button>
  </div>
</div>";
      }

      string Column(string title, string key, IReadOnlyList<KpiAction> items)
      {
        var headerClass = key switch
        {
          "todo" => "ap-col-header ap-col-header--todo",
          "prog" => "ap-col-header ap-col-header--prog",
          _      => "ap-col-header ap-col-header--done"
        };

        var sb = new StringBuilder();
        sb.Append("<div class='col-md-4'>");
        sb.Append("<div class='ap-col'>");

        // the coloured bar at the top of each column
        sb.Append($"<div class='{headerClass}'>{WebUtility.HtmlEncode(title)}</div>");

        if (!items.Any())
        {
          sb.Append("<div class='text-muted small mt-2'>No items.</div>");
        }
        else
        {
          foreach (var a in items)
          {
            sb.Append("<div class='mt-2'>");
            sb.Append(Tile(
              a,
              title,
              key == "todo"
                ? "text-bg-secondary"
                : key == "prog"
                  ? "text-bg-warning"
                  : "text-bg-success"));
            sb.Append("</div>");
          }
        }

        sb.Append("</div>");   // .ap-col
        sb.Append("</div>");   // .col-md-4
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
