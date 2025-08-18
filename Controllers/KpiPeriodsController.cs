using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class KpiPeriodsController : Controller
    {
        private readonly AppDbContext _db;
        public KpiPeriodsController(AppDbContext db) => _db = db;

        // GET: /KpiPeriods
        // Optional filter by year
        public async Task<IActionResult> Index(int? year)
        {
            var q = _db.DimPeriods.AsNoTracking();
            if (year.HasValue) q = q.Where(p => p.Year == year.Value);

            var data = await q.OrderBy(p => p.Year)
                              .ThenBy(p => p.QuarterNum ?? 0)
                              .ThenBy(p => p.MonthNum ?? 0)
                              .ToListAsync();

            ViewBag.FilterYear = year;
            return View(data);
        }

        // GET: /KpiPeriods/Create
        [HttpGet]
        public IActionResult Create()
        {
            var user = User?.Identity?.Name ?? "system";
            ViewBag.DefaultCreatedBy = user;
            return View();
        }

        // POST: /KpiPeriods/Create
        // Modes:
        //  - "year"    -> create 1 yearly record
        //  - "month"   -> create 1 yearly record (if missing) + 1 monthly record (MonthNum required)
        //  - "quarter" -> create 1 yearly record (if missing) + 1 quarterly record (QuarterNum required)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            int year,
            string periodType,    // "year" | "month" | "quarter"
            int? monthNum,        // 1..12 when periodType == "month"
            int? quarterNum,      // 1..4  when periodType == "quarter"
            string createdBy,
            string lastChangedBy)
        {
            // ---- Validation ----
            if (year < 1900 || year > 3000)
                ModelState.AddModelError(nameof(year), "Year is invalid.");

            if (string.IsNullOrWhiteSpace(periodType) ||
                (periodType != "year" && periodType != "month" && periodType != "quarter"))
            {
                ModelState.AddModelError(nameof(periodType), "Select Year only, or Year + Month, or Year + Quarter.");
            }

            if (string.IsNullOrWhiteSpace(createdBy))
                ModelState.AddModelError(nameof(createdBy), "Created By is required.");

            if (string.IsNullOrWhiteSpace(lastChangedBy))
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");

            if (periodType == "month")
            {
                if (!monthNum.HasValue || monthNum < 1 || monthNum > 12)
                    ModelState.AddModelError(nameof(monthNum), "Select a valid month (1–12).");
            }
            else if (periodType == "quarter")
            {
                if (!quarterNum.HasValue || quarterNum < 1 || quarterNum > 4)
                    ModelState.AddModelError(nameof(quarterNum), "Select a valid quarter (1–4).");
            }

            if (!ModelState.IsValid) return View();

            var now = DateTime.UtcNow;

            // Load existing keys to skip duplicates
            var existing = await _db.DimPeriods
                                    .Where(p => p.Year == year)
                                    .Select(p => new { p.MonthNum, p.QuarterNum })
                                    .ToListAsync();

            bool hasYear = existing.Any(e => !e.MonthNum.HasValue && !e.QuarterNum.HasValue);
            var toAdd = new List<DimPeriod>();

            // Helper: add a year record if needed
            void EnsureYearRecord()
            {
                if (hasYear) return;
                var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1); // last tick of the year
                toAdd.Add(new DimPeriod
                {
                    Year = year,
                    QuarterNum = null,
                    MonthNum = null,
                    StartDate = start,
                    EndDate = end,
                    CreatedBy = createdBy,
                    CreatedDate = now,
                    LastChangedBy = lastChangedBy,
                    IsActive = 1
                });
                hasYear = true;
            }

            if (periodType == "year")
            {
                // Year only
                if (!hasYear)
                {
                    EnsureYearRecord();
                }
            }
            else if (periodType == "month")
            {
                // Year + specific month
                EnsureYearRecord();

                var hasThatMonth = existing.Any(e => e.MonthNum == monthNum && !e.QuarterNum.HasValue);
                if (!hasThatMonth)
                {
                    var start = new DateTime(year, monthNum!.Value, 1, 0, 0, 0, DateTimeKind.Utc);
                    var end = start.AddMonths(1).AddTicks(-1);
                    toAdd.Add(new DimPeriod
                    {
                        Year = year,
                        MonthNum = monthNum,
                        QuarterNum = null,
                        StartDate = start,
                        EndDate = end,
                        CreatedBy = createdBy,
                        CreatedDate = now,
                        LastChangedBy = lastChangedBy,
                        IsActive = 1
                    });
                }
            }
            else if (periodType == "quarter")
            {
                // Year + specific quarter
                EnsureYearRecord();

                var hasThatQuarter = existing.Any(e => e.QuarterNum == quarterNum && !e.MonthNum.HasValue);
                if (!hasThatQuarter)
                {
                    // Quarter month ranges
                    var qMap = new Dictionary<int, (int startMonth, int endMonth)>
                    {
                        {1, (1, 3)}, {2, (4, 6)}, {3, (7, 9)}, {4, (10, 12)}
                    };
                    var (sm, em) = qMap[quarterNum!.Value];

                    var start = new DateTime(year, sm, 1, 0, 0, 0, DateTimeKind.Utc);
                    // end is first day of month after quarter ends, minus 1 tick
                    var end = new DateTime(year, em, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddTicks(-1);

                    toAdd.Add(new DimPeriod
                    {
                        Year = year,
                        QuarterNum = quarterNum,
                        MonthNum = null,
                        StartDate = start,
                        EndDate = end,
                        CreatedBy = createdBy,
                        CreatedDate = now,
                        LastChangedBy = lastChangedBy,
                        IsActive = 1
                    });
                }
            }

            if (toAdd.Count == 0)
            {
                TempData["Msg"] = "Nothing to add (these period(s) already exist).";
                return RedirectToAction(nameof(Index), new { year });
            }

            _db.DimPeriods.AddRange(toAdd);
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Added {toAdd.Count} period record(s) for {year}.";
            return RedirectToAction(nameof(Index), new { year });
        }

        // --- (Optional) Activate / Inactivate endpoints you already had can stay as-is ---
        // GET: /KpiPeriods/Activate/5
        [HttpGet]
        public async Task<IActionResult> Activate(decimal id)
        {
            var item = await _db.DimPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.PeriodId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 1)
            {
                TempData["Msg"] = "Period is already active.";
                return RedirectToAction(nameof(Index), new { year = item.Year });
            }
            return View(item);
        }

        // POST: /KpiPeriods/Activate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Activate(decimal id, string lastChangedBy)
        {
            var item = await _db.DimPeriods.FirstOrDefaultAsync(p => p.PeriodId == id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                return View(item);
            }

            item.IsActive = 1;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Period for {item.Year} {(item.QuarterNum.HasValue ? $"Q{item.QuarterNum}" : item.MonthNum.HasValue ? $"M{item.MonthNum}" : "(Year)")} set to Active.";
            return RedirectToAction(nameof(Index), new { year = item.Year });
        }

        // GET: /KpiPeriods/Inactivate/5
        [HttpGet]
        public async Task<IActionResult> Inactivate(decimal id)
        {
            var item = await _db.DimPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.PeriodId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 0)
            {
                TempData["Msg"] = "Period is already inactive.";
                return RedirectToAction(nameof(Index), new { year = item.Year });
            }
            return View(item);
        }

        // POST: /KpiPeriods/Inactivate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Inactivate(decimal id, string lastChangedBy)
        {
            var item = await _db.DimPeriods.FirstOrDefaultAsync(p => p.PeriodId == id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                return View(item);
            }

            item.IsActive = 0;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Period for {item.Year} {(item.QuarterNum.HasValue ? $"Q{item.QuarterNum}" : item.MonthNum.HasValue ? $"M{item.MonthNum}" : "(Year)")} set to Inactive.";
            return RedirectToAction(nameof(Index), new { year = item.Year });
        }
    }
}