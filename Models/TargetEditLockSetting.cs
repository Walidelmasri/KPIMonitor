namespace KPIMonitor.Models
{
    public class TargetEditLockSetting
    {
        public int Id { get; set; }                 // always 1
        public int IsUnlocked { get; set; }         // 0/1
        public string? ChangedBy { get; set; }
        public DateTime? ChangedAtUtc { get; set; }
    }
}
