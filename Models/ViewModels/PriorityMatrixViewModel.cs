namespace KPIMonitor.Models.ViewModels
{
    public class PriorityQuadrantVm
    {
        public int Quadrant { get; init; }                 // 1..4
        public string Title { get; init; } = "";           // e.g., "1", "2", ...
        public IReadOnlyList<ObjectiveCardVm> Objectives { get; init; } = new List<ObjectiveCardVm>();
    }

    public class PriorityMatrixVm
    {
        public int? Year { get; init; }                    // optional filter shown in the header if you pass one
        public IReadOnlyList<PriorityQuadrantVm> Quadrants { get; init; } = new List<PriorityQuadrantVm>();
    }
}
