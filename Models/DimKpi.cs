using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("DIMKPI")]
    public class DimKpi
    {
        [Key]
        [Column("KPIID")]
        public decimal KpiId { get; set; }

        [Required]
        [Column("OBJECTIVEID")]
        public decimal ObjectiveId { get; set; }

        [Required]
        [Column("PILLARID")]
        public decimal PillarId { get; set; }

        [Column("KPICODE")]
        [MaxLength(50)]
        public string? KpiCode { get; set; }

        [Required]
        [Column("KPINAME")]
        [MaxLength(300)]
        public string KpiName { get; set; }

        [Required]
        [Column("CREATEDBY")]
        [MaxLength(150)]
        public string CreatedBy { get; set; }

        [Column("CREATEDDATE")]
        public DateTime? CreatedDate { get; set; }

        [Required]
        [Column("LASTCHANGEDBY")]
        [MaxLength(150)]
        public string LastChangedBy { get; set; }

        [Column("ISACTIVE")]
        public int IsActive { get; set; } = 1;

        [ForeignKey(nameof(PillarId))]
        public DimPillar? Pillar { get; set; }

        [ForeignKey(nameof(ObjectiveId))]
        public DimObjective? Objective { get; set; }
    }
}
