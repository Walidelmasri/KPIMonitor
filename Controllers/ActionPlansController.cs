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

      // Local helpers
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
        var status = (a.StatusCode ?? "").ToLowerInvariant();

        string statusLabel = status switch
        {
          "todo" => "To Do",
          "inprogress" => "In Progress",
          "done" => "Done",
          "archived" => "Archived",
          _ => status
        };

        var statusBadge = string.IsNullOrWhiteSpace(statusLabel)
          ? ""
          : $"<span class='badge {badgeClass} ms-2'>{H(statusLabel)}</span>";

        // info block: general vs KPI-linked
        string info;
        if (a.KpiId == null || a.IsGeneral)
        {
          info = "<div class='small text-muted mt-1'>" +
                 "<span class='badge text-bg-info me-1'>General</span>General Action" +
                 "</div>";
        }
        else
        {
          string code = $"{H(a.Kpi?.Pillar?.PillarCode ?? "")} {H(a.Kpi?.Objective?.ObjectiveCode ?? "")} {H(a.Kpi?.KpiCode ?? "")}";
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

        var ext =
          a.ExtensionCount > 0
          ? $"<span class='badge text-bg-light ms-1'>+{a.ExtensionCount} ext</span>"
          : "";

        return $@"
<div class='border rounded-3 p-2 bg-white'>
  <div class='d-flex justify-content-between align-items-start'>
    <div>
      <strong>{H(ownerDisplay)}</strong>
      {statusBadge}
    </div>
    <div class='d-flex gap-1'>
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
  </div>

  {info}

  <div class='small mt-2'>
    <div><strong>Due:</strong> {F(a.DueDate)}{ext}</div>
    <div><strong>Assigned:</strong> {F(a.AssignedAt)}</div>
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
        sb.Append("<div class='ap-col'>");                                // column wrapper

        // full-width colored header bar
        sb.Append($"<div class='{headerClass}'>{WebUtility.HtmlEncode(title)}</div>");

        // tiles
        sb.Append("<div class='d-grid gap-2'>");
        if (!items.Any())
        {
          sb.Append("<div class='text-muted small'>No items.</div>");
        }
        else
        {
          foreach (var a in items)
          {
            sb.Append(Tile(a, title,
              key == "todo" ? "text-bg-secondary" :
              key == "prog" ? "text-bg-warning"  :
                              "text-bg-success"));
          }
        }
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
