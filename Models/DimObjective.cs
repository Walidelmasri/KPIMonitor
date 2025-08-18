using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("DIMOBJECTIVE")]
    public class DimObjective
    {
        [Key]
        [Column("OBJECTIVEID")]
        public decimal ObjectiveId { get; set; }   // Oracle NUMBER identity -> decimal is safest

        [Column("PILLARID")]
        [Required]
        public decimal PillarId { get; set; }      // FK to DIMPILLAR

        [Column("OBJECTIVECODE")]
        [MaxLength(50)]
        public string? ObjectiveCode { get; set; }

        [Column("OBJECTIVENAME")]
        [MaxLength(200)]
        [Required]
        public string ObjectiveName { get; set; }

        [Column("CREATEDBY")]
        [MaxLength(150)]
        [Required]
        public string CreatedBy { get; set; }

        [Column("CREATEDDATE")]
        public DateTime? CreatedDate { get; set; }

        [Column("LASTCHANGEDBY")]
        [MaxLength(150)]
        [Required]
        public string LastChangedBy { get; set; }

        [Column("ISACTIVE")]
        public int IsActive { get; set; } = 1;

        // (optional) nav prop for display
        [ForeignKey(nameof(PillarId))]
        public DimPillar? Pillar { get; set; }
    }
}
