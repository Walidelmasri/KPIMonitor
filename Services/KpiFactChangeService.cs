using System;
using System.Linq;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Services
{
    public class KpiFactChangeService : IKpiFactChangeService
    {
        private readonly AppDbContext _db;
        public KpiFactChangeService(AppDbContext db) { _db = db; }

        public async Task<bool> HasPendingAsync(decimal kpiFactId)
        {
            return await _db.KpiFactChanges
                .AsNoTracking()
                .AnyAsync(x => x.KpiFactId == kpiFactId && x.ApprovalStatus == "pending");
        }

        public async Task<KpiFactChange> SubmitAsync(
            decimal kpiFactId,
            decimal? actual, decimal? target, decimal? forecast,
            string? statusCode,
            string submittedBy)
        {
            // guard: existing pending
            if (await HasPendingAsync(kpiFactId))
                throw new InvalidOperationException("A change is already pending for this KPI fact.");

            // (optional) guard: ensure fact exists
            var factExists = await _db.KpiFacts
                .AsNoTracking()
                .AnyAsync(f => f.KpiFactId == kpiFactId && f.IsActive == 1);
            if (!factExists)
                throw new InvalidOperationException("KPI fact not found or inactive.");

            var change = new KpiFactChange
            {
                KpiFactId = kpiFactId,
                ProposedActualValue = actual,
                ProposedTargetValue = target,
                ProposedForecastValue = forecast,
                ProposedStatusCode = string.IsNullOrWhiteSpace(statusCode) ? null : statusCode.Trim().ToLowerInvariant(),
                SubmittedBy = submittedBy,
                SubmittedAt = DateTime.UtcNow,
                ApprovalStatus = "pending"
            };

            _db.KpiFactChanges.Add(change);
            await _db.SaveChangesAsync();
            return change;
        }

        public async Task ApproveAsync(decimal changeId, string reviewer)
        {
            // load change + fact
            var ch = await _db.KpiFactChanges
                .FirstOrDefaultAsync(c => c.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only pending changes can be approved.");

            var fact = await _db.KpiFacts.FirstOrDefaultAsync(f => f.KpiFactId == ch.KpiFactId);
            if (fact == null) throw new InvalidOperationException("Target KPI fact not found.");

            // apply only the provided values
            if (ch.ProposedActualValue.HasValue)   fact.ActualValue   = ch.ProposedActualValue.Value;
            if (ch.ProposedTargetValue.HasValue)   fact.TargetValue   = ch.ProposedTargetValue.Value;
            if (ch.ProposedForecastValue.HasValue) fact.ForecastValue = ch.ProposedForecastValue.Value;
            if (!string.IsNullOrWhiteSpace(ch.ProposedStatusCode)) fact.StatusCode = ch.ProposedStatusCode;

            ch.ApprovalStatus = "approved";
            ch.ReviewedBy = reviewer;
            ch.ReviewedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }

        public async Task RejectAsync(decimal changeId, string reviewer, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Reject reason is required.");

            var ch = await _db.KpiFactChanges
                .FirstOrDefaultAsync(c => c.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only pending changes can be rejected.");

            ch.ApprovalStatus = "rejected";
            ch.RejectReason = reason.Trim();
            ch.ReviewedBy = reviewer;
            ch.ReviewedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
        }
    }
}