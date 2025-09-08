namespace KPIMonitor.Models.ViewModels
{
    public class ObjectiveCardVm
    {
        public decimal ObjectiveId { get; init; }
        public string ObjectiveCode { get; init; } = "";
        public string ObjectiveName { get; init; } = "";
        public string StatusCode { get; init; } = "";   // canonical: red/orange/blue/green
        public string StatusColor { get; init; } = "";  // hex from StatusPalette
    }

    public class PillarColumnVm
    {
        public decimal PillarId { get; init; }
        public string PillarCode { get; init; } = "";
        public string PillarName { get; init; } = "";
        public IReadOnlyList<ObjectiveCardVm> Objectives { get; init; } = new List<ObjectiveCardVm>();
    }

    public class StrategyMapVm
    {
        public int Year { get; init; }
        public IReadOnlyList<PillarColumnVm> Pillars { get; init; } = new List<PillarColumnVm>();
    }
}
