using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("KPIYEARPLAN")]
    public class KpiYearPlan
    {
        [Key]
        [Column("KPIYEARPLANID")]
        public decimal KpiYearPlanId { get; set; }   // Oracle NUMBER identity

        [Required]
        [Column("KPIID")]
        public decimal KpiId { get; set; }

        [Required]
        [Column("PERIODID")]
        public decimal PeriodId { get; set; }        // must be a YEAR period

        [Column("FREQUENCY")]
        [MaxLength(30)]
        public string? Frequency { get; set; }       // string per your rule

        [Column("ANNUALTARGET")]
        public decimal? AnnualTarget { get; set; }   // NUMBER(7,3)

        [Column("ANNUALBUDGET")]
        public decimal? AnnualBudget { get; set; }   // NUMBER(7,3)

        [Column("PRIORITY")]
        public int? Priority { get; set; }           // NUMBER(2)

        [Column("OWNER")]
        [MaxLength(50)]
        public string? Owner { get; set; }

        [Column("EDITOR")]
        [MaxLength(50)]
        public string? Editor { get; set; }

        [Column("UNIT")]
        [MaxLength(10)]
        public string Unit { get; set; } = "";

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
        public string? OwnerEmpId  { get; set; }   // maps to OWNEREMPID (VARCHAR2(5))
        public string? EditorEmpId { get; set; }   // maps to EDITOREMPID (VARCHAR2(5))

    }
}