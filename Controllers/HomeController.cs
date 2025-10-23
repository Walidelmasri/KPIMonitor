using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;                 // IEmployeeDirectory
using KPIMonitor.ViewModels;               // KpiEditModalVm
using KPIMonitor.Services.Abstractions;    // IKpiAccessService, IAdminAuthorizer, IKpiStatusService

namespace KPIMonitor.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly AppDbContext _db;
        private readonly IKpiAccessService _acl;
        private readonly global::IAdminAuthorizer _admin;
        private readonly IEmployeeDirectory _dir;

        public HomeController(
            ILogger<HomeController> logger,
            AppDbContext db,
            IKpiAccessService acl,
            global::IAdminAuthorizer admin,
            IEmployeeDirectory dir)
        {
            _logger = logger;
            _db = db;
            _acl = acl;
            _admin = admin;
            _dir = dir;
        }

        // --------------------------
        // helpers (same normalization you use elsewhere)
        // --------------------------
        private static string Sam(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\');             // DOMAIN\user
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');                  // user@domain
            if (at > 0) s = s[..at];
            return s.Trim();
        }
        private string Sam() => Sam(User?.Identity?.Name);

        private async Task<string?> MyEmpIdAsync(CancellationToken ct = default)
        {
            var sam = Sam();
            if (string.IsNullOrWhiteSpace(sam)) return null;
            var rec = await _dir.TryGetByUserIdAsync(sam, ct);
            return rec?.EmpId; // BADEA_ADDONS.EMPLOYEES.EMP_ID
        }

        // --------------------------
        // Pages
        // --------------------------
        public IActionResult Index() => View();
        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        // --------------------------
        // JSON endpoints used by Index.cshtml
        // --------------------------

        [HttpGet]
        public async Task<IActionResult> GetPillars()
        {
            var data = await _db.DimPillars
                .AsNoTracking()
                .Where(p => p.IsActive == 1)
                .OrderBy(p => p.PillarCode)
                .Select(p => new
                {
                    id = p.PillarId,
                    name = (p.PillarCode ?? "") + " — " + (p.PillarName ?? "")
                })
                .ToListAsync();

            return Json(data);
        }

        [HttpGet]
        public async Task<IActionResult> GetObjectives(decimal pillarId)
        {
            var data = await _db.DimObjectives
                .AsNoTracking()
                .Where(o => o.PillarId == pillarId && o.IsActive == 1)
                .OrderBy(o => o.ObjectiveCode)
                .Select(o => new
                {
                    id = o.ObjectiveId,
                    name = (o.ObjectiveCode ?? "") + " — " + (o.ObjectiveName ?? "")
                })
                .ToListAsync();

            return Json(data);
        }

        [HttpGet]
        public async Task<IActionResult> GetKpis(decimal objectiveId)
        {
            var data = await _db.DimKpis
                .AsNoTracking()
                .Where(k => k.ObjectiveId == objectiveId && k.IsActive == 1)
                .OrderBy(k => k.KpiCode)
                .Select(k => new
                {
                    id = k.KpiId,
                    name = (k.KpiCode ?? "") + " — " + (k.KpiName ?? "")
                })
                .ToListAsync();

            return Json(data);
        }

        // The big one: meta + period series + 5-year targets
        [HttpGet]
        public async Task<IActionResult> GetKpiSummary(decimal kpiId)
        {
            var plan = await _db.KpiYearPlans
                .Include(p => p.Period)
                .AsNoTracking()
                .Where(p => p.KpiId == kpiId && p.IsActive == 1 && p.Period != null)
                .OrderByDescending(p => p.KpiYearPlanId)
                .FirstOrDefaultAsync();

            var planId = plan?.KpiYearPlanId ?? 0;
            var canEdit = plan != null && await _acl.CanEditPlanAsync(planId, User);

            if (plan == null || plan.Period == null)
            {
                return Json(new
                {
                    meta = new
                    {
                        owner = "—",
                        editor = "—",
                        valueType = "—",
                        unit = "—",
                        priority = (int?)null,
                        statusLabel = "—",
                        statusColor = "",
                        statusRaw = ""
                    },
                    chart = new
                    {
                        labels = Array.Empty<string>(),
                        actual = Array.Empty<decimal?>(),
                        target = Array.Empty<decimal?>(),
                        forecast = Array.Empty<decimal?>(),
                        yearTargets = Array.Empty<object>()
                    },
                    table = Array.Empty<object>()
                });
            }

            int planYear = plan.Period.Year;

            var facts = await _db.KpiFacts
                .Include(f => f.Period)
                .AsNoTracking()
                .Where(f => f.KpiId == kpiId
                         && f.IsActive == 1
                         && f.KpiYearPlanId == plan.KpiYearPlanId
                         && f.Period != null
                         && f.Period.Year == planYear)
                .OrderBy(f => f.Period!.StartDate)
                .ToListAsync();

            var factIds = facts.Select(f => f.KpiFactId).ToList();

            var pendingFactIds = await _db.KpiFactChanges
                .AsNoTracking()
                .Where(ch => ch.ApprovalStatus == "pending" && factIds.Contains(ch.KpiFactId))
                .Select(ch => ch.KpiFactId)
                .Distinct()
                .ToListAsync();

            var pendingSet = new HashSet<decimal>(pendingFactIds);

            static string LabelFor(DimPeriod p)
            {
                if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
                if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
                return p.Year.ToString();
            }

            var labels = facts.Select(f => LabelFor(f.Period!)).ToList();
            var actual = facts.Select(f => (decimal?)f.ActualValue).ToList();
            var target = facts.Select(f => (decimal?)f.TargetValue).ToList();
            var forecast = facts.Select(f => (decimal?)f.ForecastValue).ToList();

            var lastWithStatus = facts.LastOrDefault(f => !string.IsNullOrWhiteSpace(f.StatusCode));
            string? latestStatusCode = lastWithStatus?.StatusCode;

            (string label, string color) status = latestStatusCode?.Trim().ToLowerInvariant() switch
            {
                "green" => ("Ok", "#28a745"),
                "red" => ("Needs Attention", "#dc3545"),
                "orange" => ("Catching Up", "#fd7e14"),
                "blue" => ("Data Missing", "#0d6efd"),
                "conforme" => ("Ok", "#28a745"),
                "ecart" => ("Needs Attention", "#dc3545"),
                "rattrapage" => ("Catching Up", "#fd7e14"),
                "attente" => ("Data Missing", "#0d6efd"),
                _ => ("—", "")
            };

            var fy = await _db.KpiFiveYearTargets
                .AsNoTracking()
                .Where(t => t.KpiId == kpiId && t.IsActive == 1)
                .OrderByDescending(t => t.BaseYear)
                .FirstOrDefaultAsync();

            var yearTargets = new List<object>();
            if (fy != null)
            {
                void Add(int offset, decimal? v)
                {
                    if (v.HasValue) yearTargets.Add(new { year = fy.BaseYear + offset, value = v.Value });
                }
                Add(0, fy.Period1Value);
                Add(1, fy.Period2Value);
                Add(2, fy.Period3Value);
                Add(3, fy.Period4Value);
                Add(4, fy.Period5Value);
            }

            var table = facts.Select(f => new
            {
                id = f.KpiFactId,
                period = LabelFor(f.Period!),
                startDate = f.Period?.StartDate?.ToString("yyyy-MM-dd") ?? "—",
                endDate = f.Period?.EndDate?.ToString("yyyy-MM-dd") ?? "—",

                actual = f.ActualValue?.ToString("0.###"),
                target = f.TargetValue?.ToString("0.###"),
                forecast = f.ForecastValue?.ToString("0.###"),
                statusCode = f.StatusCode,
                lastBy = f.LastChangedBy,

                hasPending = pendingSet.Contains(f.KpiFactId)
            }).ToList();

            var kpi = await _db.DimKpis
                .Include(x => x.Objective)
                    .ThenInclude(o => o.Pillar)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.KpiId == kpiId);

            var meta = new
            {
                title = kpi?.KpiName ?? "—",
                code = kpi?.KpiCode ?? "",
                pillarCode = kpi?.Objective?.Pillar?.PillarCode ?? "",
                objectiveCode = kpi?.Objective?.ObjectiveCode ?? "",

                owner = plan.Owner ?? "—",
                editor = plan.Editor ?? "—",
                valueType = string.IsNullOrWhiteSpace(plan.Frequency) ? "—" : plan.Frequency,
                unit = string.IsNullOrWhiteSpace(plan.Unit) ? "—" : plan.Unit,
                priority = plan.Priority,
                statusLabel = status.label,
                statusColor = status.color,
                statusRaw = string.IsNullOrWhiteSpace(latestStatusCode) ? "" : latestStatusCode,

                planId = planId,
                canEdit = canEdit
            };

            return Json(new
            {
                meta,
                chart = new
                {
                    labels,
                    actual,
                    target,
                    forecast,
                    yearTargets
                },
                table
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetKpiFact(decimal id)
        {
            var f = await _db.KpiFacts.AsNoTracking()
                .Include(x => x.Period)
                .FirstOrDefaultAsync(x => x.KpiFactId == id && x.IsActive == 1);

            if (f == null) return NotFound();

            return Json(new
            {
                id = f.KpiFactId,
                period = f.Period != null
                    ? (f.Period.MonthNum.HasValue ? $"{f.Period.Year} — {new DateTime(f.Period.Year, f.Period.MonthNum.Value, 1):MMM}"
                       : f.Period.QuarterNum.HasValue ? $"{f.Period.Year} — Q{f.Period.QuarterNum.Value}"
                       : f.Period.Year.ToString())
                    : "—",
                actual = f.ActualValue,
                target = f.TargetValue,
                forecast = f.ForecastValue,
                statusCode = f.StatusCode
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateKpiFact(
            [Bind("KpiFactId,ActualValue,TargetValue, ForecastValue,StatusCode, LastChangedBy")] KpiFact input,
            decimal? pillarId, decimal? objectiveId, decimal? kpiId)
        {
            if (input == null || input.KpiFactId == 0)
                return BadRequest("Missing id.");

            var fact = await _db.KpiFacts.FirstOrDefaultAsync(x => x.KpiFactId == input.KpiFactId);
            if (fact == null) return NotFound("Fact not found.");
            if (!await _acl.CanEditPlanAsync(fact.KpiYearPlanId, User))
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return StatusCode(403, new { ok = false, error = "You do not have access to edit these facts." });

                return Forbid();
            }

            fact.ActualValue = input.ActualValue;
            fact.TargetValue = input.TargetValue;
            fact.ForecastValue = input.ForecastValue;
            fact.StatusCode = input.StatusCode;

            fact.LastChangedBy = string.IsNullOrWhiteSpace(input.LastChangedBy)
                ? fact.LastChangedBy
                : input.LastChangedBy;
            await _db.SaveChangesAsync();

            // Recompute plan-year so status reflects the freshly-saved values
            var statusSvc = HttpContext.RequestServices.GetRequiredService<IKpiStatusService>();
            var yr = await _db.DimPeriods.Where(p => p.PeriodId == fact.PeriodId)
                                         .Select(p => p.Year)
                                         .FirstAsync();
            await statusSvc.RecomputePlanYearAsync(fact.KpiYearPlanId, yr);

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Ok(new { ok = true });

            return RedirectToAction("Index", "Home", new { pillarId, objectiveId, kpiId });
        }

        // GET: modal with Actual + Forecast (+ Target for superadmin) grid for the active plan
        [HttpGet]
        public async Task<IActionResult> EditKpiFactsModal(decimal kpiId)
        {
            try
            {
                var plan = await _db.KpiYearPlans
                    .Include(p => p.Period)
                    .AsNoTracking()
                    .Where(p => p.KpiId == kpiId && p.IsActive == 1 && p.Period != null)
                    .OrderByDescending(p => p.KpiYearPlanId)
                    .FirstOrDefaultAsync();

                if (plan == null || plan.Period == null)
                    return Content("No active year plan found for this KPI.", "text/plain");

                var canEdit = await _acl.CanEditPlanAsync(plan.KpiYearPlanId, User);
                if (!canEdit) return StatusCode(403, "Not allowed");

                int year = plan.Period.Year;

                var kpi = await _db.DimKpis
                    .Include(x => x.Objective)
                        .ThenInclude(o => o.Pillar)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(k => k.KpiId == kpiId);

                string pillarCode = kpi?.Objective?.Pillar?.PillarCode ?? "";
                string objectiveCode = kpi?.Objective?.ObjectiveCode ?? "";
                string kpiCode = kpi?.KpiCode ?? "";
                string left = string.Join('.', new[] { pillarCode, objectiveCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
                string prefix = string.Join(' ', new[] { left, kpiCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
                string displayTitle = string.IsNullOrWhiteSpace(prefix) ? (kpi?.KpiName ?? "—") : $"{prefix} — {kpi?.KpiName ?? "—"}";

                var facts = await _db.KpiFacts
                    .Include(f => f.Period)
                    .AsNoTracking()
                    .Where(f => f.KpiId == kpiId
                             && f.IsActive == 1
                             && f.KpiYearPlanId == plan.KpiYearPlanId
                             && f.Period != null
                             && f.Period.Year == year)
                    .OrderBy(f => f.Period!.StartDate)
                    .ToListAsync();

                bool isMonthly = facts.Any(f => f.Period!.MonthNum.HasValue);

                var vm = new KpiEditModalVm
                {
                    KpiId = kpiId,
                    Year = year,
                    IsMonthly = isMonthly,
                    KpiName = displayTitle,
                    Unit = string.IsNullOrWhiteSpace(plan.Unit) ? "—" : plan.Unit,
                    IsSuperAdmin = _admin.IsSuperAdmin(User)
                };
                vm.Actuals ??= new Dictionary<int, decimal?>();
                vm.Forecasts ??= new Dictionary<int, decimal?>();
                vm.EditableActualKeys ??= new HashSet<int>();
                vm.EditableForecastKeys ??= new HashSet<int>();
                vm.Targets ??= new Dictionary<int, decimal?>();

                if (isMonthly)
                {
                    for (int m = 1; m <= 12; m++)
                    {
                        var f = facts.FirstOrDefault(x => x.Period!.MonthNum == m);
                        vm.Actuals[m] = f?.ActualValue;
                        vm.Forecasts[m] = f?.ForecastValue;
                        vm.Targets[m] = f?.TargetValue;
                    }

                    var w = PeriodEditPolicy.ComputeMonthlyWindow(year, DateTime.UtcNow, User);
                    vm.EditableActualKeys = new HashSet<int>(w.ActualMonths ?? Enumerable.Empty<int>());
                    vm.EditableForecastKeys = new HashSet<int>(w.ForecastMonths ?? Enumerable.Empty<int>());
                }
                else
                {
                    for (int q = 1; q <= 4; q++)
                    {
                        var f = facts.FirstOrDefault(x => x.Period!.QuarterNum == q);
                        vm.Actuals[q] = f?.ActualValue;
                        vm.Forecasts[q] = f?.ForecastValue;
                        vm.Targets[q] = f?.TargetValue;
                    }

                    var w = PeriodEditPolicy.ComputeQuarterlyWindow(year, DateTime.UtcNow, User);
                    vm.EditableActualKeys = new HashSet<int>(w.ActualQuarters ?? Enumerable.Empty<int>());
                    vm.EditableForecastKeys = new HashSet<int>(w.ForecastQuarters ?? Enumerable.Empty<int>());
                }

                return PartialView("_EditKpiFactsModal", vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EditKpiFactsModal failed for KPI {KpiId}", kpiId);

                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return StatusCode(500, $"EditKpiFactsModal error: {ex.GetType().Name}: {ex.Message}");

                throw;
            }
        }

        // --------------------------
        // New: roles for the dashboard card (Editor/Owner by EmpId via Year Plans)
        // --------------------------
        /// Returns the KPIs where the current user is Editor and/or Owner.
        /// JSON: { editor: [{id, text}], owner: [{id, text}] }
        /// Optional filters: pillarId, objectiveId (purely for payload trimming).
        [HttpGet]
        public async Task<IActionResult> MyKpiRoles(int? pillarId, int? objectiveId, CancellationToken ct = default)
        {
            var myEmp = await MyEmpIdAsync(ct);
            if (string.IsNullOrWhiteSpace(myEmp))
                return Json(new { editor = Array.Empty<object>(), owner = Array.Empty<object>() });

            // Include Kpi -> Objective -> Pillar to build labels and expose path
            var plans = _db.KpiYearPlans
                .AsNoTracking()
                .Include(p => p.Kpi)
                    .ThenInclude(k => k.Objective!)
                        .ThenInclude(o => o.Pillar)
                .Where(p => p.IsActive == 1 && p.Kpi != null);

            if (pillarId.HasValue)
                plans = plans.Where(p => p.Kpi!.PillarId == pillarId.Value);

            if (objectiveId.HasValue)
                plans = plans.Where(p => p.Kpi!.ObjectiveId == objectiveId.Value);

            // Common projection (keep the path fields!)
            var baseQuery = plans.Select(p => new
            {
                p.KpiId,
                p.Kpi!.PillarId,
                p.Kpi!.ObjectiveId,
                PillarCode = p.Kpi!.Objective!.Pillar!.PillarCode,
                ObjectiveCode = p.Kpi!.Objective!.ObjectiveCode,
                KpiCode = p.Kpi!.KpiCode,
                KpiName = p.Kpi!.KpiName
            });

            // Editor KPIs
            var editorRaw = await baseQuery
                .Where(p => _db.KpiYearPlans.Any(q =>
                    q.IsActive == 1 &&
                    q.KpiId == p.KpiId &&
                    q.EditorEmpId != null &&
                    q.EditorEmpId == myEmp))
                .ToListAsync(ct);

            // Owner KPIs
            var ownerRaw = await baseQuery
                .Where(p => _db.KpiYearPlans.Any(q =>
                    q.IsActive == 1 &&
                    q.KpiId == p.KpiId &&
                    q.OwnerEmpId != null &&
                    q.OwnerEmpId == myEmp))
                .ToListAsync(ct);

            static string Label(dynamic x)
            {
                var left = string.Join('.',
                    new[] { x.PillarCode as string, x.ObjectiveCode as string }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                var code = string.IsNullOrWhiteSpace(x.KpiCode as string) ? "" : (left?.Length > 0 ? $" {x.KpiCode}" : x.KpiCode);
                var head = (left?.Length > 0 ? left : "") + code;
                var name = string.IsNullOrWhiteSpace(x.KpiName as string) ? "-" : x.KpiName;
                return string.IsNullOrWhiteSpace(head) ? name : $"{head} — {name}";
            }

            // De-dup by KPI, but KEEP pillar/objective ids for the client
            var editor = editorRaw
                .GroupBy(x => x.KpiId)
                .Select(g =>
                {
                    var f = g.First();
                    return new
                    {
                        id = f.KpiId,
                        pillarId = f.PillarId,
                        objectiveId = f.ObjectiveId,
                        text = Label(f)
                    };
                })
                .OrderBy(x => x.text)
                .Take(200)
                .ToList();

            var owner = ownerRaw
                .GroupBy(x => x.KpiId)
                .Select(g =>
                {
                    var f = g.First();
                    return new
                    {
                        id = f.KpiId,
                        pillarId = f.PillarId,
                        objectiveId = f.ObjectiveId,
                        text = Label(f)
                    };
                })
                .OrderBy(x => x.text)
                .Take(200)
                .ToList();

            return Json(new { editor, owner });
        }
[HttpGet]
public async Task<IActionResult> GetDashboardSummary(CancellationToken ct = default)
{
    // ---- Latest active plan per KPI (EF-safe) ----
    // 1) Get latest plan id per KPI (by max KpiYearPlanId) where plan is active and has a Period.
    var latestPlanIds = await _db.KpiYearPlans
        .AsNoTracking()
        .Where(p => p.IsActive == 1 && p.PeriodId != null)
        .GroupBy(p => p.KpiId)
        .Select(g => g.Max(p => p.KpiYearPlanId))
        .ToListAsync(ct);

    if (latestPlanIds.Count == 0)
    {
        return Json(new
        {
            kpiStatus = new { green = 0, orange = 0, red = 0, blue = 0, unknown = 0 },
            actionStatus = new { todo = 0, inprogress = 0, done = 0, other = 0 },
            trend = (object?)null,
            updatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
        });
    }

    // 2) For those plans, get (KpiId, PlanId, Year) so we can scope facts to the plan year.
    var planTriples = await _db.KpiYearPlans
        .AsNoTracking()
        .Where(p => latestPlanIds.Contains(p.KpiYearPlanId))
        .Select(p => new { p.KpiId, p.KpiYearPlanId, Year = p.Period!.Year })
        .ToListAsync(ct);

    var planIdSet = latestPlanIds.ToHashSet();

    // 3) Pull facts for those plans (minimal columns) – server-side filter by PlanId, then (in-memory) match Year.
    var facts = await _db.KpiFacts
        .AsNoTracking()
        .Where(f => f.IsActive == 1
                 && f.StatusCode != null
                 && planIdSet.Contains(f.KpiYearPlanId))
        .Select(f => new
        {
            f.KpiId,
            f.KpiYearPlanId,
            f.StatusCode,
            PeriodYear = f.Period!.Year,
            Start = f.Period!.StartDate
        })
        .ToListAsync(ct);

    // 4) Keep only facts whose Period.Year == plan.Year for that KPI/Plan
    var planByPlanId = planTriples.ToDictionary(x => x.KpiYearPlanId, x => x);
    var sameYearFacts = facts.Where(f =>
    {
        if (!planByPlanId.TryGetValue(f.KpiYearPlanId, out var p)) return false;
        return f.PeriodYear == p.Year;
    });

    // 5) For each KPI, take the latest fact (by Period.StartDate)
    var latestStatusPerKpi = sameYearFacts
        .GroupBy(f => f.KpiId)
        .Select(g => g.OrderByDescending(x => x.KpiYearPlanId)     // tie-breaker if multiple plans leaked
                      .ThenByDescending(x => x.Start)
                      .Select(x => x.StatusCode)
                      .FirstOrDefault())
        .ToList();

    // Canonicalize + count
    static string CanonStatus(string? code)
    {
        var s = (code ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "green" or "conforme" or "ok" => "green",
            "red" or "ecart" or "needs attention" => "red",
            "orange" or "rattrapage" or "catching up" => "orange",
            "blue" or "attente" or "data missing" => "blue",
            _ => "unknown"
        };
    }

    var kpiCounts = latestStatusPerKpi
        .Select(CanonStatus)
        .GroupBy(s => s)
        .ToDictionary(g => g.Key, g => g.Count());

    int KC(string k) => kpiCounts.TryGetValue(k, out var n) ? n : 0;
    var kpiStatus = new
    {
        green = KC("green"),
        orange = KC("orange"),
        red = KC("red"),
        blue = KC("blue"),
        unknown = KC("unknown")
    };

    // ---- Action plan totals (one action = one row) ----
    var actionStatusRaw = await _db.KpiActions
        .AsNoTracking()
        .Where(a => a.StatusCode != null)
        .Select(a => a.StatusCode!)
        .ToListAsync(ct);

    static string CanonAction(string code)
    {
        var s = (code ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "todo" => "todo",
            "in progress" or "in-progress" or "doing" or "working" => "inprogress",
            "done" or "completed" or "complete" => "done",
            _ => "other"
        };
    }

    var actionCounts = actionStatusRaw
        .Select(CanonAction)
        .GroupBy(s => s)
        .ToDictionary(g => g.Key, g => g.Count());

    int AC(string k) => actionCounts.TryGetValue(k, out var n) ? n : 0;
    var actionStatus = new
    {
        todo = AC("todo"),
        inprogress = AC("inprogress"),
        done = AC("done"),
        other = AC("other")
    };

    // Trend: disable (returns null). Your UI hides it automatically.
    object? trend = null;

    return Json(new
    {
        kpiStatus,
        actionStatus,
        trend,
        updatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
    });
}
    }
}
