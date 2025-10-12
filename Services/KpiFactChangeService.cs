using System;
using System.Linq;
using System.Net;
using System.Threading;
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

        // Use HTTP for intranet images (avoid blocked https/self-signed in mail clients)
        private const string InboxUrl = "http://kpimonitor.badea.local/kpimonitor/KpiFactChanges/Inbox";
        private const string LogoUrl  = "http://kpimonitor.badea.local/kpimonitor/images/logo-en.png";

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

        public Task<bool> HasPendingAsync(decimal kpiFactId)
        {
            return _db.KpiFactChanges
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

            var fact = await _db.KpiFacts
                .Include(f => f.Period)
                .Include(f => f.Kpi).ThenInclude(k => k.Objective)
                .Include(f => f.Kpi).ThenInclude(k => k.Pillar)
                .FirstOrDefaultAsync(f => f.KpiFactId == kpiFactId);

            if (fact == null) throw new InvalidOperationException("KPI fact not found.");
            if (fact.IsActive != 1) throw new InvalidOperationException("KPI fact is inactive.");

            var ch = new KpiFactChange
            {
                KpiFactId = kpiFactId,
                ProposedActualValue = actual,
                ProposedTargetValue = target,
                ProposedForecastValue = forecast,
                ProposedStatusCode = string.IsNullOrWhiteSpace(statusCode) ? null : statusCode.Trim(),
                SubmittedBy = NormalizeSam(submittedBy),
                SubmittedAt = DateTime.UtcNow,
                ApprovalStatus = "pending",
                BatchId = batchId
            };

            _db.KpiFactChanges.Add(ch);
            await _db.SaveChangesAsync();

            // ONLY single submit sends the owner email (batch children are suppressed)
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
                            yp.OwnerEmpId,
                            KpiId    = f.KpiId,
                            KpiCode  = k.KpiCode,
                            KpiName  = k.KpiName,
                            Pillar   = k.Pillar != null ? k.Pillar.PillarCode : null,
                            Obj      = k.Objective != null ? k.Objective.ObjectiveCode : null
                        }).FirstOrDefaultAsync();

                    var ownerEmail = await ResolveOwnerEmailAsync(planInfo?.OwnerEmpId, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(ownerEmail))
                    {
                        var kpiText = (planInfo == null)
                            ? $"KPI {fact.KpiId}"
                            : $"{(planInfo.Pillar ?? "")}.{(planInfo.Obj ?? "")} {(planInfo.KpiCode ?? "")} — {(planInfo.KpiName ?? "-")}";

                        var subject = "KPI change pending approval";
                        var bodyHtml = $@"
<p>A KPI change was submitted for <em>{WebUtility.HtmlEncode(kpiText)}</em>.</p>
<p>Submitted by <strong>{WebUtility.HtmlEncode(ch.SubmittedBy)}</strong> at {DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC).</p>";

                        await _email.SendEmailAsync(ownerEmail, subject, HtmlEmail(subject, bodyHtml));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending OWNER pending email (single).");
                }
            }

            return ch;
        }

        public async Task ApproveAsync(decimal changeId, string reviewer, bool suppressEmail = false)
        {
            var ch = await _db.KpiFactChanges.Include(c => c.KpiFact)
                .FirstOrDefaultAsync(c => c.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only pending changes can be approved.");

            var f = ch.KpiFact ?? throw new InvalidOperationException("KPI fact missing.");

            if (ch.ProposedActualValue.HasValue)   f.ActualValue   = ch.ProposedActualValue;
            if (ch.ProposedTargetValue.HasValue)   f.TargetValue   = ch.ProposedTargetValue;
            if (ch.ProposedForecastValue.HasValue) f.ForecastValue = ch.ProposedForecastValue;
            if (!string.IsNullOrWhiteSpace(ch.ProposedStatusCode)) f.StatusCode = ch.ProposedStatusCode;

            ch.ApprovalStatus = "approved";
            ch.ReviewedBy = NormalizeSam(reviewer);
            ch.ReviewedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            if (!suppressEmail)
            {
                try
                {
                    var editorEmail = await ResolveUserEmailFromSamAsync(ch.SubmittedBy);
                    if (!string.IsNullOrWhiteSpace(editorEmail))
                    {
                        var k = await _db.DimKpis.AsNoTracking()
                            .Where(x => x.KpiId == f.KpiId)
                            .Select(x => new
                            {
                                x.KpiCode,
                                x.KpiName,
                                Pillar = x.Pillar != null ? x.Pillar.PillarCode : null,
                                Obj = x.Objective != null ? x.Objective.ObjectiveCode : null
                            }).FirstOrDefaultAsync();

                        var kpiText = (k == null)
                            ? $"KPI {f.KpiId}"
                            : $"{(k.Pillar ?? "")}.{(k.Obj ?? "")} {(k.KpiCode ?? "")} — {(k.KpiName ?? "-")}";

                        var subject = "Your KPI change was approved";
                        var bodyHtml = $@"
<p>Your submitted KPI change for <em>{WebUtility.HtmlEncode(kpiText)}</em> has been <strong>approved</strong>.</p>
<p>Reviewed by <strong>{WebUtility.HtmlEncode(ch.ReviewedBy)}</strong> at {DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC).</p>";

                        await _email.SendEmailAsync(editorEmail, subject, HtmlEmail(subject, bodyHtml));
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

            var ch = await _db.KpiFactChanges.Include(c => c.KpiFact)
                .FirstOrDefaultAsync(c => c.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only pending changes can be rejected.");

            ch.ApprovalStatus = "rejected";
            ch.RejectReason = reason.Trim();
            ch.ReviewedBy = NormalizeSam(reviewer);
            ch.ReviewedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            if (!suppressEmail)
            {
                try
                {
                    var editorEmail = await ResolveUserEmailFromSamAsync(ch.SubmittedBy);
                    if (!string.IsNullOrWhiteSpace(editorEmail))
                    {
                        var f = ch.KpiFact!;
                        var k = await _db.DimKpis.AsNoTracking()
                            .Where(x => x.KpiId == f.KpiId)
                            .Select(x => new
                            {
                                x.KpiCode,
                                x.KpiName,
                                Pillar = x.Pillar != null ? x.Pillar.PillarCode : null,
                                Obj = x.Objective != null ? x.Objective.ObjectiveCode : null
                            }).FirstOrDefaultAsync();

                        var kpiText = (k == null)
                            ? $"KPI {f.KpiId}"
                            : $"{(k.Pillar ?? "")}.{(k.Obj ?? "")} {(k.KpiCode ?? "")} — {(k.KpiName ?? "-")}";

                        var subject = "Your KPI change was rejected";
                        var bodyHtml = $@"
<p>Your submitted KPI change for <em>{WebUtility.HtmlEncode(kpiText)}</em> has been <strong>rejected</strong>.</p>
<p>Reason: {WebUtility.HtmlEncode(reason)}</p>
<p>Reviewed by <strong>{WebUtility.HtmlEncode(ch.ReviewedBy)}</strong> at {DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC).</p>";

                        await _email.SendEmailAsync(editorEmail, subject, HtmlEmail(subject, bodyHtml));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending REJECTED email (single).");
                }
            }
        }

        // ---------- helpers ----------

        private static string NormalizeSam(string? raw)
        {
            var s = raw?.Trim() ?? "";
            if (string.IsNullOrEmpty(s)) return "";
            var bs = s.LastIndexOf('\\');
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');
            if (at > 0) s = s[..at];
            return s.Trim();
        }

        private static string HtmlEmail(string title, string bodyHtml)
        {
            string esc(string v) => WebUtility.HtmlEncode(v);
            return $@"
<!DOCTYPE html>
<html><head><meta charset='UTF-8'/><meta name='viewport' content='width=device-width, initial-scale=1.0'/>
<title>{esc(title)}</title>
<style>
body{{font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;background:#f6f7fb;margin:0;padding:0}}
.container{{max-width:640px;margin:32px auto;background:#fff;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,.08);overflow:hidden}}
.brand{{background:#0d6efd10;padding:16px 24px;display:flex;gap:12px;align-items:center}}
.brand img{{height:36px}}h1{{margin:0;font-size:18px;font-weight:700;color:#0d3757}}
.content{{padding:24px;color:#111;line-height:1.6}}
.btn{{display:inline-block;padding:10px 16px;border-radius:10px;border:1px solid #0d6efd;text-decoration:none}}
.muted{{color:#777;font-size:12px}}
</style></head>
<body>
  <div class='container'>
    <div class='brand'>
      <img src='{LogoUrl}' alt='BADEA Logo'/>
      <h1>BADEA KPI Monitor</h1>
    </div>
    <div class='content'>
      <p style='margin-top:0'><strong>{esc(title)}</strong></p>
      {bodyHtml}
      <p style='margin:18px 0'><a class='btn' href='{InboxUrl}'>Open Approvals</a></p>
      <p class='muted'>This is an automated message.</p>
    </div>
  </div>
</body></html>";
        }

        private async Task<string?> ResolveOwnerEmailAsync(string? ownerEmpId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ownerEmpId)) return null;

            var login = await _dir.TryGetLoginByEmpIdAsync(ownerEmpId, ct);
            if (string.IsNullOrWhiteSpace(login)) return null;

            login = login.Trim().Replace("\r", "").Replace("\n", "");
            // If directory gives email, use it. If SAM, append once.
            if (login.Contains("@") && !login.EndsWith("@"))
                return login;

            // Strip DOMAIN\ if present
            var bs = login.LastIndexOf('\\');
            if (bs >= 0 && bs < login.Length - 1) login = login[(bs + 1)..];

            var at = login.IndexOf('@');
            if (at > 0) login = login[..at];

            return string.IsNullOrWhiteSpace(login) ? null : $"{login}@badea.org";
        }

        private Task<string?> ResolveUserEmailFromSamAsync(string? rawSam)
        {
            var sam = NormalizeSam(rawSam);
            return Task.FromResult(string.IsNullOrWhiteSpace(sam) ? null : $"{sam}@badea.org");
        }
    }
}
