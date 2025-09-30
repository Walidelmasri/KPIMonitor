using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;
using KPIMonitor.ViewModels; // for KpiEditModalVm

namespace KPIMonitor.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly AppDbContext _db;
        private readonly IKpiAccessService _acl;
        private readonly global::IAdminAuthorizer _admin;

        public HomeController(
            ILogger<HomeController> logger,
            AppDbContext db,
            IKpiAccessService acl,
            global::IAdminAuthorizer admin)
        {
            _logger = logger;
            _db = db;
            _acl = acl;
            _admin = admin;
        }

        // Page
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
                period = LabelFor(f.Period),
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

                string pillarCode    = kpi?.Objective?.Pillar?.PillarCode ?? "";
                string objectiveCode = kpi?.Objective?.ObjectiveCode ?? "";
                string kpiCode       = kpi?.KpiCode ?? "";
                string left  = string.Join('.', new[] { pillarCode, objectiveCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
                    KpiId     = kpiId,
                    Year      = year,
                    IsMonthly = isMonthly,
                    KpiName   = displayTitle,
                    Unit      = string.IsNullOrWhiteSpace(plan.Unit) ? "—" : plan.Unit,
                    IsSuperAdmin = _admin.IsSuperAdmin(User)
                };
                vm.Actuals               ??= new Dictionary<int, decimal?>();
                vm.Forecasts             ??= new Dictionary<int, decimal?>();
                vm.EditableActualKeys    ??= new HashSet<int>();
                vm.EditableForecastKeys  ??= new HashSet<int>();
                vm.Targets               ??= new Dictionary<int, decimal?>();

                if (isMonthly)
                {
                    for (int m = 1; m <= 12; m++)
                    {
                        var f = facts.FirstOrDefault(x => x.Period!.MonthNum == m);
                        vm.Actuals[m]   = f?.ActualValue;
                        vm.Forecasts[m] = f?.ForecastValue;
                        vm.Targets[m]   = f?.TargetValue;
                    }

                    var w = PeriodEditPolicy.ComputeMonthlyWindow(year, DateTime.UtcNow, User);
                    vm.EditableActualKeys   = new HashSet<int>(w.ActualMonths ?? Enumerable.Empty<int>());
                    vm.EditableForecastKeys = new HashSet<int>(w.ForecastMonths ?? Enumerable.Empty<int>());
                }
                else
                {
                    for (int q = 1; q <= 4; q++)
                    {
                        var f = facts.FirstOrDefault(x => x.Period!.QuarterNum == q);
                        vm.Actuals[q]   = f?.ActualValue;
                        vm.Forecasts[q] = f?.ForecastValue;
                        vm.Targets[q]   = f?.TargetValue;
                    }

                    var w = PeriodEditPolicy.ComputeQuarterlyWindow(year, DateTime.UtcNow, User);
                    vm.EditableActualKeys   = new HashSet<int>(w.ActualQuarters ?? Enumerable.Empty<int>());
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
    }
}
