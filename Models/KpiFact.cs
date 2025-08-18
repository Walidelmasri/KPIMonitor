using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("KPIFACTS")]
    public class KpiFact
    {
        [Key]
        [Column("KPIFACTID")]
        public decimal KpiFactId { get; set; }

        [Required]
        [Column("KPIID")]
        public decimal KpiId { get; set; }

        [Required]
        [Column("PERIODID")]
        public decimal PeriodId { get; set; }

        [Required]
        [Column("KPIYEARPLANID")]
        public decimal KpiYearPlanId { get; set; }

        [Column("ACTUALVALUE")]
        public decimal? ActualValue { get; set; }

        [Column("TARGETVALUE")]
        public decimal? TargetValue { get; set; }

        [Column("FORECASTVALUE")]
        public decimal? ForecastValue { get; set; }

        [Column("BUDGET")]
        public decimal? Budget { get; set; }

        [Column("STATUSCODE")]
        [MaxLength(50)]
        public string? StatusCode { get; set; }

        [Required, MaxLength(50)]
        [Column("CREATEDBY")]
        public string CreatedBy { get; set; } = "";

        [Column("CREATEDDATE")]
        public DateTime? CreatedDate { get; set; }

        [Required, MaxLength(50)]
        [Column("LASTCHANGEDBY")]
        public string LastChangedBy { get; set; } = "";

        [Column("ISACTIVE")]
        public int IsActive { get; set; } = 1;

        // Navigation
        public DimKpi? Kpi { get; set; }
        public DimPeriod? Period { get; set; }
        public KpiYearPlan? KpiYearPlan { get; set; }
    }
}