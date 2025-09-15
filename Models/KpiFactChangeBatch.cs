using System;

namespace KPIMonitor.Models
{
    public class KpiFactChangeBatch
    {
        public decimal BatchId { get; set; }

        public decimal KpiId { get; set; }
        public decimal KpiYearPlanId { get; set; }
        public int Year { get; set; }
        public string? Frequency { get; set; } // 'monthly' | 'quarterly'
        public int? PeriodMin { get; set; }
        public int? PeriodMax { get; set; }

        public int RowCount { get; set; }
        public int SkippedCount { get; set; }

        public string SubmittedBy { get; set; } = "";
        public DateTime SubmittedAt { get; set; }

        public string ApprovalStatus { get; set; } = "pending"; // pending/approved/rejected
        public string? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? RejectReason { get; set; }
    }
}
