using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("KPIFIVEYEARTARGET")]
    public class KpiFiveYearTarget
    {
        [Key]
        [Column("KPIFIVEYEARTARGETID")]
        public decimal KpiFiveYearTargetId { get; set; }   // Oracle NUMBER identity -> decimal safe

        [Required]
        [Column("KPIID")]
        public decimal KpiId { get; set; }

        [Required]
        [Column("BASEYEAR")]
        public int BaseYear { get; set; }                  // e.g., 2025 (then Period1=2025 ... Period5=2029)

        // Your SQL uses NUMBER(4). Mapping as int matches integer values.
        [Column("PERIOD1")]
        public int? Period1 { get; set; }

        [Column("PERIOD2")]
        public int? Period2 { get; set; }

        [Column("PERIOD3")]
        public int? Period3 { get; set; }

        [Column("PERIOD4")]
        public int? Period4 { get; set; }

        [Column("PERIOD5")]
        public int? Period5 { get; set; }
        [Column("PERIOD1VALUE")] public decimal? Period1Value { get; set; }
        [Column("PERIOD2VALUE")] public decimal? Period2Value { get; set; }
        [Column("PERIOD3VALUE")] public decimal? Period3Value { get; set; }
        [Column("PERIOD4VALUE")] public decimal? Period4Value { get; set; }
        [Column("PERIOD5VALUE")] public decimal? Period5Value { get; set; }

        [MaxLength(50)]
        [Column("CREATEDBY")]
        public string? CreatedBy { get; set; }

        [Column("CREATEDDATE")]
        public DateTime? CreatedDate { get; set; }

        [MaxLength(50)]
        [Column("LASTCHANGEDBY")]
        public string? LastChangedBy { get; set; }

        [Column("LASTCHANGEDDATE")]
        public DateTime? LastChangedDate { get; set; }

        [Column("ISACTIVE")]
        public int IsActive { get; set; } = 1;

        // Navigation
        public DimKpi? Kpi { get; set; }
    }
}



