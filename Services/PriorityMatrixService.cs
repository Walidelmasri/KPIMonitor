using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models.ViewModels;
using KPIMonitor.Services.Abstractions;

namespace KPIMonitor.Services
{
    public class PriorityMatrixService : IPriorityMatrixService
    {
        private readonly AppDbContext _db;
        public PriorityMatrixService(AppDbContext db) => _db = db;

        public async Task<PriorityMatrixVm> BuildAsync(int? year = null)
        {
            // ----- Latest NON-EMPTY status per KPI (optionally restricted to a year) -----
            var factsQuery =
                from f in _db.KpiFacts.AsNoTracking()
                join per in _db.DimPeriods.AsNoTracking() on f.PeriodId equals per.PeriodId
                where f.IsActive == 1 && f.StatusCode != null && f.StatusCode != ""
                select new { f.KpiId, f.PeriodId, f.StatusCode, per.Year };

            if (year.HasValue)
                factsQuery = factsQuery.Where(x => x.Year == year.Value);

            var lastPeriodPerKpi =
                from f in factsQuery
                group f by f.KpiId into g
                select new { KpiId = g.Key, LastPeriodId = g.Max(x => x.PeriodId) };

            var latestFacts =
                from l in lastPeriodPerKpi
                join f in factsQuery
                    on new { KpiId = l.KpiId, PeriodId = l.LastPeriodId }
                    equals new { KpiId = f.KpiId, PeriodId = f.PeriodId } // <-- fixed: no 'l' on RHS
                select new { f.KpiId, f.StatusCode };

            var factList = await latestFacts.ToListAsync();
            var kpiStatus = factList.ToDictionary(
                k => k.KpiId,
                v => StatusPalette.Canonicalize(v.StatusCode));

            // ----- Latest ACTIVE plan priority per KPI (1..4). If none, we'll default later to 4. -----
            var latestPlanIds =
                from p in _db.KpiYearPlans.AsNoTracking()
                where p.IsActive == 1
                group p by p.KpiId into g
                select new { KpiId = g.Key, MaxPlanId = g.Max(x => x.KpiYearPlanId) };

            var kpiPrioritiesQuery =
                from lid in latestPlanIds
                join p in _db.KpiYearPlans.AsNoTracking()
                    on new { KpiId = lid.KpiId, PlanId = lid.MaxPlanId }
                    equals new { KpiId = p.KpiId, PlanId = p.KpiYearPlanId }
                select new { p.KpiId, Priority = (int?)p.Priority }; // priority comes from KpiYearPlan

            var kpiPriorities = await kpiPrioritiesQuery.ToListAsync();
            var kpiPriorityMap = kpiPriorities.ToDictionary(x => x.KpiId, x => x.Priority ?? 4);

            // ----- Objectives + Kpis (for Objective mapping) -----
            var objectives = await _db.DimObjectives
                .AsNoTracking()
                .Where(o => o.IsActive == 1)
                .OrderBy(o => o.PillarId).ThenBy(o => o.ObjectiveCode)
                .Select(o => new { o.ObjectiveId, o.ObjectiveCode, o.ObjectiveName })
                .ToListAsync();

            var kpis = await _db.DimKpis
                .AsNoTracking()
                .Where(k => k.IsActive == 1)
                .Select(k => new { k.KpiId, k.ObjectiveId })
                .ToListAsync();

            // ----- Build objective cards -----
            var cards = objectives.Select(o =>
            {
                var kpiIds = kpis.Where(k => k.ObjectiveId == o.ObjectiveId).Select(k => k.KpiId).ToList();

                // Latest canonical status per KPI -> worst wins
                var codes = kpiIds
                    .Select(id => kpiStatus.TryGetValue(id, out var c) ? c : null)
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();

                var worst = codes.Count == 0
                    ? 0   // no statuses -> green
                    : codes.Select(c => StatusPalette.Severity(c!)).Max();

                var canonical = worst switch
                {
                    3 => "red",
                    2 => "orange",
                    1 => "blue",
                    0 => "green",
                    _ => "green"
                };
                var (_, hex) = StatusPalette.Visual(canonical);

                // Objective priority = min KPI priority from latest active plan; default 4 if none
                var objPriority = kpiIds
                    .Select(id => kpiPriorityMap.TryGetValue(id, out var p) ? p : 4)
                    .DefaultIfEmpty(4)
                    .Min();

                return new
                {
                    Quadrant = objPriority,
                    Card = new ObjectiveCardVm
                    {
                        ObjectiveId   = o.ObjectiveId,
                        ObjectiveCode = o.ObjectiveCode ?? "",
                        ObjectiveName = o.ObjectiveName ?? "",
                        StatusCode    = canonical,
                        StatusColor   = hex
                    }
                };
            }).ToList();

            // ----- Assemble quadrants 1..4 -----
            var quadrants = Enumerable.Range(1, 4).Select(q => new PriorityQuadrantVm
            {
                Quadrant = q,
                Title = q.ToString(),
                Objectives = cards.Where(c => c.Quadrant == q).Select(c => c.Card).ToList()
            }).ToList();

            return new PriorityMatrixVm
            {
                Year = year,
                Quadrants = quadrants
            };
        }
    }
}
