using System;

namespace KPIMonitor.Models
{
    public class KpiActionComment
    {
        public decimal KpiActionCommentId { get; set; }
        public decimal ActionId { get; set; }

        public string CommentText { get; set; } = null!;

        public string CreatedByEmpId { get; set; } = null!;
        public string? CreatedByName { get; set; }
        public DateTime CreatedDate { get; set; }
    }
}
