using System;

namespace KPIMonitor.Models
{
    public class AuditLog
    {
        public long AuditId { get; set; }      // identity
        public string TableName { get; set; } = "";
        public string KeyJson { get; set; } = ""; // {"KpiFactId":123}
        public string Action { get; set; } = ""; // Added | Modified | Deleted
        public string ChangedBy { get; set; } = "";
        public DateTime ChangedAtUtc { get; set; }
        public string ColumnChangesJson { get; set; } = "[]"; // [{"Column":"StatusCode","Old":"ecart","New":"conforme"}]
        // public string? CurrentUserName { get; set; }
    }
}