using System;

namespace KPIMonitor.Models
{
    public class KpiFactChange
    {
        public decimal KpiFactChangeId { get; set; }
        public decimal KpiFactId { get; set; }

        public decimal? ProposedActualValue { get; set; }
        public decimal? ProposedTargetValue { get; set; }
        public decimal? ProposedForecastValue { get; set; }
        public string?  ProposedStatusCode { get; set; }

        public string   SubmittedBy { get; set; } = null!;
        public DateTime SubmittedAt { get; set; }      // TIMESTAMP(0) DEFAULT SYSTIMESTAMP

        // NULL | 'pending' | 'approved' | 'rejected'
        public string?  ApprovalStatus { get; set; }

        public string?  ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string?  RejectReason { get; set; }

        // nav
        public virtual KpiFact KpiFact { get; set; } = null!;
    }
}