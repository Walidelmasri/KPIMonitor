using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using System.Text;
using System.Net;
using KPIMonitor.Helpers;
using KPIMonitor.Services;

namespace KPIMonitor.Controllers
{
    public class ActionPlansController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmployeeDirectory _empDir;

        public ActionPlansController(AppDbContext db, IEmployeeDirectory empDir)
        {
            _db = db;
            _empDir = empDir;
        }

        private bool IsArabicUi()
        {
            return System.Globalization.CultureInfo.CurrentUICulture.Name
                .StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string?> ResolveEmployeeNameAsync(string? empId)
        {
            var id = (empId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var pick = await _empDir.TryGetByEmpIdAsync(id);
            if (!pick.HasValue)
                return null;

            var value = pick.Value;

            var name = IsArabicUi()
                ? (!string.IsNullOrWhiteSpace(value.NameAr) ? value.NameAr! : (value.NameEng ?? ""))
                : (!string.IsNullOrWhiteSpace(value.NameEng) ? value.NameEng : (value.NameAr ?? ""));

            name = (name ?? "").Trim();

            var idx = name.IndexOf('(');
            if (idx > 0)
                name = name.Substring(0, idx).Trim();

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private string LastNameFromFullName(string? full)
        {
            var s = (full ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return "";

            var idx = s.IndexOf('(');
            if (idx > 0) s = s.Substring(0, idx).Trim();

            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "" : parts[^1];
        }

        private string CleanOwner(string? raw)
        {
            var s = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) return "";

            var idx = s.IndexOf('(');
            if (idx <= 0) return s;

            var end = s.IndexOf(')', idx + 1);
            if (end < 0) return s;

            var inside = s.Substring(idx + 1, end - idx - 1).Trim();

            if (inside.StartsWith("+")) return s;

            var digitsOnly = inside.All(char.IsDigit);
            if (digitsOnly) return s.Substring(0, idx).Trim();

            return s;
        }

        [HttpGet]
        public IActionResult Index() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BoardHtml([FromBody] decimal[] kpiIds)
        {
            try
            {
                var isArabic = System.Globalization.CultureInfo.CurrentUICulture.Name
                    .StartsWith("ar", StringComparison.OrdinalIgnoreCase);

                string T(string en, string ar) => isArabic ? ar : en;
                string Pick(string? ar, string? en) => isArabic
                    ? (!string.IsNullOrWhiteSpace(ar) ? ar! : (en ?? ""))
                    : (!string.IsNullOrWhiteSpace(en) ? en! : (ar ?? ""));

                IEnumerable<decimal> filter = kpiIds ?? Array.Empty<decimal>();

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

                var general = await _db.KpiActions
                    .AsNoTracking()
                    .Where(a => a.KpiId == null)
                    .OrderBy(a => a.StatusCode)
                    .ThenBy(a => a.DueDate)
                    .ToListAsync();

                var actions = kpiScoped
                    .Concat(general)
                    .Where(a => !string.Equals(a.StatusCode, "archived", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var actionIds = actions.Select(a => a.ActionId).ToList();

                var ownersByActionId = actionIds.Count == 0
                    ? new List<KpiActionOwner>()
                    : await _db.KpiActionOwners
                        .AsNoTracking()
                        .Where(o => actionIds.Contains(o.ActionId))
                        .OrderBy(o => o.KpiActionOwnerId)
                        .ToListAsync();

                var ownersLookup = ownersByActionId
                    .GroupBy(o => o.ActionId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var todo = actions
                    .Where(a => string.Equals(a.StatusCode, "todo", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var prog = actions
                    .Where(a => string.Equals(a.StatusCode, "inprogress", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var done = actions
                    .Where(a => string.Equals(a.StatusCode, "done", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                string H(string? s) => WebUtility.HtmlEncode(s ?? "");
                string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";

                async Task<string> TileOwnerHeaderAsync(decimal actionId, string? fallbackOwner)
                {
                    if (ownersLookup.TryGetValue(actionId, out var owners) && owners != null)
                    {
                        var resolvedNames = new List<string>();

                        foreach (var o in owners)
                        {
                            var name = await ResolveEmployeeNameAsync(o.OwnerEmpId);

                            if (string.IsNullOrWhiteSpace(name))
                                name = string.IsNullOrWhiteSpace(o.OwnerName) ? o.OwnerEmpId : o.OwnerName!.Trim();

                            if (!string.IsNullOrWhiteSpace(name))
                                resolvedNames.Add(name);
                        }

                        if (resolvedNames.Count >= 2)
                        {
                            var lastNames = resolvedNames
                                .Select(LastNameFromFullName)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .ToList();

                            return lastNames.Count == 0 ? CleanOwner(fallbackOwner) : string.Join(", ", lastNames);
                        }

                        if (resolvedNames.Count == 1)
                        {
                            return resolvedNames[0];
                        }
                    }

                    return CleanOwner(fallbackOwner);
                }

                async Task<string> TileAsync(KpiAction a, string title, string badgeClass)
                {
                    var ownerDisplay = await TileOwnerHeaderAsync(a.ActionId, a.Owner);

                    string info;
                    if (a.KpiId == null)
                    {
                        info = $@"<div class='small text-muted mt-1'><span class='badge text-bg-info me-1'>{H(T("General", "عام"))}</span>{H(T("General Action", "إجراء عام"))}</div>";
                    }
                    else
                    {
                        string code = $"{H(a.Kpi?.Pillar?.PillarCode ?? "")} {H(a.Kpi?.Objective?.ObjectiveCode ?? "")} {H(a.Kpi?.KpiCode ?? "")}".Trim();
                        string name = H(a.Kpi != null ? Pick(a.Kpi.KpiNameAr, a.Kpi.KpiName ?? "-") : "-");
                        string pillar = H(a.Kpi?.Pillar != null ? Pick(a.Kpi.Pillar.PillarNameAr, a.Kpi.Pillar.PillarName ?? "") : "");
                        string obj = H(a.Kpi?.Objective != null ? Pick(a.Kpi.Objective.ObjectiveNameAr, a.Kpi.Objective.ObjectiveName ?? "") : "");

                        info = $@"
<div class='small text-muted mt-1'>
  {H(T("KPI", "المؤشر"))}: <strong><span dir='ltr' style='unicode-bidi:isolate;'>{code}</span></strong> — {name}
  {(string.IsNullOrWhiteSpace(pillar) ? "" : $"<div>{H(T("Pillar", "الركيزة"))}: {pillar}</div>")}
  {(string.IsNullOrWhiteSpace(obj) ? "" : $"<div>{H(T("Objective", "الهدف"))}: {obj}</div>")}
</div>";
                    }

                    var extPart = a.ExtensionCount > 0
                        ? $" • {H(T("Ext", "تمديد"))}: {a.ExtensionCount}"
                        : "";

                    return $@"
<div class='border rounded-3 p-2 bg-white ap-tile'
     role='button'
     tabindex='0'
     data-action='open-details'
     data-id='{a.ActionId}'
     style='cursor:pointer;'>
  <div class='d-flex justify-content-between align-items-center'>
    <strong>{H(ownerDisplay)}</strong>
    <span class='badge rounded-pill {badgeClass}'>{H(title)}</span>
  </div>

  {info}

  <div class='mt-1'>{H(LocalizationHelper.Get(a.DescriptionAr, a.Description ?? ""))}</div>
  <div class='text-muted small mt-1'>
    {H(T("Due", "الاستحقاق"))}: {F(a.DueDate)}{extPart}
  </div>

  <div class='d-flex justify-content-end gap-2 mt-2'>
    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn ap-stop-tile'
            data-action='comment'
            data-id='{a.ActionId}'>
      {H(T("Comment", "تعليق"))}
    </button>

    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn ap-stop-tile'
            data-action='edit-plan'
            data-id='{a.ActionId}'
            asp-admin-only>
      {H(T("Edit", "تعديل"))}
    </button>

    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn ap-stop-tile'
            data-action='move-deadline'
            data-id='{a.ActionId}'
            asp-admin-only>
      {H(T("Move deadline", "تغيير الموعد النهائي"))}
    </button>

    <button type='button'
            class='btn btn-sm btn-outline-secondary ap-action-btn ap-stop-tile'
            data-action='archive-action'
            data-id='{a.ActionId}'
            asp-admin-only>
      {H(T("Archive", "أرشفة"))}
    </button>
  </div>
</div>";
                }

                async Task<string> ColumnAsync(string title, string key, IReadOnlyList<KpiAction> items)
                {
                    var headerClass = key switch
                    {
                        "todo" => "ap-col-header ap-col-header--todo",
                        "prog" => "ap-col-header ap-col-header--prog",
                        _ => "ap-col-header ap-col-header--done"
                    };

                    var sb = new StringBuilder();
                    sb.Append("<div class='col-md-4'>");
                    sb.Append("<div class='ap-col'>");
                    sb.Append($"<div class='{headerClass}'>{H(title)}</div>");

                    if (!items.Any())
                    {
                        sb.Append($"<div class='text-muted small mt-2'>{H(T("No items.", "لا توجد عناصر."))}</div>");
                    }
                    else
                    {
                        foreach (var a in items)
                        {
                            sb.Append("<div class='mt-2'>");
                            sb.Append(await TileAsync(
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

                    sb.Append("</div>");
                    sb.Append("</div>");
                    return sb.ToString();
                }

                var html =
                    "<div class='row g-3'>" +
                    await ColumnAsync(T("To Do", "المطلوب تنفيذها"), "todo", todo) +
                    await ColumnAsync(T("In Progress", "قيد التنفيذ"), "prog", prog) +
                    await ColumnAsync(T("Done", "المنجزة"), "done", done) +
                    "</div>";

                return Content(html, "text/html");
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                var msg =
                    "BoardHtml failed: " +
                    $"{root.GetType().Name}: {root.Message}\n" +
                    (root.StackTrace ?? "(no stack)");

                return StatusCode(500, msg);
            }
        }
    }
}