using System;

namespace KPIMonitor.Models
{
    public class KpiActionDeadlineHistory
    {
        public decimal Id { get; set; }               
        public decimal ActionId { get; set; }          

        public DateTime? OldDueDate { get; set; }
        public DateTime NewDueDate { get; set; }

        public DateTime ChangedAt { get; set; }
        public string? ChangedBy { get; set; }
        public string? Reason { get; set; }

        // Nav
        public KpiAction? Action { get; set; }
    }
}