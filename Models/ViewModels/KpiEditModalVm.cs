// KPIMonitor/Models/KpiEditModalVm.cs
using System.Collections.Generic;

namespace KPIMonitor.ViewModels
{
    public class KpiEditModalVm
    {
        public decimal KpiId { get; set; }
        public int Year { get; set; }
        public bool IsMonthly { get; set; }

        public string KpiName { get; set; } = "";
        public string Unit { get; set; } = "";

        public Dictionary<int, decimal?> Actuals { get; set; } = new();     // key = month (1-12) or quarter (1-4)
        public Dictionary<int, decimal?> Forecasts { get; set; } = new();

        public HashSet<int> EditableActualKeys { get; set; } = new();
        public HashSet<int> EditableForecastKeys { get; set; } = new();
    }
}
