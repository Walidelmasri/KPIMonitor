using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KPIMonitor.Models
{
    [Table("KPIYEARPLANCOMMENT")]
    public sealed class KpiYearPlanComment
    {
        [Key]
        [Column("KPIYEARPLANID")]
        public decimal KpiYearPlanId { get; set; }

        [Column("COMMENTTEXT")]
        public string? CommentText { get; set; }
    }
}
