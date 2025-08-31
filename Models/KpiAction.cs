using System;

namespace KPIMonitor.Models
{
    public class KpiAction
    {
        public decimal ActionId { get; set; }          
        public decimal? KpiId { get; set; }             
        public bool IsGeneral { get; set; }
        public string Owner { get; set; } = null!;
        public DateTime? AssignedAt { get; set; }

        public string Description { get; set; } = null!;
        public DateTime? DueDate { get; set; }

        public string StatusCode { get; set; } = null!;

        public short ExtensionCount { get; set; }     

        public string? CreatedBy { get; set; }
        public DateTime? CreatedDate { get; set; }
        public string? LastChangedBy { get; set; }
        public DateTime? LastChangedDate { get; set; }

        // Nav
        public DimKpi? Kpi { get; set; }
    }
}