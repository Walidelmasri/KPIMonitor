using System;
using System.Linq;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KPIMonitor.Services
{
    public class KpiFactChangeService : IKpiFactChangeService
    {
        private readonly AppDbContext _db;
        private readonly IKpiStatusService _status;
        private readonly IEmployeeDirectory _dir;
        private readonly IEmailSender _email;
        private readonly ILogger<KpiFactChangeService> _log;

        // ====================== EMAIL TEMPLATES (single item) ======================
        private static string OwnerPendingSingle_Subject() => "KPI change pending approval";
        private static string OwnerPendingSingle_Body(string kpiCode, string kpiName, string editorSam) =>
            $"A change was submitted for KPI {kpiCode} — {kpiName} by {editorSam}. Please review it in KPI Monitor.";

        private static string EditorApproved_Subject() => "KPI change approved";
        private static string EditorApproved_Body(string kpiCode, string kpiName) =>
            $"Your submitted change for KPI {kpiCode} — {kpiName} has been approved.";

        private static string EditorRejected_Subject() => "KPI change rejected";
        private static string EditorRejected_Body(string kpiCode, string kpiName, string reason) =>
            $"Your submitted change for KPI {kpiCode} — {kpiName} has been rejected. Reason: {reason}";
        // ==========================================================================

        public KpiFactChangeService(
            AppDbContext db,
            IKpiStatusService status,
            IEmployeeDirectory dir,
            IEmailSender email,
            ILogger<KpiFactChangeService> log)
        {
            _db = db;
            _status = status;
            _dir = dir;
            _email = email;
            _log = log;
        }

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
            string submittedBy,
            decimal? batchId = null)
        {
            if (await HasPendingAsync(kpiFactId))
                throw new InvalidOperationException("A change is already pending for this KPI fact.");

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
                ApprovalStatus = "pending",
                BatchId = batchId
            };

            _db.KpiFactChanges.Add(change);
            await _db.SaveChangesAsync();

            // Auto-approve when OwnerEmpId == EditorEmpId
            var who = await (
                from f in _db.KpiFacts
                join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                where f.KpiFactId == kpiFactId
                select new { yp.OwnerEmpId, yp.EditorEmpId, yp.OwnerLogin, yp.KpiYearPlanId, f.PeriodId, f.KpiId }
            ).FirstOrDefaultAsync();

            var ownerEmp = who?.OwnerEmpId?.Trim();
            var editorEmp = who?.EditorEmpId?.Trim();

            if (!string.IsNullOrWhiteSpace(ownerEmp) &&
                !string.IsNullOrWhiteSpace(editorEmp) &&
                string.Equals(ownerEmp, editorEmp, StringComparison.Ordinal))
            {
                var fact = await _db.KpiFacts.FirstAsync(x => x.KpiFactId == kpiFactId);

                if (change.ProposedActualValue.HasValue) fact.ActualValue = change.ProposedActualValue.Value;
                if (change.ProposedTargetValue.HasValue) fact.TargetValue = change.ProposedTargetValue.Value;
                if (change.ProposedForecastValue.HasValue) fact.ForecastValue = change.ProposedForecastValue.Value;
                if (!string.IsNullOrWhiteSpace(change.ProposedStatusCode)) fact.StatusCode = change.ProposedStatusCode;

                change.ApprovalStatus = "approved";
                change.ReviewedAt = DateTime.UtcNow;
                change.ReviewedBy = ownerEmp;

                await _db.SaveChangesAsync();

                await _status.ComputeAndSetAsync(kpiFactId);

                var factPeriodYear = await _db.DimPeriods
                    .Where(p => p.PeriodId == fact.PeriodId)
                    .Select(p => p.Year)
                    .FirstAsync();
                await _status.RecomputePlanYearAsync(fact.KpiYearPlanId, factPeriodYear);

                // Auto-approved: no emails
                return change;
            }

            // Not auto-approved → if this was a "single" submit (no batch), notify owner.
            if (batchId == null)
            {
                try
                {
                    var planInfo = await (
                        from f in _db.KpiFacts
                        join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                        join k in _db.DimKpis on f.KpiId equals k.KpiId
                        where f.KpiFactId == kpiFactId
                        select new
                        {
                            yp.KpiYearPlanId,
                            yp.OwnerLogin,
                            yp.OwnerEmpId,
                            KpiCode = k.KpiCode,
                            KpiName = k.KpiName
                        }).FirstOrDefaultAsync();

                    var ownerMail = await ResolveMailFromOwnerAsync(planInfo?.OwnerLogin, planInfo?.OwnerEmpId);
                    if (!string.IsNullOrWhiteSpace(ownerMail))
                    {
                        var editorUser = NormalizeSam(submittedBy);
                        var subject = OwnerPendingSingle_Subject();
                        var body = OwnerPendingSingle_Body(planInfo?.KpiCode ?? "KPI", planInfo?.KpiName ?? "-", editorUser);
                        await _email.SendEmailAsync(ownerMail!, subject, body);
                    }
                    else
                    {
                        _log.LogWarning("Owner email could not be resolved (single submit).");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending PENDING email to owner for single change.");
                }
            }

            return change;
        }

        public async Task ApproveAsync(decimal changeId, string reviewer, bool suppressEmail = false)
        {
            var ch = await _db.KpiFactChanges
                .FirstOrDefaultAsync(c => c.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only pending changes can be approved.");

            var fact = await _db.KpiFacts.FirstOrDefaultAsync(f => f.KpiFactId == ch.KpiFactId);
            if (fact == null) throw new InvalidOperationException("Target KPI fact not found.");

            if (ch.ProposedActualValue.HasValue) fact.ActualValue = ch.ProposedActualValue.Value;
            if (ch.ProposedTargetValue.HasValue) fact.TargetValue = ch.ProposedTargetValue.Value;
            if (ch.ProposedForecastValue.HasValue) fact.ForecastValue = ch.ProposedForecastValue.Value;
            if (!string.IsNullOrWhiteSpace(ch.ProposedStatusCode)) fact.StatusCode = ch.ProposedStatusCode;

            ch.ApprovalStatus = "approved";
            ch.ReviewedBy = reviewer;
            ch.ReviewedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _status.ComputeAndSetAsync(ch.KpiFactId);

            var approvedFact = await _db.KpiFacts
                .AsNoTracking()
                .Where(f => f.KpiFactId == ch.KpiFactId)
                .Select(f => new { f.KpiYearPlanId, f.PeriodId, f.KpiId })
                .FirstAsync();

            var approvedYear = await _db.DimPeriods
                .Where(p => p.PeriodId == approvedFact.PeriodId)
                .Select(p => p.Year)
                .FirstAsync();

            await _status.RecomputePlanYearAsync(approvedFact.KpiYearPlanId, approvedYear);

            if (!suppressEmail)
            {
                try
                {
                    var plan = await _db.KpiYearPlans.AsNoTracking()
                        .Where(p => p.KpiYearPlanId == approvedFact.KpiYearPlanId)
                        .Select(p => new { p.EditorLogin })
                        .FirstOrDefaultAsync();

                    var editorSam = !string.IsNullOrWhiteSpace(plan?.EditorLogin)
                        ? plan!.EditorLogin
                        : ch.SubmittedBy;

                    var to = BuildMailFromSam(editorSam);
                    if (!string.IsNullOrWhiteSpace(to))
                    {
                        var k = await _db.DimKpis.AsNoTracking()
                            .Where(x => x.KpiId == approvedFact.KpiId)
                            .Select(x => new { x.KpiCode, x.KpiName })
                            .FirstOrDefaultAsync();

                        var subject = EditorApproved_Subject();
                        var body = EditorApproved_Body(k?.KpiCode ?? "KPI", k?.KpiName ?? "-");
                        await _email.SendEmailAsync(to!, subject, body);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending APPROVED email (single).");
                }
            }
        }

        public async Task RejectAsync(decimal changeId, string reviewer, string reason, bool suppressEmail = false)
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

            if (!suppressEmail)
            {
                try
                {
                    var planId = await _db.KpiFacts.AsNoTracking()
                        .Where(f => f.KpiFactId == ch.KpiFactId)
                        .Select(f => f.KpiYearPlanId)
                        .FirstAsync();

                    var plan = await _db.KpiYearPlans.AsNoTracking()
                        .Where(p => p.KpiYearPlanId == planId)
                        .Select(p => new { p.EditorLogin })
                        .FirstOrDefaultAsync();

                    var editorSam = !string.IsNullOrWhiteSpace(plan?.EditorLogin)
                        ? plan!.EditorLogin
                        : ch.SubmittedBy;

                    var to = BuildMailFromSam(editorSam);
                    if (!string.IsNullOrWhiteSpace(to))
                    {
                        var kpi = await (
                            from f in _db.KpiFacts.AsNoTracking()
                            join k in _db.DimKpis.AsNoTracking() on f.KpiId equals k.KpiId
                            where f.KpiFactId == ch.KpiFactId
                            select new { k.KpiCode, k.KpiName }
                        ).FirstOrDefaultAsync();

                        var subject = EditorRejected_Subject();
                        var body = EditorRejected_Body(kpi?.KpiCode ?? "KPI", kpi?.KpiName ?? "-", reason.Trim());
                        await _email.SendEmailAsync(to!, subject, body);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending REJECTED email (single).");
                }
            }
        }

        // ---------------- helpers ----------------

        private static string NormalizeSam(string? raw)
        {
            var s = raw?.Trim() ?? "";
            if (string.IsNullOrEmpty(s)) return "";
            var bs = s.LastIndexOf('\\');
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');
            if (at > 0) s = s[..at];
            return s.Trim().ToLowerInvariant();
        }

        private static string? BuildMailFromSam(string? rawSam)
        {
            var sam = NormalizeSam(rawSam);
            return string.IsNullOrWhiteSpace(sam) ? null : $"{sam}@badea.org";
        }

        private async Task<string?> ResolveMailFromOwnerAsync(string? ownerLogin, string? ownerEmpId)
        {
            if (!string.IsNullOrWhiteSpace(ownerLogin))
                return BuildMailFromSam(ownerLogin);

            if (!string.IsNullOrWhiteSpace(ownerEmpId))
            {
                var sam = await _dir.TryGetLoginByEmpIdAsync(ownerEmpId);
                if (!string.IsNullOrWhiteSpace(sam))
                    return BuildMailFromSam(sam);
            }

            return null;
        }
    }
}
