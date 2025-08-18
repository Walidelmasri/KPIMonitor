using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("DIMPILLAR")]
    public class DimPillar
    {
        [Key]
        [Column("PILLARID")]
        public decimal PillarId { get; set; } 

        [Column("PILLARCODE")]
        [MaxLength(10)]
        public string PillarCode { get; set; }

        [Column("PILLARNAME")]
        [MaxLength(200)]
        public string PillarName { get; set; }

        [Column("CREATEDBY")]
        [MaxLength(50)]
        public string CreatedBy { get; set; }

        [Column("CREATEDDATE")]
        public DateTime? CreatedDate { get; set; }

        [Column("LASTCHANGEDBY")]
        [MaxLength(50)]
        public string LastChangedBy { get; set; }

        [Column("ISACTIVE")]
        public int IsActive { get; set; } = 1;
    }
}
