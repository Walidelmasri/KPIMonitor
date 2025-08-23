using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace KPIMonitor.Models.ViewModels
{
    public class GenerateFactsVm
    {
        // Input / context
        public decimal? KpiId { get; set; }
        public decimal? PlanId { get; set; }

        // When plan frequency is unknown/empty, user can choose here
        public string? FrequencyChoice { get; set; } // "Monthly" | "Quarterly" | null

        public bool CreateMissingOnly { get; set; } = true;
        public bool OverwriteExisting { get; set; } = false;

        // Audit
        public string? CreatedBy { get; set; }
        public string? LastChangedBy { get; set; }

        // UI data
        public SelectList? Kpis { get; set; }
        public SelectList? Plans { get; set; }

        // Preview rows
        public List<PeriodPreview> Preview { get; set; } = new();

        public class PeriodPreview
        {
            public decimal PeriodId { get; set; }
            public string Label { get; set; } = "";
            public bool Exists { get; set; }
        }

        // Resolved info for the page
        public int? PlanYear { get; set; }
        public string? PlanFrequency { get; set; } // as stored in DB (raw)
    }
}