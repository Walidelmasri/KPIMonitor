using System;

namespace KPIMonitor.Models
{
    public class KpiActionOwner
    {
        public decimal KpiActionOwnerId { get; set; }
        public decimal ActionId { get; set; }

        public string OwnerEmpId { get; set; } = null!;
        public string? OwnerName { get; set; }

        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
