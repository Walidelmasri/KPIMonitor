using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("DIMPERIOD")]
    public class DimPeriod
    {
        [Key]
        [Column("PERIODID")]
        public decimal PeriodId { get; set; }     // Oracle NUMBER identity -> decimal is safe

        [Required]
        [Column("YEAR")]
        public int Year { get; set; }             // NUMBER(4)

        [Column("QUARTERNUM")]
        public int? QuarterNum { get; set; }      // NUMBER(1), null for monthly rows

        [Column("MONTHNUM")]
        public int? MonthNum { get; set; }        // NUMBER(2), null for quarterly rows

        [Column("STARTDATE")]
        public DateTime? StartDate { get; set; }

        [Column("ENDDATE")]
        public DateTime? EndDate { get; set; }

        [Required]
        [MaxLength(150)]
        [Column("CREATEDBY")]
        public string CreatedBy { get; set; }

        [Column("CREATEDDATE")]
        public DateTime? CreatedDate { get; set; }

        [Required]
        [MaxLength(150)]
        [Column("LASTCHANGEDBY")]
        public string LastChangedBy { get; set; }

        [Column("ISACTIVE")]
        public int IsActive { get; set; } = 1;
    }
}
