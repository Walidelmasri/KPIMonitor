using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models.ViewModels;
using KPIMonitor.Services.Abstractions;

namespace KPIMonitor.Services
{
    /// <summary>
    /// Strategy Map:
    /// - Columns = Pillars
    /// - Cards   = Objectives
    /// - Card color = worst of latest KPI statuses under the objective (red > orange > blue > green).
    /// - "Latest" = most recent non-empty status per KPI by PeriodId (across all years/plans).
    /// - If an objective has no KPI statuses at all -> green.
    /// </summary>
    public class StrategyMapService : IStrategyMapService
    {
        private readonly AppDbContext _db;
        public StrategyMapService(AppDbContext db) => _db = db;

        public async Task<StrategyMapVm> BuildAsync(int? year = null)
        {
            // 1) Latest NON-EMPTY status per KPI (server-side, simple)
            //    We ignore empty/null status rows and pick the max PeriodId per KPI.
            var nonEmptyFacts =
                from f in _db.KpiFacts.AsNoTracking()
                where f.IsActive == 1 && f.StatusCode != null && f.StatusCode != ""
                select new { f.KpiId, f.PeriodId, f.StatusCode };

            // Optional year filter: applied only if provided (keeps "latest" within that year)
            if (year.HasValue)
            {
                nonEmptyFacts =
                    from f in _db.KpiFacts.AsNoTracking()
                    join per in _db.DimPeriods.AsNoTracking() on f.PeriodId equals per.PeriodId
                    where f.IsActive == 1 && f.StatusCode != null && f.StatusCode != "" && per.Year == year.Value
                    select new { f.KpiId, f.PeriodId, f.StatusCode };
            }

            var lastPeriodPerKpi =
                from f in nonEmptyFacts
                group f by f.KpiId into g
                select new { KpiId = g.Key, LastPeriodId = g.Max(x => x.PeriodId) };

            var latestFacts =
                from l in lastPeriodPerKpi
                join f in nonEmptyFacts
                    on new { l.KpiId, PeriodId = l.LastPeriodId }
                    equals new { f.KpiId, f.PeriodId }
                select new { f.KpiId, f.StatusCode };

            var factList = await latestFacts.ToListAsync();

            // KPI -> canonical status ("red","orange","blue","green")
            var kpiStatus = factList.ToDictionary(
                k => k.KpiId,
                v => StatusPalette.Canonicalize(v.StatusCode));

            // 2) Load pillars, objectives, KPIs (active only)
            var pillars = await _db.DimPillars
                .AsNoTracking()
                .Where(p => p.IsActive == 1)
                .OrderBy(p => p.PillarCode)
                .Select(p => new { p.PillarId, p.PillarCode, p.PillarName })
                .ToListAsync();

            var objectives = await _db.DimObjectives
                .AsNoTracking()
                .Where(o => o.IsActive == 1)
                .OrderBy(o => o.PillarId).ThenBy(o => o.ObjectiveCode)
                .Select(o => new { o.ObjectiveId, o.ObjectiveCode, o.ObjectiveName, o.PillarId })
                .ToListAsync();

            var kpis = await _db.DimKpis
                .AsNoTracking()
                .Where(k => k.IsActive == 1)
                .Select(k => new { k.KpiId, k.ObjectiveId })
                .ToListAsync();

            // 3) Build objective cards
            var objectiveCards = objectives
                .Select(o =>
                {
                    var kpiIds = kpis.Where(k => k.ObjectiveId == o.ObjectiveId).Select(k => k.KpiId);

                    // Only consider KPIs that actually have a latest status
                    var canonicalCodes = kpiIds
                        .Select(id => kpiStatus.TryGetValue(id, out var code) ? code : null)
                        .Where(code => !string.IsNullOrEmpty(code))
                        .ToList();

                    string canonical;
                    if (canonicalCodes.Count == 0)
                    {
                        // No statuses at all for this objective -> green by your rule
                        canonical = "green";
                    }
                    else
                    {
                        var worst = canonicalCodes
                            .Select(code => StatusPalette.Severity(code!))
                            .Max();

                        canonical = worst switch
                        {
                            3 => "red",
                            2 => "orange",
                            1 => "blue",
                            0 => "green",
                            _ => "green"
                        };
                    }

                    var (_, hex) = StatusPalette.Visual(canonical);

                    return new { o.PillarId, Card = new ObjectiveCardVm
                    {
                        ObjectiveId   = o.ObjectiveId,
                        ObjectiveCode = o.ObjectiveCode ?? "",
                        ObjectiveName = o.ObjectiveName ?? "",
                        StatusCode    = canonical,
                        StatusColor   = hex
                    }};
                })
                .ToList();

            // 4) Assemble columns
            var columns = pillars
                .Select(p => new PillarColumnVm
                {
                    PillarId   = p.PillarId,
                    PillarCode = p.PillarCode ?? "",
                    PillarName = p.PillarName ?? "",
                    Objectives = objectiveCards
                        .Where(x => x.PillarId == p.PillarId)
                        .Select(x => x.Card)
                        .ToList()
                })
                .ToList();

            // Year shown in header: if user passed one, show it; else derive latest known year or current year
            var headerYear = year ?? (await _db.DimPeriods.AsNoTracking().MaxAsync(x => (int?)x.Year)) ?? System.DateTime.UtcNow.Year;

            return new StrategyMapVm
            {
                Year    = headerYear,
                Pillars = columns
            };
        }
    }
}
