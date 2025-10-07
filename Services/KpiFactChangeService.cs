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

        // ADDED: email + logger
        private readonly IEmailSender _email;
        private readonly ILogger<KpiFactChangeService> _log;

        // UPDATED: constructor injects email + logger (no signature changes to the interface)
        public KpiFactChangeService(AppDbContext db, IKpiStatusService status,
                                    IEmailSender email, ILogger<KpiFactChangeService> log)
        {
            _db = db;
            _status = status;
            _email = email;
            _log = log;
        }

        // ---------- existing public API (unchanged signatures) ----------

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
            // guard: existing pending
            if (await HasPendingAsync(kpiFactId))
                throw new InvalidOperationException("A change is already pending for this KPI fact.");

            // guard: ensure fact exists (and is active)
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

            // --- AUTO-APPROVE when OwnerEmpId == EditorEmpId for this KPI's plan (existing behavior) ---
            var who = await (
                from f in _db.KpiFacts
                join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                where f.KpiFactId == kpiFactId
                select new { yp.OwnerEmpId, yp.EditorEmpId }
            ).FirstOrDefaultAsync();

            var owner = who?.OwnerEmpId?.Trim();
            var editor = who?.EditorEmpId?.Trim();

            if (!string.IsNullOrWhiteSpace(owner) &&
                !string.IsNullOrWhiteSpace(editor) &&
                string.Equals(owner, editor, StringComparison.Ordinal))
            {
                // Apply proposed values immediately (same as manual approval)
                var fact = await _db.KpiFacts.FirstAsync(x => x.KpiFactId == kpiFactId);

                if (change.ProposedActualValue.HasValue) fact.ActualValue = change.ProposedActualValue.Value;
                if (change.ProposedTargetValue.HasValue) fact.TargetValue = change.ProposedTargetValue.Value;
                if (change.ProposedForecastValue.HasValue) fact.ForecastValue = change.ProposedForecastValue.Value;
                if (!string.IsNullOrWhiteSpace(change.ProposedStatusCode)) fact.StatusCode = change.ProposedStatusCode;

                change.ApprovalStatus = "approved";
                change.ReviewedAt = DateTime.UtcNow;
                change.ReviewedBy = owner;    // or "auto"

                // Save the applied values and approval flags first
                await _db.SaveChangesAsync();

                // Then recompute & persist the status (uses the just-saved values)
                await _status.ComputeAndSetAsync(kpiFactId);
                // Also recompute the whole plan-year so k+1 logic sees the latest values
                var factPeriodYear = await _db.DimPeriods
                    .Where(p => p.PeriodId == fact.PeriodId)
                    .Select(p => p.Year)
                    .FirstAsync();
                await _status.RecomputePlanYearAsync(fact.KpiYearPlanId, factPeriodYear);

                // NOTE: No emails for auto-approved (as requested)
            }

            // If still PENDING (not auto-approved), notify the Owner once per submission (single change)
            if (string.Equals(change.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                var submittedBySam = NormalizeSam(submittedBy);
                await NotifyOwner_RequestAsync(kpiFactId, submittedBySam);
            }

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
            if (ch.ProposedActualValue.HasValue) fact.ActualValue = ch.ProposedActualValue.Value;
            if (ch.ProposedTargetValue.HasValue) fact.TargetValue = ch.ProposedTargetValue.Value;
            if (ch.ProposedForecastValue.HasValue) fact.ForecastValue = ch.ProposedForecastValue.Value;
            if (!string.IsNullOrWhiteSpace(ch.ProposedStatusCode)) fact.StatusCode = ch.ProposedStatusCode;

            ch.ApprovalStatus = "approved";
            ch.ReviewedBy = reviewer;
            ch.ReviewedAt = DateTime.UtcNow;

            // Save applied values + approval flags first
            await _db.SaveChangesAsync();

            // Then recompute & persist the status (uses the just-saved values)
            await _status.ComputeAndSetAsync(ch.KpiFactId);
            // And recompute the entire plan-year after the approval
            var approvedFact = await _db.KpiFacts
                .AsNoTracking()
                .Where(f => f.KpiFactId == ch.KpiFactId)
                .Select(f => new { f.KpiYearPlanId, f.PeriodId })
                .FirstAsync();

            var approvedYear = await _db.DimPeriods
                .Where(p => p.PeriodId == approvedFact.PeriodId)
                .Select(p => p.Year)
                .FirstAsync();

            await _status.RecomputePlanYearAsync(approvedFact.KpiYearPlanId, approvedYear);

            // Notify the editor (single change approved)
            try
            {
                var (ownerSam, editorSamFromPlan) = await GetOwnerEditorSamsAsync(ch.KpiFactId);
                var editorSam = !string.IsNullOrWhiteSpace(editorSamFromPlan)
                    ? editorSamFromPlan
                    : NormalizeSam(ch.SubmittedBy);

                await NotifyEditor_DecisionAsync(ch.KpiFactId, editorSam, approved: true, reason: null, reviewer);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Post-approve notify failed for change {ChangeId}", changeId);
            }
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

            // Notify the editor (single change rejected)
            try
            {
                var (ownerSam, editorSamFromPlan) = await GetOwnerEditorSamsAsync(ch.KpiFactId);
                var editorSam = !string.IsNullOrWhiteSpace(editorSamFromPlan)
                    ? editorSamFromPlan
                    : NormalizeSam(ch.SubmittedBy);

                await NotifyEditor_DecisionAsync(ch.KpiFactId, editorSam, approved: false, reason, reviewer);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Post-reject notify failed for change {ChangeId}", changeId);
            }
        }

        // ---------- private helpers (added) ----------

        private static string NormalizeSam(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\'); if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@'); if (at > 0) s = s[..at];
            return s.Trim();
        }

        private static string EmailFromSam(string sam) => $"{sam}@badea.org";

        private async Task<(string KpiLabel, int Year)> GetLabelAsync(decimal kpiFactId)
        {
            var rec = await (
                from f in _db.KpiFacts
                join k in _db.DimKpis on f.KpiId equals k.KpiId
                join p in _db.DimPeriods on f.PeriodId equals p.PeriodId
                where f.KpiFactId == kpiFactId
                select new
                {
                    p.Year,
                    Label = (k.Pillar != null ? k.Pillar.PillarCode : "") + "." +
                            (k.Objective != null ? k.Objective.ObjectiveCode : "") + " " +
                            (k.KpiCode ?? "") + " — " + (k.KpiName ?? "-")
                }
            ).FirstAsync();

            return (rec.Label, rec.Year);
        }

        private async Task<(string? OwnerSam, string? EditorSam)> GetOwnerEditorSamsAsync(decimal kpiFactId)
        {
            var rec = await (
                from f in _db.KpiFacts
                join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                where f.KpiFactId == kpiFactId
                select new { yp.OwnerLogin, yp.EditorLogin }
            ).FirstAsync();

            var ownerSam = string.IsNullOrWhiteSpace(rec.OwnerLogin) ? null : NormalizeSam(rec.OwnerLogin);
            var editorSam = string.IsNullOrWhiteSpace(rec.EditorLogin) ? null : NormalizeSam(rec.EditorLogin);
            return (ownerSam, editorSam);
        }

        private const string AppRootUrl = "http://kpimonitor.badea.local/kpimonitor";

        // Owner notification: a pending single change arrived
        private async Task NotifyOwner_RequestAsync(decimal kpiFactId, string submittedBySam)
        {
            try
            {
                var (kpiLabel, year) = await GetLabelAsync(kpiFactId);
                var (ownerSam, _) = await GetOwnerEditorSamsAsync(kpiFactId);
                if (string.IsNullOrWhiteSpace(ownerSam))
                {
                    _log.LogWarning("NotifyOwner skipped: no OwnerLogin set for fact {FactId}.", kpiFactId);
                    return;
                }

                var to = EmailFromSam(ownerSam);
                var subj = $"KPI change request – {kpiLabel} (Year {year})";
                var body = $@"
<p><strong>{NormalizeSam(submittedBySam)}</strong> submitted changes for <strong>{kpiLabel}</strong> (Year {year}).</p>
<p>Please review in KPI Monitor.</p>
<p><a href=""{AppRootUrl}"">{AppRootUrl}</a></p>";

                await _email.SendEmailAsync(to, subj, body);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "NotifyOwner_Request failed for fact {FactId}", kpiFactId);
            }
        }

        // Editor notification: their single change was approved/rejected
        private async Task NotifyEditor_DecisionAsync(decimal kpiFactId, string editorSam, bool approved, string? reason, string reviewerSam)
        {
            try
            {
                var (kpiLabel, year) = await GetLabelAsync(kpiFactId);
                var to = EmailFromSam(editorSam);
                var statusText = approved ? "approved" : "rejected";
                var subj = $"Your KPI changes were {statusText} – {kpiLabel} (Year {year})";
                var body = approved
                    ? $@"
<p>Your submission for <strong>{kpiLabel}</strong> (Year {year}) was approved by <strong>{NormalizeSam(reviewerSam)}</strong>.</p>
<p><a href=""{AppRootUrl}"">{AppRootUrl}</a></p>"
                    : $@"
<p>Your submission for <strong>{kpiLabel}</strong> (Year {year}) was rejected by <strong>{NormalizeSam(reviewerSam)}</strong>.</p>
<p><strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason ?? "-")}</p>
<p><a href=""{AppRootUrl}"">{AppRootUrl}</a></p>";

                await _email.SendEmailAsync(to, subj, body);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "NotifyEditor_Decision failed for fact {FactId}", kpiFactId);
            }
        }
    }
}
